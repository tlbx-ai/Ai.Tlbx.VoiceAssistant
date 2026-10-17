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
    Model = GoogleModel.Gemini38Live,
    Instructions = "You are a helpful assistant.",
    AutomaticContextCompression = true,
    EnableSessionResumption = true
};

var assistant = new VoiceAssistant(provider, audioHardware);
await assistant.StartAsync(settings);
```

## Full Documentation

See the main package for complete documentation:

**[Ai.Tlbx.VoiceAssistant on NuGet](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant#readme-body-tab)**

## Google Live Models

- `GoogleModel.Gemini38Live` is the stable low-latency default. `Gemini38LiveExtendedThinking` adds background reasoning with low/medium/high thinking levels. The regular 3.8 model rejects explicit thinking controls.
- Both 3.8 models use non-blocking tools. Speech reception continues while local tools run. `OnInteractionStatusChanged` forwards server activity; `turnComplete` alone does not mean the agent is idle. `OnEventReceived` preserves complete original messages for grounding metadata and API additions.
- `GoogleModel.Gemini31FlashLivePreview` and `Gemini25FlashNativeAudioLatest` remain explicitly selectable alternatives.
- See the [3.8 migration contract](https://ai.google.dev/gemini-api/docs/models/gemini-3.8-live) and [Extended Thinking protocol](https://ai.google.dev/gemini-api/docs/models/gemini-3.8-live-extended-thinking).
- `GoogleModel.Gemini25FlashNativeAudioLatest` targets Google's rolling native-audio Live API alias.
- Google TTS-only models such as `gemini-3.1-flash-tts-preview` use `generateContent`, not realtime `bidiGenerateContent`, and are intentionally not exposed through this realtime voice provider.
- `GoogleVoice` exposes all 30 native-audio voices. Context compression and session resumption are enabled by default for long-running sessions.
