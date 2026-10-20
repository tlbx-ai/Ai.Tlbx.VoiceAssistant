using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.Inference;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Reflection;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Protocol;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Translation;
using VoiceMessage = Ai.Tlbx.VoiceAssistant.Models.ChatMessage;

namespace Ai.Tlbx.VoiceAssistant.Demo.Web.Services;

public sealed class MistralDemoBackend(IAiInferenceClient client)
{
    public async Task<string> RespondAsync(IReadOnlyList<VoiceMessage> history, string instructions,
        IReadOnlyList<IVoiceTool> tools, Action<VoiceMessage> onMessage, Action<Models.UsageReport> onUsage, CancellationToken ct)
    {
        var translator = new OpenAiToolTranslator();
        var definitions = tools.Select(tool => {
            var definition = (Provider.OpenAi.Protocol.ToolDefinition)translator.TranslateToolDefinition(tool, ToolSchemaInferrer.InferSchema(tool.ArgsType));
            var schema = JsonNode.Parse(JsonSerializer.Serialize(definition, OpenAiJsonContext.Default.ToolDefinition))!["parameters"]!;
            using var doc = JsonDocument.Parse(schema.ToJsonString());
            return new Inference.ToolDefinition { Name = tool.Name, Description = tool.Description, ParametersSchema = doc.RootElement.Clone() };
        }).ToArray();
        var response = await client.CompleteWithToolsAsync(new CompletionRequest {
            Model = AiModel.Gpt6Luna, ReasoningEffort = "low", ServiceTier = "default", MaxTokens = 2048,
            ToolIterationObserver = new UsageObserver(onUsage),
            SystemMessage = instructions + " Antworte in kurzen gesprochenen Sätzen ohne Markdown. Du bist ein KI-Sprachassistent. Demo-Werkzeuge liefern simulierte Daten; benenne sie als solche.",
            // Inference 2.6 omits plain assistant records from Responses history. Preserve their
            // exact content as application-owned reference data, as in the existing handoff demo.
            Messages = history.Where(m => m.Role is "user" or "assistant").Select(m => new Inference.ChatMessage {
                Role = ChatRole.User, Content = m.Role == "user" ? m.Content : "Previous assistant answer (reference data):\n" + m.Content }).ToArray()
        }, definitions, async call => {
            ct.ThrowIfCancellationRequested();
            var tool = tools.SingleOrDefault(t => t.Name == call.Name);
            onMessage(new($"Calling tool: {call.Name}\nArguments: {call.Arguments}", "assistant", call.Id, call.Name));
            var result = tool == null ? "Unknown tool" : await tool.ExecuteAsync(call.Arguments);
            onMessage(new(result, "tool", call.Id, call.Name));
            return new ToolCallResult { ToolCallId = call.Id, Result = result, IsError = tool == null };
        }, maxIterations: 8, ct: ct);
        return response.Content;
    }

    private sealed class UsageObserver(Action<Models.UsageReport> report) : IToolIterationObserver
    {
        public ValueTask OnStartingAsync(ToolIterationRequest request, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask OnFinishedAsync(ToolIterationResult result)
        {
            if (result.Usage is { } usage) report(new Models.UsageReport {
                ProviderId = "openai", ModelId = "gpt-6-luna", ServiceTier = usage.ServiceTier ?? "default",
                OperationType = Models.UsageOperationType.DelegatedResponse,
                ReportedInputTokens = usage.InputTokens, ReportedOutputTokens = usage.OutputTokens,
                CacheReadInputTokens = usage.CacheReadTokens, CacheCreationInputTokens = usage.CacheWriteTokens,
                ReasoningOutputTokens = usage.ThinkingTokens, IsFinal = result.ResponseCompleted });
            return ValueTask.CompletedTask;
        }
    }
}
