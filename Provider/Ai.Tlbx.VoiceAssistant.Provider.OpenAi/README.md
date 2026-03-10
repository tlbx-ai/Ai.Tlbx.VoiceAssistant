# Ai.Tlbx.VoiceAssistant.Provider.OpenAi

OpenAI Realtime API provider for the AI Voice Assistant Toolkit.

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
    Voice = AssistantVoice.Alloy,
    Instructions = "You are a helpful assistant."
};

var assistant = new VoiceAssistant(provider, audioHardware);
await assistant.StartAsync(settings);
```

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

## Full Documentation

See the main package for complete documentation:

**[Ai.Tlbx.VoiceAssistant on NuGet](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant#readme-body-tab)**
