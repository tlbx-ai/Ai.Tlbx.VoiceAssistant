using System;
using System.Linq;
using System.Text.Json;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.XAi;

/// <summary>
/// Combines optional xAI response usage with the client-measured dimensions
/// used by current Speech-to-Speech billing.
/// </summary>
public static class XaiRealtimeUsageMapper
{
    public static UsageReport CreateUsageReport(
        JsonElement? usage,
        string? modelId,
        string? operationId,
        TimeSpan? inputAudioDuration,
        TimeSpan? outputAudioDuration,
        int billableTextInputEvents)
    {
        var hasProviderUsage = usage.HasValue && usage.Value.ValueKind == JsonValueKind.Object;
        var usageValue = hasProviderUsage ? usage!.Value : default;
        var inputTotal = hasProviderUsage ? TryGetInt32(usageValue, "input_tokens") : null;
        var outputTotal = hasProviderUsage ? TryGetInt32(usageValue, "output_tokens") : null;
        var totalTokens = hasProviderUsage ? TryGetInt32(usageValue, "total_tokens") : null;

        JsonElement inputDetails = default;
        JsonElement outputDetails = default;
        var hasInputDetails = hasProviderUsage &&
            usageValue.TryGetProperty("input_token_details", out inputDetails) &&
            inputDetails.ValueKind == JsonValueKind.Object;
        var hasOutputDetails = hasProviderUsage &&
            usageValue.TryGetProperty("output_token_details", out outputDetails) &&
            outputDetails.ValueKind == JsonValueKind.Object;

        var inputAudioTokens = hasInputDetails
            ? TryGetInt32(inputDetails, "audio_tokens")
            : hasProviderUsage ? TryGetInt32(usageValue, "input_audio_tokens") : null;
        var outputAudioTokens = hasOutputDetails
            ? TryGetInt32(outputDetails, "audio_tokens")
            : hasProviderUsage ? TryGetInt32(usageValue, "output_audio_tokens") : null;
        var inputImageTokens = hasInputDetails ? TryGetInt32(inputDetails, "image_tokens") : null;
        var outputImageTokens = hasOutputDetails ? TryGetInt32(outputDetails, "image_tokens") : null;
        var reasoningTokens = hasOutputDetails ? TryGetInt32(outputDetails, "reasoning_tokens") : null;
        var hasClientUsage =
            inputAudioDuration > TimeSpan.Zero ||
            outputAudioDuration > TimeSpan.Zero ||
            billableTextInputEvents > 0;

        return new UsageReport
        {
            ProviderId = "xai",
            ModelId = modelId,
            OperationId = operationId,
            OperationType = UsageOperationType.VoiceResponse,
            MeasurementSource = hasProviderUsage && hasClientUsage
                ? UsageMeasurementSource.Mixed
                : hasProviderUsage
                    ? UsageMeasurementSource.ProviderReported
                    : UsageMeasurementSource.ClientMeasured,
            InputTokens = hasInputDetails
                ? TryGetInt32(inputDetails, "text_tokens") ??
                  SubtractKnownTokenDetails(inputTotal, inputAudioTokens, inputImageTokens)
                : SubtractKnownTokenDetails(inputTotal, inputAudioTokens, inputImageTokens),
            OutputTokens = hasOutputDetails
                ? TryGetInt32(outputDetails, "text_tokens") ??
                  SubtractKnownTokenDetails(outputTotal, outputAudioTokens, outputImageTokens, reasoningTokens)
                : SubtractKnownTokenDetails(outputTotal, outputAudioTokens, outputImageTokens, reasoningTokens),
            InputAudioTokens = inputAudioTokens,
            OutputAudioTokens = outputAudioTokens,
            InputImageTokens = inputImageTokens,
            OutputImageTokens = outputImageTokens,
            ReasoningOutputTokens = reasoningTokens,
            CacheReadInputTokens = hasInputDetails
                ? TryGetInt32(inputDetails, "cached_tokens")
                : hasProviderUsage ? TryGetInt32(usageValue, "cache_read_input_tokens") : null,
            ReportedInputTokens = inputTotal,
            ReportedOutputTokens = outputTotal,
            ReportedTotalTokens = totalTokens,
            InputAudioDuration = inputAudioDuration,
            OutputAudioDuration = outputAudioDuration,
            BillableTextInputEvents = billableTextInputEvents > 0 ? billableTextInputEvents : null,
            IsEstimated = hasClientUsage,
            RawProviderUsageJson = hasProviderUsage ? usageValue.GetRawText() : null
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

        return Math.Max(0, totalTokens.Value - knownDetails.Sum(detail => detail ?? 0));
    }
}
