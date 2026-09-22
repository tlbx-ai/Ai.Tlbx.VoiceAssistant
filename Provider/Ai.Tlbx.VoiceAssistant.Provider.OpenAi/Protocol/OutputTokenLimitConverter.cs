using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Protocol;

/// <summary>The API accepts an integer or the literal string "inf".</summary>
public sealed class OutputTokenLimitConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Number
            ? reader.GetInt32().ToString(CultureInfo.InvariantCulture)
            : reader.GetString();

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit))
            writer.WriteNumberValue(limit);
        else if (value == "inf") writer.WriteStringValue(value);
        else throw new JsonException("Output token limit must be an integer or 'inf'.");
    }
}
