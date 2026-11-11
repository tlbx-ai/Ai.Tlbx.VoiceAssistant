# Ai.Tlbx.VoiceAssistant.Hardware.Linux

Native Linux audio hardware support (ALSA) for the AI Voice Assistant Toolkit.

[![NuGet](https://img.shields.io/nuget/v/Ai.Tlbx.VoiceAssistant.Hardware.Linux.svg)](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant.Hardware.Linux/)

## Installation

```bash
dotnet add package Ai.Tlbx.VoiceAssistant.Hardware.Linux
```

## Requirements

- Linux with ALSA support
- .NET 10.0 or .NET 11.0
- ALSA development libraries:
  ```bash
  sudo apt-get install libasound2-dev
  ```

## Usage

```csharp
var hardware = new LinuxAudioDevice();
var provider = new OpenAiVoiceProvider(apiKey, logger);

var assistant = new VoiceAssistant(provider, hardware);
await assistant.StartAsync(settings);
```

## Full Documentation

See the main package for complete documentation:

**[Ai.Tlbx.VoiceAssistant on NuGet](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant#readme-body-tab)**
