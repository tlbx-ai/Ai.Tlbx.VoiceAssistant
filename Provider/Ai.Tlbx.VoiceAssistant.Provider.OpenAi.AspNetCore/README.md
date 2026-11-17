# Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore

ASP.NET Core integration and static browser assets for direct OpenAI Realtime and GPT-Live WebRTC sessions.

The package keeps OpenAI API keys and voice tools on the server while sending microphone and playback audio directly between the browser and OpenAI.

## Native WebRTC audio controls and diagnostics (11.12.0)

Both `OpenAiDirectLiveVoiceProvider` and `OpenAiDirectRealtimeVoiceProvider` expose
the same `BrowserAudioOptions` property and typed `OnWebRtcDiagnostics` callback.
No browser-asset fork or global `RTCPeerConnection` override is needed.

Configure the scoped provider **before starting a session**. Options are captured
at startup; reconnect to apply a replacement. The defaults preserve existing
behavior: native receiver buffering, enabled microphone processing, no custom
peer configuration, and no periodic statistics.

```csharp
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;

var audioOptions = new OpenAiWebRtcAudioOptions
{
    // Experiment with stability vs. latency. This is a browser hint, not a guarantee.
    JitterBufferTargetMilliseconds = 125,
    StatisticsInterval = TimeSpan.FromSeconds(1)
};

var live = serviceProvider.GetRequiredService<OpenAiDirectLiveVoiceProvider>();
live.BrowserAudioOptions = audioOptions;
live.OnWebRtcDiagnostics = snapshot =>
{
    if (snapshot.EventType == OpenAiWebRtcDiagnosticEventType.ReceiverConfiguration)
        Console.WriteLine($"Receiver hint: {snapshot.JitterBufferTargetStatus}, " +
            $"requested={snapshot.RequestedJitterBufferTargetMilliseconds} ms, " +
            $"readback={snapshot.ReceiverJitterBufferTargetMilliseconds} ms");

    if (snapshot.EventType == OpenAiWebRtcDiagnosticEventType.Statistics)
        foreach (var stream in snapshot.InboundAudio)
            Console.WriteLine($"lost={stream.PacketsLost}, concealed={stream.ConcealedSamples}, " +
                $"jitter={stream.JitterSeconds} s");
};
await live.StartBrowserSessionAsync(liveSettings); // Your existing OpenAiLiveSettings.
```

For Realtime, assign the same options/callback to
`OpenAiDirectRealtimeVoiceProvider` and start with `OpenAiVoiceSettings`.
The regular `VoiceAssistant.StartAsync(settings)` integration also uses these
provider properties. GPT-Live `Responses`/`ClientBackend`, Agent-Handoff,
permissions and server-side tools retain their existing configuration.

### Options and native browser support

| Option | Default | Contract |
|---|---|---|
| `JitterBufferTargetMilliseconds` | `null` | Finite 0–4000 ms; null never writes the receiver property. Try 100–150 ms and measure the result. |
| `StatisticsInterval` | `null` | Opt-in sampling, 500 ms–60 seconds. Null disables periodic `getStats()`. |
| `Microphone.EchoCancellation` | `null` → existing `true` preference | Native capture processing only. |
| `Microphone.NoiseSuppression` | `null` → existing `true` preference | Native capture processing only. |
| `Microphone.AutoGainControl` | `null` → existing `true` preference | Native capture processing only. |
| `PeerConnection` | `null` | Optional ICE candidate pool size (0–255) and `All`/`Relay` transport policy. |

Invalid options throw before session preparation, microphone capture or peer
creation. The browser boundary also validates direct JavaScript callers.
Native microphone filters do **not** repair received audio. For a deliberate
capture-processing experiment:

```csharp
live.BrowserAudioOptions = audioOptions with
{
    Microphone = new()
    {
        EchoCancellation = false,
        NoiseSuppression = false,
        AutoGainControl = false
    },
    PeerConnection = new() { IceCandidatePoolSize = 2 }
};
// Configure before start, or disconnect/reconnect to apply it.
```

Device selection remains the `microphoneDeviceId` argument on
`StartBrowserSessionAsync`. The bounded peer options do not configure arbitrary
ICE/TURN servers. In particular, `Relay` is not a way to provision a relay:
only use it with a relay path that the browser and remote service actually support.

A missing `RTCRtpReceiver.jitterBufferTarget` produces `Unsupported`; a rejected
assignment produces `Rejected`. Neither ends the voice session. `Applied`
confirms the receiver preference/readback, **not** actual buffering or end-to-end
speech latency. Browser versions may expose different statistics fields.

### Reading diagnostics correctly

`OnWebRtcDiagnostics` receives `OpenAiWebRtcDiagnosticSnapshot`:

| Data | Meaning |
|---|---|
| `EventType`, timestamps and connection states | Peer creation, remote audio-track arrival, connection changes, receiver configuration and sampling outcomes. Track arrival is **not** first audible speech. |
| `InboundAudio` | Native audio RTP counters: received/lost/discarded packets, jitter, concealment events/samples, inserted/removed samples and jitter-buffer counters. |
| `AudioPlayout` | Optional local playout synthesis counters/duration, total sample duration/count and cumulative playout delay. Separate from RTP concealment. |
| `SelectedTransport` | Protocol, candidate types and candidate-pair RTT. No candidate IDs, addresses, ports or server URLs. |
| Capability/status fields | Missing API support, jitter-hint outcome and whether native playout statistics are present. |

Unsupported numeric fields are `null`, not measured zero. `PacketsLost` can be
negative when late packets revise a loss estimate. Counters are cumulative.
For comparable successive snapshots of the same receive stream, use interval
differences:

- Mean jitter-buffer delay in ms:
  `1000 × ΔJitterBufferDelaySeconds / ΔJitterBufferEmittedCount`.
- Mean target delay in ms:
  `1000 × ΔJitterBufferTargetDelaySeconds / ΔJitterBufferEmittedCount`.
- Mean local playout delay in ms:
  `1000 × ΔTotalPlayoutDelaySeconds / ΔTotalSamplesCount`.

Only divide when the denominator is positive. Reset baselines on a changed
`DiagnosticSessionId`, changed stream layout or counter reset. Stream/playout
indexes identify array positions within a snapshot, not stable native IDs.
RTT and these averages do not measure microphone-to-speaker conversation latency.

Keep callbacks short: enqueue data into your host's logging/telemetry mechanism
and return. One collection and one callback may be in flight; pending statistics
coalesce, with a maximum of 16 pending lifecycle observations. At most 16 audio
streams and 16 playout paths are reported. Callback/getStats failures are isolated
from media, tools and Agent-Handoff. Stop/Dispose removes the sampling timer,
ignores late collections and rejects stale diagnostic generations after reconnect.
There is no library-owned history or unbounded diagnostic queue.

These observations are reported by the browser and contain no audio, SDP or
OpenAI secrets; they are not a trusted server-side usage/billing record.
No extra PCM pipeline, automatic tool retry or additional interruption is added.

Configuration/privacy/lifecycle behavior is covered by deterministic browser
and .NET contracts. Real browser capability checks, real-device listening and
network-loss/audio-quality experiments are separate acceptance evidence; a
simulated counter is not evidence that a particular user's crackling is fixed.

Standards: [receiver jitter-buffer target](https://w3c.github.io/webrtc-extensions/#dom-rtcrtpreceiver-jitterbuffertarget)
and [WebRTC statistics](https://www.w3.org/TR/webrtc-stats/).

## GPT-Live setup (11.0.2+)

```csharp
builder.Services.AddOpenAiDirectLiveVoice(options =>
{
    options.AuthorizeRequest = context => context.User.Identity?.IsAuthenticated == true;
});
app.MapOpenAiDirectLiveVoice();
```

Inject `OpenAiDirectLiveVoiceProvider` in an interactive Blazor component and call
`StartBrowserSessionAsync(new OpenAiLiveSettings { Instructions = "Help the caller." })`
from a user gesture. Subscribe to `OnTranscriptDelta` and `OnUsageReceived` before starting.
Call `DisconnectAsync` to confirm final usage and release browser media.

Live uses a server-side JSON SDP exchange and an authenticated sideband. Tool execution and usage
come from that trusted sideband; reflected audio is discarded. Settings and registered tools are
prepared server-side, with a short-lived one-use handshake capability. Authorize session preparation
as well as the endpoint. The existing Realtime setup below uses its own protocol and provider.

See the [GPT-Live guide](../../docs/openai-gpt-live.md) for managed/client delegation, microphone and
playback controls, history, lifecycle behavior and the `/gpt-live` demo.

## Blazor Server setup

```csharp
builder.Services.AddScoped<OpenAiDirectRealtimeVoiceProvider>();
builder.Services.AddOpenAiDirectRealtimeVoice(options =>
{
    options.AuthorizeRequest = context => context.User.Identity?.IsAuthenticated == true;
});

app.UseWebSockets();
app.MapOpenAiDirectRealtimeVoice();
```

Use `OpenAiDirectRealtimeVoiceProvider` with the normal `VoiceAssistant` orchestrator:

```csharp
var provider = serviceProvider.GetRequiredService<OpenAiDirectRealtimeVoiceProvider>();
var assistant = new VoiceAssistant(audioHardware, provider);
await assistant.StartAsync(new OpenAiVoiceSettings
{
    Instructions = "You are helpful."
});
```

For OpenAI voice sessions in Blazor Server this keeps the public `VoiceAssistant.StartAsync(settings)` workflow intact while moving microphone capture and assistant playback off the Blazor circuit. The server still mints ephemeral OpenAI client secrets and executes `IVoiceTool` calls.

## Tool preamble modes

Direct WebRTC applies non-default `ToolCallPreambleMode` values through Realtime instructions when `AppendToolCallPreambleInstructions` is `true` (the default). Those modes guide when the model emits a spoken tool bridge while preserving the low-latency remote media stream.

Set `OpenAiVoiceSettings.AppendToolCallPreambleInstructions = false` in the session factory to send `Instructions` exactly as supplied, including whitespace. The application then owns tool-call speech rules and response language; the selected `ToolCallPreambleMode` and audio/event delivery remain unchanged. Reconnect to apply new server-side settings to a Direct WebRTC session.

With augmentation enabled, `ToolCallPreambleMode.Disabled` is accepted as a strong model instruction, but it is not a deterministic audio gate. Both Direct WebRTC and the WebSocket provider forward received audio immediately to preserve low latency; applications that require strict phase filtering must enforce it outside the remote media stream.

## Connection status

The browser client reports explicit connection phases through the regular status callback, including server session preparation, control WebSocket opening, microphone permission, WebRTC offer creation, OpenAI connection, DataChannel opening, and listening state.

## Realtime usage events

After each `response.done`, the browser forwards the response ID, model ID, and OpenAI usage object to the authenticated control WebSocket as a client event with type `usage`. If input transcription is enabled, each `conversation.item.input_audio_transcription.completed` event is forwarded separately with type `transcription_usage`, its item/content identity, and the ASR usage object because OpenAI bills that model separately from the conversational Realtime response. Hosts can process both event types in `IOpenAiDirectRealtimeSessionEventSink.OnClientEventAsync` to keep an idempotent cost ledger and apply application budgets. The usage numbers originate in OpenAI's events, but the forwarding path still depends on the connected browser and should be identified as client-forwarded in audit reports.

### Shared instruction composition

Browser session creation uses `OpenAiInstructionsComposer.Compose(OpenAiVoiceSettings)`,
the same deterministic composer as `OpenAiVoiceProvider`. Set
`ToolCallPreambleInstructionsOverride` for an application-localized preamble, or
`AppendToolCallPreambleInstructions = false` to preserve the application instructions
exactly. Repeated composition does not mutate settings or duplicate additions. The
WebSocket provider's protocol callbacks and requested/sent/server-returned snapshot
properties are not browser data-channel observability APIs.
