using System;

namespace Ai.Tlbx.VoiceAssistant.Provider.Google.Models
{
    /// <summary>
    /// Supported Google Gemini Live API models that have been verified to work with this library.
    /// Current defaults should prefer <see cref="Gemini31FlashLivePreview"/>.
    /// </summary>
    public enum GoogleModel
    {
        /// <summary>
        /// Current Gemini 3.1 Flash Live preview model.
        /// Model: gemini-3.1-flash-live-preview
        /// </summary>
        Gemini31FlashLivePreview,

        /// <summary>
        /// Legacy alias retained for compatibility with older integrations.
        /// </summary>
        [Obsolete("Use Gemini31FlashLivePreview. This legacy alias is kept only for compatibility.")]
        GeminiLive25FlashNativeAudio,

        /// <summary>
        /// Gemini 2.5 Flash native audio model.
        /// Model: gemini-2.5-flash-native-audio-preview-12-2025
        /// </summary>
        [Obsolete("Prefer Gemini31FlashLivePreview for current Gemini Live work. Keep this only for compatibility checks against the older native-audio path.")]
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
        [Obsolete("Google shut this model down on 2025-12-09. Use Gemini31FlashLivePreview.")]
        GeminiLive25Flash,

        /// <summary>
        /// Gemini 2.0 Flash Live.
        /// Google shut this model down on December 9, 2025.
        /// </summary>
        [Obsolete("Google shut this model down on 2025-12-09. Use Gemini31FlashLivePreview.")]
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
                GoogleModel.Gemini31FlashLivePreview => "models/gemini-3.1-flash-live-preview",
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
