# Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore

ASP.NET Core integration and static browser assets for direct OpenAI Realtime WebRTC sessions.

The package lets a host application keep OpenAI API keys and voice tools on the server while sending microphone and playback audio directly between the browser and OpenAI Realtime.

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

## Connection status

The browser client reports explicit connection phases through the regular status callback, including server session preparation, control WebSocket opening, microphone permission, WebRTC offer creation, OpenAI connection, DataChannel opening, and listening state.
