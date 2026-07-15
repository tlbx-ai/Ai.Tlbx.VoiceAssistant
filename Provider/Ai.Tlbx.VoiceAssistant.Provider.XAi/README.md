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
    InputAudioLanguage = "de-DE",
    InputAudioKeyterms = ["TLBX"],
    EnableSessionResumption = true
};

var assistant = new VoiceAssistant(provider, audioHardware);
await assistant.StartAsync(settings);
```

`XaiVoice` contains all 26 current built-in voices. Set `VoiceId` to use a custom xAI voice ID instead. Use `GrokVoiceThinkFast10` instead of the default `GrokVoiceLatest` when you need a pinned production model.

## Full Documentation

See the main package for complete documentation:

**[Ai.Tlbx.VoiceAssistant on NuGet](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant#readme-body-tab)**
