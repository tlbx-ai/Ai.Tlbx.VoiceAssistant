using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Ai.Tlbx.Inference;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Demo.Web.Services;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

internal static partial class DemoHandoffTests
{
    private static async Task HybridRegressionsAsync()
    {
        await ApplicationContextAsync();
        await CancellationRecordsAsync();
        await CorrectionBeforeToolAsync();
        Console.WriteLine("Inference adapter regressions passed: complete application context, one-call speech facts, cancelled tool history and corrections before actions.");
    }

    private static async Task ApplicationContextAsync()
    {
        var client = DispatchProxy.Create<IAiInferenceClient, HybridInferenceProbe>();
        var probe = (HybridInferenceProbe)(object)client;
        var backend = new InferenceHandoffBackend(client);
        var reports = new List<Ai.Tlbx.VoiceAssistant.Models.UsageReport>();
        var transcript = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "Prüfe das ausgewählte Dokument" },
            new JsonObject { ["role"] = "application", ["kind"] = "thinking", ["text"] = "SELECTED_DOCUMENT_9472" },
            new JsonObject { ["visual_input"] = new JsonObject { ["role"] = "user", ["content"] = "application-image-item" } });
        var calls = 0;
        var spoken = new string('ä', 300);
        probe.Run = (request, _, ct) => {
            calls++;
            if (!request.Messages.Any(m => m.Content?.Contains("SELECTED_DOCUMENT_9472") == true && m.Content.Contains("application-image-item")) || request.JsonSchema == null)
                throw new InvalidOperationException("Application/visual context or native structured output was lost.");
            return TextStream(request, "Full document findings", null, null, ct, spoken);
        };
        var answer = await backend.RunAsync(Context("app-context", "fallback", reports) with {
            GetLatestTranscript = () => (JsonArray)transcript.DeepClone() }, default);
        if (answer != spoken || calls != 1 || reports.Count != 1)
            throw new InvalidOperationException("A result over 500 bytes caused another model call or truncation.");
    }

    private static async Task CancellationRecordsAsync()
    {
        var client = DispatchProxy.Create<IAiInferenceClient, HybridInferenceProbe>();
        var probe = (HybridInferenceProbe)(object)client;
        var backend = new InferenceHandoffBackend(client);
        var tool = new HybridCounterTool();
        using var cancelled = new CancellationTokenSource();
        tool.OnExecuted = cancelled.Cancel;
        var calls = 0;
        probe.Run = (request, execute, ct) => {
            if (++calls == 1) return ToolThenCancel(request, execute, tool.Name, ct);
            if (!request.Messages.Any(m => m.Role == ChatRole.Tool && m.Content == "ACTION_DONE_3827") ||
                !request.Messages.Any(m => m.Content?.Contains("tool_actions") == true && m.Content.Contains("ACTION_DONE_3827")))
                throw new InvalidOperationException("An executed action disappeared after model cancellation.");
            return TextStream(request, "Die Aktion ist bereits ausgeführt.", null, null, ct);
        };
        var reports = new List<Ai.Tlbx.VoiceAssistant.Models.UsageReport>();
        try { await backend.RunAsync(Context("cancel-action", "Führe die Aktion aus", reports) with { Tools = [tool] }, cancelled.Token); }
        catch (OperationCanceledException) { }
        var next = Context("cancel-action", "Führe die Aktion aus", reports);
        next.Transcript.Add(new JsonObject { ["role"] = "user", ["text"] = "Wie ist der Stand?" });
        var answer = await backend.RunAsync(next with { Tools = [tool] }, default);
        if (tool.Calls != 1 || !answer.Contains("bereits") || backend.ToolJournal.Records.Single().State != OpenAiLiveToolExecutionState.Completed)
            throw new InvalidOperationException("Cancelled model replayed or forgot the completed tool action.");
    }

    private static async Task CorrectionBeforeToolAsync()
    {
        var client = DispatchProxy.Create<IAiInferenceClient, HybridInferenceProbe>();
        var probe = (HybridInferenceProbe)(object)client;
        var backend = new InferenceHandoffBackend(client);
        var tool = new HybridCounterTool();
        var transcript = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "Prüfe Freitag" });
        var calls = 0;
        var published = new List<string>(); backend.OnResult = published.Add;
        probe.Run = (request, execute, ct) => {
            if (++calls == 1)
            {
                transcript.Add(new JsonObject { ["role"] = "user", ["text"] = "Korrektur: Donnerstag. Keine Aktion ausführen." });
                return ToolThenCancel(request, execute, tool.Name, ct);
            }
            if (!request.Messages.Any(m => m.Content?.Contains("Donnerstag") == true))
                throw new InvalidOperationException("Correction did not reach the next model request.");
            return TextStream(request, "Donnerstag; es wurde keine Aktion ausgeführt.", null, null, ct);
        };
        var answer = await backend.RunAsync(Context("correction", "Prüfe Freitag", []) with {
            Tools = [tool], GetLatestTranscript = () => (JsonArray)transcript.DeepClone() }, default);
        if (calls != 2 || tool.Calls != 0 || published.Count != 1 || !answer.Contains("Donnerstag"))
            throw new InvalidOperationException("Stale action or result survived a correction.");
    }

    private static async IAsyncEnumerable<ToolStreamEvent> ToolThenCancel(CompletionRequest request,
        Func<ToolCallRequest, Task<ToolCallResult>> execute, string name, [EnumeratorCancellation] CancellationToken ct)
    {
        await request.ToolIterationObserver!.OnStartingAsync(new() { Iteration = 1, Request = request, Tools = [] }, ct);
        await execute(new ToolCallRequest { Id = "executed-before-cancel", Name = name, Arguments = "{}" });
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("This probe should stop at cancellation or changed context.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public class HybridInferenceProbe : DispatchProxy
    {
        public Func<CompletionRequest, Func<ToolCallRequest, Task<ToolCallResult>>, CancellationToken, IAsyncEnumerable<ToolStreamEvent>> Run = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == nameof(IAiInferenceClient.StreamWithToolsAsync)
            ? Run((CompletionRequest)args![0]!, (Func<ToolCallRequest, Task<ToolCallResult>>)args[2]!, (CancellationToken)args[4]!)
            : throw new NotSupportedException(method?.Name);
    }
    private sealed class HybridCounterTool : IVoiceTool
    {
        public string Name => "perform_action";
        public string Description => "An in-memory action fixture.";
        public Type ArgsType => typeof(HybridCounterArgs);
        public int Calls;
        public Action? OnExecuted;
        public Task<string> ExecuteAsync(string argumentsJson) { Calls++; OnExecuted?.Invoke(); return Task.FromResult("ACTION_DONE_3827"); }
    }
    private sealed class HybridCounterArgs { }
}
