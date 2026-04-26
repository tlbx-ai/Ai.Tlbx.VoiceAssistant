using System;
using System.Linq;
using Ai.Tlbx.VoiceAssistant.Configuration;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.XAi.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Ai.Tlbx.VoiceAssistant.Provider.XAi.Extensions
{
    /// <summary>
    /// Extension methods for configuring xAI voice provider services in dependency injection.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the xAI voice provider to the voice assistant configuration.
        /// </summary>
        /// <param name="builder">The voice assistant builder.</param>
        /// <param name="apiKey">Optional xAI API key. If not provided, will use XAI_API_KEY environment variable.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public static VoiceAssistantBuilder WithXAi(this VoiceAssistantBuilder builder, string? apiKey = null)
        {
            builder.Services.AddScoped<IVoiceProvider>(provider =>
            {
                var logAction = provider.GetService<Action<LogLevel, string>>();
                var tools = provider.GetServices<IVoiceTool>().ToList();

                var voiceProvider = new XaiVoiceProvider(apiKey, logAction);

                if (tools.Any())
                {
                    var settings = CreateDefaultXaiSettings();

                    foreach (var tool in tools.Where(t => !settings.Tools.Any(st => st.Name == t.Name)))
                    {
                        settings.Tools.Add(tool);
                    }

                    voiceProvider.Settings = settings;
                }

                return voiceProvider;
            });

            return builder;
        }

        /// <summary>
        /// Creates default xAI voice settings for voice assistant configuration.
        /// </summary>
        /// <param name="instructions">Custom instructions for the AI assistant.</param>
        /// <param name="voice">The voice to use for responses.</param>
        /// <returns>Configured xAI voice settings.</returns>
        public static XaiVoiceSettings CreateDefaultXaiSettings(
            string instructions = "You are a helpful assistant.",
            XaiVoice voice = XaiVoice.Ara)
        {
            return new XaiVoiceSettings
            {
                Instructions = instructions,
                Model = XaiVoiceModel.GrokVoiceThinkFast10,
                Voice = voice,
                AudioSampleRate = 24000,
                AudioFormatType = "audio/pcm",
                TurnDetection = new XaiTurnDetection { Type = "server_vad" },
                EnableWebSearch = false,
                EnableXSearch = false
            };
        }
    }
}
