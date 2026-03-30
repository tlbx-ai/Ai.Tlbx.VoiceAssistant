# AI Real-Time Audio Web Demo

This is a demo web application showcasing the real-time speech-to-text and text-to-speech capabilities of the Ai.Tlbx.RealTimeAudio library, integrated with OpenAI's API.

## Overview

This demo application has been refactored to use reusable Razor components from the `Ai.Tlbx.RealTime.WebUi` library, offering a more maintainable and modular architecture.

## Features

### Voice Chat Functionality
- **Real-time voice interaction** with an AI assistant
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
- OpenAI API for AI processing
- WebAudio API via JS interop for client-side audio capture
- Reusable UI components from `Ai.Tlbx.RealTime.WebUi`

## Getting Started

1. Ensure you have .NET 9.0 installed
2. Configure your OpenAI API key in appsettings.json
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
- Initialize and configure the OpenAI Real-Time API access
- Handle microphone permissions and device selection
- Manage voice chat sessions
- Dynamically enable/disable AI tools per session
- Display various message types in the chat interface
- Provide helpful status feedback to users

## Setup Requirements

1. .NET 9 SDK or later
2. OpenAI API key with access to voice API capabilities

## Environmental Variables

Configure these settings in:
- `appsettings.json` OR
- User secrets (`dotnet user-secrets`) OR
- Environment variables

```json
{
  "OpenAI": {
    "ApiKey": "sk-...",
    "VoiceModel": "tts-1" 
  }
}
```

## Local Development

1. Clone the repository
2. Configure API keys (see above)
3. Build and run:

```bash
dotnet build
cd Demo/Ai.Tlbx.RealTimeAudio.Demo.Web
dotnet run
```

## Razor Class Library Integration

This demo uses UI components from the `Ai.Tlbx.RealTime.WebUi` Razor Class Library:

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
- Check browser console for detailed error messages 
