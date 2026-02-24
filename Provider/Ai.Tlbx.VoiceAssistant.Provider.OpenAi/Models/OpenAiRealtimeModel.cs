using System;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models
{
    /// <summary>
    /// Supported OpenAI Realtime API models that have been tested and verified to work with this library.
    /// </summary>
    public enum OpenAiRealtimeModel
    {
        /// <summary>
        /// Latest GPT Realtime model (unpinned version, automatically uses newest).
        /// Recommended for production use.
        /// </summary>
        GptRealtime,

        /// <summary>
        /// GPT Realtime 1.5 - improved reasoning, transcription accuracy, and instruction following.
        /// </summary>
        GptRealtime15,

        /// <summary>
        /// First full release based on GPT5
        /// </summary>
        Gpt520250828,

        /// <summary>
        /// GPT-4 Omni Realtime Preview - Latest version (June 2025).
        /// This is the most recent and recommended model for voice interactions.
        /// Released: 2025-06-03
        /// </summary>
        Gpt4oRealtimePreview20250603,
        
        /// <summary>
        /// GPT-4 Omni Realtime Preview - December 2024 version.
        /// Stable release with improved voice quality.
        /// Released: 2024-12-17
        /// </summary>
        Gpt4oRealtimePreview20241217,
        
        /// <summary>
        /// GPT-4 Omni Realtime Preview - October 2024 version.
        /// Earlier stable release, maintained for compatibility.
        /// Released: 2024-10-01
        /// </summary>
        Gpt4oRealtimePreview20241001,
        
        /// <summary>
        /// GPT-4 Omni Mini Realtime Preview - December 2024 version.
        /// Smaller, faster model optimized for lower latency.
        /// Released: 2024-12-17
        /// </summary>
        Gpt4oMiniRealtimePreview20241217,
    }
    
    /// <summary>
    /// Extension methods for OpenAiRealtimeModel enum.
    /// </summary>
    public static class OpenAiRealtimeModelExtensions
    {
        /// <summary>
        /// Gets the API model string for the specified model.
        /// </summary>
        /// <param name="model">The model enum value.</param>
        /// <returns>The API model string to use with OpenAI.</returns>
        public static string ToApiString(this OpenAiRealtimeModel model)
        {
            return model switch
            {
                OpenAiRealtimeModel.GptRealtime => "gpt-realtime",
                OpenAiRealtimeModel.GptRealtime15 => "gpt-realtime-1.5",
                OpenAiRealtimeModel.Gpt520250828 => "gpt-realtime-2025-08-28",
                OpenAiRealtimeModel.Gpt4oRealtimePreview20250603 => "gpt-4o-realtime-preview-2025-06-03",
                OpenAiRealtimeModel.Gpt4oRealtimePreview20241217 => "gpt-4o-realtime-preview-2024-12-17",
                OpenAiRealtimeModel.Gpt4oRealtimePreview20241001 => "gpt-4o-realtime-preview-2024-10-01",
                OpenAiRealtimeModel.Gpt4oMiniRealtimePreview20241217 => "gpt-4o-mini-realtime-preview-2024-12-17",
                _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported model")
            };
        }
    }
}