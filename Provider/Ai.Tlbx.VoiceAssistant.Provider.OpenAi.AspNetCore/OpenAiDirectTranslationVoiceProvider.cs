using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Microsoft.JSInterop;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;

/// <summary>Browser translation over direct WebRTC. Source audio and translated playback stay off Blazor.</summary>
public sealed class OpenAiDirectTranslationVoiceProvider : IDirectBrowserVoiceProvider, ITextOutputProvider
{
    private readonly IJSRuntime _js;
    private readonly OpenAiDirectTranslationPreparedSessionStore _store;
    private readonly OpenAiDirectTranslationOptions _options;
    private readonly OpenAiTranslationProvider _wire;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private IJSObjectReference? _module, _client;
    private DotNetObjectReference<OpenAiDirectTranslationVoiceProvider>? _reference;
    private bool _connected, _disposed;
    private string? _language;
    private string? _model;
    public bool IsConnected => _connected;
    public bool FinalOutputConfirmed { get; private set; }
    public string? SessionId { get; private set; }
    public AudioSampleRate RequiredInputSampleRate => AudioSampleRate.Rate24000;
    public Action<OpenAiTranslationTranscriptDelta>? OnTranscriptDelta { get; set; }
    public Action<string>? OnTextDelta { get; set; }
    public Action<ChatMessage>? OnMessageReceived { get; set; }
    public Action<string>? OnAudioReceived { get; set; }
    public Func<TimeSpan?, Task<bool>>? WaitForPlaybackDrainAsync { get; set; }
    public Action<string>? OnStatusChanged { get; set; }
    public Action<string>? OnError { get; set; }
    public Action? OnInterruptDetected { get; set; }
    public Action<UsageReport>? OnUsageReceived { get; set; }
    public Action<string>? OnTranscriptionDelta { get; set; }
    public Action<string>? OnTranscriptionCompleted { get; set; }
    public OpenAiDirectTranslationVoiceProvider(IJSRuntime js, OpenAiDirectTranslationPreparedSessionStore store,
        OpenAiDirectTranslationOptions options, string? apiKey = null)
    { _js = js; _store = store; _options = options; _wire = new(apiKey, options.Log); }
    public Task ConnectAsync(IVoiceSettings settings) => StartBrowserSessionAsync(settings);
    public async Task StartBrowserSessionAsync(IVoiceSettings settings, string? microphoneDeviceId = null,
        IEnumerable<ChatMessage>? conversationHistory = null, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        string? token = null;
        CancellationTokenSource? startup = null;
        Task<OpenAiTranslationWebRtcSession>? handshake = null;
        var starting = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client != null) throw new InvalidOperationException("Stop the previous translation session.");
            if (conversationHistory?.Any() == true) throw new ArgumentException("Translation has no conversation history.");
            var translation = settings as OpenAiTranslationSettings ?? throw new ArgumentException("Use OpenAiTranslationSettings.");
            OpenAiTranslationProvider.BuildSessionConfiguration(translation);
            starting = true;
            _language = translation.TargetLanguage; _model = translation.ModelId; FinalOutputConfirmed = false; SessionId = null;
            startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(translation.ConnectionTimeout + TimeSpan.FromSeconds(30));
            var startupToken = startup.Token;
            token = _store.Prepare(async (sdp, requestToken) => {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(startupToken, requestToken);
                handshake = _wire.CreateWebRtcSessionAsync(translation, sdp, _options.HttpClient, linked.Token);
                return await handshake;
            });
            _module ??= await _js.InvokeAsync<IJSObjectReference>("import", startup.Token,
                "./_content/Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore/voice-assistant-direct-live.js");
            _reference ??= DotNetObjectReference.Create(this);
            var config = new JsonObject { ["sessionUrl"] = _options.RoutePrefix + "/session", ["preparedSessionId"] = token,
                ["microphoneId"] = microphoneDeviceId, ["timeoutMs"] = translation.ConnectionTimeout.TotalMilliseconds,
                ["closeTimeoutMs"] = translation.CloseTimeout.TotalMilliseconds };
            _client = await _module.InvokeAsync<IJSObjectReference>("createOpenAiDirectTranslationClient", startup.Token, config.ToJsonString(), _reference);
            await _client.InvokeVoidAsync("start", startup.Token);
            _connected = true; OnStatusChanged?.Invoke("Translation ready");
        }
        catch
        {
            startup?.Cancel();
            if (handshake != null) { try { await handshake; } catch { } }
            if (starting) await ReleaseBrowserAsync();
            throw;
        }
        finally { if (token != null) _store.Remove(token); startup?.Dispose(); _lifecycle.Release(); }
    }
    [JSInvokable]
    public Task OnTranslationEvent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        var type = e.GetProperty("type").GetString();
        if (type == "session.created" && e.TryGetProperty("session", out var session) && session.TryGetProperty("id", out var id)) SessionId = id.GetString();
        if (type is "session.input_transcript.delta" or "session.output_transcript.delta")
        {
            var delta = e.GetProperty("delta").GetString()!;
            var user = type == "session.input_transcript.delta";
            OnTranscriptDelta?.Invoke(new(user ? "user" : "assistant", delta, user ? null : _language,
                e.TryGetProperty("elapsed_ms", out var elapsed) && elapsed.ValueKind == JsonValueKind.Number ? elapsed.GetDouble() : null));
            if (user) OnTranscriptionDelta?.Invoke(delta); else OnTextDelta?.Invoke(delta);
        }
        if (e.TryGetProperty("usage", out var usage))
            OnUsageReceived?.Invoke(OpenAiRealtimeUsageMapper.CreateUsageReport(usage, _model, SessionId,
                UsageOperationType.VoiceSession, isCumulative: true, isFinal: type == "session.closed"));
        if (type == "session.closed") { _connected = false; FinalOutputConfirmed = true; }
        if (type == "error") { _connected = false; OnError?.Invoke(e.GetProperty("error").GetProperty("message").GetString()!); }
        return Task.CompletedTask;
    }
    [JSInvokable] public Task OnLiveBrowserError(string message) { _connected = false; OnError?.Invoke(message); return Task.CompletedTask; }
    [JSInvokable] public Task OnLiveBrowserClosed() { _connected = false; return Task.CompletedTask; }
    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync();
        try {
            if (_client != null) {
                await _client.InvokeVoidAsync("stopTranslation");
                if (!FinalOutputConfirmed) throw new IOException("Translation final output was not confirmed.");
            }
        }
        finally { await ReleaseBrowserAsync(); _lifecycle.Release(); }
    }
    private async Task ReleaseBrowserAsync()
    {
        _connected = false;
        if (_client == null) return;
        try { await _client.InvokeVoidAsync("dispose"); await _client.DisposeAsync(); }
        catch (JSDisconnectedException) { }
        finally { _client = null; }
    }
    public Task SetMicrophoneEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        _client == null ? Task.CompletedTask : _client.InvokeVoidAsync("setMicrophoneEnabled", cancellationToken, enabled).AsTask();
    public Task SetPlaybackMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
        _client == null ? Task.CompletedTask : _client.InvokeVoidAsync("setPlaybackMuted", cancellationToken, muted).AsTask();
    public Task UpdateSettingsAsync(IVoiceSettings settings) => throw new NotSupportedException("Start a new translation session.");
    public Task ProcessAudioAsync(string base64Audio) => throw new NotSupportedException("Browser translation audio uses WebRTC tracks.");
    public Task InjectConversationHistoryAsync(IEnumerable<ChatMessage> messages) => throw new NotSupportedException("Translation has no conversation history.");
    public Task SendInterruptAsync() => SetPlaybackMutedAsync(true);
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await DisconnectAsync(); }
        finally { _disposed = true; if (_module != null) { try { await _module.DisposeAsync(); } catch (JSDisconnectedException) { } } _reference?.Dispose(); await _wire.DisposeAsync(); _lifecycle.Dispose(); }
    }
}
