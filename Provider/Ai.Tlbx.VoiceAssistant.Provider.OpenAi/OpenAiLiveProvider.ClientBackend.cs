using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi;

public sealed partial class OpenAiLiveProvider
{
    private OpenAiLiveClientBackend? _clientBackend;
    private readonly object _clientLock = new();
    private readonly JsonArray _clientTranscript = new();
    private readonly HashSet<string> _clientDelegations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _clientRunLock = new(1, 1);
    private CancellationTokenSource? _clientWork;

    private void InitializeClientBackend()
    {
        _clientBackend?.Dispose();
        lock (_clientLock)
        {
            _clientTranscript.Clear();
            _clientDelegations.Clear();
            _clientWork = null;
        }
        _clientBackend = _settings!.ClientBackend == null ? null : new OpenAiLiveClientBackend(_settings, _apiKey, _history,
            message => OnMessageReceived?.Invoke(message),
            result => { lock (_backendLock) _toolResults[result.CallId] = result; },
            usage => OnUsageReceived?.Invoke(usage));
    }

    private void CollectClientTranscript(OpenAiLiveTranscriptDelta delta)
    {
        if (_clientBackend == null) return;
        lock (_clientLock) _clientTranscript.Add((JsonNode)new JsonObject { ["role"] = delta.Role, ["text"] = delta.Delta,
            ["start_ms"] = delta.StartMs, ["end_ms"] = delta.EndMs });
    }

    private void StartClientDelegation(OpenAiLiveDelegation delegation)
    {
        var backend = _clientBackend;
        if (backend == null || delegation.Target != "client" || _closing || _lifetime == null) return;
        lock (_clientLock)
        {
            if (!_clientDelegations.Add(delegation.Id)) return;
            _clientWork?.Cancel();
            var work = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _clientWork = work;
            var task = Task.Run(async () =>
            {
                try
                {
                    await _clientRunLock.WaitAsync(work.Token).ConfigureAwait(false);
                    try
                    {
                        JsonArray transcript;
                        lock (_clientLock) transcript = (JsonArray)_clientTranscript.DeepClone();
                        if (transcript.Count == 0 && _settings!.InitialHistory.Count == 0 && _history.Count == 0)
                            throw new InvalidOperationException("Client delegation arrived without transcript or startup context. No backend action was guessed.");
                        var answer = await backend.RunAsync(transcript, work.Token).ConfigureAwait(false);
                        work.Token.ThrowIfCancellationRequested();
                        await AppendCommentaryAsync(answer, delegation.Id, work.Token).ConfigureAwait(false);
                    }
                    finally { _clientRunLock.Release(); }
                }
                catch (OperationCanceledException) when (work.IsCancellationRequested) { }
                catch (Exception ex) { FailBackend($"Live client backend failed (delegation {delegation.Id}): {ex.Message}"); }
                finally
                {
                    lock (_clientLock) if (ReferenceEquals(_clientWork, work)) _clientWork = null;
                    work.Dispose();
                }
            });
            lock (_backendTasks) { _backendTasks.RemoveAll(t => t.IsCompleted); _backendTasks.Add(task); }
        }
    }

    /// <summary>Stops pending client HTTP work and suppresses its eventual commentary. A running
    /// IVoiceTool has no cancellation contract: its complete result is retained and its action is never replayed.
    /// A later delegation waits for that tool to return before starting another backend run.</summary>
    public void CancelClientBackend()
    {
        lock (_clientLock) _clientWork?.Cancel();
    }
}
