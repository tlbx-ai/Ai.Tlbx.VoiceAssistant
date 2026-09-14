using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Net.Http;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Reflection;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Protocol;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Translation;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi
{
    /// <summary>
    /// OpenAI voice provider implementation for real-time conversation via WebSocket.
    /// </summary>
    public sealed partial class OpenAiVoiceProvider : IVoiceProvider
    {
        private const int CONNECTION_TIMEOUT_MS = 10000;
        private const int DISCONNECTION_TIMEOUT_MS = 5000;
        private const int AUDIO_BUFFER_SIZE = 32384;
        private const int AUDIO_SEND_QUEUE_CAPACITY = 100;

        private readonly string _apiKey;
        private readonly Action<LogLevel, string> _logAction;
        private readonly OpenAiToolTranslator _toolTranslator = new();
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private ClientWebSocket? _webSocket;
        private Task? _receiveTask;
        private CancellationTokenSource? _cts;
        private Channel<string>? _audioSendChannel;
        private CancellationTokenSource? _audioSendCts;
        private Task? _audioSendTask;
        private long _droppedAudioChunks;
        private bool _isDisposed = false;
        private OpenAiVoiceSettings? _settings;
        
        // State tracking
        private bool _hasActiveResponse = false;
        private bool _responseRequested;
        private string _responseCreateReason = "unspecified";
        private readonly object _responseGate = new();
        private readonly HashSet<string> _completedResponses = new(StringComparer.Ordinal);
        private readonly HashSet<string> _executedCalls = new(StringComparer.Ordinal);
        private long _sessionGeneration;
        private long _turnGeneration;
        private int _pendingToolBatches;
        private bool _discardResponseTools;
        private bool _toolExecutionUncertain;
        private string? _pendingCreateEventId;
        private string? _pendingCancelEventId;
        private bool _userSpeaking;
        private bool _cancelRequested;
        private readonly StringBuilder _currentAiMessage = new();
        private string _currentResponseId = string.Empty;
        private OpenAiRealtimeResponseTrace? _currentResponseTrace;
        private readonly StringBuilder _currentResponseOutputText = new();
        private string? _currentResponseAudioTranscript;
        private string? _lastInputTranscript;

        /// <summary>
        /// Gets a value indicating whether the provider is connected and ready.
        /// </summary>
        public bool IsConnected => _webSocket?.State == WebSocketState.Open;

        /// <summary>
        /// Gets the required input audio sample rate for OpenAI (24kHz).
        /// </summary>
        public AudioSampleRate RequiredInputSampleRate => AudioSampleRate.Rate24000;

        /// <summary>
        /// Gets or sets the settings for this provider instance.
        /// </summary>
        public OpenAiVoiceSettings? Settings 
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
        /// Callback invoked when the provider needs to wait for queued playback to drain.
        /// </summary>
        public Func<TimeSpan?, Task<bool>>? WaitForPlaybackDrainAsync { get; set; }

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
        /// Callback invoked when an OpenAI Realtime response completes with trace details.
        /// </summary>
        public Action<OpenAiRealtimeResponseTrace>? OnResponseTraceCompleted { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenAiVoiceProvider"/> class.
        /// </summary>
        /// <param name="apiKey">The OpenAI API key. If null, will try to get from environment variable OPENAI_API_KEY.</param>
        /// <param name="logAction">Optional logging action.</param>
        /// <param name="httpClient">Optional HttpClient for session creation. If not provided, a new instance will be created and owned by this provider.</param>
        public OpenAiVoiceProvider(string? apiKey = null, Action<LogLevel, string>? logAction = null, HttpClient? httpClient = null)
        {
            _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                ?? throw new InvalidOperationException("OpenAI API key must be provided or set in OPENAI_API_KEY environment variable");
            _logAction = logAction ?? ((level, message) => { /* no-op */ });
            _httpClient = httpClient ?? new HttpClient();
            _ownsHttpClient = httpClient == null;
        }

        /// <summary>
        /// Creates a session and obtains an ephemeral key for WebSocket connection.
        /// </summary>
        /// <returns>The ephemeral key for authentication.</returns>
        private async Task<string> CreateSessionAsync()
        {
            if (_settings == null)
                throw new InvalidOperationException("Settings must be configured before creating session");

            // Creating client secret for ephemeral key

            // Create request with session configuration
            var instructions = OpenAiInstructionsComposer.Compose(_settings);
            var request = new ClientSecretRequest
            {
                ExpiresAfter = new ExpiresAfter
                {
                    Anchor = "created_at",
                    Seconds = 600  // 10 minutes
                },
                Session = new SessionSpec
                {
                    Type = "realtime",
                    Model = _settings.GetModelId(),
                    Instructions = instructions.FinalText,
                    Reasoning = BuildReasoningConfig(_settings)
                }
            };

            var json = JsonSerializer.Serialize(request, OpenAiJsonContext.Default.ClientSecretRequest);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _settings.ClientSecretsConnection.BuildUri(fallbackApiKey: _apiKey));
            if (!string.IsNullOrWhiteSpace(_settings.SafetyIdentifier))
            {
                httpRequest.Headers.TryAddWithoutValidation("OpenAI-Safety-Identifier", _settings.SafetyIdentifier);
            }
            httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");

            _settings.ClientSecretsConnection.Apply(httpRequest, _apiKey);
            var requested = RecordRequestedSession(json, "client-secret", instructions);
            using var response = await _httpClient.SendAsync(httpRequest);
            RecordSentSession(requested);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logAction(LogLevel.Error, $"Failed to create client secret: {response.StatusCode} - {error}");
                throw new InvalidOperationException($"Failed to create OpenAI client secret: {response.StatusCode}");
            }
            
            var responseJson = await response.Content.ReadAsStringAsync();
            // Parsing client secret response
            
            using var document = JsonDocument.Parse(responseJson);
            RecordServerSession(document.RootElement, "client-secret");
            
            // The ephemeral key is at root level as "value"
            if (document.RootElement.TryGetProperty("value", out var rootValue))
            {
                var ephemeralKey = rootValue.GetString();
                
                if (string.IsNullOrEmpty(ephemeralKey))
                {
                    throw new InvalidOperationException("Received empty ephemeral key from OpenAI");
                }
                
                // Log expiry if available
                if (document.RootElement.TryGetProperty("expires_at", out var expiresAt))
                {
                    var expiry = DateTimeOffset.FromUnixTimeSeconds(expiresAt.GetInt64());
                    // Key expires at: {expiry:yyyy-MM-dd HH:mm:ss}
                }
                
                return ephemeralKey;
            }
            
            // Log the actual structure for debugging if parsing fails
            _logAction(LogLevel.Error, $"Unexpected response structure. Root properties: {string.Join(", ", document.RootElement.EnumerateObject().Select(p => p.Name))}");
            
            throw new InvalidOperationException("Failed to extract ephemeral key from client secret response");
        }

        /// <summary>
        /// Connects to the OpenAI real-time API using the specified settings.
        /// </summary>
        /// <param name="settings">OpenAI-specific voice settings.</param>
        /// <returns>A task representing the connection operation.</returns>
        public async Task ConnectAsync(IVoiceSettings settings)
        {
            if (settings is not OpenAiVoiceSettings openAiSettings)
            {
                throw new ArgumentException("Settings must be of type OpenAiVoiceSettings for OpenAI provider", nameof(settings));
            }
                       

            if (openAiSettings.ToolExecutionTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(openAiSettings.ToolExecutionTimeout), "Tool execution timeout must be positive.");
            if (_webSocket != null) await DisconnectAsync();
            ResetSessionDiagnostics();
            _settings = openAiSettings;
            lock (_responseGate)
            {
                _sessionGeneration++;
                _turnGeneration++;
                _completedResponses.Clear();
                _executedCalls.Clear();
                _pendingToolBatches = 0;
                _discardResponseTools = false;
                _toolExecutionUncertain = false;
                _currentResponseId = "";
                _pendingCreateEventId = null;
                _pendingCancelEventId = null;
            }
            _hasActiveResponse = false;
            _responseRequested = false;
            _userSpeaking = false;
            _cancelRequested = false;
            _logAction(LogLevel.Info, $"Settings configured - Voice: {_settings.Voice}, Speed: {_settings.TalkingSpeed}, Model: {_settings.Model}");

            try
            {
                OnStatusChanged?.Invoke("Creating session...");
                // Getting ephemeral key for session
                
                // Get ephemeral key first
                var ephemeralKey = _settings.UseEphemeralKey ? await CreateSessionAsync() : _apiKey;
                
                OnStatusChanged?.Invoke("Connecting to OpenAI...");
                // Establishing WebSocket connection

                _webSocket = new ClientWebSocket();
                _settings.Connection.Apply(_webSocket.Options, ephemeralKey);
                // Beta header removed for production API

                using var connectionCts = new CancellationTokenSource(CONNECTION_TIMEOUT_MS);

                var uri = _settings.Connection.BuildUri($"model={Uri.EscapeDataString(_settings.GetModelId())}", ephemeralKey);
                await _webSocket.ConnectAsync(uri, connectionCts.Token);

                // Start the message receiving task
                _cts = new CancellationTokenSource();
                _receiveTask = ReceiveMessagesAsync(_cts.Token);
                StartAudioSender(_cts.Token);

                OnStatusChanged?.Invoke("Connected to OpenAI");

                // Send session configuration
                await SendSessionConfigurationAsync();
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Failed to connect to OpenAI: {ex.Message}");
                OnError?.Invoke($"Connection failed: {ex.Message}");
                await DisconnectAsync();
                throw;
            }
        }

        /// <summary>
        /// Disconnects from the OpenAI real-time API.
        /// </summary>
        /// <returns>A task representing the disconnection operation.</returns>
        public async Task DisconnectAsync()
        {
            try
            {
                lock (_responseGate) { _sessionGeneration++; _turnGeneration++; _responseRequested = false; }
                OnStatusChanged?.Invoke("Disconnecting...");

                await StopAudioSenderAsync();

                if (_webSocket?.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closeCts = new CancellationTokenSource(DISCONNECTION_TIMEOUT_MS);
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", closeCts.Token);
                }

                _cts?.Cancel();
                if (_receiveTask != null)
                {
                    await _receiveTask;
                }

                OnStatusChanged?.Invoke("Disconnected");
                _logAction(LogLevel.Info, "Disconnected from OpenAI");
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error during disconnection: {ex.Message}");
                OnError?.Invoke($"Disconnection error: {ex.Message}");
            }
            finally
            {
                _cts?.Cancel();

                if (_receiveTask != null)
                {
                    try
                    {
                        await _receiveTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected while shutting down.
                    }
                    catch (WebSocketException)
                    {
                        // The socket can close while a receive is pending.
                    }
                }

                _webSocket?.Dispose();
                _webSocket = null;
                _cts?.Dispose();
                _cts = null;
                _receiveTask = null;
            }
        }

        /// <summary>
        /// Updates the settings for an existing connection and sends the configuration to OpenAI.
        /// </summary>
        /// <param name="settings">The new settings to apply.</param>
        /// <returns>A task representing the update operation.</returns>
        public async Task UpdateSettingsAsync(IVoiceSettings settings)
        {
            if (settings is not OpenAiVoiceSettings openAiSettings)
            {
                throw new ArgumentException("Settings must be of type OpenAiVoiceSettings for OpenAI provider", nameof(settings));
            }

            if (openAiSettings.ToolExecutionTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(openAiSettings.ToolExecutionTimeout), "Tool execution timeout must be positive.");
            _settings = openAiSettings;
            _logAction(LogLevel.Info, $"Settings configured - Voice: {_settings.Voice}, Speed: {_settings.TalkingSpeed}, Model: {_settings.Model}");
            
            if (IsConnected)
            {
                _logAction(LogLevel.Info, "Updating session configuration for existing connection");
                await SendSessionConfigurationAsync();
            }
        }

        /// <summary>
        /// Processes audio data received from the microphone and sends it to OpenAI.
        /// </summary>
        /// <param name="base64Audio">Base64-encoded PCM 16-bit audio data.</param>
        /// <returns>A task representing the audio processing operation.</returns>
        public Task ProcessAudioAsync(string base64Audio)
        {
            if (!IsConnected)
            {
                return Task.CompletedTask;
            }

            if (!TryEnqueueAudio(base64Audio))
            {
                _logAction(LogLevel.Warn, "Audio chunk ignored because the send queue is unavailable");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Sends an interrupt signal to OpenAI to stop current response generation.
        /// </summary>
        /// <returns>A task representing the interrupt operation.</returns>
        public async Task SendInterruptAsync()
        {
            lock (_responseGate)
            {
                _turnGeneration++;
                _pendingToolBatches = 0;
                _discardResponseTools = true;
                _responseRequested = false;
                if (!_hasActiveResponse || _cancelRequested) return;
                _cancelRequested = true;
            }
            if (!IsConnected)
            {
                return;
            }

            try
            {
                _pendingCancelEventId = "cancel_" + Guid.NewGuid().ToString("N");
                await SendMessageAsync("{\"type\":\"response.cancel\",\"event_id\":\"" + _pendingCancelEventId + "\"}");
                _logAction(LogLevel.Info, "Interrupt signal sent to OpenAI");
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
                var messagesToInject = messages
                    .Where(message => message.Role != ChatMessage.ToolRole)
                    .ToList();

                foreach (var message in messagesToInject)
                {
                    var conversationItem = new ConversationItemCreateMessage
                    {
                        Item = new ConversationItem
                        {
                            Type = "message",
                            Role = message.Role == ChatMessage.UserRole ? "user" : "assistant",
                            Content = new List<ContentPart>
                            {
                                new ContentPart
                                {
                                    Type = message.Role == ChatMessage.UserRole ? "input_text" : "output_text",
                                    Text = message.Content
                                }
                            }
                        }
                    };

                    await SendMessageAsync(JsonSerializer.Serialize(conversationItem, OpenAiJsonContext.Default.ConversationItemCreateMessage));
                }
                
                _logAction(LogLevel.Info, $"Successfully injected {messagesToInject.Count} messages into conversation history");
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

            var voiceString = _settings.Voice.ToString().ToLowerInvariant();
            _logAction(LogLevel.Info, $"Configuring session with voice: {_settings.Voice} -> {voiceString}");

            var instructions = OpenAiInstructionsComposer.Compose(_settings);
            var sessionConfig = new SessionUpdateMessage
            {
                EventId = $"evt_{Guid.NewGuid()}",
                Session = new SessionConfig
                {
                    OutputModalities = new List<string> { "audio" },
                    Instructions = instructions.FinalText,
                    MaxOutputTokens = _settings.MaxTokens?.ToString() ?? "inf",
                    Truncation = new TruncationConfig
                    {
                        Type = _settings.AutomaticContextTruncation ? "retention_ratio" : "disabled",
                        RetentionRatio = _settings.AutomaticContextTruncation ? _settings.RetentionRatio : null
                    },
                    ToolChoice = "auto",
                    ParallelToolCalls = _settings.ParallelToolCalls,
                    Tools = _settings.Tools.Select(tool =>
                        (ToolDefinition)_toolTranslator.TranslateToolDefinition(tool, ToolSchemaInferrer.InferSchema(tool.ArgsType))
                    ).ToList(),
                    Reasoning = BuildReasoningConfig(_settings),
                    Audio = new AudioConfig
                    {
                        Input = new AudioInputConfig
                        {
                            NoiseReduction = new NoiseReductionConfig { Type = _settings.NoiseReduction.ToApiString() },
                            Transcription = _settings.InputAudioTranscription.Enabled
                                ? new TranscriptionConfig
                                {
                                    Model = _settings.InputAudioTranscription.GetModelId(),
                                    Prompt = _settings.InputAudioTranscription.Model.SupportsTranscriptionPrompt()
                                        ? _settings.InputAudioTranscription.Prompt ?? _settings.TranscriptionHint
                                        : null,
                                    Language = _settings.MostLikelySpokenLanguage
                                }
                                : null,
                            TurnDetection = BuildTurnDetectionConfig(_settings)
                        },
                        Output = new AudioOutputConfig
                        {
                            Speed = _settings.TalkingSpeed,
                            Voice = voiceString
                        }
                    }
                }
            };

            var jsonMessage = JsonSerializer.Serialize(sessionConfig, OpenAiJsonContextIndented.Default.SessionUpdateMessage);
            var requested = RecordRequestedSession(jsonMessage, "websocket", instructions);
            await SendMessageAsync(jsonMessage);
            RecordSentSession(requested);
            _logAction(LogLevel.Info, "Session configuration sent to OpenAI");
        }

        private static OpenAiReasoningConfig? BuildReasoningConfig(OpenAiVoiceSettings settings)
        {
            if (!settings.Model.SupportsReasoningEffort() || !settings.ReasoningEffort.HasValue)
            {
                return null;
            }

            if (settings.ReasoningEffort.Value == SessionReasoningEffort.None)
            {
                return null;
            }

            return new OpenAiReasoningConfig { Effort = settings.ReasoningEffort.Value.ToApiString() };
        }

        private static TurnDetectionConfig BuildTurnDetectionConfig(OpenAiVoiceSettings settings)
        {
            var semanticVad = IsSemanticVad(settings.TurnDetection.Type);
            return new TurnDetectionConfig
            {
                Type = settings.TurnDetection.Type,
                Eagerness = semanticVad ? settings.Eagerness.ToString() : null,
                Threshold = semanticVad ? null : settings.TurnDetection.Threshold,
                PrefixPaddingMs = semanticVad ? null : settings.TurnDetection.PrefixPaddingMs,
                SilenceDurationMs = semanticVad ? null : settings.TurnDetection.SilenceDurationMs,
                IdleTimeoutMs = semanticVad || settings.ClientResponseControl ? null : settings.TurnDetection.IdleTimeoutMs,
                CreateResponse = !settings.ClientResponseControl && settings.TurnDetection.CreateResponse,
                InterruptResponse = !settings.ClientResponseControl && settings.TurnDetection.InterruptResponse
            };
        }

        private static bool IsSemanticVad(string? type) =>
            string.Equals(type, "semantic_vad", StringComparison.OrdinalIgnoreCase);

        private static string BuildInstructions(OpenAiVoiceSettings settings) =>
            OpenAiInstructionsComposer.Compose(settings).FinalText;

        private static Channel<string> CreateAudioSendChannel()
        {
            return Channel.CreateBounded<string>(new BoundedChannelOptions(AUDIO_SEND_QUEUE_CAPACITY)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        private void StartAudioSender(CancellationToken connectionCancellationToken)
        {
            _droppedAudioChunks = 0;
            _audioSendChannel = CreateAudioSendChannel();
            _audioSendCts = CancellationTokenSource.CreateLinkedTokenSource(connectionCancellationToken);
            _audioSendTask = SendQueuedAudioAsync(_audioSendChannel.Reader, _audioSendCts.Token);
        }

        private bool TryEnqueueAudio(string base64Audio)
        {
            var channel = _audioSendChannel;
            if (channel == null)
            {
                return false;
            }

            var queueWasFull = channel.Reader.CanCount && channel.Reader.Count >= AUDIO_SEND_QUEUE_CAPACITY;
            if (!channel.Writer.TryWrite(base64Audio))
            {
                return false;
            }

            if (queueWasFull)
            {
                var dropped = Interlocked.Increment(ref _droppedAudioChunks);
                if (dropped == 1 || dropped % 50 == 0)
                {
                    _logAction(LogLevel.Warn,
                        $"Audio send queue saturated; dropped {dropped} old chunk(s) to keep latency and memory bounded");
                }
            }

            return true;
        }

        private async Task SendQueuedAudioAsync(ChannelReader<string> reader, CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var audio in reader.ReadAllAsync(cancellationToken))
                {
                    var audioMessage = new AudioBufferAppendMessage { Audio = audio };
                    var json = JsonSerializer.Serialize(
                        audioMessage,
                        OpenAiJsonContext.Default.AudioBufferAppendMessage);
                    await SendMessageAsync(json, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected while disconnecting.
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Audio sender stopped unexpectedly: {ex.Message}");
                OnError?.Invoke($"Audio processing error: {ex.Message}");
            }
        }

        private async Task StopAudioSenderAsync()
        {
            var channel = _audioSendChannel;
            var senderCts = _audioSendCts;
            var senderTask = _audioSendTask;

            _audioSendChannel = null;
            _audioSendCts = null;
            _audioSendTask = null;

            channel?.Writer.TryComplete();
            senderCts?.Cancel();

            if (senderTask != null)
            {
                try
                {
                    await senderTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected while disconnecting.
                }
            }

            senderCts?.Dispose();
        }

        private async Task SendMessageAsync(string message, CancellationToken cancellationToken = default,
            long? expectedGeneration = null, string? createReason = null, long? expectedTurn = null)
        {
            var generation = expectedGeneration ?? _sessionGeneration;
            var socket = _webSocket;
            var token = cancellationToken.CanBeCanceled ? cancellationToken : _cts?.Token ?? CancellationToken.None;
            await _sendLock.WaitAsync(token);
            try
            {
                if (generation != _sessionGeneration || (expectedTurn.HasValue && expectedTurn != _turnGeneration) || socket != _webSocket || socket?.State != WebSocketState.Open)
                    throw new InvalidOperationException("Realtime transport is unavailable or belongs to an expired session.");
                await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)), WebSocketMessageType.Text, true, token);
                ObserveProtocol(message, OpenAiRealtimeProtocolDirection.Sent, createReason);
            }
            finally { _sendLock.Release(); }
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
                // Expected when cancellation is requested
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
                ObserveProtocol(message, OpenAiRealtimeProtocolDirection.Received);

                if (!root.TryGetProperty("type", out var typeElement))
                    return;

                var messageType = typeElement.GetString();

                switch (messageType)
                {
                    case "session.created":
                        _logAction(LogLevel.Info, "Session created by OpenAI");
                        break;
                    case "session.updated":
                        _logAction(LogLevel.Info, "Session updated by OpenAI");
                        break;
                    case "response.created":
                        HandleResponseCreated(root);
                        break;
                    case "conversation.item.created":
                        // Expected message that we don't need to process
                        break;
                    // New API uses response.output_audio instead of response.audio
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
                    case "response.function_call_arguments.done":
                        // Erst response.done beendet die Antwort und enthält sämtliche finalen Toolargumente.
                        break;
                    case "input_audio_buffer.speech_started":
                        _logAction(LogLevel.Info, "User started speaking - server detected interruption");
                        lock (_responseGate)
                        {
                            _userSpeaking = true;
                            _turnGeneration++;
                            _pendingToolBatches = 0;
                            _discardResponseTools = true;
                            _responseRequested = false;
                        }
                        await HandleInterruptionSafeAsync();
                        break;
                    case "input_audio_buffer.speech_stopped":
                        _userSpeaking = false;
                        _logAction(LogLevel.Info, "User stopped speaking");
                        break;
                    case "input_audio_buffer.committed":
                        if (_settings?.ClientResponseControl == true)
                        {
                            lock (_responseGate)
                            {
                                _userSpeaking = false;
                                _responseRequested = true;
                                _responseCreateReason = "input-audio-committed";
                            }
                            await TryStartRequestedResponseAsync();
                        }
                        break;
                    case "conversation.item.input_audio_transcription.completed":
                        HandleInputAudioTranscriptionCompleted(root);
                        break;
                    case "error":
                        HandleError(root);
                        break;
                    // Known message types we don't need to process
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
                        // These are expected messages that we don't need special handling for
                        break;
                    default:
                        _logAction(LogLevel.Info, $"Unhandled message type: {messageType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error processing received message: {ex.Message}");
                OnError?.Invoke($"Realtime protocol processing failed: {ex.Message}");
            }
        }


        private async Task HandleAudioResponse(JsonElement root)
        {
            // Handle audio response - forward to audio hardware via callback
            if (root.TryGetProperty("delta", out var delta))
            {
                var audioData = delta.GetString();
                if (!string.IsNullOrEmpty(audioData))
                {
                    // Don't log every audio chunk - too verbose
                    OnAudioReceived?.Invoke(audioData);
                }
            }

            await Task.CompletedTask;
        }

        private async Task RunToolBatchAsync(JsonElement[] calls, OpenAiRealtimeResponseTrace? trace,
            long session, long turn, CancellationToken cancellationToken)
        {
            var sentResults = false;
            try
            {
                foreach (var call in calls)
                {
                    if (session != _sessionGeneration || turn != _turnGeneration) return;
                    var name = TryGetString(call, "name") ?? "";
                    var callId = TryGetString(call, "call_id") ?? "";
                    var arguments = TryGetString(call, "arguments") ?? "{}";
                    if (name.Length == 0 || callId.Length == 0) throw new InvalidOperationException("Tool call lacks name or call_id.");
                    lock (_responseGate)
                    {
                        // Retain tombstones for the session: evicting them could replay a side effect.
                        if (_executedCalls.Contains(callId)) continue;
                        if (_executedCalls.Count >= 8192) throw new InvalidOperationException("Realtime call limit reached; start a new session.");
                        _executedCalls.Add(callId);
                    }
                    var toolTrace = new OpenAiRealtimeToolCallTrace { Name = name, CallId = callId, ArgumentsJson = arguments };
                    trace?.ToolCalls.Add(toolTrace);
                    var tool = _settings?.Tools.FirstOrDefault(t => t.Name == name);
                    string result;
                    Task<string>? execution = null;
                    try
                    {
                        if (tool == null) result = $"Tool not found: {name}";
                        else
                        {
                            execution = tool.ExecuteAsync(arguments);
                            result = await execution.WaitAsync(_settings!.ToolExecutionTimeout, cancellationToken);
                        }
                    }
                    catch (TimeoutException)
                    {
                        ObserveAbandonedTool(execution, name, callId);
                        if (session != _sessionGeneration || turn != _turnGeneration) return;
                        toolTrace.Error = "Tool execution timed out; action outcome is uncertain. No retry or continuation was performed.";
                        lock (_responseGate)
                        {
                            if (session == _sessionGeneration) { _toolExecutionUncertain = true; _responseRequested = false; }
                        }
                        throw new InvalidOperationException(toolTrace.Error);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    { ObserveAbandonedTool(execution, name, callId); return; }
                    catch (Exception ex) { toolTrace.Error = ex.Message; result = $"Error: {ex.Message}"; }
                    if (session != _sessionGeneration || turn != _turnGeneration) return;
                    toolTrace.OutputJson = result;
                    var payload = JsonSerializer.Serialize(new ConversationItemCreateMessage
                    {
                        Item = new ConversationItem { Type = "function_call_output", CallId = callId, Output = result }
                    }, OpenAiJsonContext.Default.ConversationItemCreateMessage);
                    // A failed send never reruns the tool or sends a replacement result.
                    await SendMessageAsync(payload, cancellationToken, session, expectedTurn: turn);
                    sentResults = true;
                    OnMessageReceived?.Invoke(ChatMessage.CreateToolMessage(name, result, callId));
                }
                lock (_responseGate)
                {
                    if (sentResults && session == _sessionGeneration && turn == _turnGeneration)
                    {
                        if (!_responseRequested) _responseCreateReason = "tool-batch-completed";
                        _responseRequested = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Realtime tool batch failed: {ex.Message}");
                if (session == _sessionGeneration) OnError?.Invoke($"Realtime tool batch failed: {ex.Message}");
            }
            finally
            {
                if (session == _sessionGeneration)
                {
                    lock (_responseGate) { if (turn == _turnGeneration) _pendingToolBatches--; }
                    if (trace != null) OnResponseTraceCompleted?.Invoke(trace);
                    try { await TryStartRequestedResponseAsync(); }
                    catch (Exception ex) { OnError?.Invoke($"Realtime response request failed: {ex.Message}"); }
                }
            }
        }

        private void ObserveAbandonedTool(Task<string>? execution, string name, string callId)
        {
            if (execution == null) return;
            _ = execution.ContinueWith(completed =>
            {
                // Observe every eventual exception without replaying or publishing an obsolete result.
                var error = completed.Exception;
                try { _logAction(LogLevel.Warn, $"Detached tool {name} ({callId}) finished with status {completed.Status}; its result was not submitted. {error?.GetBaseException().Message}"); }
                catch { /* A logging observer must not create another unobserved task fault. */ }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void HandleError(JsonElement root)
        {
            if (root.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var messageElement))
            {
                var errorMessage = messageElement.GetString() ?? "Unknown error";
                lock (_responseGate)
                {
                    if (_pendingCreateEventId != null && TryGetString(error, "event_id") == _pendingCreateEventId)
                    { _pendingCreateEventId = null; _hasActiveResponse = false; }
                }
                
                // Don't report cancellation errors when there's no active response
                if (TryGetString(error, "code") == "response_cancel_not_active" &&
                    TryGetString(error, "event_id") == _pendingCancelEventId && _pendingCancelEventId != null)
                {
                    _logAction(LogLevel.Info, $"Ignoring cancellation error - no active response: {errorMessage}");
                    return;
                }
                
                _logAction(LogLevel.Error, $"OpenAI API error: {errorMessage}");
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
                    _currentResponseAudioTranscript = text;
                    if (_currentResponseTrace != null)
                    {
                        _currentResponseTrace.OutputAudioTranscript = text;
                    }
                    
                    // Send the transcript as an assistant message
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
                    _lastInputTranscript = text;
                    
                    // Send the transcript as a user message
                    var message = ChatMessage.CreateUserMessage(text);
                    OnMessageReceived?.Invoke(message);
                }
            }
        }

        private void HandleTextDelta(JsonElement root)
        {
            if (root.TryGetProperty("delta", out var deltaElem) &&
                TryExtractTextDelta(deltaElem, out var deltaText))
            {
                _currentAiMessage.Append(deltaText);
                _currentResponseOutputText.Append(deltaText);
                if (_currentResponseTrace != null)
                {
                    _currentResponseTrace.OutputText = _currentResponseOutputText.ToString();
                }
            }
        }
        
        private void HandleResponseCreated(JsonElement root)
        {
            if (root.TryGetProperty("response", out var response) && 
                response.TryGetProperty("id", out var idElement))
            {
                var id = idElement.GetString() ?? "";
                if (_completedResponses.Contains(id) || (_hasActiveResponse && _currentResponseId == id)) return;
                if (response.TryGetProperty("conversation_id", out var conversation) && conversation.ValueKind == JsonValueKind.Null) return;
                _pendingCreateEventId = null;
                lock (_responseGate)
                {
                    if (_pendingToolBatches > 0) { _turnGeneration++; _pendingToolBatches = 0; _responseRequested = false; }
                    _discardResponseTools = _userSpeaking;
                }
                _currentResponseId = id;
                _hasActiveResponse = true;
                _currentResponseOutputText.Clear();
                _currentResponseAudioTranscript = null;
                _currentResponseTrace = new OpenAiRealtimeResponseTrace
                {
                    ResponseId = _currentResponseId,
                    InputTranscript = _lastInputTranscript,
                    StartedAt = DateTimeOffset.UtcNow
                };
                _logAction(LogLevel.Info, $"New response started: {_currentResponseId}");
            }
        }
        
        private async Task HandleResponseDone(JsonElement root)
        {
            if (!root.TryGetProperty("response", out var doneResponse)) return;
            var doneId = TryGetString(doneResponse, "id") ?? _currentResponseId;
            if (doneResponse.TryGetProperty("conversation_id", out var conversation) && conversation.ValueKind == JsonValueKind.Null) return;
            lock (_responseGate)
            {
                if (_completedResponses.Contains(doneId)) return;
                if (_completedResponses.Count >= 8192) throw new InvalidOperationException("Realtime response limit reached; start a new session.");
                _completedResponses.Add(doneId);
                if (_currentResponseId.Length != 0 && doneId != _currentResponseId) return;
                _hasActiveResponse = false;
                _cancelRequested = false;
                _pendingCreateEventId = null;
            }
            _logAction(LogLevel.Info, "Response completed");

            UsageReport? report = null;
            OpenAiRealtimeResponseTrace? trace = _currentResponseTrace;

            if (root.TryGetProperty("response", out var response) &&
                response.TryGetProperty("usage", out var usage))
            {
                var responseId = response.TryGetProperty("id", out var idElement)
                    ? idElement.GetString()
                    : _currentResponseId;
                var modelId = response.TryGetProperty("model", out var modelElement)
                    ? modelElement.GetString()
                    : _settings?.GetModelId();

                report = OpenAiRealtimeUsageMapper.CreateUsageReport(
                    usage,
                    modelId,
                    responseId,
                    UsageOperationType.VoiceResponse);

                _logAction(LogLevel.Info, $"Usage: text_in={report.InputTokens}, text_out={report.OutputTokens}, audio_in={report.InputAudioTokens}, audio_out={report.OutputAudioTokens}, cached_in={report.CacheReadInputTokens}");
                OnUsageReceived?.Invoke(report);
            }

            var calls = Array.Empty<JsonElement>();
            if (!_toolExecutionUncertain && !_discardResponseTools && TryGetString(doneResponse, "status") == "completed" &&
                doneResponse.TryGetProperty("output", out var output))
                calls = output.EnumerateArray().Where(item => TryGetString(item, "type") == "function_call").Select(item => item.Clone()).ToArray();
            if (trace != null) PopulateResponseTrace(root, trace, report);
            if (calls.Length > 0)
            {
                var session = _sessionGeneration;
                var turn = _turnGeneration;
                var token = _cts?.Token ?? CancellationToken.None;
                lock (_responseGate) { _pendingToolBatches++; }
                _ = Task.Run(() => RunToolBatchAsync(calls, trace, session, turn, token));
            }
            else if (trace != null) OnResponseTraceCompleted?.Invoke(trace);

            _currentResponseTrace = null;
            _currentResponseOutputText.Clear();
            _currentResponseAudioTranscript = null;

            await TryStartRequestedResponseAsync();
        }

        private async Task TryStartRequestedResponseAsync()
        {
            long session;
            string eventId;
            string createReason;
            long turn;
            lock (_responseGate)
            {
                if (_toolExecutionUncertain || !_responseRequested || _hasActiveResponse || _pendingToolBatches > 0 || _userSpeaking) return;
                _responseRequested = false;
                _hasActiveResponse = true;
                _currentResponseId = "";
                session = _sessionGeneration;
                turn = _turnGeneration;
                createReason = _responseCreateReason;
                eventId = _pendingCreateEventId = "create_" + Guid.NewGuid().ToString("N");
            }
            try
            {
                var json = "{\"type\":\"response.create\",\"event_id\":\"" + eventId + "\"}";
                _logAction(LogLevel.Info, "[Tool] Sending response.create");
                await SendMessageAsync(json, expectedGeneration: session, createReason: createReason, expectedTurn: turn);
            }
            catch
            {
                lock (_responseGate)
                {
                    if (session == _sessionGeneration && _pendingCreateEventId == eventId)
                    { _hasActiveResponse = false; _pendingCreateEventId = null; }
                }
                throw;
            }
        }

        private void PopulateResponseTrace(JsonElement root, OpenAiRealtimeResponseTrace trace, UsageReport? usage)
        {
            trace.CompletedAt = DateTimeOffset.UtcNow;
            trace.Usage = usage;
            trace.OutputText ??= _currentResponseOutputText.Length > 0 ? _currentResponseOutputText.ToString() : null;
            trace.OutputAudioTranscript ??= _currentResponseAudioTranscript;
            trace.InputTranscript ??= _lastInputTranscript;

            if (!root.TryGetProperty("response", out var response))
            {
                return;
            }

            trace.ResponseId ??= TryGetString(response, "id") ?? _currentResponseId;
            trace.Status = TryGetString(response, "status");

            if (response.TryGetProperty("status_details", out var statusDetails) &&
                statusDetails.ValueKind != JsonValueKind.Null &&
                statusDetails.ValueKind != JsonValueKind.Undefined)
            {
                trace.StatusDetailsJson = statusDetails.GetRawText();
            }

            if (response.TryGetProperty("output", out var output) &&
                output.ValueKind == JsonValueKind.Array)
            {
                trace.Outputs.Clear();
                foreach (var item in output.EnumerateArray())
                {
                    var phase = TryGetString(item, "phase");
                    if (!string.IsNullOrWhiteSpace(phase) && !trace.OutputPhases.Contains(phase, StringComparer.Ordinal))
                    {
                        trace.OutputPhases.Add(phase);
                    }

                    CaptureOutputItemText(item, trace, phase);
                }
            }
        }

        private static void CaptureOutputItemText(JsonElement item, OpenAiRealtimeResponseTrace trace, string? phase)
        {
            var outputTrace = new OpenAiRealtimeOutputTrace
            {
                Id = TryGetString(item, "id"),
                Type = TryGetString(item, "type"),
                Phase = phase
            };

            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                trace.Outputs.Add(outputTrace);
                return;
            }

            var textBuilder = new StringBuilder(trace.OutputText ?? string.Empty);
            var audioBuilder = new StringBuilder(trace.OutputAudioTranscript ?? string.Empty);
            var itemTextBuilder = new StringBuilder();
            var itemAudioBuilder = new StringBuilder();

            foreach (var part in content.EnumerateArray())
            {
                var type = TryGetString(part, "type");
                if (type == "output_text")
                {
                    var text = TryGetString(part, "text");
                    if (!string.IsNullOrEmpty(text) && !textBuilder.ToString().Contains(text, StringComparison.Ordinal))
                    {
                        textBuilder.Append(text);
                    }
                    itemTextBuilder.Append(text);
                }
                else if (type == "output_audio")
                {
                    var transcript = TryGetString(part, "transcript");
                    if (!string.IsNullOrEmpty(transcript) && !audioBuilder.ToString().Contains(transcript, StringComparison.Ordinal))
                    {
                        audioBuilder.Append(transcript);
                    }
                    itemAudioBuilder.Append(transcript);
                }
            }

            trace.OutputText = textBuilder.Length > 0 ? textBuilder.ToString() : trace.OutputText;
            trace.OutputAudioTranscript = audioBuilder.Length > 0 ? audioBuilder.ToString() : trace.OutputAudioTranscript;
            outputTrace.Text = itemTextBuilder.Length > 0 ? itemTextBuilder.ToString() : null;
            outputTrace.AudioTranscript = itemAudioBuilder.Length > 0 ? itemAudioBuilder.ToString() : null;
            trace.Outputs.Add(outputTrace);
        }

        private static bool TryExtractTextDelta(JsonElement deltaElem, out string text)
        {
            if (deltaElem.ValueKind == JsonValueKind.String)
            {
                text = deltaElem.GetString() ?? string.Empty;
                return text.Length > 0;
            }

            if (deltaElem.ValueKind == JsonValueKind.Object &&
                deltaElem.TryGetProperty("text", out var textElem))
            {
                text = textElem.GetString() ?? string.Empty;
                return text.Length > 0;
            }

            text = string.Empty;
            return false;
        }

        private static string? TryGetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
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
        
        private async Task HandleInterruptionSafeAsync()
        {
            try
            {
                _logAction(LogLevel.Info, "Speech detected - user interruption");

                // Always clear audio queue when speech is detected (like in the old code)
                OnInterruptDetected?.Invoke();

                // Only cancel if there's an active response
                if (_hasActiveResponse)
                {
                    _logAction(LogLevel.Info, "Interrupting active AI response");
                    await SendInterruptAsync();
                    // Der Slot wird erst durch response.done frei, auch nach response.cancel.
                    // Both modes retain the reservation until response.done.
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

                // Try to parse and format the JSON with indentation
                using var jsonDoc = JsonDocument.Parse(argumentsJson);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    jsonDoc.RootElement.WriteTo(writer);
                }
                var formatted = Encoding.UTF8.GetString(stream.ToArray());

                // If it's a simple single-line JSON, keep it inline
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

                if (_ownsHttpClient)
                {
                    _httpClient.Dispose();
                }

                _sendLock.Dispose();
                _isDisposed = true;
                _logAction(LogLevel.Info, "OpenAI voice provider disposed");
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error during disposal: {ex.Message}");
            }
        }
    }
}
