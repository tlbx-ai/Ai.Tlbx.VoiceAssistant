using System;
using System.Linq;
using System.Text.Json;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi;

public static class OpenAiRealtimeUsageMapper
{
    public static UsageReport CreateUsageReport(
        JsonElement usage,
        string? modelId = null,
        string? operationId = null,
        UsageOperationType operationType = UsageOperationType.VoiceResponse,
        TimeSpan? inputAudioDuration = null,
        TimeSpan? outputAudioDuration = null,
        UsageMeasurementSource measurementSource = UsageMeasurementSource.ProviderReported)
    {
        var inputTotal = TryGetInt32(usage, "input_tokens");
        var outputTotal = TryGetInt32(usage, "output_tokens");
        var totalTokens = TryGetInt32(usage, "total_tokens");
        var cacheCreationTokens = TryGetInt32(usage, "cache_creation_input_tokens");

        var hasInputDetails =
            usage.TryGetProperty("input_token_details", out var inputDetails) &&
            inputDetails.ValueKind == JsonValueKind.Object;
        var hasOutputDetails =
            usage.TryGetProperty("output_token_details", out var outputDetails) &&
            outputDetails.ValueKind == JsonValueKind.Object;
        var inputAudioTokens = hasInputDetails
            ? TryGetInt32(inputDetails, "audio_tokens")
            : TryGetInt32(usage, "input_audio_tokens");
        var outputAudioTokens = hasOutputDetails
            ? TryGetInt32(outputDetails, "audio_tokens")
            : TryGetInt32(usage, "output_audio_tokens");
        var inputImageTokens = hasInputDetails ? TryGetInt32(inputDetails, "image_tokens") : null;
        var outputImageTokens = hasOutputDetails ? TryGetInt32(outputDetails, "image_tokens") : null;
        var reasoningTokens = hasOutputDetails ? TryGetInt32(outputDetails, "reasoning_tokens") : null;
        var cachedTokens = hasInputDetails
            ? TryGetInt32(inputDetails, "cached_tokens") ?? TryGetInt32(usage, "cache_read_input_tokens")
            : TryGetInt32(usage, "cache_read_input_tokens");

        JsonElement cachedDetails = default;
        var hasCachedDetails = hasInputDetails &&
            inputDetails.TryGetProperty("cached_tokens_details", out cachedDetails);

        var inputTextTokens = hasInputDetails
            ? TryGetInt32(inputDetails, "text_tokens") ??
              SubtractKnownTokenDetails(inputTotal, inputAudioTokens, inputImageTokens)
            : SubtractKnownTokenDetails(inputTotal, inputAudioTokens, inputImageTokens);
        var outputTextTokens = hasOutputDetails
            ? TryGetInt32(outputDetails, "text_tokens") ??
              SubtractKnownTokenDetails(outputTotal, outputAudioTokens, outputImageTokens, reasoningTokens)
            : SubtractKnownTokenDetails(outputTotal, outputAudioTokens, outputImageTokens, reasoningTokens);

        return new UsageReport
        {
            ProviderId = "openai",
            ModelId = modelId,
            OperationId = operationId,
            OperationType = operationType,
            MeasurementSource = measurementSource,
            InputTokens = inputTextTokens,
            OutputTokens = outputTextTokens,
            InputAudioTokens = inputAudioTokens,
            OutputAudioTokens = outputAudioTokens,
            InputImageTokens = inputImageTokens,
            OutputImageTokens = outputImageTokens,
            ReasoningOutputTokens = reasoningTokens,
            CacheCreationInputTokens = cacheCreationTokens,
            CacheReadInputTokens = cachedTokens,
            CachedTextInputTokens = hasCachedDetails ? TryGetInt32(cachedDetails, "text_tokens") : null,
            CachedAudioInputTokens = hasCachedDetails ? TryGetInt32(cachedDetails, "audio_tokens") : null,
            CachedImageInputTokens = hasCachedDetails ? TryGetInt32(cachedDetails, "image_tokens") : null,
            ReportedInputTokens = inputTotal,
            ReportedOutputTokens = outputTotal,
            ReportedTotalTokens = totalTokens,
            IsEstimated = measurementSource != UsageMeasurementSource.ProviderReported,
            InputAudioDuration = inputAudioDuration,
            OutputAudioDuration = outputAudioDuration,
            RawProviderUsageJson = usage.GetRawText()
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
