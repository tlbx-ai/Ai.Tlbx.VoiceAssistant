using System.Collections.Generic;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.Google.Models
{
    /// <summary>
    /// Google Gemini-specific voice assistant settings that control model behavior and configuration.
    /// </summary>
    public class GoogleVoiceSettings : IVoiceSettings
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
        /// The speed of the AI model's spoken response.
        /// WARNING: Google's Live API does not support speech rate control.
        /// This property exists only for IVoiceSettings interface compatibility.
        /// To influence speech pace, include instructions like "speak slowly" in your prompt.
        /// </summary>
        public double TalkingSpeed { get; set; } = 1.0;

        /// <summary>
        /// The Google Gemini model to use for the conversation.
        /// </summary>
        public GoogleModel Model { get; set; } = GoogleModel.Gemini25FlashNativeAudio;

        /// <summary>
        /// The voice to use for AI responses.
        /// </summary>
        public GoogleVoice Voice { get; set; } = GoogleVoice.Aoede;

        /// <summary>
        /// The language code for speech input and output (BCP-47 format).
        /// Examples: "en-US" (English), "de-DE" (German), "fr-FR" (French), "ja-JP" (Japanese)
        /// If not set, Google will attempt automatic language detection.
        /// </summary>
        public string? LanguageCode { get; set; } = null;

        /// <summary>
        /// Voice Activity Detection settings for turn detection and interruption handling.
        /// </summary>
        public VoiceActivityDetection VoiceActivityDetection { get; set; } = new();

        /// <summary>
        /// Audio transcription configuration for input and output audio.
        /// </summary>
        public AudioTranscriptionConfig TranscriptionConfig { get; set; } = new();

        /// <summary>
        /// The response modality to use.
        /// Note: Google requires setting AUDIO or TEXT explicitly - cannot have both simultaneously.
        /// </summary>
        public string ResponseModality { get; set; } = "AUDIO";

        /// <summary>
        /// Maximum number of tokens for the response.
        /// If null, uses model's default limit.
        /// </summary>
        public int? MaxTokens { get; set; }

        /// <summary>
        /// Controls automatic context window compression when token limit is approached.
        /// If true, uses sliding window strategy to manage context.
        /// </summary>
        public bool AutomaticContextCompression { get; set; } = true;

        /// <summary>
        /// Temperature for generation (0.0 to 2.0).
        /// Higher values make output more random, lower values more deterministic.
        /// </summary>
        public double Temperature { get; set; } = 1.0;

        /// <summary>
        /// Top-P (nucleus sampling) parameter (0.0 to 1.0).
        /// Controls diversity via nucleus sampling.
        /// </summary>
        public double TopP { get; set; } = 0.95;

        /// <summary>
        /// Top-K parameter for token sampling.
        /// If set, limits sampling to top K tokens.
        /// </summary>
        public int? TopK { get; set; }

        public NoiseReductionMode NoiseReduction { get; set; } = NoiseReductionMode.FarField;
    }
}
