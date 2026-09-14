using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Demo.Web.Tools;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

internal static class LargeResultIntegrationTests
{
    public static void VerifyFixtures()
    {
        foreach (var dossier in new[] { false, true })
        {
            var text = LargeResultFixture.Create(dossier);
            var code = dossier ? LargeResultFixture.DossierTailCode : LargeResultFixture.CatalogTailCode;
            if (text.Length <= (dossier ? 100000 : 40000) || text.IndexOf(code, StringComparison.Ordinal) <= 40000)
                throw new InvalidOperationException("Large fixture must require reading beyond 40000 characters.");
            if (JsonNode.Parse(text)?["final_inspection"]?["approval_code"]?.GetValue<string>() != code)
                throw new InvalidOperationException("Large fixture is not complete JSON.");
            Console.WriteLine($"Large fixture {(dossier ? "dossier" : "catalog")}: {text.Length} characters / {Encoding.UTF8.GetByteCount(text)} UTF-8 bytes.");
        }
    }

    // Explicit paid integration only. Feed the same mono PCM16/24kHz WAV to both protocols.
    public static async Task RunAsync(string mode)
    {
        if (mode == "wire-limit") { await VerifyManagedWireLimitAsync(); return; }
        if (mode is not ("client" or "managed" or "realtime")) throw new ArgumentException("Unknown integration mode: " + mode);
        var wavPath = Environment.GetEnvironmentVariable("LARGE_RESULT_SMOKE_WAV")
            ?? throw new InvalidOperationException("LARGE_RESULT_SMOKE_WAV is required.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(85));
        using var feedLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        Task? feed = null;
        var errors = new ConcurrentQueue<string>();
        var transcript = new StringBuilder();
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new CountingTool(new LargeCatalogTool());
        var dossier = new CountingTool(new LargeDossierTool());
        var useDossier = Environment.GetEnvironmentVariable("LARGE_RESULT_SMOKE_DOSSIER") == "1";
        var useBoth = Environment.GetEnvironmentVariable("LARGE_RESULT_SMOKE_BOTH") == "1";
        var expected = useDossier ? "8426" : "7319";
        var quantity = useDossier ? "863" : "417";
        var started = DateTimeOffset.UtcNow;
        var voiceSeconds = 0.0;
        var backendTokens = 0;
        IVoiceProvider provider;
        IVoiceSettings settings;
        const string instructions = "Antworte kurz auf Deutsch. Delegiere Fragen nach Katalog oder Projektakte zwingend an das Backend und verwende das passende Werkzeug. Nenne den Freigabecode und die freigegebene Menge exakt aus dem abschließenden Prüfvermerk. Erfinde keine Werte.";
        if (mode == "realtime")
        {
            provider = new OpenAiVoiceProvider();
            settings = new OpenAiVoiceSettings { UseEphemeralKey = false, AppendToolCallPreambleInstructions = false,
                Instructions = instructions, Tools = [tool, dossier] };
        }
        else
        {
            var live = new OpenAiLiveProvider();
            provider = live;
            var backend = new JsonObject { ["model"] = "gpt-5.6-luna", ["instructions"] = instructions,
                ["max_output_tokens"] = 2048, ["parallel_tool_calls"] = false };
            settings = new OpenAiLiveSettings { Instructions = instructions, Tools = [tool, dossier],
                Responses = mode == "managed" ? backend : null,
                ClientBackend = mode == "client" ? new OpenAiLiveClientBackendOptions { Responses = backend } : null };
            live.OnTranscriptDelta = d => { if (d.Role == "assistant") Observe(d.Delta); };
        }
        provider.OnError = e => { errors.Enqueue(e); if (mode != "managed") heard.TrySetException(new InvalidOperationException(e)); else heard.TrySetResult(); };
        provider.OnMessageReceived = m => { if (mode == "realtime" && m.Role == ChatMessage.AssistantRole && m.ToolCallId == null) Observe(m.Content); };
        provider.OnUsageReceived = u => { if (u.SessionDuration.HasValue) voiceSeconds = Math.Max(voiceSeconds, u.SessionDuration.Value.TotalSeconds); else backendTokens += u.TotalTokens; };
        void Observe(string text)
        {
            lock (transcript)
            {
                transcript.Append(text);
                var all = transcript.ToString();
                if (all.Contains(expected, StringComparison.Ordinal) && all.Contains(quantity, StringComparison.Ordinal)
                    && (!useBoth || (all.Contains("8426", StringComparison.Ordinal) && all.Contains("863", StringComparison.Ordinal)))) heard.TrySetResult();
            }
        }
        try
        {
            await provider.ConnectAsync(settings);
            var pcm = ReadPcm(wavPath);
            // Continuous silence is necessary for GPT-Live to keep advancing audio time.
            feed = Task.Run(async () =>
            {
                foreach (var chunk in pcm.Chunk(960))
                {
                    if (!provider.IsConnected) return;
                    await provider.ProcessAudioAsync(Convert.ToBase64String(chunk));
                    await Task.Delay(20, feedLifetime.Token);
                }
                var silence = Convert.ToBase64String(new byte[960]);
                while (!heard.Task.IsCompleted && provider.IsConnected)
                {
                    await provider.ProcessAudioAsync(silence);
                    await Task.Delay(20, feedLifetime.Token);
                }
            }, feedLifetime.Token);
            await heard.Task.WaitAsync(timeout.Token);
            await feed;
            if (mode == "managed")
            {
                if (errors.IsEmpty || provider is not OpenAiLiveProvider lp || lp.BackendContinuationError == null)
                    throw new InvalidOperationException("Expected explicit managed buffer-limit failure.");
                var result = lp.ToolResults.Single();
                if (result.Output == null || result.Output.Length <= 40000) throw new InvalidOperationException("Managed failure did not retain full output.");
            }
            else if (!errors.IsEmpty) throw new InvalidOperationException(string.Join("; ", errors));
            if (tool.Count != (useDossier ? 0 : 1) || dossier.Count != (useDossier || useBoth ? 1 : 0))
                throw new InvalidOperationException($"Unexpected tool execution counts: catalog={tool.Count}, dossier={dossier.Count}");
        }
        finally
        {
            feedLifetime.Cancel();
            if (feed != null) { try { await feed; } catch (OperationCanceledException) { } }
            await provider.DisconnectAsync();
            await provider.DisposeAsync();
            Console.WriteLine(new JsonObject { ["mode"] = mode, ["dossier"] = useDossier, ["both"] = useBoth, ["catalog_calls"] = tool.Count,
                ["dossier_calls"] = dossier.Count, ["assistant_transcript"] = transcript.ToString(),
                ["errors"] = new JsonArray(errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
                ["voice_seconds"] = voiceSeconds, ["backend_tokens"] = backendTokens,
                ["elapsed_seconds"] = (DateTimeOffset.UtcNow - started).TotalSeconds }.ToJsonString());
        }
    }

    private static async Task VerifyManagedWireLimitAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        await socket.ConnectAsync(new Uri("wss://api.openai.com/v1/live/sessions"), deadline.Token);
        async Task Send(string json) => await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, deadline.Token);
        async Task<JsonObject> Read()
        {
            using var stream = new MemoryStream();
            var buffer = new byte[65536];
            WebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
                if (part.MessageType == WebSocketMessageType.Close) throw new IOException("Live closed before test completed.");
                stream.Write(buffer, 0, part.Count);
            } while (!part.EndOfMessage);
            return JsonNode.Parse(stream.ToArray())!.AsObject();
        }
        await Send("""{"type":"session.start","session":{"model":"gpt-live-1","instructions":"Remain silent during this synthetic test.","store":false,"audio":{"format":{"type":"audio/pcm","rate":24000},"output":{"voice":"marin"}},"delegation":{"type":"responses","responses":{"model":"gpt-5.6-luna","instructions":"Call get_large_catalog exactly once.","max_output_tokens":512,"tool_choice":"required","parallel_tool_calls":false,"tools":[{"type":"function","name":"get_large_catalog","description":"Read a synthetic catalog","parameters":{"type":"object","properties":{},"additionalProperties":false}}]}}}}""");
        while ((await Read())["type"]?.GetValue<string>() != "session.started") { }
        await Send("""{"type":"response.create","event_id":"raw-create"}""");
        string? callId = null;
        while (true)
        {
            var envelope = await Read();
            if (envelope["type"]?.GetValue<string>() == "error") throw new IOException(envelope.ToJsonString());
            var inner = envelope["event"];
            if (inner?["type"]?.GetValue<string>() == "response.output_item.done" && inner["item"]?["type"]?.GetValue<string>() == "function_call")
                callId = inner["item"]!["call_id"]!.GetValue<string>();
            if (inner?["type"]?.GetValue<string>() == "response.completed") break;
        }
        if (callId == null) throw new IOException("Raw limit probe did not receive a function call.");
        var output = LargeResultFixture.Create(false);
        await Send(new JsonObject { ["type"] = "response.item.create", ["event_id"] = "raw-large-output",
            ["item"] = new JsonObject { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = output } }.ToJsonString());
        JsonObject failure;
        do { failure = await Read(); } while (failure["type"]?.GetValue<string>() != "error");
        if (failure["error"]?["code"]?.GetValue<string>() != "response_input_buffer_full")
            throw new IOException("Unexpected upstream rejection: " + failure.ToJsonString());
        await Send("""{"type":"session.close","event_id":"raw-close"}""");
        JsonObject terminal;
        do { terminal = await Read(); } while (terminal["type"]?.GetValue<string>() != "session.closed");
        Console.WriteLine(new JsonObject { ["case"] = "upstream-managed-tool-result-limit", ["characters"] = output.Length,
            ["utf8_bytes"] = Encoding.UTF8.GetByteCount(output), ["error"] = failure["error"]!.DeepClone(),
            ["final_usage"] = terminal["usage"]?.DeepClone() }.ToJsonString());
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", deadline.Token);
    }

    private static byte[] ReadPcm(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidOperationException("Expected WAV");
        reader.ReadInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidOperationException("Expected WAVE");
        byte[]? pcm = null;
        var valid = false;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var kind = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadInt32();
            var data = reader.ReadBytes(size);
            if (size % 2 != 0) reader.ReadByte();
            if (kind == "fmt " && size >= 16) valid = BitConverter.ToInt16(data, 0) == 1 && BitConverter.ToInt16(data, 2) == 1 && BitConverter.ToInt32(data, 4) == 24000 && BitConverter.ToInt16(data, 14) == 16;
            if (kind == "data") pcm = data;
        }
        if (!valid || pcm == null || pcm.Length % 2 != 0) throw new InvalidOperationException("Expected mono PCM16 WAV, 24000 Hz");
        return pcm;
    }
    private sealed class CountingTool(IVoiceTool inner) : IVoiceTool
    {
        private int _count;
        public int Count => _count;
        public string Name => inner.Name;
        public string Description => inner.Description;
        public Type ArgsType => inner.ArgsType;
        public Task<string> ExecuteAsync(string args) { Interlocked.Increment(ref _count); return inner.ExecuteAsync(args); }
    }
}
