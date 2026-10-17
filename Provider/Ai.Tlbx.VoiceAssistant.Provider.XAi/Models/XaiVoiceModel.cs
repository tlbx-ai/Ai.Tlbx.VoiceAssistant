using System;

namespace Ai.Tlbx.VoiceAssistant.Provider.XAi.Models
{
    /// <summary>
    /// Supported xAI realtime voice models.
    /// </summary>
    public enum XaiVoiceModel
    {
        /// <summary>Current flagship voice model. The latest alias routes here.</summary>
        GrokVoice30 = 100,

        /// <summary>Deprecated previous generation; kept for explicitly pinned sessions.</summary>
        [Obsolete("Use GrokVoice30. xAI deprecated grok-voice-2.0.")]
        GrokVoice20 = 101,
        /// <summary>
        /// Moving alias for xAI's newest voice model. Recommended for new integrations.
        /// Pin a versioned model when deterministic production rollout is more important than automatic upgrades.
        /// </summary>
        GrokVoiceLatest = 0,

        /// <summary>
        /// Historical voice model released July 29, 2026.
        /// The grok-voice-latest alias began routing to this version on August 5, 2026.
        /// </summary>
        [Obsolete("Use GrokVoice30 or GrokVoiceLatest. This is a historical model ID.")]
        GrokVoiceThinkFast20,

        /// <summary>
        /// xAI's flagship realtime voice model for multi-step, tool-heavy voice workflows.
        /// </summary>
        [Obsolete("grok-voice-think-fast-1.0 is the previous generation. Use GrokVoiceLatest or GrokVoice30.")]
        GrokVoiceThinkFast10,

        /// <summary>
        /// Previous realtime voice model. Useful as an A/B baseline.
        /// </summary>
        [Obsolete("grok-voice-fast-1.0 is deprecated by xAI. Use GrokVoiceLatest or GrokVoice30.")]
        GrokVoiceFast10
    }

    /// <summary>
    /// Extension methods for <see cref="XaiVoiceModel"/>.
    /// </summary>
    public static class XaiVoiceModelExtensions
    {
        /// <summary>
        /// Gets the API model string for the specified xAI realtime voice model.
        /// </summary>
#pragma warning disable CS0618
        public static string ToApiString(this XaiVoiceModel model)
        {
            return model switch
            {
                XaiVoiceModel.GrokVoice30 => "grok-voice-3.0",
                XaiVoiceModel.GrokVoice20 => "grok-voice-2.0",
                XaiVoiceModel.GrokVoiceLatest => "grok-voice-latest",
                XaiVoiceModel.GrokVoiceThinkFast20 => "grok-voice-think-fast-2.0",
                XaiVoiceModel.GrokVoiceThinkFast10 => "grok-voice-think-fast-1.0",
                XaiVoiceModel.GrokVoiceFast10 => "grok-voice-fast-1.0",
                _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported xAI voice model")
            };
        }
#pragma warning restore CS0618

        /// <summary>
        /// Parses an xAI API model id into the corresponding enum value.
        /// </summary>
        public static bool TryParseApiString(string? modelId, out XaiVoiceModel model)
        {
            model = default;

            if (string.IsNullOrWhiteSpace(modelId))
            {
                return false;
            }

            foreach (var candidate in Enum.GetValues<XaiVoiceModel>())
            {
                if (string.Equals(candidate.ToApiString(), modelId, StringComparison.OrdinalIgnoreCase))
                {
                    model = candidate;
                    return true;
                }
            }

            return false;
        }
    }
}
