using System;
using System.Linq;
using System.Text.Json;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi;

public static class OpenAiRealtimeUsageMapper
{
    public static UsageReport CreateUsageReport(JsonElement usage)
    {
        var inputTotal = TryGetInt32(usage, "input_tokens");
        var outputTotal = TryGetInt32(usage, "output_tokens");
        var cacheCreationTokens = TryGetInt32(usage, "cache_creation_input_tokens");

        var hasInputDetails = usage.TryGetProperty("input_token_details", out var inputDetails);
        var hasOutputDetails = usage.TryGetProperty("output_token_details", out var outputDetails);

        if (hasInputDetails || hasOutputDetails)
        {
            var inputAudioTokens = TryGetInt32(inputDetails, "audio_tokens");
            var outputAudioTokens = TryGetInt32(outputDetails, "audio_tokens");
            var cachedTokens = TryGetInt32(inputDetails, "cached_tokens") ?? TryGetInt32(usage, "cache_read_input_tokens");

            var inputTextTokens = TryGetInt32(inputDetails, "text_tokens") ??
                SubtractKnownTokenDetails(inputTotal, inputAudioTokens, TryGetInt32(inputDetails, "image_tokens"));

            var outputTextTokens = TryGetInt32(outputDetails, "text_tokens") ??
                SubtractKnownTokenDetails(outputTotal, outputAudioTokens);

            return new UsageReport
            {
                ProviderId = "openai",
                InputTokens = inputTextTokens,
                OutputTokens = outputTextTokens,
                InputAudioTokens = inputAudioTokens,
                OutputAudioTokens = outputAudioTokens,
                CacheCreationInputTokens = cacheCreationTokens,
                CacheReadInputTokens = cachedTokens,
                IsEstimated = false
            };
        }

        return new UsageReport
        {
            ProviderId = "openai",
            InputTokens = inputTotal,
            OutputTokens = outputTotal,
            InputAudioTokens = TryGetInt32(usage, "input_audio_tokens"),
            OutputAudioTokens = TryGetInt32(usage, "output_audio_tokens"),
            CacheCreationInputTokens = cacheCreationTokens,
            CacheReadInputTokens = TryGetInt32(usage, "cache_read_input_tokens"),
            IsEstimated = false
        };
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return property.TryGetInt32(out var value) ? value : null;
    }

    private static int? SubtractKnownTokenDetails(int? totalTokens, params int?[] knownDetails)
    {
        if (!totalTokens.HasValue)
        {
            return null;
        }

        var value = totalTokens.Value - knownDetails.Sum(detail => detail ?? 0);
        return Math.Max(0, value);
    }
}
