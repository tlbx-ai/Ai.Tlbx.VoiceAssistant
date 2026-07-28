using System;
using System.Text.Json;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.Google;

/// <summary>
/// Maps Gemini Live API usageMetadata to the provider-neutral usage contract.
/// </summary>
public static class GoogleLiveUsageMapper
{
    public static UsageReport CreateUsageReport(JsonElement usageMetadata, string? modelId = null)
    {
        var promptTokens = TryGetInt32(usageMetadata, "promptTokenCount");
        var responseTokens =
            TryGetInt32(usageMetadata, "responseTokenCount") ??
            TryGetInt32(usageMetadata, "candidatesTokenCount");
        var toolUseTokens = TryGetInt32(usageMetadata, "toolUsePromptTokenCount");
        var thoughtTokens = TryGetInt32(usageMetadata, "thoughtsTokenCount");

        var promptDetails = GetModalityDetails(usageMetadata, "promptTokensDetails");
        var responseDetails = GetModalityDetails(usageMetadata, "responseTokensDetails");
        var cacheDetails = GetModalityDetails(usageMetadata, "cacheTokensDetails");

        return new UsageReport
        {
            ProviderId = "google",
            ModelId = modelId,
            OperationType = UsageOperationType.VoiceResponse,
            MeasurementSource = UsageMeasurementSource.ProviderReported,
            InputTokens = promptDetails.HasValues ? promptDetails.Text : promptTokens,
            InputAudioTokens = promptDetails.Audio,
            InputImageTokens = promptDetails.Image,
            OutputTokens = responseDetails.HasValues ? responseDetails.Text : responseTokens,
            OutputAudioTokens = responseDetails.Audio,
            OutputImageTokens = responseDetails.Image,
            ToolUseInputTokens = toolUseTokens,
            ReasoningOutputTokens = thoughtTokens,
            CacheReadInputTokens = TryGetInt32(usageMetadata, "cachedContentTokenCount"),
            CachedTextInputTokens = cacheDetails.Text,
            CachedAudioInputTokens = cacheDetails.Audio,
            CachedImageInputTokens = cacheDetails.Image,
            ReportedInputTokens = AddNullable(promptTokens, toolUseTokens),
            ReportedOutputTokens = AddNullable(responseTokens, thoughtTokens),
            ReportedTotalTokens = TryGetInt32(usageMetadata, "totalTokenCount"),
            IsEstimated = false,
            RawProviderUsageJson = usageMetadata.GetRawText()
        };
    }

    private static ModalityDetails GetModalityDetails(JsonElement usage, string propertyName)
    {
        if (usage.ValueKind != JsonValueKind.Object ||
            !usage.TryGetProperty(propertyName, out var details) ||
            details.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        int? text = null;
        int? audio = null;
        int? image = null;
        var hasValues = false;

        foreach (var detail in details.EnumerateArray())
        {
            if (detail.ValueKind != JsonValueKind.Object ||
                !detail.TryGetProperty("modality", out var modalityElement) ||
                modalityElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var tokenCount = TryGetInt32(detail, "tokenCount");
            if (!tokenCount.HasValue)
            {
                continue;
            }

            hasValues = true;
            switch (modalityElement.GetString()?.ToUpperInvariant())
            {
                case "TEXT":
                    text = (text ?? 0) + tokenCount.Value;
                    break;
                case "AUDIO":
                    audio = (audio ?? 0) + tokenCount.Value;
                    break;
                case "IMAGE":
                    image = (image ?? 0) + tokenCount.Value;
                    break;
            }
        }

        return new ModalityDetails(text, audio, image, hasValues);
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

    private static int? AddNullable(int? left, int? right)
    {
        return left.HasValue || right.HasValue
            ? (left ?? 0) + (right ?? 0)
            : null;
    }

    private readonly record struct ModalityDetails(
        int? Text,
        int? Audio,
        int? Image,
        bool HasValues);
}
