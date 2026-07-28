namespace Ai.Tlbx.VoiceAssistant.Models
{
    /// <summary>
    /// What triggered the session usage update.
    /// </summary>
    public enum SessionUsageUpdateTrigger
    {
        /// <summary>
        /// Provider-reported or client-measured usage was received.
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
    /// Cumulative session usage snapshot combining token, media, event, and
    /// operational duration data. Fired periodically and on usage events.
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

        /// <summary>
        /// Cumulative input audio duration measured from billable media.
        /// </summary>
        public TimeSpan TotalInputAudioDuration { get; init; }

        /// <summary>
        /// Cumulative output audio duration measured from billable media.
        /// </summary>
        public TimeSpan TotalOutputAudioDuration { get; init; }

        /// <summary>
        /// Cumulative billable text conversation-item events.
        /// </summary>
        public int TotalBillableTextInputEvents { get; init; }

        /// <summary>
        /// Cumulative image input tokens.
        /// </summary>
        public int TotalImageInputTokens { get; init; }

        /// <summary>
        /// Cumulative image output tokens.
        /// </summary>
        public int TotalImageOutputTokens { get; init; }

        /// <summary>
        /// Cumulative input tokens attributed to tool use.
        /// </summary>
        public int TotalToolUseInputTokens { get; init; }

        /// <summary>
        /// Cumulative output tokens attributed to reasoning or thoughts.
        /// </summary>
        public int TotalReasoningOutputTokens { get; init; }
    }
}
