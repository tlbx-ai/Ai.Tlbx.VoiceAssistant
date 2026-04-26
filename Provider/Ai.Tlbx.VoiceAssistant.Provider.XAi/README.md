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
    Model = XaiVoiceModel.GrokVoiceThinkFast10,
    Voice = XaiVoice.Ara,
    Instructions = "You are a helpful assistant."
};

var assistant = new VoiceAssistant(provider, audioHardware);
await assistant.StartAsync(settings);
```

## Full Documentation

See the main package for complete documentation:

**[Ai.Tlbx.VoiceAssistant on NuGet](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant#readme-body-tab)**
