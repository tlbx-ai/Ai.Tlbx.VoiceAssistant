using System;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.Google;
using Ai.Tlbx.VoiceAssistant.Provider.XAi;
using Microsoft.Extensions.DependencyInjection;

namespace Ai.Tlbx.VoiceAssistant.Demo.Web.Services
{
    public enum VoiceProviderType
    {
        OpenAI,
        Google,
        XAi,
        OpenAiTranscription
    }

    public interface IVoiceProviderFactory
    {
        IVoiceProvider CreateProvider(VoiceProviderType providerType);
    }

    public class VoiceProviderFactory : IVoiceProviderFactory
    {
        private readonly IServiceProvider _serviceProvider;

        public VoiceProviderFactory(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public IVoiceProvider CreateProvider(VoiceProviderType providerType)
        {
            var logAction = _serviceProvider.GetService<Action<LogLevel, string>>();

            return providerType switch
            {
                VoiceProviderType.OpenAI => new OpenAiVoiceProvider(
                    Environment.GetEnvironmentVariable("OPENAI_API_KEY"), logAction),
                VoiceProviderType.Google => new GoogleVoiceProvider(
                    Environment.GetEnvironmentVariable("GOOGLE_API_KEY"), logAction),
                VoiceProviderType.XAi => new XaiVoiceProvider(
                    Environment.GetEnvironmentVariable("XAI_API_KEY"), logAction),
                VoiceProviderType.OpenAiTranscription => new OpenAiTranscriptionProvider(
                    Environment.GetEnvironmentVariable("OPENAI_API_KEY"), logAction: logAction),
                _ => throw new ArgumentException($"Unknown provider type: {providerType}", nameof(providerType))
            };
        }
    }
}
