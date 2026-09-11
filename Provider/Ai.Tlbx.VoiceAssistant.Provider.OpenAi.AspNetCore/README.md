# Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore

ASP.NET Core integration and static browser assets for direct OpenAI Realtime and GPT-Live WebRTC sessions.

The package keeps OpenAI API keys and voice tools on the server while sending microphone and playback audio directly between the browser and OpenAI.

## GPT-Live setup (11.0.2+)

```csharp
builder.Services.AddOpenAiDirectLiveVoice(options =>
{
    options.AuthorizeRequest = context => context.User.Identity?.IsAuthenticated == true;
});
app.MapOpenAiDirectLiveVoice();
```

Inject `OpenAiDirectLiveVoiceProvider` in an interactive Blazor component and call
`StartBrowserSessionAsync(new OpenAiLiveSettings { Instructions = "Help the caller." })`
from a user gesture. Subscribe to `OnTranscriptDelta` and `OnUsageReceived` before starting.
Call `DisconnectAsync` to confirm final usage and release browser media.

Live uses a server-side JSON SDP exchange and an authenticated sideband. Tool execution and usage
come from that trusted sideband; reflected audio is discarded. Settings and registered tools are
prepared server-side, with a short-lived one-use handshake capability. Authorize session preparation
as well as the endpoint. The existing Realtime setup below uses its own protocol and provider.

See the [GPT-Live guide](../../docs/openai-gpt-live.md) for managed/client delegation, microphone and
playback controls, history, lifecycle behavior and the `/gpt-live` demo.

## Blazor Server setup

```csharp
builder.Services.AddScoped<OpenAiDirectRealtimeVoiceProvider>();
builder.Services.AddOpenAiDirectRealtimeVoice(options =>
{
    options.AuthorizeRequest = context => context.User.Identity?.IsAuthenticated == true;
});

app.UseWebSockets();
app.MapOpenAiDirectRealtimeVoice();
```

Use `OpenAiDirectRealtimeVoiceProvider` with the normal `VoiceAssistant` orchestrator:

```csharp
var provider = serviceProvider.GetRequiredService<OpenAiDirectRealtimeVoiceProvider>();
var assistant = new VoiceAssistant(audioHardware, provider);
await assistant.StartAsync(new OpenAiVoiceSettings
{
    Instructions = "You are helpful."
});
```

For OpenAI voice sessions in Blazor Server this keeps the public `VoiceAssistant.StartAsync(settings)` workflow intact while moving microphone capture and assistant playback off the Blazor circuit. The server still mints ephemeral OpenAI client secrets and executes `IVoiceTool` calls.

## Tool preamble modes

Direct WebRTC applies non-default `ToolCallPreambleMode` values through Realtime instructions when `AppendToolCallPreambleInstructions` is `true` (the default). Those modes guide when the model emits a spoken tool bridge while preserving the low-latency remote media stream.

Set `OpenAiVoiceSettings.AppendToolCallPreambleInstructions = false` in the session factory to send `Instructions` exactly as supplied, including whitespace. The application then owns tool-call speech rules and response language; the selected `ToolCallPreambleMode` and audio/event delivery remain unchanged. Reconnect to apply new server-side settings to a Direct WebRTC session.

With augmentation enabled, `ToolCallPreambleMode.Disabled` is accepted as a strong model instruction, but it is not a deterministic audio gate. Both Direct WebRTC and the WebSocket provider forward received audio immediately to preserve low latency; applications that require strict phase filtering must enforce it outside the remote media stream.

## Connection status

The browser client reports explicit connection phases through the regular status callback, including server session preparation, control WebSocket opening, microphone permission, WebRTC offer creation, OpenAI connection, DataChannel opening, and listening state.

## Realtime usage events

After each `response.done`, the browser forwards the response ID, model ID, and OpenAI usage object to the authenticated control WebSocket as a client event with type `usage`. If input transcription is enabled, each `conversation.item.input_audio_transcription.completed` event is forwarded separately with type `transcription_usage`, its item/content identity, and the ASR usage object because OpenAI bills that model separately from the conversational Realtime response. Hosts can process both event types in `IOpenAiDirectRealtimeSessionEventSink.OnClientEventAsync` to keep an idempotent cost ledger and apply application budgets. The usage numbers originate in OpenAI's events, but the forwarding path still depends on the connected browser and should be identified as client-forwarded in audit reports.
