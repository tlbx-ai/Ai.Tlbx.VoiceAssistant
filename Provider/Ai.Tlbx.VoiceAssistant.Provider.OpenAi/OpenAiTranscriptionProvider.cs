using System;
using System.Collections.Generic;
using System.IO;
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
    public sealed class OpenAiTranscriptionProvider : IVoiceProvider
    {
        private const string REALTIME_WEBSOCKET_ENDPOINT = "wss://api.openai.com/v1/realtime";
        private const int CONNECTION_TIMEOUT_MS = 10000;
        private const int AUDIO_BUFFER_SIZE = 32384;

        private readonly string _apiKey;
        private readonly Action<LogLevel, string> _logAction;
        private ClientWebSocket? _webSocket;
        private Task? _receiveTask;
        private CancellationTokenSource? _cts;
        private bool _isDisposed;
        private OpenAiTranscriptionSettings? _settings;

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

            _settings = transcriptionSettings;
            _logAction(LogLevel.Info, $"Transcription settings configured - Model: {_settings.TranscriptionModel}");

            try
            {
                OnStatusChanged?.Invoke("Connecting to OpenAI Transcription...");

                _webSocket = new ClientWebSocket();
                _webSocket.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
                var connectionCts = new CancellationTokenSource(CONNECTION_TIMEOUT_MS);
                var uri = new Uri($"{REALTIME_WEBSOCKET_ENDPOINT}?intent=transcription");
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
                var audioMessage = new AudioBufferAppendMessage { Audio = base64Audio };
                await SendMessageAsync(JsonSerializer.Serialize(audioMessage, OpenAiJsonContext.Default.AudioBufferAppendMessage));
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

            var sessionUpdate = new TranscriptionSessionUpdateMessage
            {
                Session = new TranscriptionSessionConfig
                {
                    InputAudioFormat = "pcm16",
                    InputAudioTranscription = new TranscriptionConfig
                    {
                        Model = _settings.TranscriptionModel.ToApiString(),
                        Prompt = _settings.TranscriptionPrompt
                    },
                    TurnDetection = new TurnDetectionConfig
                    {
                        Type = "server_vad",
                        Threshold = _settings.VadThreshold,
                        PrefixPaddingMs = _settings.PrefixPaddingMs,
                        SilenceDurationMs = _settings.SilenceDurationMs
                    },
                    InputAudioNoiseReduction = new NoiseReductionConfig
                    {
                        Type = _settings.NoiseReduction.ToApiString()
                    }
                }
            };

            var json = JsonSerializer.Serialize(sessionUpdate, OpenAiJsonContext.Default.TranscriptionSessionUpdateMessage);
            _logAction(LogLevel.Info, $"Sending transcription session config: {json}");
            await SendMessageAsync(json);
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
                    case "transcription_session.created":
                        _logAction(LogLevel.Info, "Transcription session created");
                        OnStatusChanged?.Invoke("Transcription ready");
                        break;

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
            if (root.TryGetProperty("transcript", out var transcript))
            {
                var text = transcript.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    OnTranscriptionCompleted?.Invoke(text);
                }
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
