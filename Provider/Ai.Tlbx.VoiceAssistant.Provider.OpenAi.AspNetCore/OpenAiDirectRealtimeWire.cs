using System.Text.Json;
using System.Text.Json.Serialization;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Protocol;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;

internal sealed class DirectClientSecretRequest
{
    [JsonPropertyName("expires_after")]
    public ExpiresAfter? ExpiresAfter { get; set; }

    [JsonPropertyName("session")]
    public SessionConfig? Session { get; set; }
}

internal sealed class DirectClientSecretResponse
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

internal sealed class DirectRealtimeControlMessage
{
    public string Type { get; set; } = "";

    public string? RequestId { get; set; }

    public string? Action { get; set; }

    public JsonElement? Args { get; set; }

    public JsonElement? Result { get; set; }

    public string? Error { get; set; }

    public bool? Declined { get; set; }

    public OpenAiDirectRealtimeClientEvent? Event { get; set; }
}

internal sealed class DirectRealtimeClientActionRequest
{
    public string Type { get; set; } = "client_action_request";

    public string RequestId { get; set; } = "";

    public string Action { get; set; } = "";

    public object? Args { get; set; }

    public bool RequiresConfirmation { get; set; }
}

internal sealed class DirectRealtimeToolResultMessage
{
    public string Type { get; set; } = "tool_result";

    public string RequestId { get; set; } = "";

    public string Result { get; set; } = "";
}

internal sealed class DirectRealtimeStatusMessage
{
    public string Type { get; set; } = "status";

    public string Status { get; set; } = "";
}

internal sealed class DirectRealtimeErrorMessage
{
    public string Type { get; set; } = "error";

    public string Message { get; set; } = "";
}

internal sealed class DirectRealtimeSessionErrorResponse
{
    public string Error { get; set; } = "";

    public int? UpstreamStatus { get; set; }
}

[JsonSerializable(typeof(OpenAiDirectRealtimeSessionRequest))]
[JsonSerializable(typeof(OpenAiDirectRealtimeSessionResponse))]
[JsonSerializable(typeof(OpenAiDirectRealtimeClientEvent))]
[JsonSerializable(typeof(DirectClientSecretRequest))]
[JsonSerializable(typeof(DirectClientSecretResponse))]
[JsonSerializable(typeof(DirectRealtimeControlMessage))]
[JsonSerializable(typeof(DirectRealtimeClientActionRequest))]
[JsonSerializable(typeof(DirectRealtimeToolResultMessage))]
[JsonSerializable(typeof(DirectRealtimeStatusMessage))]
[JsonSerializable(typeof(DirectRealtimeErrorMessage))]
[JsonSerializable(typeof(DirectRealtimeSessionErrorResponse))]
[JsonSerializable(typeof(ExpiresAfter))]
[JsonSerializable(typeof(SessionConfig))]
[JsonSerializable(typeof(TruncationConfig))]
[JsonSerializable(typeof(AudioConfig))]
[JsonSerializable(typeof(AudioInputConfig))]
[JsonSerializable(typeof(AudioInputFormatConfig))]
[JsonSerializable(typeof(AudioOutputConfig))]
[JsonSerializable(typeof(OpenAiReasoningConfig))]
[JsonSerializable(typeof(NoiseReductionConfig))]
[JsonSerializable(typeof(TranscriptionConfig))]
[JsonSerializable(typeof(TurnDetectionConfig))]
[JsonSerializable(typeof(ToolDefinition))]
[JsonSerializable(typeof(ToolParameters))]
[JsonSerializable(typeof(ToolProperty))]
[JsonSerializable(typeof(List<ToolDefinition>))]
[JsonSerializable(typeof(Dictionary<string, ToolProperty>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
internal partial class OpenAiDirectRealtimeJsonContext : JsonSerializerContext
{
}
