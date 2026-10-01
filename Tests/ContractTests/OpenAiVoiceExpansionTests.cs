using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

internal static class OpenAiVoiceExpansionTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception("Voice expansion: " + message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpListener Listener(out int port)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var listener = new HttpListener(); listener.Prefixes.Add($"http://localhost:{port}/"); listener.Start(); return listener;
    }
    private static Task Send(WebSocket socket, string json, CancellationToken ct) => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, ct);
    private static async Task<JsonObject> Read(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192]; using var stream = new MemoryStream(); WebSocketReceiveResult r;
        do { r = await socket.ReceiveAsync(buffer, ct); if (r.MessageType == WebSocketMessageType.Close) throw new IOException("Unexpected close"); stream.Write(buffer, 0, r.Count); } while (!r.EndOfMessage);
        return JsonNode.Parse(stream.ToArray())!.AsObject();
    }
    public static async Task RunAsync()
    {
        var input = new InputAudioTranscription { Languages = ["de", "en", "DE"], Keywords = ["tlbx"], Prompt = "Domain terms" };
        var config = input.BuildConfiguration(true)!;
        Check(config.Model == "gpt-live-transcribe" && config.Languages!.Count == 2 && config.Keywords![0] == "tlbx" && config.Delay == "low" && config.Language == null, "modern voice input settings");
        input.Model = OpenAiTranscriptionModel.GptTranscribe;
        Check(input.BuildConfiguration(false)!.Delay == null, "committed-turn transcription has no live delay");
        try { input.BuildConfiguration(true); throw new Exception("Expected transport rejection"); } catch (ArgumentException) { }
        input.Model = OpenAiTranscriptionModel.Gpt4oTranscribe;
        Check(input.BuildConfiguration(true, fallbackLanguage: "de")!.Language == "de", "explicit legacy models remain supported");
        input.Enabled = false; Check(input.BuildConfiguration(true) == null, "disabled transcription");
        input.Enabled = true; input.Keywords = ["bad\nterm"];
        try { input.BuildConfiguration(false); throw new Exception("Expected invalid keyword"); } catch (ArgumentException) { }
        foreach (var invalid in new[] { "http://example.com/a.png", "https://user:password@example.com/a.png", "data:image/svg+xml;base64,AAA=", "data:image/png;base64," })
        { try { OpenAiVisualInput.Validate(invalid); throw new Exception("Expected invalid image"); } catch (ArgumentException) { } }
        await TranslationAsync(false); await TranslationAsync(true);
        await TranslationHandshakeAsync(); await TranslationEndpointAsync();
        await ForkAsync(false); await ForkAsync(true);
        await ManagedImageAsync(); await RealtimeImageAsync();
        Console.WriteLine("Voice expansion contracts passed: modern input, image/tool ordering, both fork transports, streaming recording, translation/drain/failure and secured WebRTC handshake.");
    }
    private static async Task TranslationAsync(bool missingFinal)
    {
        using var listener = Listener(out var port); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var audio = new List<string>(); var fragments = new List<OpenAiTranslationTranscriptDelta>(); var usage = new List<UsageReport>();
        var server = Task.Run(async () => {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            Check(context.Request.Url!.Query.Contains("model=gpt-realtime-translate") && context.Request.Url.Query.Contains("key=translate-key"), "model and gateway query auth");
            Check(context.Request.Headers["OpenAI-Safety-Identifier"] == "user-test", "translation safety identifier");
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            await Send(socket, """{"type":"session.created","session":{"id":"translation_test"}}""", timeout.Token);
            var update = await Read(socket, timeout.Token);
            Check(update["type"]!.GetValue<string>() == "session.update" && update["session"]!["model"] == null && update["session"]!["audio"]!["output"]!["language"]!.GetValue<string>() == "en", "translation language config, no assistant controls");
            await Send(socket, """{"type":"session.updated"}""", timeout.Token);
            Check((await Read(socket, timeout.Token))["type"]!.GetValue<string>() == "session.input_audio_buffer.append", "continuous PCM event");
            Check((await Read(socket, timeout.Token))["type"]!.GetValue<string>() == "session.close", "translation graceful close");
            if (!missingFinal) {
                await Send(socket, """{"type":"session.input_transcript.delta","delta":" Guten Morgen "}""", timeout.Token);
                await Send(socket, """{"type":"session.output_transcript.delta","delta":" Good morning "}""", timeout.Token);
                await Send(socket, """{"type":"session.output_audio.delta","delta":"AAA="}""", timeout.Token);
                await Send(socket, """{"type":"session.closed","usage":{"input_tokens":3,"output_tokens":5,"total_tokens":8}}""", timeout.Token);
            }
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        });
        await using var provider = new OpenAiTranslationProvider("translate-key");
        provider.OnAudioReceived = audio.Add; provider.OnTranscriptDelta = fragments.Add; provider.OnUsageReceived = usage.Add;
        provider.WaitForPlaybackDrainAsync = _ => { Check(audio.Count == 1 && fragments.Count == 2 && provider.FinalOutputConfirmed, "final deltas delivered before playback drain"); return Task.FromResult(true); };
        await provider.ConnectAsync(new OpenAiTranslationSettings { TargetLanguage = "en", SafetyIdentifier = "user-test", Connection = new($"ws://localhost:{port}/translations") { ApiKeyQueryParameter = "key" } });
        await provider.ProcessAudioAsync("AAA=");
        try { await provider.DisconnectAsync(); Check(!missingFinal, "missing final cannot succeed"); }
        catch (IOException) when (missingFinal) { Check(!provider.FinalOutputConfirmed && !provider.IsConnected, "missing final is observable and cleans up"); }
        await server;
        if (!missingFinal) Check(fragments[0].Delta == " Guten Morgen " && fragments[1].Language == "en" && usage.Single().IsFinal && usage[0].IsCumulative && usage[0].TotalTokens == 8, "exact subtitles and cumulative final metering");
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request); }
    private static async Task TranslationHandshakeAsync()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async request => {
            calls++;
            if (calls == 1) {
                Check(request.RequestUri!.AbsolutePath == "/gateway/translations/client_secrets" && request.Headers.Authorization!.Parameter == "server-key", "trusted translation secret request");
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                Check(body["session"]!["audio"]!["input"]!["transcription"]!.AsObject().Count == 1, "translation transcription has model-only schema");
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"value":"ephemeral-key","session":{"id":"translation_browser"}}""") };
            }
            Check(request.RequestUri!.AbsolutePath == "/gateway/translations/calls" && request.Headers.Authorization!.Parameter == "ephemeral-key" && request.Content!.Headers.ContentType!.MediaType == "application/sdp", "browser exchange uses ephemeral auth and raw SDP");
            return new(HttpStatusCode.Created) { Content = new StringContent("answer-sdp") };
        }));
        await using var provider = new OpenAiTranslationProvider("server-key");
        var session = await provider.CreateWebRtcSessionAsync(new() { Connection = new("https://gateway.example/gateway/translations") }, "offer-sdp", http);
        Check(calls == 2 && session.SessionId == "translation_browser" && session.Sdp == "answer-sdp", "no credential returned to browser");
    }
    private static async Task TranslationEndpointAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Services.AddOpenAiDirectTranslation(o => o.AuthorizeRequest = c => c.Request.Headers["X-User"] == "owner");
        await using var app = builder.Build(); app.Urls.Add("http://127.0.0.1:0"); app.MapOpenAiDirectTranslation(); await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new(app.Urls.Single()) }; var calls = 0;
        var token = app.Services.GetRequiredService<OpenAiDirectTranslationPreparedSessionStore>().Prepare((_, _) => { calls++; return Task.FromResult(new OpenAiTranslationWebRtcSession("id", "answer")); });
        async Task<HttpResponseMessage> Request(string body, bool owner, string? origin = null) {
            using var r = new HttpRequestMessage(HttpMethod.Post, "/api/voice/translation/session") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (owner) r.Headers.Add("X-User", "owner"); if (origin != null) r.Headers.Add("Origin", origin); return await http.SendAsync(r);
        }
        var payload = new JsonObject { ["preparedSessionId"] = token, ["sdp"] = "offer" }.ToJsonString();
        using var denied = await Request(payload, false); Check(denied.StatusCode == HttpStatusCode.Forbidden, "authorization before consume");
        using var origin = await Request(payload, true, "https://evil.example"); Check(origin.StatusCode == HttpStatusCode.Forbidden, "same origin");
        using var malformed = await Request("[]", true); Check(malformed.StatusCode == HttpStatusCode.BadRequest, "malformed shape");
        using var oversized = await Request(new string('x', 129 * 1024), true); Check((int)oversized.StatusCode == 413, "bounded body");
        using var good = await Request(payload, true); Check(good.StatusCode == HttpStatusCode.Created && calls == 1, "one authorized exchange");
        using var replay = await Request(payload, true); Check(replay.StatusCode == HttpStatusCode.Gone && calls == 1, "single use"); await app.StopAsync();
    }
    private static async Task ForkAsync(bool webRtc)
    {
        using var listener = Listener(out var port); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () => {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            Check(context.Request.Url!.AbsolutePath == "/v1/live/sessions/source_id/fork", "fork endpoint");
            WebSocket socket;
            if (webRtc) {
                using var reader = new StreamReader(context.Request.InputStream); var body = JsonNode.Parse(await reader.ReadToEndAsync())!;
                Check(body["session"]!.AsObject().Count == 1 && body["transport"]!["sdp"]!.GetValue<string>() == "offer", "sparse WebRTC fork, inherited model/prompt/history/audio");
                var bytes = Encoding.UTF8.GetBytes("""{"session":{"id":"child_id"},"transport":{"sdp":"answer"}}""");
                await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
                var attach = await listener.GetContextAsync().WaitAsync(timeout.Token);
                Check(attach.Request.Url!.AbsolutePath.EndsWith("/child_id/attach"), "attach new child id"); socket = (await attach.AcceptWebSocketAsync(null)).WebSocket;
            } else {
                socket = (await context.AcceptWebSocketAsync(null)).WebSocket; var start = await Read(socket, timeout.Token);
                Check(start["session"]!.AsObject().Count == 2 && start["session"]!["model"] == null && start["session"]!["instructions"] == null, "sparse WS fork");
                await Send(socket, """{"type":"session.started","session":{"id":"child_id"}}""", timeout.Token);
            }
            using (socket) {
                Check((await Read(socket, timeout.Token))["type"]!.GetValue<string>() == "session.close", "no duplicate WebRTC startup");
                await Send(socket, """{"type":"session.closed","usage":{"seconds":1}}""", timeout.Token);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }
        });
        await using var provider = new OpenAiLiveProvider("key"); var settings = new OpenAiLiveSettings { ForkFromSessionId = "source_id", Connection = new($"ws://localhost:{port}/v1/live/sessions") };
        if (webRtc) Check((await provider.ConnectWebRtcAsync(settings, "offer")).Sdp == "answer", "fork SDP"); else await provider.ConnectAsync(settings);
        Check(provider.SessionId == "child_id", "child identity"); await provider.DisconnectAsync(); await server;
        using var destination = new MemoryStream();
        using var http = new HttpClient(new Handler(request => {
            Check(request.RequestUri!.AbsolutePath.EndsWith("/child_id/content") && request.Method == HttpMethod.Get, "recording route");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([82, 73, 70, 70, 0, 1]) });
        }));
        await provider.DownloadRecordingAsync("child_id", destination, settings, http);
        Check(destination.CanWrite && destination.ToArray().SequenceEqual(new byte[] { 82, 73, 70, 70, 0, 1 }), "binary recording streamed and destination remains open");
    }
    private sealed class ImageTool(TaskCompletionSource started, TaskCompletionSource release) : IVoiceTool
    { public string Name => "probe"; public string Description => "Probe"; public Type ArgsType => typeof(ImageArgs); public async Task<string> ExecuteAsync(string args) { started.TrySetResult(); await release.Task; return "result"; } }
    public sealed record ImageArgs();
    private static async Task ManagedImageAsync()
    {
        using var listener = Listener(out var port); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var started = Signal(); var release = Signal(); var continued = Signal(); var finished = Signal();
        var server = Task.Run(async () => {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token); using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket; await Read(socket, timeout.Token);
            await Send(socket, """{"type":"session.started","session":{"id":"images"}}""", timeout.Token);
            await Send(socket, """{"type":"response.event","delegation_id":"d","event":{"type":"response.created","response":{"id":"r1"}}}""", timeout.Token);
            await Send(socket, """{"type":"response.event","delegation_id":"d","event":{"type":"response.output_item.done","item":{"type":"function_call","call_id":"c","name":"probe","arguments":"{}"}}}""", timeout.Token);
            const string terminal = """{"type":"response.event","delegation_id":"d","event":{"type":"response.completed","response":{"id":"r1"}}}""";
            await Send(socket, terminal, timeout.Token); await started.Task.WaitAsync(timeout.Token); await Send(socket, terminal, timeout.Token);
            var output = await Read(socket, timeout.Token); Check(output["item"]!["type"]!.GetValue<string>() == "function_call_output", "image does not overtake active tool or duplicate terminal");
            Check((await Read(socket, timeout.Token))["type"]!.GetValue<string>() == "response.create", "tool continuation first"); continued.TrySetResult();
            await Send(socket, """{"type":"response.event","delegation_id":"d","event":{"type":"response.created","response":{"id":"r2"}}}""", timeout.Token);
            await Send(socket, """{"type":"response.event","delegation_id":"d","event":{"type":"response.completed","response":{"id":"r2"}}}""", timeout.Token);
            var image = await Read(socket, timeout.Token); Check(image["item"]!["content"]![0]!["image_url"]!.GetValue<string>() == "https://example.com/test.png", "original image routed to managed Responses");
            Check((await Read(socket, timeout.Token))["type"]!.GetValue<string>() == "response.create", "image requests one response"); finished.TrySetResult();
            Check((await Read(socket, timeout.Token))["type"]!.GetValue<string>() == "session.close", "close"); await Send(socket, """{"type":"session.closed","usage":{"seconds":1}}""", timeout.Token);
        });
        await using var provider = new OpenAiLiveProvider("key"); await provider.ConnectAsync(new OpenAiLiveSettings { Connection = new($"ws://localhost:{port}/"), Responses = new JsonObject { ["model"] = "backend" }, Tools = [new ImageTool(started, release)] });
        await started.Task.WaitAsync(timeout.Token); var imageTask = provider.SendImageAsync("https://example.com/test.png", "Describe this"); release.TrySetResult();
        await imageTask; await finished.Task.WaitAsync(timeout.Token); await provider.DisconnectAsync(); await server;
    }
    private static async Task RealtimeImageAsync()
    {
        using var listener = Listener(out var port); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var finished = Signal();
        var server = Task.Run(async () => {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token); using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var session = (await Read(socket, timeout.Token))["session"]!;
            Check(session["audio"]!["input"]!["transcription"]!["model"]!.GetValue<string>() == "gpt-live-transcribe", "new transcription reaches actual voice transport");
            await Send(socket, """{"type":"session.updated","session":{}}""", timeout.Token);
            var image = await Read(socket, timeout.Token); Check(image["item"]!["content"]![0]!["type"]!.GetValue<string>() == "input_image" && image["item"]!["content"]![1]!["text"]!.GetValue<string>() == "Describe", "native realtime image content");
            Check((await Read(socket, timeout.Token))["type"]!.GetValue<string>() == "response.create", "realtime image response"); finished.TrySetResult();
            var close = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[8192]), timeout.Token); Check(close.MessageType == WebSocketMessageType.Close, "no extra response"); await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        });
        await using var provider = new OpenAiVoiceProvider("key"); await provider.ConnectAsync(new OpenAiVoiceSettings { UseEphemeralKey = false, Connection = new($"ws://localhost:{port}/") });
        await provider.SendImageAsync("https://example.com/a.png", "Describe"); await finished.Task.WaitAsync(timeout.Token); await provider.DisconnectAsync(); await server;
    }
    public static async Task RunLiveAsync()
    {
        var path = Environment.GetEnvironmentVariable("VOICE_EXPANSION_INPUT_WAV") ?? throw new Exception("Set VOICE_EXPANSION_INPUT_WAV to German mono PCM16 24kHz WAV.");
        byte[] pcm; using (var reader = new BinaryReader(File.OpenRead(path))) {
            Check(new string(reader.ReadChars(4)) == "RIFF", "WAV"); reader.ReadInt32(); Check(new string(reader.ReadChars(4)) == "WAVE", "WAVE"); pcm = [];
            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length) { var id = new string(reader.ReadChars(4)); var length = reader.ReadInt32(); var bytes = reader.ReadBytes(length); if (id == "fmt ") Check(BitConverter.ToInt16(bytes, 0) == 1 && BitConverter.ToInt16(bytes, 2) == 1 && BitConverter.ToInt32(bytes, 4) == 24000 && BitConverter.ToInt16(bytes, 14) == 16, "PCM16 mono 24k"); if (id == "data") pcm = bytes; if (length % 2 != 0) reader.ReadByte(); }
        }
        await using var translation = new OpenAiTranslationProvider(); var source = new StringBuilder(); var target = new StringBuilder(); var audio = 0;
        translation.OnTranscriptDelta = d => { lock (target) (d.Role == "user" ? source : target).Append(d.Delta); };
        translation.OnAudioReceived = _ => Interlocked.Increment(ref audio); translation.OnError = e => Console.Error.WriteLine(e);
        translation.OnUsageReceived = u => Console.WriteLine($"translation usage: {u.RawProviderUsageJson}, final={u.IsFinal}");
        await translation.ConnectAsync(new OpenAiTranslationSettings { TargetLanguage = "en" });
        foreach (var bytes in pcm.Concat(new byte[24000 * 2 * 3]).Chunk(9600)) { await translation.ProcessAudioAsync(Convert.ToBase64String(bytes)); await Task.Delay(200); }
        await translation.DisconnectAsync();
        Check(translation.FinalOutputConfirmed && audio > 0 && target.Length > 0 && source.Length > 0, "real translation source+target+audio and final drain");
        Console.WriteLine($"Live translation passed: source={source}; target={target}; audio chunks={audio}");
    }

    public static async Task RunVisionAsync()
    {
        var path = Environment.GetEnvironmentVariable("VOICE_EXPANSION_IMAGE") ?? throw new Exception("Set VOICE_EXPANSION_IMAGE to a red PNG.");
        var image = "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(path));
        await using var realtime = new OpenAiVoiceProvider();
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        realtime.OnMessageReceived = m => { if (m.Role == "assistant") answer.TrySetResult(m.Content); };
        realtime.OnError = e => { Console.Error.WriteLine(e); answer.TrySetException(new Exception(e)); };
        realtime.OnUsageReceived = u => Console.WriteLine($"vision usage: {u.RawProviderUsageJson}");
        await realtime.ConnectAsync(new OpenAiVoiceSettings { OutputMode = OpenAiOutputMode.Text, MaxTokens = 40, Instructions = "Answer briefly in English." });
        await realtime.SendImageAsync(image, "Which color fills this image? Answer with one word.");
        var text = await answer.Task.WaitAsync(TimeSpan.FromSeconds(25)); await realtime.DisconnectAsync();
        Check(text.Contains("red", StringComparison.OrdinalIgnoreCase), "actual image understood"); Console.WriteLine("Realtime vision passed: " + text);
        await using var live = new OpenAiLiveProvider();
        var completed = Signal(); var spoken = new StringBuilder();
        live.OnError = e => { Console.Error.WriteLine(e); completed.TrySetException(new Exception(e)); };
        live.OnTranscriptDelta = d => { if (d.Role == "assistant") { spoken.Append(d.Delta); if (spoken.ToString().Contains("red", StringComparison.OrdinalIgnoreCase)) completed.TrySetResult(); } };
        live.OnEventReceived = e => {
            if (e["type"]?.GetValue<string>() == "response.event") Console.WriteLine("Vision backend: " + e.ToJsonString());
        };
        await live.ConnectAsync(new OpenAiLiveSettings { Responses = new JsonObject { ["model"] = "gpt-5.6-luna", ["max_output_tokens"] = 256 }, Instructions = "Report image findings from your backend concisely in English." });
        using var stop = new CancellationTokenSource();
        var pump = Task.Run(async () => { while (!stop.IsCancellationRequested) { await live.ProcessAudioAsync(Convert.ToBase64String(new byte[9600])); await Task.Delay(200, stop.Token); } });
        try { await live.SendImageAsync(image, "Which color fills this image? Answer with one word."); await completed.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { stop.Cancel(); try { await pump; } catch (OperationCanceledException) { } await live.DisconnectAsync(); }
        Console.WriteLine("GPT-Live managed vision passed: " + spoken);
    }
    public static async Task RunStorageAsync()
    {
        string id;
        await using (var source = new OpenAiLiveProvider()) {
            source.OnError = e => Console.Error.WriteLine(e);
            await source.ConnectAsync(new OpenAiLiveSettings { Store = true, Instructions = "Listen quietly. Do not speak until asked.", InitialHistory = [ChatMessage.CreateUserMessage("Our validation code is AZUR-7319.")] });
            id = source.SessionId!;
            for (var i = 0; i < 10; i++) { await source.ProcessAudioAsync(Convert.ToBase64String(new byte[9600])); await Task.Delay(200); }
            await source.DisconnectAsync(); Check(source.FinalUsageConfirmed, "stored source finalized");
            using var wav = new MemoryStream(); await source.DownloadRecordingAsync(id, wav);
            Check(wav.Length > 44 && Encoding.ASCII.GetString(wav.ToArray(), 0, 4) == "RIFF", "real binary recording");
            await File.WriteAllBytesAsync("artifacts/live-stored-recording.wav", wav.ToArray()); Console.WriteLine($"Stored source {id}: WAV bytes={wav.Length}");
        }
        await using var child = new OpenAiLiveProvider(); var transcript = new StringBuilder(); var done = Signal();
        child.OnError = e => { Console.Error.WriteLine(e); done.TrySetException(new Exception(e)); };
        child.OnTranscriptDelta = d => { if (d.Role == "assistant") { transcript.Append(d.Delta); if (transcript.ToString().Contains("7319")) done.TrySetResult(); } };
        await child.ConnectAsync(new OpenAiLiveSettings { ForkFromSessionId = id });
        Check(child.SessionId != id, "real child ID distinct");
        using var stop = new CancellationTokenSource(); var pump = Task.Run(async () => { while (!stop.IsCancellationRequested) { await child.ProcessAudioAsync(Convert.ToBase64String(new byte[9600])); await Task.Delay(200, stop.Token); } });
        try { await child.AppendInstructionsAsync("Say the remembered validation code, now. Use digits for the numeric part in your transcript."); await done.Task.WaitAsync(TimeSpan.FromSeconds(25)); }
        finally { stop.Cancel(); try { await pump; } catch (OperationCanceledException) { } await child.DisconnectAsync(); }
        Console.WriteLine($"Stored fork passed: {id} -> {child.SessionId}; inherited code: {transcript}");
    }
}
