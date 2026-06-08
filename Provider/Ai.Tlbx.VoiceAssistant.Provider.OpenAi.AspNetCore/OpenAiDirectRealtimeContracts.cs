using System.Text.Json;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Microsoft.AspNetCore.Http;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;

public sealed class OpenAiDirectRealtimeOptions
{
    public string RoutePrefix { get; set; } = "/api/voice/direct";

    public TimeSpan SessionTtl { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan ClientActionTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public Func<HttpContext, bool>? AuthorizeRequest { get; set; }

    public Action<LogLevel, string>? Log { get; set; }
}

public sealed class OpenAiDirectRealtimeSessionRequest
{
    public string? Mode { get; set; }

    public string? Provider { get; set; }

    public string? Voice { get; set; }

    public double? Speed { get; set; }

    public string? TaskId { get; set; }

    public string? StudentName { get; set; }

    public string? Context { get; set; }

    public string? UserRole { get; set; }
}

public sealed class OpenAiDirectRealtimeSessionSpec : IAsyncDisposable
{
    public required string OpenAiApiKey { get; init; }

    public required OpenAiVoiceSettings Settings { get; init; }

    public string? SafetyIdentifier { get; init; }

    public string Channel { get; init; } = "voice";

    public IOpenAiDirectRealtimeEventSink? EventSink { get; init; }

    public Func<ValueTask>? DisposeAsyncCallback { get; init; }

    public ValueTask DisposeAsync()
    {
        return DisposeAsyncCallback?.Invoke() ?? ValueTask.CompletedTask;
    }
}

public sealed class OpenAiDirectRealtimeSessionResponse
{
    public string Type { get; set; } = "direct_realtime_session";

    public string VoiceSessionId { get; set; } = "";

    public string ClientSecret { get; set; } = "";

    public string Model { get; set; } = "";

    public string Voice { get; set; } = "";

    public string ControlUrl { get; set; } = "";

    public string Channel { get; set; } = "voice";
}

public interface IOpenAiDirectRealtimeSessionFactory
{
    Task<OpenAiDirectRealtimeSessionSpec> CreateSessionAsync(
        HttpContext httpContext,
        OpenAiDirectRealtimeSessionRequest request,
        string voiceSessionId,
        CancellationToken cancellationToken);
}

public interface IOpenAiDirectRealtimeEventSink
{
    Task OnClientEventAsync(
        string voiceSessionId,
        OpenAiDirectRealtimeClientEvent clientEvent,
        CancellationToken cancellationToken);

    Task OnSessionEndedAsync(string voiceSessionId, CancellationToken cancellationToken);
}

public sealed class OpenAiDirectRealtimeClientEvent
{
    public string Type { get; set; } = "";

    public string? Role { get; set; }

    public string? Content { get; set; }

    public string? ToolName { get; set; }

    public string? ToolCallType { get; set; }

    public JsonElement? Data { get; set; }
}

public interface IOpenAiDirectRealtimeClientActionDispatcher
{
    Task<JsonElement> RequestClientActionAsync(
        string? voiceSessionId,
        string action,
        object? args,
        bool requiresConfirmation = false,
        CancellationToken cancellationToken = default);
}
