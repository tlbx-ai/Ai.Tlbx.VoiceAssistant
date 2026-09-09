using System.Collections.Generic;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models
{
    public class OpenAiTranscriptionSettings : IVoiceSettings
    {
        /// <summary>Routing and authentication, read on each request or reconnect.</summary>
        public ProviderEndpointOptions Connection { get; set; } = new("wss://api.openai.com/v1/realtime");

        /// <summary>Exact gateway model/deployment ID. Null uses the built-in model; the enum still selects protocol capabilities.</summary>
        public string? ModelId { get; set; }
        public string GetModelId() => string.IsNullOrWhiteSpace(ModelId) ? TranscriptionModel.ToApiString() : ModelId;

        public string Instructions { get; set; } = string.Empty;
        public List<IVoiceTool> Tools { get; set; } = new();
        public double TalkingSpeed { get; set; } = 1.0;

        public OpenAiTranscriptionModel TranscriptionModel { get; set; } = OpenAiTranscriptionModel.GptLiveTranscribe;
        public double VadThreshold { get; set; } = 0.5;
        public int PrefixPaddingMs { get; set; } = 300;
        public int SilenceDurationMs { get; set; } = 200;
        public NoiseReductionMode NoiseReduction { get; set; } = NoiseReductionMode.NearField;
        public SessionReasoningEffort? ReasoningEffort { get; set; }
        public ToolCallPreambleMode ToolCallPreambleMode { get; set; } = ToolCallPreambleMode.ProviderDefault;
        public SessionThinkingConfig Thinking { get; set; } = new();
        public string? TranscriptionPrompt { get; set; }
        public string? Language { get; set; }

        /// <summary>Literal domain terms to favor with gpt-live-transcribe or gpt-transcribe.</summary>
        public List<string> Keywords { get; set; } = new();

        /// <summary>Expected language codes for current transcription models.</summary>
        public List<string> Languages { get; set; } = new();

        /// <summary>Latency/accuracy tradeoff for gpt-live-transcribe.</summary>
        public OpenAiTranscriptionDelay Delay { get; set; } = OpenAiTranscriptionDelay.Low;

        /// <summary>
        /// Include token log probabilities in Realtime transcription events when the model supports it.
        /// </summary>
        public bool IncludeLogProbabilities { get; set; }
    }

    public enum OpenAiTranscriptionDelay
    {
        Minimal,
        Low,
        Medium,
        High,
        ExtraHigh
    }

    public static class OpenAiTranscriptionDelayExtensions
    {
        public static string ToApiString(this OpenAiTranscriptionDelay delay) => delay switch
        {
            OpenAiTranscriptionDelay.Minimal => "minimal",
            OpenAiTranscriptionDelay.Low => "low",
            OpenAiTranscriptionDelay.Medium => "medium",
            OpenAiTranscriptionDelay.High => "high",
            OpenAiTranscriptionDelay.ExtraHigh => "xhigh",
            _ => throw new System.ArgumentOutOfRangeException(nameof(delay), delay, "Unsupported transcription delay")
        };
    }
}
