namespace Ai.Tlbx.VoiceAssistant.Models
{
    /// <summary>
    /// Provider-neutral thinking configuration for a voice session.
    /// Unsupported providers can safely ignore these values.
    /// </summary>
    public sealed record SessionThinkingConfig
    {
        /// <summary>
        /// High-level thinking depth selection.
        /// Used by providers that expose qualitative thinking modes.
        /// </summary>
        public SessionThinkingLevel? Level { get; init; }

        /// <summary>
        /// Explicit thinking token budget.
        /// Used by providers that expose quantitative thinking budgets.
        /// </summary>
        public int? Budget { get; init; }

        /// <summary>
        /// Whether provider thought summaries should be included in responses when supported.
        /// </summary>
        public bool IncludeThoughts { get; init; }

        /// <summary>
        /// Returns true when at least one thinking option is explicitly configured.
        /// </summary>
        public bool IsConfigured => Level.HasValue || Budget.HasValue || IncludeThoughts;
    }

    /// <summary>
    /// Shared qualitative thinking levels across providers.
    /// </summary>
    public enum SessionThinkingLevel
    {
        Minimal,
        Low,
        Medium,
        High
    }

    /// <summary>
    /// Helper methods for provider-specific thinking mappings.
    /// </summary>
    public static class SessionThinkingLevelExtensions
    {
        public static string ToApiString(this SessionThinkingLevel level)
        {
            return level switch
            {
                SessionThinkingLevel.Minimal => "minimal",
                SessionThinkingLevel.Low => "low",
                SessionThinkingLevel.Medium => "medium",
                SessionThinkingLevel.High => "high",
                _ => throw new System.ArgumentOutOfRangeException(nameof(level), level, "Unsupported thinking level")
            };
        }
    }
}
