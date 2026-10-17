using System;

namespace Ai.Tlbx.VoiceAssistant.Provider.Google.Models
{
    /// <summary>
    /// Supported Google Gemini Live API model identifiers.
    /// Current defaults prefer <see cref="Gemini38Live"/>.
    /// </summary>
    public enum GoogleModel
    {
        /// <summary>Low-latency stable native audio with asynchronous function calling.</summary>
        Gemini38Live = 100,

        /// <summary>Stable native audio with background reasoning and asynchronous tools.</summary>
        Gemini38LiveExtendedThinking = 101,

        /// <summary>
        /// Previous Gemini 3.1 Flash Live preview model.
        /// Model: gemini-3.1-flash-live-preview
        /// </summary>
        Gemini31FlashLivePreview = 0,

        /// <summary>
        /// Rolling Gemini 2.5 Flash native audio Live API model.
        /// Model: gemini-2.5-flash-native-audio-latest
        /// </summary>
        Gemini25FlashNativeAudioLatest,

        /// <summary>
        /// Legacy alias retained for compatibility with older integrations.
        /// </summary>
        [Obsolete("Use Gemini38Live. This legacy alias is kept only for compatibility.")]
        GeminiLive25FlashNativeAudio,

        /// <summary>
        /// Gemini 2.5 Flash native audio model.
        /// Model: gemini-2.5-flash-native-audio-preview-12-2025
        /// </summary>
        [Obsolete("Prefer Gemini38Live for current Gemini Live work. Keep this only for compatibility checks against the older native-audio path.")]
        Gemini25FlashNativeAudio,

        /// <summary>
        /// Pinned September 2025 Gemini 2.5 Flash native audio preview.
        /// </summary>
        [Obsolete("Pinned legacy snapshot. Use Gemini31FlashLivePreview for current work.")]
        Gemini25FlashNativeAudioPreview202509,

        /// <summary>
        /// Gemini Live 2.5 Flash preview.
        /// Google shut this model down on December 9, 2025.
        /// </summary>
        [Obsolete("Google shut this model down on 2025-12-09. Use Gemini38Live.")]
        GeminiLive25Flash,

        /// <summary>
        /// Gemini 2.0 Flash Live.
        /// Google shut this model down on December 9, 2025.
        /// </summary>
        [Obsolete("Google shut this model down on 2025-12-09. Use Gemini38Live.")]
        Gemini20FlashLive001
    }

    /// <summary>
    /// Extension methods for GoogleModel enum.
    /// </summary>
    public static class GoogleModelExtensions
    {
        /// <summary>
        /// Gets the API model string for the specified model.
        /// </summary>
        /// <param name="model">The model enum value.</param>
        /// <returns>The API model string to use with Google Gemini.</returns>
#pragma warning disable CS0618
        public static string ToApiString(this GoogleModel model)
        {
            return model switch
            {
                GoogleModel.Gemini38Live => "models/gemini-3.8-live",
                GoogleModel.Gemini38LiveExtendedThinking => "models/gemini-3.8-live-extended-thinking",
                GoogleModel.Gemini31FlashLivePreview => "models/gemini-3.1-flash-live-preview",
                GoogleModel.Gemini25FlashNativeAudioLatest => "models/gemini-2.5-flash-native-audio-latest",
                GoogleModel.GeminiLive25FlashNativeAudio => "models/gemini-live-2.5-flash-native-audio",
                GoogleModel.Gemini25FlashNativeAudio => "models/gemini-2.5-flash-native-audio-preview-12-2025",
                GoogleModel.Gemini25FlashNativeAudioPreview202509 => "models/gemini-2.5-flash-native-audio-preview-09-2025",
                GoogleModel.GeminiLive25Flash => "models/gemini-live-2.5-flash-preview",
                GoogleModel.Gemini20FlashLive001 => "models/gemini-2.0-flash-live-001",
                _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported model")
            };
        }
#pragma warning restore CS0618
    }
}
