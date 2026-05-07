namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models
{
    /// <summary>
    /// Model capability helpers for OpenAI Realtime configuration.
    /// </summary>
    public static class OpenAiRealtimeModelSupport
    {
        public static bool SupportsReasoningEffort(this OpenAiRealtimeModel model)
        {
            return model == OpenAiRealtimeModel.GptRealtime2;
        }
    }
}
