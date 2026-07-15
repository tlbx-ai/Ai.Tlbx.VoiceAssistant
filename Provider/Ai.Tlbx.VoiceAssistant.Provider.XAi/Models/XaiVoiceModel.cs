using System;

namespace Ai.Tlbx.VoiceAssistant.Provider.XAi.Models
{
    /// <summary>
    /// Supported xAI realtime voice models.
    /// </summary>
    public enum XaiVoiceModel
    {
        /// <summary>
        /// Moving alias for xAI's newest voice model. Recommended for new integrations.
        /// Pin a versioned model when deterministic production rollout is more important than automatic upgrades.
        /// </summary>
        GrokVoiceLatest,

        /// <summary>
        /// xAI's flagship realtime voice model for multi-step, tool-heavy voice workflows.
        /// </summary>
        GrokVoiceThinkFast10,

        /// <summary>
        /// Previous realtime voice model. Useful as an A/B baseline.
        /// </summary>
        [Obsolete("grok-voice-fast-1.0 is deprecated by xAI. Use GrokVoiceLatest or GrokVoiceThinkFast10.")]
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
                XaiVoiceModel.GrokVoiceLatest => "grok-voice-latest",
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
