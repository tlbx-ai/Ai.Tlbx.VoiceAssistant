using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

internal static class OpenAiRealtimeToolContinuationTests
{
    public static async Task RunAsync()
    {
        await OpenAiRealtimeSessionIsolationTests.RunAsync();
        await OpenAiRealtimeFailureTests.RunAsync();
        foreach (var clientControl in new[] { false, true }) await RunCaseAsync(clientControl);
        Console.WriteLine("Realtime tools: large intact results, batch continuation, dedup, failed/cancelled turns passed in both VAD modes.");
    }
    private static async Task RunCaseAsync(bool clientControl)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var tool = new Tool();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var server = Task.Run(async () =>
        {
            try
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                async Task<JsonObject> Read()
                {
                    using var stream = new MemoryStream();
                    var buffer = new byte[4096];
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                        stream.Write(buffer, 0, received.Count);
                    } while (!received.EndOfMessage);
                    return JsonNode.Parse(stream.ToArray())!.AsObject();
                }
                async Task Send(string json) => await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, timeout.Token);
                await Read();
                await Send("""{"type":"response.created","response":{"id":"r1"}}""");
                await Send("""{"type":"response.function_call_arguments.done","name":"probe","call_id":"c1","arguments":"{}"}""");
                await Send("""{"type":"response.function_call_arguments.delta","item_id":"i1","call_id":"c1","delta":"{\"n\":"}""");
                await Send("""{"type":"response.function_call_arguments.delta","item_id":"i2","call_id":"c2","delta":"{\"n\":999}"}""");
                await Send("""{"type":"response.function_call_arguments.delta","item_id":"i1","call_id":"c1","delta":"999}"}""");
                var done = """{"type":"response.done","response":{"id":"r1","status":"completed","output":[{"id":"i1","type":"function_call","name":"probe","call_id":"c1","arguments":"{\"n\":1}"},{"id":"i2","type":"function_call","name":"probe","call_id":"c2","arguments":"{\"n\":2}"},{"id":"i1","type":"function_call","name":"probe","call_id":"c1","arguments":"{\"n\":1}"}]}}""";
                await Send(done);
                await Send(done);
                for (var n = 1; n <= 2; n++)
                {
                    var result = await Read();
                    Check(result["type"]!.GetValue<string>() == "conversation.item.create", "Output before continuation.");
                    Check(result["item"]!["call_id"]!.GetValue<string>() == $"c{n}", "Call identity.");
                    Check(result["item"]!["output"]!.GetValue<string>() == Tool.Payload, "Intact Unicode payload >40,000 chars.");
                }
                Check((await Read())["type"]!.GetValue<string>() == "response.create", "One continuation.");
                await Send(done);
                await Send("""{"type":"response.created","response":{"id":"r2"}}""");
                await Send("""{"type":"response.done","response":{"id":"r2","status":"cancelled","output":[{"type":"function_call","name":"probe","call_id":"cancelled","arguments":"{}"}]}}""");
                await Send("""{"type":"response.created","response":{"id":"r3"}}""");
                await Send("""{"type":"response.done","response":{"id":"r3","status":"failed","output":[{"type":"function_call","name":"probe","call_id":"failed","arguments":"{}"}]}}""");
                await Send("""{"type":"input_audio_buffer.speech_started"}""");
                finished.TrySetResult();
                var closing = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[4096]), timeout.Token);
                Check(closing.MessageType == WebSocketMessageType.Close, "No duplicate output or continuation.");
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }
            catch (Exception ex) { finished.TrySetException(ex); throw; }
        });
        await using var provider = new OpenAiVoiceProvider("test");
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.OnInterruptDetected = () => processed.TrySetResult();
        provider.OnError = errors.Enqueue;
        await provider.ConnectAsync(new OpenAiVoiceSettings
        {
            UseEphemeralKey = false, ClientResponseControl = clientControl,
            Connection = new ProviderEndpointOptions($"ws://localhost:{port}/"), Tools = [tool]
        });
        await finished.Task.WaitAsync(timeout.Token);
        await processed.Task.WaitAsync(timeout.Token);
        Check(tool.Calls == 2, "Exactly two executions.");
        Check(tool.Arguments.SequenceEqual(new[] { "{\"n\":1}", "{\"n\":2}" }), "Final per-call arguments stay separate despite interleaved deltas.");
        await provider.DisconnectAsync();
        await server;
        Check(errors.IsEmpty, string.Join("; ", errors));
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class Tool : IVoiceTool
    {
        public static string Payload { get; } = "{\"report\":\"" + string.Concat(Enumerable.Repeat("Änderung 日本語 payload. ", 4000)) + "\"}";
        public int Calls;
        public readonly List<string> Arguments = new();
        public string Name => "probe";
        public string Description => "Test";
        public Type ArgsType => typeof(object);
        public Task<string> ExecuteAsync(string argumentsJson)
        {
            Interlocked.Increment(ref Calls);
            Arguments.Add(argumentsJson);
            return Task.FromResult(Payload);
        }
    }
}
