using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;
using Microsoft.AspNetCore.Http;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.Google;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Models;
using Ai.Tlbx.VoiceAssistant.Provider.XAi;
using Ai.Tlbx.VoiceAssistant.Provider.XAi.Models;

static class ProxyConfigurationTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        var endpoint = new ProviderEndpointOptions("wss://example.invalid/team/realtime?api-version=test&model=explicit");
        var uri = endpoint.BuildUri("model=default&keyterm=a&keyterm=b");
        Check(uri.Query.Contains("api-version=test") && !uri.Query.Contains("default") &&
            uri.Query.Contains("keyterm=a&keyterm=b"), "Endpoint query overrides defaults and preserves repeated parameters");
        endpoint.Endpoint = "https://example.invalid/new";
        endpoint.AuthenticationHeaderName = "api-key";
        endpoint.AuthenticationScheme = null;
        endpoint.ApiKey = "rotated";
        endpoint.Headers["X-Team"] = "accounting";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.BuildUri());
        endpoint.Apply(request, "old");
        Check(request.Headers.GetValues("api-key").Single() == "rotated" && request.Headers.Authorization == null,
            "Custom raw API key replaces bearer auth");
        Check(request.Headers.GetValues("X-Team").Single() == "accounting", "Gateway accounting header");
        endpoint.Endpoint = "https://example.invalid/path#secret";
        try { endpoint.BuildUri(); throw new Exception("Invalid URL accepted"); }
        catch (ArgumentException) { }

        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var wsRoot = $"ws://localhost:{port}";

        var open = new OpenAiVoiceSettings
        {
            UseEphemeralKey = false,
            MostLikelySpokenLanguage = "de",
            TranscriptionHint = "Deutsche Fachbegriffe",
            Instructions = "  Antworte auf Deutsch mit natürlicher deutscher Aussprache.\r\n\t",
            ToolCallPreambleMode = ToolCallPreambleMode.BeforeEveryToolCall,
            AppendToolCallPreambleInstructions = false
        };
        await using (var provider = new OpenAiVoiceProvider("unused"))
            await VerifyReconnect(listener, provider, open, open.Connection, wsRoot, "openai", id => open.ModelId = id);
        var transcription = new OpenAiTranscriptionSettings();
        await using (var provider = new OpenAiTranscriptionProvider("unused"))
            await VerifyReconnect(listener, provider, transcription, transcription.Connection, wsRoot, "openai-stt", id => transcription.ModelId = id);
        var google = new GoogleVoiceSettings();
        await using (var provider = new GoogleVoiceProvider("unused"))
            await VerifyReconnect(listener, provider, google, google.Connection, wsRoot, "google", id => google.ModelId = id);
        var xai = new XaiVoiceSettings();
        await using (var provider = new XaiVoiceProvider("unused"))
            await VerifyReconnect(listener, provider, xai, xai.Connection, wsRoot, "xai", id => xai.ModelId = id);
        var xaiStt = new XaiTranscriptionSettings();
        await using (var provider = new XaiTranscriptionProvider("unused"))
            await VerifyReconnect(listener, provider, xaiStt, xaiStt.Connection, wsRoot, "xai-stt", id => xaiStt.ModelId = id);
        await VerifyHttpAsync();
        Console.WriteLine("Proxy configuration: five WebSocket providers reconnect; HTTP routing/auth/model updates passed.");
    }

    private static async Task VerifyReconnect(HttpListener listener, IVoiceProvider provider, IVoiceSettings settings,
        ProviderEndpointOptions connection, string root, string kind, Action<string> setModel)
    {
        for (var revision = 1; revision <= 2; revision++)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var alias = $"team/{kind}-{revision}";
            setModel(alias);
            connection.Endpoint = $"{root}/gateway/{kind}/{revision}?api-version=test";
            connection.ApiKey = $"key-{revision}";
            connection.Headers["X-Cost-Centre"] = $"team-{revision}";
            connection.ConfigureWebSocket = options => options.SetRequestHeader("X-Dynamic", "callback");
            var contextTask = listener.GetContextAsync();
            var connectTask = provider.ConnectAsync(settings);
            var context = await contextTask.WaitAsync(timeout.Token);
            Check(context.Request.Url!.AbsolutePath.EndsWith($"/{revision}"), kind + " endpoint refreshed");
            Check(context.Request.QueryString["api-version"] == "test", kind + " query preserved");
            Check(context.Request.Headers["X-Cost-Centre"] == $"team-{revision}" &&
                context.Request.Headers["X-Dynamic"] == "callback", kind + " headers refreshed");
            if (kind == "google")
                Check(context.Request.QueryString["key"] == $"key-{revision}", "Google key refreshed");
            else Check(context.Request.Headers["Authorization"] == $"Bearer key-{revision}", kind + " key refreshed");
            if (kind is "openai" or "xai")
                Check(context.Request.QueryString["model"] == alias, kind + " alias encoded in query");
            if (kind is "openai-stt" or "xai-stt")
                Check(context.Request.QueryString["model"] == null, kind + " omits unsupported native model query");
            using var server = (await context.AcceptWebSocketAsync(null)).WebSocket;
            if (kind is "google" or "xai-stt")
            {
                var ready = kind == "google" ? "{\"setupComplete\":{}}" : "{\"type\":\"transcript.created\"}";
                await server.SendAsync(Encoding.UTF8.GetBytes(ready).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            }
            if (kind != "xai-stt")
            {
                var buffer = new byte[65536];
                var message = await server.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                var json = Encoding.UTF8.GetString(buffer, 0, message.Count);
                if (kind == "openai-stt")
                    Check(!json.Contains("turn_detection"), "Live transcription omits unsupported turn detection");
                if (kind is "google" or "openai-stt")
                    Check(json.Contains(alias), kind + " custom model in session payload");
                if (kind == "openai")
                {
                    var voiceSettings = (OpenAiVoiceSettings)settings;
                    VerifyOpenAiLanguage(json, voiceSettings);
                    await connectTask.WaitAsync(timeout.Token);
                    voiceSettings.MostLikelySpokenLanguage = revision == 1 ? "fr" : "de";
                    voiceSettings.InputAudioTranscription.Prompt = "Explicit transcription hint " + revision;
                    voiceSettings.Instructions = "Updated response language and accent instructions " + revision;
                    await provider.UpdateSettingsAsync(settings).WaitAsync(timeout.Token);
                    message = await server.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                    VerifyOpenAiLanguage(Encoding.UTF8.GetString(buffer, 0, message.Count), voiceSettings);
                    foreach (var mode in Enum.GetValues<ToolCallPreambleMode>())
                    foreach (var append in new[] { true, false })
                    {
                        voiceSettings.ToolCallPreambleMode = mode;
                        voiceSettings.AppendToolCallPreambleInstructions = append;
                        await provider.UpdateSettingsAsync(settings).WaitAsync(timeout.Token);
                        message = await server.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                        VerifyOpenAiLanguage(Encoding.UTF8.GetString(buffer, 0, message.Count), voiceSettings);
                        Check(voiceSettings.ToolCallPreambleMode == mode, "Prompt switch preserves selected mode");
                    }
                }
            }
            if (kind == "xai")
                await server.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"session.updated\",\"session\":{}}").AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            await connectTask.WaitAsync(timeout.Token);
            Check(provider.IsConnected, kind + " connection established");
            // Keep draining so the provider's normal close handshake can complete.
            var drain = Task.Run(async () =>
            {
                try
                {
                    var buffer = new byte[65536];
                    while (server.State == WebSocketState.Open)
                    {
                        var result = await server.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
                            break;
                        }
                    }
                }
                catch (WebSocketException) { }
            });
            await provider.DisconnectAsync().WaitAsync(timeout.Token);
            server.Abort();
            await drain.WaitAsync(timeout.Token);
        }
    }

    private static void VerifyOpenAiLanguage(string json, OpenAiVoiceSettings settings)
    {
        using var document = JsonDocument.Parse(json);
        var session = document.RootElement.GetProperty("session");
        var transcription = session.GetProperty("audio").GetProperty("input").GetProperty("transcription");
        Check(transcription.GetProperty("language").GetString() == settings.MostLikelySpokenLanguage,
            "OpenAI WebSocket sends input language on connect, update and reconnect");
        Check(transcription.GetProperty("prompt").GetString() ==
            (settings.InputAudioTranscription.Prompt ?? settings.TranscriptionHint),
            "OpenAI WebSocket uses explicit transcription prompt before fallback hint");
        VerifyInstructions(session, settings);
    }

    private static void VerifyInstructions(JsonElement session, OpenAiVoiceSettings settings)
    {
        var instructions = session.GetProperty("instructions").GetString()!;
        if (!settings.AppendToolCallPreambleInstructions || settings.ToolCallPreambleMode == ToolCallPreambleMode.ProviderDefault)
            Check(instructions == settings.Instructions, "OpenAI sends the exact application prompt when augmentation is disabled");
        else
            Check(instructions.StartsWith(settings.Instructions + Environment.NewLine + Environment.NewLine, StringComparison.Ordinal) &&
                instructions.Contains("# Tool call preambles", StringComparison.Ordinal), "OpenAI retains opt-in/default preamble augmentation");
    }

    private static async Task VerifyHttpAsync()
    {
        var capture = new CaptureHttpMessageHandler();
        using var client = new HttpClient(capture);
        var settings = new OpenAiVoiceSettings
        {
            ModelId = "team/realtime",
            Instructions = "  Antworte ausschließlich auf Deutsch.\r\n\t",
            ToolCallPreambleMode = ToolCallPreambleMode.BeforeEveryToolCall
        };
        await using var provider = new OpenAiVoiceProvider("fallback", httpClient: client) { Settings = settings };
        var create = typeof(OpenAiVoiceProvider).GetMethod("CreateSessionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        for (var n = 1; n <= 2; n++)
        {
            settings.AppendToolCallPreambleInstructions = n == 2;
            settings.ClientSecretsConnection.Endpoint = $"https://gateway.invalid/{n}/client_secrets?api-version=test";
            settings.ClientSecretsConnection.ApiKey = $"rotated-{n}";
            settings.ModelId = $"team/model-{n}";
            await (Task<string>)create.Invoke(provider, null)!;
            Check(capture.RequestUri!.AbsolutePath == $"/{n}/client_secrets" &&
                capture.Authorization == $"Bearer rotated-{n}" && capture.RequestBody!.Contains($"team/model-{n}"),
                "Client secrets use runtime endpoint, auth and alias");
            using var payload = JsonDocument.Parse(capture.RequestBody!);
            VerifyInstructions(payload.RootElement.GetProperty("session"), settings);
        }

        var directOptions = new OpenAiDirectRealtimeOptions { ClientSecretsHttpClient = client };
        var registryType = typeof(OpenAiDirectRealtimeOptions).Assembly.GetType(
            "Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore.OpenAiDirectRealtimeSessionRegistry")!;
        var registry = Activator.CreateInstance(registryType, directOptions)!;
        var createDirect = typeof(OpenAiDirectRealtimeEndpointExtensions).GetMethod(
            "CreateSessionAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        for (var n = 1; n <= 2; n++)
        {
            settings.AppendToolCallPreambleInstructions = n == 2;
            settings.ModelId = $"browser/model-{n}";
            settings.ClientSecretsConnection.Endpoint = $"https://gateway.invalid/browser/{n}/client_secrets";
            settings.RealtimeCallsEndpoint = $"https://gateway.invalid/browser/{n}/calls?api-version=test";
            var browserContext = new DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim("sub", "contract-user")], "contract-test"))
            };
            var result = await (Task<IResult>)createDirect.Invoke(null,
                [browserContext, new OpenAiDirectRealtimeSessionRequest(), new DirectFactory(settings),
                 registry, directOptions, CancellationToken.None])!;
            var session = (result as IValueHttpResult)?.Value as OpenAiDirectRealtimeSessionResponse;
            Check(session?.Model == settings.ModelId && session.RealtimeCallsEndpoint == settings.RealtimeCallsEndpoint &&
                session.ClientSecret == "ephemeral-contract-key", "Direct browser receives configured SDP URL, model and ephemeral key");
            Check(capture.RequestUri!.AbsolutePath == $"/browser/{n}/client_secrets" &&
                capture.RequestBody!.Contains(settings.ModelId), "Direct browser server uses configured secret endpoint and model");
            using var payload = JsonDocument.Parse(capture.RequestBody!);
            VerifyInstructions(payload.RootElement.GetProperty("session"), settings);
        }

        var handler = new TranscriptionHandler();
        using var transcriptionClient = new HttpClient(handler);
        var options = new OpenAiHttpLiveTranscriptionOptions();
        await using (var transcriber = new OpenAiHttpLiveTranscriber(new ContractAudioHardware(), options, "fallback", null, transcriptionClient))
        {
            var type = typeof(OpenAiHttpLiveTranscriber);
            var workItem = type.GetNestedType("SnapshotWorkItem", BindingFlags.NonPublic)!;
            var send = type.GetMethod("TranscribeSnapshotAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            for (var n = 1; n <= 2; n++)
            {
                options.Connection.Endpoint = $"https://gateway.invalid/{n}/audio/transcriptions";
                options.Connection.ApiKey = $"http-key-{n}";
                options.ModelId = $"team/transcribe-{n}";
                var snapshot = Activator.CreateInstance(workItem, 1, (long)n, new byte[4800], false, "", TimeSpan.Zero, TimeSpan.FromMilliseconds(100))!;
                await (Task)send.Invoke(transcriber, [snapshot, (Action<string>)(_ => { })])!;
                Check(handler.Uri!.AbsolutePath == $"/{n}/audio/transcriptions" &&
                    handler.Authorization == $"Bearer http-key-{n}" && handler.Body!.Contains(options.ModelId),
                    "HTTP transcriber refreshes endpoint, auth and multipart model");
            }
        }
        using var response = await transcriptionClient.GetAsync("https://gateway.invalid/ownership");
        Check(response.IsSuccessStatusCode, "Injected HTTP client remains caller-owned");
        Check(transcriptionClient.DefaultRequestHeaders.Authorization == null, "No shared client authentication mutation");
    }

    private sealed class DirectFactory(OpenAiVoiceSettings settings) : IOpenAiDirectRealtimeSessionFactory
    {
        public Task<OpenAiDirectRealtimeSessionSpec> CreateSessionAsync(HttpContext context,
            OpenAiDirectRealtimeSessionRequest request, string voiceSessionId, CancellationToken cancellationToken) =>
            Task.FromResult(new OpenAiDirectRealtimeSessionSpec { OpenAiApiKey = "server-key", Settings = settings });
    }

    private sealed class TranscriptionHandler : HttpMessageHandler
    {
        public Uri? Uri;
        public string? Authorization;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"type\":\"transcript.text.done\",\"text\":\"hello\"}\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
