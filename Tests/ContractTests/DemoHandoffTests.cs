using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Ai.Tlbx.Inference;
using Ai.Tlbx.VoiceAssistant.Demo.Web.Services;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using VoiceMessage = Ai.Tlbx.VoiceAssistant.Models.ChatMessage;

internal static class DemoHandoffTests
{
    public static async Task RunAsync()
    {
        await LateFragmentsAsync();
        await FullArtifactAndStreamingAsync();
        Console.WriteLine("Demo handoff passed: live text before completion, coherent quiet context, complete artifact follow-ups, late speech and distinct per-call usage.");
    }

    private static OpenAiLiveBackendContext Context(string session, string text,
        List<Ai.Tlbx.VoiceAssistant.Models.UsageReport> reports) => new(session, new("job", "client", 100, null),
        new JsonArray(new JsonObject { ["role"] = "user", ["text"] = text }),
        Array.Empty<VoiceMessage>(), [], (_, _) => Task.CompletedTask, reports.Add);

    private static async Task LateFragmentsAsync()
    {
        var client = DispatchProxy.Create<IAiInferenceClient, InferenceProbe>();
        var probe = (InferenceProbe)(object)client;
        var backend = new InferenceHandoffBackend(client);
        var published = new List<string>();
        var reports = new List<Ai.Tlbx.VoiceAssistant.Models.UsageReport>();
        backend.OnResult = published.Add;
        var calls = 0;
        probe.Run = (request, ct) =>
        {
            var iteration = ++calls;
            if (iteration > 1 && (!request.Messages.Any(m => m.Content?.Contains("freigegebene Menge") == true) ||
                                 !request.Messages.Any(m => m.Content == "Nur der Code")))
                throw new InvalidOperationException("Later speech or retained agent history was lost.");
            return TextStream(request, iteration == 1 ? "Nur der Code" : "Code ZEDER-8426, Menge 863", () => {
                if (iteration == 1) backend.ObserveTranscript(new("user", " und die freigegebene Menge", 100, 200));
            }, null, ct);
        };
        backend.ObserveTranscript(new("user", "Nenne den Code", 0, 100));
        var answer = await backend.RunAsync(Context("late-session", "Nenne den Code", reports), default);
        if (calls != 2 || published.Count != 1 || published[0] != answer || !answer.Contains("863") ||
            reports.Count != 2 || reports.Select(r => r.OperationId).Distinct().Count() != 2)
            throw new InvalidOperationException("Partial answer leaked, short result was summarized again, or usage was lost.");
    }

    private static async Task FullArtifactAndStreamingAsync()
    {
        var client = DispatchProxy.Create<IAiInferenceClient, InferenceProbe>();
        var probe = (InferenceProbe)(object)client;
        var backend = new InferenceHandoffBackend(client) { ForwardDraftContext = true };
        var reports = new List<Ai.Tlbx.VoiceAssistant.Models.UsageReport>();
        var contexts = new List<string>();
        var traces = new List<HandoffTraceEvent>();
        var firstText = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnTrace = trace => { traces.Add(trace); if (trace.Kind == "text_delta") firstText.TrySetResult(); };
        var poem = string.Concat(Enumerable.Repeat("Europa schreibt am neuen Morgen,\nund trägt Ideen durch die Zeit.\n\n", 20));
        var calls = 0;
        probe.Run = (request, ct) =>
        {
            var iteration = ++calls;
            if (iteration == 3 && (!request.Messages.Any(m => m.Role == ChatRole.Assistant && m.Content == poem) ||
                !request.Messages.Any(m => m.Role == ChatRole.User && m.Content?.Contains("Wurde das") == true) ||
                !request.Messages.Any(m => m.Role == ChatRole.User && m.Content?.Contains("completed_results") == true &&
                    JsonNode.Parse(m.Content[(m.Content.IndexOf('\n') + 1)..])?["completed_results"]?[0]?["full_text"]?.GetValue<string>() == poem)))
                throw new InvalidOperationException("Follow-up lost the full completed artifact behind the shortened speech.");
            var text = iteration == 1 ? poem : iteration == 2 ? "Das Gedicht ist vollständig erstellt und steht im Chat." : "Ja, das vollständige Gedicht liegt vor; ich lese es nicht vor.";
            return TextStream(request, text, null, iteration == 1 ? release.Task : null, ct);
        };
        var context = Context("artifact-session", "Lass einen Subagenten ein Gedicht schreiben", reports) with {
            ReportContextAsync = (text, _) => { contexts.Add(text); return Task.CompletedTask; }
        };
        backend.ObserveTranscript(new("user", "Lass einen Subagenten ein Gedicht schreiben", 0, 100));
        var pending = backend.RunAsync(context, default);
        await firstText.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (pending.IsCompleted) throw new InvalidOperationException("Model output was buffered until completion.");
        release.SetResult();
        var handoff = await pending;
        var cached = await backend.RunAsync(context, default);
        if (cached != handoff || calls != 2) throw new InvalidOperationException("A queued delegation without new user input repeated completed work.");
        if (handoff == poem || !handoff.Contains("vollständig") || contexts.Count < 2 ||
            contexts.Any(c => Encoding.UTF8.GetByteCount(c) > 500) ||
            !contexts.Any(c => c.Contains("Unfertiger Agent-Entwurf")))
            throw new InvalidOperationException("Long artifact was recited/rewritten, or coherent background context was not forwarded.");
        backend.ResetTranscript();
        backend.ObserveTranscript(new("user", "Wurde das vollständig erstellt? Nicht vorlesen.", 200, 400));
        var providerSnapshot = (JsonArray)context.Transcript.DeepClone();
        providerSnapshot.Add(new JsonObject { ["role"] = "application", ["text"] = "Agent-Entwurf" });
        providerSnapshot.Add(new JsonObject { ["role"] = "user", ["text"] = "Wurde das vollständig erstellt? Nicht vorlesen." });
        var followup = await backend.RunAsync(context with { SessionId = "artifact-reconnected", Transcript = providerSnapshot }, default);
        if (!followup.Contains("vollständige Gedicht") || calls != 3 || reports.Count != 3 ||
            !traces.Any(t => t.Kind == "context_accepted"))
            throw new InvalidOperationException("Artifact follow-up or confirmed quiet-context accounting failed.");
    }

    private static async IAsyncEnumerable<ToolStreamEvent> TextStream(CompletionRequest request, string text,
        Action? afterFirst, Task? pause, [EnumeratorCancellation] CancellationToken ct)
    {
        var observer = request.ToolIterationObserver!;
        await observer.OnStartingAsync(new() { Iteration = 1, Request = request, Tools = [] }, ct);
        for (var at = 0; at < text.Length; at += 24)
        {
            yield return new TextDeltaEvent(text.Substring(at, Math.Min(24, text.Length - at)));
            if (at == 0) { afterFirst?.Invoke(); if (pause != null) await pause.WaitAsync(ct); }
        }
        await observer.OnFinishedAsync(new() { Iteration = 1, Usage = new() { InputTokens = 10, OutputTokens = 5 }, ResponseCompleted = true });
        yield return new CompletedEvent(new() { InputTokens = 10, OutputTokens = 5 });
    }

    public class InferenceProbe : DispatchProxy
    {
        public Func<CompletionRequest, CancellationToken, IAsyncEnumerable<ToolStreamEvent>> Run = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == nameof(IAiInferenceClient.StreamWithToolsAsync) && method.ReturnType == typeof(IAsyncEnumerable<ToolStreamEvent>)
                ? Run((CompletionRequest)args![0]!, (CancellationToken)args[4]!) : throw new NotSupportedException(method?.Name);
    }
}
