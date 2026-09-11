using System;
using System.Linq;
using Ai.Tlbx.VoiceAssistant.Configuration;
using Ai.Tlbx.VoiceAssistant.Extensions;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Extensions
{
    /// <summary>
    /// Extension methods for configuring OpenAI voice provider services in dependency injection.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>Adds the separate GPT-Live provider. Pass OpenAiLiveSettings when starting the assistant.</summary>
        public static VoiceAssistantBuilder WithOpenAiLive(this VoiceAssistantBuilder builder, string? apiKey = null)
        {
            builder.Services.AddScoped<OpenAiLiveProvider>(provider =>
                new OpenAiLiveProvider(apiKey, provider.GetService<Action<LogLevel, string>>()));
            builder.Services.AddScoped<IVoiceProvider>(provider => provider.GetRequiredService<OpenAiLiveProvider>());
            return builder;
        }

        /// <summary>
        /// Adds the OpenAI voice provider to the voice assistant configuration.
        /// </summary>
        /// <param name="builder">The voice assistant builder.</param>
        /// <param name="apiKey">Optional OpenAI API key. If not provided, will use OPENAI_API_KEY environment variable.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public static VoiceAssistantBuilder WithOpenAi(this VoiceAssistantBuilder builder, string? apiKey = null)
        {
            builder.Services.AddScoped<IVoiceProvider>(provider =>
            {
                var logAction = provider.GetService<Action<LogLevel, string>>();
                var httpClient = provider.GetService<System.Net.Http.HttpClient>();
                var tools = provider.GetServices<IVoiceTool>().ToList();

                var voiceProvider = new OpenAiVoiceProvider(apiKey, logAction, httpClient);
                
                // Pre-configure with tools if any are registered
                if (tools.Any())
                {
                    var settings = CreateDefaultOpenAiSettings();
                    
                    // Add tools to settings
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
        /// Creates default OpenAI voice settings for voice assistant configuration.
        /// </summary>
        /// <param name="instructions">Custom instructions for the AI assistant.</param>
        /// <param name="voice">The voice to use for responses.</param>
        /// <returns>Configured OpenAI voice settings.</returns>
        public static OpenAiVoiceSettings CreateDefaultOpenAiSettings(
            string instructions = "You are a helpful assistant.",
            AssistantVoice voice = AssistantVoice.Marin)
        {
            return new OpenAiVoiceSettings
            {
                Instructions = instructions,
                Voice = voice,
                Model = OpenAiRealtimeModel.GptRealtime21,
                TurnDetection = new TurnDetection
                {
                    Type = "server_vad",
                    Threshold = 0.65,
                    PrefixPaddingMs = 500,
                    SilenceDurationMs = 200,
                    CreateResponse = true,
                    InterruptResponse = true,
                },
                InputAudioTranscription = new InputAudioTranscription
                {
                    Enabled = true
                },
                OutputAudioFormat = "pcm16"
            };
        }

        /// <summary>
        /// Creates default OpenAI voice settings with a directly supplied server-VAD threshold.
        /// </summary>
        /// <param name="vadThreshold">Voice activation threshold from 0.0 to 1.0. Higher values reduce sensitivity.</param>
        /// <param name="instructions">Custom instructions for the AI assistant.</param>
        /// <param name="voice">The voice to use for responses.</param>
        /// <returns>Configured OpenAI voice settings.</returns>
        public static OpenAiVoiceSettings CreateDefaultOpenAiSettings(
            double vadThreshold,
            string instructions = "You are a helpful assistant.",
            AssistantVoice voice = AssistantVoice.Marin)
        {
            var settings = CreateDefaultOpenAiSettings(instructions, voice);
            settings.VadThreshold = vadThreshold;
            return settings;
        }
    }
}
