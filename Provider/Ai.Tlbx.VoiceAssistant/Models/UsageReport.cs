namespace Ai.Tlbx.VoiceAssistant.Models
{
    /// <summary>
    /// Represents token/audio usage data from a voice AI provider response.
    /// Supports both native provider metrics and estimated values.
    /// </summary>
    public sealed class UsageReport
    {
        /// <summary>
        /// Identifier of the provider that generated this report (openai, xai, google).
        /// </summary>
        public string ProviderId { get; init; } = string.Empty;

        /// <summary>
        /// Timestamp when the usage report was created.
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Number of text input tokens consumed.
        /// </summary>
        public int? InputTokens { get; init; }

        /// <summary>
        /// Number of text output tokens generated.
        /// </summary>
        public int? OutputTokens { get; init; }

        /// <summary>
        /// Number of audio input tokens consumed.
        /// </summary>
        public int? InputAudioTokens { get; init; }

        /// <summary>
        /// Number of audio output tokens generated.
        /// </summary>
        public int? OutputAudioTokens { get; init; }

        /// <summary>
        /// Number of tokens used for cache creation (OpenAI-specific billing modifier).
        /// Cache tokens are a subset of input tokens and are not added to TotalInputTokens.
        /// </summary>
        public int? CacheCreationInputTokens { get; init; }

        /// <summary>
        /// Number of cached tokens read from cache (OpenAI-specific billing modifier).
        /// Cache tokens are a subset of input tokens and are not added to TotalInputTokens.
        /// </summary>
        public int? CacheReadInputTokens { get; init; }

        /// <summary>
        /// Total input tokens including text and audio tokens.
        /// </summary>
        public int TotalInputTokens =>
            (InputTokens ?? 0) + (InputAudioTokens ?? 0);

        /// <summary>
        /// Total output tokens including text and audio.
        /// </summary>
        public int TotalOutputTokens =>
            (OutputTokens ?? 0) + (OutputAudioTokens ?? 0);

        /// <summary>
        /// Total tokens consumed (input + output).
        /// </summary>
        public int TotalTokens => TotalInputTokens + TotalOutputTokens;

        /// <summary>
        /// Indicates whether the token counts are estimated rather than native provider values.
        /// True for providers that don't return native usage data (e.g., Google Live API).
        /// </summary>
        public bool IsEstimated { get; init; }

        /// <summary>
        /// Duration of input audio used for estimation purposes.
        /// </summary>
        public TimeSpan? InputAudioDuration { get; init; }

        /// <summary>
        /// Duration of output audio used for estimation purposes.
        /// </summary>
        public TimeSpan? OutputAudioDuration { get; init; }
    }
}
