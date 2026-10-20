using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.Inference;
using Ai.Tlbx.Inference.Configuration;
using Ai.Tlbx.VoiceAssistant.Demo.Web.Services;
using Ai.Tlbx.VoiceAssistant.Managers;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.BuiltInTools;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Provider.Mistral;
using VoiceMessage = Ai.Tlbx.VoiceAssistant.Models.ChatMessage;

internal static class MistralTests
{
    private static void Check(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    private static byte[] Fixture => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mono-22050.mp3"));
    private static byte[] Speech(int milliseconds = 300)
    {
        var pcm = new byte[milliseconds * 32];
        for (var i = 0; i < pcm.Length / 2; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(6000 * Math.Sin(i * Math.PI / 20)));
        return pcm;
    }

    public static async Task RunAsync()
    {
        var decoded = MistralVoiceProvider.DecodeMp3(Fixture);
        Check(decoded.Length > 200000 && decoded.Length % 2 == 0, "Long MP3 must resample without overflowing the source sample index.");
        var wav = MistralVoiceProvider.ToWav(new byte[32000]);
        Check(Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)) == 16000, "WAV header/sample rate.");
        var requested = new List<string>();
        using var http = new HttpClient(new Handler(async (message, ct) => {
            Check(message.Headers.Authorization?.Parameter == "test-key", "Request-scoped authorization.");
            using var json = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(ct));
            requested.Add(json.RootElement.GetProperty("model").GetString()!);
            if (message.RequestUri!.AbsolutePath.EndsWith("transcriptions")) {
                Check(json.RootElement.GetProperty("language").GetString() == "de", "German transcription.");
                var input = Convert.FromBase64String(json.RootElement.GetProperty("input_audio").GetProperty("data").GetString()!);
                Check(Encoding.ASCII.GetString(input, 0, 4) == "RIFF", "PCM must be wrapped in WAV.");
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"Hallo Johannes\"}") };
            }
            Check(json.RootElement.GetProperty("voice").GetString() == "voice_481", "Fiona voice.");
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Fixture) };
        }));
        await using var provider = new MistralVoiceProvider(new(http, "test-key"));
        var messages = new ConcurrentQueue<VoiceMessage>(); var usage = new ConcurrentQueue<UsageReport>();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = 0;
        provider.OnMessageReceived = messages.Enqueue; provider.OnUsageReceived = usage.Enqueue;
        provider.OnTranscriptionCompleted = text => messages.Enqueue(VoiceMessage.CreateUserMessage(text));
        provider.OnAudioReceived = b => played += Convert.FromBase64String(b).Length;
        provider.WaitForPlaybackDrainAsync = _ => Task.FromResult(true);
        provider.OnError = error => complete.TrySetException(new InvalidOperationException(error));
        provider.OnStatusChanged = status => { if (status == "Listening...") complete.TrySetResult(); };
        await provider.ConnectAsync(new MistralVoiceSettings { RespondAsync = (history, ct) => {
            Check(history.Last().Content == "Hallo Johannes", "Backend gets transcript.");
            return Task.FromResult("Guten Tag."); } });
        await provider.ProcessAudioAsync(Convert.ToBase64String(new byte[32000]));
        Check(requested.Count == 0, "Silence must not create billable requests.");
        await provider.ProcessAudioAsync(Convert.ToBase64String(Speech()));
        await provider.ProcessAudioAsync(Convert.ToBase64String(new byte[24000]));
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(messages.Count == 2 && played == decoded.Length, "User and assistant messages and decoded playback.");
        Check(usage.Any(u => u.InputCharacters == 10), "TTS bills characters, not tokens.");
        Check(requested.SequenceEqual(new[] { "mistralai/voxtral-mini-transcribe-3-0", "mistralai/voxtral-mini-tts-3-0" }), "Pinned OpenRouter IDs.");
        await provider.DisconnectAsync();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slowHttp = new HttpClient(new Handler(async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return new(HttpStatusCode.OK); }));
        await using var canceled = new MistralVoiceProvider(new(slowHttp, "key"));
        var emitted = 0; canceled.OnAudioReceived = _ => emitted++;
        await canceled.ConnectAsync(new MistralVoiceSettings { TranscriptionOnly = true });
        await canceled.ProcessAudioAsync(Convert.ToBase64String(Speech()));
        await canceled.ProcessAudioAsync(Convert.ToBase64String(new byte[24000]));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await canceled.SendInterruptAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(canceled.IsConnected && emitted == 0, "Interruption cancels the turn and retains session.");
        await canceled.DisconnectAsync();
        Check(!canceled.IsConnected, "Disconnect cancels cleanly.");

        var estimate = VoiceCostCalculator.Estimate(new() { ProviderId = "openrouter", ModelId = "voxtral-mini-tts-3-0", InputCharacters = 1000 },
            VoiceCostCalculator.GetRates(new() { ProviderId = "openrouter", ModelId = "voxtral-mini-tts-3-0" }));
        Check(estimate.IsComplete && estimate.KnownCostUsd == .013m, "Voxtral character cost.");
        Console.WriteLine("PASS Mistral: routing, WAV, 22050→24000 MP3 decode, VAD, full pipeline, interruption, cost.");
    }

    public static async Task RunLiveAsync()
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? throw new InvalidOperationException("Set OPENROUTER_API_KEY.");
        var directory = Environment.GetEnvironmentVariable("MISTRAL_ARTIFACT_DIR") ?? throw new InvalidOperationException("Set MISTRAL_ARTIFACT_DIR.");
        Directory.CreateDirectory(directory);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var audio = new MistralAudioClient(http, key);
        var settings = new MistralVoiceSettings();
        var source = await audio.SpeakAsync("Wie spät ist es gerade in Deutschland?", settings);
        await File.WriteAllBytesAsync(Path.Combine(directory, "question.mp3"), source);
        var source24 = MistralVoiceProvider.DecodeMp3(source);
        var source16 = new byte[(source24.Length / 2 * 2 / 3) * 2];
        for (var i = 0; i < source16.Length / 2; i++) Array.Copy(source24, (int)(i * 1.5) * 2, source16, i * 2, 2);
        var backend = new MistralDemoBackend(new AiInferenceClient(http, new AiInferenceOptions().AddOpenAi(
            Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new InvalidOperationException("Set OPENAI_API_KEY."))));
        await using var provider = new MistralVoiceProvider(audio);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new ConcurrentQueue<VoiceMessage>(); var reports = new ConcurrentQueue<UsageReport>();
        using var playback = new MemoryStream();
        provider.OnMessageReceived = m => { messages.Enqueue(m); Console.WriteLine($"{m.Role}: {m.Content}"); };
        provider.OnTranscriptionCompleted = text => { messages.Enqueue(VoiceMessage.CreateUserMessage(text)); Console.WriteLine("user: " + text); };
        provider.OnUsageReceived = reports.Enqueue;
        provider.OnAudioReceived = b => playback.Write(Convert.FromBase64String(b));
        provider.OnError = e => complete.TrySetException(new InvalidOperationException(e));
        provider.OnStatusChanged = s => { Console.WriteLine(s); if (s == "Listening...") complete.TrySetResult(); };
        provider.WaitForPlaybackDrainAsync = _ => Task.FromResult(true);
        settings.RespondAsync = (history, ct) => backend.RespondAsync(history, "Du bist ein KI-Sprachassistent. Antworte kurz und präzise auf Deutsch.",
            new IVoiceTool[] { new TimeTool() }, provider.ReportBackendMessage, provider.ReportBackendUsage, ct);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await provider.ConnectAsync(settings);
        await provider.ProcessAudioAsync(Convert.ToBase64String(source16));
        await provider.ProcessAudioAsync(Convert.ToBase64String(new byte[24000]));
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(90));
        Check(messages.Any(m => m.Role == "tool") && messages.Any(m => m.Role == "assistant" && m.ToolCallId == null), "Natural prompt must invoke time tool and answer.");
        Check(playback.Length > 4800, "Actual synthesized PCM playback is required.");
        await File.WriteAllBytesAsync(Path.Combine(directory, "answer.pcm"), playback.ToArray());
        Console.WriteLine($"PASS live Voxtral pipeline: {timer.ElapsedMilliseconds}ms, {playback.Length} PCM bytes, {reports.Count} usage reports.");
        Console.WriteLine("Known list-price estimate USD: " + reports.Sum(u => VoiceCostCalculator.Estimate(u, VoiceCostCalculator.GetRates(u)).KnownCostUsd));
        await provider.DisconnectAsync();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
