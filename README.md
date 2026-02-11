# AI Voice Assistant Toolkit

**Real-time voice conversations with AI in .NET — OpenAI, Google Gemini, and xAI Grok in one unified API.**

[![NuGet](https://img.shields.io/nuget/v/Ai.Tlbx.VoiceAssistant.svg?label=nuget&color=blue)](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant/)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![.NET 9 | 10](https://img.shields.io/badge/.NET-9.0_|_10.0-purple.svg)](https://dotnet.microsoft.com/)

---

## Quick Start (Blazor Server)

**1. Install packages:**
```bash
dotnet add package Ai.Tlbx.VoiceAssistant
dotnet add package Ai.Tlbx.VoiceAssistant.Provider.OpenAi   # and/or .Google, .XAi
dotnet add package Ai.Tlbx.VoiceAssistant.Hardware.Web
```

**2. Set API keys** (environment variables or pass directly to provider constructors):
```
OPENAI_API_KEY=sk-...
GOOGLE_API_KEY=AIza...
XAI_API_KEY=xai-...
```

**3. Configure services** (`Program.cs`):
```csharp
// Register audio hardware for Blazor
builder.Services.AddScoped<IAudioHardwareAccess, WebAudioAccess>();
```

**4. Create a voice page** (`Voice.razor`):
```razor
@page "/voice"
@inject IAudioHardwareAccess AudioHardware

<select @bind="_selectedProvider">
    <option value="openai">OpenAI</option>
    <option value="google">Google Gemini</option>
    <option value="xai">xAI Grok</option>
</select>

<button @onclick="Toggle">@(_assistant?.IsRecording == true ? "Stop" : "Talk")</button>

@foreach (var msg in _messages)
{
    <p><b>@msg.Role:</b> @msg.Content</p>
}

@code {
    private VoiceAssistant? _assistant;
    private List<ChatMessage> _messages = new();
    private string _selectedProvider = "openai";

    private async Task Toggle()
    {
        if (_assistant?.IsRecording == true)
        {
            await _assistant.StopAsync();
            return;
        }

        // Create provider and settings based on selection
        var (provider, settings) = _selectedProvider switch
        {
            "openai" => (
                (IVoiceProvider)new OpenAiVoiceProvider("sk-..."),
                (IVoiceSettings)new OpenAiVoiceSettings { Instructions = "You are helpful." }
            ),
            "google" => (
                (IVoiceProvider)new GoogleVoiceProvider("AIza..."),
                (IVoiceSettings)new GoogleVoiceSettings { Instructions = "You are helpful." }
            ),
            "xai" => (
                (IVoiceProvider)new XaiVoiceProvider("xai-..."),
                (IVoiceSettings)new XaiVoiceSettings { Instructions = "You are helpful." }
            ),
            _ => throw new InvalidOperationException()
        };

        _assistant = new VoiceAssistant(AudioHardware, provider);
        _assistant.OnMessageAdded = msg => InvokeAsync(() => { _messages.Add(msg); StateHasChanged(); });

        await _assistant.StartAsync(settings);
    }
}
```

**That's it.** Select a provider, talk to the AI, get voice responses back.

---

## All Packages

| Package | Purpose | NuGet |
|---------|---------|-------|
| `Ai.Tlbx.VoiceAssistant` | Core orchestrator | [![NuGet](https://img.shields.io/nuget/v/Ai.Tlbx.VoiceAssistant.svg)](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant/) |
| `...Provider.OpenAi` | OpenAI Realtime API | [![NuGet](https://img.shields.io/nuget/v/Ai.Tlbx.VoiceAssistant.Provider.OpenAi.svg)](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant.Provider.OpenAi/) |
| `...Provider.Google` | Google Gemini Live API | [![NuGet](https://img.shields.io/nuget/v/Ai.Tlbx.VoiceAssistant.Provider.Google.svg)](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant.Provider.Google/) |
| `...Provider.XAi` | xAI Grok Voice Agent API | [![NuGet](https://img.shields.io/nuget/v/Ai.Tlbx.VoiceAssistant.Provider.XAi.svg)](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant.Provider.XAi/) |
| `...Hardware.Web` | Browser audio (Blazor) | [![NuGet](https://img.shields.io/nuget/v/Ai.Tlbx.VoiceAssistant.Hardware.Web.svg)](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant.Hardware.Web/) |
| `...Hardware.Windows` | Native Windows audio | [![NuGet](https://img.shields.io/nuget/v/Ai.Tlbx.VoiceAssistant.Hardware.Windows.svg)](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant.Hardware.Windows/) |
| `...Hardware.Linux` | Native Linux audio (ALSA) | [![NuGet](https://img.shields.io/nuget/v/Ai.Tlbx.VoiceAssistant.Hardware.Linux.svg)](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant.Hardware.Linux/) |
| `...WebUi` | Pre-built Blazor components | [![NuGet](https://img.shields.io/nuget/v/Ai.Tlbx.VoiceAssistant.WebUi.svg)](https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant.WebUi/) |

---

## Switch Providers in One Line

```csharp
// OpenAI — voices: Alloy, Ash, Ballad, Coral, Echo, Sage, Shimmer, Verse, Marin, Cedar
var provider = new OpenAiVoiceProvider(apiKey);
var settings = new OpenAiVoiceSettings { Voice = AssistantVoice.Alloy };

// Google Gemini — voices: Puck, Charon, Kore, Fenrir, Aoede, Leda, Orus, Zephyr
var provider = new GoogleVoiceProvider(apiKey);
var settings = new GoogleVoiceSettings { Voice = GoogleVoice.Puck };

// xAI Grok — voices: Ara, Rex, Sal, Eve, Leo
var provider = new XaiVoiceProvider(apiKey);
var settings = new XaiVoiceSettings { Voice = XaiVoice.Ara };
```

Same `VoiceAssistant` API, same tool definitions — just swap the provider.

### Provider-Specific Settings

Each provider has its own settings class with shared and provider-specific options:

**Shared (`IVoiceSettings`):** `Instructions`, `Tools`, `TalkingSpeed`

```csharp
// OpenAI — full TalkingSpeed support (0.25–1.5), turn detection, eagerness, transcription
new OpenAiVoiceSettings
{
    Voice = AssistantVoice.Coral,
    TalkingSpeed = 1.2,
    Eagerness = Eagerness.auto,
    TurnDetection = new TurnDetection { SilenceDurationMs = 200 },
    MostLikelySpokenLanguage = "en"
};

// Google — temperature, voice activity detection, context compression
new GoogleVoiceSettings
{
    Voice = GoogleVoice.Puck,
    LanguageCode = "en",
    Temperature = 0.8,
    AutomaticContextCompression = true
};

// xAI — VAD turn detection, language hint, web/X search
new XaiVoiceSettings
{
    Voice = XaiVoice.Ara,
    InputAudioLanguage = "en",
    TurnDetection = new XaiTurnDetection { SilenceDurationMs = 200, Threshold = 0.5 },
    EnableWebSearch = true
};
```

| Setting | OpenAI | Google | xAI |
|---------|--------|--------|-----|
| TalkingSpeed | 0.25–1.5 | interface only | interface only |
| Turn detection / VAD | `TurnDetection` | `VoiceActivityDetection` | `XaiTurnDetection` |
| Language hint | `MostLikelySpokenLanguage` | `LanguageCode` | `InputAudioLanguage` |
| Context management | `AutomaticContextTruncation` | `AutomaticContextCompression` | — |
| Web search | — | — | `EnableWebSearch`, `EnableXSearch` |

---

## Tools: Just Write C#

Define tools with plain C# records. Schema is **auto-inferred** — no JSON, no manual mapping:

```csharp
[Description("Get weather for a location")]
public class WeatherTool : VoiceToolBase<WeatherTool.Args>
{
    public record Args(
        [property: Description("City name")] string Location,
        [property: Description("Temperature unit")] TemperatureUnit Unit = TemperatureUnit.Celsius
    );

    public override string Name => "get_weather";

    public override Task<string> ExecuteAsync(Args args)
    {
        return Task.FromResult(CreateSuccessResult(new { temp = 22, location = args.Location }));
    }
}

public enum TemperatureUnit { Celsius, Fahrenheit }
```

**Universal translation:** The same tool works on OpenAI, Google, and xAI. Required/optional parameters, enums, nested objects — all inferred from C# types.

Register in DI:
```csharp
builder.Services.AddTransient<IVoiceTool, WeatherTool>();
```

---

## Writing Custom Tools

### Basic Pattern

1. **Create a record for arguments** — use `[Description]` attributes for AI guidance
2. **Extend `VoiceToolBase<TArgs>`** — add `[Description]` to the class itself
3. **Implement `ExecuteAsync`** — return results via `ToolSuccessResult<T>`

```csharp
[Description("Search for products in the catalog")]
public class ProductSearchTool : VoiceToolBase<ProductSearchTool.Args>
{
    public record Args(
        [property: Description("Search query keywords")] string Query,
        [property: Description("Maximum results to return")] int MaxResults = 10,
        [property: Description("Filter by category")] string? Category = null
    );

    public override string Name => "search_products";

    public override async Task<string> ExecuteAsync(Args args)
    {
        var products = await _catalogService.SearchAsync(args.Query, args.MaxResults, args.Category);

        var result = new ToolSuccessResult<ProductSearchResult>(new ProductSearchResult
        {
            Products = products,
            TotalFound = products.Count
        });

        return JsonSerializer.Serialize(result, YourJsonContext.Default.ToolSuccessResultProductSearchResult);
    }
}
```

### Required vs Optional Parameters

- **Required**: No default value, non-nullable → AI must provide or ask user
- **Optional**: Has default value OR nullable (`string?`) → AI can omit

```csharp
public record Args(
    string RequiredParam,                    // Required - AI must provide
    string OptionalWithDefault = "default",  // Optional - has default
    string? OptionalNullable = null          // Optional - nullable
);
```

### Returning Results

Always use the provided result types for consistent AI interpretation:

```csharp
// Success with data
var result = new ToolSuccessResult<YourDataType>(data);
return JsonSerializer.Serialize(result, YourJsonContext.Default.ToolSuccessResultYourDataType);

// Error
return CreateErrorResult("Something went wrong");
```

### AOT Compatibility

For Native AOT or Blazor WebAssembly with trimming, reflection-based JSON serialization won't work. You need source-generated JSON contexts.

**Step 1: Create a JSON context for your tool types**

```csharp
[JsonSerializable(typeof(ToolSuccessResult<ProductSearchResult>))]
[JsonSerializable(typeof(ProductSearchTool.Args), TypeInfoPropertyName = "ProductSearchToolArgs")]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class MyToolsJsonContext : JsonSerializerContext
{
}
```

**Step 2: Register the type info with your tool**

```csharp
// In your DI setup or tool initialization
var tool = new ProductSearchTool();
tool.SetJsonTypeInfo(MyToolsJsonContext.Default.ProductSearchToolArgs);
```

**Step 3: Use source-generated serialization in ExecuteAsync**

```csharp
return JsonSerializer.Serialize(result, MyToolsJsonContext.Default.ToolSuccessResultProductSearchResult);
```

### AOT Checklist

| Item | Non-AOT | AOT |
|------|---------|-----|
| Args deserialization | Automatic (reflection) | Call `SetJsonTypeInfo()` |
| Result serialization | Can use `JsonSerializer.Serialize<T>()` | Must use `JsonSerializer.Serialize(value, context.TypeInfo)` |
| Enum handling | Automatic | Include enum types in `[JsonSerializable]` |
| Nested types | Automatic | Include all nested types in context |

### Dependency Injection with Tools

Tools can use constructor injection:

```csharp
public class DatabaseTool : VoiceToolBase<DatabaseTool.Args>
{
    private readonly IDbConnection _db;

    public DatabaseTool(IDbConnection db) => _db = db;

    // ...
}

// Registration
builder.Services.AddTransient<IVoiceTool, DatabaseTool>();
```

---

## Key Features

### Studio-Quality Audio Processing
The `Hardware.Web` package includes an AudioWorklet-based audio chain:
- **48kHz capture** with browser echo cancellation, noise suppression, and auto gain
- **De-esser** (high-shelf EQ) to tame sibilance before amplification
- **Compressor** (8:1 ratio) for consistent loudness across whispers and shouts
- **Anti-aliasing filter** (Butterworth LPF) before downsampling
- **Provider-specific sample rates**: 16kHz for Google, 24kHz for OpenAI/xAI

### Provider-Agnostic Architecture
Write once, run on any provider. The orchestrator handles:
- Audio format conversion (PCM 16-bit, provider-specific sample rates)
- Tool schema translation per provider
- Streaming audio playback with interruption support
- Chat history management

### Built-in Tools
- `TimeTool` — Current time in any timezone
- `WeatherTool` — Mock weather (demo)
- `CalculatorTool` — Basic math operations

### Usage Tracking

Track token consumption and session duration for billing and monitoring:

```csharp
_assistant.OnSessionUsageUpdated = update =>
{
    Console.WriteLine($"Duration: {update.LocalSessionDuration.TotalMinutes:F1} min");
    Console.WriteLine($"Tokens: {update.TotalTokens} (audio in: {update.TotalAudioInputTokens}, audio out: {update.TotalAudioOutputTokens})");
};
```

**Provider Token Reporting:**

| Provider | Token Data | Audio Tokens | Billing Model |
|----------|------------|--------------|---------------|
| OpenAI | ✅ Native | ✅ `input_audio_tokens`, `output_audio_tokens` | Per token |
| xAI | ✅ Native | ✅ Same as OpenAI (compatible API) | Per minute ($0.05/min) |
| Google | ❌ Not yet | ❌ Coming soon (per Google) | Per token |

**`SessionUsageUpdate` fires on:**
- `TokenUsageReceived` — When the provider returns token counts (OpenAI/xAI)
- `MinuteElapsed` — Every minute while session is active (checked on audio events)
- `SessionEnded` — When `StopAsync()` is called

**Important:** `LocalSessionDuration` is measured client-side and may differ slightly from provider billing due to network latency and connection establishment time. For providers like **xAI that bill by connection time**, use this as an approximation — consult the provider's usage dashboard for exact billing amounts.

---

## Native Apps (Windows/Linux)

> **Note:** Native desktop support works but is less polished than the web implementation. Good for experiments and prototypes.

```csharp
// Windows (requires Windows 10+)
dotnet add package Ai.Tlbx.VoiceAssistant.Hardware.Windows

// Linux (requires libasound2-dev)
dotnet add package Ai.Tlbx.VoiceAssistant.Hardware.Linux
```

```csharp
var hardware = new WindowsAudioHardware(); // or LinuxAudioDevice
var provider = new OpenAiVoiceProvider(apiKey);

var assistant = new VoiceAssistant(hardware, provider);
await assistant.StartAsync(settings);

Console.ReadKey(); // Talk now
await assistant.StopAsync();
```

---

## Requirements

- **.NET 9.0 or .NET 10.0**
- **API Key:** [OpenAI](https://platform.openai.com/api-keys), [Google AI Studio](https://aistudio.google.com/apikey), or [xAI](https://console.x.ai/)
- **Web:** Modern browser with microphone permission (HTTPS or localhost)
- **Windows:** Windows 10+
- **Linux:** `sudo apt-get install libasound2-dev`

---

## License

MIT — do whatever you want.

---

<p align="center">
  <a href="https://www.nuget.org/packages/Ai.Tlbx.VoiceAssistant/">NuGet</a> •
  <a href="https://github.com/AiTlbx/Ai.Tlbx.VoiceAssistant/issues">Issues</a> •
  <a href="https://github.com/AiTlbx/Ai.Tlbx.VoiceAssistant">GitHub</a>
</p>
