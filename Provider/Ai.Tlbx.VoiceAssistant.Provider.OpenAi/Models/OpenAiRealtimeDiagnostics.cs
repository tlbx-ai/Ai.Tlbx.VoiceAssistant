using System;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

public enum OpenAiRealtimeProtocolDirection { Sent, Received }
public enum OpenAiSessionSnapshotStage { Requested, Sent, ServerReturned }

/// <summary>Protocol metadata; server events have unobserved origin unless explicitly correlated by the protocol.</summary>
public sealed record OpenAiRealtimeProtocolEvent(
    DateTimeOffset Timestamp, OpenAiRealtimeProtocolDirection Direction, string EventType,
    string? EventId, string? ResponseId, string? ItemId, string? CallId,
    string? ErrorCode, string? ReferencedEventId, string Origin, string? CreateReason,
    int WireCharacters, string? ContentJson);

/// <summary>
/// Immutable session JSON at one observation point. LocalRevision is an observation sequence, never a server acknowledgement.
/// RequestedRevision links only local requested/sent observations. Missing server fields remain missing.
/// SessionJson contains instructions and translated tool schemas; applications must choose explicitly to log it.
/// </summary>
public sealed record OpenAiRealtimeSessionSnapshot(
    long LocalRevision, long? RequestedRevision, DateTimeOffset Timestamp,
    OpenAiSessionSnapshotStage Stage, string Transport, string? EventId,
    string SessionJson, OpenAiInstructionsSnapshot? Instructions);
