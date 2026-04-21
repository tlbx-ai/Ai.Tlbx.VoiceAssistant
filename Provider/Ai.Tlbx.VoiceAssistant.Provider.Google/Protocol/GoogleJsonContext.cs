using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ai.Tlbx.VoiceAssistant.Provider.Google.Protocol
{
    [JsonSerializable(typeof(SetupMessage))]
    [JsonSerializable(typeof(Setup))]
    [JsonSerializable(typeof(ProactivityConfig))]
    [JsonSerializable(typeof(GenerationConfig))]
    [JsonSerializable(typeof(GoogleThinkingConfig))]
    [JsonSerializable(typeof(SpeechConfig))]
    [JsonSerializable(typeof(VoiceConfig))]
    [JsonSerializable(typeof(PrebuiltVoiceConfig))]
    [JsonSerializable(typeof(SystemInstruction))]
    [JsonSerializable(typeof(Part))]
    [JsonSerializable(typeof(List<Part>))]
    [JsonSerializable(typeof(InlineData))]
    [JsonSerializable(typeof(Tool))]
    [JsonSerializable(typeof(List<Tool>))]
    [JsonSerializable(typeof(FunctionDeclaration))]
    [JsonSerializable(typeof(List<FunctionDeclaration>))]
    [JsonSerializable(typeof(GoogleToolParameters))]
    [JsonSerializable(typeof(GoogleToolProperty))]
    [JsonSerializable(typeof(Dictionary<string, GoogleToolProperty>))]
    [JsonSerializable(typeof(RealtimeInputMessage))]
    [JsonSerializable(typeof(RealtimeInput))]
    [JsonSerializable(typeof(Blob))]
    [JsonSerializable(typeof(ClientContentMessage))]
    [JsonSerializable(typeof(ClientContent))]
    [JsonSerializable(typeof(Turn))]
    [JsonSerializable(typeof(List<Turn>))]
    [JsonSerializable(typeof(ToolResponseMessage))]
    [JsonSerializable(typeof(ToolResponse))]
    [JsonSerializable(typeof(FunctionResponse))]
    [JsonSerializable(typeof(List<FunctionResponse>))]
    [JsonSerializable(typeof(FunctionResponseData))]
    [JsonSerializable(typeof(ServerMessage))]
    [JsonSerializable(typeof(ServerContent))]
    [JsonSerializable(typeof(ModelTurn))]
    [JsonSerializable(typeof(ToolCall))]
    [JsonSerializable(typeof(FunctionCall))]
    [JsonSerializable(typeof(List<FunctionCall>))]
    [JsonSerializable(typeof(ToolCallCancellation))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(RealtimeInputConfig))]
    [JsonSerializable(typeof(AutomaticActivityDetection))]
    [JsonSerializable(typeof(EmptyObject))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSourceGenerationOptions(
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false)]
    public partial class GoogleJsonContext : JsonSerializerContext
    {
    }

    public class EmptyObject { }
}
