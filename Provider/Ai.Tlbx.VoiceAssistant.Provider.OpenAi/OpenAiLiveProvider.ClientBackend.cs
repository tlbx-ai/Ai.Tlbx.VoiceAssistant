using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
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
    private readonly HashSet<CancellationTokenSource> _clientJobs = new();
    private Task _clientTail = Task.CompletedTask;
    public Action<OpenAiLiveBackendActivity>? OnBackendActivity { get; set; }

    private void InitializeClientBackend()
    {
        _clientBackend?.Dispose();
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

    private void CollectClientTranscript(OpenAiLiveTranscriptDelta delta)
    {
        if (_settings?.ClientBackend == null) return;
        lock (_clientLock) _clientTranscript.Add((JsonNode)new JsonObject { ["role"] = delta.Role, ["text"] = delta.Delta,
            ["start_ms"] = delta.StartMs, ["end_ms"] = delta.EndMs });
    }

    private void StartClientDelegation(OpenAiLiveDelegation delegation)
    {
        var backend = _clientBackend;
        var options = _settings?.ClientBackend;
        if (options == null || delegation.Target != "client" || _closing || _lifetime == null) return;
        lock (_clientLock)
        {
            if (!_clientDelegations.Add(delegation.Id)) return;
            if (options.NewDelegationBehavior == OpenAiLiveDelegationBehavior.Supersede)
                foreach (var job in _clientJobs) job.Cancel();
            var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            work.CancelAfter(options.Timeout);
            _clientJobs.Add(work);
            var transcript = (JsonArray)_clientTranscript.DeepClone();
            var previous = _clientTail;
            OnBackendActivity?.Invoke(new(delegation.Id, "queued"));
            var task = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    await _clientRunLock.WaitAsync(work.Token).ConfigureAwait(false);
                    try
                    {
                        OnBackendActivity?.Invoke(new(delegation.Id, "running"));
                        if (transcript.Count == 0 && _settings!.InitialHistory.Count == 0 && _history.Count == 0)
                            throw new InvalidOperationException("Client delegation arrived without transcript or startup context. No backend action was guessed.");
                        var context = new OpenAiLiveBackendContext(SessionId, delegation, transcript,
                            _settings!.InitialHistory.Concat(_history).ToArray(), _settings.Tools.ToArray(),
                            async (progress, token) => {
                                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, work.Token);
                                await AppendCommentaryAsync(progress, delegation.Id, linked.Token).ConfigureAwait(false);
                            },
                            usage => OnUsageReceived?.Invoke(usage))
                        {
                            ReportContextAsync = async (content, token) => {
                                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, work.Token);
                                await AppendThinkingAsync(content, delegation.Id, linked.Token).ConfigureAwait(false);
                            }
                        };
                        var answer = options.Handler != null
                            ? await options.Handler(context, work.Token).ConfigureAwait(false)
                            : await backend!.RunAsync(transcript, work.Token).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(answer) || Encoding.UTF8.GetByteCount(answer) > 500)
                            throw new InvalidOperationException("Client agent must return a nonempty spoken result of at most 500 UTF-8 bytes. Keep full results in application storage.");
                        work.Token.ThrowIfCancellationRequested();
                        await AppendCommentaryAsync(answer, delegation.Id, work.Token).ConfigureAwait(false);
                        OnBackendActivity?.Invoke(new(delegation.Id, "completed"));
                    }
                    finally { _clientRunLock.Release(); }
                }
                catch (OperationCanceledException) when (work.IsCancellationRequested) { OnBackendActivity?.Invoke(new(delegation.Id, "cancelled")); }
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

    /// <summary>Stops running and queued client agent work and suppresses its eventual commentary. A running
    /// IVoiceTool has no cancellation contract: its complete result is retained and its action is never replayed.
    /// A later delegation waits for that tool to return before starting another backend run.</summary>
    public void CancelClientBackend()
    {
        lock (_clientLock) foreach (var work in _clientJobs) work.Cancel();
    }
}
