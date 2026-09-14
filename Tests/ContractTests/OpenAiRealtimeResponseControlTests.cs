using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

internal static class OpenAiRealtimeResponseControlTests
{
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<string>();
        var server = Task.Run(async () =>
        {
            try
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                async Task<JsonObject> Read()
                {
                    var buffer = new byte[65536];
                    using var stream = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                        stream.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    return JsonNode.Parse(stream.ToArray())!.AsObject();
                }
                async Task Send(string json) => await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, timeout.Token);
                async Task Expect(string type)
                {
                    var message = await Read();
                    if (message["type"]?.GetValue<string>() != type)
                        throw new InvalidOperationException($"Erwartet {type}, erhalten {message}");
                }

                var configuration = await Read();
                var vad = configuration["session"]!["audio"]!["input"]!["turn_detection"]!;
                if (vad["create_response"]!.GetValue<bool>() || vad["interrupt_response"]!.GetValue<bool>() || vad["idle_timeout_ms"] != null)
                    throw new InvalidOperationException("Server darf keine konkurrierenden Antworten erzeugen.");
                await Send("""{"type":"session.updated","session":{}}""");
                await Send("""{"type":"input_audio_buffer.committed","item_id":"u1"}""");
                await Expect("response.create");
                await Send("""{"type":"response.created","response":{"id":"r1"}}""");
                await Send("""{"type":"response.done","response":{"id":"r1","status":"completed","output":[{"type":"function_call","name":"probe","call_id":"c1","arguments":"{}"}]}}""");
                await started.Task.WaitAsync(timeout.Token);
                // Neue Sprache trifft ein, während ein reales asynchrones Tool noch arbeitet.
                await Send("""{"type":"input_audio_buffer.speech_started","item_id":"u2"}""");
                await Send("""{"type":"input_audio_buffer.speech_stopped","item_id":"u2"}""");
                await Send("""{"type":"input_audio_buffer.committed","item_id":"u2"}""");
                release.TrySetResult();
                await Expect("conversation.item.create");
                await Expect("response.create");
                // Noch ohne response.created muss der reservierte Antwortslot geschützt sein.
                await Expect("response.cancel");
                await Send("""{"type":"response.created","response":{"id":"r2"}}""");
                await Send("""{"type":"response.done","response":{"id":"r2","status":"cancelled","output":[]}}""");
                await Expect("response.create");
                await Send("""{"type":"response.created","response":{"id":"r3"}}""");
                await Send("""{"type":"response.done","response":{"id":"r3","status":"completed","output":[]}}""");
                finished.TrySetResult();
                var closing = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), timeout.Token);
                if (closing.MessageType != WebSocketMessageType.Close) throw new InvalidOperationException("Unerwartete weitere Antwort.");
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }
            catch (Exception ex) { finished.TrySetException(ex); throw; }
        }, timeout.Token);
        await using var provider = new OpenAiVoiceProvider("test");
        provider.OnError = errors.Add;
        await provider.ConnectAsync(new OpenAiVoiceSettings
        {
            ClientResponseControl = true,
            UseEphemeralKey = false,
            Connection = new ProviderEndpointOptions($"ws://localhost:{port}/"),
            TurnDetection = new TurnDetection { IdleTimeoutMs = 6000 },
            Tools = [new DelayedTool(started, release)]
        });
        await finished.Task.WaitAsync(timeout.Token);
        await provider.DisconnectAsync();
        await server;
        if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors));
        Console.WriteLine("Realtime WebSocket: VAD/tool overlap, response reservation and cancellation completion passed.");
    }

    private sealed class DelayedTool(TaskCompletionSource started, TaskCompletionSource release) : IVoiceTool
    {
        public string Name => "probe";
        public string Description => "Test";
        public Type ArgsType => typeof(object);
        public async Task<string> ExecuteAsync(string argumentsJson)
        {
            started.TrySetResult();
            await release.Task;
            return "ok";
        }
    }
}
