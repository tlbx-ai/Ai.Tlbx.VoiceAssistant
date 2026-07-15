using System.Text.Json;
using System.Text.Json.Serialization;
using Ai.Tlbx.VoiceAssistant;
using Ai.Tlbx.VoiceAssistant.BuiltInTools;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.Google;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Models;
using Ai.Tlbx.VoiceAssistant.Provider.XAi;
using Ai.Tlbx.VoiceAssistant.Provider.XAi.Models;
using Ai.Tlbx.VoiceAssistant.Hardware.Web;

var builder = WebApplication.CreateSlimBuilder(args);

// Configure JSON for minimal API
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AotTestJsonContext.Default);
});

var app = builder.Build();

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
