using System.Text.Json.Nodes;

namespace Ai.Tlbx.VoiceAssistant.Telnyx;

/// <summary>Server-side credentials and telephone transport settings. Never send these credentials to a browser.</summary>
public sealed class TelnyxOptions
{
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>Base64 account Ed25519 public key from Mission Control, not the API key.</summary>
    public string PublicKey { get; set; } = string.Empty;
    public Uri ApiBaseUri { get; set; } = new("https://api.telnyx.com/v2/");
    /// <summary>Externally reachable HTTPS origin/path prefix. Used verbatim to construct signed callback URLs.</summary>
    public Uri? PublicBaseUri { get; set; }
    public string RoutePrefix { get; set; } = "/telnyx";
    public string? ConnectionId { get; set; }
    public string? FromNumber { get; set; }
    public TimeSpan SignatureTolerance { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan MediaStartTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan CallSetupTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxWebSocketMessageBytes { get; set; } = 1024 * 1024;
    public int MaxQueuedAudioMilliseconds { get; set; } = 30000;
    public int MaxConcurrentCalls { get; set; } = 20;
    /// <summary>Defaults for the built-in PCM adapter; raw REST requests may use any Telnyx codec.</summary>
    public TelnyxStreamOptions Stream { get; set; } = new();

    public Uri WebhookUri => CallbackUri("webhook", "https");
    public Uri MediaUri => CallbackUri("media", "wss");

    private Uri CallbackUri(string suffix, string scheme)
    {
        if (PublicBaseUri is null || PublicBaseUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(PublicBaseUri.Query))
            throw new InvalidOperationException("PublicBaseUri must be an externally reachable HTTPS URL without a query string.");
        var uri = new Uri(PublicBaseUri.AbsoluteUri.TrimEnd('/') + RoutePrefix.TrimEnd('/') + "/" + suffix);
        return scheme == "wss" ? new UriBuilder(uri) { Scheme = scheme }.Uri : uri;
    }

    internal void ValidateHost()
    {
        if (string.IsNullOrWhiteSpace(PublicKey)) throw new InvalidOperationException("Telnyx PublicKey is required for webhook and WebSocket signature verification.");
        if (!RoutePrefix.StartsWith('/') || RoutePrefix.Contains('?') || RoutePrefix.Contains('#'))
            throw new InvalidOperationException("RoutePrefix must be an absolute route path.");
        if (SignatureTolerance <= TimeSpan.Zero || MediaStartTimeout <= TimeSpan.Zero || CallSetupTimeout <= TimeSpan.Zero ||
            MaxWebSocketMessageBytes < 1024 || MaxQueuedAudioMilliseconds < 100 || MaxConcurrentCalls < 1)
            throw new InvalidOperationException("Telnyx transport limits must be positive.");
        _ = WebhookUri; _ = MediaUri;
        Stream.ValidatePcmAdapter();
    }
}

public sealed class TelnyxStreamOptions
{
    public string ReceiveCodec { get; set; } = "L16";
    public string SendCodec { get; set; } = "L16";
    public int SendSampleRate { get; set; } = 16000;
    /// <summary>Native target leg; self sends assistant audio to the caller, including API-initiated outbound calls.</summary>
    public string TargetLegs { get; set; } = "self";

    public JsonObject ToRequest(Uri streamUrl) => new()
    {
        ["stream_url"] = streamUrl.AbsoluteUri,
        ["stream_track"] = "inbound_track",
        ["stream_codec"] = ReceiveCodec,
        ["stream_bidirectional_mode"] = "rtp",
        ["stream_bidirectional_codec"] = SendCodec,
        ["stream_bidirectional_sampling_rate"] = SendSampleRate,
        ["stream_bidirectional_target_legs"] = TargetLegs
    };

    internal void ValidatePcmAdapter()
    {
        if (ReceiveCodec is not ("PCMU" or "PCMA" or "L16") || SendCodec is not ("PCMU" or "PCMA" or "L16"))
            throw new NotSupportedException("The PCM audio adapter supports L16, PCMU and PCMA. Use the native REST/media interfaces for other codecs.");
        if ((SendCodec == "L16" && SendSampleRate is not (8000 or 16000 or 22050 or 24000 or 48000)) ||
            (SendCodec != "L16" && SendSampleRate != 8000))
            throw new ArgumentException("G.711 uses 8000 Hz; L16 supports the Telnyx sampling rates 8000, 16000, 22050, 24000 or 48000 Hz.");
    }
}
