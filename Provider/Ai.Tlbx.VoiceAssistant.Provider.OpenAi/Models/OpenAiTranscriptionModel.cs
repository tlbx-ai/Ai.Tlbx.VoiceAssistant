using System;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models
{
    public enum OpenAiTranscriptionModel
    {
        Gpt4oMiniTranscribe,
        [Obsolete("Pinned legacy snapshot. Prefer Gpt4oMiniTranscribe or Gpt4oMiniTranscribe20251215.")]
        Gpt4oMiniTranscribe20250320,
        Gpt4oMiniTranscribe20251215,
        Gpt4oTranscribe,
        Gpt4oTranscribeDiarize,
        [Obsolete("Legacy transcription model. Prefer Gpt4oMiniTranscribe or Gpt4oTranscribe.")]
        Whisper1,
    }

    public static class OpenAiTranscriptionModelExtensions
    {
#pragma warning disable CS0618
        public static string ToApiString(this OpenAiTranscriptionModel model)
        {
            return model switch
            {
                OpenAiTranscriptionModel.Gpt4oMiniTranscribe => "gpt-4o-mini-transcribe",
                OpenAiTranscriptionModel.Gpt4oMiniTranscribe20250320 => "gpt-4o-mini-transcribe-2025-03-20",
                OpenAiTranscriptionModel.Gpt4oMiniTranscribe20251215 => "gpt-4o-mini-transcribe-2025-12-15",
                OpenAiTranscriptionModel.Gpt4oTranscribe => "gpt-4o-transcribe",
                OpenAiTranscriptionModel.Gpt4oTranscribeDiarize => "gpt-4o-transcribe-diarize",
                OpenAiTranscriptionModel.Whisper1 => "whisper-1",
                _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported transcription model")
            };
        }
#pragma warning restore CS0618
    }
}
