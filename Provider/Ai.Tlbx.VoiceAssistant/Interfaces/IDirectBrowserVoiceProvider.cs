using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Interfaces
{
    /// <summary>
    /// Optional provider capability for browser-hosted realtime voice sessions where
    /// microphone capture and assistant playback are handled directly in the browser.
    /// </summary>
    public interface IDirectBrowserVoiceProvider : IVoiceProvider
    {
        /// <summary>
        /// Starts a browser-direct voice session. Implementations should keep audio off
        /// the Blazor circuit and use provider events for status, chat, and usage updates.
        /// </summary>
        Task StartBrowserSessionAsync(
            IVoiceSettings settings,
            string? microphoneDeviceId = null,
            IEnumerable<ChatMessage>? conversationHistory = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Enables or disables microphone capture without tearing down the provider session.
        /// </summary>
        Task SetMicrophoneEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    }
}
