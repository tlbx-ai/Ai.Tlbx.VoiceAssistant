using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Demo.Web.Services;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Protocol;

internal static class ConversationSupportTests
{
    public static async Task RunAsync()
    {
        var settings = new OpenAiVoiceSettings();
        ConversationSupport.Configure(settings);
        var registryType = typeof(OpenAiDirectRealtimeVoiceProvider).Assembly.GetType(
            "Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore.OpenAiDirectRealtimeSessionRegistry")!;
        var registry = Activator.CreateInstance(registryType, new OpenAiDirectRealtimeOptions())!;
        var voiceConfig = (SessionConfig)registryType.GetMethod("BuildSessionConfig")!.Invoke(registry, [new OpenAiVoiceSettings()])!;
        Check(voiceConfig.OutputModalities!.SequenceEqual(["audio"]) && voiceConfig.Audio!.Output != null,
            "Existing voice sessions retain audio output");
        Check(voiceConfig.Audio!.Input!.TurnDetection!.CreateResponse == true && voiceConfig.Audio.Input.TurnDetection.InterruptResponse == true,
            "Existing voice sessions retain server VAD response and interruption defaults");
        var config = (SessionConfig)registryType.GetMethod("BuildSessionConfig")!.Invoke(registry, [settings])!;
        Check(config.OutputModalities!.SequenceEqual(["text"]), "WebRTC text modality");
        Check(config.Audio!.Output == null, "WebRTC no output audio configuration");
        Check(config.Audio.Input!.TurnDetection!.CreateResponse == false && config.Audio.Input.TurnDetection.InterruptResponse == false,
            "WebRTC application owns response scheduling");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var toolRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var textReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deltas = new StringBuilder();
        var audioCount = 0;
        var interruptions = 0;
        var errors = new List<string>();
        var tool = new DelayedWeatherTool(toolStarted, toolRelease);
        var server = Task.Run(async () =>
        {
            try
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                async Task<JsonNode> Read()
                {
                    var buffer = new byte[65536];
                    using var stream = new MemoryStream();
                    WebSocketReceiveResult result;
                    do { result = await socket.ReceiveAsync(buffer, timeout.Token); stream.Write(buffer, 0, result.Count); }
                    while (!result.EndOfMessage);
                    return JsonNode.Parse(stream.ToArray())!;
                }
                async Task Send(string json) => await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, timeout.Token);
                async Task<JsonNode> Expect(string type)
                {
                    var item = await Read();
                    Check(item["type"]!.GetValue<string>() == type, $"Expected {type}, got {item}");
                    return item;
                }
                var session = (await Read())["session"]!;
                Check(session["output_modalities"]![0]!.GetValue<string>() == "text", "WebSocket text modality");
                Check(session["max_output_tokens"]!.GetValue<int>() == 500, "Numeric token cap, not a JSON string");
                Check(session["audio"]!["output"] == null, "WebSocket no output audio config");
                await Send("""{"type":"session.updated","session":{}}""");
                await Send("""{"type":"input_audio_buffer.committed","item_id":"u1"}""");
                await Expect("response.create");
                await Send("""{"type":"response.created","response":{"id":"r1"}}""");
                // Conversation continues while the model chooses a tool.
                await Send("""{"type":"input_audio_buffer.speech_started"}""");
                await Send("""{"type":"response.done","response":{"id":"r1","status":"completed","output":[{"type":"function_call","name":"get_weather","call_id":"c1","arguments":"{}"}]}}""");
                await toolStarted.Task.WaitAsync(timeout.Token);
                // Multiple new audio turns while the tool runs must coalesce, never cancel it.
                await Send("""{"type":"input_audio_buffer.committed","item_id":"u2"}""");
                await Send("""{"type":"input_audio_buffer.committed","item_id":"u3"}""");
                toolRelease.TrySetResult();
                var result = await Expect("conversation.item.create");
                Check(result["item"]!["output"]!.GetValue<string>() == "Demo-Wetter: 17 Grad", "Tool result retained across speech");
                await Expect("response.create");
                await Send("""{"type":"response.created","response":{"id":"r2"}}""");
                await Send("""{"type":"response.output_text.delta","delta":"Demo-Wetter: "}""");
                await Send("""{"type":"input_audio_buffer.speech_started"}""");
                await Send("""{"type":"response.output_text.delta","delta":"17 Grad"}""");
                await Send("""{"type":"response.output_audio.delta","delta":"AAAA"}""");
                await Send("""{"type":"response.output_text.done","text":"Demo-Wetter: 17 Grad"}""");
                await Send("""{"type":"response.done","response":{"id":"r2","status":"completed","output":[]}}""");
                await textReceived.Task.WaitAsync(timeout.Token);
                finished.TrySetResult();
                var closing = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[1024]), timeout.Token);
                Check(closing.MessageType == WebSocketMessageType.Close, "No duplicate response or cancellation");
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }
            catch (Exception ex) { finished.TrySetException(ex); throw; }
        }, timeout.Token);
        await using var provider = new OpenAiVoiceProvider("test");
        provider.OnTextDelta = delta => deltas.Append(delta);
        provider.OnMessageReceived = m => { if (m.Role == ChatMessage.AssistantRole) textReceived.TrySetResult(); };
        provider.OnAudioReceived = _ => audioCount++;
        provider.OnInterruptDetected = () => interruptions++;
        provider.OnError = errors.Add;
        settings.UseEphemeralKey = false;
        settings.Connection = new ProviderEndpointOptions($"ws://localhost:{port}/");
        settings.Tools = [tool];
        await provider.ConnectAsync(settings);
        await finished.Task.WaitAsync(timeout.Token);
        await provider.DisconnectAsync();
        await server;
        Check(deltas.ToString() == "Demo-Wetter: 17 Grad", "Text stream forwarded");
        Check(audioCount == 0 && interruptions == 0 && tool.Calls == 1, "Silent, non-interrupting, one tool execution");
        Check(errors.Count == 0, string.Join("; ", errors));
        Console.WriteLine("Conversation support: text-only config, continued speech during tools/text, coalescing, no audio passed.");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class DelayedWeatherTool(TaskCompletionSource started, TaskCompletionSource release) : IVoiceTool
    {
        public int Calls;
        public string Name => "get_weather";
        public string Description => "Demo weather";
        public Type ArgsType => typeof(object);
        public async Task<string> ExecuteAsync(string argumentsJson)
        {
            Calls++;
            started.TrySetResult();
            await release.Task;
            return "Demo-Wetter: 17 Grad";
        }
    }
}
