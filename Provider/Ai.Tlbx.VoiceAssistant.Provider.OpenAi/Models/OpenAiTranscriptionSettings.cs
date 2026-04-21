using System.Collections.Generic;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models
{
    public class OpenAiTranscriptionSettings : IVoiceSettings
    {
        public string Instructions { get; set; } = string.Empty;
        public List<IVoiceTool> Tools { get; set; } = new();
        public double TalkingSpeed { get; set; } = 1.0;

        public OpenAiTranscriptionModel TranscriptionModel { get; set; } = OpenAiTranscriptionModel.Gpt4oTranscribe;
        public double VadThreshold { get; set; } = 0.5;
        public int PrefixPaddingMs { get; set; } = 300;
        public int SilenceDurationMs { get; set; } = 200;
        public NoiseReductionMode NoiseReduction { get; set; } = NoiseReductionMode.NearField;
        public SessionThinkingConfig Thinking { get; set; } = new();
        public string? TranscriptionPrompt { get; set; }
        public string? Language { get; set; }
    }
}
