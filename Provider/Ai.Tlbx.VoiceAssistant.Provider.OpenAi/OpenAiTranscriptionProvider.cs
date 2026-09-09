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
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Protocol;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi
{
    public sealed class OpenAiTranscriptionProvider : IVoiceProvider, IStructuredTranscriptionProvider
    {
        private const int CONNECTION_TIMEOUT_MS = 10000;
        private const int AUDIO_BUFFER_SIZE = 32384;
        private const int PCM16_BYTES_PER_SECOND = 24000 * 2;

        private readonly string _apiKey;
        private readonly Action<LogLevel, string> _logAction;
        private ClientWebSocket? _webSocket;
        private Task? _receiveTask;
        private CancellationTokenSource? _cts;
        private bool _isDisposed;
        private OpenAiTranscriptionSettings? _settings;
        private long _pendingInputAudioBytes;

        public bool IsConnected => _webSocket?.State == WebSocketState.Open;
        public AudioSampleRate RequiredInputSampleRate => AudioSampleRate.Rate24000;

        public Action<ChatMessage>? OnMessageReceived { get; set; }
        public Action<string>? OnAudioReceived { get; set; }
        public Func<TimeSpan?, Task<bool>>? WaitForPlaybackDrainAsync { get; set; }
        public Action<string>? OnStatusChanged { get; set; }
        public Action<string>? OnError { get; set; }
        public Action? OnInterruptDetected { get; set; }
        public Action<UsageReport>? OnUsageReceived { get; set; }
        public Action<string>? OnTranscriptionDelta { get; set; }
        public Action<string>? OnTranscriptionCompleted { get; set; }
        public Action<StructuredTranscript>? OnStructuredTranscriptionReceived { get; set; }

        public OpenAiTranscriptionProvider(string? apiKey = null, Action<LogLevel, string>? logAction = null)
        {
            _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                ?? throw new InvalidOperationException("OpenAI API key must be provided or set in OPENAI_API_KEY environment variable");
            _logAction = logAction ?? ((level, message) => { /* no-op */ });
        }

        public async Task ConnectAsync(IVoiceSettings settings)
        {
            if (settings is not OpenAiTranscriptionSettings transcriptionSettings)
            {
                throw new ArgumentException("Settings must be of type OpenAiTranscriptionSettings", nameof(settings));
            }

            ValidateSettings(transcriptionSettings);
            _settings = transcriptionSettings;
            Interlocked.Exchange(ref _pendingInputAudioBytes, 0);
            _logAction(LogLevel.Info, $"Transcription settings configured - Model: {_settings.TranscriptionModel}");

            try
            {
                OnStatusChanged?.Invoke("Connecting to OpenAI Transcription...");

                _webSocket = new ClientWebSocket();
                _settings.Connection.Apply(_webSocket.Options, _apiKey);
                var connectionCts = new CancellationTokenSource(CONNECTION_TIMEOUT_MS);
                var uri = _settings.Connection.BuildUri("intent=transcription", _apiKey);
                await _webSocket.ConnectAsync(uri, connectionCts.Token);
                connectionCts.Dispose();

                OnStatusChanged?.Invoke("Connected to OpenAI Transcription");

                _cts = new CancellationTokenSource();
                _receiveTask = ReceiveMessagesAsync(_cts.Token);

                await SendTranscriptionSessionConfigAsync();
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Failed to connect to OpenAI Transcription: {ex.Message}");
                OnError?.Invoke($"Connection failed: {ex.Message}");
                throw;
            }
        }

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

                FlushMeasuredTranscriptionUsage();
                OnStatusChanged?.Invoke("Disconnected");
                _logAction(LogLevel.Info, "Disconnected from OpenAI Transcription");
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error during disconnection: {ex.Message}");
                OnError?.Invoke($"Disconnection error: {ex.Message}");
            }
        }

        public async Task UpdateSettingsAsync(IVoiceSettings settings)
        {
            if (settings is not OpenAiTranscriptionSettings transcriptionSettings)
            {
                throw new ArgumentException("Settings must be of type OpenAiTranscriptionSettings", nameof(settings));
            }

            ValidateSettings(transcriptionSettings);
            _settings = transcriptionSettings;

            if (IsConnected)
            {
                await SendTranscriptionSessionConfigAsync();
            }
        }

        public async Task ProcessAudioAsync(string base64Audio)
        {
            if (!IsConnected)
                return;

            try
            {
                if (string.IsNullOrWhiteSpace(base64Audio))
                    return;

                var audioByteCount = Convert.FromBase64String(base64Audio).Length;
                var audioMessage = new AudioBufferAppendMessage { Audio = base64Audio };
                var sent = await SendMessageAsync(JsonSerializer.Serialize(audioMessage, OpenAiJsonContext.Default.AudioBufferAppendMessage));
                if (sent)
                {
                    Interlocked.Add(ref _pendingInputAudioBytes, audioByteCount);
                }
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error processing audio: {ex.Message}");
            }
        }

        public Task SendInterruptAsync() => Task.CompletedTask;

        public Task InjectConversationHistoryAsync(IEnumerable<ChatMessage> messages) => Task.CompletedTask;

        private async Task SendTranscriptionSessionConfigAsync()
        {
            if (_settings == null) return;

            var sessionUpdate = new SessionUpdateMessage
            {
                Session = new SessionConfig
                {
                    Type = "transcription",
                    Audio = new AudioConfig
                    {
                        Input = new AudioInputConfig
                        {
                            Format = new AudioInputFormatConfig
                            {
                                Type = "audio/pcm",
                                Rate = 24000
                            },
                            Transcription = new TranscriptionConfig
                            {
                                Model = _settings.GetModelId(),
                                Prompt = _settings.TranscriptionModel.SupportsTranscriptionPrompt()
                                    ? _settings.TranscriptionPrompt
                                    : null,
                                Language = !_settings.TranscriptionModel.SupportsContextLists()
                                    ? _settings.Language
                                    : null,
                                Keywords = _settings.TranscriptionModel.SupportsContextLists() && _settings.Keywords.Count > 0
                                    ? _settings.Keywords
                                    : null,
                                Languages = _settings.TranscriptionModel.SupportsContextLists()
                                    ? BuildLanguageHints(_settings)
                                    : null,
                                Delay = _settings.TranscriptionModel.SupportsDelayControl()
                                    ? _settings.Delay.ToApiString()
                                    : null
                            },
                            TurnDetection = _settings.TranscriptionModel.SupportsRealtimeTurnDetection()
                                ? new TurnDetectionConfig
                                {
                                    Type = "server_vad",
                                    Threshold = _settings.VadThreshold,
                                    PrefixPaddingMs = _settings.PrefixPaddingMs,
                                    SilenceDurationMs = _settings.SilenceDurationMs
                                }
                                : null,
                            NoiseReduction = new NoiseReductionConfig
                            {
                                Type = _settings.NoiseReduction.ToApiString()
                            }
                        }
                    },
                    Include = _settings.IncludeLogProbabilities
                        ? new List<string> { "item.input_audio_transcription.logprobs" }
                        : null
                }
            };

            var json = JsonSerializer.Serialize(sessionUpdate, OpenAiJsonContext.Default.SessionUpdateMessage);
            _logAction(LogLevel.Info, $"Sending transcription session config: {json}");
            await SendMessageAsync(json);
        }

        private static List<string>? BuildLanguageHints(OpenAiTranscriptionSettings settings)
        {
            var languages = settings.Languages
                .Where(language => !string.IsNullOrWhiteSpace(language))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!string.IsNullOrWhiteSpace(settings.Language) &&
                !languages.Contains(settings.Language, StringComparer.OrdinalIgnoreCase))
            {
                languages.Add(settings.Language);
            }

            return languages.Count > 0 ? languages : null;
        }

        private async Task<bool> SendMessageAsync(string message)
        {
            try
            {
                if (_webSocket?.State != WebSocketState.Open)
                    return false;

                var buffer = Encoding.UTF8.GetBytes(message);
                await _webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (WebSocketException ex)
            {
                _logAction(LogLevel.Warn, $"WebSocket send failed: {ex.Message}");
                return false;
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
                        ProcessReceivedMessage(message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error in transcription receive loop: {ex.Message}");
                OnError?.Invoke($"Transcription receive error: {ex.Message}");
            }
            finally
            {
                FlushMeasuredTranscriptionUsage();
            }
        }

        private void ProcessReceivedMessage(string message)
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
                    case "transcription_session.created":
                        _logAction(LogLevel.Info, "Transcription session created");
                        OnStatusChanged?.Invoke("Transcription ready");
                        break;

                    case "session.updated":
                    case "transcription_session.updated":
                        _logAction(LogLevel.Info, "Transcription session updated");
                        break;

                    case "conversation.item.input_audio_transcription.delta":
                        HandleTranscriptionDelta(root);
                        break;

                    case "conversation.item.input_audio_transcription.completed":
                        HandleTranscriptionCompleted(root);
                        break;

                    case "input_audio_buffer.speech_started":
                        _logAction(LogLevel.Info, "Speech started");
                        OnStatusChanged?.Invoke("Listening...");
                        break;

                    case "input_audio_buffer.speech_stopped":
                        _logAction(LogLevel.Info, "Speech stopped");
                        OnStatusChanged?.Invoke("Processing...");
                        break;

                    case "input_audio_buffer.committed":
                        break;

                    case "error":
                        HandleError(root);
                        break;

                    default:
                        _logAction(LogLevel.Info, $"Unhandled transcription event: {messageType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error processing transcription message: {ex.Message}");
            }
        }

        private void HandleTranscriptionDelta(JsonElement root)
        {
            if (root.TryGetProperty("delta", out var delta))
            {
                var text = delta.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    OnTranscriptionDelta?.Invoke(text);
                }
            }
        }

        private void HandleTranscriptionCompleted(JsonElement root)
        {
            FlushMeasuredTranscriptionUsage(root);

            if (root.TryGetProperty("transcript", out var transcript))
            {
                var text = transcript.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    OnTranscriptionCompleted?.Invoke(text);
                    OnStructuredTranscriptionReceived?.Invoke(new StructuredTranscript
                    {
                        ProviderId = "openai",
                        ModelId = _settings?.GetModelId(),
                        Text = text,
                        Language = GetDetectedLanguage(root) ?? _settings?.Language,
                         IsFinal = true,
                         IsSpeechFinal = true,
                         IsSnapshotComplete = true,
                         Segments = new[] { new TranscriptSegment { Text = text } }
                    });
                }
            }
        }

        private static string? GetDetectedLanguage(JsonElement root)
        {
            if (!root.TryGetProperty("languages", out var languages) || languages.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var language in languages.EnumerateArray())
            {
                if (language.TryGetProperty("code", out var code))
                {
                    return code.GetString();
                }
            }

            return null;
        }

        private static void ValidateSettings(OpenAiTranscriptionSettings settings)
        {
            foreach (var keyword in settings.Keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword) || keyword.IndexOfAny(new[] { '<', '>', '\r', '\n' }) >= 0)
                {
                    throw new InvalidOperationException("OpenAI transcription keywords must be non-empty single-line text without angle brackets.");
                }
            }
        }

        private void FlushMeasuredTranscriptionUsage(JsonElement? completedEvent = null)
        {
            var audioBytes = Interlocked.Exchange(ref _pendingInputAudioBytes, 0);
            var duration = audioBytes > 0
                ? TimeSpan.FromSeconds(audioBytes / (double)PCM16_BYTES_PER_SECOND)
                : (TimeSpan?)null;
            var modelId = _settings?.GetModelId();
            var operationId = completedEvent.HasValue &&
                completedEvent.Value.TryGetProperty("item_id", out var itemId)
                    ? itemId.GetString()
                    : null;

            UsageReport report;
            if (completedEvent.HasValue &&
                completedEvent.Value.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                report = OpenAiRealtimeUsageMapper.CreateUsageReport(
                    usage,
                    modelId,
                    operationId,
                    UsageOperationType.Transcription,
                    duration,
                    measurementSource: duration.HasValue
                        ? UsageMeasurementSource.Mixed
                        : UsageMeasurementSource.ProviderReported);
            }
            else
            {
                report = new UsageReport
                {
                    ProviderId = "openai",
                    ModelId = modelId,
                    OperationId = operationId,
                    OperationType = UsageOperationType.Transcription,
                    MeasurementSource = UsageMeasurementSource.ClientMeasured,
                    InputAudioDuration = duration,
                    IsEstimated = true
                };
            }

            if (report.HasUsage)
            {
                OnUsageReceived?.Invoke(report);
            }
        }

        private void HandleError(JsonElement root)
        {
            var errorMessage = "Unknown transcription error";

            if (root.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var msg))
                {
                    errorMessage = msg.GetString() ?? errorMessage;
                }
            }

            _logAction(LogLevel.Error, $"Transcription error: {errorMessage}");
            OnError?.Invoke(errorMessage);
        }

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
                _logAction(LogLevel.Info, "OpenAI transcription provider disposed");
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error during disposal: {ex.Message}");
            }
        }
    }
}
