using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Interfaces
{
    /// <summary>
    /// Base interface for provider-specific voice assistant settings.
    /// Each provider (OpenAI, Google, xAI) implements this with their specific options.
    /// </summary>
    public interface IVoiceSettings
    {
        /// <summary>
        /// Instructions for the AI assistant's behavior and personality.
        /// </summary>
        string Instructions { get; set; }

        /// <summary>
        /// List of tools available to the AI assistant.
        /// </summary>
        List<IVoiceTool> Tools { get; set; }

        /// <summary>
        /// The speed of the AI model's spoken response.
        /// Typical range is 0.25 to 1.5, where 1.0 is normal speed.
        /// </summary>
        double TalkingSpeed { get; set; }

        /// <summary>
        /// Noise reduction optimization mode. NearField for close-mic/headset,
        /// FarField for speaker/room setups. Currently only supported by OpenAI.
        /// </summary>
        NoiseReductionMode NoiseReduction { get; set; }
    }
}