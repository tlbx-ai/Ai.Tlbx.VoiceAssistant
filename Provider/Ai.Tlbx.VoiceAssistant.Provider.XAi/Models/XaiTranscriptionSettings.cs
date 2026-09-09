using System.Collections.Generic;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.XAi.Models;

/// <summary>
/// Settings for xAI's dedicated streaming Speech-to-Text WebSocket API.
/// </summary>
public sealed class XaiTranscriptionSettings : IVoiceSettings
{
        /// <summary>Routing and authentication, read on each request or reconnect.</summary>
        public ProviderEndpointOptions Connection { get; set; } = new("wss://api.x.ai/v1/stt");

        /// <summary>Model ID for transcript and usage metadata. The native STT API has no model selector; gateway routing parameters belong in Connection.Endpoint.</summary>
        public string? ModelId { get; set; }
        public string GetModelId() => string.IsNullOrWhiteSpace(ModelId) ? "grok-transcribe" : ModelId;

    public bool InterimResults { get; set; } = true;
    public int EndpointingMs { get; set; } = 10;
    public string? Language { get; set; }
    public bool Diarize { get; set; } = true;
    public bool IncludeFillerWords { get; set; }
    public bool Multichannel { get; set; }
    public int Channels { get; set; } = 1;
    public List<string> Keyterms { get; set; } = new();

    /// <summary>
    /// Smart Turn confidence required before a silence boundary finalizes the utterance.
    /// Null disables Smart Turn. A balanced 0.7 default avoids splitting natural pauses.
    /// </summary>
    public double? SmartTurnThreshold { get; set; } = 0.7;

    /// <summary>Maximum Smart Turn silence before forcing finalization.</summary>
    public int? SmartTurnTimeoutMs { get; set; } = 3000;

    /// <summary>Speech probability threshold. Zero disables the speech gate.</summary>
    public double VadThreshold { get; set; } = 0.08;

    // IVoiceSettings compatibility: STT does not generate speech or invoke tools.
    public string Instructions { get; set; } = string.Empty;
    public List<IVoiceTool> Tools { get; set; } = new();
    public double TalkingSpeed { get; set; } = 1.0;
    public NoiseReductionMode NoiseReduction { get; set; } = NoiseReductionMode.NearField;
    public SessionReasoningEffort? ReasoningEffort { get; set; }
    public ToolCallPreambleMode ToolCallPreambleMode { get; set; } = ToolCallPreambleMode.ProviderDefault;
    public SessionThinkingConfig Thinking { get; set; } = new();
}
