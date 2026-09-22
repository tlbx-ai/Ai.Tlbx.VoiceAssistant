# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Important Shell Commands

### Common Development Commands

Build the entire solution:
```bash
dotnet build TLBX.Ai.VoiceAssistant.slnx
```

Build and generate NuGet packages:
```bash
dotnet pack TLBX.Ai.VoiceAssistant.slnx
```

Run specific demo applications:
```bash
# Windows Demo
dotnet run --project Demo/Ai.Tlbx.VoiceAssistant.Demo.Windows/Ai.Tlbx.VoiceAssistant.Demo.Windows.csproj

# Linux Demo
dotnet run --project Demo/Ai.Tlbx.VoiceAssistant.Demo.Linux/Ai.Tlbx.VoiceAssistant.Demo.Linux.csproj

# Web Demo
dotnet run --project Demo/Ai.Tlbx.VoiceAssistant.Demo.Web/Ai.Tlbx.VoiceAssistant.Demo.Web.csproj
```

Clean build artifacts:
```bash
dotnet clean TLBX.Ai.VoiceAssistant.slnx
```

Publish NuGet packages (auto-increment version):
```bash
pwsh publish-nuget.ps1
```

Deep clean of bin/obj folders:
```bash
cleanBinObj.cmd
```

Publish without version increment:
```bash
pwsh publish-nuget-current-version.ps1
```

Sign NuGet packages (requires certificate):
```bash
pwsh sign-packages.ps1 -CertPath "path/to/cert.pfx" -CertPassword "password"
```

## Web Demo Testing Workflow

When user says "launch the web demo" or similar, this is a test session:

1. **Launch**: Run `pwsh launch-web-test.ps1` in background
2. **Wait**: Server starts at https://localhost:7079, isolated Chrome opens automatically
3. **Test**: User tests the voice assistant (OpenAI, Google, xAI providers)
4. **Monitor**: Watch console output for errors/diagnostics
5. **End**: User closes Chrome browser, server stops automatically
6. **Debrief**: Discuss results, errors, and improvements

The script creates an isolated Chrome profile (like Visual Studio does) so browser state doesn't interfere with testing.

## High-Level Architecture (v8.0)

This is a voice assistant toolkit that provides a modular architecture for integrating with multiple AI providers. The architecture uses composition-based design with clean separation between orchestration and provider implementations.

### Core Components

1. **Main Orchestrator** (`Provider/Ai.Tlbx.VoiceAssistant/`)
   - `VoiceAssistant`: Main orchestrator class that coordinates between hardware and AI providers
   - Provider-agnostic interfaces: `IVoiceProvider`, `IVoiceSettings`, `IVoiceTool`
   - Fluent DI configuration via `VoiceAssistantBuilder`
   - Thread-safe `ChatHistoryManager` for conversation state

2. **Provider Implementations**
   - **OpenAI** (`Provider/Ai.Tlbx.VoiceAssistant.Provider.OpenAi/`): WebSocket-based Realtime API (24kHz audio)
   - **Google** (`Provider/Ai.Tlbx.VoiceAssistant.Provider.Google/`): WebSocket-based Gemini Live API (16kHz audio)
   - **xAI** (`Provider/Ai.Tlbx.VoiceAssistant.Provider.XAi/`): WebSocket-based Grok Voice Agent API (24kHz audio)
   - Each provider implements `IVoiceProvider` interface with `RequiredInputSampleRate` property

3. **Hardware Abstraction** (`IAudioHardwareAccess` interface)
   - Defines contract for platform-specific audio operations
   - Key methods: `InitAudioAsync()`, `StartRecordingAudio()`, `PlayAudio()`, `GetAvailableMicrophonesAsync()`
   - Each platform provides its own implementation

4. **Platform Implementations**
   - **Windows** (`Hardware/Ai.Tlbx.VoiceAssistant.Hardware.Windows/`): Uses NAudio library
   - **Linux** (`Hardware/Ai.Tlbx.VoiceAssistant.Hardware.Linux/`): Direct ALSA integration via P/Invoke
   - **Web** (`Hardware/Ai.Tlbx.VoiceAssistant.Hardware.Web/`): JavaScript interop with Web Audio API and AudioWorklet

5. **UI Components** (`WebUi/Ai.Tlbx.VoiceAssistant.WebUi/`)
   - Reusable Blazor components for audio controls and chat interfaces
   - Platform-agnostic UI elements

### Audio Processing Flow (Web Hardware)

1. **Capture**: Browser captures at 48kHz via Web Audio API with echoCancellation, noiseSuppression, autoGainControl
2. **Processing Chain**: De-esser (high-shelf EQ @ 5.5kHz) → Gain (1.5x) → Compressor (8:1 @ -18dB threshold)
3. **Anti-aliasing**: 2nd order Butterworth low-pass filter in AudioWorklet
4. **Downsampling**: 48kHz → provider's required rate (16kHz for Google, 24kHz for OpenAI/xAI)
5. **Encoding**: PCM 16-bit, base64 encoded, sent to AI provider
6. **Playback**: Provider returns 24kHz audio, upsampled to 48kHz with linear interpolation

### Provider Sample Rates

| Provider | Input Rate | Output Rate |
|----------|------------|-------------|
| OpenAI   | 24kHz      | 24kHz       |
| Google   | 16kHz      | 24kHz       |
| xAI      | 24kHz      | 24kHz       |

The `AudioSampleRate` enum defines supported rates: `Rate16000`, `Rate24000`, `Rate44100`, `Rate48000`

### Key Design Patterns

- **Composition Pattern**: VoiceAssistant orchestrator composes with pluggable providers
- **Strategy Pattern**: Platform implementations via `IAudioHardwareAccess`
- **Dependency Injection**: Fluent builder pattern for DI configuration
- **Template Method**: `IVoiceTool` for custom AI tool extensions

### Package Distribution

The solution produces multiple NuGet packages (output to `nupkg/`):
- `Ai.Tlbx.VoiceAssistant`: Core orchestrator and interfaces
- `Ai.Tlbx.VoiceAssistant.Provider.OpenAi`: OpenAI Realtime API provider
- `Ai.Tlbx.VoiceAssistant.Provider.Google`: Google Gemini Live API provider
- `Ai.Tlbx.VoiceAssistant.Provider.XAi`: xAI Grok Voice Agent API provider
- `Ai.Tlbx.VoiceAssistant.Hardware.Windows`: Windows audio support (NAudio)
- `Ai.Tlbx.VoiceAssistant.Hardware.Linux`: Linux audio support (ALSA)
- `Ai.Tlbx.VoiceAssistant.Hardware.Web`: Web/Blazor audio support (AudioWorklet)
- `Ai.Tlbx.VoiceAssistant.WebUi`: Reusable Blazor UI components

Version is managed centrally in `Directory.Build.props` and auto-incremented by `publish-nuget.ps1`.

### Logging Strategy

This codebase uses a **centralized logging architecture** where all logging flows from lower layers up to the orchestrator.

**CRITICAL: DO NOT USE ILogger<T> OR Microsoft.Extensions.Logging**

All logging flows from lower layers up to the orchestrator where users configure their preferred logging approach. This pattern:
- Maintains clean architecture boundaries
- Allows user choice of logging framework
- Simplifies testing and debugging
- Prevents tight coupling to Microsoft logging

Always use `Action<LogLevel, string>` for logging delegation and forward logs up through natural architectural layers. The LogLevel enum is defined in the Models namespace with three levels: Error, Warn, and Info.

### Testing Strategy

- When testing voice behavior or tool selection, keep existing demo tool fixtures unchanged; real external integrations are a separate scope and require an explicit request.

- Automated provider contracts and controlled HTTP/WebSocket event sequences live in `Tests/ContractTests`.
- Run `pwsh -File verify-release.ps1` before publication; it builds the solution, runs .NET and browser response-control contracts plus console smoke, and verifies Native AOT. Node.js is required for the browser contracts.
- Large-tool-result work must prove that the configured reasoning backend consumes complete output and returns facts located beyond character 40000. A retained output plus an explicit failure alone does not solve the application use case. Keep GPT-Live managed delegation, client delegation, and Realtime evidence distinct.
- Real audio and browser tests complement the deterministic contracts; document unavailable gateway coverage explicitly.
- Tool discovery tests must use natural user requests without tool names, explicit tool-use hints, or task-specific delegation instructions. Verify the library derives Live routing context from registered tool metadata.
- Each platform has its own demo for validation:
  - Windows: `Demo/Ai.Tlbx.VoiceAssistant.Demo.Windows/`
  - Linux: `Demo/Ai.Tlbx.VoiceAssistant.Demo.Linux/`
  - Web: `Demo/Ai.Tlbx.VoiceAssistant.Demo.Web/`

### Important Notes

- The project targets .NET 9.0 and .NET 10.0 (multi-targeting)
- Windows components require Windows 10 or later
- Linux components require libasound (ALSA) to be installed
- Web components require HTTPS or localhost for microphone access due to browser security
- JavaScript AudioWorklet processors handle real-time audio processing (de-esser, compression, downsampling)
- Solution uses the new .slnx format (TLBX.Ai.VoiceAssistant.slnx)
- Publishing requires NUGET_API_KEY environment variable for NuGet.org uploads
