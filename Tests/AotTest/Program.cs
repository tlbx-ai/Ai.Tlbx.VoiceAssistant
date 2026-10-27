using System.Text.Json;
using System.Text.Json.Serialization;
using Ai.Tlbx.VoiceAssistant;
using Ai.Tlbx.VoiceAssistant.BuiltInTools;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.Google;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Models;
using Ai.Tlbx.VoiceAssistant.Provider.XAi;
using Ai.Tlbx.VoiceAssistant.Provider.XAi.Models;
using Ai.Tlbx.VoiceAssistant.Hardware.Web;
using Ai.Tlbx.VoiceAssistant.Telnyx;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.AddTelnyxVoice(options =>
{
    options.ApiKey = "aot-contract-no-network";
    options.PublicKey = Convert.ToBase64String(new byte[32]);
    options.PublicBaseUri = new Uri("https://aot.example.test");
});
builder.Services.AddOpenAiDirectLiveVoice();
builder.Services.AddOpenAiDirectTranslation();

// Configure JSON for minimal API
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AotTestJsonContext.Default);
});

var app = builder.Build();
app.UseWebSockets();
app.MapTelnyxVoice((call, ct) => call.Audio.Completion.WaitAsync(ct));
app.MapGet("/telnyx-smoke", () =>
{
    var native = TelnyxWebhook.Parse("{\"data\":{\"id\":\"aot\",\"event_type\":\"custom.native\",\"payload\":{\"unknown\":true}}}");
    var verifier = new TelnyxSignatureVerifier(new TelnyxOptions { PublicKey = Convert.ToBase64String(new byte[32]) });
    bool invalidRejected = !verifier.Verify(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), Convert.ToBase64String(new byte[64]), "{}");
    string streamJson = new TelnyxStreamOptions().ToRequest(new Uri("wss://aot.example.test/telnyx/media")).ToJsonString();
    using var stream = JsonDocument.Parse(streamJson);
    return new TestResults { Success = invalidRejected && native.Payload.GetProperty("unknown").GetBoolean() && stream.RootElement.GetProperty("stream_bidirectional_codec").GetString() == "L16", Results = new() { "Telnyx native JSON serialization and Ed25519 verification executed without reflection" } };
});
app.MapGet("/mistral-smoke", async () =>
{
    using var http = new HttpClient();
    await using var provider = new Ai.Tlbx.VoiceAssistant.Provider.Mistral.MistralVoiceProvider(
        new Ai.Tlbx.VoiceAssistant.Provider.Mistral.MistralAudioClient(http, "aot-contract-no-network"));
    await provider.ConnectAsync(new Ai.Tlbx.VoiceAssistant.Provider.Mistral.MistralVoiceSettings { TranscriptionOnly = true });
    await provider.ProcessAudioAsync(Convert.ToBase64String(new byte[3200]));
    await provider.DisconnectAsync();
    return new TestResults { Success = !provider.IsConnected, Results = new() { "Mistral silence/lifecycle completed without a network request" } };
});
app.MapOpenAiDirectLiveVoice();
app.MapOpenAiDirectTranslation();

app.MapGet("/translation-smoke", async () =>
{
    await using var translation = new OpenAiTranslationProvider();
    await translation.ConnectAsync(new OpenAiTranslationSettings());
    await translation.ProcessAudioAsync(Convert.ToBase64String(new byte[9600]));
    await translation.DisconnectAsync();
    return new TestResults { Success = translation.FinalOutputConfirmed, Results = new() { "Translation finalized" } };
});

// Opt-in live connection smoke; roots the GPT-Live transport and JSON paths in the native binary.
app.MapGet("/live-smoke", async () =>
{
    await using var provider = new OpenAiLiveProvider();
    await provider.ConnectAsync(new OpenAiLiveSettings());
    await provider.ProcessAudioAsync(Convert.ToBase64String(new byte[960]));
    await provider.DisconnectAsync();
    return new TestResults { Success = provider.FinalUsageConfirmed, Results = new List<string> { $"GPT-Live final usage confirmed: {provider.FinalUsageConfirmed}" } };
});

// Endpoint that exercises all the voice assistant types
app.MapGet("/test", () =>
{
    var results = new List<string>();

    // Test OpenAI provider types
    try
    {
        var openAiSettings = new OpenAiVoiceSettings
        {
            Instructions = "Test",
            Voice = AssistantVoice.Marin
        };
        results.Add($"OpenAI settings created: Voice={openAiSettings.Voice}");
    }
    catch (Exception ex)
    {
        results.Add($"OpenAI error: {ex.Message}");
    }

    // Test Google provider types
    try
    {
        var googleSettings = new GoogleVoiceSettings
        {
            Instructions = "Test",
            Voice = GoogleVoice.Puck
        };
        results.Add($"Google settings created: Voice={googleSettings.Voice}");
    }
    catch (Exception ex)
    {
        results.Add($"Google error: {ex.Message}");
    }

    // Test xAI provider types
    try
    {
        var xaiSettings = new XaiVoiceSettings
        {
            Instructions = "Test",
            Voice = XaiVoice.Eve
        };
        results.Add($"xAI settings created: Voice={xaiSettings.Voice}");
    }
    catch (Exception ex)
    {
        results.Add($"xAI error: {ex.Message}");
    }

    // Test core types
    try
    {
        var chatMessage = ChatMessage.CreateUserMessage("Hello");
        results.Add($"ChatMessage created: Role={chatMessage.Role}");
    }
    catch (Exception ex)
    {
        results.Add($"ChatMessage error: {ex.Message}");
    }

    // Test AudioDeviceInfo
    try
    {
        var deviceInfo = new AudioDeviceInfo { Id = "id", Name = "Test Mic", IsDefault = true };
        results.Add($"AudioDeviceInfo created: {deviceInfo.Name}");
    }
    catch (Exception ex)
    {
        results.Add($"AudioDeviceInfo error: {ex.Message}");
    }

    // Test built-in tools (the most problematic for AOT)
    try
    {
        var timeTool = new TimeTool();
        timeTool.SetJsonTypeInfo(ToolResultsJsonContext.Default.TimeToolArgs);
        results.Add($"TimeTool created: Name={timeTool.Name}");
    }
    catch (Exception ex)
    {
        results.Add($"TimeTool error: {ex.Message}");
    }

    try
    {
        var weatherTool = new WeatherTool();
        weatherTool.SetJsonTypeInfo(ToolResultsJsonContext.Default.WeatherToolArgs);
        results.Add($"WeatherTool created: Name={weatherTool.Name}");
    }
    catch (Exception ex)
    {
        results.Add($"WeatherTool error: {ex.Message}");
    }

    try
    {
        var calcTool = new CalculatorTool();
        calcTool.SetJsonTypeInfo(ToolResultsJsonContext.Default.CalculatorToolArgs);
        results.Add($"CalculatorTool created: Name={calcTool.Name}");
    }
    catch (Exception ex)
    {
        results.Add($"CalculatorTool error: {ex.Message}");
    }

    // Test AudioSampleRate enum
    var sampleRate = AudioSampleRate.Rate24000;
    results.Add($"AudioSampleRate: {sampleRate} = {(int)sampleRate}Hz");

    return new TestResults { Results = results, Success = !results.Any(r => r.Contains("error")) };
});

// Test tool execution with AOT-safe deserialization
app.MapPost("/test-tool", async (ToolTestRequest request) =>
{
    var results = new List<string>();

    try
    {
        IVoiceTool tool = request.ToolName.ToLower() switch
        {
            "time" => CreateTimeTool(),
            "weather" => CreateWeatherTool(),
            "calculator" => CreateCalculatorTool(),
            _ => throw new ArgumentException($"Unknown tool: {request.ToolName}")
        };

        var result = await tool.ExecuteAsync(request.ArgumentsJson);
        results.Add($"Tool {request.ToolName} executed successfully");
        results.Add($"Result: {result}");
    }
    catch (Exception ex)
    {
        results.Add($"Tool execution error: {ex.Message}");
    }

    return new TestResults { Results = results, Success = !results.Any(r => r.Contains("error")) };
});

app.Run();

// Helper methods to create tools with proper AOT setup
static TimeTool CreateTimeTool()
{
    var tool = new TimeTool();
    tool.SetJsonTypeInfo(ToolResultsJsonContext.Default.TimeToolArgs);
    return tool;
}

static WeatherTool CreateWeatherTool()
{
    var tool = new WeatherTool();
    tool.SetJsonTypeInfo(ToolResultsJsonContext.Default.WeatherToolArgs);
    return tool;
}

static CalculatorTool CreateCalculatorTool()
{
    var tool = new CalculatorTool();
    tool.SetJsonTypeInfo(ToolResultsJsonContext.Default.CalculatorToolArgs);
    return tool;
}

// Request/Response types
public record TestResults
{
    public List<string> Results { get; init; } = new();
    public bool Success { get; init; }
}

public record ToolTestRequest
{
    public string ToolName { get; init; } = "";
    public string ArgumentsJson { get; init; } = "{}";
}

// Source-generated JSON context for AOT compatibility
[JsonSerializable(typeof(TestResults))]
[JsonSerializable(typeof(ToolTestRequest))]
[JsonSerializable(typeof(List<string>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class AotTestJsonContext : JsonSerializerContext
{
}
