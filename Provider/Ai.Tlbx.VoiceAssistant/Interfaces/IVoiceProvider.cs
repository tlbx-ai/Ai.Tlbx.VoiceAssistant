using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Interfaces
{
    /// <summary>
    /// Interface for voice AI providers (OpenAI, Google, xAI, etc.).
    /// Handles provider-specific communication and protocol logic.
    /// </summary>
    public interface IVoiceProvider : IAsyncDisposable
    {
        /// <summary>
        /// Gets a value indicating whether the provider is connected and ready.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Gets the required input audio sample rate for this provider.
        /// The hardware layer will downsample from 48kHz capture to this rate.
        /// </summary>
        AudioSampleRate RequiredInputSampleRate { get; }
        
        /// <summary>
        /// Connects to the AI provider using the specified settings.
        /// </summary>
        /// <param name="settings">Provider-specific settings for connection.</param>
        /// <returns>A task representing the connection operation.</returns>
        Task ConnectAsync(IVoiceSettings settings);
        
        /// <summary>
        /// Disconnects from the AI provider.
        /// </summary>
        /// <returns>A task representing the disconnection operation.</returns>
        Task DisconnectAsync();
        
        /// <summary>
        /// Updates the settings for an existing connection.
        /// </summary>
        /// <param name="settings">The new settings to apply.</param>
        /// <returns>A task representing the update operation.</returns>
        Task UpdateSettingsAsync(IVoiceSettings settings);
        
        /// <summary>
        /// Processes audio data received from the microphone and sends it to the AI provider.
        /// </summary>
        /// <param name="base64Audio">Base64-encoded PCM 16-bit audio data.</param>
        /// <returns>A task representing the audio processing operation.</returns>
        Task ProcessAudioAsync(string base64Audio);
        
        /// <summary>
        /// Sends an interrupt signal to the AI provider to stop current response generation.
        /// </summary>
        /// <returns>A task representing the interrupt operation.</returns>
        Task SendInterruptAsync();
        
        /// <summary>
        /// Injects conversation history into the current session.
        /// </summary>
        /// <param name="messages">The conversation history to inject.</param>
        /// <returns>A task representing the injection operation.</returns>
        Task InjectConversationHistoryAsync(IEnumerable<ChatMessage> messages);
        
        // Provider → Orchestrator callbacks
        /// <summary>
        /// Callback invoked when a message is received from the AI provider.
        /// </summary>
        Action<ChatMessage>? OnMessageReceived { get; set; }
        
        /// <summary>
        /// Callback invoked when audio data is received from the AI provider for playback.
        /// </summary>
        Action<string>? OnAudioReceived { get; set; }

        /// <summary>
        /// Callback invoked when the provider needs to wait until queued playback has drained.
        /// </summary>
        Func<TimeSpan?, Task<bool>>? WaitForPlaybackDrainAsync { get; set; }
        
        /// <summary>
        /// Callback invoked when the provider status changes.
        /// </summary>
        Action<string>? OnStatusChanged { get; set; }
        
        /// <summary>
        /// Callback invoked when an error occurs in the provider.
        /// </summary>
        Action<string>? OnError { get; set; }
        
        /// <summary>
        /// Callback invoked when interruption is detected and audio needs to be cleared.
        /// </summary>
        Action? OnInterruptDetected { get; set; }

        /// <summary>
        /// Callback invoked when usage data is received from the AI provider.
        /// Reports token/audio usage for billing and monitoring.
        /// </summary>
        Action<UsageReport>? OnUsageReceived { get; set; }

        /// <summary>
        /// Callback invoked with partial transcription text as the user speaks.
        /// Enables real-time streaming display of speech-to-text.
        /// </summary>
        Action<string>? OnTranscriptionDelta { get; set; }

        /// <summary>
        /// Callback invoked with the finalized transcript when an utterance completes.
        /// </summary>
        Action<string>? OnTranscriptionCompleted { get; set; }
    }
}
