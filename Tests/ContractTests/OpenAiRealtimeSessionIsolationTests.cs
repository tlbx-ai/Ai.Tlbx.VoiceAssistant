using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

internal static class OpenAiRealtimeSessionIsolationTests
{
    public static async Task RunAsync()
    {
        await using (var disconnected = new OpenAiVoiceProvider("test"))
        {
            var send = typeof(OpenAiVoiceProvider).GetMethod("SendMessageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var failed = false;
            try { await (Task)send.Invoke(disconnected, ["{\"type\":\"response.create\"}", CancellationToken.None, null, null, null])!; }
            catch (InvalidOperationException) { failed = true; }
            if (!failed) throw new InvalidOperationException("Disconnected control sends must fail visibly.");
            typeof(OpenAiVoiceProvider).GetField("_responseRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(disconnected, true);
            var start = typeof(OpenAiVoiceProvider).GetMethod("TryStartRequestedResponseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            try { await (Task)start.Invoke(disconnected, null)!; } catch (InvalidOperationException) { }
            if ((bool)typeof(OpenAiVoiceProvider).GetField("_hasActiveResponse", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(disconnected)!)
                throw new InvalidOperationException("Failed response.create must release its reservation without replay.");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var tool = new SlowTool();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            try
            {
                for (var session = 0; session < 2; session++)
                {
                    var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                    using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                    async Task<JsonObject?> Read()
                    {
                        var buffer = new byte[65536];
                        using var stream = new MemoryStream();
                        WebSocketReceiveResult received;
                        do
                        {
                            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                            if (received.MessageType == WebSocketMessageType.Close) return null;
                            stream.Write(buffer, 0, received.Count);
                        } while (!received.EndOfMessage);
                        return JsonNode.Parse(stream.ToArray())!.AsObject();
                    }
                    async Task Send(string json) => await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, timeout.Token);
                    await Read();
                    await Send("""{"type":"response.created","response":{"id":"r1"}}""");
                    await Send("""{"type":"response.done","response":{"id":"r1","status":"completed","output":[{"id":"i1","type":"function_call","name":"slow","call_id":"c1","arguments":"{}"}]}}""");
                    if (session == 1)
                    {
                        var result = await Read();
                        if (result?["item"]?["output"]?.GetValue<string>() != "fresh-session") throw new InvalidOperationException("Old tool output crossed session boundary.");
                        if ((await Read())?["type"]?.GetValue<string>() != "response.create") throw new InvalidOperationException("Fresh session continuation missing.");
                        finished.TrySetResult();
                    }
                    if (await Read() != null) throw new InvalidOperationException("Unexpected stale result or continuation.");
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
                }
            }
            catch (Exception ex) { finished.TrySetException(ex); throw; }
        });
        await using var provider = new OpenAiVoiceProvider("test");
        var settings = new OpenAiVoiceSettings { UseEphemeralKey = false, Connection = new ProviderEndpointOptions($"ws://localhost:{port}/"), Tools = [tool] };
        await provider.ConnectAsync(settings);
        await tool.Started.Task.WaitAsync(timeout.Token);
        await provider.DisconnectAsync();
        await provider.ConnectAsync(settings);
        tool.Release.TrySetResult();
        await finished.Task.WaitAsync(timeout.Token);
        await provider.DisconnectAsync();
        await server;
        if (tool.Calls != 2) throw new InvalidOperationException("Reconnect must reset call identity without replaying an old execution.");
        Console.WriteLine("Realtime transport failure and reconnect isolation passed.");
    }
    private sealed class SlowTool : IVoiceTool
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public string Name => "slow";
        public string Description => "test";
        public Type ArgsType => typeof(object);
        public async Task<string> ExecuteAsync(string argumentsJson)
        {
            if (Interlocked.Increment(ref Calls) > 1) return "fresh-session";
            Started.TrySetResult();
            await Release.Task;
            return "obsolete-session";
        }
    }
}
