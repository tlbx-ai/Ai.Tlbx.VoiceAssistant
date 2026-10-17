using System.Reflection;
using System.Text.Json.Nodes;
using Ai.Tlbx.Inference;
using Ai.Tlbx.VoiceAssistant.Demo.Web.Services;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using VoiceMessage = Ai.Tlbx.VoiceAssistant.Models.ChatMessage;

internal static class DemoHandoffTests
{
    public static async Task RunAsync()
    {
        var client = DispatchProxy.Create<IAiInferenceClient, InferenceProbe>();
        var probe = (InferenceProbe)(object)client;
        var backend = new InferenceHandoffBackend(client);
        var published = new List<string>();
        var reports = new List<Ai.Tlbx.VoiceAssistant.Models.UsageReport>();
        backend.OnResult = published.Add;
        var calls = 0;
        probe.Run = async request =>
        {
            var iteration = ++calls;
            await request.ToolIterationObserver!.OnStartingAsync(new() { Iteration = 1, Request = request, Tools = [] }, default);
            if (iteration == 1)
                backend.ObserveTranscript(new("user", " und die freigegebene Menge", 100, 200));
            else if (!request.Messages.Any(m => m.Content.Contains("freigegebene Menge")) ||
                     !request.Messages.Any(m => m.Content == "Nur der Code"))
                throw new InvalidOperationException("Later user fragments or retained agent history were lost.");
            await request.ToolIterationObserver.OnFinishedAsync(new() { Iteration = 1, Usage = new() { InputTokens = 10, OutputTokens = 5 }, ResponseCompleted = true });
            return new() { Content = iteration == 1 ? "Nur der Code" : "Code ZEDER-8426, Menge 863", Usage = default, Iterations = 1, Messages = request.Messages };
        };
        backend.ObserveTranscript(new("user", "Nenne den Code", 0, 100));
        var answer = await backend.RunAsync(new("demo-session", new("job", "client", 100, null),
            new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "Nenne den Code" }),
            Array.Empty<VoiceMessage>(), [], (_, _) => Task.CompletedTask, reports.Add), default);
        if (calls != 2 || published.Count != 1 || published[0] != answer || !answer.Contains("863") ||
            reports.Count != 2 || reports.Select(r => r.OperationId).Distinct().Count() != 2)
            throw new InvalidOperationException("Partial task answer leaked, short result was summarized again, or per-call usage was lost.");
        Console.WriteLine("Demo handoff passed: late speech fragments, retained history, complete answer publication and distinct per-call usage.");
    }

    public class InferenceProbe : DispatchProxy
    {
        public Func<CompletionRequest, Task<ToolExecutionResponse<string>>> Run = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == nameof(IAiInferenceClient.CompleteWithToolsAsync) && method.ReturnType == typeof(Task<ToolExecutionResponse<string>>)
                ? Run((CompletionRequest)args![0]!) : throw new NotSupportedException(method?.Name);
    }
}
