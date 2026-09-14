using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

internal static class OpenAiRealtimeFailureTests
{
    public static async Task RunAsync()
    {
        await using (var provider = new OpenAiVoiceProvider("test"))
        {
            foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1) })
            {
                var failed = false;
                try { await provider.ConnectAsync(new OpenAiVoiceSettings { ToolExecutionTimeout = timeout }); }
                catch (ArgumentOutOfRangeException) { failed = true; }
                Check(failed, "Nonpositive timeout must fail before connection.");
            }
        }
        await RunCaseAsync(false);
        await RunCaseAsync(true);
        Console.WriteLine("Realtime failure matrix: unknown tool, invalid arguments, exception, uncertain timeout and correlated cancel race passed.");
    }

    private static async Task RunCaseAsync(bool timeOutTool)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var tool = new FailureTool();
        var errors = new ConcurrentQueue<string>();
        var errorSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            try
            {
                var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
                using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                async Task<JsonObject?> Read()
                {
                    using var stream = new MemoryStream();
                    var buffer = new byte[4096];
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
                        if (result.MessageType == WebSocketMessageType.Close) return null;
                        stream.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    return JsonNode.Parse(stream.ToArray())!.AsObject();
                }
                async Task Send(string json) => await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, deadline.Token);
                await Read();
                await Send("""{"type":"response.created","response":{"id":"r1"}}""");
                var done = timeOutTool
                    ? """{"type":"response.done","response":{"id":"r1","status":"completed","output":[{"type":"function_call","name":"failure","call_id":"slow","arguments":"{\"slow\":true}"}]}}"""
                    : """{"type":"response.done","response":{"id":"r1","status":"completed","output":[{"type":"function_call","name":"unknown","call_id":"missing","arguments":"{}"},{"type":"function_call","name":"failure","call_id":"invalid","arguments":"not-json"},{"type":"function_call","name":"failure","call_id":"exception","arguments":"{}"}]}}""";
                await Send(done);
                if (timeOutTool)
                {
                    await errorSeen.Task.WaitAsync(deadline.Token);
                    // A duplicated completion and a fresh server turn must not retry an uncertain action.
                    await Send(done);
                    await Send("""{"type":"response.created","response":{"id":"r2"}}""");
                    await Send("""{"type":"response.done","response":{"id":"r2","status":"completed","output":[{"type":"function_call","name":"failure","call_id":"retry","arguments":"{\"slow\":true}"}]}}""");
                    await Send("""{"type":"input_audio_buffer.committed","item_id":"new-input"}""");
                    tool.Completion.TrySetException(new IOException("late tool fault"));
                    await observed.Task.WaitAsync(deadline.Token);
                }
                else
                {
                    foreach (var expected in new[] { "missing", "invalid", "exception" })
                    {
                        var result = await Read();
                        Check(result?["type"]?.GetValue<string>() == "conversation.item.create", "Defined error result before continuation.");
                        Check(result?["item"]?["call_id"]?.GetValue<string>() == expected, "Error result retains call identity.");
                        var output = result!["item"]!["output"]!.GetValue<string>();
                        Check(expected == "missing" ? output == "Tool not found: unknown" : output.StartsWith("Error: ", StringComparison.Ordinal), "Defined tool error output.");
                    }
                    Check((await Read())?["type"]?.GetValue<string>() == "response.create", "One error-batch continuation.");
                    await Send("""{"type":"response.created","response":{"id":"r2"}}""");
                    await Send("""{"type":"input_audio_buffer.speech_started"}""");
                    var cancel = await Read();
                    Check(cancel?["type"]?.GetValue<string>() == "response.cancel", "Cancellation requested.");
                    var eventId = cancel!["event_id"]!.GetValue<string>();
                    // Natural completion wins the race before the server processes cancellation.
                    await Send("""{"type":"response.done","response":{"id":"r2","status":"completed","output":[]}}""");
                    async Task Error(string code, string referenced) => await Send(new JsonObject { ["type"] = "error", ["error"] = new JsonObject {
                        ["code"] = code, ["event_id"] = referenced, ["message"] = "same localized message" } }.ToJsonString());
                    await Error("response_cancel_not_active", eventId);
                    await Error("invalid_request", eventId);
                    await Error("response_cancel_not_active", "unrelated-client-event");
                    await errorSeen.Task.WaitAsync(deadline.Token);
                }
                await Send("""{"type":"conversation.item.input_audio_transcription.completed","transcript":"barrier"}""");
                finished.TrySetResult();
                Check(await Read() == null, "No extra result, continuation or retry after completion/timeout.");
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
            }
            catch (Exception ex) { finished.TrySetException(ex); throw; }
        });
        await using var client = new OpenAiVoiceProvider("test", (_, text) =>
        {
            if (text.Contains("Detached tool", StringComparison.Ordinal) && text.Contains("late tool fault", StringComparison.Ordinal)) observed.TrySetResult();
        });
        client.OnError = error => { errors.Enqueue(error); if (errors.Count >= (timeOutTool ? 1 : 2)) errorSeen.TrySetResult(); };
        client.OnMessageReceived = message => { if (message.Content == "barrier") processed.TrySetResult(); };
        await client.ConnectAsync(new OpenAiVoiceSettings { UseEphemeralKey = false, ClientResponseControl = true,
            ToolExecutionTimeout = TimeSpan.FromMilliseconds(50), Connection = new ProviderEndpointOptions($"ws://localhost:{port}/"), Tools = [tool] });
        await finished.Task.WaitAsync(deadline.Token);
        await processed.Task.WaitAsync(deadline.Token);
        Check(tool.Calls == (timeOutTool ? 1 : 2), "No repeated action.");
        Check(errors.Count == (timeOutTool ? 1 : 2), "Only expected errors reported; correlated benign cancel suppressed.");
        if (timeOutTool) Check(errors.Single().Contains("uncertain", StringComparison.Ordinal), "Timeout identifies uncertain action outcome.");
        await client.DisconnectAsync();
        await server;
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class FailureTool : IVoiceTool
    {
        public readonly TaskCompletionSource<string> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public string Name => "failure";
        public string Description => "test";
        public Type ArgsType => typeof(object);
        public Task<string> ExecuteAsync(string argumentsJson)
        {
            Interlocked.Increment(ref Calls);
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.TryGetProperty("slow", out _)) return Completion.Task;
            throw new InvalidOperationException("deliberate tool exception");
        }
    }
}
