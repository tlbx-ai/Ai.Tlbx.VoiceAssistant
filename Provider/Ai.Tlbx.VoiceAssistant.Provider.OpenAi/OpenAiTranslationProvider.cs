using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi;

/// <summary>Continuous translation over WebSocket. Preserve silence in the source stream.
/// Disconnect drains translated output through session.closed before releasing the connection.</summary>
public sealed class OpenAiTranslationProvider : IVoiceProvider, ITextOutputProvider
{
    private readonly string _apiKey;
    private readonly Action<LogLevel, string> _log;
    private readonly SemaphoreSlim _send = new(1, 1), _lifecycle = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetime;
    private Task? _receiver;
    private TaskCompletionSource _ready = Completion(), _closed = Completion();
    private OpenAiTranslationSettings? _settings;
    private bool _closing, _disposed;
    private long _inputAudioBytes, _outputAudioBytes;
    public string? SessionId { get; private set; }
    public bool FinalOutputConfirmed { get; private set; }
    public bool IsConnected => !_closing && _ready.Task.IsCompletedSuccessfully && _socket?.State == WebSocketState.Open && !_closed.Task.IsCompleted;
    public AudioSampleRate RequiredInputSampleRate => AudioSampleRate.Rate24000;
    public Action<OpenAiTranslationTranscriptDelta>? OnTranscriptDelta { get; set; }
    public Action<JsonObject>? OnEventReceived { get; set; }
    public Action<string>? OnTextDelta { get; set; }
    public Action<ChatMessage>? OnMessageReceived { get; set; }
    public Action<string>? OnAudioReceived { get; set; }
    public Func<TimeSpan?, Task<bool>>? WaitForPlaybackDrainAsync { get; set; }
    public Action<string>? OnStatusChanged { get; set; }
    public Action<string>? OnError { get; set; }
    public Action? OnInterruptDetected { get; set; }
    public Action<UsageReport>? OnUsageReceived { get; set; }
    public Action<string>? OnTranscriptionDelta { get; set; }
    /// <summary>The protocol has no speech-turn completion; this callback is never synthesized.</summary>
    public Action<string>? OnTranscriptionCompleted { get; set; }

    public OpenAiTranslationProvider(string? apiKey = null, Action<LogLevel, string>? logAction = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        _log = logAction ?? ((_, _) => { });
    }
    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static JsonObject BuildSessionConfiguration(OpenAiTranslationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.TargetLanguage);
        if (settings.Instructions.Length != 0 || settings.Tools.Count != 0 || settings.TalkingSpeed != 1 || settings.ReasoningEffort != null || settings.Thinking.IsConfigured || settings.ToolCallPreambleMode != default)
            throw new ArgumentException("Translation sessions do not accept assistant instructions, tools, speed or reasoning settings.");
        if (settings.ConnectionTimeout <= TimeSpan.Zero || settings.CloseTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Translation timeouts must be positive.");
        if (settings.SourceTranscriptionModelId != null) ArgumentException.ThrowIfNullOrWhiteSpace(settings.SourceTranscriptionModelId);
        return new JsonObject { ["model"] = settings.ModelId, ["audio"] = new JsonObject {
            ["input"] = new JsonObject {
                ["noise_reduction"] = !settings.EnableNoiseReduction ? null : new JsonObject { ["type"] = settings.NoiseReduction.ToApiString() },
                ["transcription"] = settings.SourceTranscriptionModelId == null ? null : new JsonObject { ["model"] = settings.SourceTranscriptionModelId } },
            ["output"] = new JsonObject { ["language"] = settings.TargetLanguage } } };
    }
    private Uri Endpoint(OpenAiTranslationSettings settings, string suffix, bool webSocket)
    {
        var uri = new UriBuilder(settings.Connection.BuildUri(fallbackApiKey: _apiKey));
        uri.Scheme = webSocket ? (uri.Scheme is "https" or "wss" ? "wss" : "ws") : (uri.Scheme is "https" or "wss" ? "https" : "http");
        uri.Path = uri.Path.TrimEnd('/') + suffix;
        return uri.Uri;
    }
    public async Task ConnectAsync(IVoiceSettings settings)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_socket != null) throw new InvalidOperationException("Disconnect the previous translation session.");
            _settings = settings as OpenAiTranslationSettings ?? throw new ArgumentException("Use OpenAiTranslationSettings.");
            var config = BuildSessionConfiguration(_settings);
            _ready = Completion(); _closed = Completion(); _closing = false; FinalOutputConfirmed = false; SessionId = null;
            _inputAudioBytes = _outputAudioBytes = 0;
            _lifetime = new CancellationTokenSource();
            _socket = new ClientWebSocket();
            _settings.Connection.Apply(_socket.Options, _apiKey);
            if (_settings.SafetyIdentifier != null) _socket.Options.SetRequestHeader("OpenAI-Safety-Identifier", _settings.SafetyIdentifier);
            using var timeout = new CancellationTokenSource(_settings.ConnectionTimeout);
            var endpoint = new ProviderEndpointOptions(Endpoint(_settings, "", true).AbsoluteUri);
            await _socket.ConnectAsync(endpoint.BuildUri("model=" + Uri.EscapeDataString(_settings.ModelId)), timeout.Token).ConfigureAwait(false);
            _receiver = ReceiveAsync(_lifetime.Token);
            // The model is selected in the connection URL, not in the update.
            config.Remove("model");
            await SendAsync(new JsonObject { ["type"] = "session.update", ["session"] = config }, timeout.Token).ConfigureAwait(false);
            await _ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            OnStatusChanged?.Invoke("Translation ready");
        }
        catch { await ReleaseAsync().ConfigureAwait(false); throw; }
        finally { _lifecycle.Release(); }
    }
    public Task UpdateSettingsAsync(IVoiceSettings settings) => throw new NotSupportedException("Start a new translation session to change its language or model.");
    public Task InjectConversationHistoryAsync(IEnumerable<ChatMessage> messages) => throw new NotSupportedException("Translation consumes source audio, not conversation history.");
    public Task SendInterruptAsync() { OnInterruptDetected?.Invoke(); return Task.CompletedTask; }
    public async Task ProcessAudioAsync(string base64Audio)
    {
        if (!IsConnected) throw new InvalidOperationException("Translation is not ready.");
        var bytes = Convert.FromBase64String(base64Audio);
        if (bytes.Length % 2 != 0 || bytes.Length > 15 * 1024 * 1024) throw new ArgumentException("Expected bounded mono PCM16 audio.");
        await SendAsync(new JsonObject { ["type"] = "session.input_audio_buffer.append", ["audio"] = base64Audio }, _lifetime!.Token).ConfigureAwait(false);
        Interlocked.Add(ref _inputAudioBytes, bytes.Length);
    }
    private async Task SendAsync(JsonObject message, CancellationToken token)
    {
        await _send.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_closing && message["type"]?.GetValue<string>() != "session.close") throw new InvalidOperationException("Translation session is closing.");
            await _socket!.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message.ToJsonString())), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        }
        finally { _send.Release(); }
    }
    private async Task ReceiveAsync(CancellationToken token)
    {
        var buffer = new byte[32768];
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket!.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType != WebSocketMessageType.Text) throw new IOException("Translation transport closed before session.closed.");
                    stream.Write(buffer, 0, result.Count);
                    if (stream.Length > 16 * 1024 * 1024) throw new IOException("Translation event exceeded 16 MiB.");
                } while (!result.EndOfMessage);
                var message = JsonNode.Parse(stream.ToArray())!.AsObject();
                HandleEvent(message);
                if (FinalOutputConfirmed) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _ready.TrySetException(ex); _closed.TrySetException(ex);
            _log(LogLevel.Error, ex.Message); OnError?.Invoke(ex.Message);
        }
    }
    private void HandleEvent(JsonObject message)
    {
        var type = message["type"]?.GetValue<string>();
        if (type == "session.created") SessionId = message["session"]?["id"]?.GetValue<string>();
        if (type == "session.updated") _ready.TrySetResult();
        if (type == "session.output_audio.delta") {
            var delta = message["delta"]!.GetValue<string>();
            Interlocked.Add(ref _outputAudioBytes, Convert.FromBase64String(delta).Length);
            OnAudioReceived?.Invoke(delta);
        }
        if (type is "session.input_transcript.delta" or "session.output_transcript.delta")
        {
            var delta = message["delta"]!.GetValue<string>();
            var role = type == "session.input_transcript.delta" ? "user" : "assistant";
            OnTranscriptDelta?.Invoke(new(role, delta, role == "assistant" ? _settings!.TargetLanguage : null, message["elapsed_ms"]?.GetValue<double>()));
            if (role == "user") OnTranscriptionDelta?.Invoke(delta); else OnTextDelta?.Invoke(delta);
        }
        if (message["usage"] is JsonObject usage)
        {
            using var doc = JsonDocument.Parse(usage.ToJsonString());
            var report = OpenAiRealtimeUsageMapper.CreateUsageReport(doc.RootElement, _settings!.ModelId, SessionId, UsageOperationType.VoiceSession,
                isCumulative: true, isFinal: type == "session.closed");
            OnUsageReceived?.Invoke(report);
        }
        if (type == "error")
        {
            var exception = new IOException(message["error"]?["message"]?.GetValue<string>() ?? "Translation API error.");
            _ready.TrySetException(exception); _closed.TrySetException(exception); OnError?.Invoke(exception.Message);
        }
        if (type == "session.closed") {
            FinalOutputConfirmed = true;
            if (message["usage"] == null) OnUsageReceived?.Invoke(new UsageReport {
                ProviderId = "openai", ModelId = _settings!.ModelId, OperationId = SessionId,
                OperationType = UsageOperationType.VoiceSession, MeasurementSource = UsageMeasurementSource.ClientMeasured,
                InputAudioDuration = TimeSpan.FromSeconds(Interlocked.Read(ref _inputAudioBytes) / 48000d),
                OutputAudioDuration = TimeSpan.FromSeconds(Interlocked.Read(ref _outputAudioBytes) / 48000d),
                IsEstimated = true, IsCumulative = true, IsFinal = true
            });
            _closed.TrySetResult();
        }
        OnEventReceived?.Invoke((JsonObject)message.DeepClone());
    }
    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_socket == null) return;
            _closing = true;
            using var timeout = new CancellationTokenSource(_settings!.CloseTimeout);
            if (_socket.State == WebSocketState.Open && !_closed.Task.IsCompleted)
            {
                await SendAsync(new JsonObject { ["type"] = "session.close" }, timeout.Token).ConfigureAwait(false);
                await _closed.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            if (!FinalOutputConfirmed) throw new IOException("Translation final output was not confirmed.");
            if (WaitForPlaybackDrainAsync != null && !await WaitForPlaybackDrainAsync(_settings.CloseTimeout).ConfigureAwait(false))
                throw new TimeoutException("Translation playback did not finish draining.");
        }
        finally { await ReleaseAsync().ConfigureAwait(false); _lifecycle.Release(); }
    }
    private async Task ReleaseAsync()
    {
        _lifetime?.Cancel(); _socket?.Abort();
        if (_receiver != null) await _receiver.ConfigureAwait(false);
        _socket?.Dispose(); _lifetime?.Dispose(); _socket = null; _lifetime = null; _receiver = null;
    }
    /// <summary>Trusted SDP exchange. Only the answer and session ID reach the browser; API keys stay server-side.</summary>
    public async Task<OpenAiTranslationWebRtcSession> CreateWebRtcSessionAsync(OpenAiTranslationSettings settings, string sdpOffer,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdpOffer);
        var config = BuildSessionConfiguration(settings);
        using var owned = httpClient == null ? new HttpClient() : null;
        var client = httpClient ?? owned!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.ConnectionTimeout);
        using var secretRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint(settings, "/client_secrets", false));
        settings.Connection.Apply(secretRequest, _apiKey);
        if (settings.SafetyIdentifier != null) secretRequest.Headers.TryAddWithoutValidation("OpenAI-Safety-Identifier", settings.SafetyIdentifier);
        secretRequest.Content = new StringContent(new JsonObject { ["session"] = config }.ToJsonString(), Encoding.UTF8, "application/json");
        using var secretResponse = await client.SendAsync(secretRequest, timeout.Token).ConfigureAwait(false);
        secretResponse.EnsureSuccessStatusCode();
        var secret = JsonNode.Parse(await secretResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false))!;
        using var callRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint(settings, "/calls", false));
        settings.Connection.Apply(callRequest, _apiKey);
        callRequest.Headers.Remove(settings.Connection.AuthenticationHeaderName ?? "Authorization");
        callRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret["value"]!.GetValue<string>());
        callRequest.Content = new StringContent(sdpOffer, Encoding.UTF8, "application/sdp");
        using var callResponse = await client.SendAsync(callRequest, timeout.Token).ConfigureAwait(false);
        callResponse.EnsureSuccessStatusCode();
        var sessionId = secret["session"]?["id"]?.GetValue<string>()
            ?? callResponse.Headers.Location?.Segments[^1];
        if (string.IsNullOrWhiteSpace(sessionId)) throw new IOException("Translation call returned no session ID.");
        return new(sessionId, await callResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await DisconnectAsync().ConfigureAwait(false); }
        finally { _disposed = true; _send.Dispose(); _lifecycle.Dispose(); }
    }
}
