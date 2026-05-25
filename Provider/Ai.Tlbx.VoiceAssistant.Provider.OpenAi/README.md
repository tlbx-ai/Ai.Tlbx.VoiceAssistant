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
        TranscriptionModel = OpenAiTranscriptionModel.GptRealtimeWhisper,
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

For low-latency live transcript deltas use `GptRealtimeWhisper`, the current
default for realtime transcription. For higher-quality file/HTTP transcription
use `Gpt4oTranscribe`; for lower cost use `Gpt4oMiniTranscribe` or the pinned
current `Gpt4oMiniTranscribe20251215` snapshot.
`Gpt4oTranscribeDiarize` enables speaker labels through the HTTP transcription
endpoint and is not supported by OpenAI's Realtime transcription stream.
Realtime Whisper does not accept the `prompt` parameter, so the provider omits
prompt steering and server-side turn detection for that model.
`Whisper1` remains available for OpenAI's bounded transcription API, but it does
not support the streamed HTTP responses used by `OpenAiHttpLiveTranscriber`.

## Full Documentation

See the main package for complete documentation:

**[Ai.Tlbx.VoiceAssistant on NuGet](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant#readme-body-tab)**
