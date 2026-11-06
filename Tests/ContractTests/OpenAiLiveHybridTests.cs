using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

internal static partial class OpenAiLiveTests
{
    public static async Task RunHybridAsync()
    {
        await VerifyJournalAsync();
        await VerifyRawContextRefreshAsync();
        foreach (var mode in new[] { "refresh", "cancel", "supersede", "ignore", "queue-budget" })
            await VerifyHybridCoordinatorAsync(mode);
        Console.WriteLine("Hybrid library contracts passed: original/app/visual context, fresh actions/results, per-delegation routing, queue budget, token-based handoff and durable action records.");
    }

    private static async Task VerifyJournalAsync()
    {
        var tool = new HybridTool();
        var checkpoints = new List<IReadOnlyList<OpenAiLiveToolExecution>>();
        var journal = new OpenAiLiveToolJournal { CheckpointAsync = (records, _) => { checkpoints.Add(records); return Task.CompletedTask; } };
        using var cancellation = new CancellationTokenSource();
        tool.Action = () => { cancellation.Cancel(); return Task.FromResult("action-completed"); };
        var result = await journal.ExecuteAsync("call-one", tool, "{}", cancellation.Token);
        Check(result == "action-completed" && journal.Records.Single().State == OpenAiLiveToolExecutionState.Completed,
            "action completes and checkpoints even if model was cancelled");
        var restored = new OpenAiLiveToolJournal(checkpoints.Last());
        Check(await restored.ExecuteAsync("call-one", tool, "{}") == result && tool.Calls == 1, "restored completed call cannot replay");
        Check(checkpoints.First().Single().State == OpenAiLiveToolExecutionState.Started, "checkpoint before execution");
        try { await restored.ExecuteAsync("call-one", tool, "different"); throw new Exception("Expected call identity rejection"); }
        catch (InvalidOperationException) { }
        var uncertain = new OpenAiLiveToolJournal(checkpoints.First());
        try { await uncertain.ExecuteAsync("call-one", tool, "{}"); throw new Exception("Expected unresolved action rejection"); }
        catch (InvalidOperationException) { }
        Check(tool.Calls == 1 && uncertain.Records.Single().State == OpenAiLiveToolExecutionState.Uncertain, "restart reconciles unfinished actions before retry");
        var failing = new OpenAiLiveToolJournal { CheckpointAsync = (_, _) => throw new IOException("storage unavailable") };
        try { await failing.ExecuteAsync("not-started", tool, "{}"); throw new Exception("Expected checkpoint failure"); }
        catch (IOException) { }
        Check(tool.Calls == 1 && failing.Records.Count == 0, "failed checkpoint prevents action");
    }

    private static async Task VerifyHybridCoordinatorAsync(string mode)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://localhost:{port}/"); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contextArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new List<string>();
        var errors = new List<string>();
        var tool = new HybridTool();
        var calls = new List<string>();
        var longFacts = new string('ä', 300); // 600 bytes; must not be rejected as a 500-byte payload.
        var server = Task.Run(async () => {
            using var socket = (await (await listener.GetContextAsync()).AcceptWebSocketAsync(null)).WebSocket;
            await ReadAsync(socket, timeout.Token);
            await WriteAsync(socket, """{"type":"session.started","session":{"id":"hybrid"}}""", timeout.Token);
            await WriteAsync(socket, """{"type":"session.input_transcript.delta","delta":"Friday","start_ms":0,"end_ms":100}""", timeout.Token);
            await WriteAsync(socket, """{"type":"session.delegation.created","delegation":{"id":"first","target":"client"},"offset_ms":100}""", timeout.Token);
            await firstStarted.Task.WaitAsync(timeout.Token);
            if (mode == "refresh")
                await WriteAsync(socket, """{"type":"session.input_transcript.delta","delta":"Thursday, not Friday","start_ms":100,"end_ms":200}""", timeout.Token);
            await WriteAsync(socket, """{"type":"session.delegation.created","delegation":{"id":"second","target":"client"},"offset_ms":200}""", timeout.Token);
            while (true)
            {
                var command = await ReadAsync(socket, timeout.Token);
                var type = command["type"]!.GetValue<string>();
                if (type is "session.thinking.append" or "session.commentary.append")
                {
                    if (type == "session.commentary.append")
                    {
                        output.Add(command["content"]!.GetValue<string>());
                        if (mode is "cancel" or "supersede" or "queue-budget" || output.Count == 2) finished.TrySetResult();
                    }
                    await WriteAsync(socket, new JsonObject { ["type"] = type.Replace(".append", ".appended"), ["client_event_id"] = command["event_id"]!.GetValue<string>() }.ToJsonString(), timeout.Token);
                }
                if (type == "session.close")
                {
                    await WriteAsync(socket, """{"type":"session.closed","usage":{"seconds":1}}""", timeout.Token);
                    await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
                    return;
                }
            }
        });
        await using var provider = new OpenAiLiveProvider("test");
        provider.OnError = errors.Add;
        provider.OnTranscriptDelta = delta => { if (delta.Delta.Contains("Thursday")) contextArrived.TrySetResult(); };
        provider.OnBackendActivity = activity => {
            if (activity.DelegationId == "second" && activity.State == "queued") routed.TrySetResult();
            if (activity.DelegationId == "second" && activity.State == "ignored") finished.TrySetResult();
        };
        await provider.ConnectAsync(new OpenAiLiveSettings { Connection = new($"ws://localhost:{port}/live"), Tools = [tool],
            ClientBackend = new() {
                Timeout = mode == "queue-budget" ? TimeSpan.FromMilliseconds(150) : TimeSpan.FromSeconds(5),
                CountContextTokens = content => content == longFacts ? 300 : 40,
                ResolveDelegationAsync = (context, _) => Task.FromResult(context.Delegation.Id == "second" ? mode switch {
                    "cancel" => OpenAiLiveDelegationBehavior.Cancel, "supersede" => OpenAiLiveDelegationBehavior.Supersede,
                    "ignore" => OpenAiLiveDelegationBehavior.Ignore, _ => OpenAiLiveDelegationBehavior.Queue } : OpenAiLiveDelegationBehavior.Queue),
                Handler = async (context, ct) => {
                    calls.Add(context.Delegation.Id);
                    if (calls.Count == 1)
                    {
                        firstStarted.TrySetResult();
                        if (mode is "cancel" or "supersede") await Task.Delay(Timeout.Infinite, ct);
                        else if (mode == "queue-budget") await release.Task; // a running tool need not accept cancellation
                        else await release.Task.WaitAsync(ct);
                        return "STALE-Friday";
                    }
                    if (mode == "refresh")
                    {
                        Check(context.Transcript.ToJsonString().Contains("Thursday") && context.Transcript.ToJsonString().Contains("SELECTED_DOCUMENT_9472"), "latest speech and application context reach arbitrary adapters");
                        await context.ExecuteToolAsync("same-action", tool.Name, "{}", ct);
                    }
                    return longFacts;
                }
            } });
        await firstStarted.Task.WaitAsync(timeout.Token);
        await routed.Task.WaitAsync(timeout.Token);
        if (mode == "refresh")
        {
            await contextArrived.Task.WaitAsync(timeout.Token);
            await provider.AppendThinkingAsync("SELECTED_DOCUMENT_9472", cancellationToken: timeout.Token);
        }
        if (mode == "queue-budget") await Task.Delay(350, timeout.Token);
        release.TrySetResult();
        await finished.Task.WaitAsync(timeout.Token);
        await provider.DisconnectAsync(); await server;
        Check(errors.Count == 0 && provider.FinalUsageConfirmed, mode + ": normal finalization; final=" + provider.FinalUsageConfirmed + " errors=" + string.Join(" | ", errors));
        if (mode == "refresh") Check(!output.Contains("STALE-Friday") && tool.Calls == 1 && calls.Count == 3, "stale result suppressed; refreshed/queued calls retain action identity");
        if (mode == "cancel") Check(calls.Count == 1 && output.Single().Contains("abgebrochen"), "per-delegation cancellation bypasses pending work and preserves outcomes");
        if (mode == "supersede") Check(calls.SequenceEqual(["first", "second"]) && output.Single() == longFacts, "per-delegation supersession cancels old continuation");
        if (mode == "queue-budget") Check(calls.SequenceEqual(["first", "second"]) && output.Single() == longFacts, "queued job gets execution budget after an uncancellable action finishes");
    }

    private sealed class HybridTool : IVoiceTool
    {
        public string Name => "hybrid_action";
        public string Description => "An in-memory contract action.";
        public Type ArgsType => typeof(HybridArgs);
        public int Calls;
        public Func<Task<string>> Action = () => Task.FromResult("action-done");
        public Task<string> ExecuteAsync(string argumentsJson) { Calls++; return Action(); }
    }
    private sealed class HybridArgs { }
}
