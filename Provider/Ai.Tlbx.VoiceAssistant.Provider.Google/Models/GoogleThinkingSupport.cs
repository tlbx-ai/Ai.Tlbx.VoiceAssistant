namespace Ai.Tlbx.VoiceAssistant.Provider.Google.Models
{
    /// <summary>
    /// Model capability helpers for Google thinking configuration.
    /// </summary>
    public static class GoogleThinkingSupport
    {
        public static bool SupportsThinkingLevel(this GoogleModel model)
        {
            return model == GoogleModel.Gemini31FlashLivePreview;
        }

#pragma warning disable CS0618
        public static bool SupportsThinkingBudget(this GoogleModel model)
        {
            return model is
                GoogleModel.GeminiLive25FlashNativeAudio or
                GoogleModel.Gemini25FlashNativeAudio or
                GoogleModel.Gemini25FlashNativeAudioPreview202509 or
                GoogleModel.GeminiLive25Flash;
        }
#pragma warning restore CS0618

        public static bool SupportsThoughtSummaries(this GoogleModel model)
        {
            return model.SupportsThinkingLevel() || model.SupportsThinkingBudget();
        }
    }
}
