using System;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models
{
    /// <summary>
    /// Supported OpenAI Realtime API models that have been tested with this library.
    /// Current defaults should prefer <see cref="GptRealtime15"/>.
    /// </summary>
    public enum OpenAiRealtimeModel
    {
        /// <summary>
        /// Legacy alias for the unpinned GPT Realtime model id.
        /// Kept for compatibility with earlier releases.
        /// </summary>
        [Obsolete("Prefer GptRealtime15. This unpinned alias is kept for compatibility.")]
        GptRealtime,

        /// <summary>
        /// Latest GPT Realtime mini model.
        /// Recommended when you want lower cost and lower latency than the full realtime model.
        /// </summary>
        GptRealtimeMini,

        /// <summary>
        /// GPT Realtime 1.5.
        /// Recommended default for production use.
        /// </summary>
        GptRealtime15,

        /// <summary>
        /// Pinned GPT Realtime snapshot from August 28, 2025.
        /// </summary>
        [Obsolete("Prefer GptRealtime15 unless you explicitly need the 2025-08-28 snapshot.")]
        Gpt520250828,

        /// <summary>
        /// GPT-4o Realtime preview from June 2025.
        /// </summary>
        [Obsolete("Legacy preview model. Use GptRealtime15 instead.")]
        Gpt4oRealtimePreview20250603,
        
        /// <summary>
        /// GPT-4o Realtime preview from December 2024.
        /// </summary>
        [Obsolete("Legacy preview model. Use GptRealtime15 instead.")]
        Gpt4oRealtimePreview20241217,
        
        /// <summary>
        /// GPT-4o Realtime preview from October 2024.
        /// </summary>
        [Obsolete("Legacy preview model. Use GptRealtime15 instead.")]
        Gpt4oRealtimePreview20241001,
        
        /// <summary>
        /// GPT-4o Mini Realtime preview from December 2024.
        /// </summary>
        [Obsolete("Legacy preview model. Use GptRealtimeMini instead.")]
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
#pragma warning disable CS0618
        public static string ToApiString(this OpenAiRealtimeModel model)
        {
            return model switch
            {
                OpenAiRealtimeModel.GptRealtime => "gpt-realtime",
                OpenAiRealtimeModel.GptRealtimeMini => "gpt-realtime-mini",
                OpenAiRealtimeModel.GptRealtime15 => "gpt-realtime-1.5",
                OpenAiRealtimeModel.Gpt520250828 => "gpt-realtime-2025-08-28",
                OpenAiRealtimeModel.Gpt4oRealtimePreview20250603 => "gpt-4o-realtime-preview-2025-06-03",
                OpenAiRealtimeModel.Gpt4oRealtimePreview20241217 => "gpt-4o-realtime-preview-2024-12-17",
                OpenAiRealtimeModel.Gpt4oRealtimePreview20241001 => "gpt-4o-realtime-preview-2024-10-01",
                OpenAiRealtimeModel.Gpt4oMiniRealtimePreview20241217 => "gpt-4o-mini-realtime-preview-2024-12-17",
                _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported model")
            };
        }
#pragma warning restore CS0618

        /// <summary>
        /// Parses an OpenAI API model id into the corresponding enum value.
        /// </summary>
        public static bool TryParseApiString(string? modelId, out OpenAiRealtimeModel model)
        {
            model = default;

            if (string.IsNullOrWhiteSpace(modelId))
            {
                return false;
            }

            foreach (var candidate in Enum.GetValues<OpenAiRealtimeModel>())
            {
                if (string.Equals(candidate.ToApiString(), modelId, StringComparison.OrdinalIgnoreCase))
                {
                    model = candidate;
                    return true;
                }
            }

            return false;
        }
    }
}
