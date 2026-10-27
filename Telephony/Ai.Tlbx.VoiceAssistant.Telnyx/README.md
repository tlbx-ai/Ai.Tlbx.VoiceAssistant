# Ai.Tlbx.VoiceAssistant.Telnyx

Telnyx telephone transport for .NET 9/10 VoiceAssistant applications. One package provides a thin REST client, explicit provisioning, signed ASP.NET Core endpoints and isolated bidirectional PCM audio for each telephone call. AI models and tools stay in the existing VoiceAssistant providers.

## Configure a telephone assistant

The runnable sample is `Demo/Ai.Tlbx.VoiceAssistant.Demo.Telnyx`. Configure `TELNYX_API_KEY`, `TELNYX_PUBLIC_KEY`, `PUBLIC_BASE_URL`, `TELNYX_CONNECTION_ID`, `TELNYX_FROM_NUMBER` and the selected AI provider's key locally. Run the server with `dotnet run --project Demo/Ai.Tlbx.VoiceAssistant.Demo.Telnyx`. The demo also accepts explicit `--provision` (requires `TELNYX_PHONE_NUMBER_ID`) and `--dial +49...` commands. Keep the public server running when dialing from a second terminal. Country registration and a public HTTPS/WebSocket endpoint are prerequisites for real calls.

```csharp
using Ai.Tlbx.VoiceAssistant.Extensions;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Telnyx;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVoiceAssistant()
    .WithProvider<OpenAiVoiceProvider>()
    .WithTelnyx(options =>
    {
        options.ApiKey = builder.Configuration["TELNYX_API_KEY"]!;
        options.PublicKey = builder.Configuration["TELNYX_PUBLIC_KEY"]!;
        options.ConnectionId = builder.Configuration["TELNYX_CONNECTION_ID"];
        options.FromNumber = builder.Configuration["TELNYX_FROM_NUMBER"];
        options.PublicBaseUri = new Uri(builder.Configuration["PUBLIC_BASE_URL"]!);
    });
var app = builder.Build();
app.UseWebSockets();
app.MapTelnyxVoice((call, ct) => call.RunAssistantAsync(new OpenAiVoiceSettings
{
    Instructions = "Du bist eine freundliche KI-Telefonassistenz. Begrüße Anrufer, erfasse ihr Anliegen und frage nach einer Rückrufnummer. Erfinde keine Termine oder Zusagen."
}, ct));
app.Run();
```

Configure the selected AI provider's credentials separately (e.g. `OPENAI_API_KEY`). Register your existing voice tools through `AddTool<T>()`. A fresh ASP.NET request/DI scope, audio device, provider and VoiceAssistant are used for each call. `TelnyxCall` exposes caller/callee, the native start event and scoped services. The handler must observe its cancellation token.

The public server needs HTTPS and WebSockets. `PUBLIC_BASE_URL=https://voice.example.com` yields webhook `https://voice.example.com/telnyx/webhook` and media `wss://voice.example.com/telnyx/media`. Webhook and WebSocket Ed25519 signatures and timestamp freshness are checked before accepting data. The exact configured external URL is used for WebSocket signatures, so a reverse proxy cannot accidentally substitute its internal address. No anonymous HTTP endpoint for making paid calls is added.

## Provision an existing Telnyx number

```csharp
var telnyx = app.Services.GetRequiredService<TelnyxClient>();
var resources = await telnyx.ProvisionAsync(new TelnyxProvisioningRequest
{
    ApplicationName = "office-secretary",
    PhoneNumberId = "your-owned-telnyx-number-resource-id",
    OutboundProfile = new System.Text.Json.Nodes.JsonObject
    {
        ["whitelisted_destinations"] = new System.Text.Json.Nodes.JsonArray("DE"),
        ["daily_spend_limit"] = "5.00",
        ["daily_spend_limit_enabled"] = true
    }
});
```

Provisioning reuses a matching named application/profile (or explicit IDs), updates the application's webhook/outbound profile, and assigns the owned number. Persist the returned IDs and configure `ConnectionId` and `FromNumber` for the running host. It never buys a number automatically. `OrderNumbersAsync` is a separate explicit paid API operation. Account verification, number inventory and country-specific registration remain Telnyx operations; an API key cannot bypass them. Provisioning is not a transactional operation; retain returned/native resource IDs and inspect the account after a partial failure.

## Make and control calls

```csharp
var response = await telnyx.DialAsync("+49...");
string callId = response.GetProperty("data").GetProperty("call_control_id").GetString()!;
await telnyx.TransferAsync(callId, new System.Text.Json.Nodes.JsonObject { ["to"] = "+49..." });
await telnyx.HangupAsync(callId);
```

`DialAsync(JsonObject)`, `CommandAsync` and `SendAsync` expose native request fields. Responses are native `JsonElement` documents, including provider metadata. `TelnyxApiException` retains HTTP status and the complete native error response. Paid commands are not silently retried; use a stable native `command_id` for your retry policy. Calls require an active number, a Call Control application and an outbound voice profile.

## Audio and lifecycle

The built-in adapter requests L16/16 kHz and bidirectional RTP mode with target leg `self`. Received codec/rate are read from each native `start.media_format`, not assumed. L16 little-endian PCM and G.711 PCMU/PCMA are decoded into mono PCM16, continuously resampled to the AI provider's input rate. Provider output is resampled, encoded and sent in 20 ms frames; a short final frame is padded after 40 ms of idle time or an explicit drain request. Telnyx WebSocket payloads have no RTP/WAV headers. L16 little-endian follows Telnyx's own [reference bridge](https://github.com/team-telnyx/realtime-ai-demo), despite the big-endian convention used for ordinary RTP L16.

`ClearAudioQueueAsync` clears queued local/remote playback for interruptions. Playback-drain waits use Telnyx `mark` acknowledgments, not just successful socket writes. Audio queues, message size, startup time and concurrent calls are bounded; disconnect/hangup cancels the per-call application. The PCM adapter explicitly rejects other codecs; the native REST client and native media events remain accessible for custom transports. `OnNativeEvent` retains unknown/native events without rewriting their data.

The default webhook worker acknowledges verified events after placing them in a bounded **in-memory** queue and deduplicates IDs for ten minutes. It does not survive a process restart. Applications requiring durable processing can use `TelnyxSignatureVerifier`, `TelnyxWebhook.Parse` and `TelnyxVoiceHost.ProcessAsync` with their own durable queue instead of mapping the default webhook route. Observe `TelnyxVoiceHost.OnError` for failed asynchronous call commands; `OnWebhook` exposes native events.

Local contracts exercise real HTTP/WebSocket transports with signed simulated Telnyx events and a fake AI provider. Real PSTN/codec/latency acceptance requires a provisioned Telnyx account and controlled test phone; local contracts are not a real telephone call.

Reference: [Telnyx REST](https://developers.telnyx.com/docs/voice/programmable-voice/get-started), [media streaming](https://developers.telnyx.com/docs/voice/programmable-voice/media-streaming), [webhooks](https://developers.telnyx.com/docs/voice/programmable-voice/receiving-webhooks).
