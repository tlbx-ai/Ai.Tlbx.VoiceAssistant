using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ai.Tlbx.VoiceAssistant.Provider.XAi.Protocol
{
    #region Tool Definitions

    public class XaiToolDefinition
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("parameters")]
        public XaiToolParameters? Parameters { get; set; }
    }

    public class XaiToolParameters
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "object";

        [JsonPropertyName("properties")]
        public Dictionary<string, XaiToolProperty>? Properties { get; set; }

        [JsonPropertyName("required")]
        public List<string>? Required { get; set; }

        [JsonPropertyName("additionalProperties")]
        public bool? AdditionalProperties { get; set; }
    }

    public class XaiToolProperty
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
        public XaiToolProperty? Items { get; set; }

        [JsonPropertyName("properties")]
        public Dictionary<string, XaiToolProperty>? Properties { get; set; }

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

    #region Audio Buffer

    public class XaiAudioBufferAppendMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "input_audio_buffer.append";

        [JsonPropertyName("audio")]
        public string? Audio { get; set; }
    }

    #endregion

    #region Response Control

    public class XaiResponseCancelMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "response.cancel";
    }

    public class XaiResponseCreateMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "response.create";
    }

    #endregion

    #region Conversation Items

    public class XaiConversationItemCreateMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; } = "conversation.item.create";

        [JsonPropertyName("item")]
        public XaiConversationItem? Item { get; set; }
    }

    public class XaiConversationItem
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("content")]
        public List<XaiContentPart>? Content { get; set; }

        [JsonPropertyName("call_id")]
        public string? CallId { get; set; }

        [JsonPropertyName("output")]
        public string? Output { get; set; }
    }

    public class XaiContentPart
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    #endregion

    #region Session Configuration

    public class XaiSessionUpdateMessage
    {
        [JsonPropertyName("event_id")]
        public string? EventId { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; } = "session.update";

        [JsonPropertyName("session")]
        public XaiSessionConfig? Session { get; set; }
    }

    public class XaiSessionConfig
    {
        [JsonPropertyName("voice")]
        public string? Voice { get; set; }

        [JsonPropertyName("instructions")]
        public string? Instructions { get; set; }

        [JsonPropertyName("tools")]
        public List<XaiToolDefinition>? Tools { get; set; }

        [JsonPropertyName("tool_choice")]
        public string? ToolChoice { get; set; }

        [JsonPropertyName("audio")]
        public XaiAudioConfig? Audio { get; set; }

        [JsonPropertyName("turn_detection")]
        public XaiTurnDetectionConfig? TurnDetection { get; set; }

        [JsonPropertyName("input_audio_transcription")]
        public XaiInputAudioTranscriptionConfig? InputAudioTranscription { get; set; }
    }

    public class XaiInputAudioTranscriptionConfig
    {
        [JsonPropertyName("language")]
        public string? Language { get; set; }
    }

    public class XaiAudioConfig
    {
        [JsonPropertyName("input")]
        public XaiAudioEndpointConfig? Input { get; set; }

        [JsonPropertyName("output")]
        public XaiAudioEndpointConfig? Output { get; set; }
    }

    public class XaiAudioEndpointConfig
    {
        [JsonPropertyName("format")]
        public XaiAudioFormatConfig? Format { get; set; }

        [JsonPropertyName("speed")]
        public double? Speed { get; set; }
    }

    public class XaiAudioFormatConfig
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("rate")]
        public int? Rate { get; set; }
    }

    public class XaiTurnDetectionConfig
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("threshold")]
        public double? Threshold { get; set; }

        [JsonPropertyName("prefix_padding_ms")]
        public int? PrefixPaddingMs { get; set; }

        [JsonPropertyName("silence_duration_ms")]
        public int? SilenceDurationMs { get; set; }

        [JsonPropertyName("create_response")]
        public bool? CreateResponse { get; set; }

        [JsonPropertyName("interrupt_response")]
        public bool? InterruptResponse { get; set; }
    }

    #endregion
}
