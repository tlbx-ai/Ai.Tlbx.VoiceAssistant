namespace Ai.Tlbx.VoiceAssistant.Models
{
    /// <summary>
    /// What triggered the session usage update.
    /// </summary>
    public enum SessionUsageUpdateTrigger
    {
        /// <summary>
        /// Token usage was received from the AI provider.
        /// </summary>
        TokenUsageReceived,

        /// <summary>
        /// A minute has elapsed since the last update.
        /// </summary>
        MinuteElapsed,

        /// <summary>
        /// The session has ended.
        /// </summary>
        SessionEnded
    }

    /// <summary>
    /// Cumulative session usage snapshot combining token usage and session duration.
    /// Fired periodically (every minute) and on token usage events.
    /// </summary>
    public sealed class SessionUsageUpdate
    {
        /// <summary>
        /// What triggered this update.
        /// </summary>
        public SessionUsageUpdateTrigger Trigger { get; init; }

        /// <summary>
        /// Session duration measured locally (client-side).
        /// Note: May differ from provider billing duration due to network latency,
        /// connection establishment time, and clock differences.
        /// For accurate billing, consult provider's usage dashboard.
        /// </summary>
        public TimeSpan LocalSessionDuration { get; init; }

        /// <summary>
        /// Cumulative text input tokens.
        /// </summary>
        public int TextInputTokens { get; init; }

        /// <summary>
        /// Cumulative text output tokens.
        /// </summary>
        public int TextOutputTokens { get; init; }

        /// <summary>
        /// Cumulative total input tokens (text + audio).
        /// </summary>
        public int TotalInputTokens { get; init; }

        /// <summary>
        /// Cumulative total output tokens (text + audio).
        /// </summary>
        public int TotalOutputTokens { get; init; }

        /// <summary>
        /// Cumulative audio input tokens.
        /// </summary>
        public int TotalAudioInputTokens { get; init; }

        /// <summary>
        /// Cumulative audio output tokens.
        /// </summary>
        public int TotalAudioOutputTokens { get; init; }

        /// <summary>
        /// Cumulative total tokens (input + output).
        /// </summary>
        public int TotalTokens { get; init; }

        /// <summary>
        /// Cumulative cached input tokens used as a billing modifier.
        /// Cache tokens are a subset of input tokens.
        /// </summary>
        public int TotalCachedInputTokens { get; init; }
    }
}
