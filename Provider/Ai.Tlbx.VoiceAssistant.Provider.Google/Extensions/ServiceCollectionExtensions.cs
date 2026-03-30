using System;
using System.Linq;
using Ai.Tlbx.VoiceAssistant.Configuration;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Ai.Tlbx.VoiceAssistant.Provider.Google.Extensions
{
    /// <summary>
    /// Extension methods for configuring Google Gemini voice provider services in dependency injection.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the Google Gemini voice provider to the voice assistant configuration.
        /// </summary>
        /// <param name="builder">The voice assistant builder.</param>
        /// <param name="apiKey">Optional Google API key. If not provided, will use GOOGLE_API_KEY environment variable.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public static VoiceAssistantBuilder WithGoogle(this VoiceAssistantBuilder builder, string? apiKey = null)
        {
            builder.Services.AddScoped<IVoiceProvider>(provider =>
            {
                var logAction = provider.GetService<Action<LogLevel, string>>();
                return new GoogleVoiceProvider(apiKey, logAction);
            });

            return builder;
        }

        /// <summary>
        /// Creates default Google Gemini voice settings for voice assistant configuration.
        /// </summary>
        /// <param name="instructions">Custom instructions for the AI assistant.</param>
        /// <param name="voice">The voice to use for responses.</param>
        /// <param name="languageCode">BCP-47 language code (e.g., "de-DE" for German, "en-US" for English). If null, Google will auto-detect.</param>
        /// <returns>Configured Google voice settings.</returns>
        public static GoogleVoiceSettings CreateDefaultGoogleSettings(
            string instructions = "You are a helpful assistant.",
            GoogleVoice voice = GoogleVoice.Aoede,
            string? languageCode = null)
        {
            return new GoogleVoiceSettings
            {
                Instructions = instructions,
                Voice = voice,
                Model = GoogleModel.Gemini31FlashLivePreview,
                ResponseModality = "AUDIO",
                LanguageCode = languageCode,
                VoiceActivityDetection = new VoiceActivityDetection
                {
                    StartOfSpeechSensitivity = SpeechSensitivity.HIGH,
                    EndOfSpeechSensitivity = SpeechSensitivity.LOW,
                    PrefixPaddingMs = 100,
                    SilenceDurationMs = 200,
                    ActivityHandling = ActivityHandling.START_OF_ACTIVITY_INTERRUPTS,
                    AutomaticDetection = true
                },
                TranscriptionConfig = new AudioTranscriptionConfig
                {
                    EnableInputTranscription = true,
                    EnableOutputTranscription = true
                },
                Temperature = 1.0,
                TopP = 0.95
            };
        }
    }
}
