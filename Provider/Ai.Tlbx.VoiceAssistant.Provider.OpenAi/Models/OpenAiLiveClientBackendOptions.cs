using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

/// <summary>Explicit client delegation through the ordinary Responses HTTP API. Complete tool results
/// remain in that backend's history; only a concise factual answer is appended to Live.
/// Configure before connecting. No automatic transport or delegation fallback is performed.</summary>
public sealed class OpenAiLiveClientBackendOptions
{
    /// <summary>Optional application-owned agent. When set, no library Responses HTTP calls are made.
    /// Return a concise factual spoken result, at most 500 tokens (the Live API enforces the limit). Use context callbacks for progress and usage.
    /// The application owns authorization, durable context, model selection and disposal of its agent.</summary>
    public Func<OpenAiLiveBackendContext, CancellationToken, Task<string>>? Handler { get; set; }
    /// <summary>Optional per-delegation decision, evaluated before waiting behind existing work. Interpret user
    /// corrections/cancellations with your backend and application state, not keyword heuristics. Can return
    /// Queue, Supersede, Cancel or Ignore. Tool actions already started are retained independently.</summary>
    public Func<OpenAiLiveBackendContext, CancellationToken, Task<OpenAiLiveDelegationBehavior>>? ResolveDelegationAsync { get; set; }
    /// <summary>Share across reconnections and adapters; configure its checkpoint hook for durable storage.</summary>
    public OpenAiLiveToolJournal ToolJournal { get; set; } = new();
    /// <summary>Optional tokenizer from your inference library. Must count actual Live-compatible tokens.
    /// Without it, the server validates the documented 500-token append limit; no byte approximation is used.</summary>
    public Func<string, int>? CountContextTokens { get; set; }
    public OpenAiLiveDelegationBehavior NewDelegationBehavior { get; set; } = OpenAiLiveDelegationBehavior.Queue;
    public ProviderEndpointOptions Connection { get; set; } = new("https://api.openai.com/v1/responses");
    /// <summary>Responses model configuration. The runner owns input, tools, stream, store and continuation.
    /// Requires model. Instructions are supplemented with a short spoken-answer contract.</summary>
    public JsonObject Responses { get; set; } = new() { ["model"] = "gpt-6-luna", ["max_output_tokens"] = 2048 };
    /// <summary>Optional application-owned HTTP client, never disposed by the provider.</summary>
    public HttpClient? HttpClient { get; set; }
    public int MaxRounds { get; set; } = 8;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
}
