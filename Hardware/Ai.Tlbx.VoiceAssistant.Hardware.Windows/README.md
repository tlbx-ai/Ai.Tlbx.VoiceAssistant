# Ai.Tlbx.VoiceAssistant.Hardware.Windows

Native Windows audio hardware support for the AI Voice Assistant Toolkit.

[![NuGet](https://img.shields.io/nuget/v/Ai.Tlbx.VoiceAssistant.Hardware.Windows.svg)](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant.Hardware.Windows/)

## Installation

```bash
dotnet add package Ai.Tlbx.VoiceAssistant.Hardware.Windows
```

## Requirements

- Windows 10 or later
- .NET 10.0 or .NET 11.0

## Usage

```csharp
var hardware = new WindowsAudioHardware();
var provider = new OpenAiVoiceProvider(apiKey, logger);

var assistant = new VoiceAssistant(provider, hardware);
await assistant.StartAsync(settings);
```

## Full Documentation

See the main package for complete documentation:

**[Ai.Tlbx.VoiceAssistant on NuGet](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant#readme-body-tab)**
