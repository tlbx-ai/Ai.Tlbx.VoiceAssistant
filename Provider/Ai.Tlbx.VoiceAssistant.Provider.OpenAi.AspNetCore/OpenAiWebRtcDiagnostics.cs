using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;

/// <summary>Native browser diagnostic kind. RemoteAudioTrackReceived means a track arrived, not first audible speech.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OpenAiWebRtcDiagnosticEventType>))]
public enum OpenAiWebRtcDiagnosticEventType
{
    PeerCreated, RemoteAudioTrackReceived, ConnectionStateChanged, ReceiverConfiguration,
    Statistics, StatisticsUnavailable
}

/// <summary>Result of applying the optional receiver hint. Applied confirms the setting, not actual playout latency.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OpenAiWebRtcJitterBufferTargetStatus>))]
public enum OpenAiWebRtcJitterBufferTargetStatus { NotRequested, Applied, Unsupported, Rejected }

/// <summary>Privacy-filtered browser observation. Missing native fields are null, not measured zeros.
/// RTP/playout counters are cumulative; use differences between snapshots for interval measurements.
/// Contains no audio, SDP, secrets, candidate addresses or candidate identifiers. Browser-reported, not trusted billing data.</summary>
public sealed record OpenAiWebRtcDiagnosticSnapshot
{
    /// <summary>Opaque local diagnostic generation; changes on reconnect and is unrelated to provider session credentials.</summary>
    public string DiagnosticSessionId { get; init; } = "";
    public OpenAiWebRtcDiagnosticEventType EventType { get; init; }
    /// <summary>Browser wall-clock capture time, Unix milliseconds.</summary>
    public long TimestampUnixMilliseconds { get; init; }
    /// <summary>Monotonic milliseconds since this browser peer's diagnostic controller started.</summary>
    public double ElapsedMilliseconds { get; init; }
    public string? ConnectionState { get; init; }
    public string? IceConnectionState { get; init; }
    public bool StatisticsSupported { get; init; }
    public bool? AudioPlayoutStatisticsAvailable { get; init; }
    public int? ReceiverIndex { get; init; }
    public bool? JitterBufferTargetSupported { get; init; }
    public OpenAiWebRtcJitterBufferTargetStatus? JitterBufferTargetStatus { get; init; }
    public double? RequestedJitterBufferTargetMilliseconds { get; init; }
    /// <summary>Receiver property readback only. Measure actual buffering through the statistics counters.</summary>
    public double? ReceiverJitterBufferTargetMilliseconds { get; init; }
    /// <summary>Fixed SDK code such as get-stats-failed; never raw browser exception text.</summary>
    public string? ErrorCode { get; init; }
    /// <summary>At most 16 inbound audio streams. StreamIndex identifies order within the snapshot, not an SSRC.</summary>
    public OpenAiWebRtcInboundAudioStatistics[] InboundAudio { get; init; } = [];
    /// <summary>At most 16 native playout paths. An empty array can mean the browser does not expose these stats.</summary>
    public OpenAiWebRtcAudioPlayoutStatistics[] AudioPlayout { get; init; } = [];
    public OpenAiWebRtcTransportStatistics? SelectedTransport { get; init; }

    internal static OpenAiWebRtcDiagnosticSnapshot? Read(string json, string? expectedSessionId)
    {
        if (expectedSessionId == null || json.Length > 32768) return null;
        var snapshot = JsonSerializer.Deserialize(json, OpenAiWebRtcJsonContext.Default.OpenAiWebRtcDiagnosticSnapshot);
        return snapshot?.DiagnosticSessionId == expectedSessionId && snapshot.InboundAudio is { Length: <= 16 } &&
            snapshot.AudioPlayout is { Length: <= 16 } ? snapshot : null;
    }
}

/// <summary>Native inbound RTP audio counters. Durations and jitter explicitly use seconds.</summary>
public sealed record OpenAiWebRtcInboundAudioStatistics
{
    public int StreamIndex { get; init; }
    public double? TimestampMilliseconds { get; init; }
    public long? PacketsReceived { get; init; }
    /// <summary>May be negative when packets arrive after an earlier loss estimate.</summary>
    public long? PacketsLost { get; init; }
    public long? PacketsDiscarded { get; init; }
    public double? JitterSeconds { get; init; }
    public long? ConcealedSamples { get; init; }
    public long? SilentConcealedSamples { get; init; }
    public long? ConcealmentEvents { get; init; }
    public long? TotalSamplesReceived { get; init; }
    public long? InsertedSamplesForDeceleration { get; init; }
    public long? RemovedSamplesForAcceleration { get; init; }
    /// <summary>Cumulative sum of buffering time per emitted sample, seconds; divide its interval delta by emitted-count delta.</summary>
    public double? JitterBufferDelaySeconds { get; init; }
    public double? JitterBufferTargetDelaySeconds { get; init; }
    public long? JitterBufferEmittedCount { get; init; }
}

/// <summary>Optional native media-playout counters. Synthesized samples here reflect local playout,
/// distinct from RTP concealment. Use TotalSamplesCount to normalize TotalPlayoutDelaySeconds.</summary>
public sealed record OpenAiWebRtcAudioPlayoutStatistics
{
    public int PlayoutIndex { get; init; }
    public double? TimestampMilliseconds { get; init; }
    public double? SynthesizedSamplesDurationSeconds { get; init; }
    public long? SynthesizedSamplesEvents { get; init; }
    public double? TotalSamplesDurationSeconds { get; init; }
    public double? TotalPlayoutDelaySeconds { get; init; }
    public long? TotalSamplesCount { get; init; }
}

/// <summary>Selected candidate-pair summary, without addresses, ports, server URLs or candidate identifiers.</summary>
public sealed record OpenAiWebRtcTransportStatistics
{
    public string? Protocol { get; init; }
    public string? LocalCandidateType { get; init; }
    public string? RemoteCandidateType { get; init; }
    public double? CurrentRoundTripTimeSeconds { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OpenAiWebRtcDiagnosticSnapshot))]
internal partial class OpenAiWebRtcJsonContext : JsonSerializerContext;
