# Ai.Tlbx.VoiceAssistant Web Demo

This is a Blazor Server demo for `Ai.Tlbx.VoiceAssistant`.

## Overview

The demo uses reusable Razor components from `Ai.Tlbx.VoiceAssistant.WebUi` and shows the library's provider switching, microphone selection, live voice sessions, realtime transcription, and tool-call flow.

## Features

### Voice Chat Functionality
- **Real-time voice interaction** with an AI assistant
- **OpenAI direct WebRTC on Blazor Server**: browser microphone/playback audio goes directly between the browser and OpenAI; the server only prepares ephemeral sessions and executes tools
- **Provider model selection** with obsolete models kept visible for manual regression checks
- Start/stop voice recording controls
- Voice selection for AI responses
- Microphone device selection
- **Dynamic Tool Selection**:
  - Enable/disable tools before starting a session
  - Select from all registered built-in tools
  - Built-in tools include:
    - TimeTool - Basic time functionality (backward compatible)
    - TimeToolWithSchema - Advanced time with timezone support
    - WeatherTool - Get weather with location and unit options
    - WeatherLookupTool - Advanced weather with time queries
    - CalculatorTool - Mathematical operations (add, subtract, multiply, divide, power, modulo)
- Chat history display with message types (user, AI, tool calls)

### Testing & Diagnostics
- Microphone testing functionality
- Real-time audio diagnostics (buffer levels, latency, etc.)
- Connection status indicators
- Error handling and display

## Architecture

The application uses:
- Blazor Server for the web framework
- OpenAI Realtime direct WebRTC for Blazor Server voice sessions
- Google Gemini Live and xAI Grok voice through the normal browser PCM hardware path
- WebAudio API via JS interop for microphone tests, transcription, and non-direct providers
- Reusable UI components from `Ai.Tlbx.VoiceAssistant.WebUi`

## Getting Started

1. Ensure you have .NET 9 SDK or later
2. Set `OPENAI_API_KEY` for OpenAI voice sessions
3. Run the application using `dotnet run`

## Component Usage

This demo showcases how to use the RCL components:

- **AiTalkControl** - Start/stop real-time voice chat
- **ChatWidget** - Display conversation history
- **MicrophoneSelect** - Device selection with permission handling
- **VoiceSelect** - AI voice selection
- **ToolSelector** - Enable/disable AI tools dynamically
- **StatusWidget** - Display connection status and errors
- **MicTestWidget** - Test microphone functionality
- **DiagnosticsWidget** - Show real-time audio diagnostics

## Implementation Notes

The demo provides a complete reference implementation showing how to:
- Initialize and configure OpenAI direct Realtime browser sessions
- Handle microphone permissions and device selection
- Manage voice chat sessions
- Dynamically enable/disable AI tools per session
- Display various message types in the chat interface
- Provide helpful status feedback to users

## Setup Requirements

1. .NET 9 SDK or later
2. OpenAI API key with access to voice API capabilities

## Environmental Variables

Set `OPENAI_API_KEY` in the environment for OpenAI sessions. Set `GOOGLE_API_KEY` (or `GEMINI_API_KEY`) or `XAI_API_KEY` when testing those providers.

## Local Development

1. Clone the repository
2. Configure API keys (see above)
3. Build and run:

```bash
dotnet build
dotnet run --project Demo/Ai.Tlbx.VoiceAssistant.Demo.Web/Ai.Tlbx.VoiceAssistant.Demo.Web.csproj
```

Run the automated OpenAI direct-WebRTC browser smoke gate:

```bash
node tools/direct-demo-browser-smoke.mjs --start-server --configuration=Release
```

## Razor Class Library Integration

This demo uses UI components from the `Ai.Tlbx.VoiceAssistant.WebUi` Razor Class Library:

- `ChatWidget` - displays conversation
- `AiTalkControl` - start/stop buttons
- `MicrophoneSelect` - device dropdown
- `VoiceSelect` - voice picker
- `StatusWidget` - connection status
- `DiagnosticsWidget` - audio buffer/latency info
- `ToastNotification` - non-blocking alerts

The RCL components depend on JavaScript interop with the Web Audio API, as implemented in `webAudioAccess.js`.

## Example Voice Commands

Try these commands to test the AI tools:

- "What's the current time?"
- "What's the weather in Paris?"
- "Tell me the weather forecast for Tokyo tomorrow"
- "What will the weather be like in London at 3pm?"
- "Give me a detailed weather report for New York"
- "Calculate 25 times 17"
- "What's 100 divided by 7?"
- "What's 2 to the power of 10?"

## Troubleshooting

- Microphone permissions must be granted by the browser
- Web Audio API requires HTTPS in production or localhost for development
- OpenAI direct WebRTC requires `app.UseWebSockets()`, `app.MapOpenAiDirectRealtimeVoice()`, and `OPENAI_API_KEY`
- Check browser console for detailed error messages 

## Large tool results (11.1)

The main voice page includes `get_large_catalog` (68,774 characters) and
`get_large_project_dossier` (137,503 characters). Both return synthetic nested JSON;
the final inspection facts appear after character 40,000. They perform no external actions.

Select `gpt-live-1` and **Application Responses backend — full tool results**.
The explicitly configured normal Responses model receives complete tool outputs;
GPT-Live receives a short factual answer for speech. **Live-managed Responses**
remains available to reproduce its separate cumulative input-buffer limit.
Realtime 2.1 can use the same tools as a comparison.

Example: “Lies den großen Bauteilkatalog und die große Projektakte mit beiden Werkzeugen.
Nenne aus beiden abschließenden Prüfvermerken den Freigabecode und die freigegebene Menge.”
Expected: catalog `KUPFER-7319`, 417; dossier `ZEDER-8426`, 863.
Debug mode displays actual tool requests and full results.

Repeatable audio integration from the repository root (requires `OPENAI_API_KEY`, incurs API cost):

```powershell
pwsh -File Tests/Run-LargeResultSmoke.ps1 -Mode client -Document both
pwsh -File Tests/Run-LargeResultSmoke.ps1 -Mode realtime -Document catalog -NoBuild
pwsh -File Tests/Run-LargeResultSmoke.ps1 -Mode managed -Document catalog -NoBuild
pwsh -File Tests/Run-LargeResultSmoke.ps1 -Mode wire-limit -NoBuild
```

`managed` passes only when the oversized result is explicitly rejected locally,
executed once, and retained completely. `wire-limit` bypasses the library guard
in a synthetic raw protocol session and expects OpenAI's `response_input_buffer_full`.
These expected failures are not successful large-result processing.
Windows can synthesize the input locally using a German SAPI voice; elsewhere
provide `-WavPath` containing mono PCM16, 24 kHz speech. Logs go to `.logs/large-results`.
Browser automation is in `Tests/Browser/large-result-smoke.js`; it checks spoken tail facts,
full result visibility, bidirectional WebRTC traffic, and media cleanup.
# Gesprächssupport

The **Gesprächssupport** tab uses OpenAI Realtime microphone input with text-only output.
Start a session and talk naturally with another person. Context hints stream above a
separate transcript/tool-call view. The prompt in `Services/ConversationSupport.cs` sets
Nordhausen, Germany as the location and asks the model to listen without joining the dialogue.

Try discussing the weather (the existing `get_weather` demo fixture should be selected
without a direct command), the Eiffel Tower (technical context), or ending the conversation
after agreeing next steps. Weather values remain simulated test data, labeled Demo-Wetter.
Both speakers share the microphone; this mode does not provide speaker identification.

Verification: `node Tests/Browser/conversation-support-contracts.mjs` from the repo root
checks browser response/tool overlap and stop isolation. The normal .NET contract suite
includes the WebSocket counterpart. `Tests/Browser/conversation-support-smoke.js` exercises
the actual web UI and OpenAI WebRTC using three injected WAV conversation clips.
