using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Extensions;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Telnyx;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

static class TelnyxTests
{
    private static readonly Ed25519PrivateKeyParameters Key = new(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray(), 0);
    private static string PublicKey => Convert.ToBase64String(Key.GeneratePublicKey().GetEncoded());
    public static async Task RunAsync()
    {
        VerifySignaturesAndCodecs();
        VerifyResampling();
        await VerifyRestAsync();
        await VerifyCallsAsync();
        Console.WriteLine("Telnyx contracts passed: native REST/credentials/errors, repeatable provisioning, signed HTTP/WebSockets, duplicate/late events, isolated AI calls, PCM/G.711/resampling, remote playback marks, interruption and hangup cleanup.");
    }
    private static TelnyxOptions Options() => new() { ApiKey = "test-key", PublicKey = PublicKey, ConnectionId = "app-1", FromNumber = "+49301234567", PublicBaseUri = new("https://voice.example.test") };
    private static void VerifySignaturesAndCodecs()
    {
        var verifier = new TelnyxSignatureVerifier(Options());
        string now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), body = "{ \"native\": \"Grüße ☎\" }";
        Check(verifier.Verify(now, Sign(now, body), body), "valid Ed25519 signature");
        Check(!verifier.Verify(now, Sign(now, body), body + " "), "exact body verification");
        foreach (int minutes in new[] { -6, 6 })
        { string time = DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds().ToString(); Check(!verifier.Verify(time, Sign(time, body), body), "past/future replay rejected"); }
        Check(!verifier.Verify(now, "invalid", body), "invalid base64 rejected");
        Check(TelnyxPcmCodec.Decode([0xff, 0x7f, 0x80, 0x00], "PCMU").SequenceEqual(Pcm([0, 0, 32124, -32124])), "ITU mu-law reference samples");
        Check(TelnyxPcmCodec.Decode([0xd5, 0x55, 0xaa, 0x2a], "PCMA").SequenceEqual(Pcm([8, -8, 32256, -32256])), "ITU A-law reference samples");
        Check(TelnyxPcmCodec.Encode(Pcm([0x1234, -2]), "L16").SequenceEqual(new byte[] { 0x34, 0x12, 0xfe, 0xff }), "Telnyx L16 little-endian wire");
        foreach (string codec in new[] { "PCMU", "PCMA" })
        {
            byte[] pcm = Pcm([-30000, -10000, -1000, 0, 1000, 10000, 30000]), decoded = TelnyxPcmCodec.Decode(TelnyxPcmCodec.Encode(pcm, codec), codec);
            for (int i = 0; i < pcm.Length; i += 2) Check(Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i)) - BinaryPrimitives.ReadInt16LittleEndian(decoded.AsSpan(i))) < 1000, "G.711 quantization tolerance");
        }
    }
    private static void VerifyResampling()
    {
        byte[] input = Tone(16000, 1000, 1600);
        var whole = new Pcm16Resampler(16000, 24000);
        byte[] expected = [.. whole.Process(input), .. whole.Process([], true)];
        var split = new Pcm16Resampler(16000, 24000); var actual = new List<byte>();
        for (int offset = 0; offset < input.Length; offset += 34) actual.AddRange(split.Process(input.AsSpan(offset, Math.Min(34, input.Length - offset))));
        actual.AddRange(split.Process([], true));
        Check(expected.Length == 4800 && expected.SequenceEqual(actual), "resampling preserves duration/phase exactly across arbitrary packet boundaries");
        var low = new Pcm16Resampler(24000, 8000); var high = new Pcm16Resampler(24000, 8000);
        byte[] pass = [.. low.Process(Tone(24000, 1000, 2400)), .. low.Process([], true)];
        byte[] stop = [.. high.Process(Tone(24000, 6000, 2400)), .. high.Process([], true)];
        Check(Energy(pass) > 6500 && Energy(stop.AsSpan(64, stop.Length - 128).ToArray()) < 200, "downsampling preserves speech-band tones and filters aliasing");
    }
    private static async Task VerifyRestAsync()
    {
        var requests = new List<(HttpMethod Method, string Path, JsonElement Body)>(); bool profile = false, application = false;
        using var http = new HttpClient(new HttpTranscriptionTests.Handler(async (request, ct) =>
        {
            Check(request.Headers.Authorization?.ToString() == "Bearer test-key", "server-side bearer authentication");
            using var document = JsonDocument.Parse(request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct));
            string path = request.RequestUri!.AbsolutePath; requests.Add((request.Method, request.RequestUri.PathAndQuery, document.RootElement.Clone()));
            if (request.Method == HttpMethod.Get && path.EndsWith("outbound_voice_profiles"))
            {
                Check(Uri.UnescapeDataString(request.RequestUri.Query).Contains("filter[name][contains]="), "official profile name filter schema");
                return Json(!profile ? "{\"data\":[]}" : Uri.UnescapeDataString(request.RequestUri.Query).Contains("page[number]=2")
                    ? "{\"data\":[{\"id\":\"profile-1\",\"name\":\"secretary\"}],\"meta\":{\"total_pages\":2}}"
                    : "{\"data\":[{\"id\":\"other\",\"name\":\"secretary-other\"}],\"meta\":{\"total_pages\":2}}");
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("call_control_applications")) return Json(application ? "{\"data\":[{\"id\":\"app-1\",\"application_name\":\"secretary\"}]}" : "{\"data\":[]}");
            if (path.EndsWith("outbound_voice_profiles")) { profile = true; return Json("{\"data\":{\"id\":\"profile-1\"}}"); }
            if (path.EndsWith("call_control_applications")) { application = true; return Json("{\"data\":{\"id\":\"app-1\"}}"); }
            if (path.Contains("phone_numbers/")) return Json("{\"data\":{\"phone_number\":\"+49301234567\"}}");
            if (path.EndsWith("error")) return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"errors\":[{\"code\":\"10011\"}]}") };
            return Json("{\"data\":{\"call_control_id\":\"native-id\",\"future_field\":true}}");
        }));
        var client = new TelnyxClient(http, Options());
        var response = await client.DialAsync(new JsonObject { ["future_option"] = "native", ["to"] = "+49309999999" });
        Check(response.GetProperty("data").GetProperty("future_field").GetBoolean() && requests[^1].Body.GetProperty("future_option").GetString() == "native", "native fields retained");
        await client.DialAsync("+49309999999");
        Check(requests[^1].Body.GetProperty("stream_bidirectional_target_legs").GetString() == "self" && requests[^1].Body.GetProperty("stream_bidirectional_codec").GetString() == "L16", "outgoing audio targets the actual caller leg");
        try { await client.SendAsync(HttpMethod.Get, "https://other.example/v2/key"); throw new Exception("off-host API request accepted"); } catch (ArgumentException) { }
        try { await client.SendAsync(HttpMethod.Post, "error"); throw new Exception("API error hidden"); } catch (TelnyxApiException ex) { Check(ex.StatusCode == HttpStatusCode.TooManyRequests && ex.ResponseBody.Contains("10011"), "native error/status retained"); }
        var spec = new TelnyxProvisioningRequest { ApplicationName = "secretary", PhoneNumberId = "number-1", OutboundProfile = new() { ["whitelisted_destinations"] = new JsonArray("DE") } };
        var first = await client.ProvisionAsync(spec); var second = await client.ProvisionAsync(spec);
        Check(first == second && first.ConnectionId == "app-1", "repeat provisioning keeps resource identities");
        Check(requests.Count(x => x.Method == HttpMethod.Post && x.Path.EndsWith("call_control_applications")) == 1 && requests.Count(x => x.Method == HttpMethod.Post && x.Path.EndsWith("outbound_voice_profiles")) == 1, "no duplicate applications/profiles");
        Check(!requests.Any(x => x.Path.Contains("number_orders")), "provisioning cannot accidentally buy a number");
    }
    private static async Task VerifyCallsAsync()
    {
        var commands = new ConcurrentQueue<(string Path, JsonElement Body)>(); var providers = new ConcurrentQueue<FakeProvider>();
        var calls = new ConcurrentDictionary<string, TelnyxAudioHardware>(); var errors = new ConcurrentQueue<Exception>();
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddVoiceAssistant().WithTelnyx(o => { var spec = Options(); o.ApiKey = spec.ApiKey; o.PublicKey = spec.PublicKey; o.PublicBaseUri = spec.PublicBaseUri; o.ConnectionId = spec.ConnectionId; o.FromNumber = spec.FromNumber; });
        builder.Services.AddScoped<IVoiceProvider>(_ => { var p = new FakeProvider(); providers.Enqueue(p); return p; });
        builder.Services.AddHttpClient<TelnyxClient>().ConfigurePrimaryHttpMessageHandler(() => new HttpTranscriptionTests.Handler(async (request, ct) =>
        { using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); commands.Enqueue((request.RequestUri!.AbsolutePath, json.RootElement.Clone())); return Json("{\"data\":{\"result\":\"ok\"}}"); }));
        await using var app = builder.Build(); app.UseWebSockets();
        app.MapTelnyxVoice(async (call, ct) => { Check(ReferenceEquals(call.Audio, call.Services.GetRequiredService<IAudioHardwareAccess>()), "DI scope shares one audio device"); calls[call.CallControlId] = call.Audio; await call.RunAssistantAsync(new OpenAiVoiceSettings(), ct); });
        var host = app.Services.GetRequiredService<TelnyxVoiceHost>(); host.OnError = errors.Enqueue;
        await app.StartAsync(); string origin = app.Urls.Single(); using var http = new HttpClient { BaseAddress = new(origin) };
        string incoming = Webhook("event-1", "call.initiated", "call-in", "incoming");
        using var rejected = await Post(http, incoming, false); Check(rejected.StatusCode == HttpStatusCode.Unauthorized, "unsigned webhook rejected");
        using var accepted = await Post(http, incoming); Check(accepted.StatusCode == HttpStatusCode.OK, "signed webhook acknowledged");
        await Until(() => commands.Count == 1);
        Check(commands.First().Body.GetProperty("command_id").GetString() == "event-1" && commands.First().Path.EndsWith("/call-in/actions/answer"), "answer with stable command ID");
        using var duplicate = await Post(http, incoming); await Task.Delay(30); Check(commands.Count == 1, "duplicate event deduplicated");
        using var outgoing = await Post(http, Webhook("out", "call.initiated", "call-out", "outgoing"));
        using var ended = await Post(http, Webhook("late-end", "call.hangup", "late", "incoming"));
        using var late = await Post(http, Webhook("late-start", "call.initiated", "late", "incoming"));
        await Task.Delay(30); Check(commands.Count == 1, "outgoing/late-after-hangup calls are not answered");
        using (var bad = new ClientWebSocket()) { try { await bad.ConnectAsync(SocketUri(origin), CancellationToken.None); throw new Exception("unsigned socket accepted"); } catch (WebSocketException) { } }
        using var socket = await Connect(origin); await Send(socket, Start("call-in", "stream-in", "L16", 16000));
        await Until(() => providers.TryPeek(out var p) && p.IsConnected);
        await Send(socket, Media(Tone(16000, 1000, 1600)));
        var output = await Read(socket); Check(output.GetProperty("event").GetString() == "media", "AI audio returned on real WebSocket");
        byte[] wire = Convert.FromBase64String(output.GetProperty("media").GetProperty("payload").GetString()!);
        Check(wire.Length == 640 && Energy(wire) > 2000, "20ms L16 PCM frame, no WAV/RTP header");
        var first = providers.First(); Check(first.Audio.First().Length is >= 4700 and <= 4800 && Energy(first.Audio.First()) > 2000, "input resampled from 16kHz to 24kHz");
        var drain = calls["call-in"].WaitForPlaybackDrainAsync(TimeSpan.FromSeconds(3)); JsonElement mark;
        do { mark = await Read(socket); } while (mark.GetProperty("event").GetString() != "mark");
        Check(!drain.IsCompleted, "remote playback not inferred from successful writes");
        await Send(socket, JsonNode.Parse(mark.GetRawText())!.AsObject()); Check(await drain, "remote mark confirms playback");
        await calls["call-in"].ClearAudioQueueAsync(); Check((await Read(socket)).GetProperty("event").GetString() == "clear", "interruption clears remote playback");
        using var other = await Connect(origin); await Send(other, Start("call-out", "stream-out", "PCMU", 8000));
        await Until(() => providers.Count == 2 && providers.Last().IsConnected);
        await Send(other, Media(Enumerable.Repeat((byte)0x80, 160).ToArray())); _ = await Read(other);
        Check(!ReferenceEquals(calls["call-in"], calls["call-out"]) && providers.Last().Audio.First().Length > 800, "parallel calls isolated, narrowband codec honored");
        using var hangup = await Post(http, Webhook("end-1", "call.hangup", "call-in", "incoming")); await Until(() => !first.IsConnected);
        Check(providers.Last().IsConnected, "one hangup does not terminate another call");
        await Send(other, new() { ["event"] = "stop", ["stream_id"] = "stream-out" });
        await Until(() => host.ActiveCallCount == 0 && providers.All(p => !p.IsConnected));
        Check(errors.IsEmpty, "normal lifecycle has no errors: " + string.Join(',', errors.Select(e => e.Message)));
        // A network failure must also stop the AI and terminate the paid telephone leg.
        using var broken = await Connect(origin); await Send(broken, Start("broken", "broken-stream", "L16", 16000));
        await Until(() => providers.Count == 3 && providers.Last().IsConnected);
        broken.Abort();
        await Until(() => commands.Any(c => c.Path.EndsWith("/broken/actions/hangup")) && !providers.Last().IsConnected && host.ActiveCallCount == 0);
        await app.StopAsync();
    }
    private static string Webhook(string id, string type, string call, string direction) => new JsonObject { ["data"] = new JsonObject { ["id"] = id, ["event_type"] = type, ["payload"] = new JsonObject { ["call_control_id"] = call, ["direction"] = direction, ["connection_id"] = "app-1", ["to"] = "+49301234567" } } }.ToJsonString();
    private static JsonObject Start(string id, string stream, string codec, int rate) => new() { ["event"] = "start", ["stream_id"] = stream, ["start"] = new JsonObject { ["call_control_id"] = id, ["media_format"] = new JsonObject { ["encoding"] = codec, ["sample_rate"] = rate, ["channels"] = 1 } } };
    private static JsonObject Media(byte[] bytes) => new() { ["event"] = "media", ["media"] = new JsonObject { ["payload"] = Convert.ToBase64String(bytes), ["track"] = "inbound" } };
    private static async Task<HttpResponseMessage> Post(HttpClient client, string json, bool signed = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/telnyx/webhook") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (signed) { string now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(); request.Headers.Add("telnyx-timestamp", now); request.Headers.Add("telnyx-signature-ed25519", Sign(now, json)); }
        return await client.SendAsync(request);
    }
    private static async Task<ClientWebSocket> Connect(string origin)
    {
        var socket = new ClientWebSocket(); string now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        socket.Options.SetRequestHeader("telnyx-timestamp", now); socket.Options.SetRequestHeader("telnyx-signature-ed25519", Sign(now, Options().MediaUri.AbsoluteUri));
        await socket.ConnectAsync(SocketUri(origin), CancellationToken.None); return socket;
    }
    private static Uri SocketUri(string origin) => new(origin.Replace("http://", "ws://") + "/telnyx/media");
    private static async Task Send(ClientWebSocket socket, JsonObject body) => await socket.SendAsync(Encoding.UTF8.GetBytes(body.ToJsonString()).AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Read(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); using var bytes = new MemoryStream(); byte[] buffer = new byte[8192]; ValueWebSocketReceiveResult read;
        do { read = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token); Check(read.MessageType == WebSocketMessageType.Text, "expected media JSON"); bytes.Write(buffer, 0, read.Count); } while (!read.EndOfMessage);
        using var json = JsonDocument.Parse(bytes.ToArray()); return json.RootElement.Clone();
    }
    private static string Sign(string time, string text) { var signer = new Ed25519Signer(); signer.Init(true, Key); byte[] bytes = Encoding.UTF8.GetBytes(time + "|" + text); signer.BlockUpdate(bytes, 0, bytes.Length); return Convert.ToBase64String(signer.GenerateSignature()); }
    private static byte[] Pcm(short[] samples) { var bytes = new byte[samples.Length * 2]; for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]); return bytes; }
    private static byte[] Tone(int rate, int frequency, int count) => Pcm(Enumerable.Range(0, count).Select(i => (short)(10000 * Math.Sin(2 * Math.PI * frequency * i / rate))).ToArray());
    private static double Energy(byte[] pcm) => Math.Sqrt(Enumerable.Range(0, pcm.Length / 2).Select(i => Math.Pow(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2)), 2)).Average());
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static async Task Until(Func<bool> predicate) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!predicate()) await Task.Delay(10, timeout.Token); }
    private static void Check(bool passed, string message) { if (!passed) throw new Exception("Telnyx: " + message); }
    sealed class FakeProvider : IVoiceProvider
    {
        public ConcurrentQueue<byte[]> Audio { get; } = new(); public bool IsConnected { get; private set; }
        public AudioSampleRate RequiredInputSampleRate => AudioSampleRate.Rate24000;
        public Task ConnectAsync(IVoiceSettings settings) { IsConnected = true; return Task.CompletedTask; }
        public Task DisconnectAsync() { IsConnected = false; return Task.CompletedTask; }
        public Task UpdateSettingsAsync(IVoiceSettings settings) => Task.CompletedTask;
        public Task ProcessAudioAsync(string audio) { Audio.Enqueue(Convert.FromBase64String(audio)); if (Audio.Count == 1) OnAudioReceived?.Invoke(Convert.ToBase64String(Tone(24000, 1000, 2400))); return Task.CompletedTask; }
        public Task SendInterruptAsync() => Task.CompletedTask;
        public Task InjectConversationHistoryAsync(IEnumerable<ChatMessage> history) => Task.CompletedTask;
        public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
        public Action<ChatMessage>? OnMessageReceived { get; set; } public Action<string>? OnAudioReceived { get; set; }
        public Func<TimeSpan?, Task<bool>>? WaitForPlaybackDrainAsync { get; set; }
        public Action<string>? OnStatusChanged { get; set; } public Action<string>? OnError { get; set; }
        public Action? OnInterruptDetected { get; set; } public Action<UsageReport>? OnUsageReceived { get; set; }
        public Action<string>? OnTranscriptionDelta { get; set; } public Action<string>? OnTranscriptionCompleted { get; set; }
    }
}
