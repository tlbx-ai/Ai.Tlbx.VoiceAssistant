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

## Tool-call speech policy

`ToolCallPreambleMode.Disabled` adds a strong Realtime instruction asking the
model to remain silent until its final answer. It is intentionally not an audio
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
