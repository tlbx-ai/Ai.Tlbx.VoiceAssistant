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

The existing direct browser WebRTC integration is for Realtime. GPT-Live currently uses the library's
server-side WebSocket/audio hardware path. Keep project keys on the server.

Full examples and API analysis: https://github.com/AiTlbx/Ai.Tlbx.VoiceAssistant/blob/master/docs/openai-gpt-live.md

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
