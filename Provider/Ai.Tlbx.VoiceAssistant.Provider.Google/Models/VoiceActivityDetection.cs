namespace Ai.Tlbx.VoiceAssistant.Provider.Google.Models
{
    /// <summary>
    /// Voice Activity Detection configuration for turn detection and interruption handling.
    /// </summary>
    public class VoiceActivityDetection
    {
        /// <summary>
        /// Start-of-speech detection sensitivity. HIGH for reliable detection, LOW to reduce false positives.
        /// For barge-in/interruption, HIGH is recommended to detect user speech quickly.
        /// </summary>
        public SpeechSensitivity StartOfSpeechSensitivity { get; set; } = SpeechSensitivity.HIGH;

        /// <summary>
        /// End-of-speech detection sensitivity. LOW requires longer pause to detect end (more tolerant).
        /// HIGH detects end-of-speech faster (shorter pause triggers it).
        /// </summary>
        public SpeechSensitivity EndOfSpeechSensitivity { get; set; } = SpeechSensitivity.LOW;

        /// <summary>
        /// Audio buffering duration before confirming speech start (milliseconds).
        /// Lower values = faster speech detection but more false positives.
        /// </summary>
        public int PrefixPaddingMs { get; set; } = 20;

        /// <summary>
        /// Required silence duration before ending speech detection (milliseconds).
        /// Lower values = faster turn-taking but may cut off speech.
        /// </summary>
        public int SilenceDurationMs { get; set; } = 100;

        /// <summary>
        /// How user activity affects ongoing AI generation (interruption behavior).
        /// START_OF_ACTIVITY_INTERRUPTS enables barge-in (user can interrupt AI).
        /// </summary>
        public ActivityHandling ActivityHandling { get; set; } = ActivityHandling.START_OF_ACTIVITY_INTERRUPTS;

        /// <summary>
        /// Enable automatic VAD.
        /// The current Google provider implementation requires this to remain true.
        /// </summary>
        public bool AutomaticDetection { get; set; } = true;
    }

    public enum SpeechSensitivity
    {
        HIGH,
        MEDIUM,
        LOW
    }

    public enum ActivityHandling
    {
        /// <summary>
        /// Enable barge-in: user can interrupt AI responses.
        /// </summary>
        START_OF_ACTIVITY_INTERRUPTS,

        /// <summary>
        /// Disable barge-in: AI completes responses without interruption.
        /// </summary>
        NO_INTERRUPTION
    }

    public static class VoiceActivityDetectionExtensions
    {
        public static string ToApiString(this SpeechSensitivity sensitivity, bool isStartOfSpeech)
        {
            return $"{(isStartOfSpeech ? "START" : "END")}_SENSITIVITY_{sensitivity}";
        }

        public static string ToApiString(this ActivityHandling handling)
        {
            return handling.ToString();
        }
    }
}
