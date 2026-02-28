using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Protocol
{
    #region Client Secret / Session Creation

    public class ClientSecretRequest
    {
        [JsonPropertyName("expires_after")]
        public ExpiresAfter? ExpiresAfter { get; set; }

        [JsonPropertyName("session")]
        public SessionSpec? Session { get; set; }
    }

    public class ExpiresAfter
    {
        [JsonPropertyName("anchor")]
        public string Anchor { get; set; } = "created_at";

        [JsonPropertyName("seconds")]
        public int Seconds { get; set; } = 600;
    }

    public class SessionSpec
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "realtime";

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("instructions")]
        public string? Instructions { get; set; }
    }

    #endregion

    #region Audio Buffer

    public class AudioBufferAppendMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "input_audio_buffer.append";

        [JsonPropertyName("audio")]
        public string? Audio { get; set; }
    }

    #endregion

    #region Response Control

    public class ResponseCancelMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "response.cancel";
    }

    public class ResponseCreateMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "response.create";
    }

    #endregion

    #region Conversation Items

    public class ConversationItemCreateMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "conversation.item.create";

        [JsonPropertyName("item")]
        public ConversationItem? Item { get; set; }
    }

    public class ConversationItem
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("content")]
        public List<ContentPart>? Content { get; set; }

        [JsonPropertyName("call_id")]
        public string? CallId { get; set; }

        [JsonPropertyName("output")]
        public string? Output { get; set; }
    }

    public class ContentPart
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    #endregion

    #region Tool Definitions

    public class ToolDefinition
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("parameters")]
        public ToolParameters? Parameters { get; set; }
    }

    public class ToolParameters
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "object";

        [JsonPropertyName("properties")]
        public Dictionary<string, ToolProperty>? Properties { get; set; }

        [JsonPropertyName("required")]
        public List<string>? Required { get; set; }

        [JsonPropertyName("additionalProperties")]
        public bool? AdditionalProperties { get; set; }
    }

    public class ToolProperty
    {
        [JsonPropertyName("type")]
        public JsonElement? Type { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("enum")]
        public List<string>? Enum { get; set; }

        [JsonPropertyName("format")]
        public string? Format { get; set; }

        [JsonPropertyName("default")]
        public JsonElement? Default { get; set; }

        [JsonPropertyName("items")]
        public ToolProperty? Items { get; set; }

        [JsonPropertyName("properties")]
        public Dictionary<string, ToolProperty>? Properties { get; set; }

        [JsonPropertyName("required")]
        public List<string>? Required { get; set; }

        [JsonPropertyName("additionalProperties")]
        public bool? AdditionalProperties { get; set; }

        [JsonPropertyName("minimum")]
        public double? Minimum { get; set; }

        [JsonPropertyName("maximum")]
        public double? Maximum { get; set; }

        [JsonPropertyName("minLength")]
        public int? MinLength { get; set; }

        [JsonPropertyName("maxLength")]
        public int? MaxLength { get; set; }

        [JsonPropertyName("pattern")]
        public string? Pattern { get; set; }
    }

    #endregion

    #region Session Configuration

    public class SessionUpdateMessage
    {
        [JsonPropertyName("event_id")]
        public string? EventId { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; } = "session.update";

        [JsonPropertyName("session")]
        public SessionConfig? Session { get; set; }
    }

    public class SessionConfig
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "realtime";

        [JsonPropertyName("output_modalities")]
        public List<string>? OutputModalities { get; set; }

        [JsonPropertyName("instructions")]
        public string? Instructions { get; set; }

        [JsonPropertyName("max_output_tokens")]
        public string? MaxOutputTokens { get; set; }

        [JsonPropertyName("truncation")]
        public TruncationConfig? Truncation { get; set; }

        [JsonPropertyName("tool_choice")]
        public string? ToolChoice { get; set; }

        [JsonPropertyName("tools")]
        public List<ToolDefinition>? Tools { get; set; }

        [JsonPropertyName("audio")]
        public AudioConfig? Audio { get; set; }
    }

    public class TruncationConfig
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("retention_ratio")]
        public double? RetentionRatio { get; set; }
    }

    public class AudioConfig
    {
        [JsonPropertyName("input")]
        public AudioInputConfig? Input { get; set; }

        [JsonPropertyName("output")]
        public AudioOutputConfig? Output { get; set; }
    }

    public class AudioInputConfig
    {
        [JsonPropertyName("noise_reduction")]
        public NoiseReductionConfig? NoiseReduction { get; set; }

        [JsonPropertyName("transcription")]
        public TranscriptionConfig? Transcription { get; set; }

        [JsonPropertyName("turn_detection")]
        public TurnDetectionConfig? TurnDetection { get; set; }
    }

    public class NoiseReductionConfig
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }
    }

    public class TranscriptionConfig
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }
    }

    public class TurnDetectionConfig
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("threshold")]
        public double? Threshold { get; set; }

        [JsonPropertyName("prefix_padding_ms")]
        public int? PrefixPaddingMs { get; set; }

        [JsonPropertyName("silence_duration_ms")]
        public int? SilenceDurationMs { get; set; }

        [JsonPropertyName("idle_timeout_ms")]
        public int? IdleTimeoutMs { get; set; }

        [JsonPropertyName("create_response")]
        public bool? CreateResponse { get; set; }

        [JsonPropertyName("interrupt_response")]
        public bool? InterruptResponse { get; set; }
    }

    public class AudioOutputConfig
    {
        [JsonPropertyName("speed")]
        public double? Speed { get; set; }

        [JsonPropertyName("voice")]
        public string? Voice { get; set; }
    }

    #endregion

    #region Transcription Session

    public class TranscriptionSessionUpdateMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "transcription_session.update";

        [JsonPropertyName("session")]
        public TranscriptionSessionConfig? Session { get; set; }
    }

    public class TranscriptionSessionConfig
    {
        [JsonPropertyName("input_audio_format")]
        public string InputAudioFormat { get; set; } = "pcm16";

        [JsonPropertyName("input_audio_transcription")]
        public TranscriptionConfig? InputAudioTranscription { get; set; }

        [JsonPropertyName("turn_detection")]
        public TurnDetectionConfig? TurnDetection { get; set; }

        [JsonPropertyName("input_audio_noise_reduction")]
        public NoiseReductionConfig? InputAudioNoiseReduction { get; set; }
    }

    #endregion
}
