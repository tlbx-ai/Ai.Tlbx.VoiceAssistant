using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Extensions;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Telnyx;

// Outbound/provision commands are explicit CLI actions, never anonymous public HTTP routes.
bool commandMode = args.FirstOrDefault() is "--provision" or "--dial";
var builder = WebApplication.CreateBuilder(commandMode ? [] : args);
string Required(string name) => builder.Configuration[name] ?? throw new InvalidOperationException($"Configure {name} in environment variables or user secrets.");
builder.Services.AddVoiceAssistant().WithProvider<OpenAiVoiceProvider>().WithTelnyx(options =>
{
    options.ApiKey = Required("TELNYX_API_KEY"); options.PublicKey = Required("TELNYX_PUBLIC_KEY");
    options.PublicBaseUri = new Uri(Required("PUBLIC_BASE_URL"));
    options.ConnectionId = builder.Configuration["TELNYX_CONNECTION_ID"];
    options.FromNumber = builder.Configuration["TELNYX_FROM_NUMBER"];
    if (builder.Configuration["TELNYX_API_BASE_URL"] is string api) options.ApiBaseUri = new(api);
});
await using var app = builder.Build();
var client = app.Services.GetRequiredService<TelnyxClient>();
if (args.FirstOrDefault() == "--provision")
{
    var result = await client.ProvisionAsync(new TelnyxProvisioningRequest
    {
        ApplicationName = builder.Configuration["TELNYX_APPLICATION_NAME"] ?? "voiceassistant-secretary",
        PhoneNumberId = Required("TELNYX_PHONE_NUMBER_ID"),
        ConnectionId = builder.Configuration["TELNYX_CONNECTION_ID"],
        OutboundVoiceProfileId = builder.Configuration["TELNYX_OUTBOUND_PROFILE_ID"],
        OutboundProfile = new JsonObject { ["whitelisted_destinations"] = new JsonArray("DE"), ["daily_spend_limit"] = "5.00", ["daily_spend_limit_enabled"] = true }
    });
    Console.WriteLine($"TELNYX_CONNECTION_ID={result.ConnectionId}");
    Console.WriteLine($"TELNYX_OUTBOUND_PROFILE_ID={result.OutboundVoiceProfileId}");
    Console.WriteLine($"TELNYX_FROM_NUMBER={result.PhoneNumber}");
    return;
}
if (args.FirstOrDefault() == "--dial")
{
    if (args.Length != 2) throw new ArgumentException("Usage: --dial +49... (the public server must already be running).");
    Console.WriteLine((await client.DialAsync(args[1])).GetRawText());
    return;
}
var host = app.Services.GetRequiredService<TelnyxVoiceHost>();
host.OnError = error => Console.Error.WriteLine($"Telephone operation failed: {error.GetType().Name}: {error.Message}");
app.UseWebSockets();
app.MapTelnyxVoice((call, ct) => call.RunAssistantAsync(new OpenAiVoiceSettings
{
    Instructions = builder.Configuration["ASSISTANT_INSTRUCTIONS"] ?? "Du bist eine KI-Telefonassistenz. Begrüße den Anrufer freundlich und sage, dass du eine KI bist. Erfasse sein Anliegen und bei Bedarf Name und Rückrufnummer. Erfinde keine Termine, Buchungen oder Zusagen. Antworte kurz und natürlich auf Deutsch."
}, ct));
Console.WriteLine($"Telnyx webhook: {app.Services.GetRequiredService<TelnyxOptions>().WebhookUri}");
await app.RunAsync();
