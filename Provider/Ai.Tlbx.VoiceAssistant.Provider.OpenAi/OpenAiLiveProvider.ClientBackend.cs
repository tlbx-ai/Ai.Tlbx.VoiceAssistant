using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi;

public sealed partial class OpenAiLiveProvider
{
    private OpenAiLiveClientBackend? _clientBackend;
    private readonly object _clientLock = new();
    private readonly JsonArray _clientTranscript = new();
    private readonly HashSet<string> _clientDelegations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _clientRunLock = new(1, 1);
    private readonly Dictionary<CancellationTokenSource, string> _clientJobs = new();
    private Task _clientTail = Task.CompletedTask;
    public Action<OpenAiLiveBackendActivity>? OnBackendActivity { get; set; }

    private void InitializeClientBackend()
    {
        // ReleaseTransportAsync retains old runners until their already-started tools finish.
        lock (_clientLock)
        {
            _clientTranscript.Clear();
            _clientDelegations.Clear();
            _clientTail = Task.CompletedTask;
        }
        _clientBackend = _settings!.ClientBackend == null || _settings.ClientBackend.Handler != null ? null : new OpenAiLiveClientBackend(_settings, _apiKey, _history,
            message => OnMessageReceived?.Invoke(message),
            result => { lock (_backendLock) _toolResults[result.CallId] = result; },
            usage => OnUsageReceived?.Invoke(usage));
    }

    private JsonArray CaptureClientTranscript() { lock (_clientLock) return (JsonArray)_clientTranscript.DeepClone(); }

    private void CollectClientTranscript(OpenAiLiveTranscriptDelta delta)
    {
        if (_settings?.ClientBackend == null) return;
        lock (_clientLock) _clientTranscript.Add((JsonNode)new JsonObject { ["role"] = delta.Role, ["text"] = delta.Delta,
            ["start_ms"] = delta.StartMs, ["end_ms"] = delta.EndMs });
    }

    private void StartClientDelegation(OpenAiLiveDelegation delegation)
    {
        var backend = _clientBackend;
        var settings = _settings;
        var options = settings?.ClientBackend;
        if (options == null || delegation.Target != "client" || _closing || _lifetime == null) return;
        lock (_clientLock)
        {
            if (!_clientDelegations.Add(delegation.Id)) return;
            var earlierJobs = _clientJobs.Keys.ToArray();
            var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _clientJobs.Add(work, delegation.Id);
            var previous = _clientTail;
            var sessionId = SessionId;
            var lifetime = _lifetime;
            var initialHistory = settings!.InitialHistory.Concat(_history).ToArray();
            var tools = settings.Tools.ToArray();
            JsonArray ReadCurrent()
            {
                if (!ReferenceEquals(_lifetime, lifetime)) throw new OperationCanceledException("Live connection was replaced.");
                return CaptureClientTranscript();
            }
            OpenAiLiveBackendContext Context() => new(sessionId, delegation, ReadCurrent(), initialHistory, tools,
                async (progress, token) => {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, work.Token);
                    await AppendAsync("commentary", progress, delegation.Id, linked.Token, false).ConfigureAwait(false);
                }, usage => OnUsageReceived?.Invoke(usage))
            {
                GetLatestTranscript = ReadCurrent, ToolJournal = options.ToolJournal,
                ActiveDelegations = Active(),
                ReportContextAsync = async (content, token) => {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, work.Token);
                    await AppendAsync("thinking", content, delegation.Id, linked.Token, false).ConfigureAwait(false);
                }
            };
            string[] Active() { lock (_clientLock) return _clientJobs.Where(pair => pair.Key != work).Select(pair => pair.Value).ToArray(); }
            OnBackendActivity?.Invoke(new(delegation.Id, "queued"));
            var task = Task.Run(async () =>
            {
                try
                {
                    var behavior = options.NewDelegationBehavior;
                    if (options.ResolveDelegationAsync != null)
                    {
                        using var routing = CancellationTokenSource.CreateLinkedTokenSource(work.Token);
                        routing.CancelAfter(options.Timeout);
                        behavior = await options.ResolveDelegationAsync(Context(), routing.Token).ConfigureAwait(false);
                    }
                    if (!Enum.IsDefined(behavior)) throw new InvalidOperationException("Unknown delegation behavior.");
                    if (behavior == OpenAiLiveDelegationBehavior.Ignore) { OnBackendActivity?.Invoke(new(delegation.Id, "ignored")); return; }
                    if (behavior is OpenAiLiveDelegationBehavior.Supersede or OpenAiLiveDelegationBehavior.Cancel)
                        lock (_clientLock) foreach (var job in earlierJobs.Where(job => _clientJobs.ContainsKey(job))) job.Cancel();
                    await previous.WaitAsync(work.Token).ConfigureAwait(false);
                    await _clientRunLock.WaitAsync(work.Token).ConfigureAwait(false);
                    try
                    {
                        // Queue time is not execution time. Never expire a follow-up before it can start.
                        work.CancelAfter(options.Timeout);
                        if (behavior == OpenAiLiveDelegationBehavior.Cancel)
                        {
                            await AppendCommentaryAsync("Die weitere Bearbeitung wurde abgebrochen. Bereits ausgeführte Aktionen bleiben wirksam.", delegation.Id, work.Token).ConfigureAwait(false);
                            OnBackendActivity?.Invoke(new(delegation.Id, "cancelled")); return;
                        }
                        OnBackendActivity?.Invoke(new(delegation.Id, "running"));
                        for (var attempt = 0; attempt < options.MaxRounds; attempt++)
                        {
                            var context = Context();
                            if (context.Transcript.Count == 0 && initialHistory.Length == 0)
                                throw new InvalidOperationException("Client delegation arrived without transcript or startup context. No backend action was guessed.");
                            try
                            {
                                if (backend != null) backend.Context = context;
                                var answer = options.Handler != null
                                    ? await options.Handler(context, work.Token).ConfigureAwait(false)
                                    : await backend!.RunAsync(context.Transcript, work.Token).ConfigureAwait(false);
                                if (string.IsNullOrWhiteSpace(answer)) throw new InvalidOperationException("Client agent returned no spoken facts.");
                                context.ThrowIfContextChanged();
                                work.Token.ThrowIfCancellationRequested();
                                await AppendCommentaryAsync(answer, delegation.Id, work.Token).ConfigureAwait(false);
                                OnBackendActivity?.Invoke(new(delegation.Id, "completed")); return;
                            }
                            catch (OpenAiLiveContextChangedException)
                            { OnBackendActivity?.Invoke(new(delegation.Id, "refreshing", "New conversation context; retained tool outcomes will not be replayed.")); }
                        }
                        throw new InvalidOperationException("Client context kept changing; no stale result was published.");
                    }
                    finally { _clientRunLock.Release(); }
                }
                catch (OperationCanceledException) when (work.IsCancellationRequested || _closing) { OnBackendActivity?.Invoke(new(delegation.Id, "cancelled")); }
                catch (InvalidOperationException ex) when (_closing) { OnBackendActivity?.Invoke(new(delegation.Id, "cancelled", ex.Message)); }
                catch (Exception ex) { OnBackendActivity?.Invoke(new(delegation.Id, "failed", ex.Message)); FailBackend($"Live client backend failed (delegation {delegation.Id}): {ex.Message}"); }
                finally
                {
                    lock (_clientLock) _clientJobs.Remove(work);
                    work.Dispose();
                }
            });
            _clientTail = task;
            lock (_backendTasks) { _backendTasks.RemoveAll(t => t.IsCompleted); _backendTasks.Add(task); }
        }
    }

    /// <summary>Cancel model continuation and pending commentary. Already-started tools finish and remain
    /// in the shared journal. This does not undo an action or merely stop speech playback.</summary>
    public void CancelClientBackend()
    {
        lock (_clientLock) foreach (var work in _clientJobs.Keys) work.Cancel();
    }
}
