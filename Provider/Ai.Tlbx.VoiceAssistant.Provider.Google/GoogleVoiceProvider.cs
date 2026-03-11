using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Reflection;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Models;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Protocol;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Translation;

namespace Ai.Tlbx.VoiceAssistant.Provider.Google
{
    /// <summary>
    /// Google Gemini Live API provider implementation for real-time conversation via WebSocket.
    /// </summary>
    public sealed class GoogleVoiceProvider : IVoiceProvider
    {
        private const string LIVE_API_ENDPOINT = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
        private const int CONNECTION_TIMEOUT_MS = 10000;
        private const int SETUP_TIMEOUT_MS = 10000;
        private const int AUDIO_BUFFER_SIZE = 32384;
        private const string AUDIO_MIME_TYPE = "audio/pcm;rate=16000";
        private const int AUDIO_TX_LOG_INTERVAL = 100;
        private const int AUDIO_RX_LOG_INTERVAL = 50;
        private const int MESSAGE_LOG_TRUNCATE_LENGTH = 300;

        private readonly string _apiKey;
        private readonly Action<LogLevel, string> _logAction;
        private readonly GoogleToolTranslator _toolTranslator = new();
        private ClientWebSocket? _webSocket;
        private Task? _receiveTask;
        private CancellationTokenSource? _cts;
        private bool _isDisposed = false;
        private GoogleVoiceSettings? _settings;
        private TaskCompletionSource<bool>? _setupCompletionSource;

        private bool _setupComplete = false;
        private bool _responseInterrupted = false;
        private readonly StringBuilder _currentTranscript = new();
        private readonly StringBuilder _currentModelText = new();
        private readonly StringBuilder _currentUserTranscript = new();
        private readonly Dictionary<string, string> _pendingToolCalls = new();
        private int _audioTxCount = 0;
        private int _audioRxCount = 0;

        /// <summary>
        /// Gets a value indicating whether the provider is connected and ready.
        /// </summary>
        public bool IsConnected => _webSocket?.State == WebSocketState.Open && _setupComplete;

        /// <summary>
        /// Gets the required input audio sample rate for Google (16kHz).
        /// </summary>
        public AudioSampleRate RequiredInputSampleRate => AudioSampleRate.Rate16000;

        /// <summary>
        /// Callback invoked when a message is received from the AI provider.
        /// </summary>
        public Action<ChatMessage>? OnMessageReceived { get; set; }

        /// <summary>
        /// Callback invoked when audio data is received from the AI provider for playback.
        /// </summary>
        public Action<string>? OnAudioReceived { get; set; }

        /// <summary>
        /// Callback invoked when the provider status changes.
        /// </summary>
        public Action<string>? OnStatusChanged { get; set; }

        /// <summary>
        /// Callback invoked when an error occurs in the provider.
        /// </summary>
        public Action<string>? OnError { get; set; }

        /// <summary>
        /// Callback invoked when interruption is detected and audio needs to be cleared.
        /// </summary>
        public Action? OnInterruptDetected { get; set; }

        /// <summary>
        /// Callback invoked when usage data is received from the AI provider.
        /// </summary>
        public Action<UsageReport>? OnUsageReceived { get; set; }
        public Action<string>? OnTranscriptionDelta { get; set; }
        public Action<string>? OnTranscriptionCompleted { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="GoogleVoiceProvider"/> class.
        /// </summary>
        /// <param name="apiKey">The Google API key. If null, will try to get from environment variable GOOGLE_API_KEY.</param>
        /// <param name="logAction">Optional logging action.</param>
        public GoogleVoiceProvider(string? apiKey = null, Action<LogLevel, string>? logAction = null)
        {
            _apiKey = apiKey ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
                ?? throw new InvalidOperationException("Google API key must be provided or set in GOOGLE_API_KEY environment variable");
            _logAction = logAction ?? ((level, message) => { /* no-op */ });
        }

        /// <summary>
        /// Connects to the Google Gemini Live API using the specified settings.
        /// </summary>
        /// <param name="settings">Google-specific voice settings.</param>
        /// <returns>A task representing the connection operation.</returns>
        public async Task ConnectAsync(IVoiceSettings settings)
        {
            if (settings is not GoogleVoiceSettings googleSettings)
            {
                throw new ArgumentException("Settings must be of type GoogleVoiceSettings for Google provider", nameof(settings));
            }

            ValidateSettings(googleSettings);
            ResetConversationState();
            _settings = googleSettings;
            _logAction(LogLevel.Info, $"Settings configured - Voice: {_settings.Voice}, Model: {_settings.Model}");

            try
            {
                if (string.IsNullOrEmpty(_apiKey))
                {
                    throw new InvalidOperationException("Google API key is not set. Please set the GOOGLE_API_KEY environment variable.");
                }

                OnStatusChanged?.Invoke("Connecting to Google Gemini...");

                _webSocket = new ClientWebSocket();
                _setupCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                var uri = new Uri($"{LIVE_API_ENDPOINT}?key={_apiKey}");
                _logAction(LogLevel.Info, $"Connecting to: {LIVE_API_ENDPOINT}");
                _logAction(LogLevel.Info, $"API Key present: {!string.IsNullOrEmpty(_apiKey)}, Length: {_apiKey?.Length ?? 0}");

                using (var connectionCts = new CancellationTokenSource(CONNECTION_TIMEOUT_MS))
                {
                    await _webSocket.ConnectAsync(uri, connectionCts.Token);
                }

                OnStatusChanged?.Invoke("Connected to Google Gemini");
                _logAction(LogLevel.Info, $"WebSocket connected, State: {_webSocket.State}");

                _cts = new CancellationTokenSource();
                _logAction(LogLevel.Info, "Starting message receive loop");
                _receiveTask = ReceiveMessagesAsync(_cts.Token);

                await SendSetupMessageAsync();
                await WaitForSetupCompletionAsync();
            }
            catch (Exception ex)
            {
                _setupCompletionSource?.TrySetException(ex);
                _logAction(LogLevel.Error, $"Failed to connect to Google Gemini: {ex.Message}");
                OnError?.Invoke($"Connection failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Disconnects from the Google Gemini Live API.
        /// </summary>
        /// <returns>A task representing the disconnection operation.</returns>
        public async Task DisconnectAsync()
        {
            try
            {
                OnStatusChanged?.Invoke("Disconnecting...");

                _cts?.Cancel();

                if (_webSocket?.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None);
                }

                if (_receiveTask != null)
                {
                    await _receiveTask;
                }

                _setupComplete = false;
                _setupCompletionSource?.TrySetCanceled();
                _setupCompletionSource = null;
                OnStatusChanged?.Invoke("Disconnected");
                _logAction(LogLevel.Info, "Disconnected from Google Gemini");
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error during disconnection: {ex.Message}");
                OnError?.Invoke($"Disconnection error: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the settings for an existing connection and sends the configuration to Google.
        /// </summary>
        /// <param name="settings">The new settings to apply.</param>
        /// <returns>A task representing the update operation.</returns>
        public async Task UpdateSettingsAsync(IVoiceSettings settings)
        {
            if (settings is not GoogleVoiceSettings googleSettings)
            {
                throw new ArgumentException("Settings must be of type GoogleVoiceSettings for Google provider", nameof(settings));
            }

            _settings = googleSettings;
            _logAction(LogLevel.Info, $"Settings updated - Voice: {_settings.Voice}, Model: {_settings.Model}");

            if (IsConnected)
            {
                _logAction(LogLevel.Warn, "Updating settings requires reconnection for Google provider");
                await DisconnectAsync();
                await ConnectAsync(settings);
            }
        }

        /// <summary>
        /// Processes audio data received from the microphone and sends it to Google.
        /// </summary>
        /// <param name="base64Audio">Base64-encoded PCM 16-bit audio data.</param>
        /// <returns>A task representing the audio processing operation.</returns>
        public async Task ProcessAudioAsync(string base64Audio)
        {
            if (!IsConnected)
            {
                return;
            }

            try
            {
                if (string.IsNullOrEmpty(base64Audio))
                {
                    return;
                }

                var message = new RealtimeInputMessage
                {
                    RealtimeInput = new RealtimeInput
                    {
                        Audio = new Blob
                        {
                            MimeType = AUDIO_MIME_TYPE,
                            Data = base64Audio
                        }
                    }
                };

                await SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error processing audio: {ex.Message}");
                OnError?.Invoke($"Audio processing error: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends an interrupt signal to Google to stop current response generation.
        /// </summary>
        /// <returns>A task representing the interrupt operation.</returns>
        public async Task SendInterruptAsync()
        {
            if (!IsConnected)
            {
                return;
            }

            try
            {
                _logAction(LogLevel.Info, "Interrupt handled automatically by Google VAD");
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error sending interrupt: {ex.Message}");
                OnError?.Invoke($"Interrupt error: {ex.Message}");
            }
        }

        /// <summary>
        /// Injects conversation history into the current session.
        /// </summary>
        /// <param name="messages">The conversation history to inject.</param>
        /// <returns>A task representing the injection operation.</returns>
        public async Task InjectConversationHistoryAsync(IEnumerable<ChatMessage> messages)
        {
            if (!IsConnected)
            {
                _logAction(LogLevel.Warn, "Cannot inject conversation history: not connected");
                return;
            }

            try
            {
                var turns = new List<Turn>();

                foreach (var message in messages)
                {
                    if (message.Role == ChatMessage.ToolRole)
                        continue;

                    var turn = new Turn
                    {
                        Role = message.Role == ChatMessage.UserRole ? "user" : "model",
                        Parts = new List<Part>
                        {
                            new Part { Text = message.Content }
                        }
                    };

                    turns.Add(turn);
                }

                if (turns.Count > 0)
                {
                    var message = new ClientContentMessage
                    {
                        ClientContent = new ClientContent
                        {
                            Turns = turns,
                            TurnComplete = true
                        }
                    };

                    await SendMessageAsync(message);
                    _logAction(LogLevel.Info, $"Successfully injected {turns.Count} messages into conversation history");
                }
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error injecting conversation history: {ex.Message}");
                throw;
            }
        }

        private static void ValidateSettings(GoogleVoiceSettings settings)
        {
            if (!settings.VoiceActivityDetection.AutomaticDetection)
            {
                throw new InvalidOperationException(
                    "Google Live manual activity signaling is not supported by this toolkit. Set VoiceActivityDetection.AutomaticDetection to true.");
            }
        }

        private void ResetConversationState()
        {
            _setupComplete = false;
            _responseInterrupted = false;
            _currentTranscript.Clear();
            _currentModelText.Clear();
            _currentUserTranscript.Clear();
            _pendingToolCalls.Clear();
            _audioTxCount = 0;
            _audioRxCount = 0;
        }

        private async Task WaitForSetupCompletionAsync()
        {
            var setupTask = _setupCompletionSource?.Task
                ?? throw new InvalidOperationException("Google setup completion task was not initialized.");

            var completedTask = await Task.WhenAny(setupTask, Task.Delay(SETUP_TIMEOUT_MS));
            if (completedTask != setupTask)
            {
                throw new TimeoutException("Timed out waiting for Google Live setupComplete.");
            }

            await setupTask;
        }

        private void ReportTransportError(string message, Exception ex)
        {
            _setupComplete = false;
            _logAction(LogLevel.Error, $"{message}\n{ex}");
            _setupCompletionSource?.TrySetException(new InvalidOperationException(message, ex));
            OnError?.Invoke(message);
        }

        private async Task SendSetupMessageAsync()
        {
            if (_settings == null || !(_webSocket?.State == WebSocketState.Open))
                return;

            var voiceName = _settings.Voice.ToString();

            var setup = new SetupMessage
            {
                Setup = new Setup
                {
                    Model = _settings.Model.ToApiString(),
                    GenerationConfig = new GenerationConfig
                    {
                        ResponseModalities = new List<string> { _settings.ResponseModality },
                        SpeechConfig = new SpeechConfig
                        {
                            VoiceConfig = new VoiceConfig
                            {
                                PrebuiltVoiceConfig = new PrebuiltVoiceConfig
                                {
                                    VoiceName = voiceName
                                }
                            },
                            LanguageCode = _settings.LanguageCode
                        },
                        Temperature = _settings.Temperature,
                        TopP = _settings.TopP,
                        TopK = _settings.TopK,
                        MaxOutputTokens = _settings.MaxTokens
                    },
                    SystemInstruction = new SystemInstruction
                    {
                        Parts = new List<Part>
                        {
                            new Part { Text = _settings.Instructions }
                        }
                    },
                    Tools = _settings.Tools.Count > 0 ? ConvertToolsToGoogleFormat() : null,
                    InputAudioTranscription = _settings.TranscriptionConfig.EnableInputTranscription ? new EmptyObject() : null,
                    OutputAudioTranscription = _settings.TranscriptionConfig.EnableOutputTranscription ? new EmptyObject() : null,
                    RealtimeInputConfig = new RealtimeInputConfig
                    {
                        AutomaticActivityDetection = _settings.VoiceActivityDetection.AutomaticDetection ? new Protocol.AutomaticActivityDetection
                        {
                            StartOfSpeechSensitivity = _settings.VoiceActivityDetection.StartOfSpeechSensitivity.ToApiString(true),
                            EndOfSpeechSensitivity = _settings.VoiceActivityDetection.EndOfSpeechSensitivity.ToApiString(false),
                            PrefixPaddingMs = _settings.VoiceActivityDetection.PrefixPaddingMs,
                            SilenceDurationMs = _settings.VoiceActivityDetection.SilenceDurationMs,
                            Disabled = false
                        } : new Protocol.AutomaticActivityDetection { Disabled = true },
                        ActivityHandling = _settings.VoiceActivityDetection.ActivityHandling.ToApiString(),
                        TurnCoverage = "TURN_INCLUDES_ALL_INPUT"
                    }
                }
            };

            _logAction(LogLevel.Info, $"Configuring session - Transcription (In/Out): {_settings.TranscriptionConfig.EnableInputTranscription}/{_settings.TranscriptionConfig.EnableOutputTranscription}");
            _logAction(LogLevel.Info, $"VAD: Start={_settings.VoiceActivityDetection.StartOfSpeechSensitivity}, End={_settings.VoiceActivityDetection.EndOfSpeechSensitivity}, Prefix={_settings.VoiceActivityDetection.PrefixPaddingMs}ms, Silence={_settings.VoiceActivityDetection.SilenceDurationMs}ms");
            _logAction(LogLevel.Info, $"Turn handling: ActivityHandling={_settings.VoiceActivityDetection.ActivityHandling}, TurnCoverage=ALL_INPUT");

            await SendMessageAsync(setup);
            _logAction(LogLevel.Info, "Setup message sent, awaiting confirmation");
        }

        private void HandleUsageMetadata(JsonElement usageMetadata)
        {
            var report = new UsageReport
            {
                ProviderId = "google",
                InputTokens = usageMetadata.TryGetProperty("promptTokenCount", out var promptTokens) ? promptTokens.GetInt32() : null,
                OutputTokens = usageMetadata.TryGetProperty("candidatesTokenCount", out var candidateTokens) ? candidateTokens.GetInt32() : null,
                IsEstimated = false
            };

            if (report.InputTokens.HasValue || report.OutputTokens.HasValue)
            {
                OnUsageReceived?.Invoke(report);
            }
        }

        private List<Tool> ConvertToolsToGoogleFormat()
        {
            if (_settings?.Tools == null || _settings.Tools.Count == 0)
                return new List<Tool>();

            var functionDeclarations = _settings.Tools.Select(tool =>
            {
                var schema = ToolSchemaInferrer.InferSchema(tool.ArgsType);
                return (FunctionDeclaration)_toolTranslator.TranslateToolDefinition(tool, schema);
            }).ToList();

            return new List<Tool>
            {
                new Tool { FunctionDeclarations = functionDeclarations }
            };
        }

        private Task SendMessageAsync(SetupMessage message) =>
            SendJsonAsync(JsonSerializer.Serialize(message, GoogleJsonContext.Default.SetupMessage), nameof(SetupMessage));

        private Task SendMessageAsync(RealtimeInputMessage message)
        {
            _audioTxCount++;
            if (_audioTxCount % AUDIO_TX_LOG_INTERVAL == 1)
            {
                _logAction(LogLevel.Info, $"[AUDIO-TX] Sent {_audioTxCount} audio chunks to Google");
            }
            return SendJsonAsync(JsonSerializer.Serialize(message, GoogleJsonContext.Default.RealtimeInputMessage), null);
        }

        private Task SendMessageAsync(ClientContentMessage message) =>
            SendJsonAsync(JsonSerializer.Serialize(message, GoogleJsonContext.Default.ClientContentMessage), nameof(ClientContentMessage));

        private Task SendMessageAsync(ToolResponseMessage message) =>
            SendJsonAsync(JsonSerializer.Serialize(message, GoogleJsonContext.Default.ToolResponseMessage), nameof(ToolResponseMessage));

        private async Task SendJsonAsync(string json, string? messageType)
        {
            try
            {
                if (_webSocket?.State != WebSocketState.Open)
                    return;

                if (messageType != null)
                {
                    var logMessage = json.Length > MESSAGE_LOG_TRUNCATE_LENGTH
                        ? json.Substring(0, MESSAGE_LOG_TRUNCATE_LENGTH) + "..."
                        : json;
                    _logAction(LogLevel.Info, $"[MSG-TX] {messageType}: {logMessage}");
                }

                var buffer = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (ObjectDisposedException)
            {
                if (!_isDisposed)
                {
                    throw;
                }
            }
            catch (WebSocketException ex)
            {
                ReportTransportError($"WebSocket send failed: {ex.Message}", ex);
                throw;
            }
        }

        private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[AUDIO_BUFFER_SIZE];

            try
            {
                _logAction(LogLevel.Info, "[RX-LOOP] Receive loop started");

                while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    using var messageBuilder = new MemoryStream();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                        if (result.MessageType == WebSocketMessageType.Text || result.MessageType == WebSocketMessageType.Binary)
                        {
                            messageBuilder.Write(buffer, 0, result.Count);
                        }
                        else if (result.MessageType == WebSocketMessageType.Close)
                        {
                            var closeStatus = result.CloseStatus?.ToString() ?? "Unknown";
                            var closeDescription = result.CloseStatusDescription ?? "No description provided";
                            var closeMessage = $"Connection closed by server: {closeStatus} - {closeDescription}";
                            _logAction(LogLevel.Info, $"[RX-LOOP] Close message received from server - Status: {closeStatus}, Description: {closeDescription}");
                            _setupCompletionSource?.TrySetException(new InvalidOperationException(closeMessage));
                            OnStatusChanged?.Invoke(closeMessage);
                            return;
                        }
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text || result.MessageType == WebSocketMessageType.Binary)
                    {
                        var message = Encoding.UTF8.GetString(messageBuilder.ToArray());
                        await ProcessReceivedMessage(message);
                    }
                }

                _logAction(LogLevel.Info, $"[RX-LOOP] Exited loop - WebSocket State: {_webSocket?.State}, Cancelled: {cancellationToken.IsCancellationRequested}");
            }
            catch (OperationCanceledException)
            {
                _logAction(LogLevel.Info, "[RX-LOOP] Receive loop cancelled");
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"[RX-LOOP] Error in message receive loop: {ex.Message}");
                _logAction(LogLevel.Error, $"[RX-LOOP] Stack trace: {ex.StackTrace}");
                _setupCompletionSource?.TrySetException(ex);
                OnError?.Invoke($"Message receive error: {ex.Message}");
            }
        }

        private async Task ProcessReceivedMessage(string message)
        {
            try
            {
                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;

                if (root.TryGetProperty("setupComplete", out _))
                {
                    _setupComplete = true;
                    _setupCompletionSource?.TrySetResult(true);
                    _logAction(LogLevel.Info, "Setup complete - ready for interaction");
                    OnStatusChanged?.Invoke("Ready");
                    return;
                }

                if (root.TryGetProperty("usageMetadata", out var usageMetadata))
                {
                    HandleUsageMetadata(usageMetadata);
                }

                if (root.TryGetProperty("serverContent", out var serverContent))
                {
                    await HandleServerContent(serverContent);
                    return;
                }

                if (root.TryGetProperty("toolCall", out var toolCall))
                {
                    await HandleToolCall(toolCall);
                    return;
                }

                if (root.TryGetProperty("toolCallCancellation", out var cancellation))
                {
                    HandleToolCallCancellation(cancellation);
                    return;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var errorMessage = error.ToString();
                    _logAction(LogLevel.Error, $"[ERROR-FROM-SERVER] {errorMessage}");
                    _setupCompletionSource?.TrySetException(new InvalidOperationException(errorMessage));
                    OnError?.Invoke($"Server error: {errorMessage}");
                    return;
                }

                _logAction(LogLevel.Info, $"Received unhandled message type: {message}");
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error processing received message: {ex.Message}");
                OnError?.Invoke($"Message processing error: {ex.Message}");
            }
        }

        private async Task HandleServerContent(JsonElement serverContent)
        {
            if (serverContent.TryGetProperty("inputTranscription", out var inputTranscription))
            {
                if (inputTranscription.TryGetProperty("text", out var inputText))
                {
                    var textValue = inputText.GetString();
                    if (!string.IsNullOrEmpty(textValue))
                    {
                        if (_currentUserTranscript.Length == 0)
                        {
                            _logAction(LogLevel.Info, "[VAD] Speech detected - starting user transcription");
                        }
                        _currentUserTranscript.Append(textValue);
                    }
                }
            }

            if (serverContent.TryGetProperty("outputTranscription", out var outputTranscription))
            {
                if (outputTranscription.TryGetProperty("text", out var outputText))
                {
                    var textValue = outputText.GetString();
                    if (!string.IsNullOrEmpty(textValue))
                    {
                        _currentTranscript.Append(textValue);
                    }
                }
            }

            if (serverContent.TryGetProperty("interrupted", out var interrupted) && interrupted.GetBoolean())
            {
                _logAction(LogLevel.Info, "Response interrupted by user - discarding remaining audio from interrupted turn");
                _responseInterrupted = true;
                OnInterruptDetected?.Invoke();
                _currentTranscript.Clear();
                _currentModelText.Clear();
                return;
            }

            if (serverContent.TryGetProperty("modelTurn", out var modelTurn))
            {
                FlushUserTranscript();

                if (modelTurn.TryGetProperty("parts", out var parts))
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var text))
                        {
                            var isThought = part.TryGetProperty("thought", out var thoughtProp) && thoughtProp.GetBoolean();

                            var textValue = text.GetString();
                            if (!string.IsNullOrEmpty(textValue))
                            {
                                if (isThought)
                                {
                                    _logAction(LogLevel.Info, $"[THOUGHT] {textValue}");
                                }
                                else
                                {
                                    _currentModelText.Append(textValue);
                                }
                            }
                        }

                        if (part.TryGetProperty("inlineData", out var inlineData))
                        {
                            if (inlineData.TryGetProperty("mimeType", out var mimeType) &&
                                mimeType.GetString()?.Contains("audio") == true &&
                                inlineData.TryGetProperty("data", out var data))
                            {
                                var audioData = data.GetString();
                                if (!string.IsNullOrEmpty(audioData))
                                {
                                    if (_responseInterrupted)
                                    {
                                        _logAction(LogLevel.Info, "[AUDIO-RX] Discarding audio chunk from interrupted response");
                                    }
                                    else
                                    {
                                        _audioRxCount++;
                                        if (_audioRxCount % AUDIO_RX_LOG_INTERVAL == 1)
                                        {
                                            _logAction(LogLevel.Info, $"[AUDIO-RX] Received {_audioRxCount} audio chunks from Google");
                                        }
                                        OnAudioReceived?.Invoke(audioData);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (serverContent.TryGetProperty("turnComplete", out var turnComplete) && turnComplete.GetBoolean())
            {
                _logAction(LogLevel.Info, "[TURN] Model turn complete - ready for user input");

                if (_responseInterrupted)
                {
                    _logAction(LogLevel.Info, "[TURN] Turn complete after interruption - ready for new response");
                    _responseInterrupted = false;
                }

                FlushUserTranscript();
                FlushAssistantMessage();
            }
        }

        private void FlushUserTranscript()
        {
            if (_currentUserTranscript.Length == 0)
            {
                return;
            }

            var userMessage = _currentUserTranscript.ToString();
            _logAction(LogLevel.Info, $"[USER-COMPLETE] {userMessage}");
            OnMessageReceived?.Invoke(ChatMessage.CreateUserMessage(userMessage));
            _currentUserTranscript.Clear();
        }

        private void FlushAssistantMessage()
        {
            string transcriptText = ChooseAssistantMessageText();
            if (string.IsNullOrWhiteSpace(transcriptText))
            {
                _currentTranscript.Clear();
                _currentModelText.Clear();
                return;
            }

            _logAction(LogLevel.Info, $"[AI] {transcriptText}");
            OnMessageReceived?.Invoke(ChatMessage.CreateAssistantMessage(transcriptText));
            _currentTranscript.Clear();
            _currentModelText.Clear();
        }

        private string ChooseAssistantMessageText()
        {
            if (string.Equals(_settings?.ResponseModality, "AUDIO", StringComparison.OrdinalIgnoreCase) &&
                _currentTranscript.Length > 0)
            {
                return _currentTranscript.ToString();
            }

            if (_currentModelText.Length > 0)
            {
                return _currentModelText.ToString();
            }

            return _currentTranscript.ToString();
        }

        private async Task HandleToolCall(JsonElement toolCall)
        {
            if (!toolCall.TryGetProperty("functionCalls", out var functionCalls))
                return;

            // Flush user transcript BEFORE tool call messages to maintain correct order
            if (_currentUserTranscript.Length > 0)
            {
                var userMessage = _currentUserTranscript.ToString();
                _logAction(LogLevel.Info, $"[USER-COMPLETE] {userMessage}");
                OnMessageReceived?.Invoke(ChatMessage.CreateUserMessage(userMessage));
                _currentUserTranscript.Clear();
            }

            var responses = new List<FunctionResponse>();

            foreach (var functionCall in functionCalls.EnumerateArray())
            {
                if (!functionCall.TryGetProperty("id", out var idElement) ||
                    !functionCall.TryGetProperty("name", out var nameElement))
                    continue;

                var id = idElement.GetString() ?? "";
                var name = nameElement.GetString() ?? "";

                _logAction(LogLevel.Info, $"Tool call received: {name} (ID: {id})");

                var tool = _settings?.Tools.FirstOrDefault(t => t.Name == name);

                if (tool != null)
                {
                    try
                    {
                        var argsJson = functionCall.TryGetProperty("args", out var args)
                            ? args.GetRawText()
                            : "{}";

                        _logAction(LogLevel.Info, $"Executing tool: {name}");
                        var result = await tool.ExecuteAsync(argsJson);

                        responses.Add(new FunctionResponse
                        {
                            Id = id,
                            Name = name,
                            Response = new FunctionResponseData { Result = result }
                        });

                        OnMessageReceived?.Invoke(ChatMessage.CreateAssistantMessage($"Calling tool: {name}"));
                        OnMessageReceived?.Invoke(ChatMessage.CreateToolMessage(name, result, id));
                    }
                    catch (Exception ex)
                    {
                        _logAction(LogLevel.Error, $"Error executing tool {name}: {ex.Message}");
                        responses.Add(new FunctionResponse
                        {
                            Id = id,
                            Name = name,
                            Response = new FunctionResponseData { Result = $"Error: {ex.Message}" }
                        });
                    }
                }
                else
                {
                    _logAction(LogLevel.Warn, $"Tool not found: {name}");
                    responses.Add(new FunctionResponse
                    {
                        Id = id,
                        Name = name,
                        Response = new FunctionResponseData { Result = $"Tool not found: {name}" }
                    });
                }
            }

            if (responses.Count > 0)
            {
                var toolResponse = new ToolResponseMessage
                {
                    ToolResponse = new ToolResponse
                    {
                        FunctionResponses = responses
                    }
                };

                await SendMessageAsync(toolResponse);
                _logAction(LogLevel.Info, $"Sent {responses.Count} tool response(s)");
            }
        }

        private void HandleToolCallCancellation(JsonElement cancellation)
        {
            if (cancellation.TryGetProperty("ids", out var ids))
            {
                var cancelledIds = new List<string>();
                foreach (var id in ids.EnumerateArray())
                {
                    var idValue = id.GetString();
                    if (!string.IsNullOrEmpty(idValue))
                    {
                        cancelledIds.Add(idValue);
                        _pendingToolCalls.Remove(idValue);
                    }
                }

                if (cancelledIds.Count > 0)
                {
                    _logAction(LogLevel.Info, $"Tool calls cancelled: {string.Join(", ", cancelledIds)}");
                }
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources asynchronously.
        /// </summary>
        /// <returns>A task that represents the asynchronous dispose operation.</returns>
        public async ValueTask DisposeAsync()
        {
            if (_isDisposed)
                return;

            try
            {
                await DisconnectAsync();

                _webSocket?.Dispose();
                _cts?.Dispose();

                _isDisposed = true;
                _logAction(LogLevel.Info, "Google voice provider disposed");
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error during disposal: {ex.Message}");
            }
        }
    }
}
