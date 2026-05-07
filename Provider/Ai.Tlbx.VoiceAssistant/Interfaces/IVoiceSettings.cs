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

        /// <summary>
        /// Optional provider-neutral reasoning effort for models that support it.
        /// Providers and models that do not support reasoning effort should ignore it.
        /// </summary>
        SessionReasoningEffort? ReasoningEffort { get; set; }

        /// <summary>
        /// Optional provider-neutral policy for spoken bridge messages around tool calls.
        /// Providers and models that do not support tool-call preambles should ignore it.
        /// </summary>
        ToolCallPreambleMode ToolCallPreambleMode { get; set; }

        /// <summary>
        /// Optional provider-neutral thinking configuration for the session.
        /// Providers that do not support thinking controls should ignore it.
        /// </summary>
        SessionThinkingConfig Thinking { get; set; }
    }
}
