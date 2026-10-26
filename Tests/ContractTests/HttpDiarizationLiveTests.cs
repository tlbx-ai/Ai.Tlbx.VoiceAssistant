using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

static class HttpDiarizationLiveTests
{
    public static async Task RunAsync(string directory)
    {
        var hedda = ReadPcm(Path.Combine(directory, "hedda.wav"));
        var david = ReadPcm(Path.Combine(directory, "david.wav"));
        await VerifyAsync("monologue", Enumerable.Repeat(hedda, 9).ToArray(), 1);
        await VerifyAsync("two voices with reversed order", [Join(hedda, david), Join(david, hedda), Join(hedda, david)], 2);
    }

    private static async Task VerifyAsync(string name, byte[][] sections, int expectedSpeakers)
    {
        var hardware = new HttpTranscriptionTests.Hardware();
        var options = new OpenAiHttpLiveTranscriptionOptions
        {
            TranscriptionModel = OpenAiTranscriptionModel.Gpt4oTranscribeDiarize,
            SnapshotInterval = TimeSpan.FromMilliseconds(700), MaxSectionDuration = TimeSpan.FromSeconds(30),
            RequestTimeout = TimeSpan.FromSeconds(60), StreamIdleTimeout = TimeSpan.FromSeconds(30),
            FinalizationTimeout = TimeSpan.FromSeconds(45), MaxRetries = 0
        };
        await using var transcriber = new OpenAiHttpLiveTranscriber(hardware, options);
        using var confirmed = new SemaphoreSlim(0);
        transcriber.OnStructuredTranscriptionReceived = t =>
        {
            if (!t.IsSessionFinal)
            {
                Console.WriteLine($"Live {name}, revision {t.SnapshotRevision}: " + string.Join(" | ", t.Segments.Where(s => s.Id?.StartsWith(t.SnapshotRevision + ":") == true)
                    .Select(s => $"native:{s.Speaker}; session:{s.SessionSpeakerId ?? "unresolved"}: {s.Text}")));
                confirmed.Release();
            }
        };
        transcriber.OnTranscriptionError = error => Console.WriteLine($"Live error: {error.Code}, status={error.HttpStatus}");
        var run = transcriber.TranscribeLive(_ => { });
        foreach (var audio in sections)
        {
            hardware.Emit(audio);
            await confirmed.WaitAsync(TimeSpan.FromSeconds(90));
        }
        await transcriber.StopAsync(); await run.WaitAsync(TimeSpan.FromSeconds(60));
        var segments = transcriber.LatestStructuredTranscript!.Segments;
        var speakers = segments.Select(s => s.SessionSpeakerId).Where(s => s != null).Distinct().ToArray();
        HttpDiarizationTests.Check(transcriber.LatestStatus!.IsComplete && speakers.Length == expectedSpeakers,
            $"live {name}: expected {expectedSpeakers} identities, found {speakers.Length}");
        HttpDiarizationTests.Check(segments.All(s => s.SessionSpeakerId is not null && s.Speaker is not null && s.Speaker == s.LocalSpeaker),
            $"live {name}: native labels and independent reference-backed identities are both retained");
        foreach (var marker in expectedSpeakers == 1 ? new[] { "Nordhausen" } : new[] { "Nordhausen", "Berlin" })
        {
            var identities = segments.Where(s => s.Text.Contains(marker, StringComparison.OrdinalIgnoreCase)).Select(s => s.SessionSpeakerId).Distinct().ToArray();
            HttpDiarizationTests.Check(identities.Length == 1 && identities[0] != null, $"live {name}: voice for {marker} remains stable");
        }
        Console.WriteLine($"Live diarization passed: {name}, {sections.Length} uploads, {speakers.Length} identities, {transcriber.LatestStatus.ConfirmedAudioEnd.TotalSeconds:F2}s confirmed.");
    }

    private static byte[] Join(byte[] first, byte[] second) => [.. first, .. second];
    private static byte[] ReadPcm(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path)); reader.ReadBytes(12);
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4)); int length = reader.ReadInt32();
            var data = reader.ReadBytes(length); if (id == "data") return data;
            if ((length & 1) != 0) reader.ReadByte();
        }
        throw new InvalidOperationException("WAV data is missing.");
    }
}
