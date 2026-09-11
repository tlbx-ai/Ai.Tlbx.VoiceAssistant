namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

/// <summary>Submission state, independent of the tool's execution result.</summary>
public enum OpenAiLiveToolSubmissionState
{
    /// <summary>No submission has been attempted.</summary>
    NotSent,
    /// <summary>The conservative session budget blocked the send. The complete result remains available.</summary>
    BudgetExceeded,
    /// <summary>Sending failed; delivery is unknown. Never replay the action.</summary>
    TransportUncertain,
    /// <summary>Written to the transport; Live provides no standalone item success acknowledgment.</summary>
    SentUnconfirmed,
    /// <summary>The API rejected this submission through its correlated error event.</summary>
    Rejected
}

/// <summary>A local execution snapshot. Null Output means execution has not returned yet.
/// Output is retained verbatim, including after submission failure or disconnect.</summary>
public sealed record OpenAiLiveToolResult(string CallId, string ToolName, string? Output, string? EventId,
    OpenAiLiveToolSubmissionState SubmissionState, string? Error);
