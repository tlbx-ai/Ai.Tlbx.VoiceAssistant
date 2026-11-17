using System.Collections.Concurrent;
using System.Reflection;
using Ai.Tlbx.VoiceAssistant.Hardware.Web;
using Microsoft.JSInterop;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.XAi;

static class WebPlaybackTests
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static WebAudioAccess Create(Module module)
    {
        var hardware = new WebAudioAccess(module);
        typeof(WebAudioAccess).GetField("_audioModule", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(hardware, module);
        typeof(WebAudioAccess).GetMethod("StartAudioProcessor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(hardware, null);
        return hardware;
    }

    public static async Task RunAsync()
    {
        var settingsModule = new Module();
        var settingsHardware = new WebAudioAccess(settingsModule);
        await using var settingsLifetime = (IAsyncDisposable)settingsHardware;
        Require(settingsHardware.PlaybackSmoothingEnabled, "smoothing defaults to enabled");
        Require(await settingsHardware.SetPlaybackSmoothingEnabledAsync(false), "setting before initialization");
        Require(!settingsHardware.PlaybackSmoothingEnabled && settingsModule.Smoothing.Count == 0, "pre-init setting stores preference without opening audio");
        await (Task)typeof(WebAudioAccess).GetMethod("LoadJavaScriptModule", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settingsHardware, null)!;
        Require(settingsModule.Smoothing.SequenceEqual(new[] { false }), "module loading applies stored preference");
        Require(await settingsHardware.SetPlaybackSmoothingEnabledAsync(true) && settingsHardware.PlaybackSmoothingEnabled, "live setting is applied");
        settingsModule.RejectSmoothing = true;
        Require(!await settingsHardware.SetPlaybackSmoothingEnabledAsync(false) && settingsHardware.PlaybackSmoothingEnabled, "failed interop does not change preference");
        await VerifyProviderOrderingAsync(new OpenAiVoiceProvider("contract-test"));
        await VerifyProviderOrderingAsync(new XaiVoiceProvider("contract-test"));
        var module = new Module { HoldFirstAudio = true };
        var hardware = Create(module);
        await using var lifetime = (IAsyncDisposable)hardware;
        Require(hardware.PlayAudio("first", 24000), "first accepted");
        await module.AudioStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(hardware.PlayAudio("second", 24000), "second accepted");
        var drain = hardware.WaitForPlaybackDrainAsync(TimeSpan.FromSeconds(3));
        Require(!drain.IsCompleted && module.DrainCalls == 0, "drain must include the in-flight JS submission");
        module.ReleaseAudio.TrySetResult(true);
        await module.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(module.Audio.Select(x => x.Audio).SequenceEqual(new[] { "first", "second" }), "FIFO delivery");
        Require(module.Audio.Select(x => x.Sequence).SequenceEqual(new long[] { 1, 2 }), "monotonic sequence");
        Require(!drain.IsCompleted, "JS submission completion is not playback completion");
        module.ReleaseDrain.TrySetResult(true);
        Require(await drain, "worklet acknowledgement completes drain");

        var concurrentModule = new Module();
        var concurrentHardware = Create(concurrentModule);
        await using var concurrentLifetime = (IAsyncDisposable)concurrentHardware;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
            Require(concurrentHardware.PlayAudio($"packet-{i}", 24000), "concurrent chunk accepted"))));
        concurrentModule.ReleaseDrain.TrySetResult(true);
        Require(await concurrentHardware.WaitForPlaybackDrainAsync(TimeSpan.FromSeconds(3)), "concurrent stream drains");
        Require(concurrentModule.Audio.Select(x => x.Sequence).SequenceEqual(Enumerable.Range(1, 100).Select(i => (long)i)),
            "concurrent producers keep sequence allocation and enqueue atomic");

        var clearModule = new Module { HoldFirstAudio = true };
        var interrupted = Create(clearModule);
        await using var interruptedLifetime = (IAsyncDisposable)interrupted;
        interrupted.PlayAudio("old-in-flight", 24000);
        await clearModule.AudioStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        interrupted.PlayAudio("old-queued", 24000);
        var oldDrain = interrupted.WaitForPlaybackDrainAsync(TimeSpan.FromSeconds(3));
        await interrupted.ClearAudioQueueAsync();
        Require(clearModule.ClearCalls == 1, "clear must bypass blocked submission");
        Require(!await oldDrain.WaitAsync(TimeSpan.FromSeconds(1)), "clear cancels drain even while an old submission remains blocked");
        interrupted.PlayAudio("new", 24000);
        clearModule.ReleaseAudio.TrySetResult(true);
        clearModule.ReleaseDrain.TrySetResult(true);
        Require(!await oldDrain, "old-generation drain must be cancelled");
        Require(await interrupted.WaitForPlaybackDrainAsync(TimeSpan.FromSeconds(3)), "new generation can drain");
        var delivered = clearModule.Audio.ToArray();
        Require(delivered.Select(x => x.Audio).SequenceEqual(new[] { "old-in-flight", "new" }), "old queued audio discarded");
        Require(delivered[1].Generation > delivered[0].Generation && delivered[1].Sequence == 1, "new generation restarts sequence");

        var timeoutModule = new Module();
        var timeoutHardware = Create(timeoutModule);
        await using var timeoutLifetime = (IAsyncDisposable)timeoutHardware;
        Require(!await timeoutHardware.WaitForPlaybackDrainAsync(TimeSpan.FromMilliseconds(30)), "missing acknowledgement times out");
        timeoutModule.ReleaseDrain.TrySetResult(true);

        var failedModule = new Module { RejectAudio = true };
        var failedHardware = Create(failedModule);
        await using var failedLifetime = (IAsyncDisposable)failedHardware;
        failedHardware.PlayAudio("bad", 24000);
        failedModule.ReleaseDrain.TrySetResult(true);
        Require(!await failedHardware.WaitForPlaybackDrainAsync(TimeSpan.FromSeconds(3)), "failed chunk cannot report successful playback");
        Console.WriteLine("Web playback: provider response isolation, concurrent FIFO, in-flight drain fence, interruption generations, clear bypass, timeout and failed delivery passed.");
    }

    static async Task VerifyProviderOrderingAsync(IVoiceProvider provider)
    {
        await using var lifetime = (IAsyncDisposable)provider;
        var audio = new List<string>();
        provider.OnAudioReceived = audio.Add;
        var process = provider.GetType().GetMethod("ProcessReceivedMessage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        async Task Deliver(string json) => await (Task)process.Invoke(provider, new object[] { json })!;
        await Deliver("""{"type":"response.created","response":{"id":"old"}}""");
        await Deliver("""{"type":"response.output_audio.delta","response_id":"old","delta":"AAAA"}""");
        Require(audio.Count == 1, $"{provider.GetType().Name}: current audio accepted");
        await provider.SendInterruptAsync();
        await Deliver("""{"type":"response.output_audio.delta","response_id":"old","delta":"AAAA"}""");
        Require(audio.Count == 1, "cancelled response audio discarded");
        await Deliver("""{"type":"response.done","response":{"id":"old","status":"cancelled"}}""");
        await Deliver("""{"type":"response.created","response":{"id":"new"}}""");
        await Deliver("""{"type":"response.output_audio.delta","response_id":"old","delta":"AAAA"}""");
        Require(audio.Count == 1, "old response cannot leak into new playback generation");
        await Deliver("""{"type":"response.done","response":{"id":"old","status":"cancelled"}}""");
        await Deliver("""{"type":"response.output_audio.delta","response_id":"new","delta":"AAAA"}""");
        Require(audio.Count == 2, "late old completion cannot cancel new response audio");
        await Deliver("""{"type":"response.done","response":{"id":"new","status":"completed"}}""");
        await Deliver("""{"type":"response.output_audio.delta","response_id":"new","delta":"AAAA"}""");
        Require(audio.Count == 2, "completed response audio discarded");
    }

    sealed class Module : IJSRuntime, IJSObjectReference
    {
        public bool HoldFirstAudio, RejectAudio, RejectSmoothing;
        public ConcurrentQueue<bool> Smoothing = new();
        public int DrainCalls, ClearCalls;
        public ConcurrentQueue<(string Audio, long Generation, long Sequence)> Audio = new();
        public TaskCompletionSource<bool> AudioStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseAudio = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> DrainStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseDrain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        #pragma warning disable BL0016 // In-process test double intentionally propagates errors to the test.
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => InvokeCoreAsync<T>(name, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string name, CancellationToken ct, object?[]? args) => InvokeCoreAsync<T>(name, ct, args);
        #pragma warning restore BL0016
        private async ValueTask<T> InvokeCoreAsync<T>(string name, CancellationToken ct, object?[]? args)
        {
            if (name == "import") return (T)(object)this;
            if (name == "setPlaybackSmoothingEnabled")
            {
                Smoothing.Enqueue((bool)args![0]!);
                return (T)(object)!RejectSmoothing;
            }
            if (name == "playAudio")
            {
                Audio.Enqueue(((string)args![0]!, (long)args[2]!, (long)args[3]!));
                AudioStarted.TrySetResult(true);
                if (HoldFirstAudio && Audio.Count == 1) await ReleaseAudio.Task.WaitAsync(ct);
                return (T)(object)!RejectAudio;
            }
            if (name == "waitForPlaybackDrain")
            {
                Interlocked.Increment(ref DrainCalls);
                DrainStarted.TrySetResult(true);
                return (T)(object)await ReleaseDrain.Task.WaitAsync(ct);
            }
            if (name == "stopAudioPlayback") Interlocked.Increment(ref ClearCalls);
            return default!;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
