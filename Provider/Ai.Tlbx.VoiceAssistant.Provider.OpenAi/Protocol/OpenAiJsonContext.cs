using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Protocol
{
    [JsonSerializable(typeof(ClientSecretRequest))]
    [JsonSerializable(typeof(ExpiresAfter))]
    [JsonSerializable(typeof(SessionSpec))]
    [JsonSerializable(typeof(AudioBufferAppendMessage))]
    [JsonSerializable(typeof(ResponseCancelMessage))]
    [JsonSerializable(typeof(ResponseCreateMessage))]
    [JsonSerializable(typeof(ConversationItemCreateMessage))]
    [JsonSerializable(typeof(ConversationItem))]
    [JsonSerializable(typeof(ContentPart))]
    [JsonSerializable(typeof(List<ContentPart>))]
    [JsonSerializable(typeof(SessionUpdateMessage))]
    [JsonSerializable(typeof(SessionConfig))]
    [JsonSerializable(typeof(TruncationConfig))]
    [JsonSerializable(typeof(AudioConfig))]
    [JsonSerializable(typeof(AudioInputConfig))]
    [JsonSerializable(typeof(AudioOutputConfig))]
    [JsonSerializable(typeof(NoiseReductionConfig))]
    [JsonSerializable(typeof(TranscriptionConfig))]
    [JsonSerializable(typeof(TurnDetectionConfig))]
    [JsonSerializable(typeof(ToolDefinition))]
    [JsonSerializable(typeof(ToolParameters))]
    [JsonSerializable(typeof(ToolProperty))]
    [JsonSerializable(typeof(List<ToolDefinition>))]
    [JsonSerializable(typeof(Dictionary<string, ToolProperty>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(TranscriptionSessionUpdateMessage))]
    [JsonSerializable(typeof(TranscriptionSessionConfig))]
    [JsonSourceGenerationOptions(
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false)]
    public partial class OpenAiJsonContext : JsonSerializerContext
    {
    }

    [JsonSerializable(typeof(SessionUpdateMessage))]
    [JsonSerializable(typeof(SessionConfig))]
    [JsonSerializable(typeof(ToolDefinition))]
    [JsonSerializable(typeof(ToolParameters))]
    [JsonSerializable(typeof(ToolProperty))]
    [JsonSerializable(typeof(List<ToolDefinition>))]
    [JsonSerializable(typeof(Dictionary<string, ToolProperty>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSourceGenerationOptions(
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true)]
    public partial class OpenAiJsonContextIndented : JsonSerializerContext
    {
    }
}
