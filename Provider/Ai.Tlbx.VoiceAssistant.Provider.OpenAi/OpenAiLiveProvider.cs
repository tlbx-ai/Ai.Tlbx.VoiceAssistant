using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Protocol;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Translation;
using Ai.Tlbx.VoiceAssistant.Reflection;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi;

/// <summary>Continuous GPT-Live audio over a server-side WebSocket with client or Responses delegation.
/// Callback handlers must return promptly; run application-owned backend work outside the receive loop.</summary>
public sealed class OpenAiLiveProvider : IVoiceProvider, IStartupHistoryVoiceProvider, IStructuredTranscriptionProvider
{
    private readonly string _apiKey;
    private readonly Action<LogLevel, string> _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetime;
    private Task? _receiver;
    private Channel<string>? _audio;
    private CancellationTokenSource? _audioLifetime;
    private Task? _audioSender;
    private TaskCompletionSource _started = NewCompletion();
    private TaskCompletionSource _closed = NewCompletion();
    private readonly Dictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly Dictionary<string, List<JsonObject>> _calls = new();
    private readonly Dictionary<string, string> _responseIds = new();
    private readonly object _backendLock = new();
    private readonly SemaphoreSlim _toolLock = new(1, 1);
    private readonly SemaphoreSlim _disconnectLock = new(1, 1);
    private readonly Dictionary<string, OpenAiLiveToolResult> _toolResults = new();
    private readonly Dictionary<string, string?> _backendEvents = new();
    private int _backendInputItems;
    private int _backendInputBytes;
    private string? _backendFailure;
    private Task? _failureCleanup;
    private readonly List<Task> _backendTasks = new();
    private List<ChatMessage> _history = new();
    private OpenAiLiveSettings? _settings;
    private JsonObject? _startup;
    private bool _closing;
    private bool _disposed;
    private bool _sideband;
    public bool IsConnected => (_sideband || _audioSender != null) && _started.Task.IsCompletedSuccessfully && !_closing && !_closed.Task.IsCompleted && _socket?.State == WebSocketState.Open;
    public AudioSampleRate RequiredInputSampleRate => AudioSampleRate.Rate24000;
    public string? SessionId { get; private set; }
    public bool FinalUsageConfirmed { get; private set; }
    public string? CloseReason { get; private set; }
    /// <summary>First terminal backend continuation failure. No automatic retry or tool replay is performed.</summary>
    public string? BackendContinuationError { get { lock (_backendLock) return _backendFailure; } }
    /// <summary>Immutable snapshots retained until the next connection. SentUnconfirmed is not an API acknowledgment.
    /// Preserve results in application storage before reconnecting if recovery is needed.</summary>
    public IReadOnlyList<OpenAiLiveToolResult> ToolResults { get { lock (_backendLock) return _toolResults.Values.ToArray(); } }
    public Action<ChatMessage>? OnMessageReceived { get; set; }
    public Action<string>? OnAudioReceived { get; set; }
    public Func<TimeSpan?, Task<bool>>? WaitForPlaybackDrainAsync { get; set; }
    public Action<string>? OnStatusChanged { get; set; }
    public Action<string>? OnError { get; set; }
    public Action? OnInterruptDetected { get; set; }
    public Action<UsageReport>? OnUsageReceived { get; set; }
    public Action<string>? OnTranscriptionDelta { get; set; }
    /// <summary>Live has no turn-completed event. This callback is never synthesized.</summary>
    public Action<string>? OnTranscriptionCompleted { get; set; }
    public Action<OpenAiLiveTranscriptDelta>? OnTranscriptDelta { get; set; }
    public Action<StructuredTranscript>? OnStructuredTranscriptionReceived { get; set; }
    public Action<OpenAiLiveDelegation>? OnDelegationCreated { get; set; }
    /// <summary>All original Live events, including acknowledgments and nested Responses events.</summary>
    public Action<JsonObject>? OnEventReceived { get; set; }

    public OpenAiLiveProvider(string? apiKey = null, Action<LogLevel, string>? logAction = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        _log = logAction ?? ((_, _) => { });
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void SetStartupHistory(IEnumerable<ChatMessage> messages)
    {
        if (_socket != null) throw new InvalidOperationException("Set history before connecting.");
        _history = messages.ToList();
    }

    public async Task ConnectAsync(IVoiceSettings settings)
    {
        Initialize(settings, false);
        try
        {
            using var timeout = new CancellationTokenSource(_settings!.ConnectionTimeout);
            await _socket!.ConnectAsync(_settings.Connection.BuildUri(fallbackApiKey: _apiKey), timeout.Token).ConfigureAwait(false);
            _receiver = ReceiveAsync(_socket, _lifetime!.Token);
            await SendCoreAsync(new JsonObject { ["type"] = "session.start", ["session"] = _startup!.DeepClone() }, timeout.Token).ConfigureAwait(false);
            await _started.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            _audio = Channel.CreateBounded<string>(new BoundedChannelOptions(100) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            _audioLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _audioSender = SendAudioAsync(_audio.Reader, _audioLifetime.Token);
            OnStatusChanged?.Invoke("Connected to GPT-Live");
        }
        catch
        {
            await ReleaseTransportAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void Initialize(IVoiceSettings settings, bool sideband)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_socket != null) throw new InvalidOperationException("Disconnect the existing session first.");
        if (_failureCleanup is { IsCompleted: false }) throw new InvalidOperationException("Previous backend failure cleanup is still running.");
        lock (_backendTasks)
        {
            _backendTasks.RemoveAll(t => t.IsCompleted);
            if (_backendTasks.Count != 0) throw new InvalidOperationException("Previous local tool execution is still running. Wait for it to finish before reusing this provider.");
        }
        _settings = settings as OpenAiLiveSettings ?? throw new ArgumentException("GPT-Live requires OpenAiLiveSettings.", nameof(settings));
        _startup = BuildSession(_settings, _history);
        _sideband = sideband;
        if (sideband) _startup["audio"]!.AsObject().Remove("format");
        _started = NewCompletion();
        _closed = NewCompletion();
        _closing = false;
        FinalUsageConfirmed = false;
        CloseReason = null;
        SessionId = null;
        _calls.Clear();
        _responseIds.Clear();
        lock (_backendLock)
        {
            _toolResults.Clear();
            _backendEvents.Clear();
            _backendInputItems = _backendInputBytes = 0;
            _backendFailure = null;
        }
        _lifetime = new CancellationTokenSource();
        _socket = new ClientWebSocket();
        _settings.Connection.Apply(_socket.Options, _apiKey);
    }

    /// <summary>Exchanges a browser SDP offer for a Live WebRTC session and attaches server-side controls.
    /// Apply the returned SDP in the browser and wait for session.started on its data channel.
    /// Audio travels over WebRTC; the attached socket owns tools, transcripts, commands, and usage.</summary>
    public async Task<OpenAiLiveWebRtcSession> ConnectWebRtcAsync(OpenAiLiveSettings settings, string sdpOffer,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdpOffer);
        Initialize(settings, true);
        using var ownedClient = httpClient == null ? new HttpClient() : null;
        var client = httpClient ?? ownedClient!;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.ConnectionTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, LiveUri(settings, "", false));
            request.Content = new StringContent(new JsonObject { ["session"] = _startup!.DeepClone(),
                ["transport"] = new JsonObject { ["type"] = "webrtc", ["sdp"] = sdpOffer } }.ToJsonString(), Encoding.UTF8, "application/json");
            settings.Connection.Apply(request, _apiKey);
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false))!;
            SessionId = result["session"]?["id"]?.GetValue<string>() ?? throw new IOException("Live creation response omitted session.id.");
            var answer = result["transport"]?["sdp"]?.GetValue<string>() ?? throw new IOException("Live creation response omitted transport.sdp.");
            await _socket!.ConnectAsync(LiveUri(settings, $"/{Uri.EscapeDataString(SessionId)}/attach", true), timeout.Token).ConfigureAwait(false);
            _receiver = ReceiveAsync(_socket, _lifetime!.Token);
            // Attaching neither replays session.started nor requires a first command.
            _started.TrySetResult();
            return new OpenAiLiveWebRtcSession(SessionId, answer);
        }
        catch
        {
            if (SessionId != null)
            {
                try
                {
                    using var cleanupTimeout = new CancellationTokenSource(settings.CloseTimeout);
                    using var hangup = new HttpRequestMessage(HttpMethod.Post, LiveUri(settings, $"/{Uri.EscapeDataString(SessionId)}/hangup", false));
                    settings.Connection.Apply(hangup, _apiKey);
                    using var response = await client.SendAsync(hangup, cleanupTimeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex) { _log(LogLevel.Error, $"Failed to hang up an incomplete Live WebRTC session: {ex.Message}"); }
            }
            await ReleaseTransportAsync().ConfigureAwait(false);
            throw;
        }
    }

    private Uri LiveUri(OpenAiLiveSettings settings, string suffix, bool websocket)
    {
        var uri = new UriBuilder(settings.Connection.BuildUri(fallbackApiKey: _apiKey));
        uri.Scheme = websocket ? (uri.Scheme is "https" or "wss" ? "wss" : "ws") : (uri.Scheme is "https" or "wss" ? "https" : "http");
        uri.Path = uri.Path.TrimEnd('/') + suffix;
        return uri.Uri;
    }

    internal static JsonObject BuildSession(OpenAiLiveSettings settings, IEnumerable<ChatMessage> history)
    {
        if (string.IsNullOrWhiteSpace(settings.ModelId) || string.IsNullOrWhiteSpace(settings.Voice))
            throw new ArgumentException("ModelId and Voice are required.");
        if (settings.TalkingSpeed != 1 || settings.NoiseReduction != default || settings.ReasoningEffort != null || settings.Thinking.IsConfigured || settings.ToolCallPreambleMode != default)
            throw new ArgumentException("Live speech has no speed, reasoning, thinking, or tool-preamble control. Configure the Responses backend separately.");
        if (settings.Responses == null && settings.Tools.Count != 0)
            throw new ArgumentException("Local tools require Responses delegation. Client delegation runs its own backend.");
        if (settings.ConnectionTimeout <= TimeSpan.Zero || settings.CloseTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Connection and close timeouts must be positive.");
        var input = new JsonArray();
        foreach (var message in settings.InitialHistory.Concat(history))
        {
            if (message.Role is not ("user" or "assistant" or "developer"))
                throw new ArgumentException("Live startup history supports only user, assistant, and developer text messages.");
            input.Add((JsonNode)new JsonObject { ["type"] = "message", ["role"] = message.Role,
                ["content"] = new JsonArray(new JsonObject { ["type"] = message.Role == "assistant" ? "output_text" : "input_text", ["text"] = message.Content }) });
        }
        if (input.Count > 128) throw new ArgumentException("Live startup history accepts at most 128 messages.");
        return new JsonObject
        {
            ["model"] = settings.ModelId, ["instructions"] = settings.Instructions,
            ["audio"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24000 }, ["output"] = new JsonObject { ["voice"] = settings.Voice } },
            ["store"] = settings.Store, ["input"] = input, ["delegation"] = BuildDelegation(settings)
        };
    }

    private static JsonObject BuildDelegation(OpenAiLiveSettings settings)
    {
        if (settings.Responses == null) return new JsonObject { ["type"] = "client" };
        var responses = (JsonObject)settings.Responses.DeepClone();
        if (string.IsNullOrWhiteSpace(responses["model"]?.GetValue<string>())) throw new ArgumentException("Responses delegation requires a backend model.");
        var tools = responses["tools"] as JsonArray ?? new JsonArray();
        if (responses["tools"] == null) responses["tools"] = tools;
        var translator = new OpenAiToolTranslator();
        foreach (var tool in settings.Tools)
        {
            if (tools.Any(t => t?["name"]?.GetValue<string>() == tool.Name)) throw new ArgumentException($"Duplicate Live tool: {tool.Name}");
            var definition = (ToolDefinition)translator.TranslateToolDefinition(tool, ToolSchemaInferrer.InferSchema(tool.ArgsType));
            tools.Add(JsonNode.Parse(JsonSerializer.Serialize(definition, OpenAiJsonContext.Default.ToolDefinition)));
        }
        return new JsonObject { ["type"] = "responses", ["responses"] = responses };
    }

    public async Task UpdateSettingsAsync(IVoiceSettings settings)
    {
        RequireConnected();
        var live = settings as OpenAiLiveSettings ?? throw new ArgumentException("GPT-Live requires OpenAiLiveSettings.");
        var updated = BuildSession(live, _history);
        if (_sideband) updated["audio"]!.AsObject().Remove("format");
        foreach (var field in new[] { "model", "instructions", "audio", "store", "input" })
            if (!JsonNode.DeepEquals(updated[field], _startup![field])) throw new InvalidOperationException($"Live {field} is immutable; start a new session or append context.");
        if (updated["delegation"]!["type"]!.GetValue<string>() != _startup!["delegation"]!["type"]!.GetValue<string>())
            throw new InvalidOperationException("Live delegation mode cannot change during a session.");
        if (live.Responses != null)
            await SendCommandAsync(new JsonObject { ["type"] = "session.update", ["session"] = new JsonObject { ["delegation"] = updated["delegation"]!.DeepClone() } }).ConfigureAwait(false);
        _settings = live;
    }

    public Task ProcessAudioAsync(string base64Audio)
    {
        RequireConnected();
        if (_sideband) throw new NotSupportedException("Send microphone audio on the primary WebRTC media track, not the sideband.");
        var bytes = Convert.FromBase64String(base64Audio);
        if (bytes.Length % 2 != 0) throw new ArgumentException("PCM16 audio must contain complete two-byte samples.", nameof(base64Audio));
        if (_audio?.Writer.TryWrite(base64Audio) != true)
        {
            // A continuous Live stream must not silently lose or reorder microphone samples.
            _socket?.Abort();
            throw new InvalidOperationException("Live audio queue exceeded 100 chunks; connection aborted because capture outpaced the transport.");
        }
        return Task.CompletedTask;
    }

    private async Task SendAudioAsync(ChannelReader<string> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var audio in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                await SendCoreAsync(new JsonObject { ["type"] = "session.input_audio.append", ["audio"] = audio }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closing) { _socket?.Abort(); OnError?.Invoke($"Live audio transport failed: {ex.Message}"); }
        }
    }

    /// <summary>Clears local playback and requests silence through instructions. Live has no deterministic speech-cancel command.
    /// This does not cancel backend tasks.</summary>
    public Task SendInterruptAsync()
    {
        OnInterruptDetected?.Invoke();
        return AppendInstructionsAsync("Stop speaking now and listen to the user.");
    }
    public Task InjectConversationHistoryAsync(IEnumerable<ChatMessage> messages) =>
        throw new NotSupportedException("Live history belongs in InitialHistory or SetStartupHistory before ConnectAsync. Use AppendThinkingAsync for concise ongoing factual context.");

    public Task<JsonObject> AppendInstructionsAsync(string content, string? delegationId = null, CancellationToken cancellationToken = default) => AppendAsync("instructions", content, delegationId, cancellationToken);
    public Task<JsonObject> AppendThinkingAsync(string content, string? delegationId = null, CancellationToken cancellationToken = default) => AppendAsync("thinking", content, delegationId, cancellationToken);
    public Task<JsonObject> AppendCommentaryAsync(string content, string? delegationId = null, CancellationToken cancellationToken = default) => AppendAsync("commentary", content, delegationId, cancellationToken);
    private Task<JsonObject> AppendAsync(string kind, string content, string? delegationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        return SendCommandAsync(new JsonObject { ["type"] = $"session.{kind}.append", ["content"] = content, ["delegation_id"] = delegationId }, cancellationToken);
    }
    public Task<JsonObject> SetInputMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
        SendCommandAsync(new JsonObject { ["type"] = muted ? "session.input_audio.mute" : "session.input_audio.unmute" }, cancellationToken);

    /// <summary>Sends a Live command and awaits its correlated acknowledgment. Appends are limited to 500 tokens by the API;
    /// acknowledgment requires audio frame progress and does not confirm playback.</summary>
    private async Task<JsonObject> SendCommandAsync(JsonObject command, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        var id = Guid.NewGuid().ToString("N");
        command["event_id"] = id;
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending.Add(id, completion);
        try
        {
            await SendCoreAsync(command, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(_settings!.ConnectionTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally { lock (_pending) _pending.Remove(id); }
    }

    /// <summary>Sends an advanced Live event (for example response.item.create or response.create).
    /// Observe OnEventReceived/OnError: these commands may have no standalone acknowledgment.</summary>
    public Task SendEventAsync(JsonObject command, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        ArgumentNullException.ThrowIfNull(command);
        var type = command["type"]?.GetValue<string>();
        if (_sideband && type == "session.input_audio.append") throw new ArgumentException("Sideband connections do not accept microphone audio.");
        if (type is "session.start" or "session.close") throw new ArgumentException("Use ConnectAsync/DisconnectAsync for lifecycle commands.");
        return SendCoreAsync((JsonObject)command.DeepClone(), cancellationToken);
    }

    private void RequireConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConnected) throw new InvalidOperationException("Live session is not ready or is closing.");
    }
    private async Task SendCoreAsync(JsonObject command, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closing && command["type"]?.GetValue<string>() != "session.close")
                throw new InvalidOperationException("Live session is closing.");
            var bytes = Encoding.UTF8.GetBytes(command.ToJsonString());
            var type = command["type"]?.GetValue<string>();
            if (type is "response.item.create" or "response.create")
            {
                var eventId = command["event_id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                command["event_id"] = eventId;
                bytes = Encoding.UTF8.GetBytes(command.ToJsonString());
                lock (_backendLock)
                {
                    if (_backendFailure != null) throw new InvalidOperationException(_backendFailure);
                    if (_backendEvents.ContainsKey(eventId)) throw new ArgumentException("Backend event_id must be unique within the session.");
                    var callId = command["item"]?["call_id"]?.GetValue<string>();
                    _backendEvents.Add(eventId, callId);
                    if (callId != null && _toolResults.TryGetValue(callId, out var result))
                        _toolResults[callId] = result with { EventId = eventId };
                    // Count the entire serialized event, including escaped Unicode and envelope overhead.
                    // This deliberately exceeds the observed server item cost. Never refund or reset on continuation:
                    // there is no item ACK, and the input history limit is per session.
                    if (type == "response.item.create")
                    {
                        if (_backendInputItems >= 128 || bytes.Length > 32768 - _backendInputBytes)
                        {
                            if (callId != null && _toolResults.TryGetValue(callId, out var blocked))
                                _toolResults[callId] = blocked with { SubmissionState = OpenAiLiveToolSubmissionState.BudgetExceeded };
                            throw new InvalidOperationException($"Live backend input budget exhausted before sending event {eventId} (call {callId}): reserved {_backendInputItems} items / {_backendInputBytes} UTF-8 bytes, next event {bytes.Length} bytes; limits 128 / 32768. Result retained without truncation; start a new session only after reconciling executed actions.");
                        }
                        _backendInputItems++;
                        _backendInputBytes += bytes.Length;
                    }
                }
            }
            await (_socket ?? throw new InvalidOperationException("Live transport is closed.")).SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (command["type"]?.GetValue<string>() is "response.item.create" or "response.create")
        {
            lock (_backendLock)
                if (command["item"]?["call_id"]?.GetValue<string>() is string callId && _toolResults.TryGetValue(callId, out var result)
                    && result.SubmissionState == OpenAiLiveToolSubmissionState.NotSent)
                    _toolResults[callId] = result with { SubmissionState = OpenAiLiveToolSubmissionState.TransportUncertain, Error = ex.Message };
            FailBackend($"Live backend submission failed: {ex.Message}");
            throw;
        }
        finally { _sendLock.Release(); }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[32768];
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_closed.Task.IsCompleted)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Live transport closed before session.closed; final usage is unconfirmed.");
                    if (result.MessageType != WebSocketMessageType.Text) throw new IOException("Expected a Live JSON text event.");
                    stream.Write(buffer, 0, result.Count);
                    if (stream.Length > 16 * 1024 * 1024) throw new IOException("Live event exceeded 16 MiB.");
                } while (!result.EndOfMessage);
                var message = JsonNode.Parse(stream.ToArray()) as JsonObject ?? throw new IOException("Invalid Live event.");
                HandleEvent(message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            failure = ex;
            _log(LogLevel.Error, ex.Message);
            OnError?.Invoke(ex.Message);
        }
        finally
        {
            var error = failure ?? new IOException("Live session ended.");
            _started.TrySetException(error);
            if (!_closed.Task.IsCompleted) _closed.TrySetException(error);
            lock (_pending) foreach (var pending in _pending.Values) pending.TrySetException(error);
        }
    }

    internal void HandleEvent(JsonObject message)
    {
        var type = message["type"]?.GetValue<string>();
        // Reflected sideband audio is not played or forwarded through the application/Blazor circuit.
        if (_sideband && type is "session.input_audio.append" or "session.output_audio.delta") return;
        var clientId = message["client_event_id"]?.GetValue<string>() ?? message["error"]?["client_event_id"]?.GetValue<string>();
        if (clientId != null)
        {
            lock (_pending)
                if (_pending.TryGetValue(clientId, out var pending))
                {
                    if (type == "error") pending.TrySetException(new InvalidOperationException(message["error"]?.ToJsonString()));
                    else pending.TrySetResult((JsonObject)message.DeepClone());
                }
        }
        switch (type)
        {
            case "session.started":
                SessionId = message["session"]?["id"]?.GetValue<string>();
                _started.TrySetResult();
                break;
            case "session.output_audio.delta": OnAudioReceived?.Invoke(message["delta"]!.GetValue<string>()); break;
            case "session.input_transcript.delta":
            case "session.output_transcript.delta":
                var role = type == "session.input_transcript.delta" ? "user" : "assistant";
                var delta = message["delta"]!.GetValue<string>();
                var transcript = new OpenAiLiveTranscriptDelta(role, delta, message["start_ms"]!.GetValue<double>(), message["end_ms"]!.GetValue<double>());
                OnTranscriptDelta?.Invoke(transcript);
                OnStructuredTranscriptionReceived?.Invoke(new StructuredTranscript {
                    ProviderId = "openai", ModelId = _settings?.ModelId ?? "gpt-live-1", Text = delta,
                    AudioStart = TimeSpan.FromMilliseconds(transcript.StartMs), AudioEnd = TimeSpan.FromMilliseconds(transcript.EndMs),
                    Segments = new[] { new TranscriptSegment { Text = delta, Speaker = role,
                        Start = TimeSpan.FromMilliseconds(transcript.StartMs), End = TimeSpan.FromMilliseconds(transcript.EndMs) } }
                });
                if (role == "user") OnTranscriptionDelta?.Invoke(delta);
                // A ChatMessage callback would falsely present a fragment as a complete turn.
                break;
            case "session.delegation.created":
                var delegation = message["delegation"]!;
                OnDelegationCreated?.Invoke(new OpenAiLiveDelegation(delegation["id"]!.GetValue<string>(), delegation["target"]!.GetValue<string>(),
                    message["offset_ms"]!.GetValue<double>(), delegation["response_id"]?.GetValue<string>()));
                break;
            case "session.usage.updated": EmitVoiceUsage(message, false); break;
            case "session.closed":
                CloseReason = message["reason"]?.GetValue<string>();
                FinalUsageConfirmed = message["usage"]?["seconds"] != null;
                EmitVoiceUsage(message, true);
                _closed.TrySetResult();
                OnStatusChanged?.Invoke($"GPT-Live closed: {CloseReason}");
                break;
            case "response.event": HandleResponseEvent(message); break;
            case "error":
                var error = message["error"]?.ToJsonString() ?? "Live API error";
                _started.TrySetException(new InvalidOperationException(error));
                var code = message["error"]?["code"]?.GetValue<string>();
                bool backendError;
                lock (_backendLock)
                {
                    backendError = clientId != null && _backendEvents.ContainsKey(clientId);
                    if (backendError && _backendEvents[clientId!] is string failedCall && _toolResults.TryGetValue(failedCall, out var failedResult))
                        _toolResults[failedCall] = failedResult with { SubmissionState = OpenAiLiveToolSubmissionState.Rejected, Error = error };
                }
                if (backendError || code is "response_input_buffer_full" or "function_call_outputs_required")
                    FailBackend($"Live backend rejected event {clientId ?? "(uncorrelated)"}: {error}");
                else OnError?.Invoke(error);
                break;
        }
        OnEventReceived?.Invoke((JsonObject)message.DeepClone());
    }

    private void EmitVoiceUsage(JsonObject message, bool final)
    {
        if (message["usage"] is not JsonObject usage || usage["seconds"] == null) return;
        OnUsageReceived?.Invoke(new UsageReport { ProviderId = "openai", ModelId = _settings?.ModelId ?? "gpt-live-1", OperationId = SessionId,
            OperationType = UsageOperationType.VoiceSession, SessionDuration = TimeSpan.FromSeconds(usage["seconds"]!.GetValue<double>()),
            IsCumulative = true, IsFinal = final, RawProviderUsageJson = usage.ToJsonString() });
    }

    private void HandleResponseEvent(JsonObject envelope)
    {
        if (envelope["event"] is not JsonObject inner) return;
        var delegation = envelope["delegation_id"]?.GetValue<string>() ?? "";
        var type = inner["type"]?.GetValue<string>();
        if (type == "response.created" && inner["response"]?["id"] is JsonNode responseId)
            _responseIds[delegation] = responseId.GetValue<string>();
        if (type == "response.output_item.done" && inner["item"] is JsonObject item && item["type"]?.GetValue<string>() == "function_call")
        {
            if (!_responseIds.TryGetValue(delegation, out var activeResponse)) return;
            if (!_calls.TryGetValue(activeResponse, out var calls)) _calls[activeResponse] = calls = new();
            calls.Add((JsonObject)item.DeepClone());
        }
        if (type is "response.completed" or "response.failed" or "response.incomplete" or "response.cancelled")
        {
            if (inner["response"] is JsonObject response && response["usage"] is JsonObject usage)
                OnUsageReceived?.Invoke(new UsageReport { ProviderId = "openai", ModelId = response["model"]?.GetValue<string>(),
                    OperationId = response["id"]?.GetValue<string>(), OperationType = UsageOperationType.DelegatedResponse,
                    ReportedInputTokens = usage["input_tokens"]?.GetValue<int>(), ReportedOutputTokens = usage["output_tokens"]?.GetValue<int>(),
                    ReportedTotalTokens = usage["total_tokens"]?.GetValue<int>(), CacheReadInputTokens = usage["input_tokens_details"]?["cached_tokens"]?.GetValue<int>(),
                    ReasoningOutputTokens = usage["output_tokens_details"]?["reasoning_tokens"]?.GetValue<int>(), IsFinal = true, RawProviderUsageJson = usage.ToJsonString() });
            var completedId = inner["response"]?["id"]?.GetValue<string>();
            if (completedId != null && _responseIds.TryGetValue(delegation, out var activeId) && activeId == completedId)
                _responseIds.Remove(delegation);
            if (type is "response.failed" or "response.incomplete")
            {
                // These are nested Responses failures, not top-level Live error events.
                // Surface them without retrying operations which may already have run.
                var details = inner["response"]?["error"]?.ToJsonString()
                    ?? inner["response"]?["incomplete_details"]?.ToJsonString() ?? "No failure details provided";
                OnError?.Invoke($"Live backend {type} (response {completedId}, delegation {delegation}): {details}");
            }
            if (completedId != null && _calls.Remove(completedId, out var calls) && type == "response.completed" && _settings?.Tools.Count > 0 && BackendContinuationError == null)
            {
                // Tool execution must not block microphone/audio/transcript reception.
                var task = Task.Run(() => ExecuteToolsAsync(calls));
                lock (_backendTasks) { _backendTasks.RemoveAll(t => t.IsCompleted); _backendTasks.Add(task); }
            }
        }
    }

    private async Task ExecuteToolsAsync(List<JsonObject> calls)
    {
        await _toolLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var submitted = false;
            foreach (var call in calls)
            {
                if (!IsConnected || BackendContinuationError != null) return;
                var id = call["call_id"]!.GetValue<string>();
                var name = call["name"]!.GetValue<string>();
                lock (_backendLock)
                {
                    if (_toolResults.ContainsKey(id)) continue;
                    _toolResults.Add(id, new OpenAiLiveToolResult(id, name, null, null, OpenAiLiveToolSubmissionState.NotSent, null));
                }
                var tool = _settings!.Tools.SingleOrDefault(t => t.Name == name);
                string output;
                try { output = tool == null ? "{\"error\":\"Tool is not registered for execution\"}" : await tool.ExecuteAsync(call["arguments"]!.GetValue<string>()).ConfigureAwait(false); }
                catch (Exception ex) { _log(LogLevel.Error, $"Live tool {name} failed: {ex.Message}"); output = "{\"error\":\"Tool execution failed\"}"; }
                lock (_backendLock)
                    _toolResults[id] = _toolResults[id] with { Output = output };
                // Execution is an application fact even when the subsequent submission fails.
                OnMessageReceived?.Invoke(ChatMessage.CreateToolMessage(name, output, id));
                if (!IsConnected || BackendContinuationError != null) return;
                await SendEventAsync(new JsonObject { ["type"] = "response.item.create", ["item"] = new JsonObject {
                    ["type"] = "function_call_output", ["call_id"] = id, ["output"] = output } }, _lifetime!.Token).ConfigureAwait(false);
                lock (_backendLock)
                    if (_toolResults[id].SubmissionState == OpenAiLiveToolSubmissionState.NotSent)
                        _toolResults[id] = _toolResults[id] with { SubmissionState = OpenAiLiveToolSubmissionState.SentUnconfirmed };
                submitted = true;
            }
            if (submitted && IsConnected && BackendContinuationError == null) await SendEventAsync(new JsonObject { ["type"] = "response.create" }, _lifetime!.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { FailBackend($"Live backend continuation failed: {ex.Message}"); }
        finally { _toolLock.Release(); }
    }

    private void FailBackend(string error)
    {
        lock (_backendLock)
        {
            if (_backendFailure != null) return;
            _backendFailure = error;
            // Closing from the receive loop would await the receiver itself. Schedule bounded graceful
            // finalization independently; DisconnectAsync serializes against application cleanup.
            _failureCleanup = Task.Run(async () =>
            {
                try { await DisconnectAsync().ConfigureAwait(false); }
                catch (Exception ex) { _log(LogLevel.Error, $"Live backend failure cleanup: {ex.Message}"); }
            });
        }
        OnError?.Invoke(error);
    }

    public async Task DisconnectAsync()
    {
        await _disconnectLock.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { _disconnectLock.Release(); }
    }

    private async Task DisconnectCoreAsync()
    {
        if (_socket == null) return;
        try
        {
            _closing = true;
            _audio?.Writer.TryComplete();
            _audioLifetime?.Cancel();
            if (_audioSender != null) await _audioSender.ConfigureAwait(false);
            if (_socket.State == WebSocketState.Open && _started.Task.IsCompletedSuccessfully && !_closed.Task.IsCompleted)
            {
                using var timeout = new CancellationTokenSource(_settings!.CloseTimeout);
                await SendCoreAsync(new JsonObject { ["type"] = "session.close" }, timeout.Token).ConfigureAwait(false);
                await _closed.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            if (!FinalUsageConfirmed) throw new IOException("Live session ended without confirmed final usage.");
        }
        finally { await ReleaseTransportAsync().ConfigureAwait(false); }
    }

    private async Task ReleaseTransportAsync()
    {
        _lifetime?.Cancel();
        _audioLifetime?.Cancel();
        _audio?.Writer.TryComplete();
        _socket?.Abort();
        if (_audioSender != null) await _audioSender.ConfigureAwait(false);
        if (_receiver != null) await _receiver.ConfigureAwait(false);
        _socket?.Dispose();
        _socket = null;
        _receiver = null;
        _lifetime?.Dispose();
        _lifetime = null;
        _audioLifetime?.Dispose();
        _audioLifetime = null;
        _audioSender = null;
        _audio = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await DisconnectAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log(LogLevel.Error, ex.Message); }
        finally { _disposed = true; }
    }
}
