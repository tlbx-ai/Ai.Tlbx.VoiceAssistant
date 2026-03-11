using System.Collections.Generic;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.XAi.Models
{
    /// <summary>
    /// xAI-specific voice assistant settings that control behavior and configuration.
    /// </summary>
    public class XaiVoiceSettings : IVoiceSettings
    {
        /// <summary>
        /// Instructions for the AI assistant's behavior and personality.
        /// </summary>
        public string Instructions { get; set; } = "You are a helpful assistant.";

        /// <summary>
        /// List of tools available to the AI assistant.
        /// </summary>
        public List<IVoiceTool> Tools { get; set; } = new();

        /// <summary>
        /// The voice to use for AI responses.
        /// Available: Ara (default), Rex, Sal, Eve, Leo
        /// </summary>
        public XaiVoice Voice { get; set; } = XaiVoice.Ara;

        /// <summary>
        /// The speed of the AI model's spoken response.
        /// Note: xAI Voice Agent does not currently expose speech-rate control.
        /// This property is retained for IVoiceSettings compatibility and is ignored by the provider.
        /// </summary>
        public double TalkingSpeed { get; set; } = 1.0;

        /// <summary>
        /// Audio sample rate in Hz.
        /// The current toolkit implementation supports 24000 Hz only.
        /// </summary>
        public int AudioSampleRate { get; set; } = 24000;

        /// <summary>
        /// Audio format type.
        /// The current toolkit implementation supports "audio/pcm" only.
        /// </summary>
        public string AudioFormatType { get; set; } = "audio/pcm";

        /// <summary>
        /// Turn detection settings for conversation flow.
        /// The current toolkit implementation requires xAI server VAD and does not support null/manual turn control.
        /// </summary>
        public XaiTurnDetection? TurnDetection { get; set; } = new();

        /// <summary>
        /// Enable xAI's built-in web search tool for real-time internet search.
        /// </summary>
        public bool EnableWebSearch { get; set; } = false;

        /// <summary>
        /// Enable xAI's built-in X (Twitter) search tool for posts, trends, and discussions.
        /// </summary>
        public bool EnableXSearch { get; set; } = false;

        /// <summary>
        /// ISO-639-1 language code hint for input audio recognition (e.g. "de", "en").
        /// Improves accuracy and latency when the expected language is known.
        /// </summary>
        public string? InputAudioLanguage { get; set; }

        public NoiseReductionMode NoiseReduction { get; set; } = NoiseReductionMode.FarField;
    }

    /// <summary>
    /// Turn detection configuration for conversation management.
    /// </summary>
    public class XaiTurnDetection
    {
        public string Type { get; set; } = "server_vad";
        public double Threshold { get; set; } = 0.5;
        public int PrefixPaddingMs { get; set; } = 300;
        public int SilenceDurationMs { get; set; } = 200;
        public bool CreateResponse { get; set; } = true;
        public bool InterruptResponse { get; set; } = true;
    }
}
