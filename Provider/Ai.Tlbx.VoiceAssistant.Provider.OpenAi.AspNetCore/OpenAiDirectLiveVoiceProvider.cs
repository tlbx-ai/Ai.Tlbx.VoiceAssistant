using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Microsoft.JSInterop;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;

/// <summary>Blazor Server GPT-Live provider. Browser media goes directly to OpenAI; server controls use a sideband.</summary>
public sealed class OpenAiDirectLiveVoiceProvider : IDirectBrowserVoiceProvider, IStructuredTranscriptionProvider
{
    private readonly IJSRuntime _js;
    private readonly OpenAiDirectLivePreparedSessionStore _prepared;
    private readonly OpenAiDirectLiveOptions _options;
    private readonly OpenAiLiveProvider _live;
    private IJSObjectReference? _module;
    private IJSObjectReference? _client;
    private DotNetObjectReference<OpenAiDirectLiveVoiceProvider>? _reference;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private bool _browserReady;
    private bool _disposed;
    public bool IsConnected => _browserReady && _live.IsConnected;
    public bool FinalUsageConfirmed => _live.FinalUsageConfirmed;
    public string? SessionId => _live.SessionId;
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
    public Action<OpenAiLiveTranscriptDelta>? OnTranscriptDelta { get; set; }
    public Action<OpenAiLiveDelegation>? OnDelegationCreated { get; set; }
    /// <summary>Trusted sideband events, excluding reflected audio. Browser data-channel events do not execute tools.</summary>
    public Action<JsonObject>? OnEventReceived { get; set; }

    public OpenAiDirectLiveVoiceProvider(IJSRuntime jsRuntime, OpenAiDirectLivePreparedSessionStore preparedSessions,
        OpenAiDirectLiveOptions options, string? apiKey = null)
    {
        _js = jsRuntime;
        _prepared = preparedSessions;
        _options = options;
        _live = new OpenAiLiveProvider(apiKey, options.Log);
        _live.OnMessageReceived = m => OnMessageReceived?.Invoke(m);
        _live.OnStatusChanged = s => OnStatusChanged?.Invoke(s);
        _live.OnError = e => { OnError?.Invoke(e); if (!_live.IsConnected) _ = CleanupAfterFailureAsync(); };
        _live.OnUsageReceived = u => OnUsageReceived?.Invoke(u);
        _live.OnTranscriptionDelta = d => OnTranscriptionDelta?.Invoke(d);
        _live.OnStructuredTranscriptionReceived = t => OnStructuredTranscriptionReceived?.Invoke(t);
        _live.OnTranscriptDelta = d => OnTranscriptDelta?.Invoke(d);
        _live.OnDelegationCreated = d => OnDelegationCreated?.Invoke(d);
        _live.OnEventReceived = e => OnEventReceived?.Invoke(e);
    }

    public Task ConnectAsync(IVoiceSettings settings) => StartBrowserSessionAsync(settings);
    public async Task StartBrowserSessionAsync(IVoiceSettings settings, string? microphoneDeviceId = null,
        IEnumerable<ChatMessage>? conversationHistory = null, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        string? token = null;
        CancellationTokenSource? startup = null;
        Task<OpenAiLiveWebRtcSession>? handshake = null;
        var starting = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client != null) throw new InvalidOperationException("Stop the existing browser session first.");
            if (settings is not OpenAiLiveSettings liveSettings) throw new ArgumentException("Use OpenAiLiveSettings.", nameof(settings));
            starting = true;
            _live.SetStartupHistory(conversationHistory ?? Array.Empty<ChatMessage>());
            startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(liveSettings.ConnectionTimeout + TimeSpan.FromSeconds(30));
            var startupToken = startup.Token;
            token = _prepared.Prepare(async (sdp, requestToken) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(startupToken, requestToken);
                handshake = _live.ConnectWebRtcAsync(liveSettings, sdp, _options.HttpClient, linked.Token);
                return await handshake;
            });
            _module ??= await _js.InvokeAsync<IJSObjectReference>("import", startupToken,
                "./_content/Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore/voice-assistant-direct-live.js");
            _reference ??= DotNetObjectReference.Create(this);
            // JSON strings keep this boundary explicit and Native AOT friendly.
            var config = new JsonObject { ["sessionUrl"] = _options.RoutePrefix + "/session", ["preparedSessionId"] = token,
                ["microphoneId"] = microphoneDeviceId, ["timeoutMs"] = liveSettings.ConnectionTimeout.TotalMilliseconds,
                ["closeTimeoutMs"] = liveSettings.CloseTimeout.TotalMilliseconds };
            _client = await _module.InvokeAsync<IJSObjectReference>("createOpenAiDirectLiveClient", startupToken, config.ToJsonString(), _reference);
            await _client.InvokeVoidAsync("start", startupToken);
            _browserReady = true;
            OnStatusChanged?.Invoke("Listening (GPT-Live WebRTC)");
        }
        catch
        {
            startup?.Cancel();
            if (handshake != null) { try { await handshake; } catch { } }
            if (starting) { try { await CleanupAsync(); } catch (Exception ex) { _options.Log?.Invoke(LogLevel.Error, ex.Message); } }
            throw;
        }
        finally
        {
            if (token != null) _prepared.Remove(token);
            startup?.Dispose();
            _lifecycle.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync();
        try { await CleanupAsync(); }
        finally { _lifecycle.Release(); }
    }
    private async Task CleanupAsync()
    {
        _browserReady = false;
        try { await _live.DisconnectAsync(); } // Keep browser media/data alive until the trusted terminal event arrives.
        finally
        {
            if (_client != null)
            {
                try { await _client.InvokeVoidAsync("dispose"); }
                catch (JSDisconnectedException) { }
                finally { try { await _client.DisposeAsync(); } catch (JSDisconnectedException) { } _client = null; }
            }
        }
    }
    public Task UpdateSettingsAsync(IVoiceSettings settings) => _live.UpdateSettingsAsync(settings);
    public Task ProcessAudioAsync(string base64Audio) => throw new NotSupportedException("Browser microphone audio uses WebRTC media tracks.");
    public Task InjectConversationHistoryAsync(IEnumerable<ChatMessage> messages) => throw new NotSupportedException("Supply history when starting the Live browser session.");
    public Task<JsonObject> AppendInstructionsAsync(string content, string? delegationId = null, CancellationToken cancellationToken = default) => _live.AppendInstructionsAsync(content, delegationId, cancellationToken);
    public Task<JsonObject> AppendThinkingAsync(string content, string? delegationId = null, CancellationToken cancellationToken = default) => _live.AppendThinkingAsync(content, delegationId, cancellationToken);
    public Task<JsonObject> AppendCommentaryAsync(string content, string? delegationId = null, CancellationToken cancellationToken = default) => _live.AppendCommentaryAsync(content, delegationId, cancellationToken);
    public Task SendEventAsync(JsonObject command, CancellationToken cancellationToken = default) => _live.SendEventAsync(command, cancellationToken);
    public async Task SetMicrophoneEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (_client == null) throw new InvalidOperationException("No browser session.");
        await _live.SetInputMutedAsync(!enabled, cancellationToken);
        await _client.InvokeVoidAsync("setMicrophoneEnabled", cancellationToken, enabled);
    }
    /// <summary>Explicit local playback mute, independent of microphone muting or backend tasks.</summary>
    public async Task SetPlaybackMutedAsync(bool muted, CancellationToken cancellationToken = default)
    {
        if (_client != null) await _client.InvokeVoidAsync("setPlaybackMuted", cancellationToken, muted);
    }
    public async Task SendInterruptAsync()
    {
        await SetPlaybackMutedAsync(true);
        OnInterruptDetected?.Invoke();
        await _live.SendInterruptAsync();
        // The application explicitly unmutes playback; an instruction acknowledgment does not prove speech stopped.
    }
    [JSInvokable]
    public Task OnLiveBrowserError(string message)
    {
        _browserReady = false;
        OnError?.Invoke(message);
        _ = CleanupAfterFailureAsync();
        return Task.CompletedTask;
    }
    private async Task CleanupAfterFailureAsync()
    {
        try { await DisconnectAsync(); }
        catch (Exception ex) { _options.Log?.Invoke(LogLevel.Error, $"Live cleanup: {ex.Message}"); }
    }
    [JSInvokable]
    public Task OnLiveBrowserClosed()
    {
        _browserReady = false;
        OnStatusChanged?.Invoke("GPT-Live browser media closed");
        _ = CleanupAfterFailureAsync();
        return Task.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await DisconnectAsync(); }
        finally
        {
            await _live.DisposeAsync();
            if (_module != null) { try { await _module.DisposeAsync(); } catch (JSDisconnectedException) { } }
            _reference?.Dispose();
            _disposed = true;
        }
    }
}
