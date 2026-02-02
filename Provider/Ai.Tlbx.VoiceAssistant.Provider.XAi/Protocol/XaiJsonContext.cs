using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ai.Tlbx.VoiceAssistant.Provider.XAi.Protocol
{
    [JsonSerializable(typeof(XaiAudioBufferAppendMessage))]
    [JsonSerializable(typeof(XaiResponseCancelMessage))]
    [JsonSerializable(typeof(XaiResponseCreateMessage))]
    [JsonSerializable(typeof(XaiConversationItemCreateMessage))]
    [JsonSerializable(typeof(XaiConversationItem))]
    [JsonSerializable(typeof(XaiContentPart))]
    [JsonSerializable(typeof(List<XaiContentPart>))]
    [JsonSerializable(typeof(XaiSessionUpdateMessage))]
    [JsonSerializable(typeof(XaiSessionConfig))]
    [JsonSerializable(typeof(XaiAudioConfig))]
    [JsonSerializable(typeof(XaiAudioEndpointConfig))]
    [JsonSerializable(typeof(XaiAudioFormatConfig))]
    [JsonSerializable(typeof(XaiTurnDetectionConfig))]
    [JsonSerializable(typeof(XaiInputAudioTranscriptionConfig))]
    [JsonSerializable(typeof(XaiToolDefinition))]
    [JsonSerializable(typeof(XaiToolParameters))]
    [JsonSerializable(typeof(XaiToolProperty))]
    [JsonSerializable(typeof(List<XaiToolDefinition>))]
    [JsonSerializable(typeof(Dictionary<string, XaiToolProperty>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSourceGenerationOptions(
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false)]
    public partial class XaiJsonContext : JsonSerializerContext
    {
    }

    [JsonSerializable(typeof(XaiSessionUpdateMessage))]
    [JsonSerializable(typeof(XaiSessionConfig))]
    [JsonSerializable(typeof(XaiToolDefinition))]
    [JsonSerializable(typeof(XaiToolParameters))]
    [JsonSerializable(typeof(XaiToolProperty))]
    [JsonSerializable(typeof(List<XaiToolDefinition>))]
    [JsonSerializable(typeof(Dictionary<string, XaiToolProperty>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSourceGenerationOptions(
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true)]
    public partial class XaiJsonContextIndented : JsonSerializerContext
    {
    }
}
