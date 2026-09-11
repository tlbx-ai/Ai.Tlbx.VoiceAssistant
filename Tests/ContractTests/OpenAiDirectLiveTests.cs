using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

internal static class OpenAiDirectLiveTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Live WebRTC: " + message); }
    public static async Task RunAsync()
    {
        await VerifySidebandAsync(false);
        await VerifySidebandAsync(true);
        await VerifyEndpointAsync();
        Console.WriteLine("GPT-Live WebRTC handshake, sideband, cleanup, and endpoint authorization contracts passed.");
    }
    private static int Port()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(); return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
    private static async Task VerifySidebandAsync(bool rejectAttach)
    {
        var port = Port();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/"); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var clientClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            var create = await listener.GetContextAsync().WaitAsync(timeout.Token);
            Check(create.Request.HttpMethod == "POST" && create.Request.Url!.AbsolutePath == "/v1/live/sessions", "JSON creation route");
            Check(create.Request.Headers["Authorization"] == "Bearer contract-key", "server authentication");
            using var reader = new StreamReader(create.Request.InputStream);
            var body = JsonNode.Parse(await reader.ReadToEndAsync(timeout.Token))!;
            Check(body["transport"]!["sdp"]!.GetValue<string>() == "test-offer", "offer preserved");
            Check(body["session"]!["audio"]!["format"] == null, "WebRTC format is negotiated, not PCM configuration");
            Check(body["session"]!["input"]!.AsArray().Count == 1, "startup history preserved");
            create.Response.StatusCode = 201;
            var answer = Encoding.UTF8.GetBytes("""{"session":{"id":"live_contract"},"transport":{"type":"webrtc","sdp":"test-answer"}}""");
            await create.Response.OutputStream.WriteAsync(answer, timeout.Token); create.Response.Close();
            var attach = await listener.GetContextAsync().WaitAsync(timeout.Token);
            Check(attach.Request.Url!.AbsolutePath == "/v1/live/sessions/live_contract/attach", "opaque session attach route");
            if (rejectAttach)
            {
                attach.Response.StatusCode = 403; attach.Response.Close();
                var hangup = await listener.GetContextAsync().WaitAsync(timeout.Token);
                Check(hangup.Request.HttpMethod == "POST" && hangup.Request.Url!.AbsolutePath.EndsWith("/live_contract/hangup"), "failed attachment hangs up created session");
                hangup.Response.StatusCode = 200; hangup.Response.Close();
                return;
            }
            using var socket = (await attach.AcceptWebSocketAsync(null)).WebSocket;
            // No initial event is emitted on attachment. Sending session.start would fail the next assertion.
            await Send(socket, """{"type":"session.output_audio.delta","delta":"AAA=","start_ms":0,"end_ms":1}""", timeout.Token);
            await Send(socket, """{"type":"session.input_audio.append","audio":"AAA="}""", timeout.Token);
            var command = await Read(socket, timeout.Token);
            Check(command["type"]!.GetValue<string>() == "session.thinking.append", "no session.start on sideband");
            await Send(socket, new JsonObject { ["type"] = "session.thinking.appended", ["client_event_id"] = command["event_id"]!.GetValue<string>() }.ToJsonString(), timeout.Token);
            Check((await Read(socket, timeout.Token))["type"]!.GetValue<string>() == "session.close", "graceful sideband close");
            await Send(socket, """{"type":"session.closed","usage":{"seconds":15},"reason":"close_requested"}""", timeout.Token);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            await clientClosed.Task.WaitAsync(timeout.Token);
        });
        await using var provider = new OpenAiLiveProvider("contract-key");
        provider.SetStartupHistory([Ai.Tlbx.VoiceAssistant.Models.ChatMessage.CreateUserMessage("Previous request")]);
        var reflected = 0;
        provider.OnAudioReceived = _ => reflected++;
        provider.OnEventReceived = e => { if (e["type"]!.GetValue<string>().Contains("audio.")) reflected++; };
        var settings = new OpenAiLiveSettings { Connection = new($"ws://localhost:{port}/v1/live/sessions") };
        if (rejectAttach)
        {
            try { await provider.ConnectWebRtcAsync(settings, "test-offer"); throw new Exception("Expected attach rejection"); }
            catch (WebSocketException) { }
            Check(!provider.IsConnected && !provider.FinalUsageConfirmed, "failed startup releases resources without final usage claim");
        }
        else
        {
            var result = await provider.ConnectWebRtcAsync(settings, "test-offer");
            Check(result.SessionId == "live_contract" && result.Sdp == "test-answer" && provider.IsConnected, "sideband ready without replayed startup event");
            try { await provider.ProcessAudioAsync("AAA="); throw new Exception("Expected audio rejection"); } catch (NotSupportedException) { }
            try { await provider.SendEventAsync(new JsonObject { ["type"] = "session.input_audio.append", ["audio"] = "AAA=" }); throw new Exception("Expected raw audio rejection"); } catch (ArgumentException) { }
            await provider.AppendThinkingAsync("Verified context");
            await provider.DisconnectAsync();
            clientClosed.TrySetResult();
            Check(provider.FinalUsageConfirmed && reflected == 0, "final usage confirmed; reflected audio never relayed to browser callbacks");
        }
        await server;
    }
    private static async Task VerifyEndpointAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddOpenAiDirectLiveVoice(o => { o.RoutePrefix = "/test/live"; o.AuthorizeRequest = c => c.Request.Headers["X-Test-User"] == "owner"; });
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0"); app.MapOpenAiDirectLiveVoice(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var store = app.Services.GetRequiredService<OpenAiDirectLivePreparedSessionStore>();
        var calls = 0;
        var token = store.Prepare((sdp, _) => { calls++; return Task.FromResult(new OpenAiLiveWebRtcSession("live_public", "answer")); });
        async Task<HttpResponseMessage> Request(string? user, string? origin, string body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/test/live/session") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (user != null) request.Headers.Add("X-Test-User", user);
            if (origin != null) request.Headers.Add("Origin", origin);
            return await client.SendAsync(request);
        }
        var payload = new JsonObject { ["preparedSessionId"] = token, ["sdp"] = "offer" }.ToJsonString();
        using var denied = await Request(null, null, payload); Check(denied.StatusCode == HttpStatusCode.Forbidden, "unauthorized request rejected before consumption");
        using var crossOrigin = await Request("owner", "https://evil.example", payload); Check(crossOrigin.StatusCode == HttpStatusCode.Forbidden, "cross-origin request rejected");
        using var oversized = await Request("owner", null, new string('x', 129 * 1024)); Check((int)oversized.StatusCode == 413, "bounded request body");
        using var malformed = await Request("owner", null, "[]"); Check(malformed.StatusCode == HttpStatusCode.BadRequest, "malformed JSON shape rejected");
        using var success = await Request("owner", null, payload);
        Check(success.StatusCode == HttpStatusCode.Created && calls == 1, "authorized token consumed once");
        var response = JsonNode.Parse(await success.Content.ReadAsStringAsync())!.AsObject();
        Check(response.Count == 2 && response["session"]!.AsObject().Count == 1 && response["transport"]!["sdp"]!.GetValue<string>() == "answer", "only public handshake returned");
        using var replay = await Request("owner", null, payload); Check(replay.StatusCode == HttpStatusCode.Gone && calls == 1, "token replay rejected");
        await app.StopAsync();
    }
    private static Task Send(WebSocket socket, string json, CancellationToken token) => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, token);
    private static async Task<JsonObject> Read(WebSocket socket, CancellationToken token)
    {
        using var stream = new MemoryStream(); var bytes = new byte[8192]; WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(bytes, token); if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Unexpected close"); stream.Write(bytes, 0, result.Count); } while (!result.EndOfMessage);
        return JsonNode.Parse(stream.ToArray())!.AsObject();
    }
}
