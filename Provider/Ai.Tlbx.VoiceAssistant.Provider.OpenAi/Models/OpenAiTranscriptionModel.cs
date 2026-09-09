using System;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models
{
    public enum OpenAiTranscriptionModel
    {
        /// <summary>
        /// OpenAI's recommended low-latency model for live microphone transcription.
        /// Supports context, keyword/language hints, and a latency/accuracy delay control.
        /// </summary>
        GptLiveTranscribe,

        /// <summary>
        /// OpenAI's recommended high-accuracy transcription model for recorded audio
        /// and committed realtime turns. Supports context, keyword, and language hints.
        /// </summary>
        GptTranscribe,

        GptRealtimeWhisper,
        Gpt4oMiniTranscribe,
        Gpt4oMiniTranscribe20251215,
        Gpt4oTranscribe,
        Gpt4oTranscribeDiarize,
        [Obsolete("Legacy transcription model. Prefer GptLiveTranscribe for realtime streaming or GptTranscribe for HTTP transcription.")]
        Whisper1,
    }

    public static class OpenAiTranscriptionModelExtensions
    {
#pragma warning disable CS0618
        public static string ToApiString(this OpenAiTranscriptionModel model)
        {
            return model switch
            {
                OpenAiTranscriptionModel.GptLiveTranscribe => "gpt-live-transcribe",
                OpenAiTranscriptionModel.GptTranscribe => "gpt-transcribe",
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
            return model == OpenAiTranscriptionModel.GptLiveTranscribe ||
                model == OpenAiTranscriptionModel.GptTranscribe ||
                model == OpenAiTranscriptionModel.GptRealtimeWhisper;
        }

        public static bool SupportsHttpStreamingTranscription(this OpenAiTranscriptionModel model)
        {
            return model == OpenAiTranscriptionModel.GptTranscribe ||
                model == OpenAiTranscriptionModel.Gpt4oTranscribe ||
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
            return model == OpenAiTranscriptionModel.GptLiveTranscribe ||
                model == OpenAiTranscriptionModel.GptTranscribe ||
                model == OpenAiTranscriptionModel.Gpt4oTranscribe ||
                model == OpenAiTranscriptionModel.Whisper1;
        }
#pragma warning restore CS0618

        public static bool SupportsRealtimeTurnDetection(this OpenAiTranscriptionModel model)
        {
            return model != OpenAiTranscriptionModel.GptLiveTranscribe &&
                model != OpenAiTranscriptionModel.GptRealtimeWhisper &&
                model != OpenAiTranscriptionModel.Gpt4oTranscribeDiarize;
        }

        public static bool SupportsContextLists(this OpenAiTranscriptionModel model) =>
            model == OpenAiTranscriptionModel.GptLiveTranscribe ||
            model == OpenAiTranscriptionModel.GptTranscribe;

        public static bool SupportsDelayControl(this OpenAiTranscriptionModel model) =>
            model == OpenAiTranscriptionModel.GptLiveTranscribe;

        public static bool SupportsTranscriptionLogProbabilities(this OpenAiTranscriptionModel model)
        {
            return model == OpenAiTranscriptionModel.GptRealtimeWhisper ||
                model == OpenAiTranscriptionModel.Gpt4oTranscribe ||
                model == OpenAiTranscriptionModel.Gpt4oMiniTranscribe ||
                model == OpenAiTranscriptionModel.Gpt4oMiniTranscribe20251215;
        }
    }
}
