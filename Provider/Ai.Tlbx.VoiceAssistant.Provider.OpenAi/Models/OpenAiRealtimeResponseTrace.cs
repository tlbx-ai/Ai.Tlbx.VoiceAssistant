using System;
using System.Collections.Generic;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models
{
    /// <summary>
    /// Captures an OpenAI Realtime response for debugging, evaluation, and test campaign replay.
    /// </summary>
    public sealed class OpenAiRealtimeResponseTrace
    {
        public string? ResponseId { get; set; }
        public string? Status { get; set; }
        public string? StatusDetailsJson { get; set; }
        public string? InputTranscript { get; set; }
        public string? OutputText { get; set; }
        public string? OutputAudioTranscript { get; set; }
        public List<string> OutputPhases { get; } = new();
        public List<OpenAiRealtimeOutputTrace> Outputs { get; } = new();
        public List<OpenAiRealtimeToolCallTrace> ToolCalls { get; } = new();
        public UsageReport? Usage { get; set; }
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAt { get; set; }
    }

    /// <summary>
    /// Captures one output item within an OpenAI Realtime response, including its response phase.
    /// </summary>
    public sealed class OpenAiRealtimeOutputTrace
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Phase { get; set; }
        public string? Text { get; set; }
        public string? AudioTranscript { get; set; }
    }

    /// <summary>
    /// Captures one function tool call within an OpenAI Realtime response.
    /// </summary>
    public sealed class OpenAiRealtimeToolCallTrace
    {
        public string? Name { get; set; }
        public string? CallId { get; set; }
        public string? ArgumentsJson { get; set; }
        public string? OutputJson { get; set; }
        public string? Error { get; set; }
    }
}
