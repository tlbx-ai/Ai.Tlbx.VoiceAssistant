# Ai.Tlbx.VoiceAssistant.Provider.XAi

xAI Grok Voice Agent API provider for the AI Voice Assistant Toolkit.

[![NuGet](https://img.shields.io/nuget/v/Ai.Tlbx.VoiceAssistant.Provider.XAi.svg)](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant.Provider.XAi/)

## Installation

```bash
dotnet add package Ai.Tlbx.VoiceAssistant.Provider.XAi
```

## Usage

```csharp
var provider = factory.CreateXai(apiKey);
var settings = new XaiVoiceSettings
{
    Model = XaiVoiceModel.GrokVoiceLatest,
    Voice = XaiVoice.Eve,
    Instructions = "You are a helpful assistant.",
    TalkingSpeed = 1.1,
    VadThreshold = 0.85,
    InputAudioLanguage = "de-DE",
    InputAudioKeyterms = ["TLBX"],
    EnableSessionResumption = true
};

var assistant = new VoiceAssistant(audioHardware, provider);
await assistant.StartAsync(settings);
```

`VadThreshold` directly controls xAI's numeric server-VAD activation threshold
from 0.1 to 0.9. Higher values make speech detection less sensitive and can reduce
false interruptions from quiet background audio. It is a convenience alias for
`TurnDetection.Threshold` and can be changed together with the active settings.

`XaiVoice` contains all 28 current built-in voices. Set `VoiceId` to use a custom xAI voice ID instead.

## Grok Voice Think Fast 2.0

| Library value | API model ID | Use case |
|---|---|---|
| `GrokVoiceLatest` | `grok-voice-latest` | Recommended moving alias; currently routes to Think Fast 2.0 |
| `GrokVoiceThinkFast20` | `grok-voice-think-fast-2.0` | Pinned production rollout |
| `GrokVoiceThinkFast10` | `grok-voice-think-fast-1.0` | Deprecated compatibility baseline |

xAI released Think Fast 2.0 on July 29, 2026 and moved `grok-voice-latest`
to it on August 5. The wire protocol remains the Speech-to-Speech Realtime
WebSocket API, so the model supports the provider's existing 24 kHz PCM audio,
streaming transcription, interruption, tools, custom voices, pronunciation
replacement, and session resumption paths.

Reasoning is enabled at `high` by xAI when `ReasoningEffort` is left unset. Set
`SessionReasoningEffort.None` explicitly for workloads where reasoning is not
wanted. xAI documents Think Fast 2.0 at USD 0.08 per minute of audio plus USD
0.004 per text input. For migration from 1.0, prefer a shorter system prompt and
remove workaround instructions that only compensated for older model behavior.

## Streaming Speech to Text

```csharp
var provider = new XaiTranscriptionProvider(apiKey);
var assistant = new VoiceAssistant(audioHardware, provider);

assistant.OnStructuredTranscriptionReceived = transcript =>
    Console.WriteLine(transcript.ToSpeakerLabeledText());

await assistant.StartAsync(new XaiTranscriptionSettings
{
    Language = "de",
    Keyterms = ["TLBX"],
    Diarize = true,
    SmartTurnThreshold = 0.7,
    SmartTurnTimeoutMs = 3000
});
```

The structured callback preserves word timestamps, acoustic speaker labels, optional channel assignment, final/chunk-final state, and Smart Turn confidence. For calls recorded with one speaker per channel, prefer `Multichannel = true` plus `Channels = 2` over acoustic diarization.

## Full Documentation

See the main package for complete documentation:

**[Ai.Tlbx.VoiceAssistant on NuGet](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant#readme-body-tab)**
