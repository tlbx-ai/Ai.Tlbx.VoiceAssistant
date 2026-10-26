using System.Net;
using System.Text;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

static class HttpDiarizationTests
{
    public static async Task RunAsync()
    {
        await VerifyNativeOutputAsync();
        await VerifyMonologueAsync();
        await VerifySwapsAndUnknownsAsync();
        await VerifyReferenceLimitsAsync();
    }

    private static async Task VerifyNativeOutputAsync()
    {
        foreach (bool fullReferences in new[] { false, true })
        foreach (string finalText in new[] { "Provider final text.", "", " \t " })
        {
            var hardware = new HttpTranscriptionTests.Hardware();
            var options = Options(); options.MaxSectionDuration = TimeSpan.FromSeconds(16);
            options.EnableAutomaticSpeakerReferences = false;
            if (fullReferences)
                for (int i = 0; i < 4; i++) options.KnownSpeakerReferences["known-" + i] = "data:audio/wav;base64,AAAA";
            using var client = new HttpClient(new HttpTranscriptionTests.Handler((_, _) =>
            {
                var events = Enumerable.Range(0, 8).Select(i => "data: " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "transcript.text.segment", id = "native-" + i, speaker = ((char)('A' + i)).ToString(),
                    start = i * 2, end = i * 2 + 2, text = "  native text " + i + "  ",
                    words = new[] { new { word = "exact", speaker = (string?)("word-" + i), start = i * 2, end = i * 2 + 1 },
                        new { word = "unspecified", speaker = (string?)null, start = i * 2 + 1, end = i * 2 + 2 } }
                }) + "\n\n");
                string body = string.Concat(events) + "data: " + System.Text.Json.JsonSerializer.Serialize(
                    new { type = "transcript.text.done", text = finalText }) + "\n\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }));
            await using var transcriber = new OpenAiHttpLiveTranscriber(hardware, options, "test", null, client);
            using var ready = new SemaphoreSlim(0);
            var progress = new List<TranscriptSegment>();
            transcriber.OnStructuredTranscriptionProgress = t => progress.AddRange(t.Segments);
            transcriber.OnStructuredTranscriptionReceived = t => { if (!t.IsSessionFinal) ready.Release(); };
            var run = transcriber.TranscribeLive(_ => { }); hardware.Emit(Audio(16));
            await ready.WaitAsync(TimeSpan.FromSeconds(3)); await transcriber.StopAsync(); await run;
            var transcript = transcriber.LatestStructuredTranscript!;
            Check(transcript.Segments.Select(s => s.Speaker).SequenceEqual(Enumerable.Range(0, 8).Select(i => ((char)('A' + i)).ToString())),
                "all eight native speakers must pass through, even without or beyond four references");
            Check(progress.Select(s => s.Speaker).SequenceEqual(transcript.Segments.Select(s => s.Speaker)), "native progress and completed labels agree");
            Check(transcript.Text == finalText, "provider final text, including empty or whitespace text, must not be replaced by wrapper formatting");
            for (int i = 0; i < 8; i++)
            {
                var segment = transcript.Segments[i];
                Check(segment.Text == "  native text " + i + "  ", "native segment text remains exact");
                Check(segment.Words[0].Speaker == "word-" + i && segment.Words[1].Speaker is null,
                    "word labels and absent word labels are not replaced by segment or wrapper labels");
                Check(segment.Words[1].SpeakerScope == TranscriptSpeakerScope.Unspecified && segment.Words[1].SpeakerScopeId is null,
                    "absent native word labels do not acquire a fabricated native identity scope");
                Check(transcript.ToSpeakerLabeledText().Contains("[" + (char)('A' + i) + "]  "), "normal rendering retains every native speaker");
            }
        }
        Console.WriteLine("HTTP wrapper: eight native speakers, exact word labels, missing labels, provider text and rendering preserved.");
    }

    private static async Task VerifySwapsAndUnknownsAsync()
    {
        var hardware = new HttpTranscriptionTests.Hardware();
        var options = Options(); options.MaxSectionDuration = TimeSpan.FromSeconds(6);
        int requestIndex = 0;
        using var client = new HttpClient(new HttpTranscriptionTests.Handler(async (request, token) =>
        {
            var names = await NamesAsync(request, token);
            var references = ((MultipartFormDataContent)request.Content!).Where(p => p.Headers.ContentDisposition?.Name?.Trim('"') == "known_speaker_references[]");
            foreach (var reference in references)
            {
                var url = await reference.ReadAsStringAsync(token);
                var wav = Convert.FromBase64String(url.Split(',')[1]);
                Check(wav.Length >= 2 * 48000 + 44 && wav.Length <= 5 * 48000 + 44, "reference clips are bounded and use valid provider durations");
            }
            requestIndex++;
            if (requestIndex == 1)
            {
                Check(names.Length == 0, "first request needs no user-provided references");
                return Segments(("A", 0, 3, "first person"), ("B", 3, 6, "second person"));
            }
            Check(names.Length == 2, "learned voice references accompany the next upload");
            // Provider identifies the reverse order by reference, not by local A/B order.
            if (requestIndex == 2) return Segments((names[1], 0, 3, "second person"), (names[0], 3, 6, "first person"));
            // Too short to enroll, and a local A does not inherit the first request's A.
            return Segments(("A", 0, 0.5, "uncertain voice"));
        }));
        await using var transcriber = new OpenAiHttpLiveTranscriber(hardware, options, "test", null, client);
        using var ready = new SemaphoreSlim(0);
        var progress = new List<TranscriptSegment>();
        transcriber.OnStructuredTranscriptionProgress = t => progress.AddRange(t.Segments);
        transcriber.OnStructuredTranscriptionReceived = t => { if (!t.IsSessionFinal) ready.Release(); };
        var run = transcriber.TranscribeLive(_ => { });
        for (int i = 0; i < 3; i++) { hardware.Emit(Audio(6)); await ready.WaitAsync(TimeSpan.FromSeconds(3)); }
        await transcriber.StopAsync(); await run;
        var segments = transcriber.LatestStructuredTranscript!.Segments;
        Check(segments[0].SessionSpeakerId == segments[3].SessionSpeakerId && segments[1].SessionSpeakerId == segments[2].SessionSpeakerId && segments[0].SessionSpeakerId != segments[1].SessionSpeakerId,
            "two identities survive reversed speaking order");
        Check(segments[4].Speaker == "A" && segments[4].SessionSpeakerId is null && segments[4].SpeakerScope == TranscriptSpeakerScope.Request,
            "an uncertain local A is not merged into an established person");
        Check(progress[0].Speaker == "A" && progress[0].SessionSpeakerId is null && progress[2].SessionSpeakerId == segments[2].SessionSpeakerId,
            "progress retains native labels before session identity enrollment");
        Check(segments[0].SessionSpeakerScopeId == segments[3].SessionSpeakerScopeId && segments[4].SessionSpeakerScopeId is null,
            "session and request identity scopes are explicit");
        Check(segments.All(s => s.Words.All(w => w.Speaker == s.Speaker && w.LocalSpeaker == s.LocalSpeaker &&
            w.SpeakerScope == s.SpeakerScope && w.SpeakerScopeId == s.SpeakerScopeId && w.SessionSpeakerId == s.SessionSpeakerId)),
            "segment and word identities use consistent scopes without replacing provider labels");
        Check(options.KnownSpeakerReferences.Count == 0, "automatic voice references never leak into caller settings or subsequent sessions");
        Console.WriteLine("HTTP diarization: two voices, reversed order, uncertain labels and reference bounds passed.");
    }

    private static async Task VerifyReferenceLimitsAsync()
    {
        foreach (var scenario in new[] { "limit", "overlap", "disabled", "silence" })
        {
            var hardware = new HttpTranscriptionTests.Hardware();
            var options = Options(); options.MaxSectionDuration = TimeSpan.FromSeconds(15);
            options.EnableAutomaticSpeakerReferences = scenario != "disabled";
            int requests = 0;
            using var client = new HttpClient(new HttpTranscriptionTests.Handler(async (request, token) =>
            {
                var names = await NamesAsync(request, token);
                if (++requests == 2) Check(names.Length == (scenario == "limit" ? 4 : 0), "reference count is bounded and unsafe clips are not enrolled");
                return scenario == "limit" ? Segments(("A", 0, 3, "one"), ("B", 3, 6, "two"), ("C", 6, 9, "three"), ("D", 9, 12, "four"), ("E", 12, 15, "five")) :
                    scenario == "overlap" ? Segments(("A", 0, 3, "one"), ("B", 2, 5, "overlap")) : Segments(("A", 0, 3, "one"));
            }));
            await using var transcriber = new OpenAiHttpLiveTranscriber(hardware, options, "test", null, client);
            using var ready = new SemaphoreSlim(0);
            transcriber.OnStructuredTranscriptionReceived = t => { if (!t.IsSessionFinal) ready.Release(); };
            var run = transcriber.TranscribeLive(_ => { });
            for (int i = 0; i < 2; i++)
            { hardware.Emit(scenario == "silence" ? new byte[15 * 48000] : Audio(15)); await ready.WaitAsync(TimeSpan.FromSeconds(3)); }
            await transcriber.StopAsync(); await run;
            var segments = transcriber.LatestStructuredTranscript!.Segments;
            var speakers = segments.Select(s => s.SessionSpeakerId).Where(s => s != null).Distinct().ToArray();
            Check(speakers.Length == (scenario == "limit" ? 4 : 0), "no invented people when enrollment is disabled, ambiguous or full");
            Check(segments.Select(s => s.Speaker).Distinct().Count() == (scenario == "limit" ? 5 : scenario == "overlap" ? 2 : 1),
                "reference limits never cap native speaker results");
        }
        Console.WriteLine("HTTP diarization: four-reference cap, overlapping speech, silent clips and opt-out passed.");
    }
    public static async Task VerifyMonologueAsync()
    {
        var hardware = new HttpTranscriptionTests.Hardware();
        using var client = new HttpClient(new HttpTranscriptionTests.Handler(async (request, token) =>
        {
            var names = await NamesAsync(request, token);
            return Segments((names.FirstOrDefault() ?? "A", 0, 3, "continuous speaker"));
        }));
        await using var transcriber = new OpenAiHttpLiveTranscriber(hardware, Options(), "test", null, client);
        using var confirmed = new SemaphoreSlim(0);
        transcriber.OnStructuredTranscriptionReceived = t => { if (!t.IsSessionFinal) confirmed.Release(); };
        var run = transcriber.TranscribeLive(_ => { });
        for (int i = 0; i < 9; i++)
        {
            hardware.Emit(Audio(3));
            await confirmed.WaitAsync(TimeSpan.FromSeconds(3));
        }
        await transcriber.StopAsync(); await run;
        var people = transcriber.LatestStructuredTranscript!.Segments.Select(s => s.SessionSpeakerId).Where(s => s != null).Distinct().ToArray();
        Check(people.Length == 1, $"one continuous voice must remain one person across nine uploads, got {people.Length}");
        Console.WriteLine("HTTP diarization monologue: one person across nine uploads passed.");
    }

    internal static OpenAiHttpLiveTranscriptionOptions Options() => new()
    {
        TranscriptionModel = OpenAiTranscriptionModel.Gpt4oTranscribeDiarize,
        MaxSectionDuration = TimeSpan.FromSeconds(3), SnapshotInterval = TimeSpan.FromMilliseconds(1),
        RequestTimeout = TimeSpan.FromSeconds(2), FinalizationTimeout = TimeSpan.FromSeconds(1)
    };
    internal static byte[] Audio(int seconds)
    {
        var bytes = new byte[48000 * seconds];
        for (int i = 0; i < bytes.Length; i += 2) { bytes[i] = 0xE8; bytes[i + 1] = 3; }
        return bytes;
    }
    internal static async Task<string[]> NamesAsync(HttpRequestMessage request, CancellationToken token) =>
        await Task.WhenAll(((MultipartFormDataContent)request.Content!).Where(p =>
            p.Headers.ContentDisposition?.Name?.Trim('"') == "known_speaker_names[]").Select(p => p.ReadAsStringAsync(token)));
    internal static HttpResponseMessage Segments(params (string speaker, double start, double end, string text)[] segments)
    {
        var body = new StringBuilder();
        for (int i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            body.Append("data: ").Append(System.Text.Json.JsonSerializer.Serialize(new
            { type = "transcript.text.segment", id = "segment-" + i, speaker = s.speaker, start = s.start, end = s.end, text = s.text,
                words = new[] { new { word = s.text, speaker = s.speaker, start = s.start, end = s.end } } })).Append("\n\n");
        }
        body.Append("data: {\"type\":\"transcript.text.done\",\"text\":\"complete\"}\n\n");
        return new(HttpStatusCode.OK) { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream") };
    }
    internal static void Check(bool condition, string message)
    { if (!condition) throw new Exception("Diarization regression: " + message); }
}
