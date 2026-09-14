using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

/// <summary>Explicit client delegation through the ordinary Responses HTTP API. Complete tool results
/// remain in that backend's history; only a concise factual answer is appended to Live.
/// Configure before connecting. No automatic transport or delegation fallback is performed.</summary>
public sealed class OpenAiLiveClientBackendOptions
{
    public ProviderEndpointOptions Connection { get; set; } = new("https://api.openai.com/v1/responses");
    /// <summary>Responses model configuration. The runner owns input, tools, stream, store and continuation.
    /// Requires model. Instructions are supplemented with a short spoken-answer contract.</summary>
    public JsonObject Responses { get; set; } = new() { ["model"] = "gpt-5.6-luna", ["max_output_tokens"] = 2048 };
    /// <summary>Optional application-owned HTTP client, never disposed by the provider.</summary>
    public HttpClient? HttpClient { get; set; }
    public int MaxRounds { get; set; } = 8;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
}
