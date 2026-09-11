using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

/// <summary>GPT-Live configuration. This uses /v1/live/sessions, not the Realtime API.</summary>
public sealed class OpenAiLiveSettings : IVoiceSettings
{
    public string ModelId { get; set; } = "gpt-live-1";
    public string Instructions { get; set; } = "Be concise and helpful. Delegate tasks that need tools or reasoning to the backend.";
    /// <summary>API voice name, including Live voices such as quartz, ripple, and willow.</summary>
    public string Voice { get; set; } = "marin";
    public bool Store { get; set; }
    public ProviderEndpointOptions Connection { get; set; } = new("wss://api.openai.com/v1/live/sessions");
    /// <summary>Null selects client delegation. Otherwise this is the Live-supported Responses configuration
    /// (model, instructions, tools, reasoning, text, tool_choice, parallel_tool_calls, max_output_tokens, service_tier).</summary>
    public JsonObject? Responses { get; set; }
    /// <summary>Prior text messages included in session.start, at most 128 messages / 8192 tokens.</summary>
    public List<ChatMessage> InitialHistory { get; set; } = new();
    /// <summary>Local functions for Responses delegation. They execute only when explicitly registered.</summary>
    public List<IVoiceTool> Tools { get; set; } = new();
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(15);
    // These inherited controls do not exist in the Live speech protocol; non-default speech controls are rejected.
    public double TalkingSpeed { get; set; } = 1;
    public NoiseReductionMode NoiseReduction { get; set; }
    public SessionReasoningEffort? ReasoningEffort { get; set; }
    public ToolCallPreambleMode ToolCallPreambleMode { get; set; }
    public SessionThinkingConfig Thinking { get; set; } = new();
}

/// <summary>An original transcript fragment; its interval is not an authoritative speech turn.</summary>
public sealed record OpenAiLiveTranscriptDelta(string Role, string Delta, double StartMs, double EndMs);

/// <summary>Delegation metadata. Task text must be assembled from transcripts and application state.</summary>
public sealed record OpenAiLiveDelegation(string Id, string Target, double OffsetMs, string? ResponseId);
