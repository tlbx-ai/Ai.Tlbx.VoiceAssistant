using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

internal static class OpenAiRealtimeGatewayConfigurationTests
{
    public static async Task RunProviderAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var direct = Environment.GetEnvironmentVariable("REALTIME_SMOKE_DIRECT") == "1";
        var key = Environment.GetEnvironmentVariable(direct ? "OPENAI_API_KEY" : "LITELLM_API_KEY")!;
        await using var provider = new Ai.Tlbx.VoiceAssistant.Provider.OpenAi.OpenAiVoiceProvider(key, (level, message) => Console.WriteLine(message));
        provider.OnError = e => { errors.Enqueue(e); done.TrySetException(new InvalidOperationException(e)); };
        provider.OnResponseTraceCompleted = trace => { if (trace.ToolCalls.Count == 0) done.TrySetResult(); };
        await provider.ConnectAsync(new Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models.OpenAiVoiceSettings
        {
            ClientResponseControl = true, UseEphemeralKey = false,
            Connection = new Ai.Tlbx.VoiceAssistant.Models.ProviderEndpointOptions(BuildEndpoint(direct).AbsoluteUri) { ApiKey = key },
            Instructions = "Antworte kurz auf Deutsch. Nutze bei Zeitfragen das Werkzeug probe.",
            Tools = [new ProbeTool()]
        });
        using var reader = new BinaryReader(File.OpenRead(Environment.GetEnvironmentVariable("REALTIME_SMOKE_WAV")!));
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") throw new InvalidOperationException("Expected WAV");
        reader.ReadInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new InvalidOperationException("Expected WAVE");
        byte[]? pcm = null;
        var validFormat = false;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var kind = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadInt32();
            var data = reader.ReadBytes(size);
            if (size % 2 != 0) reader.ReadByte();
            if (kind == "fmt " && size >= 16)
                validFormat = BitConverter.ToInt16(data, 0) == 1 && BitConverter.ToInt16(data, 2) == 1
                    && BitConverter.ToInt32(data, 4) == 24000 && BitConverter.ToInt16(data, 14) == 16;
            if (kind == "data") pcm = data;
        }
        if (!validFormat || pcm == null) throw new InvalidOperationException("Expected mono PCM16 WAV at 24kHz");
        foreach (var chunk in pcm!.Concat(new byte[48000]).Chunk(960))
        {
            await provider.ProcessAudioAsync(Convert.ToBase64String(chunk));
            await Task.Delay(20, timeout.Token);
        }
        await done.Task.WaitAsync(timeout.Token);
        await provider.DisconnectAsync();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(";", errors));
    }

    private static Uri BuildEndpoint(bool direct)
    {
        var url = direct ? "https://api.openai.com" : Environment.GetEnvironmentVariable("LITELLM_BASE_URL")
            ?? throw new InvalidOperationException("LITELLM_BASE_URL missing");
        return new UriBuilder(url) { Scheme = "wss", Path = "/v1/realtime", Query = "model=gpt-realtime-2.1", Port = -1 }.Uri;
    }

    private sealed class ProbeTool : Ai.Tlbx.VoiceAssistant.Interfaces.IVoiceTool
    {
        public string Name => "probe";
        public string Description => "Liest die aktuelle Uhrzeit.";
        public Type ArgsType => typeof(object);
        public Task<string> ExecuteAsync(string args) => Task.FromResult(DateTimeOffset.Now.ToString("O"));
    }

    // Explicit live smoke entry point; uses only a synthetic session, no customer content.
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var socket = new ClientWebSocket();
        var key = Environment.GetEnvironmentVariable("LITELLM_API_KEY")
            ?? throw new InvalidOperationException("LITELLM_API_KEY missing");
        socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
        await socket.ConnectAsync(BuildEndpoint(false), timeout.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"session.update","session":{"type":"realtime","audio":{"input":{"turn_detection":{"type":"server_vad","create_response":false,"interrupt_response":false}}}}}"""), WebSocketMessageType.Text, true, timeout.Token);
        while (true)
        {
            using var stream = new MemoryStream();
            var buffer = new byte[65536];
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("Gateway closed before config acknowledgment");
                stream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var message = JsonNode.Parse(stream.ToArray())!;
            var type = message["type"]?.GetValue<string>();
            if (type == "error") throw new InvalidOperationException(message["error"]?.ToJsonString());
            if (type is "session.created" or "session.updated")
            {
                Console.WriteLine(type + ": " + message["session"]?["audio"]?["input"]?["turn_detection"]?.ToJsonString());
                if (type == "session.updated")
                {
                    var vad = message["session"]!["audio"]!["input"]!["turn_detection"]!;
                    if (vad["create_response"]!.GetValue<bool>() || vad["interrupt_response"]!.GetValue<bool>())
                        throw new InvalidOperationException("Gateway did not preserve explicit false response controls");
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "config checked", timeout.Token);
                    return;
                }
            }
        }
    }
}
