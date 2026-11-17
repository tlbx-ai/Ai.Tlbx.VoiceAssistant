using System.Text.Json.Nodes;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;

/// <summary>Optional native browser audio controls shared by Direct Realtime and GPT-Live.
/// Set on the provider before starting; reconnect to apply a replacement. Defaults preserve existing behavior.</summary>
public sealed record OpenAiWebRtcAudioOptions
{
    /// <summary>Preferred receiver buffering in milliseconds, 0–4000, or null to leave the browser default untouched.
    /// A hint, not guaranteed latency. Unsupported/rejected hints are reported through OnWebRtcDiagnostics, not session errors.</summary>
    public double? JitterBufferTargetMilliseconds { get; init; }

    /// <summary>Native microphone processing preferences. Null members retain the existing enabled defaults.
    /// These affect capture, not received audio. Browsers may interpret them as preferences.</summary>
    public OpenAiWebRtcMicrophoneOptions Microphone { get; init; } = new();

    /// <summary>Optional bounded native peer configuration. Does not add ICE/TURN servers or guarantee relay availability.</summary>
    public OpenAiWebRtcPeerConnectionOptions? PeerConnection { get; init; }

    /// <summary>Opt-in getStats sampling interval, 500 ms–60 seconds. Null (default) disables periodic sampling.
    /// At most one collection and one diagnostic callback are in flight. Pending statistics coalesce; lifecycle events have a bounded queue of 16.</summary>
    public TimeSpan? StatisticsInterval { get; init; }

    /// <summary>Validates before any session, microphone or peer is created.</summary>
    public void Validate()
    {
        if (JitterBufferTargetMilliseconds is { } target && (!double.IsFinite(target) || target < 0 || target > 4000))
            throw new ArgumentOutOfRangeException(nameof(JitterBufferTargetMilliseconds), "Use null or a finite value from 0 through 4000 milliseconds.");
        if (StatisticsInterval is { } interval && (interval.TotalMilliseconds < 500 || interval.TotalMilliseconds > 60000))
            throw new ArgumentOutOfRangeException(nameof(StatisticsInterval), "Use null or an interval from 500 milliseconds through 60 seconds.");
        ArgumentNullException.ThrowIfNull(Microphone);
        PeerConnection?.Validate();
    }

    internal JsonObject ToBrowserJson(string sessionId, bool diagnosticsEnabled)
    {
        Validate();
        return new JsonObject
        {
            ["diagnosticSessionId"] = sessionId,
            ["diagnosticsEnabled"] = diagnosticsEnabled,
            ["jitterBufferTargetMilliseconds"] = JitterBufferTargetMilliseconds,
            ["statisticsIntervalMs"] = StatisticsInterval?.TotalMilliseconds,
            ["microphone"] = new JsonObject
            {
                ["echoCancellation"] = Microphone.EchoCancellation,
                ["noiseSuppression"] = Microphone.NoiseSuppression,
                ["autoGainControl"] = Microphone.AutoGainControl
            },
            ["peerConnection"] = PeerConnection?.ToBrowserJson()
        };
    }
}

/// <summary>Optional native getUserMedia processing preferences; device selection remains on StartBrowserSessionAsync.</summary>
public sealed record OpenAiWebRtcMicrophoneOptions
{
    /// <summary>Acoustic echo cancellation. Null preserves the existing true preference.</summary>
    public bool? EchoCancellation { get; init; }
    /// <summary>Microphone noise suppression. Null preserves the existing true preference.</summary>
    public bool? NoiseSuppression { get; init; }
    /// <summary>Microphone automatic gain control. Null preserves the existing true preference.</summary>
    public bool? AutoGainControl { get; init; }
}

/// <summary>Supported RTCPeerConnection constructor preferences; null members leave native defaults unchanged.
/// Deliberately excludes server credentials, SDP and arbitrary JavaScript/configuration hooks.</summary>
public sealed record OpenAiWebRtcPeerConnectionOptions
{
    /// <summary>All (default browser policy) or Relay. Relay needs a working relay path supported by the remote service.</summary>
    public OpenAiWebRtcIceTransportPolicy? IceTransportPolicy { get; init; }
    /// <summary>Number of ICE candidates to pre-gather, 0–255; null leaves the browser default.</summary>
    public int? IceCandidatePoolSize { get; init; }

    internal void Validate()
    {
        if (IceTransportPolicy is { } policy && !Enum.IsDefined(policy))
            throw new ArgumentOutOfRangeException(nameof(IceTransportPolicy));
        if (IceCandidatePoolSize is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(IceCandidatePoolSize), "Use null or an integer from 0 through 255.");
    }

    internal JsonObject ToBrowserJson()
    {
        var result = new JsonObject();
        if (IceTransportPolicy is { } policy) result["iceTransportPolicy"] = policy == OpenAiWebRtcIceTransportPolicy.All ? "all" : "relay";
        if (IceCandidatePoolSize is { } size) result["iceCandidatePoolSize"] = size;
        return result;
    }
}

/// <summary>Native ICE transport selection policy; this does not provision STUN/TURN infrastructure.</summary>
public enum OpenAiWebRtcIceTransportPolicy { All, Relay }
