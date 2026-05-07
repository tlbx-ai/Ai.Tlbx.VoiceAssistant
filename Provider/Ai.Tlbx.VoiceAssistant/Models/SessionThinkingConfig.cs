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

    /// <summary>
    /// Provider-neutral reasoning effort for models that expose qualitative reasoning controls.
    /// Providers and models that do not support this should ignore it.
    /// </summary>
    public enum SessionReasoningEffort
    {
        None,
        Minimal,
        Low,
        Medium,
        High,
        XHigh
    }

    /// <summary>
    /// Helper methods for provider-specific reasoning effort mappings.
    /// </summary>
    public static class SessionReasoningEffortExtensions
    {
        public static string ToApiString(this SessionReasoningEffort effort)
        {
            return effort switch
            {
                SessionReasoningEffort.None => "none",
                SessionReasoningEffort.Minimal => "minimal",
                SessionReasoningEffort.Low => "low",
                SessionReasoningEffort.Medium => "medium",
                SessionReasoningEffort.High => "high",
                SessionReasoningEffort.XHigh => "xhigh",
                _ => throw new System.ArgumentOutOfRangeException(nameof(effort), effort, "Unsupported reasoning effort")
            };
        }
    }

    /// <summary>
    /// Provider-neutral policy for spoken bridge messages around tool calls.
    /// Providers and models that do not support this should ignore it.
    /// </summary>
    public enum ToolCallPreambleMode
    {
        /// <summary>
        /// Let the provider/model decide whether to speak before tool calls.
        /// </summary>
        ProviderDefault,

        /// <summary>
        /// Do not add spoken preambles before tool calls.
        /// </summary>
        Disabled,

        /// <summary>
        /// Speak one short bridge line before a multi-tool burst, then stay quiet until the final result.
        /// </summary>
        BeforeToolBurst,

        /// <summary>
        /// Speak a short preamble only before slow or user-visible tool calls.
        /// </summary>
        ForLongRunningTools,

        /// <summary>
        /// Speak one short preamble before every tool call.
        /// </summary>
        BeforeEveryToolCall
    }
}
