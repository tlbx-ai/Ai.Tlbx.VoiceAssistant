using System.Text.Json;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Microsoft.JSInterop;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;

public sealed class OpenAiDirectRealtimeVoiceProvider : IDirectBrowserVoiceProvider
{
    private const string DefaultModulePath = "./_content/Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore/voice-assistant-direct-realtime.js";

    private readonly IJSRuntime _jsRuntime;
    private readonly OpenAiDirectRealtimePreparedSessionStore _preparedSessions;
    private readonly OpenAiDirectRealtimeOptions _endpointOptions;
    private readonly string _apiKey;
    private readonly Action<LogLevel, string> _logAction;
    private readonly OpenAiDirectRealtimeBrowserOptions _browserOptions;

    private DotNetObjectReference<OpenAiDirectRealtimeVoiceProvider>? _dotNetReference;
    private IJSObjectReference? _module;
    private IJSObjectReference? _client;
    private bool _isDisposed;
    private bool _isConnected;
    private OpenAiVoiceSettings? _settings;

    public OpenAiDirectRealtimeVoiceProvider(
        IJSRuntime jsRuntime,
        OpenAiDirectRealtimePreparedSessionStore preparedSessions,
        OpenAiDirectRealtimeOptions endpointOptions,
        string? apiKey = null,
        Action<LogLevel, string>? logAction = null,
        OpenAiDirectRealtimeBrowserOptions? browserOptions = null)
    {
        _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
        _preparedSessions = preparedSessions ?? throw new ArgumentNullException(nameof(preparedSessions));
        _endpointOptions = endpointOptions ?? throw new ArgumentNullException(nameof(endpointOptions));
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException("OpenAI API key must be provided or set in OPENAI_API_KEY environment variable");
        _logAction = logAction ?? ((level, message) => { });
        _browserOptions = browserOptions ?? new OpenAiDirectRealtimeBrowserOptions();
    }

    public bool IsConnected => _isConnected;

    public AudioSampleRate RequiredInputSampleRate => AudioSampleRate.Rate24000;

    public Action<ChatMessage>? OnMessageReceived { get; set; }

    public Action<string>? OnAudioReceived { get; set; }

    public Func<TimeSpan?, Task<bool>>? WaitForPlaybackDrainAsync { get; set; }

    public Action<string>? OnStatusChanged { get; set; }

    public Action<string>? OnError { get; set; }

    public Action? OnInterruptDetected { get; set; }

    public Action<UsageReport>? OnUsageReceived { get; set; }

    public Action<string>? OnTranscriptionDelta { get; set; }

    public Action<string>? OnTranscriptionCompleted { get; set; }

    public Task ConnectAsync(IVoiceSettings settings)
    {
        return StartBrowserSessionAsync(settings);
    }

    public async Task StartBrowserSessionAsync(
        IVoiceSettings settings,
        string? microphoneDeviceId = null,
        IEnumerable<ChatMessage>? conversationHistory = null,
        CancellationToken cancellationToken = default)
    {
        if (settings is not OpenAiVoiceSettings openAiSettings)
        {
            throw new ArgumentException("Settings must be of type OpenAiVoiceSettings for OpenAI direct realtime provider", nameof(settings));
        }

        _settings = openAiSettings;
        ReportStatus("Preparing OpenAI browser session...");

        var preparedSessionId = _preparedSessions.Prepare(new OpenAiDirectRealtimePreparedSession
        {
            OpenAiApiKey = _apiKey,
            Settings = openAiSettings,
            SafetyIdentifier = _browserOptions.SafetyIdentifier,
            Channel = _browserOptions.Channel
        });

        await EnsureClientAsync(cancellationToken);

        var startConfig = new DirectRealtimeBrowserStartConfig
        {
            PreparedSessionId = preparedSessionId,
            MicrophoneId = string.IsNullOrWhiteSpace(microphoneDeviceId) ? null : microphoneDeviceId,
            Provider = "OpenAI",
            Voice = openAiSettings.Voice.ToString(),
            Speed = openAiSettings.TalkingSpeed
        };

        await _client!.InvokeVoidAsync("start", cancellationToken, startConfig);
        _isConnected = true;

        if (conversationHistory is not null)
        {
            var history = conversationHistory
                .Where(message => message.Role is ChatMessage.UserRole or ChatMessage.AssistantRole)
                .Select(message => new DirectRealtimeBrowserHistoryMessage
                {
                    Role = message.Role,
                    Content = message.Content
                })
                .Where(message => !string.IsNullOrWhiteSpace(message.Content))
                .ToList();

            if (history.Count > 0)
            {
                await _client!.InvokeVoidAsync("injectConversationHistory", cancellationToken, history);
            }
        }

        ReportStatus("Listening");
    }

    public async Task DisconnectAsync()
    {
        if (_client is null)
        {
            _isConnected = false;
            return;
        }

        try
        {
            ReportStatus("Disconnecting...");
            await _client.InvokeVoidAsync("stop");
            ReportStatus("Disconnected");
        }
        catch (JSDisconnectedException)
        {
        }
        finally
        {
            _isConnected = false;
        }
    }

    public Task UpdateSettingsAsync(IVoiceSettings settings)
    {
        if (settings is not OpenAiVoiceSettings openAiSettings)
        {
            throw new ArgumentException("Settings must be of type OpenAiVoiceSettings for OpenAI direct realtime provider", nameof(settings));
        }

        _settings = openAiSettings;
        _logAction(LogLevel.Warn, "OpenAI direct realtime settings updates require a new browser session.");
        return Task.CompletedTask;
    }

    public Task ProcessAudioAsync(string base64Audio)
    {
        return Task.CompletedTask;
    }

    public async Task SendInterruptAsync()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            await _client.InvokeVoidAsync("interrupt");
        }
        catch (JSDisconnectedException)
        {
        }
    }

    public async Task InjectConversationHistoryAsync(IEnumerable<ChatMessage> messages)
    {
        if (_client is null)
        {
            return;
        }

        var history = messages
            .Where(message => message.Role is ChatMessage.UserRole or ChatMessage.AssistantRole)
            .Select(message => new DirectRealtimeBrowserHistoryMessage
            {
                Role = message.Role,
                Content = message.Content
            })
            .Where(message => !string.IsNullOrWhiteSpace(message.Content))
            .ToList();

        if (history.Count > 0)
        {
            await _client.InvokeVoidAsync("injectConversationHistory", history);
        }
    }

    public async Task SetMicrophoneEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (_client is null)
        {
            return;
        }

        await _client.InvokeVoidAsync("setMicrophoneEnabled", cancellationToken, enabled);
    }

    [JSInvokable]
    public Task OnDirectRealtimeStatus(string status)
    {
        ReportStatus(status);
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnDirectRealtimeError(string error)
    {
        _isConnected = false;
        _logAction(LogLevel.Error, $"OpenAI direct realtime error: {error}");
        OnError?.Invoke(error);
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnDirectRealtimeChatMessage(string chatJson)
    {
        using var document = JsonDocument.Parse(chatJson);
        var root = document.RootElement;
        var role = TryGetString(root, "role") ?? "";
        var content = TryGetString(root, "content") ?? "";
        var toolName = TryGetString(root, "toolName");

        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.CompletedTask;
        }

        var message = role switch
        {
            ChatMessage.UserRole => ChatMessage.CreateUserMessage(content),
            ChatMessage.ToolRole => ChatMessage.CreateToolMessage(toolName ?? "", content),
            _ => ChatMessage.CreateAssistantMessage(content)
        };

        OnMessageReceived?.Invoke(message);

        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnDirectRealtimeDiagnostic(string diagnosticJson)
    {
        using var document = JsonDocument.Parse(diagnosticJson);
        var type = TryGetString(document.RootElement, "type") ?? "diagnostic";
        _logAction(LogLevel.Info, $"[OpenAI Direct] {type}: {diagnosticJson}");
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnDirectRealtimeUsage(string usageJson)
    {
        using var document = JsonDocument.Parse(usageJson);
        var report = OpenAiRealtimeUsageMapper.CreateUsageReport(document.RootElement);
        OnUsageReceived?.Invoke(report);
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnDirectRealtimeSpeechStarted()
    {
        OnInterruptDetected?.Invoke();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            await DisconnectAsync();
        }
        finally
        {
            if (_client is not null)
            {
                try { await _client.DisposeAsync(); } catch { }
                _client = null;
            }

            if (_module is not null)
            {
                try { await _module.DisposeAsync(); } catch { }
                _module = null;
            }

            _dotNetReference?.Dispose();
            _dotNetReference = null;
            _isDisposed = true;
        }
    }

    private async Task EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return;
        }

        _module ??= await _jsRuntime.InvokeAsync<IJSObjectReference>(
            "import",
            cancellationToken,
            _browserOptions.ModulePath ?? DefaultModulePath);

        _dotNetReference ??= DotNetObjectReference.Create(this);

        var clientOptions = new DirectRealtimeBrowserClientOptions
        {
            SessionUrl = $"{NormalizePrefix(_endpointOptions.RoutePrefix)}/session",
            Credentials = _browserOptions.Credentials
        };

        _client = await _module.InvokeAsync<IJSObjectReference>(
            "createOpenAiDirectRealtimeClient",
            cancellationToken,
            clientOptions,
            _dotNetReference);
    }

    private void ReportStatus(string status)
    {
        _logAction(LogLevel.Info, $"[OpenAI Direct] {status}");
        OnStatusChanged?.Invoke(status);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static string NormalizePrefix(string routePrefix)
    {
        if (string.IsNullOrWhiteSpace(routePrefix))
        {
            return "";
        }

        var trimmed = routePrefix.Trim().TrimEnd('/');
        return trimmed.StartsWith("/", StringComparison.Ordinal) ? trimmed : "/" + trimmed;
    }

    private sealed class DirectRealtimeBrowserStartConfig
    {
        public string PreparedSessionId { get; init; } = "";
        public string? MicrophoneId { get; init; }
        public string Provider { get; init; } = "OpenAI";
        public string? Voice { get; init; }
        public double Speed { get; init; } = 1.0;
    }

    private sealed class DirectRealtimeBrowserClientOptions
    {
        public string SessionUrl { get; init; } = "";
        public string Credentials { get; init; } = "include";
    }

    private sealed class DirectRealtimeBrowserHistoryMessage
    {
        public string Role { get; init; } = "";
        public string Content { get; init; } = "";
    }
}

public sealed class OpenAiDirectRealtimeBrowserOptions
{
    public string? ModulePath { get; init; }

    public string Credentials { get; init; } = "include";

    public string Channel { get; init; } = "voice";

    public string? SafetyIdentifier { get; init; }
}
