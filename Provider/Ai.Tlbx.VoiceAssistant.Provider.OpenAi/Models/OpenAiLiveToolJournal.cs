using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ai.Tlbx.VoiceAssistant.Interfaces;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

/// <summary>The application-visible outcome of an action, independently of model or speech completion.</summary>
public enum OpenAiLiveToolExecutionState { Started, Completed, Uncertain }

/// <summary>A complete action record. An uncertain action must be reconciled with its service before retrying.</summary>
public sealed record OpenAiLiveToolExecution(string CallId, string Name, string Arguments,
    OpenAiLiveToolExecutionState State, string? Output);

/// <summary>Retains tool actions across cancellation, backend calls and Live reconnections. Share one instance
/// per application conversation, including with adapters for other inference libraries. Permissions and business
/// idempotency keys remain the application's responsibility; a new call ID is a distinct action.</summary>
public sealed class OpenAiLiveToolJournal
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, OpenAiLiveToolExecution> _records = new(StringComparer.Ordinal);

    /// <summary>Restore application-persisted records. Started records become uncertain after a restart.</summary>
    public OpenAiLiveToolJournal(IEnumerable<OpenAiLiveToolExecution>? records = null)
    {
        foreach (var record in records ?? Array.Empty<OpenAiLiveToolExecution>())
            _records.Add(record.CallId, record.State == OpenAiLiveToolExecutionState.Started
                ? record with { State = OpenAiLiveToolExecutionState.Uncertain } : record);
    }

    /// <summary>Optionally persist each snapshot atomically. Called before an action starts and after its outcome,
    /// even when the model was cancelled. A failed pre-action checkpoint prevents execution. Without this hook,
    /// records survive only as long as this object. Never store credentials in arguments or outputs.</summary>
    public Func<IReadOnlyList<OpenAiLiveToolExecution>, CancellationToken, Task>? CheckpointAsync { get; set; }

    /// <summary>A snapshot including full outputs and unresolved actions.</summary>
    public IReadOnlyList<OpenAiLiveToolExecution> Records { get { lock (_sync) return _records.Values.ToArray(); } }

    /// <summary>Run an authorized tool once for this call ID. Running IVoiceTool actions are awaited and recorded
    /// even if cancellation arrives, since that interface has no cancellation contract. Reusing an uncertain ID
    /// or changing its arguments is blocked; completed results are returned without replay.</summary>
    public async Task<string> ExecuteAsync(string callId, IVoiceTool tool, string arguments, CancellationToken cancellationToken = default, Action? validateBeforeExecution = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        ArgumentNullException.ThrowIfNull(tool);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OpenAiLiveToolExecution? existing;
            lock (_sync) _records.TryGetValue(callId, out existing);
            if (existing != null)
            {
                if (existing.Name != tool.Name || existing.Arguments != arguments)
                    throw new InvalidOperationException("A tool call ID cannot be reused for another action.");
                if (existing.State != OpenAiLiveToolExecutionState.Completed)
                    throw new InvalidOperationException("The prior action outcome is uncertain. Reconcile it with the application before retrying.");
                return existing.Output!;
            }
            cancellationToken.ThrowIfCancellationRequested();
            validateBeforeExecution?.Invoke();
            var record = new OpenAiLiveToolExecution(callId, tool.Name, arguments, OpenAiLiveToolExecutionState.Started, null);
            lock (_sync) _records.Add(callId, record);
            try { await Checkpoint(cancellationToken).ConfigureAwait(false); }
            catch { lock (_sync) _records.Remove(callId); throw; }
            try { cancellationToken.ThrowIfCancellationRequested(); validateBeforeExecution?.Invoke(); }
            catch
            {
                lock (_sync) _records.Remove(callId);
                await Checkpoint(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            string output;
            try { output = await tool.ExecuteAsync(arguments).ConfigureAwait(false); }
            catch
            {
                lock (_sync) _records[callId] = record with { State = OpenAiLiveToolExecutionState.Uncertain };
                await Checkpoint(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            lock (_sync) _records[callId] = record with { State = OpenAiLiveToolExecutionState.Completed, Output = output };
            await Checkpoint(CancellationToken.None).ConfigureAwait(false);
            return output;
        }
        finally { _gate.Release(); }
    }

    private Task Checkpoint(CancellationToken token) => CheckpointAsync?.Invoke(Records, token) ?? Task.CompletedTask;

    /// <summary>Start a new application conversation. Refuses to forget an in-flight action.</summary>
    public void Clear()
    {
        if (!_gate.Wait(0)) throw new InvalidOperationException("Cannot clear a journal while an action is running.");
        try { lock (_sync) _records.Clear(); }
        finally { _gate.Release(); }
    }
}
