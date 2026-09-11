# GPT-Live integration and API analysis

API reviewed against official OpenAI documentation on 2026-09-11. Introduced in toolkit 11.0.0.

## Architecture and transport

GPT-Live is a continuously running conversation model. It can listen while speaking and delegates
reasoning/tools to a separate backend. Realtime's single-model, discrete response lifecycle does
not describe this API. A `gpt-live-1` value in `OpenAiVoiceSettings.ModelId` is therefore not an
integration: use the dedicated `OpenAiLiveProvider` and `OpenAiLiveSettings`.

| Area | GPT-Live contract | Toolkit implementation |
| --- | --- | --- |
| Primary WebSocket | `wss://api.openai.com/v1/live/sessions`, bearer authentication, no model query | `OpenAiLiveProvider`; configurable `ProviderEndpointOptions` |
| Startup | First command `session.start`, configuration in `session`; wait for `session.started` | `ConnectAsync` waits for readiness and releases failed transports |
| Audio | `session.input_audio.append.audio` and `session.output_audio.delta.delta`, base64 raw audio | Mono PCM16 little endian at 24 kHz, ordered serialized sends; caller paces capture |
| Other wire formats | Shared input/output format: PCM16 16/24 kHz or G.711 8 kHz | This hardware integration selects 24 kHz PCM16; it does not expose G.711 passthrough |
| Browser WebRTC | Server `POST /v1/live/sessions` with JSON `session` and `transport: {type:"webrtc",sdp:...}` | Separate future transport; existing direct Realtime browser provider is unchanged |
| WebRTC events | Negotiated audio tracks, JSON data channel, no `session.start` on channel | Do not reuse the Realtime multipart SDP/client-secret protocol |
| Sideband | Backend event/control connection to an existing primary session | Not needed when this provider owns the primary socket; no sideband helper in this release |

The primary WebSocket does not need a Realtime ephemeral key or a preliminary HTTP session call.
Microphone sends use a single consumer and a 100-chunk queue. Congestion aborts the session with
an error instead of accumulating unbounded audio or silently dropping samples. Pace incoming audio
at capture speed. Closing stops pending microphone sends while the event receiver drains final usage.
Custom endpoints/authentication headers/proxies are configured through `settings.Connection`.
Custom gateway routing is not proof of a gateway's compatibility with the Live wire protocol.

Sources: [Live overview](https://developers.openai.com/api/docs/guides/live),
[WebSocket connection](https://developers.openai.com/api/docs/guides/voice-websockets?api=live),
[WebRTC connection](https://developers.openai.com/api/docs/guides/voice-webrtc?api=live).

## Session shape, history, and updates

Startup fields are `model`, `instructions`, `input`, `audio`, `delegation`, and `store`.
`audio.format` is shared by input and output; `audio.output.voice` defaults to `marin`.
The string `Voice` also accepts additional Live voice names such as `quartz`, `ripple`, `vesper`,
`willow`, `stone`, `gleam`, `meridian`, `bossa`, `tempo`, `beacon`, `delta`, and `cinder`.
Custom-voice objects are not exposed by these settings.

Initial instructions support up to 16,384 tokens. Startup history accepts at most 128 text messages,
8,192 combined tokens, with `developer`, `user`, or `assistant` roles. Developer/user content uses
`input_text`; assistant content uses `output_text`. System and tool message roles are rejected.
The toolkit checks message count and roles; the API enforces tokenizer-dependent limits.
Use `InitialHistory` directly, or let `VoiceAssistant` supply its history through
`IStartupHistoryVoiceProvider` before connecting. The final assistant message is retained for Live.
Application-owned tool records should be summarized into relevant text before restoring a session.

Model, initial instructions/history, audio, store, and delegation mode cannot be replaced after
startup. `UpdateSettingsAsync` rejects those changes and only sends the supported
`session.delegation.responses` update. Noise reduction, speech speed, reasoning, thinking, and
preamble settings from Realtime do not belong to Live speech configuration. Unsupported non-default
controls fail validation. Configure backend reasoning in `Responses`, and conversational pacing in
the conversation instructions.

Use `AppendInstructionsAsync`, `AppendThinkingAsync`, and `AppendCommentaryAsync` for ongoing
context. Each accepts at most 500 tokens (API enforced), a plain string, and a delegation ID or
explicit null. Instructions are application-authored behavioral guidance; thinking supplies quiet
factual context; commentary asks for spoken delivery, which may be paraphrased. Do not put untrusted
tool results in instructions. Acknowledgments correlate through `client_event_id`, require frame
progress, and do not prove consumption or speech. Keep microphone audio flowing, including silence.
The toolkit bounds acknowledgment waits with `ConnectionTimeout`; a timeout is not a retry guarantee.

Source: [Session configuration, history and lifecycle](https://developers.openai.com/api/docs/guides/live-conversations).

## Delegation and tools

`Responses = null` selects `{type:"client"}`. `OnDelegationCreated` supplies the opaque delegation
ID, target, timeline offset, and optional response ID. The event contains no task text. Subscribe
to transcripts and retain application state, corrections, operation IDs and confirmations yourself.
Callbacks must return promptly; launch asynchronous backend work outside the callback. Check current
task state before executing an action or sending a result. Return the exact delegation ID through
`AppendCommentaryAsync`; repeated appends may continue the same task. Speech interruption does not
cancel your backend operation. Avoid returning old results into a replacement session.

For managed delegation, set `Responses` to a JSON object containing a required backend `model`.
Supported options include backend instructions, function/web-search tools, tool choice, parallel
tool calls, reasoning/text settings, output token cap and service tier. This object is the
Live-supported subset of Responses configuration, not an arbitrary standalone Responses request.
The library clones it before adding registered local tool schemas.

Backend events arrive inside `response.event.event`; retain the outer `delegation_id`. The provider
tracks `response.created` IDs, collects complete function items from `response.output_item.done`,
and waits for `response.completed` before executing the batch outside the audio receive loop.
Terminal snapshots can have `output: []` even when function calls require results. For every
registered operation it submits `response.item.create` with `function_call_output`, `call_id` and
output, then sends one bodyless `response.create` after the batch. An arguments-done event alone
is insufficient. Duplicate call IDs do not repeat execution. Failed/incomplete/cancelled responses
do not execute collected calls. Application permissions belong inside registered tool handlers.

`Tools` is explicit execution registration. With no local tools, raw events remain available for
applications that own the tool loop. `SendEventAsync` supports advanced commands such as
`response.item.create` and `response.create`; observe errors and nested lifecycle events because
those commands have no standalone acknowledgment. Do not both manually execute and automatically
register the same tools. A Live `response.create` starts backend work; it does not request speech.

Source: [Delegation and tool protocol](https://developers.openai.com/api/docs/guides/live-delegation).

## Transcripts, interruptions and usage

Input/output transcript deltas contain text and `start_ms`/`end_ms` on the session timeline.
There are no authoritative turn IDs or transcript-completed events. Preserve whitespace, repeated
words, timing and overlapping speakers. `OnTranscriptDelta` and `OnStructuredTranscriptionReceived`
expose the original fragments; structured records remain nonfinal. `VoiceAssistant` forwards the
structured callback for either role. No user/assistant `ChatMessage` or transcription-completed
event is invented. Consumers that need caption rows or persistent conversation history must group
fragments themselves and keep grouping revisable.

Audio deltas have no timing fields or audio-done event. Backend completion and transcript timestamps
do not establish playback completion. `SendInterruptAsync` clears local queued playback and appends
an instruction to stop speaking; future generated audio may still arrive. Live has no deterministic
Realtime-style speech cancellation. `SetInputMutedAsync` awaits mute/unmute acknowledgment but does
not stop inference, playback, billing, or tools.

Voice usage is duration based: `session.usage.updated.usage.seconds` is cumulative.
`UsageReport.OperationType = VoiceSession` reports `SessionDuration`, `IsCumulative = true`,
session ID and raw usage. Replace earlier snapshots for that session; never sum them.
`UsageManager` performs this replacement automatically;
`VoiceAssistant.TotalProviderSessionDuration` and session updates expose the aggregate separately
from local elapsed time. A late nonfinal snapshot cannot replace confirmed final usage. Do not add input
and output audio duration as a substitute. Backend token usage is separate `DelegatedResponse`
usage, keyed by response ID, preserving cache/reasoning details and original JSON.

`DisconnectAsync` sends `session.close`, stops new submissions, awaits `session.closed`, and then
releases transport resources. Final voice usage must be confirmed independently of socket close.
Timeout or transport loss raises a disconnect error, with `FinalUsageConfirmed = false`.
`CloseReason` preserves the terminal reason. Disposal logs finalization errors without masking an
exception already being unwound; use explicit disconnect when confirmation is required.
Finish required tool batches before closing: closing cancels queued Responses and blocks further
results/continuations. Existing local `IVoiceTool` calls cannot be forcibly cancelled through that
interface; reconnecting the same provider is rejected while one is still running. There is no
automatic retry or replay of potentially side-effecting work.

Sources: [Continuous transcripts and graceful close](https://developers.openai.com/api/docs/guides/live-conversations),
[Realtime migration](https://developers.openai.com/api/docs/guides/live-migration).

## Minimal integration

```csharp
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

await using var live = new OpenAiLiveProvider(); // OPENAI_API_KEY on your server
live.OnAudioReceived = audio => QueuePcm24KhzForPlayback(audio);
live.OnTranscriptDelta = fragment => SaveAndDisplayFragment(fragment);
live.OnUsageReceived = usage => RecordUsage(usage);

await live.ConnectAsync(new OpenAiLiveSettings
{
    Instructions = "Help the caller. Be concise; delegate factual lookups to the backend.",
    Responses = new JsonObject
    {
        ["model"] = "gpt-5.6-luna",
        ["instructions"] = "Return concise verified results.",
        ["tools"] = new JsonArray(JsonNode.Parse("{\"type\":\"web_search\"}"))
    }
});
// Continuously call await live.ProcessAudioAsync(base64Pcm16At24Khz)
// from your paced microphone pipeline; play received chunks in order.
await RunMicrophoneUntilFinishedAsync(live);
await live.DisconnectAsync();
```

The capitalized playback/storage/microphone helpers above are application callbacks, not toolkit
methods. To use existing toolkit hardware, register `.WithOpenAiLive()` on the voice assistant
builder and start `VoiceAssistant` with `OpenAiLiveSettings`. Subscribe to
`OnStructuredTranscriptionReceived` for continuous captions. Resolve `OpenAiLiveProvider` from DI
to attach client delegation handlers and send backend results. For client mode, omit `Responses`;
for local managed tools, add your `IVoiceTool` instances to `settings.Tools`.

## Validation and release notes

11.0.0 introduces a separate full-duplex provider and session-duration usage semantics. Existing
Realtime provider defaults remain unchanged; switching requires explicit settings and fragment-aware
caption/usage consumption. All toolkit packages retain a coordinated version.

11.0.1 corrects startup readiness: `IsConnected` remains false until the audio sender exists,
so microphone callbacks that run during session startup continue using the pre-connect buffer.

Local WebSocket tests verify startup/history/authentication, audio, overlapping transcripts,
acknowledgment/error correlation, empty-output function batches, backend/voice usage separation,
startup rejection, and missing finalization. Opt-in real API checks:

```powershell
dotnet run --project Tests/ContractTests -- --live-gpt-live-smoke
dotnet run --project Tests/ContractTests -- --live-gpt-live-tools-smoke
```

The first checks generated audio and final voice usage. Set `GPT_LIVE_SMOKE_INPUT_WAV` to a mono,
24-kHz PCM16 WAV containing an English order-status request to additionally verify spoken input,
client delegation, result acknowledgment and the returned shipping-status transcript.
The second checks a real backend function
call, result submission and continuation. These synthetic tests do not establish physical microphone,
speaker, echo cancellation, conversational interruption quality, or direct browser WebRTC behavior.
The Native AOT test app exposes `/live-smoke` for an opt-in compiled transport check.

Remaining transport options (direct Live WebRTC, sideband, telephony codecs and stored-session forking)
are documented API extension points, not implemented or tested by this release. Stored sessions may
be requested with `Store`; callers should allow a longer close timeout when recordings take time.
