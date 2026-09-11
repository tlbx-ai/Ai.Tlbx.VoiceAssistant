using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

internal static partial class OpenAiLiveTests
{
    public static async Task RunLiveBudgetAsync()
    {
        var output = new string('x', 33000);
        var tool = new BudgetTool([output], null);
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new OpenAiLiveProvider();
        var apiErrors = new ConcurrentQueue<string>();
        provider.OnError = _ => failure.TrySetResult();
        provider.OnEventReceived = e => { if (e["type"]?.GetValue<string>() == "error") apiErrors.Enqueue(e.ToJsonString()); };
        await provider.ConnectAsync(new OpenAiLiveSettings {
            Instructions = "Remain silent. Use the backend for the test.", Tools = [tool],
            Responses = new JsonObject { ["model"] = "gpt-5.6-luna", ["instructions"] = "Call budget exactly once for this synthetic test.",
                ["tool_choice"] = "required", ["parallel_tool_calls"] = false, ["max_output_tokens"] = 512 }
        });
        await provider.SendEventAsync(new JsonObject { ["type"] = "response.create" });
        await failure.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await provider.DisconnectAsync();
        Check(tool.Calls == 1 && provider.ToolResults.Single().Output == output, "real oversized tool executes once and keeps complete output");
        Check(provider.ToolResults.Single().SubmissionState == OpenAiLiveToolSubmissionState.BudgetExceeded, "real oversized result blocked locally");
        Check(provider.FinalUsageConfirmed && !provider.IsConnected && apiErrors.IsEmpty, "real overflow closes with final usage and no API error cascade");
        Console.WriteLine("GPT-Live real budget smoke passed: one tool execution, full output retained, no rejected submission or continuation, final usage confirmed.");
    }

    private static async Task VerifyBackendFailuresAsync()
    {
        await VerifyBackendFailureAsync("oversized", [new string('x', 33000)], 0);
        await VerifyBackendFailureAsync("cumulative", [new string('x', 17000), new string('y', 17000)], 1);
        await VerifyBackendFailureAsync("cumulative across responses", [new string('x', 17000), new string('y', 17000)], 1, chained: true);
        await VerifyBackendFailureAsync("utf8", [string.Concat(Enumerable.Repeat("ä😀", 5500))], 0);
        await VerifyBackendFailureAsync("delayed rejection", ["{\"ok\":true}"], 1, delayedRejection: true);
        await VerifyBackendFailureAsync("batch rejection", ["first", "second", "must not execute"], 1, earlyRejection: true);
        await VerifyBackendFailureAsync("item count", Enumerable.Repeat("x", 129).ToArray(), 128);
        Console.WriteLine("GPT-Live backend failure regressions passed.");
    }

    private static async Task VerifyBackendFailureAsync(string scenario, string[] outputs, int expectedSubmissions,
        bool delayedRejection = false, bool earlyRejection = false, bool chained = false)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var errors = new ConcurrentQueue<string>();
        var received = new List<JsonObject>();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new BudgetTool(outputs, earlyRejection ? failed.Task : null);
        await using var provider = new OpenAiLiveProvider("test-key");
        provider.OnError = error => { errors.Enqueue(error); failed.TrySetResult(); };

        var batchIndex = 0;
        async Task BatchAsync(WebSocket socket)
        {
            await WriteAsync(socket, """{"type":"response.event","delegation_id":"d","event":{"type":"response.created","response":{"id":"r"}}}""", timeout.Token);
            for (var i = chained ? batchIndex : 0; i < (chained ? batchIndex + 1 : outputs.Length); i++)
                await WriteAsync(socket, new JsonObject { ["type"] = "response.event", ["delegation_id"] = "d", ["event"] = new JsonObject {
                    ["type"] = "response.output_item.done", ["item"] = new JsonObject { ["type"] = "function_call", ["call_id"] = $"call_{i}", ["name"] = "budget", ["arguments"] = "{}" } } }.ToJsonString(), timeout.Token);
            await WriteAsync(socket, """{"type":"response.event","delegation_id":"d","event":{"type":"response.completed","response":{"id":"r","output":[]}}}""", timeout.Token);
        }

        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            await ReadAsync(socket, timeout.Token);
            await WriteAsync(socket, """{"type":"session.started","session":{"id":"budget_test"}}""", timeout.Token);
            await ReadAsync(socket, timeout.Token); // application trigger, after ConnectAsync is ready
            await BatchAsync(socket);
            string? submissionId = null;
            while (true)
            {
                var command = await ReadAsync(socket, timeout.Token);
                received.Add(command);
                var type = command["type"]!.GetValue<string>();
                if (type == "response.item.create") submissionId = command["event_id"]!.GetValue<string>();
                if (chained && type == "response.create") { batchIndex++; await BatchAsync(socket); }
                if ((earlyRejection && type == "response.item.create") || (delayedRejection && type == "response.create"))
                {
                    // Deterministically reject after a continuation is already on the wire in the delayed case.
                    await WriteAsync(socket, new JsonObject { ["type"] = "error", ["error"] = new JsonObject {
                        ["code"] = "response_input_buffer_full", ["client_event_id"] = submissionId, ["message"] = "server rejected input" } }.ToJsonString(), timeout.Token);
                    if (delayedRejection)
                        await WriteAsync(socket, new JsonObject { ["type"] = "error", ["error"] = new JsonObject {
                            ["code"] = "function_call_outputs_required", ["client_event_id"] = command["event_id"]!.GetValue<string>() } }.ToJsonString(), timeout.Token);
                    await BatchAsync(socket); // repeat original calls: never repeat the action
                }
                if (type == "session.close")
                {
                    await WriteAsync(socket, """{"type":"session.closed","reason":"close_requested","usage":{"seconds":1}}""", timeout.Token);
                    return;
                }
            }
        });
        await provider.ConnectAsync(new OpenAiLiveSettings { Connection = new($"ws://localhost:{port}/"),
            Responses = new JsonObject { ["model"] = "backend" }, Tools = [tool], CloseTimeout = TimeSpan.FromSeconds(2) });
        await provider.SendEventAsync(new JsonObject { ["type"] = "response.create" });
        await failed.Task.WaitAsync(timeout.Token);
        await server.WaitAsync(timeout.Token);
        await provider.DisconnectAsync(); // concurrent automatic/application cleanup is safe
        await tool.Finished.Task.WaitAsync(timeout.Token);
        while (provider.ToolResults.Any(r => r.Output == null)) await Task.Delay(10, timeout.Token);
        var snapshots = provider.ToolResults;
        Check(provider.BackendContinuationError != null && !provider.IsConnected && provider.FinalUsageConfirmed, scenario + ": bounded graceful failure cleanup");
        Check(errors.Count == 1, scenario + ": one terminal error, no retry cascade");
        Check(received.Count(c => c["type"]!.GetValue<string>() == "response.item.create") == expectedSubmissions, scenario + ": budget/rejection stops submissions");
        Check(received.Count(c => c["type"]!.GetValue<string>() == "response.create") == (delayedRejection || chained ? 1 : 0), scenario + ": no continuation after known failure");
        Check(tool.Calls <= outputs.Length && (!earlyRejection || tool.Calls <= 2), scenario + ": no action replay or remaining batch execution");
        foreach (var result in snapshots.Where(r => r.Output != null))
            Check(result.Output == outputs[int.Parse(result.CallId[5..])], scenario + ": immutable complete output retained");
        if (delayedRejection || earlyRejection)
            Check(snapshots.Single(r => r.CallId == "call_0").SubmissionState == OpenAiLiveToolSubmissionState.Rejected, scenario + ": rejection correlated to original output");
        else
            Check(snapshots.Last().SubmissionState == OpenAiLiveToolSubmissionState.BudgetExceeded, scenario + ": local budget state is explicit");
        try { await provider.SendEventAsync(new JsonObject { ["type"] = "response.create" }); throw new Exception("Expected stopped backend"); }
        catch (InvalidOperationException) { }
    }

    private sealed class BudgetTool(string[] outputs, Task? waitBeforeSecond) : IVoiceTool
    {
        public int Calls;
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "budget";
        public string Description => "Return a deterministic test payload";
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
        public Type ArgsType => typeof(LookupArgs);
        public async Task<string> ExecuteAsync(string argumentsJson)
        {
            var index = Interlocked.Increment(ref Calls) - 1;
            if (index == 1 && waitBeforeSecond != null) await waitBeforeSecond.WaitAsync(TimeSpan.FromSeconds(5));
            Finished.TrySetResult();
            return outputs[index];
        }
    }
}
