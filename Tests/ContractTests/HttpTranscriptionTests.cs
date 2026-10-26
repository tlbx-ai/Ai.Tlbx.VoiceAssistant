using System.Net;
using System.Text;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

static class HttpTranscriptionTests
{
    public static async Task RunLiveAsync(string wavPath)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("OPENAI_API_KEY is unavailable.");
        using var reader = new BinaryReader(File.OpenRead(wavPath));
        reader.ReadBytes(12);
        byte[]? pcm = null;
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            string name = Encoding.ASCII.GetString(reader.ReadBytes(4));
            int count = reader.ReadInt32();
            var bytes = reader.ReadBytes(count);
            if (name == "fmt ") Check(BitConverter.ToInt32(bytes, 4) == 24000 && BitConverter.ToInt16(bytes, 14) == 16, "synthetic fixture is PCM16 24kHz");
            if (name == "data") pcm = bytes;
        }
        Check(pcm is { Length: > 0 }, "synthetic speech fixture exists");
        var hardware = new Hardware();
        var options = Options(); options.RequestTimeout = TimeSpan.FromSeconds(45); options.StreamIdleTimeout = TimeSpan.FromSeconds(20);
        options.FinalizationTimeout = TimeSpan.FromSeconds(45); options.Language = "en";
        await using var transcriber = new OpenAiHttpLiveTranscriber(hardware, options, key);
        using var confirmed = new SemaphoreSlim(0);
        transcriber.OnStructuredTranscriptionReceived = transcript => { if (!transcript.IsSessionFinal) confirmed.Release(); };
        transcriber.OnTranscriptionError = error => Console.WriteLine($"Live HTTP: {error.Code}, status={error.HttpStatus}, retry={error.WillRetry}");
        var run = transcriber.TranscribeLive(_ => { }); hardware.Emit(pcm!);
        await confirmed.WaitAsync(TimeSpan.FromSeconds(60));
        Check(transcriber.LatestText.Contains("transcript", StringComparison.OrdinalIgnoreCase), "actual provider recognizes synthetic speech");
        hardware.Emit(pcm!);
        await transcriber.StopAsync();
        Check(hardware.Stopped, "actual provider does not delay microphone stop");
        await run.WaitAsync(TimeSpan.FromSeconds(60));
        Check(transcriber.LatestStatus!.IsComplete && transcriber.LatestStructuredTranscript!.Segments.Count == 2, "actual provider confirms both sections");
        Console.WriteLine($"Live HTTP passed: {transcriber.Options.GetModelId()}, two synthetic speech sections, confirmed {transcriber.LatestStatus.ConfirmedAudioEnd.TotalSeconds:F3}s, complete finalization.");
    }

    public static async Task RunAsync()
    {
        await VerifyImmediateStopAsync();
        await VerifyFourHoursAsync();
        await VerifyFailuresAsync();
        await VerifyBlockedStreamAsync();
        await VerifyDeadlinesAndStartupAsync();
        await VerifySpeakersAndTailAsync();
        await VerifyOverflowAndHardwareAsync();
        Console.WriteLine("HTTP transcription: four simulated hours, bounded uploads/RAM, stop, tail, retries, SSE deadlines, speaker scopes, reuse and hardware contracts passed.");
    }

    private static OpenAiHttpLiveTranscriptionOptions Options() => new()
    {
        SnapshotInterval = TimeSpan.FromMilliseconds(1), MinimumUtteranceDuration = TimeSpan.FromMilliseconds(1),
        LeadingTrimDuration = TimeSpan.Zero, RetryDelay = TimeSpan.FromMilliseconds(1),
        RequestTimeout = TimeSpan.FromSeconds(2), StreamIdleTimeout = TimeSpan.FromMilliseconds(80),
        FinalizationTimeout = TimeSpan.FromMilliseconds(150)
    };

    private static void Check(bool value, string message)
    { if (!value) throw new Exception("HTTP transcription contract: " + message); }

    private static async Task WaitUntil(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(1, timeout.Token);
    }

    private static async Task VerifyFourHoursAsync()
    {
        const int sections = 480, sectionBytes = 30 * 48000;
        var hardware = new Hardware();
        int requests = 0, maxUpload = 0, maxBuffered = 0;
        long uploaded = 0;
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            var form = (MultipartFormDataContent)request.Content!;
            var audio = await form.First(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "file").ReadAsByteArrayAsync(token);
            Check(audio.Length == sectionBytes + 44, "every upload is exactly one 30-second section");
            int id = BitConverter.ToInt16(audio, 44);
            Check(id == 1000 + requests, "no reordered, duplicate or skipped PCM sections");
            requests++;
            uploaded += audio.Length - 44;
            maxUpload = Math.Max(maxUpload, audio.Length);
            return Done("section-" + requests);
        }));
        await using var transcriber = new OpenAiHttpLiveTranscriber(hardware, Options(), "test", null, client);
        using var confirmed = new SemaphoreSlim(0);
        transcriber.OnStructuredTranscriptionReceived = transcript => { if (!transcript.IsSessionFinal) confirmed.Release(); };
        var run = transcriber.TranscribeLive(_ => { });
        long before = GC.GetTotalMemory(true);
        for (int index = 0; index < sections; index++)
        {
            var data = new byte[sectionBytes];
            var sample = BitConverter.GetBytes((short)(1000 + index));
            for (int pos = 0; pos < data.Length; pos += 2) { data[pos] = sample[0]; data[pos + 1] = sample[1]; }
            hardware.Emit(data);
            maxBuffered = Math.Max(maxBuffered, transcriber.BufferedAudioBytes);
            await confirmed.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await transcriber.StopAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Check(requests == sections && uploaded == 4L * 3600 * 48000, "four hours uploaded once including stop");
        Check(transcriber.LatestStatus is { IsComplete: true, ConfirmedAudioEnd.TotalHours: 4 }, "complete four-hour coverage");
        Check(transcriber.LatestStructuredTranscript?.Segments.Count == sections, "all section texts retained once");
        long retained = GC.GetTotalMemory(true) - before;
        Check(retained < 64_000_000 && maxBuffered <= 120 * 48000, "retained RAM and pending PCM bounded");
        Console.WriteLine($"HTTP 4h: {requests} requests, {uploaded} PCM bytes, max upload {maxUpload}, max pending {maxBuffered}, retained RAM delta {retained}.");
    }

    private static async Task VerifyFailuresAsync()
    {
        foreach (int status in new[] { 429, 503, 401, 400, 413, 0 })
        {
            var hardware = new Hardware();
            var errors = new List<HttpTranscriptionError>();
            int attempts = 0;
            using var client = new HttpClient(new Handler((_, _) =>
            {
                attempts++;
                if (status == 0 && attempts == 1) throw new HttpRequestException("network down");
                if (attempts > 1 || status == 0) return Task.FromResult(Done("recovered"));
                var response = new HttpResponseMessage((HttpStatusCode)status);
                response.Headers.Add("x-request-id", "request-contract");
                response.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(1));
                return Task.FromResult(response);
            }));
            await using var transcriber = new OpenAiHttpLiveTranscriber(hardware, Options(), "test", null, client);
            transcriber.OnTranscriptionError = error => errors.Add(error);
            var run = transcriber.TranscribeLive(_ => { });
            hardware.Emit(new byte[4800]);
            if (status is 400 or 401 or 413)
            {
                await run.WaitAsync(TimeSpan.FromSeconds(3));
                Check(attempts == 1 && errors.Single().IsFatal && transcriber.LatestStatus?.State == HttpTranscriptionState.Failed,
                    $"fatal {status} is never retried");
                Check(transcriber.LatestStatus!.MissingSections.Count == 1, "fatal failure retains missing coverage");
            }
            else
            {
                await WaitUntil(() => transcriber.LatestStatus?.ConfirmedAudioEnd > TimeSpan.Zero);
                await transcriber.StopAsync(); await run;
                Check(attempts == 2 && errors.Single().WillRetry && transcriber.LatestStatus!.IsComplete, "transient failure recovered");
            }
            if (status != 0) Check(errors[0].HttpStatus == status && errors[0].ProviderRequestId == "request-contract", "structured HTTP metadata");
        }
        foreach (var body in new[] { "", "data: {\"type\":\"transcript.text.delta\",\"delta\":\"unconfirmed\"}\n\n", "data: broken\n\n" })
        {
            var hardware = new Hardware();
            var options = Options(); options.MaxRetries = 0;
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
            await using var transcriber = new OpenAiHttpLiveTranscriber(hardware, options, "test", null, client);
            var run = transcriber.TranscribeLive(_ => { }); hardware.Emit(new byte[4800]);
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Check(transcriber.LatestStatus is { State: HttpTranscriptionState.Partial, IsComplete: false }, "empty/truncated SSE cannot confirm coverage");
        }
    }

    private static async Task VerifyBlockedStreamAsync()
    {
        var hardware = new Hardware();
        var options = Options(); options.MaxRetries = 0;
        int requests = 0;
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(++requests == 1 ? Done("confirmed") :
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockedStream()) })));
        await using var transcriber = new OpenAiHttpLiveTranscriber(hardware, options, "test", null, client);
        var run = transcriber.TranscribeLive(_ => { }); hardware.Emit(new byte[4800]);
        await WaitUntil(() => transcriber.LatestStatus?.Text == "confirmed");
        hardware.Emit(new byte[4800]);
        await WaitUntil(() => requests >= 2);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await transcriber.StopAsync();
        Check(hardware.Stopped && transcriber.LatestText == "confirmed", "blocked SSE never delays stop or confirmed text");
        await run.WaitAsync(TimeSpan.FromSeconds(1));
        Check(clock.Elapsed < TimeSpan.FromSeconds(1) && transcriber.LatestStatus is { State: HttpTranscriptionState.Partial, Text: "confirmed" }, "bounded partial finalization");
        Check(transcriber.LatestStatus!.ConfirmedAudioEnd == TimeSpan.FromMilliseconds(100) &&
            transcriber.LatestStatus.RecordedAudioEnd == TimeSpan.FromMilliseconds(200), "explicit unconfirmed tail coverage");
    }

    private static async Task VerifySpeakersAndTailAsync()
    {
        var hardware = new Hardware { Tail = new byte[480] };
        var options = Options(); options.TranscriptionModel = OpenAiTranscriptionModel.Gpt4oTranscribeDiarize;
        options.KnownSpeakerReferences["Known"] = "data:audio/wav;base64,AAAA";
        int requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            string segment = "data: {\"type\":\"transcript.text.segment\",\"id\":\"same-id\",\"speaker\":\"A\",\"text\":\"hello\",\"start\":0,\"end\":0.005}\n\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(segment + segment + "data: {\"type\":\"transcript.text.segment\",\"id\":\"known\",\"speaker\":\"Known\",\"text\":\"yes\",\"start\":0.005,\"end\":0.01}\n\ndata: {\"type\":\"transcript.text.done\",\"text\":\"hello yes\"}\n\n") });
        }));
        await using var transcriber = new OpenAiHttpLiveTranscriber(hardware, options, "test", null, client);
        var run = transcriber.TranscribeLive(_ => { }); hardware.Emit(new byte[4800]);
        await WaitUntil(() => transcriber.LatestStatus?.ConfirmedAudioEnd > TimeSpan.Zero);
        await transcriber.StopAsync(); await run;
        var segments = transcriber.LatestStructuredTranscript!.Segments;
        Check(requests == 2 && segments.Count == 4 && segments.Select(s => s.Id).Distinct().Count() == 4, "tail acknowledged once; repeated segments deduplicated within requests");
        Check(segments[0].Speaker == "section-1/A" && segments[2].Speaker == "section-2/A" && segments[3].Speaker == "Known", "anonymous speakers scoped and known labels stable");
        Check(segments[2].Start == TimeSpan.FromMilliseconds(100) && transcriber.LatestStatus!.IsComplete, "absolute segment times include section offset");
        var oldCallback = hardware.CaptureCallback();
        hardware.Tail = null;
        var second = transcriber.TranscribeLive(_ => { });
        oldCallback(new byte[4800]);
        await transcriber.StopAsync(); await second;
        Check(transcriber.LatestStatus is { RecordedAudioEnd.Ticks: 0 }, "callbacks from old sessions cannot enter a new recording");
    }

    private static async Task VerifyDeadlinesAndStartupAsync()
    {
        foreach (bool stopDuringRequest in new[] { false, true })
        {
            var hardware = new Hardware();
            var options = Options(); options.MaxRetries = 0;
            options.RequestTimeout = TimeSpan.FromMilliseconds(60);
            options.FinalizationTimeout = TimeSpan.FromMilliseconds(30);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new HttpClient(new Handler(async (_, token) =>
            {
                entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Done("never");
            }));
            await using var transcriber = new OpenAiHttpLiveTranscriber(hardware, options, "test", null, client);
            var errors = new List<HttpTranscriptionError>(); transcriber.OnTranscriptionError = errors.Add;
            var run = transcriber.TranscribeLive(_ => { }); hardware.Emit(new byte[4800]);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (stopDuringRequest) await transcriber.StopAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
            Check(transcriber.LatestStatus is { State: HttpTranscriptionState.Partial, IsComplete: false }, "request/finalization deadlines retain partial status");
            Check(errors.Any(e => e.Code == (stopDuringRequest ? "finalization_cancelled" : "request_timeout")), "deadline is exposed structurally");
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedHardware = new Hardware { InitGate = gate.Task };
        using var okClient = new HttpClient(new Handler((_, _) => Task.FromResult(Done("ok"))));
        await using var delayed = new OpenAiHttpLiveTranscriber(delayedHardware, Options(), "test", null, okClient);
        var delayedRun = delayed.TranscribeLive(_ => { });
        var stop = delayed.StopAsync();
        gate.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(1)); await delayedRun;
        Check(delayedHardware.Stopped, "Stop racing initialization still closes the opened microphone");

        var badHardware = new Hardware { StopSuccess = false };
        await using var bad = new OpenAiHttpLiveTranscriber(badHardware, Options(), "test", null, okClient);
        bad.OnStructuredTranscriptionReceived = _ => throw new Exception("callback failure");
        var badRun = bad.TranscribeLive(_ => throw new Exception("callback failure"));
        await bad.StopAsync(); await badRun;
        Check(bad.LatestStatus is { State: HttpTranscriptionState.Partial, CaptureStopConfirmed: false }, "unacknowledged stop never claims complete capture");
    }

    private static async Task VerifyOverflowAndHardwareAsync()
    {
        var hardware = new Hardware();
        var options = Options(); options.MaxBufferedDuration = TimeSpan.FromSeconds(30);
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Done("ok"))));
        await using var transcriber = new OpenAiHttpLiveTranscriber(hardware, options, "test", null, client);
        var run = transcriber.TranscribeLive(_ => { });
        hardware.Emit(new byte[31 * 48000]);
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Check(transcriber.LatestStatus is { State: HttpTranscriptionState.Failed, IsComplete: false } && hardware.Stopped, "buffer overflow stops capture explicitly");
        var second = transcriber.TranscribeLive(_ => { }); hardware.Fail(); await second;
        Check(transcriber.LatestStatus is { State: HttpTranscriptionState.Partial, CaptureStopConfirmed: false }, "hardware loss cannot claim complete capture");
    }

    private sealed class BlockedStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.Infinite, token); return 0; }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long l) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
    public static async Task VerifyImmediateStopAsync()
    {
        var hardware = new Hardware();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Done("unused");
        }));
        await using var transcriber = new OpenAiHttpLiveTranscriber(hardware,
            new OpenAiHttpLiveTranscriptionOptions { SnapshotInterval = TimeSpan.FromMilliseconds(1), LeadingTrimDuration = TimeSpan.Zero },
            "test", null, client);
        var run = transcriber.TranscribeLive(_ => { });
        hardware.Emit(new byte[48000]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await transcriber.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
        if (!hardware.Stopped) throw new Exception("Stop must stop the microphone while HTTP is blocked.");
        // Dispose must cancel finalization and join the session, even with a blocked provider.
        await transcriber.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await run;
    }

    internal static HttpResponseMessage Done(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: {\"type\":\"transcript.text.done\",\"text\":\"" + text + "\"}\n\n", Encoding.UTF8, "text/event-stream")
    };

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    internal sealed class Hardware : IAudioHardwareAccess
    {
        public event EventHandler<string>? AudioError;
        private MicrophoneAudioReceivedEventHandler? _receive;
        public bool Stopped { get; private set; }
        public bool StopSuccess { get; set; } = true;
        public Task InitGate { get; set; } = Task.CompletedTask;
        public byte[]? Tail { get; set; }
        public void Emit(byte[] bytes) => _receive?.Invoke(this, new(Convert.ToBase64String(bytes)));
        public Action<byte[]> CaptureCallback()
        { var receive = _receive; return bytes => receive?.Invoke(this, new(Convert.ToBase64String(bytes))); }
        public void Fail() => AudioError?.Invoke(this, "device lost");
        public Task InitAudioAsync() => InitGate;
        public Task<bool> StartRecordingAudio(MicrophoneAudioReceivedEventHandler receive, AudioSampleRate rate = AudioSampleRate.Rate24000)
        { _receive = receive; Stopped = false; return Task.FromResult(true); }
        public Task<bool> StopRecordingAudio()
        { if (!Stopped && Tail != null) Emit(Tail); Stopped = true; return Task.FromResult(StopSuccess); }
        public bool PlayAudio(string audio, int rate) => true;
        public Task ClearAudioQueueAsync() => Task.CompletedTask;
        public Task<List<AudioDeviceInfo>> GetAvailableMicrophonesAsync() => Task.FromResult(new List<AudioDeviceInfo>());
        public Task<List<AudioDeviceInfo>> RequestMicrophonePermissionAndGetDevicesAsync() => GetAvailableMicrophonesAsync();
        public Task<bool> SetMicrophoneDeviceAsync(string id) => Task.FromResult(true);
        public Task<string?> GetCurrentMicrophoneDeviceAsync() => Task.FromResult<string?>(null);
        public Task<bool> SetDiagnosticLevelAsync(DiagnosticLevel level) => Task.FromResult(true);
        public Task<DiagnosticLevel> GetDiagnosticLevelAsync() => Task.FromResult(DiagnosticLevel.None);
        public void SetLogAction(Action<LogLevel, string> action) { }
        public Task<bool> WaitForPlaybackDrainAsync(TimeSpan? timeout = null) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
