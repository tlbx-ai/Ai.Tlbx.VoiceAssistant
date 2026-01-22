using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Ai.Tlbx.VoiceAssistant.Configuration
{
    /// <summary>
    /// Builder class for fluent configuration of voice assistant services.
    /// Provides a chainable API for setting up hardware access and AI providers.
    /// </summary>
    public sealed class VoiceAssistantBuilder
    {
        private readonly IServiceCollection _services;

        /// <summary>
        /// Initializes a new instance of the <see cref="VoiceAssistantBuilder"/> class.
        /// </summary>
        /// <param name="services">The service collection to configure.</param>
        internal VoiceAssistantBuilder(IServiceCollection services)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
        }

        /// <summary>
        /// Configures the audio hardware access implementation.
        /// </summary>
        /// <typeparam name="THardware">The type of audio hardware access implementation.</typeparam>
        /// <returns>The builder instance for method chaining.</returns>
        public VoiceAssistantBuilder WithHardware<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THardware>()
            where THardware : class, IAudioHardwareAccess
        {
            _services.AddScoped<IAudioHardwareAccess, THardware>();
            return this;
        }

        /// <summary>
        /// Configures the AI voice provider implementation.
        /// </summary>
        /// <typeparam name="TProvider">The type of voice provider implementation.</typeparam>
        /// <returns>The builder instance for method chaining.</returns>
        public VoiceAssistantBuilder WithProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>()
            where TProvider : class, IVoiceProvider
        {
            _services.AddScoped<IVoiceProvider, TProvider>();
            return this;
        }

        /// <summary>
        /// Adds a voice tool that can be used by AI providers.
        /// </summary>
        /// <typeparam name="TTool">The type of voice tool implementation.</typeparam>
        /// <returns>The builder instance for method chaining.</returns>
        public VoiceAssistantBuilder AddTool<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTool>()
            where TTool : class, IVoiceTool
        {
            _services.AddTransient<IVoiceTool, TTool>();
            return this;
        }

        /// <summary>
        /// Configures logging for voice assistant operations.
        /// </summary>
        /// <param name="logAction">Action for logging voice assistant operations.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public VoiceAssistantBuilder WithLogging(Action<LogLevel, string> logAction)
        {
            _services.AddSingleton(logAction);
            return this;
        }

        /// <summary>
        /// Configures an HttpClient for providers that need HTTP communication.
        /// If not configured, providers will create and manage their own HttpClient instances.
        /// </summary>
        /// <param name="httpClient">The HttpClient to use for HTTP requests.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public VoiceAssistantBuilder WithHttpClient(HttpClient httpClient)
        {
            _services.AddSingleton(httpClient);
            return this;
        }

        /// <summary>
        /// Gets the configured service collection.
        /// This is the final step in the fluent configuration chain.
        /// </summary>
        /// <returns>The configured service collection.</returns>
        public IServiceCollection Services => _services;
    }
}