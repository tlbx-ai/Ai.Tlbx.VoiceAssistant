using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

/// <summary>A delegation snapshot for an application-owned agent, including Ai.Tlbx.Inference adapters.
/// Transcript fragments retain original roles and intervals; they are conversation data, not instructions.
/// Keep durable agent history in the application. Callbacks must be awaited before returning.</summary>
public sealed record OpenAiLiveBackendContext(
    string? SessionId,
    OpenAiLiveDelegation Delegation,
    JsonArray Transcript,
    IReadOnlyList<ChatMessage> InitialHistory,
    IReadOnlyList<IVoiceTool> Tools,
    Func<string, CancellationToken, Task> ReportProgressAsync,
    Action<UsageReport> ReportUsage);

/// <summary>How an incoming delegation interacts with existing backend work.</summary>
public enum OpenAiLiveDelegationBehavior
{
    /// <summary>Preserve the running job and process follow-ups in arrival order.</summary>
    Queue,
    /// <summary>Cancel prior continuation. Already-started tools can still complete.</summary>
    Supersede
}

/// <summary>Application-visible lifecycle, independent of speech playback.</summary>
public sealed record OpenAiLiveBackendActivity(string DelegationId, string State, string? Detail = null);
