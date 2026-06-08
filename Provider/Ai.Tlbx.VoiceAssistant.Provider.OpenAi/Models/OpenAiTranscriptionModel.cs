using System;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models
{
    public enum OpenAiTranscriptionModel
    {
        GptRealtimeWhisper,
        Gpt4oMiniTranscribe,
        Gpt4oMiniTranscribe20251215,
        Gpt4oTranscribe,
        Gpt4oTranscribeDiarize,
        [Obsolete("Legacy transcription model. Prefer GptRealtimeWhisper for realtime streaming or Gpt4oTranscribe for HTTP transcription.")]
        Whisper1,
    }

    public static class OpenAiTranscriptionModelExtensions
    {
#pragma warning disable CS0618
        public static string ToApiString(this OpenAiTranscriptionModel model)
        {
            return model switch
            {
                OpenAiTranscriptionModel.GptRealtimeWhisper => "gpt-realtime-whisper",
                OpenAiTranscriptionModel.Gpt4oMiniTranscribe => "gpt-4o-mini-transcribe",
                OpenAiTranscriptionModel.Gpt4oMiniTranscribe20251215 => "gpt-4o-mini-transcribe-2025-12-15",
                OpenAiTranscriptionModel.Gpt4oTranscribe => "gpt-4o-transcribe",
                OpenAiTranscriptionModel.Gpt4oTranscribeDiarize => "gpt-4o-transcribe-diarize",
                OpenAiTranscriptionModel.Whisper1 => "whisper-1",
                _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported transcription model")
            };
        }
#pragma warning restore CS0618

        public static bool SupportsRealtimeTranscription(this OpenAiTranscriptionModel model)
        {
            return model == OpenAiTranscriptionModel.GptRealtimeWhisper;
        }

        public static bool SupportsHttpStreamingTranscription(this OpenAiTranscriptionModel model)
        {
            return model == OpenAiTranscriptionModel.Gpt4oTranscribe ||
                model == OpenAiTranscriptionModel.Gpt4oMiniTranscribe ||
                model == OpenAiTranscriptionModel.Gpt4oMiniTranscribe20251215 ||
                model == OpenAiTranscriptionModel.Gpt4oTranscribeDiarize;
        }

        public static bool SupportsDiarizedJson(this OpenAiTranscriptionModel model)
        {
            return model == OpenAiTranscriptionModel.Gpt4oTranscribeDiarize;
        }

#pragma warning disable CS0618
        public static bool SupportsTranscriptionPrompt(this OpenAiTranscriptionModel model)
        {
            return model == OpenAiTranscriptionModel.Gpt4oTranscribe ||
                model == OpenAiTranscriptionModel.Whisper1;
        }
#pragma warning restore CS0618

        public static bool SupportsRealtimeTurnDetection(this OpenAiTranscriptionModel model)
        {
            return model != OpenAiTranscriptionModel.GptRealtimeWhisper &&
                model != OpenAiTranscriptionModel.Gpt4oTranscribeDiarize;
        }

        public static bool SupportsTranscriptionLogProbabilities(this OpenAiTranscriptionModel model)
        {
            return model == OpenAiTranscriptionModel.GptRealtimeWhisper ||
                model == OpenAiTranscriptionModel.Gpt4oTranscribe ||
                model == OpenAiTranscriptionModel.Gpt4oMiniTranscribe ||
                model == OpenAiTranscriptionModel.Gpt4oMiniTranscribe20251215;
        }
    }
}
