using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

internal static partial class OpenAiLiveTests
{
    public static async Task RunClientBackendAsync()
    {
        await VerifyLargeClientBackendAsync();
        await VerifyClientSupersessionAsync();
        await VerifyClientFailureBoundariesAsync();
        Console.WriteLine("GPT-Live client backend contracts passed: complete large Unicode results, followups, duplicate actions/delegations, supersession, endpoint isolation and failures.");
    }

    private static async Task VerifyLargeClientBackendAsync()
    {
        var large = new JsonObject { ["records"] = string.Concat(Enumerable.Repeat("ä😀\n\"detail\";", 5500)),
            ["tail"] = new JsonObject { ["code"] = "KUPFER-7319", ["count"] = 417 } }.ToJsonString();
        Check(large.Length > 40000, "large complex fixture");
        var tool = new ClientLargeTool(large);
        var requests = new List<JsonObject>();
        using var http = new HttpClient(new ClientHandler(async request =>
        {
            Check(request.RequestUri!.AbsolutePath == "/v1/responses", "ordinary Responses endpoint");
            Check(request.Headers.Authorization?.Parameter == "backend-key", "explicit backend auth");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            requests.Add(body);
            Check(body["store"]!.GetValue<bool>() == false && !body["stream"]!.GetValue<bool>(), "stateless nonstreaming backend");
            if (requests.Count == 1) return ClientResponse(ClientCall("call-large"));
            Check(body["input"]!.AsArray().OfType<JsonObject>().Any(i => i["type"]?.GetValue<string>() == "function_call_output" && i["output"]?.GetValue<string>() == large), "complete tail and Unicode retained in backend");
            if (requests.Count == 2) return ClientResponse(ClientCall("call-large")); // repeated call must not repeat action
            return ClientResponse(ClientAnswer("KUPFER-7319: 417."));
        }));
        var results = new List<OpenAiLiveToolResult>();
        var messages = new List<ChatMessage>();
        var usage = new List<UsageReport>();
        var settings = new OpenAiLiveSettings { Tools = [tool], ClientBackend = new() {
            HttpClient = http, Connection = new("https://api.openai.com/v1/responses") { ApiKey = "backend-key" } } };
        using var backend = new ClientBackendProbe(settings, "live-key", [], messages.Add, results.Add, usage.Add);
        var transcript = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "Read the tail." });
        Check(await backend.RunAsync(transcript, default) == "KUPFER-7319: 417.", "tail answer");
        transcript.Add(new JsonObject { ["role"] = "user", ["text"] = "Correction: repeat only that count." });
        Check(await backend.RunAsync(transcript, default) == "KUPFER-7319: 417.", "followup preserves prior tool state");
        var finalInput = requests.Last()["input"]!.AsArray().OfType<JsonObject>()
            .Where(i => i["role"]?.GetValue<string>() == "user").Select(i => i["content"]!.GetValue<string>()).ToArray();
        Check(finalInput.Count(t => t.Contains("Read the tail.")) == 1 && finalInput.Count(t => t.Contains("Correction: repeat only that count.")) == 1,
            "prior raw transcript appears exactly once and appended correction is preserved");
        Check(tool.Calls == 1 && requests.Count == 4, "duplicate call and followup do not replay action");
        Check(messages.Count(m => m.Role == "tool") == 1 && messages.Single(m => m.Role == "tool").Content == large, "full observable tool result");
        Check(messages.Single(m => m.Role == "assistant").ToolCallId == "call-large", "request callback correlates tool call");
        Check(results.Last().SubmissionState == OpenAiLiveToolSubmissionState.AcceptedByClientBackend && usage.Count == 4, "HTTP acceptance and usage exposed");
    }

    private static async Task VerifyClientSupersessionAsync()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new ClientLargeTool(new string('ä', 41000) + "TAIL", entered, release);
        var requests = 0;
        using var http = new HttpClient(new ClientHandler(async request =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!.AsObject();
            if (Interlocked.Increment(ref requests) == 1) return ClientResponse(ClientCall("slow-call"));
            Check(body["input"]!.ToJsonString().Contains("Thursday"), "correction present in replacement delegation");
            Check(body["input"]!.AsArray().OfType<JsonObject>().Any(i => i["output"]?.GetValue<string>() == tool.Output), "completed superseded action retained for next backend");
            return ClientResponse(ClientAnswer("Thursday is confirmed."));
        }));
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var start = await ReadAsync(socket, timeout.Token);
            Check(start["session"]!["delegation"]!["type"]!.GetValue<string>() == "client", "client mode selected at startup");
            await WriteAsync(socket, """{"type":"session.started","session":{"id":"client-test"}}""", timeout.Token);
            await WriteAsync(socket, """{"type":"session.input_transcript.delta","delta":"Check Friday","start_ms":0,"end_ms":100}""", timeout.Token);
            const string first = """{"type":"session.delegation.created","offset_ms":100,"delegation":{"id":"first","target":"client"}}""";
            await WriteAsync(socket, first, timeout.Token);
            await WriteAsync(socket, first, timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            await WriteAsync(socket, """{"type":"session.input_transcript.delta","delta":"Thursday, not Friday","start_ms":150,"end_ms":250}""", timeout.Token);
            await WriteAsync(socket, """{"type":"session.delegation.created","offset_ms":250,"delegation":{"id":"second","target":"client"}}""", timeout.Token);
            await heard.Task.WaitAsync(timeout.Token); // receipt continues while tool blocks
            release.TrySetResult();
            var commentary = await ReadAsync(socket, timeout.Token);
            Check(commentary["type"]!.GetValue<string>() == "session.commentary.append" && commentary["delegation_id"]!.GetValue<string>() == "second", "only replacement delegation speaks; no Live raw tool result");
            Check(Encoding.UTF8.GetByteCount(commentary["content"]!.GetValue<string>()) <= 500, "short commentary");
            await WriteAsync(socket, new JsonObject { ["type"] = "session.commentary.appended", ["client_event_id"] = commentary["event_id"]!.DeepClone() }.ToJsonString(), timeout.Token);
            var close = await ReadAsync(socket, timeout.Token);
            Check(close["type"]!.GetValue<string>() == "session.close", "explicit close");
            await WriteAsync(socket, """{"type":"session.closed","reason":"client_requested","usage":{"seconds":1}}""", timeout.Token);
        });
        await using var provider = new OpenAiLiveProvider("test-key");
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<string>();
        provider.OnError = errors.Add;
        provider.OnDelegationCreated = d => { if (d.Id == "second") heard.TrySetResult(); };
        provider.OnEventReceived = e => { if (e["type"]?.GetValue<string>() == "session.commentary.appended") complete.TrySetResult(); };
        await provider.ConnectAsync(new OpenAiLiveSettings { Connection = new($"ws://localhost:{port}/live"), Tools = [tool],
            ClientBackend = new() { Connection = new($"http://localhost:{port}/responses"), HttpClient = http } });
        await complete.Task.WaitAsync(timeout.Token);
        await provider.DisconnectAsync();
        await server;
        Check(tool.Calls == 1 && requests == 2 && errors.Count == 0, "superseded action once, receiver unblocked and no failures");
        Check(provider.ToolResults.Single().Output == tool.Output && provider.FinalUsageConfirmed, "retained result after finalization");
    }

    private static async Task VerifyClientFailureBoundariesAsync()
    {
        var unsafeSettings = new OpenAiLiveSettings { Connection = new("wss://gateway.example/live"), ClientBackend = new() };
        try { BuildClientSession(unsafeSettings, []); throw new Exception("cross-authority credentials unexpectedly accepted"); }
        catch (ArgumentException) { }
        var downgrade = new OpenAiLiveSettings { Connection = new("wss://api.openai.com:443/live"),
            ClientBackend = new() { Connection = new("http://api.openai.com:443/responses") } };
        try { BuildClientSession(downgrade, []); throw new Exception("cross-scheme credentials unexpectedly accepted"); }
        catch (ArgumentException) { }
        unsafeSettings.ClientBackend!.Connection.ApiKey = "separate-key";
        Check(BuildClientSession(unsafeSettings, [])["delegation"]!["type"]!.GetValue<string>() == "client", "explicit separate auth accepted");
        foreach (var scenario in new[] { "oversized-answer", "incomplete", "http-error", "http-after-tool", "round-limit", "timeout" })
        {
            var calls = 0;
            var tool = new ClientLargeTool("retained");
            using var http = new HttpClient(new ClientHandler(async _ =>
            {
                calls++;
                if (scenario == "http-after-tool" && calls == 1) return ClientResponse(ClientCall("executed-before-http-failure"));
                if (scenario == "timeout") await Task.Delay(200);
                if (scenario is "http-error" or "http-after-tool") return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("rejected") };
                if (scenario == "incomplete") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[]}""") };
                return ClientResponse(scenario == "round-limit" ? ClientCall("round") : ClientAnswer(scenario == "oversized-answer" ? new string('ä', 251) : "late"));
            }));
            var settings = new OpenAiLiveSettings { Tools = [tool], ClientBackend = new() { HttpClient = http, MaxRounds = scenario == "http-after-tool" ? 2 : 1,
                Timeout = scenario == "timeout" ? TimeSpan.FromMilliseconds(30) : TimeSpan.FromSeconds(5) } };
            var retained = new List<OpenAiLiveToolResult>();
            using var backend = new ClientBackendProbe(settings, "key", [], _ => { }, retained.Add, _ => { });
            var failed = false;
            try { await backend.RunAsync(new JsonArray(new JsonObject { ["text"] = "run" }), default); }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException) { failed = true; }
            Check(failed && calls == (scenario == "http-after-tool" ? 2 : 1), $"{scenario}: surfaced without retry or truncation");
            if (scenario == "http-after-tool") Check(tool.Calls == 1 && retained.Last().Output == "retained"
                && retained.Last().SubmissionState == OpenAiLiveToolSubmissionState.Rejected, "HTTP rejection retains executed action without replay");
        }
    }

    private static JsonObject ClientCall(string id) => new() { ["type"] = "function_call", ["call_id"] = id, ["name"] = "large", ["arguments"] = "{}" };
    private static JsonObject ClientAnswer(string text) => new() { ["type"] = "message", ["role"] = "assistant",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text }) };
    private static HttpResponseMessage ClientResponse(JsonObject output) => new(HttpStatusCode.OK) { Content = new StringContent(new JsonObject {
        ["id"] = Guid.NewGuid().ToString(), ["model"] = "fake", ["status"] = "completed", ["output"] = new JsonArray(output),
        ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 20, ["total_tokens"] = 120 } }.ToJsonString()) };
    private sealed class ClientHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request).WaitAsync(cancellationToken);
    }
    private sealed class ClientLargeTool(string output, TaskCompletionSource? entered = null, TaskCompletionSource? release = null) : IVoiceTool
    {
        public string Name => "large";
        public string Description => "Fetch a complete large structured report.";
        public Type ArgsType => typeof(ClientLargeArgs);
        public int Calls;
        public string Output => output;
        public async Task<string> ExecuteAsync(string arguments) { Interlocked.Increment(ref Calls); entered?.TrySetResult(); if (release != null) await release.Task; return output; }
    }
    private sealed class ClientLargeArgs { }
    private static JsonObject BuildClientSession(OpenAiLiveSettings settings, IEnumerable<ChatMessage> history)
    {
        try { return (JsonObject)typeof(OpenAiLiveProvider).GetMethod("BuildSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [settings, history])!; }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private sealed class ClientBackendProbe : IDisposable
    {
        private readonly object _instance;
        private readonly Type _type = typeof(OpenAiLiveProvider).Assembly.GetType("Ai.Tlbx.VoiceAssistant.Provider.OpenAi.OpenAiLiveClientBackend")!;
        public ClientBackendProbe(OpenAiLiveSettings settings, string apiKey, IEnumerable<ChatMessage> history,
            Action<ChatMessage> message, Action<OpenAiLiveToolResult> result, Action<UsageReport> usage)
        {
            _instance = Activator.CreateInstance(_type, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, [settings, apiKey, history, message, result, usage], null)!;
        }
        public Task<string> RunAsync(JsonArray transcript, CancellationToken token) => (Task<string>)_type.GetMethod("RunAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(_instance, [transcript, token])!;
        public void Dispose() => ((IDisposable)_instance).Dispose();
    }
}
