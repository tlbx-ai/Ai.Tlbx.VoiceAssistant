using System.Collections.Concurrent;
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

internal static class ConversationStartTests
{
    public static async Task RunAsync()
    {
        await using (var unsupported = new Ai.Tlbx.VoiceAssistant.VoiceAssistant(new ContractAudioHardware(), new FailingConnectVoiceProvider()))
        {
            Check(!unsupported.SupportsConversationStart && !unsupported.IsConversationStartReady, "Explicit unsupported capability");
            await Reject<NotSupportedException>(() => unsupported.StartConversationAsync());
        }
        await NormalConversationAsync();
        foreach (var race in new[] { "cancel", "speech", "speech-interrupt", "automatic", "automatic-committed", "stop", "disconnect", "transport" })
            await PendingStartAsync(race);
        await BusyAsync();
        await ServerRejectionAsync();
        Console.WriteLine("Conversation start: readiness, single start, transcript/audio/usage, VAD/barge-in, tools, cancellation, races and reconnect passed.");
    }

    private static async Task NormalConversationAsync()
    {
        await using var peer = new Peer();
        await Reject<InvalidOperationException>(() => peer.Assistant.StartConversationAsync());
        await Reject<InvalidOperationException>(() => peer.Provider.StartConversationAsync());
        await peer.ConnectAsync();
        Check(peer.Assistant.SupportsConversationStart && !peer.Assistant.IsConversationStartReady, "Wait for session.updated");
        await Reject<InvalidOperationException>(() => peer.Assistant.StartConversationAsync());
        await peer.Send("""{"type":"session.updated","session":{}}""");
        Check(peer.Assistant.IsConversationStartReady, "Configured session is ready");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Reject<OperationCanceledException>(() => peer.Assistant.StartConversationAsync(cancelled.Token));
        Check(peer.Assistant.IsConversationStartReady, "Pre-cancelled request does not consume opening");

        var request = await peer.Assistant.StartConversationAsync();
        var create = await peer.Read();
        Check(create?["type"]?.GetValue<string>() == "response.create", "First event is response.create, no dummy input");
        Check(create!["event_id"]!.GetValue<string>() == request && create["response"]!["metadata"]!["conversation_start_request_id"]!.GetValue<string>() == request, "Request correlation");
        Check(create["response"]!.AsObject().Count == 1, "No context, tools, modality or instructions override");
        await Reject<InvalidOperationException>(() => peer.Assistant.StartConversationAsync());
        await peer.Send("{\"type\":\"response.created\",\"response\":{\"id\":\"opening\",\"metadata\":{\"conversation_start_request_id\":\"" + request + "\"}}}");
        await peer.Send("""{"type":"response.output_audio.delta","delta":"AQACAAMA"}""");
        await peer.Send("""{"type":"response.output_audio_transcript.done","transcript":"Ich bin der KI-Assistent. Wie kann ich helfen?"}""");
        await peer.Send("""{"type":"response.done","response":{"id":"opening","status":"completed","output":[],"usage":{"input_tokens":10,"output_tokens":20,"total_tokens":30}}}""");
        Check(peer.Hardware.PlayedAudioCount == 1 && peer.Messages.Any(m => m.Role == ChatMessage.AssistantRole), "Normal playback and transcript");
        Check(peer.Usage.Count == 1, "Normal final usage");
        await Reject<InvalidOperationException>(() => peer.Assistant.StartConversationAsync());
        await peer.Send("""{"type":"input_audio_buffer.committed"}""");
        Check((await peer.Read())?["type"]?.GetValue<string>() == "response.create", "Normal follow-up");
        await peer.Send("""{"type":"response.created","response":{"id":"followup"}}""");
        await peer.Send("""{"type":"input_audio_buffer.speech_started"}""");
        Check((await peer.Read())?["type"]?.GetValue<string>() == "response.cancel", "Normal barge-in");
        await peer.Send("""{"type":"response.done","response":{"id":"followup","status":"cancelled","output":[]}}""");
        await peer.CloseAsync();
    }

    private static async Task PendingStartAsync(string race)
    {
        await using var peer = new Peer();
        await peer.ConnectAsync(clientControl: !race.StartsWith("automatic"), interrupt: race == "speech-interrupt");
        await peer.Send("""{"type":"session.updated","session":{}}""");
        var gate = (SemaphoreSlim)typeof(OpenAiVoiceProvider).GetField("_sendLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(peer.Provider)!;
        await gate.WaitAsync();
        using var cancel = new CancellationTokenSource();
        var opening = peer.Assistant.StartConversationAsync(cancel.Token);
        Check(!opening.IsCompleted, "Opening waits for send lock");
        await Reject<InvalidOperationException>(() => peer.Assistant.StartConversationAsync());
        Task? stop = null;
        if (race == "cancel") cancel.Cancel();
        if (race is "speech" or "speech-interrupt") await peer.Send("""{"type":"input_audio_buffer.speech_started"}""");
        if (race == "automatic") await peer.Send("""{"type":"response.created","response":{"id":"automatic"}}""");
        if (race == "automatic-committed") await peer.Send("""{"type":"input_audio_buffer.committed"}""");
        if (race == "stop") stop = peer.Assistant.StopAsync();
        if (race == "disconnect") stop = peer.Provider.DisconnectAsync();
        if (race == "transport") peer.Socket!.Abort();
        gate.Release();
        if (race is "cancel" or "stop" or "disconnect") await Reject<OperationCanceledException>(() => opening);
        else await Reject<Exception>(() => opening);
        Check(!peer.Assistant.IsConversationStartReady, "No retry of consumed/uncertain attempt");
        if (race != "transport") await peer.CloseAsync(stop);
        else await peer.Assistant.StopAsync();
        await peer.ConnectAsync();
        await peer.Send("""{"type":"session.updated","session":{}}""");
        Check(peer.Assistant.IsConversationStartReady, "Reconnect resets capability");
        await peer.CloseAsync(); // StartAsync alone remains silent; no stale response crosses sessions.
    }

    private static async Task BusyAsync()
    {
        await using var peer = new Peer();
        var tool = new SlowTool();
        await peer.ConnectAsync(tools: [tool]);
        await peer.Send("""{"type":"session.updated","session":{}}""");
        await peer.Send("""{"type":"input_audio_buffer.speech_started"}""");
        await Reject<InvalidOperationException>(() => peer.Assistant.StartConversationAsync());
        await peer.Send("""{"type":"input_audio_buffer.speech_stopped"}""");
        await peer.Send("""{"type":"response.created","response":{"id":"tool","metadata":null}}""");
        await Reject<InvalidOperationException>(() => peer.Assistant.StartConversationAsync());
        await peer.Send("""{"type":"response.done","response":{"id":"tool","status":"completed","output":[{"type":"function_call","name":"slow","call_id":"c","arguments":"{}"}]}}""");
        await tool.Started.Task.WaitAsync(peer.Timeout.Token);
        await Reject<InvalidOperationException>(() => peer.Assistant.StartConversationAsync());
        tool.Release.TrySetResult();
        Check((await peer.Read())?["type"]?.GetValue<string>() == "conversation.item.create", "Tool output remains on normal path");
        Check((await peer.Read())?["type"]?.GetValue<string>() == "response.create", "Tool continuation remains on normal path");
        await peer.CloseAsync();
    }

    private static async Task ServerRejectionAsync()
    {
        await using (var peer = new Peer())
        {
            await peer.ConnectAsync(clientControl: false);
            await peer.Send("""{"type":"session.updated","session":{}}""");
            await peer.Send("""{"type":"input_audio_buffer.committed"}""");
            await Reject<InvalidOperationException>(() => peer.Assistant.StartConversationAsync());
            await peer.CloseAsync(); // VAD owns the pending response even before response.created.
        }
        await using (var peer = new Peer())
        {
            await peer.ConnectAsync();
            await peer.Send("""{"type":"session.updated","session":{}}""");
            var request = await peer.Assistant.StartConversationAsync();
            await peer.Read();
            await peer.Send("{\"type\":\"error\",\"error\":{\"code\":\"conversation_already_has_active_response\",\"message\":\"Test race\",\"event_id\":\"" + request + "\"}}");
            await Reject<InvalidOperationException>(() => peer.Assistant.StartConversationAsync());
            await peer.CloseAsync(); // No replay or response.cancel to force the opening.
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class SlowTool : IVoiceTool
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "slow";
        public string Description => "Contract probe";
        public Type ArgsType => typeof(object);
        public async Task<string> ExecuteAsync(string json) { Started.TrySetResult(); await Release.Task; return "ok"; }
    }
    private sealed class Peer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _barriers = new();
        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(15));
        public WebSocket? Socket { get; private set; }
        public ContractAudioHardware Hardware { get; } = new();
        public OpenAiVoiceProvider Provider { get; } = new("test");
        public Ai.Tlbx.VoiceAssistant.VoiceAssistant Assistant { get; }
        public ConcurrentQueue<ChatMessage> Messages { get; } = new();
        public ConcurrentQueue<UsageReport> Usage { get; } = new();
        private readonly int _port;
        public Peer()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            _listener.Prefixes.Add($"http://localhost:{_port}/"); _listener.Start();
            Assistant = new(Hardware, Provider);
            Assistant.OnMessageAdded = Messages.Enqueue; Assistant.OnUsageReceived = Usage.Enqueue;
            Provider.OnProtocolEvent = e =>
            {
                if (e.EventId is string id && _barriers.TryRemove(id, out var done)) done.TrySetResult();
            };
        }
        public async Task ConnectAsync(bool clientControl = true, bool interrupt = true, List<IVoiceTool>? tools = null)
        {
            var starting = Assistant.StartAsync(new OpenAiVoiceSettings
            {
                UseEphemeralKey = false, ClientResponseControl = clientControl,
                Instructions = "Use the existing guide.", Tools = tools ?? [],
                TurnDetection = new() { InterruptResponse = interrupt },
                Connection = new ProviderEndpointOptions($"ws://localhost:{_port}/")
            });
            var context = await _listener.GetContextAsync().WaitAsync(Timeout.Token);
            Socket?.Dispose(); Socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            Check((await Read())?["type"]?.GetValue<string>() == "session.update", "Session configuration first");
            await starting.WaitAsync(Timeout.Token);
        }
        public async Task Send(string json)
        {
            await Socket!.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, Timeout.Token);
            var id = Guid.NewGuid().ToString("N");
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _barriers[id] = done;
            await Socket.SendAsync(Encoding.UTF8.GetBytes($$"""{"type":"conversation.item.created","event_id":"{{id}}"}"""), WebSocketMessageType.Text, true, Timeout.Token);
            await done.Task.WaitAsync(Timeout.Token);
        }
        public async Task<JsonObject?> Read()
        {
            var buffer = new byte[65536]; using var stream = new MemoryStream(); WebSocketReceiveResult result;
            do
            {
                result = await Socket!.ReceiveAsync(new ArraySegment<byte>(buffer), Timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                stream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            return JsonNode.Parse(stream.ToArray())!.AsObject();
        }
        public async Task CloseAsync(Task? stopping = null)
        {
            stopping ??= Assistant.StopAsync();
            Check(await Read() == null, "No unexpected response, dummy input or cancellation");
            await Socket!.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", Timeout.Token);
            await stopping.WaitAsync(Timeout.Token);
        }
        public async ValueTask DisposeAsync()
        {
            Socket?.Abort(); Socket?.Dispose(); _listener.Close();
            await Assistant.DisposeAsync(); Timeout.Dispose();
        }
    }
}
