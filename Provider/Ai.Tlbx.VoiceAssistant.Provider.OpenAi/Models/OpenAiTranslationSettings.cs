using System;
using System.Collections.Generic;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

/// <summary>Continuous speech translation. Each session translates one source stream into one target language.</summary>
public sealed class OpenAiTranslationSettings : IVoiceSettings
{
    public string ModelId { get; set; } = "gpt-realtime-translate";
    public string TargetLanguage { get; set; } = "en";
    public string? SafetyIdentifier { get; set; }
    public ProviderEndpointOptions Connection { get; set; } = new("wss://api.openai.com/v1/realtime/translations");
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Null disables source transcript deltas. Translation itself still consumes the audio.</summary>
    public string? SourceTranscriptionModelId { get; set; } = "gpt-realtime-whisper";
    public bool EnableNoiseReduction { get; set; } = true;
    public NoiseReductionMode NoiseReduction { get; set; } = NoiseReductionMode.NearField;
    // These assistant controls do not exist in the interpreter protocol; non-default values are rejected.
    public string Instructions { get; set; } = "";
    public List<IVoiceTool> Tools { get; set; } = new();
    public double TalkingSpeed { get; set; } = 1;
    public SessionReasoningEffort? ReasoningEffort { get; set; }
    public ToolCallPreambleMode ToolCallPreambleMode { get; set; }
    public SessionThinkingConfig Thinking { get; set; } = new();
}

/// <summary>A continuous original fragment, not a completed speech turn.</summary>
public sealed record OpenAiTranslationTranscriptDelta(string Role, string Delta, string? Language, double? ElapsedMs = null);
public sealed record OpenAiTranslationWebRtcSession(string SessionId, string Sdp);
