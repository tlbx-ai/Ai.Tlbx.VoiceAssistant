using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Protocol;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Translation;
using Ai.Tlbx.VoiceAssistant.Reflection;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;

internal sealed class OpenAiDirectRealtimeSessionRegistry : IOpenAiDirectRealtimeClientActionDispatcher
{
    private readonly ConcurrentDictionary<string, DirectRealtimeSessionState> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<PendingClientActionKey, TaskCompletionSource<ClientActionResult>> _pendingClientActions = new();
    private readonly OpenAiDirectRealtimeOptions _options;
    private readonly OpenAiToolTranslator _toolTranslator = new();

    public OpenAiDirectRealtimeSessionRegistry(OpenAiDirectRealtimeOptions options)
    {
        _options = options;
    }

    public void Register(
        string voiceSessionId,
        OpenAiDirectRealtimeSessionSpec spec,
        DateTimeOffset expiresAt)
    {
        _sessions[voiceSessionId] = new DirectRealtimeSessionState(spec, expiresAt);
    }

    public bool TryGet(string voiceSessionId, out DirectRealtimeSessionState session)
    {
        if (_sessions.TryGetValue(voiceSessionId, out session!))
        {
            if (session.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return true;
            }

            _ = RemoveAsync(voiceSessionId, CancellationToken.None);
        }

        session = null!;
        return false;
    }

    public async Task RemoveAsync(string voiceSessionId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryRemove(voiceSessionId, out var session))
        {
            return;
        }

        FailPendingClientActions(voiceSessionId, new InvalidOperationException($"Voice session '{voiceSessionId}' ended."));

        try
        {
            if (session.Spec.EventSink is not null)
            {
                await session.Spec.EventSink.OnSessionEndedAsync(voiceSessionId, cancellationToken);
            }
        }
        finally
        {
            await session.Spec.DisposeAsync();
        }
    }

    public SessionConfig BuildSessionConfig(OpenAiVoiceSettings settings)
    {
        return new SessionConfig
        {
            Type = "realtime",
            Model = settings.Model.ToApiString(),
            OutputModalities = ["audio"],
            Instructions = BuildInstructions(settings),
            MaxOutputTokens = settings.MaxTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "inf",
            Truncation = settings.AutomaticContextTruncation
                ? new TruncationConfig
                {
                    Type = "retention_ratio",
                    RetentionRatio = settings.RetentionRatio
                }
                : new TruncationConfig { Type = "disabled" },
            ToolChoice = "auto",
            ParallelToolCalls = settings.ParallelToolCalls,
            Tools = settings.Tools.Select(tool =>
                (ToolDefinition)_toolTranslator.TranslateToolDefinition(tool, ToolSchemaInferrer.InferSchema(tool.ArgsType))).ToList(),
            Audio = new AudioConfig
            {
                Input = new AudioInputConfig
                {
                    Format = new AudioInputFormatConfig { Type = "audio/pcm", Rate = 24000 },
                    NoiseReduction = new NoiseReductionConfig { Type = settings.NoiseReduction.ToApiString() },
                    Transcription = settings.InputAudioTranscription.Enabled
                        ? new TranscriptionConfig
                        {
                            Model = settings.InputAudioTranscription.Model.ToApiString(),
                            Prompt = settings.InputAudioTranscription.Model.SupportsTranscriptionPrompt()
                                ? settings.InputAudioTranscription.Prompt ?? settings.TranscriptionHint
                                : null,
                            Language = settings.MostLikelySpokenLanguage
                        }
                        : null,
                    TurnDetection = new TurnDetectionConfig
                    {
                        Type = settings.TurnDetection.Type,
                        Threshold = settings.TurnDetection.Threshold,
                        PrefixPaddingMs = settings.TurnDetection.PrefixPaddingMs,
                        SilenceDurationMs = settings.TurnDetection.SilenceDurationMs,
                        IdleTimeoutMs = settings.TurnDetection.IdleTimeoutMs,
                        CreateResponse = settings.TurnDetection.CreateResponse,
                        InterruptResponse = settings.TurnDetection.InterruptResponse
                    }
                },
                Output = new AudioOutputConfig
                {
                    Voice = settings.Voice.ToString().ToLowerInvariant(),
                    Speed = settings.TalkingSpeed
                }
            },
            Reasoning = settings.ReasoningEffort.HasValue
                ? new OpenAiReasoningConfig { Effort = settings.ReasoningEffort.Value.ToApiString() }
                : null,
            Include = ["item.input_audio_transcription.logprobs"]
        };
    }

    public async Task<JsonElement> RequestClientActionAsync(
        string? voiceSessionId,
        string action,
        object? args,
        bool requiresConfirmation = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(voiceSessionId))
        {
            throw new InvalidOperationException("No active direct realtime voice session is available for client action execution.");
        }

        if (!TryGet(voiceSessionId, out var session) || session.ControlSocket?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException($"No active direct realtime control socket is registered for voice session '{voiceSessionId}'.");
        }

        var requestId = Guid.NewGuid().ToString("N")[..8];
        var pendingKey = new PendingClientActionKey(voiceSessionId, requestId);
        var tcs = new TaskCompletionSource<ClientActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingClientActions[pendingKey] = tcs;

        try
        {
            var request = new DirectRealtimeClientActionRequest
            {
                RequestId = requestId,
                Action = action,
                Args = args,
                RequiresConfirmation = requiresConfirmation
            };

            await SendJsonAsync(session.ControlSocket, request, OpenAiDirectRealtimeJsonContext.Default.DirectRealtimeClientActionRequest, cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.ClientActionTimeout);

            using var registration = timeoutCts.Token.Register(static state =>
            {
                ((TaskCompletionSource<ClientActionResult>)state!).TrySetCanceled();
            }, tcs);

            var result = await tcs.Task;
            if (result.Declined)
            {
                using var document = JsonDocument.Parse("{\"declined\":true,\"message\":\"User declined this action\"}");
                return document.RootElement.Clone();
            }

            return result.Result;
        }
        finally
        {
            _pendingClientActions.TryRemove(pendingKey, out _);
        }
    }

    public void HandleClientActionResponse(
        string voiceSessionId,
        string requestId,
        JsonElement result,
        string? error,
        bool declined)
    {
        var pendingKey = new PendingClientActionKey(voiceSessionId, requestId);
        if (!_pendingClientActions.TryGetValue(pendingKey, out var pending))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            pending.TrySetException(new InvalidOperationException(error));
            return;
        }

        pending.TrySetResult(new ClientActionResult(result, declined));
    }

    private static string BuildInstructions(OpenAiVoiceSettings settings)
    {
        var preambleInstructions = BuildToolCallPreambleInstructions(settings.ToolCallPreambleMode);
        return string.IsNullOrWhiteSpace(preambleInstructions)
            ? settings.Instructions
            : settings.Instructions + Environment.NewLine + Environment.NewLine + preambleInstructions;
    }

    private static string? BuildToolCallPreambleInstructions(ToolCallPreambleMode mode)
    {
        return mode switch
        {
            ToolCallPreambleMode.ProviderDefault => null,
            ToolCallPreambleMode.Disabled =>
                "# Tool call preambles" + Environment.NewLine +
                "- Do not speak a preamble before tool calls." + Environment.NewLine +
                "- Call tools directly when the user's intent is clear.",
            ToolCallPreambleMode.BeforeToolBurst =>
                "# Tool call preambles" + Environment.NewLine +
                "- If a user request requires a burst of multiple tool calls, say one short bridge sentence before the burst." + Environment.NewLine +
                "- Summarize the overall action, not each individual tool call." + Environment.NewLine +
                "- Do not narrate every tool call in the burst; keep working quietly after the bridge sentence." + Environment.NewLine +
                "- For a single lightweight tool call, call the tool silently unless the user needs context.",
            ToolCallPreambleMode.ForLongRunningTools =>
                "# Tool call preambles" + Environment.NewLine +
                "- Say one short bridge sentence before a tool call only when it may take noticeable time or change user-visible state." + Environment.NewLine +
                "- Do not speak preambles for quick lookups or lightweight tool calls.",
            ToolCallPreambleMode.BeforeEveryToolCall =>
                "# Tool call preambles" + Environment.NewLine +
                "- Before any tool call, say one short natural sentence describing what you are about to do.",
            _ => null
        };
    }

    private static async Task SendJsonAsync<T>(
        WebSocket webSocket,
        T message,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(message, jsonTypeInfo);
        var bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private void FailPendingClientActions(string voiceSessionId, Exception exception)
    {
        foreach (var entry in _pendingClientActions)
        {
            if (!string.Equals(entry.Key.VoiceSessionId, voiceSessionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (_pendingClientActions.TryRemove(entry.Key, out var pending))
            {
                pending.TrySetException(exception);
            }
        }
    }
}

internal sealed class DirectRealtimeSessionState
{
    public DirectRealtimeSessionState(OpenAiDirectRealtimeSessionSpec spec, DateTimeOffset expiresAt)
    {
        Spec = spec;
        ExpiresAt = expiresAt;
    }

    public OpenAiDirectRealtimeSessionSpec Spec { get; }

    public DateTimeOffset ExpiresAt { get; }

    public WebSocket? ControlSocket { get; set; }
}

internal sealed record PendingClientActionKey(string VoiceSessionId, string RequestId);

internal sealed record ClientActionResult(JsonElement Result, bool Declined);
