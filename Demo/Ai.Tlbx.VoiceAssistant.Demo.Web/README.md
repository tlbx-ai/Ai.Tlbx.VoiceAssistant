# Ai.Tlbx.VoiceAssistant Web Demo

This is a Blazor Server demo for `Ai.Tlbx.VoiceAssistant`.

## Overview

The demo uses reusable Razor components from `Ai.Tlbx.VoiceAssistant.WebUi` and shows the library's provider switching, microphone selection, live voice sessions, realtime transcription, and tool-call flow.

## Features

### Agent handoff

In the normal Voice Chat UI (`/`), select **OpenAI → GPT-Live 1 → Agent-Handoff · GPT-6.1 Sol** in the GPT-Live backend selector for continuous GPT-Live speech with GPT-6.1 Sol through Ai.Tlbx.Inference 2.6.0. The agent receives complete synthetic documents, retains tool results across follow-ups and emits per-call usage, including summary calls and failures with unknown usage. The shared ChatWidget shows the spoken conversation, agent progress, tool requests with arguments, expandable complete tool outputs and full agent answers. Speaker fragments remain grouped across interleaved progress events; scrolling up pauses automatic scrolling. The adapter consumes user fragments received during an active inference call before publishing its final answer, retaining completed tool results. The footer shows model costs and final usage. There is no separate handoff page. The core library remains independent of the inference package; applications can supply their own `ClientBackend.Handler`.

The demo uses the verified API model `gpt-live-1`. Short results (at most 500 UTF-8 bytes) return directly to Live, avoiding an extra inference call; longer results stay in full in the agent history and UI and receive a separate spoken summary. GPT-Live 1.1 and Quip were announced for ChatGPT on October 15, 2026, with API availability announced as coming soon. They are outside this demo's verified scope. The demo never silently substitutes another model. Google demo credentials prefer the dedicated `GEMINI_API_KEY`, then `GOOGLE_API_KEY`.

For a Tailscale dev loop, bind `dotnet watch --no-hot-reload` to the Tailscale HTTPS address and set `DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH=true`; otherwise the SDK's localhost browser-refresh WebSocket can fail against the Tailscale origin. Use a suitable development certificate. Direct browser acceptance is required: the tlbx reverse-proxy preview rendered this page but failed its Blazor circuit during verification, while direct HTTPS interaction worked.

Remaining verification: one synthetic-speech run timed out before a document result was observed; equivalent runs before and after it passed. Its cause is unconfirmed. The browser harness now preserves the full visible conversation, activity, usage, console errors and screenshot on failure, then attempts graceful session finalization. Do not turn a failed or ambiguous real action into an automatic replay.

Debug mode separates GPT-Live events and actual context/handoff payloads from each GPT-6.1 Sol call. Public agent text streams into the inspector before completion; each call shows request size, first-text time, completion/cancellation and usage. Complete backend results remain in agent history even when speech is shortened. Because Ai.Tlbx.Inference 2.6.0 omits plain assistant messages on its Responses path, the adapter also supplies completed works as explicit application-owned reference records. Those records survive voice reconnects and are cleared with Clear Chat; queued delegations without new user input reuse the completed result. The delegated agent identifies itself correctly and uses its own complete prior work when evaluating follow-ups.

The delegation selector offers balanced, mostly delegated and explicit/tool-driven prompt policies. These are instructions, not a numeric API threshold or a hard routing guarantee. Browser acceptance observed balanced Live sometimes answer a quality question itself; mostly-delegated mode handed the same request to the backend. Deterministic enforcement would require application-owned routing. Optional context streaming forwards short, complete draft paragraphs with `session.thinking.append`; drafts are labelled unfinished, may influence later speech and are not private reasoning. Long paragraphs stay in the UI. `session.commentary.append` requests speech and may paraphrase, so it is not a verbatim reading channel. Creative work is handed back with completion status rather than a rewritten spoken replacement.

The activity view timestamps individual inference calls, tool execution and speech-summary preparation. Failure snapshots also capture the audio-context state, WebRTC audio byte counters and a bounded event-type timeline, without storing raw event payloads. Live acceptance requires provider-confirmed final usage before it reports success.

`Tests/Browser/agent-handoff-smoke.cjs` checks desktop/mobile rendering in an isolated Playwright browser. Set `HANDOFF_LIVE_SMOKE=1`, `HANDOFF_REQUEST_WAV` and `HANDOFF_FOLLOWUP_WAV` explicitly for paid speech acceptance: read dossier tail facts, delegate a follow-up calculation, speak both results and collect final usage. `HANDOFF_URL` and `HANDOFF_ARTIFACTS` select the running demo and evidence directory. The standalone provider smoke is `dotnet run --project Tests/ContractTests -- --provider-update-live` with `PROVIDER_SMOKE_WAV` (mono PCM16/24kHz); `PROVIDER_SMOKE_FILTER=gemini-` limits it to Google models. For poem creation and a follow-up review, set `HANDOFF_POEM_SMOKE=1` and provide matching natural-speech WAVs. `HANDOFF_POLICY=delegate` selects mostly-delegated routing; `HANDOFF_FORWARD_CONTEXT=0` disables draft forwarding for the test. The harness asserts backend text is visible before completion and rejects follow-ups that deny access to the stored full work. These tests send synthetic speech and incur provider charges.

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
