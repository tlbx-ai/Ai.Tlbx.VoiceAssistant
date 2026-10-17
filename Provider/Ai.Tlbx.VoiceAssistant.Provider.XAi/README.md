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

## Grok Voice 3.0

`GrokVoiceLatest` currently routes to `grok-voice-3.0`. Use `GrokVoice30` to pin this release; older IDs remain explicitly selectable and deprecated. xAI documents no separate economy voice model. The existing endpoint and session protocol remain supported, including custom voices, pronunciation replacements, tools and session resumption.

Pricing is USD 0.08 per minute plus USD 0.004 per text input event. With default server VAD, billing covers the whole session, including silence and tool wait. Push-to-talk bills input plus output audio duration. The provider emits a separate cumulative, client-measured `VoiceSession` report for VAD sessions; response audio durations remain diagnostic and must not be charged again. Local clock estimates are marked as estimates and are not provider-confirmed invoices. Switching metering modes requires reconnecting.

`OnEventReceived` forwards complete original messages, including hosted-tool progress. See [model migration](https://docs.x.ai/developers/model-capabilities/audio/speech-to-speech#model-selection) and [billing rules](https://docs.x.ai/developers/models/speech-to-speech).

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
