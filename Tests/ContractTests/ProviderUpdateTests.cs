using System.Reflection;
using System.Text.Json;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Managers;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.Google;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Models;
using Ai.Tlbx.VoiceAssistant.Provider.XAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.XAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using System.Collections.Concurrent;

internal static class ProviderUpdateTests
{
    // Explicit paid smoke: the same natural SAPI request reaches each provider through its public audio API.
    public static async Task RunLiveAsync()
    {
        var path = Environment.GetEnvironmentVariable("PROVIDER_SMOKE_WAV") ?? throw new InvalidOperationException("PROVIDER_SMOKE_WAV (PCM16 mono/24kHz) is required.");
        var pcm24 = LargeResultIntegrationTests.ReadPcm(path);
        var pcm16 = new byte[(pcm24.Length / 2 * 2 / 3) * 2];
        for (var i = 0; i < pcm16.Length / 2; i++)
        {
            var source = (int)(i * 1.5) * 2;
            pcm16[i * 2] = pcm24[source]; pcm16[i * 2 + 1] = pcm24[source + 1];
        }
        var failures = new List<string>();
        var runs = new List<(string Model, IVoiceProvider Provider, IVoiceSettings Settings)> {
            ("gemini-3.8-live", new GoogleVoiceProvider(), new GoogleVoiceSettings { Model = GoogleModel.Gemini38Live }),
            ("gemini-3.8-live-extended-thinking", new GoogleVoiceProvider(), new GoogleVoiceSettings { Model = GoogleModel.Gemini38LiveExtendedThinking,
                Thinking = new() { Level = SessionThinkingLevel.Medium } }),
            ("gemini-2.5-flash-native-audio-latest", new GoogleVoiceProvider(), new GoogleVoiceSettings { Model = GoogleModel.Gemini25FlashNativeAudioLatest }),
            ("grok-voice-3.0", new XaiVoiceProvider(), new XaiVoiceSettings { Model = XaiVoiceModel.GrokVoice30 }),
            ("gpt-realtime-2.1", new OpenAiVoiceProvider(), new OpenAiVoiceSettings { Model = OpenAiRealtimeModel.GptRealtime21, UseEphemeralKey = false }),
            ("gpt-realtime-2.1-mini", new OpenAiVoiceProvider(), new OpenAiVoiceSettings { Model = OpenAiRealtimeModel.GptRealtime21Mini, UseEphemeralKey = false })
        };
        foreach (var (model, provider, settings) in runs)
        {
            var filter = Environment.GetEnvironmentVariable("PROVIDER_SMOKE_FILTER");
            if (filter != null && !model.StartsWith(filter, StringComparison.Ordinal)) { await provider.DisposeAsync(); continue; }
            var audio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var errors = new ConcurrentQueue<string>();
            var ledger = new UsageManager();
            long bytes = 0;
            settings.Instructions = "Antworte auf Deutsch mit genau den zwei Wörtern: Guten Tag.";
            provider.OnAudioReceived = data => { Interlocked.Add(ref bytes, Convert.FromBase64String(data).Length); audio.TrySetResult(); };
            provider.OnUsageReceived = ledger.AddReport;
            provider.OnError = error => { errors.Enqueue(error); audio.TrySetException(new InvalidOperationException(error)); };
            try
            {
                await provider.ConnectAsync(settings);
                var pcm = provider.RequiredInputSampleRate == AudioSampleRate.Rate16000 ? pcm16 : pcm24;
                var bytesPerSecond = provider.RequiredInputSampleRate == AudioSampleRate.Rate16000 ? 32000 : 48000;
                var chunk = bytesPerSecond / 50;
                for (var at = -bytesPerSecond / 2; at < pcm.Length + bytesPerSecond; at += chunk)
                {
                    var data = new byte[chunk];
                    if (at >= 0 && at < pcm.Length) Array.Copy(pcm, at, data, 0, Math.Min(chunk, pcm.Length - at));
                    await provider.ProcessAudioAsync(Convert.ToBase64String(data));
                    await Task.Delay(20);
                }
                await audio.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await Task.Delay(2000); // collect the short answer's terminal usage
                await provider.DisconnectAsync();
                Check(errors.IsEmpty, model + ": " + string.Join("; ", errors));
                var estimates = ledger.GetReports().Select(r => VoiceCostCalculator.Estimate(r, VoiceCostCalculator.GetRates(r))).ToArray();
                Console.WriteLine($"LIVE {model}: received_audio_bytes={bytes}; tokens={ledger.TotalTokens}; duration_s={ledger.TotalSessionDuration.TotalSeconds:F3}; known_model_cost_usd={estimates.Sum(e => e.KnownCostUsd):F6}; incomplete_cost={estimates.Any(e => !e.IsComplete)}");
                if (Environment.GetEnvironmentVariable("PROVIDER_SMOKE_USAGE_DETAIL") == "1")
                    foreach (var report in ledger.GetReports()) Console.WriteLine($"METER {model}: input={report.TotalInputTokens}; output={report.TotalOutputTokens}; reported_total={report.ReportedTotalTokens}; raw={report.RawProviderUsageJson}");
            }
            catch (Exception ex) { failures.Add(model + ": " + ex.Message); Console.WriteLine("LIVE FAILED " + model + ": " + ex.Message); }
            finally { await provider.DisposeAsync(); }
        }
        if (failures.Count > 0) throw new InvalidOperationException("Provider live acceptance failures: " + string.Join(" | ", failures));
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static async Task RunAsync()
    {
        Check((int)GoogleModel.Gemini31FlashLivePreview == 0 && (int)XaiVoiceModel.GrokVoiceLatest == 0, "Existing serialized enum values must remain stable");
        Check(new GoogleVoiceSettings().Model == GoogleModel.Gemini38Live, "Google defaults to stable 3.8");
        Check(GoogleModel.Gemini38LiveExtendedThinking.ToApiString() == "models/gemini-3.8-live-extended-thinking", "Extended Thinking model ID");
        Check(XaiVoiceModel.GrokVoice30.ToApiString() == "grok-voice-3.0", "Current Grok model ID");
        Check(!GoogleModel.Gemini38Live.SupportsThinkingLevel() && GoogleModel.Gemini38LiveExtendedThinking.SupportsThinkingLevel(), "Distinct 3.8 thinking capabilities");
        VerifyCosts();
        await VerifyAsyncGoogleToolsAsync();
        Console.WriteLine("Provider updates passed: model IDs, stable enums, background tools, interaction status, cumulative/duplicate billing and modality/cache prices.");
    }

    private static void VerifyCosts()
    {
        var ledger = new UsageManager();
        UsageReport Voice(double seconds, bool final = false) => new() { ProviderId = "openai", ModelId = "gpt-live-1", OperationId = "session", OperationType = UsageOperationType.VoiceSession,
            SessionDuration = TimeSpan.FromSeconds(seconds), IsCumulative = true, IsFinal = final };
        ledger.AddReport(Voice(15)); ledger.AddReport(Voice(30)); ledger.AddReport(Voice(60, true)); ledger.AddReport(Voice(90));
        Check(ledger.TotalSessionDuration == TimeSpan.FromMinutes(1), "Final duration replaces snapshots; late snapshots cannot inflate cost");
        var voice = VoiceCostCalculator.Estimate(ledger.GetReports().Single(), VoiceCostCalculator.GetRates(Voice(60)));
        Check(voice.IsComplete && voice.KnownCostUsd == .05m, "Live bills exact duration once");
        var backend = new UsageReport { ProviderId = "openai", ModelId = "gpt-6.1-sol", OperationId = "response", IsFinal = true, OperationType = UsageOperationType.DelegatedResponse,
            ReportedInputTokens = 1000, CacheReadInputTokens = 200, CacheCreationInputTokens = 300, ReportedOutputTokens = 100, ReasoningOutputTokens = 60 };
        ledger.AddReport(backend); ledger.AddReport(backend);
        Check(ledger.ReportCount == 2, "Repeated final backend usage is idempotent");
        var estimate = VoiceCostCalculator.Estimate(backend, VoiceCostCalculator.GetRates(backend));
        Check(estimate.IsComplete && estimate.KnownCostUsd == .00277m, "Charge 500 ordinary + 200 cached + 300 written tokens, reasoning already in 100 output");
        var multimodal = new UsageReport { OperationType = UsageOperationType.VoiceResponse, ReportedInputTokens = 1000, InputAudioTokens = 400, InputImageTokens = 100,
            InputTokens = 500, OutputTokens = 30,
            CacheReadInputTokens = 200, CachedTextInputTokens = 100, CachedAudioInputTokens = 80, CachedImageInputTokens = 20,
            ReportedOutputTokens = 200, OutputAudioTokens = 150, ReasoningOutputTokens = 20 };
        var mixed = VoiceCostCalculator.Estimate(multimodal, new() { TextInput = 4, CachedTextInput = .4m, AudioInput = 32, CachedAudioInput = .4m,
            ImageInput = 5, CachedImageInput = .5m, TextOutput = 24, AudioOutput = 64 });
        Check(mixed.IsComplete && mixed.KnownCostUsd == .023122m, "Each modality uses its cached and ordinary rate; output reasoning is a subset");
        using var googleMeter = JsonDocument.Parse("""{"promptTokenCount":2979,"responseTokenCount":34,"totalTokenCount":3013,"promptTokensDetails":[{"modality":"TEXT","tokenCount":2630},{"modality":"AUDIO","tokenCount":236}],"responseTokensDetails":[{"modality":"AUDIO","tokenCount":34}],"thoughtsTokenCount":50}""");
        var googleReport = GoogleLiveUsageMapper.CreateUsageReport(googleMeter.RootElement, "models/gemini-3.8-live-extended-thinking");
        var googleEstimate = VoiceCostCalculator.Estimate(googleReport, VoiceCostCalculator.GetRates(googleReport));
        Check(!googleEstimate.IsComplete && googleEstimate.KnownCostUsd == .0033135m,
            "Real Google meter: 113 unclassified input tokens stay unknown; 50 additional thought tokens use text output pricing");
        Check(!VoiceCostCalculator.Estimate(new UsageReport { InputAudioDuration = TimeSpan.FromSeconds(1) }, new() { AudioInput = 3 }).IsComplete, "Audio duration alone cannot prove token cost");
        Check(VoiceCostCalculator.GetRates(new() { ProviderId = "openai", ModelId = "gpt-live-1.1" }) == null, "No invented 1.1 price");
        Check(VoiceCostCalculator.GetRates(new() { ProviderId = "openai", ModelId = "gpt-6.1-sol", ServiceTier = "ultrafast" })?.TextInput == 12m, "Ultrafast uses its verified sixfold price");
        var longContext = new UsageReport { ProviderId = "openai", ModelId = "gpt-6.1-sol", ReportedInputTokens = 300000, ReportedOutputTokens = 1000 };
        Check(VoiceCostCalculator.Estimate(longContext, VoiceCostCalculator.GetRates(longContext)).KnownCostUsd == 1.215m, "Entire long-context request uses long-context input and output prices");
        Check(VoiceCostCalculator.GetRates(new() { ProviderId = "openai", ModelId = "gpt-6-luna", ServiceTier = "fast" })?.TextOutput == 1m, "Fast economy backend pricing");
        Check(VoiceCostCalculator.GetRates(new() { ProviderId = "openai", ModelId = "gpt-6.1-sol", ServiceTier = "unknown" }) == null, "Unknown tier stays unpriced");
        var vad = new UsageReport { ProviderId = "xai", ModelId = "grok-voice-3.0", OperationType = UsageOperationType.VoiceSession,
            BillingMode = VoiceBillingMode.SessionDuration, SessionDuration = TimeSpan.FromMinutes(1) };
        Check(VoiceCostCalculator.Estimate(vad, VoiceCostCalculator.GetRates(vad)).KnownCostUsd == .08m, "xAI VAD charges a minute including silence and tool wait");
        var vadDetails = new UsageReport { ProviderId = "xai", ModelId = "grok-voice-3.0", BillingMode = VoiceBillingMode.SessionDuration,
            InputAudioDuration = TimeSpan.FromSeconds(10), OutputAudioDuration = TimeSpan.FromSeconds(5), BillableTextInputEvents = 2 };
        Check(VoiceCostCalculator.Estimate(vadDetails, VoiceCostCalculator.GetRates(vadDetails)).KnownCostUsd == .008m, "xAI media diagnostics do not double-charge a VAD session");
        var ptt = new UsageReport { ProviderId = "xai", ModelId = "grok-voice-3.0", BillingMode = VoiceBillingMode.AudioDuration,
            InputAudioDuration = TimeSpan.FromSeconds(10), OutputAudioDuration = TimeSpan.FromSeconds(5), BillableTextInputEvents = 2 };
        Check(VoiceCostCalculator.Estimate(ptt, VoiceCostCalculator.GetRates(ptt)).KnownCostUsd == .028m, "xAI push-to-talk charges only audio plus text events");
    }

    private static async Task VerifyAsyncGoogleToolsAsync()
    {
        await using var provider = new GoogleVoiceProvider("test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new SlowTool(entered, release);
        typeof(GoogleVoiceProvider).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider,
            new GoogleVoiceSettings { Model = GoogleModel.Gemini38LiveExtendedThinking, Tools = [tool] });
        var process = typeof(GoogleVoiceProvider).GetMethod("ProcessReceivedMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task Receive(string json) => (Task)process.Invoke(provider, [json])!;
        await Receive("""{"toolCall":{"functionCalls":[{"id":"slow","name":"slow","args":{}}]}}""");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var audio = false;
        provider.OnAudioReceived = _ => audio = true;
        await Receive("""{"serverContent":{"turnComplete":true},"interactionStatus":"IN_PROGRESS"}""");
        Check(provider.InteractionStatus == "IN_PROGRESS", "A completed speech turn is not an idle agent");
        await Receive("""{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"mimeType":"audio/pcm;rate=24000","data":"AAA="}}]}}}""");
        Check(audio, "Receive loop continues audio while a tool waits");
        release.SetResult();
        await ((Task)typeof(GoogleVoiceProvider).GetField("_toolWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!).WaitAsync(TimeSpan.FromSeconds(3));
        await Receive("""{"toolCall":{"functionCalls":[{"id":"slow","name":"slow","args":{}}]}}""");
        await ((Task)typeof(GoogleVoiceProvider).GetField("_toolWork", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!).WaitAsync(TimeSpan.FromSeconds(3));
        Check(tool.Calls == 1, "Duplicate tool event cannot repeat an action");
        await Receive("""{"interactionStatus":"IDLE"}""");
        Check(provider.InteractionStatus == "IDLE", "Explicit idle forwarded");
    }

    private sealed class SlowTool(TaskCompletionSource entered, TaskCompletionSource release) : IVoiceTool
    {
        public int Calls;
        public string Name => "slow";
        public string Description => "A slow test operation";
        public Type ArgsType => typeof(NoArgs);
        public async Task<string> ExecuteAsync(string args) { Calls++; entered.TrySetResult(); await release.Task; return "done"; }
    }
    private sealed class NoArgs { }
}
