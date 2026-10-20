using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.Mistral;

public sealed class MistralVoiceSettings : IVoiceSettings
{
    public string Instructions { get; set; } = "Antworte knapp auf Deutsch.";
    public List<IVoiceTool> Tools { get; set; } = new();
    public double TalkingSpeed { get; set; } = 1;
    public NoiseReductionMode NoiseReduction { get; set; }
    public SessionReasoningEffort? ReasoningEffort { get; set; }
    public ToolCallPreambleMode ToolCallPreambleMode { get; set; }
    public SessionThinkingConfig Thinking { get; set; } = new();
    public string TranscriptionModel { get; set; } = "voxtral-mini-transcribe-3-0";
    public string SpeechModel { get; set; } = "voxtral-mini-tts-3-0";
    public string Voice { get; set; } = "voice_481"; // Fiona, German
    public string? Language { get; set; } = "de";
    public bool TranscriptionOnly { get; set; }
    public int SilenceDurationMs { get; set; } = 650;
    public int MaxUtteranceSeconds { get; set; } = 30;
    /// <summary>RMS in normalized PCM16 samples; tune for the microphone.</summary>
    public double SpeechThreshold { get; set; } = 0.015;
    /// <summary>Application-owned reasoning and tools; receives a snapshot of the whole conversation.</summary>
    public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>>? RespondAsync { get; set; }
}
