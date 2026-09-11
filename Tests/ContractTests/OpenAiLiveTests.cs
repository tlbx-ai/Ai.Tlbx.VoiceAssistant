using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

internal static class OpenAiLiveTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Live contract: " + message);
    }

    public static async Task RunAsync()
    {
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var received = new ConcurrentQueue<JsonObject>();
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            Check(context.Request.Url!.Query == "", "no Realtime model query");
            Check(context.Request.Headers["Authorization"] == "Bearer test-live-key", "authentication");
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var start = await ReadAsync(socket, timeout.Token);
            Check(start["type"]!.GetValue<string>() == "session.start", "first event is session.start");
            var session = start["session"]!;
            Check(session["model"]!.GetValue<string>() == "gpt-live-1", "model in session body");
            Check(session["input"]!.AsArray().Count == 2, "history delivered before startup");
            Check(session["audio"]!["format"]!["rate"]!.GetValue<int>() == 24000, "shared audio format");
            Check(session["delegation"]!["responses"]!["tools"]!.AsArray().Count == 1, "Responses tool schema");
            await WriteAsync(socket, """{"type":"session.started","session":{"id":"sess_test"}}""", timeout.Token);
            await WriteAsync(socket, """{"type":"session.input_transcript.delta","delta":" hi ","start_ms":0,"end_ms":100} """, timeout.Token);
            await WriteAsync(socket, """{"type":"session.output_transcript.delta","delta":"hello","start_ms":50,"end_ms":200}""", timeout.Token);
            await WriteAsync(socket, """{"type":"session.output_audio.delta","delta":"AAA="}""", timeout.Token);
            await WriteAsync(socket, """{"type":"session.usage.updated","usage":{"seconds":2}}""", timeout.Token);
            await WriteAsync(socket, """{"type":"response.event","delegation_id":"item_d","event":{"type":"response.created","response":{"id":"resp_1"}}}""", timeout.Token);
            await WriteAsync(socket, """{"type":"response.event","delegation_id":"item_d","event":{"type":"response.output_item.done","item":{"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{}"}}}""", timeout.Token);
            // A repeated item must not repeat an operation. All distinct results precede continuation.
            await WriteAsync(socket, """{"type":"response.event","delegation_id":"item_d","event":{"type":"response.output_item.done","item":{"type":"function_call","call_id":"call_1","name":"lookup","arguments":"{}"}}}""", timeout.Token);
            await WriteAsync(socket, """{"type":"response.event","delegation_id":"item_d","event":{"type":"response.output_item.done","item":{"type":"function_call","call_id":"call_2","name":"lookup","arguments":"{}"}}}""", timeout.Token);
            await WriteAsync(socket, """{"type":"response.event","delegation_id":"item_d","event":{"type":"response.completed","response":{"id":"resp_1","output":[],"model":"backend","usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}}}""", timeout.Token);
            while (true)
            {
                var command = await ReadAsync(socket, timeout.Token);
                received.Enqueue(command);
                var type = command["type"]!.GetValue<string>();
                if (type == "response.create")
                {
                    Check(!command.ContainsKey("delegation_id") && !command.ContainsKey("response"), "bodyless Live continuation");
                    var step = received.Count(x => x["type"]?.GetValue<string>() == "response.create");
                    Check(received.Count(x => x["type"]?.GetValue<string>() == "response.item.create") == (step == 1 ? 2 : 3), "all function outputs precede continuation");
                    if (step == 1)
                    {
                        await WriteAsync(socket, """{"type":"response.event","delegation_id":"item_d","event":{"type":"response.created","response":{"id":"resp_2"}}}""", timeout.Token);
                        await WriteAsync(socket, """{"type":"response.event","delegation_id":"item_d","event":{"type":"response.output_item.done","item":{"type":"function_call","call_id":"call_3","name":"lookup","arguments":"{}"}}}""", timeout.Token);
                        await WriteAsync(socket, """{"type":"response.event","delegation_id":"item_d","event":{"type":"response.completed","response":{"id":"resp_2","output":[]}}}""", timeout.Token);
                        await WriteAsync(socket, """{"type":"response.event","delegation_id":"item_d","event":{"type":"response.completed","response":{"id":"resp_2","output":[]}}}""", timeout.Token);
                    }
                    else
                    {
                        await WriteAsync(socket, """{"type":"response.event","delegation_id":"item_d","event":{"type":"response.incomplete","response":{"id":"resp_limit","output":[],"incomplete_details":{"reason":"max_output_tokens"}}}}""", timeout.Token);
                        await WriteAsync(socket, """{"type":"response.event","delegation_id":"item_d","event":{"type":"response.failed","response":{"id":"resp_failed","output":[],"error":{"code":"server_error","message":"backend failed"}}}}""", timeout.Token);
                    }
                }
                if (type == "session.close")
                {
                    await WriteAsync(socket, """{"type":"session.closed","reason":"close_requested","usage":{"seconds":3},"session":{"id":"sess_test"}}""", timeout.Token);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "finalized", timeout.Token);
                    return;
                }
                if (type == "session.thinking.append")
                {
                    Check(command.ContainsKey("delegation_id") && command["delegation_id"] == null, "explicit null delegation ID");
                    await WriteAsync(socket, new JsonObject { ["type"] = "session.thinking.appended", ["client_event_id"] = command["event_id"]!.GetValue<string>() }.ToJsonString(), timeout.Token);
                }
                if (type == "session.input_audio.mute")
                    await WriteAsync(socket, new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["client_event_id"] = command["event_id"]!.GetValue<string>(), ["message"] = "test rejection" } }.ToJsonString(), timeout.Token);
            }
        });
        var tool = new LookupTool();
        _ = server.ContinueWith(t => Console.Error.WriteLine(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        var settings = new OpenAiLiveSettings { Connection = new($"ws://localhost:{port}/"),
            Responses = new JsonObject { ["model"] = "backend" }, Tools = [tool] };
        await using var provider = new OpenAiLiveProvider("test-live-key");
        provider.SetStartupHistory([ChatMessage.CreateUserMessage("previous question"), ChatMessage.CreateAssistantMessage("previous answer")]);
        var fragments = new ConcurrentQueue<OpenAiLiveTranscriptDelta>();
        var usage = new ConcurrentQueue<UsageReport>();
        var audio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedTranscripts = 0;
        var errors = new ConcurrentQueue<string>();
        provider.OnError = errors.Enqueue;
        provider.OnTranscriptDelta = fragments.Enqueue;
        provider.OnUsageReceived = usage.Enqueue;
        provider.OnAudioReceived = _ => audio.TrySetResult();
        provider.OnTranscriptionCompleted = _ => completedTranscripts++;
        provider.OnMessageReceived = _ => continuation.TrySetResult();
        await provider.ConnectAsync(settings);
        Check(provider.IsConnected, "ready only after acknowledgment");
        await audio.Task.WaitAsync(timeout.Token);
        await provider.ProcessAudioAsync("AAA=");
        await provider.AppendThinkingAsync("verified context");
        try { await provider.SetInputMutedAsync(true); throw new Exception("Expected rejection"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("test rejection")) { }
        await continuation.Task.WaitAsync(timeout.Token);
        while (!errors.Any(x => x.Contains("resp_failed"))) await Task.Delay(10, timeout.Token);
        await provider.DisconnectAsync();
        await server;
        Check(provider.FinalUsageConfirmed && !provider.IsConnected, "graceful finalization");
        Check(fragments.Count == 2 && fragments.First().Delta == " hi " && fragments.Last().StartMs == 50, "exact overlapping fragments");
        Check(completedTranscripts == 0, "no invented end of turn");
        Check(usage.Count(x => x.OperationType == UsageOperationType.VoiceSession) == 2, "cumulative snapshots preserved");
        Check(usage.Last().IsFinal && usage.Last().SessionDuration == TimeSpan.FromSeconds(3), "final duration");
        Check(usage.Single(x => x.OperationType == UsageOperationType.DelegatedResponse).TotalTokens == 15, "backend usage separated");
        var meter = new Ai.Tlbx.VoiceAssistant.Managers.UsageManager();
        foreach (var report in usage) meter.AddReport(report);
        meter.AddReport(usage.First()); // late nonfinal snapshot cannot overwrite final usage
        Check(meter.TotalSessionDuration == TimeSpan.FromSeconds(3) && meter.TotalTokens == 15 && meter.ReportCount == 2,
            "orchestrator metering replaces cumulative snapshots and retains final usage");
        Check(tool.Calls == 3, "each distinct tool call executes once across batches and duplicate events");
        Check(received.Count(x => x["type"]?.GetValue<string>() == "response.create") == 2, "one continuation per completed tool batch");
        Check(errors.Any(x => x.Contains("resp_limit") && x.Contains("max_output_tokens") && x.Contains("item_d")), "nested token limit is observable with response and delegation IDs");
        Check(errors.Any(x => x.Contains("resp_failed") && x.Contains("server_error")), "nested backend failure is observable");
        Check(received.Any(x => x["type"]?.GetValue<string>() == "response.item.create"), "tool output submitted");
        await VerifyFailedLifecycleAsync(false);
        await VerifyFailedLifecycleAsync(true);
        Console.WriteLine("GPT-Live protocol contracts passed.");
    }

    private static async Task VerifyFailedLifecycleAsync(bool started)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            await ReadAsync(socket, timeout.Token);
            if (started)
            {
                await WriteAsync(socket, """{"type":"session.started","session":{"id":"sess_no_final"}}""", timeout.Token);
                await ReadAsync(socket, timeout.Token); // close request
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "missing final event", timeout.Token);
            }
            else
            {
                await WriteAsync(socket, """{"type":"error","error":{"message":"invalid startup"}}""", timeout.Token);
                await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "rejected", timeout.Token);
            }
        });
        await using var provider = new OpenAiLiveProvider("test-key");
        var settings = new OpenAiLiveSettings { Connection = new($"ws://localhost:{port}/") };
        if (started)
        {
            await provider.ConnectAsync(settings);
            try { await provider.DisconnectAsync(); throw new Exception("Expected missing final usage failure"); }
            catch (IOException) { }
        }
        else
        {
            try { await provider.ConnectAsync(settings); throw new Exception("Expected startup failure"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("invalid startup")) { }
        }
        await server;
        Check(!provider.IsConnected && !provider.FinalUsageConfirmed, "failed lifecycle releases transport without inventing final usage");
        provider.SetStartupHistory([]); // transport cleanup permits reconfiguration
    }

    public static async Task RunLiveToolsAsync()
    {
        var tool = new ChainedLookupTool();
        await using var provider = new OpenAiLiveProvider();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responses = 0;
        provider.OnError = e => Console.Error.WriteLine("Live API: " + e);
        provider.OnTranscriptDelta = d => Console.Write(d.Delta);
        provider.OnDelegationCreated = d => Console.WriteLine($"Delegation target={d.Target}");
        provider.OnEventReceived = e =>
        {
            if (e["type"]?.GetValue<string>() == "response.event" && e["event"]?["type"]?.GetValue<string>() == "response.completed")
            {
                Interlocked.Increment(ref responses);
                if (tool.Verified && responses >= 3) complete.TrySetResult();
            }
        };
        provider.OnUsageReceived = u => Console.WriteLine($"Usage {u.OperationType}: seconds={u.SessionDuration?.TotalSeconds}, tokens={u.TotalTokens}, final={u.IsFinal}");
        await provider.ConnectAsync(new OpenAiLiveSettings {
            Instructions = "Be concise. Backend tools: lookup_chain obtains and verifies a test token. Delegate token verification to the backend. Wait for its confirmed result; do not invent tokens or repeat completed work.", Tools = [tool],
            Responses = new JsonObject { ["model"] = "gpt-5.6-luna", ["instructions"] = "Verify the test token: first call lookup_chain with step=get and seed=null. Then call lookup_chain with step=verify and the exact seed returned by the first call. After verified=true, return a concise success result without further tools.", ["parallel_tool_calls"] = false, ["max_output_tokens"] = 1024 }
        });
        using var stop = new CancellationTokenSource();
        // Optional real spoken request: raw mono PCM16, 24 kHz.
        var inputPath = Environment.GetEnvironmentVariable("GPT_LIVE_TOOL_SMOKE_INPUT_PCM");
        var pcm = string.IsNullOrWhiteSpace(inputPath) ? Array.Empty<byte>() : File.ReadAllBytes(inputPath);
        var pump = Task.Run(async () =>
        {
            var silence = Convert.ToBase64String(new byte[960]);
            var offset = -24000; // half a second of silence before the spoken request
            while (!stop.IsCancellationRequested)
            {
                await provider.ProcessAudioAsync(offset >= 0 && offset < pcm.Length
                    ? Convert.ToBase64String(pcm, offset, Math.Min(960, pcm.Length - offset)) : silence);
                offset += 960;
                await Task.Delay(20, stop.Token);
            }
        });
        try
        {
            // Start backend work explicitly: this test verifies the tool protocol, independently
            // of the voice model's probabilistic decision to delegate a spoken request.
            if (pcm.Length == 0) await provider.SendEventAsync(new JsonObject { ["type"] = "response.create" });
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            stop.Cancel();
            try { await pump; } catch (OperationCanceledException) { }
            await provider.DisconnectAsync();
        }
        Check(tool.Calls == 2 && tool.Verified && provider.FinalUsageConfirmed, "real delegation and dependent tool chain without repeats");
        Console.WriteLine($"GPT-Live Responses smoke passed: {tool.Calls} dependent tool calls, {responses} completed responses, final voice usage confirmed.");
    }

    public static async Task RunLiveAsync()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))) throw new InvalidOperationException("Missing OPENAI_API_KEY");
        await using var provider = new OpenAiLiveProvider();
        var audioChunks = 0;
        var inputWav = Environment.GetEnvironmentVariable("GPT_LIVE_SMOKE_INPUT_WAV");
        var inputChunks = new ConcurrentQueue<string>();
        var delegation = new TaskCompletionSource<OpenAiLiveDelegation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transcript = new StringBuilder();
        provider.OnDelegationCreated = d => { if (d.Target == "client") delegation.TrySetResult(d); };
        provider.OnAudioReceived = _ => Interlocked.Increment(ref audioChunks);
        provider.OnTranscriptDelta = d => { if (d.Role == "assistant") lock (transcript) transcript.Append(d.Delta); };
        provider.OnError = e => Console.Error.WriteLine("Live API: " + e);
        provider.OnUsageReceived = u => Console.WriteLine($"Usage {u.OperationType}: seconds={u.SessionDuration?.TotalSeconds}, tokens={u.TotalTokens}, final={u.IsFinal}");
        await provider.ConnectAsync(new OpenAiLiveSettings { Instructions = "Speak English. Be concise. You have a backend that can look up orders. Delegate any order status request to the backend immediately; do not guess the status. When instructed, greet the user briefly." });
        using var stopAudio = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            var silence = Convert.ToBase64String(new byte[960]);
            while (!stopAudio.IsCancellationRequested)
            {
                await provider.ProcessAudioAsync(inputChunks.TryDequeue(out var chunk) ? chunk : silence);
                await Task.Delay(20, stopAudio.Token);
            }
        });
        try
        {
            await provider.AppendInstructionsAsync("Immediately say hello and identify yourself as an AI assistant, then pause and listen.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (Volatile.Read(ref audioChunks) == 0) await Task.Delay(100, timeout.Token);
            await Task.Delay(1000);
            if (!string.IsNullOrWhiteSpace(inputWav))
            {
                using var reader = new BinaryReader(File.OpenRead(inputWav));
                Check(new string(reader.ReadChars(4)) == "RIFF", "input WAV RIFF header");
                reader.ReadInt32();
                Check(new string(reader.ReadChars(4)) == "WAVE", "input WAV format");
                byte[]? pcm = null;
                var validFormat = false;
                while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
                {
                    var kind = new string(reader.ReadChars(4));
                    var length = reader.ReadInt32();
                    var payload = reader.ReadBytes(length);
                    if (kind == "fmt ") validFormat = BitConverter.ToInt16(payload, 0) == 1 && BitConverter.ToInt16(payload, 2) == 1 && BitConverter.ToInt32(payload, 4) == 24000 && BitConverter.ToInt16(payload, 14) == 16;
                    if (kind == "data") pcm = payload;
                    if (length % 2 != 0) reader.ReadByte();
                }
                Check(validFormat && pcm != null, "smoke WAV must be mono PCM16 at 24 kHz");
                foreach (var bytes in pcm!.Chunk(960)) inputChunks.Enqueue(Convert.ToBase64String(bytes));
                var request = await delegation.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await provider.AppendCommentaryAsync("The test order has shipped. No action was taken.", request.Id);
                using var spokenResultTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (true)
                {
                    lock (transcript)
                        if (transcript.ToString().Contains("shipped", StringComparison.OrdinalIgnoreCase)) break;
                    await Task.Delay(100, spokenResultTimeout.Token);
                }
            }
        }
        finally
        {
            stopAudio.Cancel();
            try { await pump; } catch (OperationCanceledException) { }
            await provider.DisconnectAsync();
        }
        Check(provider.FinalUsageConfirmed && audioChunks > 0, "live audio and final usage");
        if (!string.IsNullOrWhiteSpace(inputWav)) Check(delegation.Task.IsCompletedSuccessfully, "live client delegation and result acknowledgment");
        Console.WriteLine($"GPT-Live live smoke passed: client delegation={delegation.Task.IsCompletedSuccessfully}, {audioChunks} audio chunks; transcript: {transcript}");
    }

    private static async Task<JsonObject> ReadAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[65536];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Unexpected close");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonNode.Parse(stream.ToArray())!.AsObject();
    }
    private static Task WriteAsync(WebSocket socket, string json, CancellationToken token) => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, token);
    private sealed class LookupTool : IVoiceTool
    {
        public int Calls;
        public string Name => "lookup";
        public string Description => "Look up a value";
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
        public Type ArgsType => typeof(LookupArgs);
        public async Task<string> ExecuteAsync(string argumentsJson) { Interlocked.Increment(ref Calls); await Task.Delay(50); return "{\"value\":42}"; }
    }
    public sealed record LookupArgs();
    private sealed class ChainedLookupTool : IVoiceTool
    {
        private readonly string _seed = Guid.NewGuid().ToString("N");
        public int Calls;
        public bool Verified;
        public string Name => "lookup_chain";
        public string Description => "Get a test token, then verify that exact token in a second call.";
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]
        public Type ArgsType => typeof(ChainedLookupArgs);
        public Task<string> ExecuteAsync(string argumentsJson)
        {
            var args = JsonNode.Parse(argumentsJson)!;
            var count = Interlocked.Increment(ref Calls);
            if (count == 1 && args["step"]?.GetValue<string>() == "get" && args["seed"] == null)
                return Task.FromResult(new JsonObject { ["seed"] = _seed }.ToJsonString());
            Verified = count == 2 && args["step"]?.GetValue<string>() == "verify" && args["seed"]?.GetValue<string>() == _seed;
            return Task.FromResult(new JsonObject { ["verified"] = Verified }.ToJsonString());
        }
    }
    public sealed record ChainedLookupArgs(
        [Ai.Tlbx.VoiceAssistant.Attributes.ToolEnumValues("get", "verify")] string Step,
        string? Seed = null);
}

