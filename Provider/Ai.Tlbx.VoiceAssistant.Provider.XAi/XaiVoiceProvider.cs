using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Reflection;
using Ai.Tlbx.VoiceAssistant.Provider.XAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.XAi.Protocol;
using Ai.Tlbx.VoiceAssistant.Provider.XAi.Translation;

namespace Ai.Tlbx.VoiceAssistant.Provider.XAi
{
    /// <summary>
    /// xAI Grok voice provider implementation for real-time conversation via WebSocket.
    /// Compatible with OpenAI Realtime API specification.
    /// </summary>
    public sealed class XaiVoiceProvider : IVoiceProvider
    {
        private const string REALTIME_WEBSOCKET_ENDPOINT = "wss://api.x.ai/v1/realtime";
        private const int CONNECTION_TIMEOUT_MS = 10000;
        private const int AUDIO_BUFFER_SIZE = 32384;

        private readonly string _apiKey;
        private readonly Action<LogLevel, string> _logAction;
        private readonly XaiToolTranslator _toolTranslator = new();
        private ClientWebSocket? _webSocket;
        private Task? _receiveTask;
        private CancellationTokenSource? _cts;
        private bool _isDisposed = false;
        private XaiVoiceSettings? _settings;

        private bool _hasActiveResponse = false;
        private readonly StringBuilder _currentAiMessage = new();
        private string _currentResponseId = string.Empty;
        private readonly StringBuilder _currentFunctionArgs = new();
        private string _currentFunctionName = string.Empty;
        private string _currentCallId = string.Empty;

        /// <summary>
        /// Gets a value indicating whether the provider is connected and ready.
        /// </summary>
        public bool IsConnected => _webSocket?.State == WebSocketState.Open;

        /// <summary>
        /// Gets the required input sample rate for xAI (24kHz, same as OpenAI).
        /// </summary>
        public AudioSampleRate RequiredInputSampleRate => AudioSampleRate.Rate24000;

        /// <summary>
        /// Gets or sets the settings for this provider instance.
        /// </summary>
        public XaiVoiceSettings? Settings
        {
            get => _settings;
            set => _settings = value;
        }

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

        /// <summary>
        /// Initializes a new instance of the <see cref="XaiVoiceProvider"/> class.
        /// </summary>
        /// <param name="apiKey">The xAI API key. If null, will try to get from environment variable XAI_API_KEY.</param>
        /// <param name="logAction">Optional logging action.</param>
        public XaiVoiceProvider(string? apiKey = null, Action<LogLevel, string>? logAction = null)
        {
            _apiKey = apiKey ?? Environment.GetEnvironmentVariable("XAI_API_KEY")
                ?? throw new InvalidOperationException("xAI API key must be provided or set in XAI_API_KEY environment variable");
            _logAction = logAction ?? ((level, message) => { });
        }

        /// <summary>
        /// Connects to the xAI real-time API using the specified settings.
        /// </summary>
        /// <param name="settings">xAI-specific voice settings.</param>
        /// <returns>A task representing the connection operation.</returns>
        public async Task ConnectAsync(IVoiceSettings settings)
        {
            if (settings is not XaiVoiceSettings xaiSettings)
            {
                throw new ArgumentException("Settings must be of type XaiVoiceSettings for xAI provider", nameof(settings));
            }

            _settings = xaiSettings;
            _logAction(LogLevel.Info, $"Settings configured - Voice: {_settings.Voice}, Speed: {_settings.TalkingSpeed}");

            try
            {
                OnStatusChanged?.Invoke("Connecting to xAI...");

                _webSocket = new ClientWebSocket();
                _webSocket.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");

                var connectionCts = new CancellationTokenSource(CONNECTION_TIMEOUT_MS);

                var uri = new Uri(REALTIME_WEBSOCKET_ENDPOINT);
                await _webSocket.ConnectAsync(uri, connectionCts.Token);
                connectionCts.Dispose();

                OnStatusChanged?.Invoke("Connected to xAI");

                _cts = new CancellationTokenSource();
                _receiveTask = ReceiveMessagesAsync(_cts.Token);

                await SendSessionConfigurationAsync();
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Failed to connect to xAI: {ex.Message}");
                OnError?.Invoke($"Connection failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Disconnects from the xAI real-time API.
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

                OnStatusChanged?.Invoke("Disconnected");
                _logAction(LogLevel.Info, "Disconnected from xAI");
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error during disconnection: {ex.Message}");
                OnError?.Invoke($"Disconnection error: {ex.Message}");
            }
        }

        /// <summary>
        /// Updates the settings for an existing connection and sends the configuration to xAI.
        /// </summary>
        /// <param name="settings">The new settings to apply.</param>
        /// <returns>A task representing the update operation.</returns>
        public async Task UpdateSettingsAsync(IVoiceSettings settings)
        {
            if (settings is not XaiVoiceSettings xaiSettings)
            {
                throw new ArgumentException("Settings must be of type XaiVoiceSettings for xAI provider", nameof(settings));
            }

            _settings = xaiSettings;
            _logAction(LogLevel.Info, $"Settings configured - Voice: {_settings.Voice}, Speed: {_settings.TalkingSpeed}");

            if (IsConnected)
            {
                _logAction(LogLevel.Info, "Updating session configuration for existing connection");
                await SendSessionConfigurationAsync();
            }
        }

        /// <summary>
        /// Processes audio data received from the microphone and sends it to xAI.
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
                var audioMessage = new XaiAudioBufferAppendMessage { Audio = base64Audio };
                await SendMessageAsync(JsonSerializer.Serialize(audioMessage, XaiJsonContext.Default.XaiAudioBufferAppendMessage));
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error processing audio: {ex.Message}");
                OnError?.Invoke($"Audio processing error: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends an interrupt signal to xAI to stop current response generation.
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
                var interruptMessage = new XaiResponseCancelMessage();
                await SendMessageAsync(JsonSerializer.Serialize(interruptMessage, XaiJsonContext.Default.XaiResponseCancelMessage));
                _logAction(LogLevel.Info, "Interrupt signal sent to xAI");
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
                foreach (var message in messages)
                {
                    if (message.Role == ChatMessage.ToolRole)
                        continue;

                    var conversationItem = new XaiConversationItemCreateMessage
                    {
                        Item = new XaiConversationItem
                        {
                            Type = "message",
                            Role = message.Role == ChatMessage.UserRole ? "user" : "assistant",
                            Content = new List<XaiContentPart>
                            {
                                new XaiContentPart
                                {
                                    Type = message.Role == ChatMessage.UserRole ? "input_text" : "output_text",
                                    Text = message.Content
                                }
                            }
                        }
                    };

                    await SendMessageAsync(JsonSerializer.Serialize(conversationItem, XaiJsonContext.Default.XaiConversationItemCreateMessage));
                    await Task.Delay(50);
                }

                _logAction(LogLevel.Info, $"Successfully injected {messages.Count()} messages into conversation history");
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error injecting conversation history: {ex.Message}");
                throw;
            }
        }

        private async Task SendSessionConfigurationAsync()
        {
            if (_settings == null || !IsConnected)
                return;

            var voiceString = _settings.Voice.ToString();
            _logAction(LogLevel.Info, $"Configuring session with voice: {_settings.Voice} -> {voiceString}");

            var tools = new List<XaiToolDefinition>();

            if (_settings.EnableWebSearch)
            {
                tools.Add(XaiToolTranslator.CreateWebSearchTool());
            }

            if (_settings.EnableXSearch)
            {
                tools.Add(XaiToolTranslator.CreateXSearchTool());
            }

            foreach (var tool in _settings.Tools)
            {
                tools.Add((XaiToolDefinition)_toolTranslator.TranslateToolDefinition(tool, ToolSchemaInferrer.InferSchema(tool.ArgsType)));
            }

            var sessionConfig = new XaiSessionUpdateMessage
            {
                EventId = $"evt_{Guid.NewGuid()}",
                Session = new XaiSessionConfig
                {
                    Voice = voiceString,
                    Instructions = _settings.Instructions,
                    Tools = tools,
                    ToolChoice = "auto",
                    Audio = new XaiAudioConfig
                    {
                        Input = new XaiAudioEndpointConfig
                        {
                            Format = new XaiAudioFormatConfig
                            {
                                Type = _settings.AudioFormatType,
                                Rate = _settings.AudioSampleRate
                            }
                        },
                        Output = new XaiAudioEndpointConfig
                        {
                            Format = new XaiAudioFormatConfig
                            {
                                Type = _settings.AudioFormatType,
                                Rate = _settings.AudioSampleRate
                            },
                            Speed = _settings.TalkingSpeed
                        }
                    },
                    InputAudioTranscription = _settings.InputAudioLanguage != null
                        ? new XaiInputAudioTranscriptionConfig { Language = _settings.InputAudioLanguage }
                        : null,
                    TurnDetection = _settings.TurnDetection != null
                        ? new XaiTurnDetectionConfig
                        {
                            Type = _settings.TurnDetection.Type,
                            Threshold = _settings.TurnDetection.Threshold,
                            PrefixPaddingMs = _settings.TurnDetection.PrefixPaddingMs,
                            SilenceDurationMs = _settings.TurnDetection.SilenceDurationMs,
                            CreateResponse = _settings.TurnDetection.CreateResponse,
                            InterruptResponse = _settings.TurnDetection.InterruptResponse
                        }
                        : null
                }
            };

            var jsonMessage = JsonSerializer.Serialize(sessionConfig, XaiJsonContextIndented.Default.XaiSessionUpdateMessage);
            _logAction(LogLevel.Info, $"Sending session config to xAI:\n{jsonMessage}");
            await SendMessageAsync(jsonMessage);
            _logAction(LogLevel.Info, "Session configuration sent to xAI");
        }

        private async Task SendMessageAsync(string message)
        {
            try
            {
                if (_webSocket?.State != WebSocketState.Open)
                    return;

                var buffer = Encoding.UTF8.GetBytes(message);
                await _webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (ObjectDisposedException)
            {
                // WebSocket closed during send - expected during disconnect
            }
            catch (WebSocketException ex)
            {
                _logAction(LogLevel.Warn, $"WebSocket send failed: {ex.Message}");
            }
        }

        private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[AUDIO_BUFFER_SIZE];

            try
            {
                while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    using var messageBuilder = new MemoryStream();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            messageBuilder.Write(buffer, 0, result.Count);
                        }
                        else if (result.MessageType == WebSocketMessageType.Close)
                        {
                            OnStatusChanged?.Invoke("Connection closed by server");
                            return;
                        }
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(messageBuilder.ToArray());
                        await ProcessReceivedMessage(message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error in message receive loop: {ex.Message}");
                OnError?.Invoke($"Message receive error: {ex.Message}");
            }
        }

        private async Task ProcessReceivedMessage(string message)
        {
            try
            {
                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;

                if (!root.TryGetProperty("type", out var typeElement))
                    return;

                var messageType = typeElement.GetString();

                switch (messageType)
                {
                    case "session.created":
                        _logAction(LogLevel.Info, "Session created by xAI");
                        break;
                    case "session.updated":
                        break;
                    case "response.created":
                        HandleResponseCreated(root);
                        break;
                    case "conversation.item.created":
                        break;
                    case "response.output_audio.delta":
                        await HandleAudioResponse(root);
                        break;
                    case "response.output_audio_transcript.done":
                        HandleAudioTranscriptDone(root);
                        break;
                    case "response.done":
                        await HandleResponseDone(root);
                        break;
                    case "response.output_text.delta":
                        HandleTextDelta(root);
                        break;
                    case "response.output_text.done":
                        await HandleTextDone();
                        break;
                    case "response.function_call_arguments.delta":
                        HandleFunctionCallDelta(root);
                        break;
                    case "response.function_call_arguments.done":
                        await HandleFunctionCallDone(root);
                        break;
                    case "input_audio_buffer.speech_started":
                        _logAction(LogLevel.Info, "User started speaking - server detected interruption");
                        HandleInterruption();
                        break;
                    case "input_audio_buffer.speech_stopped":
                        _logAction(LogLevel.Info, "User stopped speaking");
                        break;
                    case "conversation.item.input_audio_transcription.completed":
                        HandleInputAudioTranscriptionCompleted(root);
                        break;
                    case "error":
                        HandleError(root);
                        break;
                    case "input_audio_buffer.committed":
                    case "response.output_item.added":
                    case "response.content_part.added":
                    case "response.content_part.done":
                    case "response.output_audio.done":
                    case "response.output_audio_transcript.delta":
                    case "response.output_item.done":
                    case "rate_limits.updated":
                    case "conversation.item.input_audio_transcription.delta":
                    case "conversation.item.added":
                    case "conversation.item.done":
                    case "conversation.item.retrieved":
                    case "conversation.item.input_audio_transcription.segment":
                    case "input_audio_buffer.timeout_triggered":
                    case "output_audio_buffer.started":
                    case "output_audio_buffer.stopped":
                    case "output_audio_buffer.cleared":
                        break;
                    default:
                        _logAction(LogLevel.Info, $"Unhandled message type: {messageType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error processing received message: {ex.Message}");
            }
        }

        private async Task HandleAudioResponse(JsonElement root)
        {
            if (root.TryGetProperty("delta", out var delta))
            {
                var audioData = delta.GetString();
                if (!string.IsNullOrEmpty(audioData))
                {
                    OnAudioReceived?.Invoke(audioData);
                }
            }

            await Task.CompletedTask;
        }

        private void HandleFunctionCallDelta(JsonElement root)
        {
            if (root.TryGetProperty("delta", out var delta))
            {
                var argsDelta = delta.GetString() ?? "";
                _currentFunctionArgs.Append(argsDelta);

                if (root.TryGetProperty("name", out var nameElement))
                {
                    _currentFunctionName = nameElement.GetString() ?? "";
                }

                if (root.TryGetProperty("call_id", out var callIdElement))
                {
                    _currentCallId = callIdElement.GetString() ?? "";
                }
            }
        }

        private async Task HandleFunctionCallDone(JsonElement root)
        {
            if (root.TryGetProperty("name", out var nameElement) &&
                root.TryGetProperty("call_id", out var callIdElement))
            {
                var functionName = nameElement.GetString() ?? _currentFunctionName;
                var callId = callIdElement.GetString() ?? _currentCallId;
                var argumentsJson = _currentFunctionArgs.ToString();

                _logAction(LogLevel.Info, $"Function call complete: {functionName} (ID: {callId})");

                var tool = _settings?.Tools.FirstOrDefault(t => t.Name == functionName);

                if (tool != null)
                {
                    try
                    {
                        _logAction(LogLevel.Info, $"Executing tool: {functionName}");
                        var result = await tool.ExecuteAsync(argumentsJson);

                        await SendToolResultAsync(callId, result);

                        var formattedArgs = FormatToolArguments(argumentsJson);
                        var toolCallMessage = ChatMessage.CreateAssistantMessage($"Calling tool: {functionName}\nArguments: {formattedArgs}");
                        OnMessageReceived?.Invoke(toolCallMessage);

                        var toolResponseMessage = ChatMessage.CreateToolMessage(functionName, result, callId);
                        OnMessageReceived?.Invoke(toolResponseMessage);
                    }
                    catch (Exception ex)
                    {
                        _logAction(LogLevel.Error, $"Error executing tool {functionName}: {ex.Message}");
                        await SendToolResultAsync(callId, $"Error: {ex.Message}");
                    }
                }
                else
                {
                    _logAction(LogLevel.Warn, $"Tool not found: {functionName}");
                    await SendToolResultAsync(callId, $"Tool not found: {functionName}");
                }

                _currentFunctionArgs.Clear();
                _currentFunctionName = "";
                _currentCallId = "";
            }
        }

        private async Task SendToolResultAsync(string callId, string result)
        {
            var toolResponse = new XaiConversationItemCreateMessage
            {
                Item = new XaiConversationItem
                {
                    Type = "function_call_output",
                    CallId = callId,
                    Output = result
                }
            };

            await SendMessageAsync(JsonSerializer.Serialize(toolResponse, XaiJsonContext.Default.XaiConversationItemCreateMessage));
            _logAction(LogLevel.Info, $"Tool result sent for call {callId}");

            var responseCreate = new XaiResponseCreateMessage();
            await SendMessageAsync(JsonSerializer.Serialize(responseCreate, XaiJsonContext.Default.XaiResponseCreateMessage));
            _logAction(LogLevel.Info, "Requested AI response after tool execution");
        }

        private void HandleError(JsonElement root)
        {
            if (root.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var messageElement))
            {
                var errorMessage = messageElement.GetString() ?? "Unknown error";

                if (errorMessage.Contains("Cannot cancel response", StringComparison.OrdinalIgnoreCase) && !_hasActiveResponse)
                {
                    _logAction(LogLevel.Info, $"Ignoring cancellation error - no active response: {errorMessage}");
                    return;
                }

                _logAction(LogLevel.Error, $"xAI API error: {errorMessage}");
                OnError?.Invoke(errorMessage);
            }
        }

        private void HandleAudioTranscriptDone(JsonElement root)
        {
            if (root.TryGetProperty("transcript", out var transcript))
            {
                var text = transcript.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    _logAction(LogLevel.Info, $"Audio transcript: {text}");

                    var message = ChatMessage.CreateAssistantMessage(text);
                    OnMessageReceived?.Invoke(message);
                }
            }
        }

        private void HandleInputAudioTranscriptionCompleted(JsonElement root)
        {
            if (root.TryGetProperty("transcript", out var transcript))
            {
                var text = transcript.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    _logAction(LogLevel.Info, $"User transcript: {text}");

                    var message = ChatMessage.CreateUserMessage(text);
                    OnMessageReceived?.Invoke(message);
                }
            }
        }

        private void HandleTextDelta(JsonElement root)
        {
            if (root.TryGetProperty("delta", out var deltaElem) &&
                deltaElem.TryGetProperty("text", out var textElem))
            {
                string deltaText = textElem.GetString() ?? string.Empty;
                _currentAiMessage.Append(deltaText);
            }
        }

        private void HandleResponseCreated(JsonElement root)
        {
            if (root.TryGetProperty("response", out var response) &&
                response.TryGetProperty("id", out var idElement))
            {
                _currentResponseId = idElement.GetString() ?? "";
                _hasActiveResponse = true;
                _logAction(LogLevel.Info, $"New response started: {_currentResponseId}");
            }
        }

        private async Task HandleResponseDone(JsonElement root)
        {
            _hasActiveResponse = false;
            _logAction(LogLevel.Info, "Response completed");

            // Parse usage from response.done event (OpenAI-compatible format)
            if (root.TryGetProperty("response", out var response) &&
                response.TryGetProperty("usage", out var usage))
            {
                var report = new UsageReport
                {
                    ProviderId = "xai",
                    InputTokens = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : null,
                    OutputTokens = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : null,
                    InputAudioTokens = usage.TryGetProperty("input_audio_tokens", out var iat) ? iat.GetInt32() : null,
                    OutputAudioTokens = usage.TryGetProperty("output_audio_tokens", out var oat) ? oat.GetInt32() : null,
                    IsEstimated = false
                };

                _logAction(LogLevel.Info, $"Usage: in={report.TotalInputTokens}, out={report.TotalOutputTokens}, audio_in={report.InputAudioTokens}, audio_out={report.OutputAudioTokens}");
                OnUsageReceived?.Invoke(report);
            }

            await Task.CompletedTask;
        }

        private async Task HandleTextDone()
        {
            if (_currentAiMessage.Length > 0)
            {
                string messageText = _currentAiMessage.ToString();
                _logAction(LogLevel.Info, $"AI Text Complete: {messageText}");

                var message = ChatMessage.CreateAssistantMessage(messageText);
                OnMessageReceived?.Invoke(message);

                _currentAiMessage.Clear();
            }
            await Task.CompletedTask;
        }

        private void HandleInterruption()
        {
            _ = HandleInterruptionSafeAsync();
        }

        private async Task HandleInterruptionSafeAsync()
        {
            try
            {
                _logAction(LogLevel.Info, "Speech detected - user interruption");

                OnInterruptDetected?.Invoke();

                if (_hasActiveResponse)
                {
                    _logAction(LogLevel.Info, "Interrupting active AI response");
                    await SendInterruptAsync();
                    _hasActiveResponse = false;
                }
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error handling interruption: {ex.Message}");
            }
        }

        private string FormatToolArguments(string argumentsJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson == "{}")
                {
                    return "(no arguments)";
                }

                using var jsonDoc = JsonDocument.Parse(argumentsJson);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    jsonDoc.RootElement.WriteTo(writer);
                }
                var formatted = Encoding.UTF8.GetString(stream.ToArray());

                if (!formatted.Contains('\n') || formatted.Length < 50)
                {
                    return argumentsJson;
                }

                return formatted;
            }
            catch
            {
                return argumentsJson;
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
                _logAction(LogLevel.Info, "xAI voice provider disposed");
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error during disposal: {ex.Message}");
            }
        }
    }
}
