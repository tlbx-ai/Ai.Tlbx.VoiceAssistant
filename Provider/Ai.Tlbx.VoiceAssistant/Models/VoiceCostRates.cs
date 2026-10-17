namespace Ai.Tlbx.VoiceAssistant.Models;

/// <summary>USD list prices per million tokens, minute or input event. Null means unknown, never free.
/// Supply your own schedule for negotiated rates, regional/tier pricing and custom providers.</summary>
public sealed record VoiceCostRates
{
    public decimal? TextInput { get; init; }
    public decimal? CachedTextInput { get; init; }
    public decimal? TextOutput { get; init; }
    public decimal? AudioInput { get; init; }
    public decimal? CachedAudioInput { get; init; }
    public decimal? AudioOutput { get; init; }
    public decimal? ImageInput { get; init; }
    public decimal? CachedImageInput { get; init; }
    public decimal? ImageOutput { get; init; }
    public decimal? CacheWrite { get; init; }
    public decimal? SessionMinute { get; init; }
    public decimal? InputAudioMinute { get; init; }
    public decimal? OutputAudioMinute { get; init; }
    public decimal? TextInputEvent { get; init; }
    public string? SourceUrl { get; init; }
    public string? VerifiedOn { get; init; }
}

/// <summary>Partial estimates retain known cost. Missing prices/dimensions never silently become zero.
/// Tool charges (hosted search, containers, etc.) and tax are outside model usage estimates.</summary>
public sealed record VoiceCostEstimate(decimal KnownCostUsd, bool IsComplete, string? Limitation);
