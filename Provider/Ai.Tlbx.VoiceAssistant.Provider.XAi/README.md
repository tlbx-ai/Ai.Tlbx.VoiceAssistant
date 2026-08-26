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

`XaiVoice` contains all 28 current built-in voices. Set `VoiceId` to use a custom xAI voice ID instead. Use `GrokVoiceThinkFast20` instead of the moving `GrokVoiceLatest` alias when you need a pinned production model.

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
