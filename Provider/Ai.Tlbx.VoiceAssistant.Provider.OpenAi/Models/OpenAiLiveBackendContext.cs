using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

/// <summary>A delegation snapshot for any application-owned inference library. Original transcript fragments,
/// application context and visual items remain data. Keep durable model history in the application.</summary>
public sealed record OpenAiLiveBackendContext(
    string? SessionId, OpenAiLiveDelegation Delegation, JsonArray Transcript,
    IReadOnlyList<ChatMessage> InitialHistory, IReadOnlyList<IVoiceTool> Tools,
    Func<string, CancellationToken, Task> ReportProgressAsync, Action<UsageReport> ReportUsage)
{
    /// <summary>Append concise factual background without requesting immediate speech. Await acceptance.</summary>
    public Func<string, CancellationToken, Task>? ReportContextAsync { get; init; }
    /// <summary>Capture the complete current provider context, including application and visual items.
    /// Provided by the library; adapters should not substitute a separate voice-only transcript.</summary>
    public Func<JsonArray>? GetLatestTranscript { get; init; }
    /// <summary>Share this journal across backend calls and reconnections. Persist through its checkpoint hook.</summary>
    public OpenAiLiveToolJournal ToolJournal { get; init; } = new();
    /// <summary>Other running or queued delegations, for application-owned routing decisions.</summary>
    public IReadOnlyList<string> ActiveDelegations { get; init; } = Array.Empty<string>();

    /// <summary>Take a fresh snapshot before model work or a per-delegation routing decision.</summary>
    public OpenAiLiveBackendContext Refresh() => this with { Transcript = GetLatestTranscript?.Invoke() ?? (JsonArray)Transcript.DeepClone() };

    /// <summary>Reject actions/results prepared before new user, application or visual context arrived.
    /// Assistant captions alone do not invalidate work. The provider reruns the backend with fresh context.</summary>
    public void ThrowIfContextChanged()
    {
        if (GetLatestTranscript != null && Relevant(Transcript) != Relevant(GetLatestTranscript()))
            throw new OpenAiLiveContextChangedException();
    }
    private static string Relevant(JsonArray transcript) => new JsonArray(transcript
        .Where(item => item?["role"]?.GetValue<string>() != "assistant")
        .Select(item => item?.DeepClone()).ToArray()).ToJsonString();

    /// <summary>Execute a registered, application-authorized tool through the shared action journal. Checks
    /// freshness before starting, records full outcomes even after cancellation, and never replays a call ID.</summary>
    public Task<string> ExecuteToolAsync(string callId, string name, string arguments, CancellationToken cancellationToken = default)
    {
        ThrowIfContextChanged();
        var tool = Tools.SingleOrDefault(tool => tool.Name == name)
            ?? throw new InvalidOperationException($"Tool is not registered: {name}");
        return ToolJournal.ExecuteAsync(callId, tool, arguments, cancellationToken, ThrowIfContextChanged);
    }
}

/// <summary>Control signal: refresh the request before starting another action or publishing its result.</summary>
public sealed class OpenAiLiveContextChangedException : InvalidOperationException
{
    public OpenAiLiveContextChangedException() : base("Live conversation context changed; refresh the backend request.") { }
}

/// <summary>Application-owned interpretation of a new delegation. A speech interruption alone does not cancel work.</summary>
public enum OpenAiLiveDelegationBehavior
{
    Queue,
    Supersede,
    /// <summary>Cancel existing continuation and acknowledge cancellation without starting a new backend job.</summary>
    Cancel,
    /// <summary>No new backend work or speech is required for this delegation.</summary>
    Ignore
}

/// <summary>Application-visible lifecycle, independent of speech playback.</summary>
public sealed record OpenAiLiveBackendActivity(string DelegationId, string State, string? Detail = null);
