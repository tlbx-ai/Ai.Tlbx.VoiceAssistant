# Ai.Tlbx.VoiceAssistant.Provider.Google

Google Gemini Live API provider for the AI Voice Assistant Toolkit.

[![NuGet](https://img.shields.io/nuget/v/Ai.Tlbx.VoiceAssistant.Provider.Google.svg)](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant.Provider.Google/)

## Installation

```bash
dotnet add package Ai.Tlbx.VoiceAssistant.Provider.Google
```

## Usage

```csharp
var provider = factory.CreateGoogle(apiKey);
var settings = new GoogleVoiceSettings
{
    Voice = GoogleVoice.Puck,
    Model = GoogleModel.Gemini31FlashLivePreview,
    Instructions = "You are a helpful assistant."
};

var assistant = new VoiceAssistant(provider, audioHardware);
await assistant.StartAsync(settings);
```

## Full Documentation

See the main package for complete documentation:

**[Ai.Tlbx.VoiceAssistant on NuGet](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant#readme-body-tab)**

## Google Live Models

- `GoogleModel.Gemini31FlashLivePreview` is the default Gemini Live API model.
- `GoogleModel.Gemini25FlashNativeAudioLatest` targets Google's rolling native-audio Live API alias.
- Google TTS-only models such as `gemini-3.1-flash-tts-preview` use `generateContent`, not realtime `bidiGenerateContent`, and are intentionally not exposed through this realtime voice provider.
