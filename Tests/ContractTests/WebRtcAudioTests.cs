using System.Reflection;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Microsoft.JSInterop;

static class WebRtcAudioTests
{
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("WebRTC audio: " + message); }
    public static async Task RunAsync()
    {
        foreach (var target in new[] { 0d, 125d, 4000d }) new OpenAiWebRtcAudioOptions { JitterBufferTargetMilliseconds = target }.Validate();
        foreach (var target in new[] { -1d, 4001d, double.NaN, double.PositiveInfinity })
            Reject(() => new OpenAiWebRtcAudioOptions { JitterBufferTargetMilliseconds = target }.Validate());
        foreach (var interval in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(499), TimeSpan.FromSeconds(61) })
            Reject(() => new OpenAiWebRtcAudioOptions { StatisticsInterval = interval }.Validate());
        Reject(() => new OpenAiWebRtcAudioOptions { PeerConnection = new() { IceCandidatePoolSize = 256 } }.Validate());
        Reject(() => new OpenAiWebRtcAudioOptions { PeerConnection = new() { IceTransportPolicy = (OpenAiWebRtcIceTransportPolicy)99 } }.Validate());
        new OpenAiWebRtcAudioOptions { StatisticsInterval = TimeSpan.FromMilliseconds(500) }.Validate();
        new OpenAiWebRtcAudioOptions { StatisticsInterval = TimeSpan.FromSeconds(60) }.Validate();

        foreach (var live in new[] { false, true })
        {
            var js = new Browser();
            var snapshots = new List<OpenAiWebRtcDiagnosticSnapshot>();
            await using var provider = live
                ? (IAsyncDisposable)new OpenAiDirectLiveVoiceProvider(js, new(), new(), "contract-key")
                : new OpenAiDirectRealtimeVoiceProvider(js, new(), new(), "contract-key");
            var invalid = new OpenAiWebRtcAudioOptions { JitterBufferTargetMilliseconds = -1 };
            Set(provider, invalid, snapshots.Add);
            try { await Start(provider); throw new Exception("Invalid options accepted"); }
            catch (ArgumentOutOfRangeException) { Check(js.Calls == 0, "invalid configuration must not touch browser/session transport"); }
            var options = new OpenAiWebRtcAudioOptions
            {
                JitterBufferTargetMilliseconds = 125, StatisticsInterval = TimeSpan.FromSeconds(1),
                Microphone = new() { AutoGainControl = false, EchoCancellation = false, NoiseSuppression = false },
                PeerConnection = new() { IceCandidatePoolSize = 2, IceTransportPolicy = OpenAiWebRtcIceTransportPolicy.All }
            };
            Set(provider, options, snapshots.Add);
            await Start(provider);
            var first = js.Audio!;
            Check(first["jitterBufferTargetMilliseconds"]!.GetValue<double>() == 125 && first["statisticsIntervalMs"]!.GetValue<double>() == 1000, "units serialized unchanged");
            Check(first["microphone"]!["autoGainControl"]!.GetValue<bool>() == false, "native microphone preference");
            Check(first["peerConnection"]!["iceTransportPolicy"]!.GetValue<string>() == "all", "bounded peer configuration");
            var id = first["diagnosticSessionId"]!.GetValue<string>();
            var snapshot = new JsonObject
            {
                ["diagnosticSessionId"] = id, ["eventType"] = "Statistics",
                ["inboundAudio"] = new JsonArray(new JsonObject { ["packetsLost"] = -1, ["jitterSeconds"] = .02 }),
                ["audioPlayout"] = new JsonArray(new JsonObject { ["totalPlayoutDelaySeconds"] = .5 })
            };
            await Receive(provider, snapshot.ToJsonString());
            Check(snapshots.Count == 1 && snapshots[0].InboundAudio[0].PacketsLost == -1 && snapshots[0].InboundAudio[0].PacketsDiscarded == null, "AOT-safe typed counters and unsupported values");
            await Receive(provider, "{bad-json");
            Check(snapshots.Count == 1, "malformed diagnostics do not escape");
            Set(provider, options, _ => throw new InvalidOperationException("consumer failure"));
            await Receive(provider, snapshot.ToJsonString()); // Must not propagate into media.
            await Stop(provider);
            await Receive(provider, snapshot.ToJsonString());
            Set(provider, new OpenAiWebRtcAudioOptions(), snapshots.Add);
            await Start(provider);
            Check(js.Audio!["diagnosticSessionId"]!.GetValue<string>() != id, "fresh diagnostic generation on reconnect");
            Check(js.Audio["jitterBufferTargetMilliseconds"] == null && js.Audio["statisticsIntervalMs"] == null, "native defaults preserved");
            await Receive(provider, snapshot.ToJsonString());
            Check(snapshots.Count == 1, "old generation cannot report into reconnected session");
        }
        Console.WriteLine("WebRTC audio API: both providers, validation-before-start, native defaults, AOT JSON, callback failure isolation and reconnect generations passed.");
    }

    static void Reject(Action action) { try { action(); } catch (ArgumentOutOfRangeException) { return; } throw new Exception("Expected range rejection"); }
    static void Set(object provider, OpenAiWebRtcAudioOptions options, Action<OpenAiWebRtcDiagnosticSnapshot> callback)
    {
        if (provider is OpenAiDirectLiveVoiceProvider live) { live.BrowserAudioOptions = options; live.OnWebRtcDiagnostics = callback; }
        else { var realtime = (OpenAiDirectRealtimeVoiceProvider)provider; realtime.BrowserAudioOptions = options; realtime.OnWebRtcDiagnostics = callback; }
    }
    static Task Start(object provider) => provider is OpenAiDirectLiveVoiceProvider live
        ? live.StartBrowserSessionAsync(new OpenAiLiveSettings()) : ((OpenAiDirectRealtimeVoiceProvider)provider).StartBrowserSessionAsync(new OpenAiVoiceSettings());
    static Task Stop(object provider) => provider is OpenAiDirectLiveVoiceProvider live ? live.DisconnectAsync() : ((OpenAiDirectRealtimeVoiceProvider)provider).DisconnectAsync();
    static Task Receive(object provider, string json) => provider is OpenAiDirectLiveVoiceProvider live
        ? live.OnWebRtcDiagnosticsReceived(json) : ((OpenAiDirectRealtimeVoiceProvider)provider).OnWebRtcDiagnosticsReceived(json);

    sealed class Browser : IJSRuntime, IJSObjectReference
    {
        public int Calls;
        public JsonObject? Audio;
        #pragma warning disable BL0016 // In-process test double deliberately propagates assertion failures.
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => Run<T>(name, args);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken token, object?[]? args) => Run<T>(name, args);
        #pragma warning restore BL0016
        ValueTask<T> Run<T>(string name, object?[]? args)
        {
            Calls++;
            if (name == "createOpenAiDirectLiveClient")
                Audio = JsonNode.Parse((string)args![0]!)!["browserAudio"]!.AsObject();
            if (name == "start" && args is { Length: > 0 })
            {
                var value = (string)args[0]!.GetType().GetProperty("BrowserAudioJson", BindingFlags.Instance | BindingFlags.Public)!.GetValue(args[0])!;
                Audio = JsonNode.Parse(value)!.AsObject();
            }
            return ValueTask.FromResult(name == "import" || name.StartsWith("createOpenAiDirect", StringComparison.Ordinal) ? (T)(object)this : default!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
