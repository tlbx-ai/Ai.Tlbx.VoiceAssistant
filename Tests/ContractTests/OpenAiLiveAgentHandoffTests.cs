using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Managers;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

internal static partial class OpenAiLiveTests
{
    private static async Task VerifyApplicationAgentAsync()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/"); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<string>();
        var commentary = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        var ledger = new UsageManager();
        var server = Task.Run(async () => {
            var accepted = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using var socket = (await accepted.AcceptWebSocketAsync(null)).WebSocket;
            var startup = await ReadAsync(socket, timeout.Token);
            Check(startup["session"]!["delegation"]!["type"]!.GetValue<string>() == "client", "application agent uses client mode");
            await WriteAsync(socket, """{"type":"session.started","session":{"id":"agent-session"}}""", timeout.Token);
            await WriteAsync(socket, """{"type":"session.input_transcript.delta","delta":"Prüfe die Projektakte","start_ms":0,"end_ms":100}""", timeout.Token);
            await WriteAsync(socket, """{"type":"session.delegation.created","delegation":{"id":"first","target":"client"},"offset_ms":100}""", timeout.Token);
            await firstStarted.Task.WaitAsync(timeout.Token);
            await WriteAsync(socket, """{"type":"session.input_transcript.delta","delta":"Berücksichtige auch die Kosten","start_ms":100,"end_ms":200}""", timeout.Token);
            await WriteAsync(socket, """{"type":"session.delegation.created","delegation":{"id":"second","target":"client"},"offset_ms":200}""", timeout.Token);
            await WriteAsync(socket, """{"type":"session.delegation.created","delegation":{"id":"second","target":"client"},"offset_ms":200}""", timeout.Token);
            while (true)
            {
                var command = await ReadAsync(socket, timeout.Token);
                var type = command["type"]?.GetValue<string>();
                if (type == "session.commentary.append")
                {
                    commentary.Enqueue(command["content"]!.GetValue<string>());
                    await WriteAsync(socket, new JsonObject { ["type"] = "session.commentary.appended", ["client_event_id"] = command["event_id"]!.GetValue<string>() }.ToJsonString(), timeout.Token);
                    if (command["content"]!.GetValue<string>() == "Ergebnis zwei") secondDone.TrySetResult();
                }
                if (type == "session.close")
                {
                    await WriteAsync(socket, """{"type":"session.closed","usage":{"seconds":4}}""", timeout.Token);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token); return;
                }
            }
        });
        await using var provider = new OpenAiLiveProvider("test");
        provider.OnError = errors.Enqueue;
        provider.OnUsageReceived = ledger.AddReport;
        provider.OnBackendActivity = a => {
            if (a.DelegationId == "second" && a.State == "queued") secondQueued.TrySetResult();
            if (a.DelegationId == "third" && a.State == "cancelled") thirdCancelled.TrySetResult();
        };
        await provider.ConnectAsync(new OpenAiLiveSettings {
            Connection = new($"ws://localhost:{port}/live"),
            ClientBackend = new() { Handler = async (context, ct) => {
                calls.Enqueue(context.Delegation.Id);
                Check(context.SessionId == "agent-session", "agent gets session identity");
                if (context.Delegation.Id == "first")
                {
                    Check(context.Transcript.Count == 1, "first snapshot excludes future user speech");
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(ct);
                    await context.ReportProgressAsync("Die Akte ist geprüft", ct);
                }
                else if (context.Delegation.Id == "second") Check(context.Transcript.Count == 2, "follow-up sees original role-labelled speech");
                else { thirdStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
                context.ReportUsage(new() { ProviderId = "custom", OperationId = context.Delegation.Id, OperationType = UsageOperationType.DelegatedResponse,
                    ReportedInputTokens = 10, ReportedOutputTokens = 2, IsFinal = true });
                return context.Delegation.Id == "first" ? "Ergebnis eins" : "Ergebnis zwei";
            } }
        });
        await secondQueued.Task.WaitAsync(timeout.Token);
        Check(calls.SequenceEqual(["first"]), "new delegation queues without cancelling the active agent");
        releaseFirst.TrySetResult();
        await secondDone.Task.WaitAsync(timeout.Token);
        Check(calls.SequenceEqual(["first", "second"]), "FIFO execution and duplicate delegation suppression");
        Check(commentary.SequenceEqual(["Die Akte ist geprüft", "Ergebnis eins", "Ergebnis zwei"]), "acknowledged progress and final results delivered in order");
        // Inject another delegation through the same production dispatch path; it must cancel without commentary.
        var dispatch = typeof(OpenAiLiveProvider).GetMethod("StartClientDelegation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        dispatch.Invoke(provider, [new OpenAiLiveDelegation("third", "client", 300, null)]);
        await thirdStarted.Task.WaitAsync(timeout.Token);
        provider.CancelClientBackend();
        await thirdCancelled.Task.WaitAsync(timeout.Token);
        await provider.DisconnectAsync(); await server;
        Check(errors.IsEmpty && provider.FinalUsageConfirmed, "custom agent cancellation does not fail Live finalization");
        Check(ledger.TotalTokens == 24 && commentary.Count == 3, "custom backend reports usage once; cancelled work cannot append a result");
        Console.WriteLine("Application agent handoff contracts passed: metadata, transcript snapshots, progress, FIFO follow-ups, duplicate suppression, usage and cancellation.");
    }
}
