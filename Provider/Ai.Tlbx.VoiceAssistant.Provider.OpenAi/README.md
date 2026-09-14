# Ai.Tlbx.VoiceAssistant.Provider.OpenAi

OpenAI Realtime and GPT-Live providers for the AI Voice Assistant Toolkit.

## GPT-Live (11.0)

Use `OpenAiLiveProvider` with `OpenAiLiveSettings` for `gpt-live-1`.
This separate server-side WebSocket provider supports continuous PCM16 audio, client delegation,
managed Responses delegation with registered `IVoiceTool` execution, timed transcript fragments,
and final duration usage. Register it using `WithOpenAiLive()`.

Set `OpenAiLiveSettings.Responses` to a `System.Text.Json.Nodes.JsonObject` containing a backend
`model` and supported Responses options. Null selects client delegation; handle
`OnDelegationCreated`, retain transcript/application context, and return verified results through
`AppendCommentaryAsync(content, delegationId)`. Keep each append within the API's 500-token limit.

Live does not use Realtime VAD, `response.cancel`, or audio commit events. Startup model, voice,
history and delegation mode are immutable. `OnTranscriptDelta` and the provider-neutral structured
transcription callback preserve fragments; they do not claim complete turns. `OnTranscriptionCompleted`
is not synthesized. Track playback separately. Duration reports are cumulative; replace them by
session ID, and check `FinalUsageConfirmed` after `DisconnectAsync`.

GPT-Live supports both server-side WebSocket/audio hardware and direct browser WebRTC.
For Blazor Server, use `OpenAiDirectLiveVoiceProvider` from the ASP.NET Core package (11.0.2+).
`OpenAiLiveProvider.ConnectWebRtcAsync` also exposes SDP creation and sideband attachment for custom
browser hosts. Keep project keys on the server. See the [Live integration guide](../../docs/openai-gpt-live.md).

Full examples and API analysis: https://github.com/tlbx-ai/Ai.Tlbx.VoiceAssistant/blob/master/docs/openai-gpt-live.md

### Client backend for large tool results (11.1)

For tools returning large documents or complex JSON, explicitly select the library-owned
client backend before connecting. It sends the **complete, unmodified tool results** to
the ordinary Responses HTTP endpoint. GPT-Live receives only the backend's concise factual
answer. This works with `OpenAiLiveProvider` and `OpenAiDirectLiveVoiceProvider`.

```csharp
var settings = new OpenAiLiveSettings
{
    Instructions = "Answer in German. Delegate data lookups to the backend.",
    Responses = null,
    Tools = [myDocumentTool],
    ClientBackend = new OpenAiLiveClientBackendOptions
    {
        Responses = new JsonObject
        {
            ["model"] = "gpt-5.6-luna",
            ["instructions"] = "Use verified tool records. Answer in German.",
            ["reasoning"] = new JsonObject { ["effort"] = "low" },
            ["max_output_tokens"] = 2048
        }
    }
};
await provider.ConnectAsync(settings);
```

`ClientBackend.Connection` configures the full HTTP endpoint, gateway headers, and API key
independently of the Live WebSocket endpoint. A different host or port requires explicit
backend authentication; the Live constructor key is never forwarded across authorities.
Settings are captured at connection start. An optional application-owned `HttpClient` is
reused and not disposed by the provider.

The runner retains role-labelled transcript fragments, startup history, acknowledged
instruction/thinking appends, and complete tool outputs across delegated followups. Later
speech can correct earlier fragments; they are never presented as finalized user turns.
Tool request/result callbacks include the call ID. `ToolResults` retains complete outputs
after failures; `AcceptedByClientBackend` confirms HTTP request acceptance, not speech.
Normal delegated Responses usage is forwarded through `OnUsageReceived`.

Backend work runs outside the Live receiver. A new delegation supersedes pending HTTP work
and suppresses stale commentary. `CancelClientBackend()` offers the same explicit cancellation.
A running `IVoiceTool` cannot be forcibly cancelled: its result is retained and the next
delegation waits for it. Disconnect remains bounded, but reuse of the provider is blocked
until any running tool returns. Duplicate call IDs never re-execute an action.

The spoken answer must fit a conservative **500 UTF-8 byte** bound; oversized answers fail
visibly without truncation. This is deliberately stricter than Live's 500-token append limit.
Backend HTTP limits and the selected model's context window still apply. Defaults allow
eight Responses rounds and two minutes of HTTP work per delegation; tools without a
cancellation contract may outlive that timeout. HTTP/continuation errors are surfaced and
close Live with bounded finalization. The runner never retries executed tools or changes
delegation mode automatically.

Managed Responses delegation remains available through `settings.Responses` with
`ClientBackend = null`. Its existing 128-item / 32768-byte conservative session input budget
is unchanged; it is unsuitable for the same large raw tool results.

[![NuGet](https://img.shields.io/nuget/v/Ai.Tlbx.VoiceAssistant.Provider.OpenAi.svg)](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant.Provider.OpenAi/)

## Installation

```bash
dotnet add package Ai.Tlbx.VoiceAssistant.Provider.OpenAi
```

## Usage

```csharp
var provider = factory.CreateOpenAi(apiKey);
var settings = new OpenAiVoiceSettings
{
    Voice = AssistantVoice.Marin,
    Model = OpenAiRealtimeModel.GptRealtime21,
    Instructions = "You are a helpful assistant.",
    VadThreshold = 0.75
};

var assistant = new VoiceAssistant(audioHardware, provider);
await assistant.StartAsync(settings);
```

`VadThreshold` directly controls the numeric `server_vad` activation threshold
without requiring a nested `TurnDetection` object. Higher values make speech
detection less sensitive and can reduce false interruptions from quiet background
audio. It is a convenience alias for `TurnDetection.Threshold` and is ignored by
`semantic_vad`. It can also be changed during a session:

```csharp
settings.VadThreshold = 0.8;
await assistant.UpdateSettingsAsync(settings);
```

## Response language and accent

`MostLikelySpokenLanguage` and `TranscriptionHint` guide **input transcription**.
They do not select the assistant's output language or accent. Set both explicitly
in `Instructions`, alongside the assistant's task instructions. For example:

```csharp
settings.MostLikelySpokenLanguage = "de";
settings.Instructions = """
    Du bist ein hilfreicher Sprachassistent.

    # Sprache
    Antworte auf Deutsch. Wechsle die Sprache nur auf ausdrücklichen Wunsch.
    Einzelne englische Fachbegriffe, Namen und kurze Bestätigungen ändern die Antwortsprache nicht.
    Das gilt auch für Rückfragen und kurze Ansagen vor oder nach Werkzeugaufrufen.

    # Aussprache
    Sprich natürliches Standarddeutsch mit deutscher Lautbildung, Wortbetonung und Satzmelodie.
    Halte diese Aussprache vom ersten bis zum letzten Wort und über alle Gesprächsrunden stabil.
    Sprich in natürlichem Tempo. Übernimm den Akzent deines Gegenübers nicht.
    """;
```

OpenAI recommends controlling language and accent separately; prompting remains
guidance rather than an accent guarantee. See the
[Realtime prompting guide](https://developers.openai.com/api/docs/guides/realtime-models-prompting#control-language-and-accent-separately).
`marin` (the toolkit default) and `cedar` are OpenAI's recommended standard voices
for quality, not guarantees of a particular German accent.

WebSocket settings changes are sent with `UpdateSettingsAsync`. The direct WebRTC
provider requires a new browser session; update the server-side session factory's
settings before reconnecting. OpenAI also requires a new session to change the
voice once audio has been generated.

## Tool-call speech policy

`AppendToolCallPreambleInstructions` defaults to `true`, preserving the library's
prompt rules for the selected `ToolCallPreambleMode`. Applications that provide
their own rules and response language can disable this augmentation independently:

```csharp
settings.ToolCallPreambleMode = selectedMode;
settings.AppendToolCallPreambleInstructions = false;
settings.Instructions = finalizedAuraPrompt;
```

With `false`, the transmitted `instructions` text is exactly `settings.Instructions`,
including whitespace, with no replacement rules. This applies to session creation,
WebSocket `UpdateSettingsAsync`, and Direct WebRTC session creation. The selected
mode and audio/event delivery remain unchanged. The application prompt is then
responsible for describing the desired tool-call speech behavior.

`ToolCallPreambleMode.Disabled` adds a strong Realtime instruction asking the
model to remain silent until its final answer when augmentation is enabled.
It is intentionally not an audio
gate: received audio is forwarded immediately, so the setting improves model
behavior without adding response-completion latency. Applications that require
deterministic phase filtering should filter transcripts at the application layer.

## HTTP Live Transcription

If you want near-live transcription without using the realtime WebSocket API, use
`OpenAiHttpLiveTranscriber`. It records microphone audio through the existing
`IAudioHardwareAccess` abstraction and repeatedly uploads the current utterance
to the HTTP transcription endpoint.

```csharp
var transcriber = new OpenAiHttpLiveTranscriber(
    audioHardware,
    new OpenAiHttpLiveTranscriptionOptions
    {
        TranscriptionModel = OpenAiTranscriptionModel.Gpt4oMiniTranscribe,
        Language = "de",
        Prompt = "Expect German with business and IT terms"
    },
    apiKey);

transcriber.OnUsageReceived = usage =>
{
    // Each successful full-snapshot upload is reported independently.
    Console.WriteLine($"Uploaded audio: {usage.InputAudioDuration?.TotalSeconds:F2}s");
};

var cts = new CancellationTokenSource();

await transcriber.TranscribeLive(chunk =>
{
    Console.Write($"\r{chunk}");
}, cts.Token);
```

This path is near-live, not true realtime: it uses short repeated HTTP uploads,
so partials may arrive a bit later. The callback receives the latest current
hypothesis for the active push-to-talk segment, which lets a UI replace the
current line as the model revises earlier words.

For low-latency live transcript deltas use `GptLiveTranscribe`, the current
default. It supports prompt, keyword and multiple-language hints plus a tunable
delay. For high-quality file/HTTP transcription use `GptTranscribe`, the current
HTTP default. The older GPT-4o and Realtime Whisper variants remain available
for compatibility.
`Gpt4oTranscribeDiarize` enables speaker labels through the HTTP transcription
endpoint and is not supported by OpenAI's Realtime transcription stream.
Realtime Whisper does not accept the `prompt` parameter, so the provider omits
prompt steering and server-side turn detection for that model.
`Whisper1` remains available for OpenAI's bounded transcription API, but it does
not support the streamed HTTP responses used by `OpenAiHttpLiveTranscriber`.

## Full Documentation

See the main package for complete documentation:

**[Ai.Tlbx.VoiceAssistant on NuGet](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant#readme-body-tab)**

## Realtime protocol diagnostics and session snapshots

`OpenAiVoiceProvider` exposes metadata diagnostics separately from the existing completed response trace:

```csharp
provider.OnProtocolEvent = e => Console.WriteLine(
    $"{e.Timestamp:O} {e.Direction} {e.EventType} response={e.ResponseId} " +
    $"call={e.CallId} error={e.ErrorCode} referencedEvent={e.ReferencedEventId}");
provider.OnSessionSnapshot = snapshot =>
    Console.WriteLine($"Session {snapshot.Stage}, local observation {snapshot.LocalRevision}");
```

Protocol entries include direction, timestamp, event/response/item/call IDs when present,
structured error code and referenced client event, and the library's reason for an observed
`response.create`. `Origin` is `library` for successfully sent events and `unobserved` for
received events. The latter does not attribute an event to a gateway, server VAD, or another
client without protocol evidence. Successful send means the transport accepted the write;
it does not mean the server accepted the request. Failed writes are not reported as sent.

Content is excluded by default. `OpenAiVoiceSettings.IncludeProtocolContent = true` enables
`ContentJson` for explicit application inspection, which can contain private prompts,
transcripts and tool results. Audio data and client-secret fields remain excluded. Observer
exceptions are isolated from protocol execution. These APIs describe the WebSocket Realtime
provider; they do not claim visibility into gateway-internal events or GPT Live protocol traffic.

`RequestedSessionSnapshot`, `SentSessionSnapshot`, and `ServerSessionSnapshot` retain the
latest observation of each stage. Snapshots contain immutable `SessionJson` strings copied
from the actual serialized request or server-returned session, including final instructions
and translated tool schemas when those fields exist. They are never automatically logged.
The client-secret HTTP path also produces snapshots, restricted to its `session` object;
the returned credential is excluded. A failed request does not advance the sent snapshot.
An HTTP response, including a rejection, proves request transport and therefore advances sent;
it does not imply configuration acceptance. A server-returned snapshot exists only if the
response actually contains a session object.

`LocalRevision` numbers observations; it is not a server revision. Only a local sent snapshot
has `RequestedRevision` linking its actual request. A `session.updated` event gets no invented
request correlation, even when several updates are outstanding. Inspect `SessionJson` with
`JsonDocument.TryGetProperty` to distinguish an omitted field from an explicit JSON `null`.
An omitted `instructions` or `tools` field is not confirmation of a previous value.

Prompt composition is shared with the browser session registry:

```csharp
settings.ToolCallPreambleInstructionsOverride =
    "Sprich vor einer längeren Werkzeugaktion genau einen kurzen Überleitungssatz.";
var prompt = OpenAiInstructionsComposer.Compose(settings);
// prompt.ApplicationText, prompt.LibraryPreamble, prompt.FinalText
```

The override replaces the library's preamble text, allowing application localization.
`AppendToolCallPreambleInstructions = false` ignores the override and preserves the input
instructions exactly, including whitespace and line endings. Composition never mutates
settings, so repeating session updates does not append the preamble twice. Requested/sent
snapshots retain prompt provenance captured when that payload was built. Server-returned
snapshots do not invent provenance for server-modified instructions.
