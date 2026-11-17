# Ai.Tlbx.VoiceAssistant.Hardware.Web

Browser audio hardware support for Blazor applications in the AI Voice Assistant Toolkit.

[![NuGet](https://img.shields.io/nuget/v/Ai.Tlbx.VoiceAssistant.Hardware.Web.svg)](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant.Hardware.Web/)

## Installation

```bash
dotnet add package Ai.Tlbx.VoiceAssistant.Hardware.Web
```

## Features

- **Browser-native capture** with echo cancellation, noise suppression, and auto gain
- **De-esser** (high-shelf EQ) to tame sibilance before amplification
- **Compressor** (8:1 ratio) for consistent loudness across whispers and shouts
- **Worklet resampler** from the browser's actual capture rate to provider PCM16 rates
- **Provider-specific sample rates**: 16kHz for Google, 24kHz for xAI and legacy OpenAI WebSocket paths

OpenAI live voice on Blazor Server should use `Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore` for direct browser-to-OpenAI WebRTC audio. This package remains the PCM hardware path for Google, xAI, microphone tests, and transcription workflows.

## PCM playback

Chunks are submitted through a single ordered queue. Playback starts when 60 ms
of PCM is buffered, or after a maximum 60 ms wait for a shorter utterance. The
same bounded prebuffer applies after a real underrun; contiguous chunks keep
sample-exact transitions. A drain request flushes short buffered tails.

The 5 ms start/resume and underrun smoothing is enabled by default. For an A/B
comparison, call `await webAudioAccess.SetPlaybackSmoothingEnabledAsync(false)`;
pass `true` to enable it again. This works before initialization and during
playback. `PlaybackSmoothingEnabled` reports the applied preference. It preserves
queued PCM, resampling and the 60 ms prebuffer, and survives clear/reinitialization.
The Web demo exposes this toggle for Google, xAI and Mistral PCM playback. It does
not apply to SIP or OpenAI's direct WebRTC output.

`WaitForPlaybackDrainAsync` places a fence behind prior submissions and waits for
acknowledgement from the playback worklet, including its final fade. Browser
output latency is then allowed for before completion. This confirms rendered
output, not an acoustic measurement of the speaker. Timeout, interruption or
failed delivery returns `false`.

Clearing playback changes its generation. Late chunks and late clears cannot
alter a newer generation. Sequence checks reject duplicate or missing chunks;
they do not guess a new order for provider audio that has no sequence metadata.
These PCM controls do not change direct WebRTC playback.

## Requirements

- Modern browser with Web Audio API support
- HTTPS or localhost (required for microphone access)
- .NET 10.0 or .NET 11.0

## Usage

```csharp
// In Program.cs
builder.Services.AddScoped<IAudioHardwareAccess, WebAudioAccess>();

// In your Blazor component
@inject IAudioHardwareAccess AudioHardware

var provider = factory.CreateOpenAi(apiKey);
var assistant = new VoiceAssistant(provider, AudioHardware);
await assistant.StartAsync(settings);
```

## Full Documentation

See the main package for complete documentation:

**[Ai.Tlbx.VoiceAssistant on NuGet](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant#readme-body-tab)**
