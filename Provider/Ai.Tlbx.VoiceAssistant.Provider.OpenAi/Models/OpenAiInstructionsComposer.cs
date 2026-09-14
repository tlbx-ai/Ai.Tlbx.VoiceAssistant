using System;
using Ai.Tlbx.VoiceAssistant.Models;
namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

/// <summary>Immutable provenance of the exact final instructions. Contains application content; do not log by default.</summary>
public sealed record OpenAiInstructionsSnapshot(string ApplicationText, string? LibraryPreamble, string FinalText);

/// <summary>Shared deterministic prompt composition for WebSocket and browser sessions.</summary>
public static class OpenAiInstructionsComposer
{
    public static OpenAiInstructionsSnapshot Compose(OpenAiVoiceSettings settings)
    {
        var addition = settings.AppendToolCallPreambleInstructions
            ? settings.ToolCallPreambleInstructionsOverride ?? BuildToolCallPreambleInstructions(settings.ToolCallPreambleMode)
            : null;
        if (string.IsNullOrWhiteSpace(addition)) addition = null;
        return new(settings.Instructions, addition, addition is null ? settings.Instructions
            : settings.Instructions + Environment.NewLine + Environment.NewLine + addition);
    }
        private static string? BuildToolCallPreambleInstructions(ToolCallPreambleMode mode)
        {
            return mode switch
            {
                ToolCallPreambleMode.ProviderDefault => null,
                ToolCallPreambleMode.Disabled =>
                    "# Tool call preambles" + Environment.NewLine +
                    "- Do not speak a preamble before, between, or during tool calls." + Environment.NewLine +
                    "- Call tools directly when the user's intent is clear and remain silent until the final answer or a required clarification." + Environment.NewLine +
                    "- Never repeat, paraphrase, or acknowledge the user's request as filler while tools are running.",
                ToolCallPreambleMode.BeforeToolBurst =>
                    "# Tool call preambles" + Environment.NewLine +
                    "- If a user request requires a burst of multiple tool calls, say one short bridge sentence before the first call." + Environment.NewLine +
                    "- Summarize the overall action, not each individual tool call." + Environment.NewLine +
                    "- Do not narrate every tool call in the burst; keep working quietly until you have the final result or need clarification." + Environment.NewLine +
                    "- For a single lightweight tool call, call the tool silently unless the user would benefit from an update.",
                ToolCallPreambleMode.ForLongRunningTools =>
                    "# Tool call preambles" + Environment.NewLine +
                    "- Say one short bridge sentence before a tool call only when it may take noticeable time or affects visible external state." + Environment.NewLine +
                    "- Do not speak preambles for quick lookups or lightweight tool calls." + Environment.NewLine +
                    "- Describe the action, not internal reasoning.",
                ToolCallPreambleMode.BeforeEveryToolCall =>
                    "# Tool call preambles" + Environment.NewLine +
                    "- Before any tool call, say one short natural sentence describing the action, then call the tool immediately." + Environment.NewLine +
                    "- Vary wording and avoid filler." + Environment.NewLine +
                    "- Describe the action, not internal reasoning.",
                _ => null
            };
        }

}
