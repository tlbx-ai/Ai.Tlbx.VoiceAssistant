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
    /// Return a concise factual spoken result, at most 500 UTF-8 bytes. Use context callbacks for progress and usage.
    /// The application owns authorization, durable context, model selection and disposal of its agent.</summary>
    public Func<OpenAiLiveBackendContext, CancellationToken, Task<string>>? Handler { get; set; }
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
