namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models
{
    /// <summary>
    /// Model capability helpers for OpenAI Realtime configuration.
    /// </summary>
    public static class OpenAiRealtimeModelSupport
    {
        public static bool SupportsReasoningEffort(this OpenAiRealtimeModel model)
        {
            return model is OpenAiRealtimeModel.GptRealtime21
                or OpenAiRealtimeModel.GptRealtime2;
        }
    }
}
