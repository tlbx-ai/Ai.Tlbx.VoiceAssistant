using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Threading.Channels;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Managers;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.Google;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Models;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Protocol;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Extensions;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Protocol;
using Ai.Tlbx.VoiceAssistant.Provider.XAi;
using Ai.Tlbx.VoiceAssistant.Provider.XAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.XAi.Protocol;

Assert(Enum.GetValues<AssistantVoice>().Length == 10, "OpenAI voice roster");
Assert(Enum.GetValues<GoogleVoice>().Length == 30, "Gemini voice roster");
Assert(Enum.GetValues<XaiVoice>().Length == 28, "xAI voice roster");
Assert(new OpenAiVoiceSettings().Model == OpenAiRealtimeModel.GptRealtime21, "OpenAI default model");
Assert(new OpenAiVoiceSettings().Voice == AssistantVoice.Marin, "OpenAI default voice");
Assert(new OpenAiVoiceSettings().ReasoningEffort == SessionReasoningEffort.Low, "OpenAI low-latency reasoning default");
Assert(new OpenAiVoiceSettings().TurnDetection.SilenceDurationMs == 200, "OpenAI low-latency VAD default");
Assert(new OpenAiVoiceSettings().Eagerness == Eagerness.high, "OpenAI low-latency semantic VAD default");
Assert(ServiceCollectionExtensions.CreateDefaultOpenAiSettings().TurnDetection.SilenceDurationMs == 200, "OpenAI DI VAD default matches settings default");
VerifyVadThresholdConvenienceApi();
Assert(OpenAiRealtimeModel.GptRealtime21.ToApiString() == "gpt-realtime-2.1", "OpenAI 2.1 model id");
Assert(OpenAiRealtimeModel.GptRealtime21Mini.ToApiString() == "gpt-realtime-2.1-mini", "OpenAI 2.1 mini model id");
Assert(new OpenAiTranscriptionSettings().TranscriptionModel == OpenAiTranscriptionModel.GptLiveTranscribe, "OpenAI live transcription default");
Assert(new OpenAiHttpLiveTranscriptionOptions().TranscriptionModel == OpenAiTranscriptionModel.GptTranscribe, "OpenAI file transcription default");
Assert(OpenAiTranscriptionModel.GptLiveTranscribe.ToApiString() == "gpt-live-transcribe", "OpenAI live transcription model id");
Assert(OpenAiTranscriptionModel.GptTranscribe.ToApiString() == "gpt-transcribe", "OpenAI high-accuracy transcription model id");
Assert(new XaiVoiceSettings().Model == XaiVoiceModel.GrokVoiceLatest, "xAI default model");
Assert(XaiVoiceModel.GrokVoiceLatest.ToApiString() == "grok-voice-latest", "xAI latest model id");
Assert(XaiVoiceModel.GrokVoiceThinkFast20.ToApiString() == "grok-voice-think-fast-2.0", "xAI Think Fast 2.0 model id");
Assert(XaiVoiceModelExtensions.TryParseApiString("grok-voice-think-fast-2.0", out var xaiThinkFast20) &&
    xaiThinkFast20 == XaiVoiceModel.GrokVoiceThinkFast20, "xAI Think Fast 2.0 model parsing");

VerifyOpenAiTranscriptionContextContract();
await VerifyOpenAiHttpStructuredSnapshotMonotonicityAsync();
VerifyGoogleInitialHistoryContract();
VerifyXaiStructuredTranscriptionContract();

await VerifyOpenAiPreambleOutputPolicyAsync();
VerifyOpenAiPreambleInstructionPolicy();
VerifyOpenAiTurnDetectionPolicy();
VerifyOpenAiReasoningPolicy();
await VerifyOpenAiMiniEphemeralSessionAsync();
VerifyLowLatencyPlaybackPolicy();
await VerifyOpenAiAudioBackpressurePolicyAsync();
await VerifyPreConnectAudioBackpressurePolicyAsync();
await VerifyFailedStartCleanupAsync();

if (args.Contains("--live-provider-smoke", StringComparer.Ordinal))
{
    await VerifyLiveProviderConnectionsAsync();
}

if (args.Contains("--live-xai-voice-smoke", StringComparer.Ordinal))
{
    await VerifyLiveXaiVoice20ConnectionAsync();
}

static void VerifyOpenAiTranscriptionContextContract()
{
    var config = new TranscriptionConfig
    {
        Model = OpenAiTranscriptionModel.GptLiveTranscribe.ToApiString(),
        Prompt = "A German construction meeting",
        Keywords = ["TLBX", "VOB"],
        Languages = ["de", "en"],
        Delay = OpenAiTranscriptionDelay.Low.ToApiString()
    };
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(config, OpenAiJsonContext.Default.TranscriptionConfig));
    Assert(json.RootElement.GetProperty("model").GetString() == "gpt-live-transcribe", "OpenAI live transcription request model");
    Assert(json.RootElement.GetProperty("keywords").GetArrayLength() == 2, "OpenAI transcription keyword hints");
    Assert(json.RootElement.GetProperty("languages").GetArrayLength() == 2, "OpenAI transcription language hints");
    Assert(json.RootElement.GetProperty("delay").GetString() == "low", "OpenAI transcription delay control");

    var parser = typeof(OpenAiHttpLiveTranscriber).GetMethod("TryParseDiarizedSegment", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(OpenAiHttpLiveTranscriber).FullName, "TryParseDiarizedSegment");
    using var segmentJson = JsonDocument.Parse(
        """{"type":"transcript.text.segment","segment":{"speaker":"Johannes","text":"Guten Morgen","start":1.25,"end":2.5}}""");
    var segment = parser.Invoke(null, [segmentJson.RootElement]) as TranscriptSegment
        ?? throw new InvalidOperationException("OpenAI diarized segment parser returned no result.");
    Assert(segment.Speaker == "Johannes" && segment.Text == "Guten Morgen", "OpenAI known speaker label preservation");
    Assert(segment.Start == TimeSpan.FromSeconds(1.25) && segment.End == TimeSpan.FromSeconds(2.5), "OpenAI diarized timing preservation");
}

static async Task VerifyOpenAiHttpStructuredSnapshotMonotonicityAsync()
{
    await using var transcriber = new OpenAiHttpLiveTranscriber(
        new ContractAudioHardware(),
        new OpenAiHttpLiveTranscriptionOptions
        {
            TranscriptionModel = OpenAiTranscriptionModel.Gpt4oTranscribeDiarize,
            LeadingTrimDuration = TimeSpan.Zero
        },
        "contract-test-key");

    var authoritative = new List<StructuredTranscript>();
    var progress = new List<StructuredTranscript>();
    var rawText = new List<string>();
    transcriber.OnStructuredTranscriptionReceived = authoritative.Add;
    transcriber.OnStructuredTranscriptionProgress = progress.Add;

    var snapshotType = typeof(OpenAiHttpLiveTranscriber).GetNestedType(
        "SnapshotWorkItem",
        BindingFlags.NonPublic)
        ?? throw new TypeLoadException("OpenAI HTTP snapshot work item was not found.");
    var publishCompleted = typeof(OpenAiHttpLiveTranscriber).GetMethod(
        "PublishCompletedSnapshot",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(OpenAiHttpLiveTranscriber).FullName, "PublishCompletedSnapshot");
    var publishProgress = typeof(OpenAiHttpLiveTranscriber).GetMethod(
        "PublishStructuredProgress",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(OpenAiHttpLiveTranscriber).FullName, "PublishStructuredProgress");
    var applyEvent = typeof(OpenAiHttpLiveTranscriber).GetMethod(
        "ApplyStreamingEvent",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(OpenAiHttpLiveTranscriber).FullName, "ApplyStreamingEvent");

    object Snapshot(long revision, bool isFinal, double audioEndSeconds, string displayFloor) =>
        Activator.CreateInstance(
            snapshotType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                0,
                revision,
                Array.Empty<byte>(),
                isFinal,
                displayFloor,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(audioEndSeconds)
            ],
            culture: null)
        ?? throw new InvalidOperationException("OpenAI HTTP snapshot work item could not be created.");

    static TranscriptSegment Segment(string id, string speaker, string text, double start, double end) => new()
    {
        Id = id,
        Speaker = speaker,
        Text = text,
        Start = TimeSpan.FromSeconds(start),
        End = TimeSpan.FromSeconds(end)
    };

    var completeSegments = new[]
    {
        Segment("turn-1", "A", "Turn 1", 0, 1),
        Segment("turn-2", "B", "Turn 2", 1, 2),
        Segment("turn-3", "A", "Turn 3", 2, 3),
        Segment("turn-4", "B", "Turn 4", 3, 4)
    };
    var completeText = string.Join(Environment.NewLine, completeSegments.Select(segment => segment.Text));
    publishCompleted.Invoke(transcriber,
        [Snapshot(1, false, 4, string.Empty), completeSegments, completeText, "de", (Action<string>)rawText.Add]);

    Assert(authoritative.Count == 1 && authoritative[0].Segments.Count == 4,
        "OpenAI publishes a complete diarized snapshot as the authoritative state");
    Assert(authoritative[0].SnapshotRevision == 1 && authoritative[0].IsSnapshotComplete && !authoritative[0].IsSessionFinal,
        "OpenAI complete snapshot metadata distinguishes request and session finality");
    Assert(rawText.SequenceEqual([completeText]),
        "OpenAI raw and structured callbacks publish the same accepted snapshot text");

    publishProgress.Invoke(transcriber, [Snapshot(2, false, 5, completeText), completeSegments[0]]);
    publishProgress.Invoke(transcriber, [Snapshot(2, false, 5, completeText), completeSegments[1]]);
    Assert(authoritative.Count == 1 && progress.Count == 2,
        "OpenAI replay segments do not replace the authoritative structured state");
    Assert(progress.All(update => update.SnapshotRevision == 2 && !update.IsSnapshotComplete && update.Segments.Count == 1),
        "OpenAI segment progress is explicitly revision-scoped and incomplete");

    var truncatedSegments = completeSegments.Take(2).ToArray();
    publishCompleted.Invoke(transcriber,
        [Snapshot(2, false, 5, completeText), truncatedSegments, "Turn 1\nTurn 2", "de", (Action<string>)rawText.Add]);
    Assert(authoritative.Count == 1 && rawText.Count == 1 && transcriber.LatestStructuredTranscript?.Segments.Count == 4,
        "OpenAI rejects a completed snapshot whose segment coverage regresses");

    publishCompleted.Invoke(transcriber,
        [Snapshot(3, true, 6, completeText), truncatedSegments, "Turn 1\nTurn 2", "de", (Action<string>)rawText.Add]);
    Assert(authoritative.Count == 2 && authoritative[^1].IsSessionFinal,
        "OpenAI finalizes the last confirmed snapshot when the final request regresses");
    Assert(authoritative[^1].Segments.Count == 4 && authoritative[^1].Text == completeText && rawText.Count == 1,
        "OpenAI final fallback preserves the last UI text and all confirmed speaker turns");
    Assert(ReferenceEquals(transcriber.LatestStructuredTranscript, authoritative[^1]),
        "OpenAI exposes the same final structured state that was last delivered to the UI");

    publishCompleted.Invoke(transcriber,
        [Snapshot(4, true, 7, completeText), Array.Empty<TranscriptSegment>(), "Short fallback", "de", (Action<string>)rawText.Add]);
    Assert(authoritative.Count == 2 && transcriber.LatestStructuredTranscript?.Segments.Count == 4 && rawText.Count == 1,
        "OpenAI text-only fallback cannot erase a confirmed diarized state");

    var duplicateSegments = new List<TranscriptSegment>();
    var duplicateKeys = new HashSet<string>(StringComparer.Ordinal);
    var duplicateHypothesis = new StringBuilder();
    const string duplicateEvent =
        "{\"type\":\"transcript.text.segment\",\"segment\":{\"id\":\"stable-turn\",\"speaker\":\"A\",\"text\":\"Einmal\",\"start\":0,\"end\":1}}";
    var duplicateSnapshot = Snapshot(4, false, 7, completeText);
    applyEvent.Invoke(transcriber,
        [duplicateSnapshot, "transcript.text.segment", duplicateEvent, duplicateHypothesis, duplicateSegments, duplicateKeys, (Action<string>)rawText.Add]);
    applyEvent.Invoke(transcriber,
        [duplicateSnapshot, "transcript.text.segment", duplicateEvent, duplicateHypothesis, duplicateSegments, duplicateKeys, (Action<string>)rawText.Add]);
    Assert(duplicateSegments.Count == 1 && duplicateSegments[0].Id == "stable-turn",
        "OpenAI preserves segment identity and deduplicates replayed events within a snapshot");

    transcriber.OnStructuredTranscriptionProgress = _ => throw new InvalidOperationException("expected callback failure");
    publishProgress.Invoke(transcriber, [Snapshot(5, false, 8, completeText), completeSegments[0]]);
    transcriber.OnStructuredTranscriptionReceived = _ => throw new InvalidOperationException("expected callback failure");
    publishCompleted.Invoke(transcriber,
        [Snapshot(5, false, 9, completeText), new[] { Segment("turn-5", "A", "Turn 1 bis 5", 0, 9) }, "Turn 1 bis 5", "de", (Action<string>)(_ => throw new InvalidOperationException("expected callback failure"))]);

    await using var fallbackTranscriber = new OpenAiHttpLiveTranscriber(
        new ContractAudioHardware(),
        new OpenAiHttpLiveTranscriptionOptions { TranscriptionModel = OpenAiTranscriptionModel.Gpt4oTranscribeDiarize },
        "contract-test-key");
    var fallbackStructured = new List<StructuredTranscript>();
    var fallbackRaw = new List<string>();
    fallbackTranscriber.OnStructuredTranscriptionReceived = fallbackStructured.Add;
    publishCompleted.Invoke(fallbackTranscriber,
        [Snapshot(1, true, 2, string.Empty), Array.Empty<TranscriptSegment>(), "Fallback text", "de", (Action<string>)fallbackRaw.Add]);
    Assert(fallbackStructured.Count == 1 && fallbackStructured[0].Segments.Count == 1 && fallbackStructured[0].Text == "Fallback text",
        "OpenAI retains non-empty text when a completed request contains no speaker segments");
    Assert(fallbackRaw.SequenceEqual(["Fallback text"]),
        "OpenAI text-only fallback keeps raw and structured callback state synchronized");
}

static void VerifyGoogleInitialHistoryContract()
{
    var setup = new SetupMessage
    {
        Setup = new Setup
        {
            Model = GoogleModel.Gemini31FlashLivePreview.ToApiString(),
            HistoryConfig = new HistoryConfig { InitialHistoryInClientContent = true }
        }
    };
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(setup, GoogleJsonContext.Default.SetupMessage));
    Assert(
        json.RootElement.GetProperty("setup").GetProperty("historyConfig").GetProperty("initialHistoryInClientContent").GetBoolean(),
        "Gemini 3.1 initial history setup contract");
}

static async Task VerifyLiveProviderConnectionsAsync()
{
    await using (var openAi = new OpenAiTranscriptionProvider())
    {
        await openAi.ConnectAsync(new OpenAiTranscriptionSettings
        {
            TranscriptionModel = OpenAiTranscriptionModel.GptLiveTranscribe,
            Languages = ["de", "en"],
            Keywords = ["TLBX"]
        });
        Assert(openAi.IsConnected, "OpenAI live transcription API connection");
        await openAi.DisconnectAsync();
    }
    Console.WriteLine("OpenAI live transcription connection passed.");

    await using (var xai = new XaiTranscriptionProvider())
    {
        await xai.ConnectAsync(new XaiTranscriptionSettings
        {
            Language = "de",
            Diarize = true,
            SmartTurnThreshold = 0.7
        });
        Assert(xai.IsConnected, "xAI streaming transcription API connection");
        await xai.DisconnectAsync();
    }
    Console.WriteLine("xAI streaming transcription connection passed.");

    await VerifyLiveXaiVoice20ConnectionAsync();

    await using (var google = new GoogleVoiceProvider(Environment.GetEnvironmentVariable("GEMINI_API_KEY")))
    {
        await google.ConnectAsync(new GoogleVoiceSettings
        {
            Model = GoogleModel.Gemini31FlashLivePreview,
            Instructions = "Connection verification only."
        });
        Assert(google.IsConnected, "Gemini 3.1 Live API connection with history config");
        await google.DisconnectAsync();
    }
    Console.WriteLine("Gemini 3.1 Live API connection passed.");

    Console.WriteLine("Live provider connection smoke tests passed.");
}

static async Task VerifyLiveXaiVoice20ConnectionAsync()
{
    await using var xai = new XaiVoiceProvider();
    await xai.ConnectAsync(new XaiVoiceSettings
    {
        Model = XaiVoiceModel.GrokVoiceThinkFast20,
        Instructions = "Connection verification only.",
        ReasoningEffort = SessionReasoningEffort.High,
        EnableInputAudioTranscription = false,
        EnableSessionResumption = false
    });
    Assert(xai.IsConnected, "xAI Grok Voice Think Fast 2.0 connection");
    await xai.DisconnectAsync();
    Console.WriteLine("xAI Grok Voice Think Fast 2.0 connection passed.");
}

static void VerifyXaiStructuredTranscriptionContract()
{
    var endpointBuilder = typeof(XaiTranscriptionProvider).GetMethod("BuildEndpoint", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(XaiTranscriptionProvider).FullName, "BuildEndpoint");
    var endpoint = endpointBuilder.Invoke(null,
        [new XaiTranscriptionSettings { Language = "de", Keyterms = ["TLBX Voice"], Diarize = true }]) as Uri
        ?? throw new InvalidOperationException("xAI transcription endpoint builder returned no URI.");
    Assert(endpoint.Query.Contains("diarize=true", StringComparison.Ordinal), "xAI streaming diarization query");
    Assert(endpoint.Query.Contains("smart_turn=0.7", StringComparison.Ordinal), "xAI Smart Turn query");
    Assert(endpoint.Query.Contains("keyterm=TLBX%20Voice", StringComparison.Ordinal), "xAI keyterm query encoding");

    var parser = typeof(XaiTranscriptionProvider).GetMethod("ParseTranscript", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(XaiTranscriptionProvider).FullName, "ParseTranscript");
    using var eventJson = JsonDocument.Parse(
        """
        {
          "type":"transcript.partial",
          "text":"Hallo Johannes Guten Morgen",
          "is_final":true,
          "speech_final":true,
          "start":0.0,
          "duration":2.0,
          "end_of_turn_confidence":0.97,
          "words":[
            {"text":"Hallo","start":0.0,"end":0.4,"speaker":0},
            {"text":"Johannes","start":0.4,"end":0.9,"speaker":0},
            {"text":"Guten","start":1.1,"end":1.5,"speaker":1},
            {"text":"Morgen","start":1.5,"end":2.0,"speaker":1}
          ]
        }
        """);
    var transcript = parser.Invoke(null, [eventJson.RootElement, false]) as StructuredTranscript
        ?? throw new InvalidOperationException("xAI transcription parser returned no result.");
    Assert(transcript.IsFinal && transcript.IsSpeechFinal, "xAI final turn flags");
    Assert(transcript.Segments.Count == 2, "xAI words grouped into speaker segments");
    Assert(transcript.Segments[0].Speaker == "speaker-0" && transcript.Segments[1].Speaker == "speaker-1", "xAI speaker assignment");
    Assert(transcript.Segments[0].Start == TimeSpan.Zero && transcript.Segments[1].End == TimeSpan.FromSeconds(2), "xAI word timing preservation");
    Assert(transcript.ToSpeakerLabeledText().Contains("[speaker-1] Guten Morgen", StringComparison.Ordinal), "provider-neutral speaker rendering");
}

var directRealtimeClient = File.ReadAllText(FindRepositoryFile(
    "Provider",
    "Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore",
    "wwwroot",
    "voice-assistant-direct-realtime.js"));
Assert(directRealtimeClient.Contains("this.emitUsage(event);", StringComparison.Ordinal), "direct Realtime response usage forwarding");
Assert(directRealtimeClient.Contains("type: 'usage'", StringComparison.Ordinal), "direct Realtime usage control event type");
Assert(directRealtimeClient.Contains("responseId,", StringComparison.Ordinal), "direct Realtime response identity forwarding");
Assert(directRealtimeClient.Contains("this.emitTranscriptionUsage(event);", StringComparison.Ordinal), "direct Realtime transcription usage forwarding");
Assert(directRealtimeClient.Contains("type: 'transcription_usage'", StringComparison.Ordinal), "direct Realtime transcription usage control event type");
Assert(directRealtimeClient.Contains("itemId,", StringComparison.Ordinal), "direct Realtime transcription identity forwarding");
Assert(directRealtimeClient.Contains("'OnDirectRealtimeTranscriptionUsage'", StringComparison.Ordinal), "direct Realtime transcription usage .NET callback");
Assert(directRealtimeClient.Contains("'OnDirectRealtimeUsageWithMetadata'", StringComparison.Ordinal), "direct Realtime usage metadata .NET callback");
Assert(directRealtimeClient.Contains("event?.response?.model ?? null", StringComparison.Ordinal), "direct Realtime response model forwarding");
Assert(directRealtimeClient.Contains("await Promise.allSettled([", StringComparison.Ordinal), "direct Realtime opens control and media channels in parallel");

using (var openAiUsageJson = JsonDocument.Parse(
    """
    {
      "total_tokens": 100,
      "input_tokens": 80,
      "output_tokens": 20,
      "input_token_details": {
        "text_tokens": 10,
        "audio_tokens": 60,
        "image_tokens": 10,
        "cached_tokens": 40,
        "cached_tokens_details": {
          "text_tokens": 5,
          "audio_tokens": 35,
          "image_tokens": 0
        }
      },
      "output_token_details": {
        "text_tokens": 2,
        "audio_tokens": 18
      }
    }
    """))
{
    var report = OpenAiRealtimeUsageMapper.CreateUsageReport(
        openAiUsageJson.RootElement,
        "gpt-realtime-2.1",
        "resp_123");
    Assert(report.TotalInputTokens == 80, "OpenAI authoritative input total");
    Assert(report.TotalOutputTokens == 20, "OpenAI authoritative output total");
    Assert(report.TotalTokens == 100, "OpenAI authoritative grand total");
    Assert(report.InputTokens == 10 && report.InputAudioTokens == 60 && report.InputImageTokens == 10, "OpenAI input modality details");
    Assert(report.CachedTextInputTokens == 5 && report.CachedAudioInputTokens == 35, "OpenAI cached modality details");
    Assert(report.ModelId == "gpt-realtime-2.1" && report.OperationId == "resp_123", "OpenAI usage identity");
    Assert(!string.IsNullOrWhiteSpace(report.RawProviderUsageJson), "OpenAI raw provider usage");
}

using (var openAiFlatUsageJson = JsonDocument.Parse(
    """{"input_tokens":100,"output_tokens":40,"input_audio_tokens":80,"output_audio_tokens":30}"""))
{
    var report = OpenAiRealtimeUsageMapper.CreateUsageReport(openAiFlatUsageJson.RootElement);
    Assert(report.InputTokens == 20 && report.OutputTokens == 10, "OpenAI flat usage text derivation");
    Assert(report.TotalInputTokens == 100 && report.TotalOutputTokens == 40, "OpenAI flat usage is not double-counted");
}

using (var googleUsageJson = JsonDocument.Parse(
    """
    {
      "promptTokenCount": 100,
      "cachedContentTokenCount": 20,
      "responseTokenCount": 40,
      "toolUsePromptTokenCount": 10,
      "thoughtsTokenCount": 5,
      "totalTokenCount": 155,
      "promptTokensDetails": [
        {"modality":"TEXT","tokenCount":10},
        {"modality":"AUDIO","tokenCount":90}
      ],
      "cacheTokensDetails": [
        {"modality":"TEXT","tokenCount":5},
        {"modality":"AUDIO","tokenCount":15}
      ],
      "responseTokensDetails": [
        {"modality":"TEXT","tokenCount":5},
        {"modality":"AUDIO","tokenCount":35}
      ]
    }
    """))
{
    var report = GoogleLiveUsageMapper.CreateUsageReport(
        googleUsageJson.RootElement,
        "models/gemini-3.1-flash-live-preview");
    Assert(report.TotalInputTokens == 110, "Gemini prompt plus tool-use input total");
    Assert(report.TotalOutputTokens == 45, "Gemini response plus thoughts output total");
    Assert(report.TotalTokens == 155, "Gemini authoritative grand total");
    Assert(report.InputTokens == 10 && report.InputAudioTokens == 90, "Gemini input modality details");
    Assert(report.OutputTokens == 5 && report.OutputAudioTokens == 35, "Gemini response modality details");
    Assert(report.CacheReadInputTokens == 20 && report.CachedAudioInputTokens == 15, "Gemini cache usage");
}

using (var googleLegacyUsageJson = JsonDocument.Parse(
    """{"promptTokenCount":12,"candidatesTokenCount":4,"totalTokenCount":16}"""))
{
    var report = GoogleLiveUsageMapper.CreateUsageReport(googleLegacyUsageJson.RootElement);
    Assert(report.TotalOutputTokens == 4, "Gemini legacy candidatesTokenCount fallback");
}

using (var xaiUsageJson = JsonDocument.Parse(
    """{"total_tokens":30,"input_tokens":20,"output_tokens":10}"""))
{
    var report = XaiRealtimeUsageMapper.CreateUsageReport(
        xaiUsageJson.RootElement,
        "grok-voice-latest",
        "resp_xai",
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3),
        4);
    Assert(report.TotalTokens == 30, "xAI optional provider token total");
    Assert(report.InputAudioDuration == TimeSpan.FromSeconds(2), "xAI measured input audio duration");
    Assert(report.OutputAudioDuration == TimeSpan.FromSeconds(3), "xAI measured output audio duration");
    Assert(report.BillableTextInputEvents == 4, "xAI billable text input events");
    Assert(report.MeasurementSource == UsageMeasurementSource.Mixed, "xAI mixed measurement source");
}

var usageManager = new UsageManager();
usageManager.AddReport(new UsageReport
{
    ProviderId = "google",
    ReportedInputTokens = 10,
    ReportedOutputTokens = 5,
    ReportedTotalTokens = 20,
    InputAudioDuration = TimeSpan.FromSeconds(1),
    BillableTextInputEvents = 2
});
Assert(usageManager.TotalTokens == 20, "usage manager provider grand total");
Assert(usageManager.TotalInputAudioDuration == TimeSpan.FromSeconds(1), "usage manager audio duration");
Assert(usageManager.TotalBillableTextInputEvents == 2, "usage manager billable text events");
Assert(typeof(OpenAiHttpLiveTranscriber).GetProperty(nameof(OpenAiHttpLiveTranscriber.OnUsageReceived)) != null, "HTTP transcriber usage callback");

var xaiMessage = new XaiSessionUpdateMessage
{
    Session = new XaiSessionConfig
    {
        Voice = "eve",
        Reasoning = new XaiReasoningConfig { Effort = "high" },
        Resumption = new XaiResumptionConfig { Enabled = true },
        Audio = new XaiAudioConfig
        {
            Input = new XaiAudioEndpointConfig
            {
                Transcription = new XaiInputAudioTranscriptionConfig
                {
                    LanguageHint = "de-DE",
                    Keyterms = ["TLBX"]
                }
            },
            Output = new XaiAudioEndpointConfig { Speed = 1.2 }
        }
    }
};
using (var xaiJson = JsonDocument.Parse(JsonSerializer.Serialize(xaiMessage, XaiJsonContext.Default.XaiSessionUpdateMessage)))
{
    var session = xaiJson.RootElement.GetProperty("session");
    Assert(session.GetProperty("audio").GetProperty("input").GetProperty("transcription").GetProperty("model").GetString() == "grok-transcribe", "xAI nested transcription model");
    Assert(session.GetProperty("audio").GetProperty("output").GetProperty("speed").GetDouble() == 1.2, "xAI output speed");
    Assert(session.GetProperty("reasoning").GetProperty("effort").GetString() == "high", "xAI reasoning effort");
    Assert(session.GetProperty("resumption").GetProperty("enabled").GetBoolean(), "xAI resumption");
}

var googleMessage = new SetupMessage
{
    Setup = new Setup
    {
        Model = "models/gemini-3.1-flash-live-preview",
        ContextWindowCompression = new ContextWindowCompressionConfig(),
        SessionResumption = new SessionResumptionConfig { Handle = "resume-token" }
    }
};
using (var googleJson = JsonDocument.Parse(JsonSerializer.Serialize(googleMessage, GoogleJsonContext.Default.SetupMessage)))
{
    var setup = googleJson.RootElement.GetProperty("setup");
    Assert(setup.GetProperty("contextWindowCompression").TryGetProperty("slidingWindow", out _), "Gemini context compression");
    Assert(setup.GetProperty("sessionResumption").GetProperty("handle").GetString() == "resume-token", "Gemini session resumption");
}

Console.WriteLine("Provider contract tests passed.");

static void Assert(bool condition, string contract)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Contract failed: {contract}");
    }
}

static async Task VerifyOpenAiPreambleOutputPolicyAsync()
{
    await using var provider = new OpenAiVoiceProvider("contract-test-key")
    {
        Settings = new OpenAiVoiceSettings
        {
            ToolCallPreambleMode = ToolCallPreambleMode.Disabled
        }
    };

    var audio = new List<string>();
    var messages = new List<ChatMessage>();
    provider.OnAudioReceived = audio.Add;
    provider.OnMessageReceived = messages.Add;

    await DeliverOpenAiEventAsync(provider,
        """{"type":"response.output_audio.delta","item_id":"commentary_1","delta":"immediate-audio"}""");
    await DeliverOpenAiEventAsync(provider,
        """{"type":"response.output_audio_transcript.done","item_id":"commentary_1","transcript":"Einen Moment, ich schaue nach."}""");
    Assert(audio.SequenceEqual(["immediate-audio"]), "OpenAI streams received audio immediately even when preambles are disabled");
    Assert(messages.Count == 1 && messages[0].Content == "Einen Moment, ich schaue nach.", "OpenAI streams the accompanying transcript immediately");

    audio.Clear();
    messages.Clear();
    await DeliverOpenAiEventAsync(provider,
        """{"type":"response.output_text.delta","item_id":"text_1","delta":"Voll"}""");
    await DeliverOpenAiEventAsync(provider,
        """{"type":"response.output_text.delta","item_id":"text_1","delta":"ständig."}""");
    await DeliverOpenAiEventAsync(provider,
        """{"type":"response.output_text.done","item_id":"text_1","text":"Vollständig."}""");

    Assert(messages.Count == 1 && messages[0].Content == "Vollständig.", "OpenAI text output remains available with disabled preambles");
}

static void VerifyOpenAiPreambleInstructionPolicy()
{
    var method = typeof(OpenAiVoiceProvider).GetMethod("BuildInstructions", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(OpenAiVoiceProvider).FullName, "BuildInstructions");

    string Build(ToolCallPreambleMode mode) => method.Invoke(null,
        [new OpenAiVoiceSettings { Instructions = "base-instructions", ToolCallPreambleMode = mode }]) as string
        ?? throw new InvalidOperationException("OpenAI instruction builder did not return text.");

    Assert(Build(ToolCallPreambleMode.ProviderDefault) == "base-instructions", "OpenAI provider default preserves instructions");
    Assert(Build(ToolCallPreambleMode.Disabled).Contains("remain silent until the final answer", StringComparison.Ordinal), "OpenAI disabled mode instruction");
    Assert(Build(ToolCallPreambleMode.BeforeToolBurst).Contains("burst of multiple tool calls", StringComparison.Ordinal), "OpenAI tool burst instruction");
    Assert(Build(ToolCallPreambleMode.ForLongRunningTools).Contains("noticeable time", StringComparison.Ordinal), "OpenAI long-running tool instruction");
    Assert(Build(ToolCallPreambleMode.BeforeEveryToolCall).Contains("Before any tool call", StringComparison.Ordinal), "OpenAI every-tool instruction");
}

static void VerifyOpenAiTurnDetectionPolicy()
{
    var method = typeof(OpenAiVoiceProvider).GetMethod(
        "BuildTurnDetectionConfig",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(OpenAiVoiceProvider).FullName, "BuildTurnDetectionConfig");

    TurnDetectionConfig Build(OpenAiVoiceSettings settings) =>
        method.Invoke(null, [settings]) as TurnDetectionConfig
        ?? throw new InvalidOperationException("OpenAI turn detection builder returned no configuration.");

    var serverVadSettings = new OpenAiVoiceSettings { VadThreshold = 0.77 };
    var serverVad = Build(serverVadSettings);
    Assert(serverVad.Eagerness is null, "OpenAI server VAD omits semantic eagerness");
    Assert(serverVad.SilenceDurationMs == 200 && serverVad.Threshold == 0.77, "OpenAI server VAD carries directly configured threshold");

    var semanticVad = Build(new OpenAiVoiceSettings
    {
        Eagerness = Eagerness.high,
        TurnDetection = new TurnDetection { Type = "semantic_vad" }
    });
    Assert(semanticVad.Eagerness == "high", "OpenAI semantic VAD carries eagerness");
    Assert(semanticVad.Threshold is null && semanticVad.PrefixPaddingMs is null && semanticVad.SilenceDurationMs is null,
        "OpenAI semantic VAD omits server VAD-only fields");
}

static void VerifyVadThresholdConvenienceApi()
{
    var openAi = new OpenAiVoiceSettings { VadThreshold = 0.72 };
    Assert(openAi.TurnDetection.Threshold == 0.72, "OpenAI direct VAD threshold forwards to turn detection");
    openAi.TurnDetection.Threshold = 0.68;
    Assert(openAi.VadThreshold == 0.68, "OpenAI direct VAD threshold reflects nested updates");
    Assert(ServiceCollectionExtensions.CreateDefaultOpenAiSettings(vadThreshold: 0.74).VadThreshold == 0.74,
        "OpenAI default settings accept a direct VAD threshold");

    var xai = new XaiVoiceSettings { TurnDetection = null, VadThreshold = 0.82 };
    Assert(xai.TurnDetection?.Threshold == 0.82, "xAI direct VAD threshold initializes and forwards to turn detection");
    xai.TurnDetection!.Threshold = 0.79;
    Assert(xai.VadThreshold == 0.79, "xAI direct VAD threshold reflects nested updates");
    Assert(Ai.Tlbx.VoiceAssistant.Provider.XAi.Extensions.ServiceCollectionExtensions
            .CreateDefaultXaiSettings(vadThreshold: 0.81).VadThreshold == 0.81,
        "xAI default settings accept a direct VAD threshold");
}

static void VerifyOpenAiReasoningPolicy()
{
    var method = typeof(OpenAiVoiceProvider).GetMethod(
        "BuildReasoningConfig",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(OpenAiVoiceProvider).FullName, "BuildReasoningConfig");

    var fullModelReasoning = method.Invoke(null, [new OpenAiVoiceSettings()]) as OpenAiReasoningConfig;
    Assert(fullModelReasoning?.Effort == "low", "OpenAI full realtime model sends low reasoning effort by default");

    var miniModelReasoning = method.Invoke(null,
        [new OpenAiVoiceSettings { Model = OpenAiRealtimeModel.GptRealtime21Mini }]);
    Assert(miniModelReasoning is null, "OpenAI mini model omits unsupported reasoning configuration");
}

static async Task VerifyOpenAiMiniEphemeralSessionAsync()
{
    var handler = new CaptureHttpMessageHandler();
    using var httpClient = new HttpClient(handler);
    await using var provider = new OpenAiVoiceProvider("contract-test-key", httpClient: httpClient)
    {
        Settings = new OpenAiVoiceSettings { Model = OpenAiRealtimeModel.GptRealtime21Mini }
    };

    var method = typeof(OpenAiVoiceProvider).GetMethod(
        "CreateSessionAsync",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(OpenAiVoiceProvider).FullName, "CreateSessionAsync");
    var task = method.Invoke(provider, null) as Task<string>
        ?? throw new InvalidOperationException("OpenAI session factory did not return a task.");

    Assert(await task == "ephemeral-contract-key", "OpenAI ephemeral key response remains in use");
    Assert(handler.RequestUri?.AbsoluteUri == "https://api.openai.com/v1/realtime/client_secrets",
        "OpenAI session still uses the client secrets endpoint");
    Assert(handler.Authorization == "Bearer contract-test-key", "OpenAI client secret request uses the library API key");

    using var requestJson = JsonDocument.Parse(handler.RequestBody
        ?? throw new InvalidOperationException("OpenAI client secret request body was not captured."));
    var session = requestJson.RootElement.GetProperty("session");
    Assert(session.GetProperty("model").GetString() == "gpt-realtime-2.1-mini",
        "OpenAI caller-selected mini model reaches ephemeral session creation");
    Assert(!session.TryGetProperty("reasoning", out _), "OpenAI mini session omits unsupported reasoning configuration");
}

static void VerifyLowLatencyPlaybackPolicy()
{
    var webPlaybackProcessor = File.ReadAllText(FindRepositoryFile(
        "Hardware",
        "Ai.Tlbx.VoiceAssistant.Hardware.Web",
        "wwwroot",
        "js",
        "audio-processor.js"));
    Assert(!webPlaybackProcessor.Contains("MIN_START_BUFFER", StringComparison.Ordinal),
        "web playback does not wait for an artificial startup buffer");
    Assert(webPlaybackProcessor.Contains("this._bufferFill > 0", StringComparison.Ordinal),
        "web playback starts with the first received audio samples");
    Assert(webPlaybackProcessor.Contains("targetChunkMs = 20", StringComparison.Ordinal),
        "web microphone uses 20 ms realtime packets");

    var windowsAudioHardware = File.ReadAllText(FindRepositoryFile(
        "Hardware",
        "Ai.Tlbx.VoiceAssistant.Hardware.Windows",
        "WindowsAudioHardware.cs"));
    Assert(windowsAudioHardware.Contains("PlaybackLatencyMilliseconds = 50", StringComparison.Ordinal),
        "Windows playback uses a low-latency output buffer");
    Assert(windowsAudioHardware.Contains("RecordingBufferMilliseconds = 20", StringComparison.Ordinal),
        "Windows microphone uses 20 ms realtime packets");

    var openAiProvider = File.ReadAllText(FindRepositoryFile(
        "Provider",
        "Ai.Tlbx.VoiceAssistant.Provider.OpenAi",
        "OpenAiVoiceProvider.cs"));
    Assert(!openAiProvider.Contains("Task.Delay(50)", StringComparison.Ordinal),
        "OpenAI history injection has no artificial per-message delay");
    Assert(!openAiProvider.Contains("BufferedOutputItem", StringComparison.Ordinal),
        "OpenAI output is never held until response completion");
}

static async Task VerifyOpenAiAudioBackpressurePolicyAsync()
{
    await using var provider = new OpenAiVoiceProvider("contract-test-key");
    var createChannel = typeof(OpenAiVoiceProvider).GetMethod(
        "CreateAudioSendChannel",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(OpenAiVoiceProvider).FullName, "CreateAudioSendChannel");
    var tryEnqueue = typeof(OpenAiVoiceProvider).GetMethod(
        "TryEnqueueAudio",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(OpenAiVoiceProvider).FullName, "TryEnqueueAudio");
    var channelField = typeof(OpenAiVoiceProvider).GetField(
        "_audioSendChannel",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(OpenAiVoiceProvider).FullName, "_audioSendChannel");
    var droppedField = typeof(OpenAiVoiceProvider).GetField(
        "_droppedAudioChunks",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(OpenAiVoiceProvider).FullName, "_droppedAudioChunks");

    var channel = createChannel.Invoke(null, null) as Channel<string>
        ?? throw new InvalidOperationException("OpenAI audio queue factory returned no channel.");
    channelField.SetValue(provider, channel);

    for (var i = 0; i < 5_000; i++)
    {
        Assert(tryEnqueue.Invoke(provider, [$"chunk-{i}"]) as bool? == true,
            "OpenAI audio queue accepts current audio while saturated");
    }

    Assert(channel.Reader.Count == 100, "OpenAI audio send queue remains bounded at 100 chunks");
    Assert(channel.Reader.TryRead(out var firstRetained) && firstRetained == "chunk-4900",
        "OpenAI audio send queue drops oldest audio and retains the newest speech");
    Assert((long)(droppedField.GetValue(provider) ?? 0L) == 4_900,
        "OpenAI audio send queue records dropped chunks");
}

static async Task VerifyPreConnectAudioBackpressurePolicyAsync()
{
    await using var assistant = new Ai.Tlbx.VoiceAssistant.VoiceAssistant(
        new ContractAudioHardware(),
        provider: null);
    var bufferField = typeof(Ai.Tlbx.VoiceAssistant.VoiceAssistant).GetField(
        "_preConnectBuffer",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(Ai.Tlbx.VoiceAssistant.VoiceAssistant).FullName, "_preConnectBuffer");
    var bufferAudio = typeof(Ai.Tlbx.VoiceAssistant.VoiceAssistant).GetMethod(
        "BufferPreConnectAudio",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(Ai.Tlbx.VoiceAssistant.VoiceAssistant).FullName, "BufferPreConnectAudio");
    var buffer = new ConcurrentQueue<string>();
    bufferField.SetValue(assistant, buffer);

    for (var i = 0; i < 5_000; i++)
    {
        bufferAudio.Invoke(assistant, [buffer, $"chunk-{i}"]);
    }

    Assert(buffer.Count == 100, "pre-connect audio queue remains bounded at 100 chunks");
    Assert(buffer.TryDequeue(out var firstRetained) && firstRetained == "chunk-4900",
        "pre-connect audio queue drops oldest audio and retains the newest speech");
}

static async Task VerifyFailedStartCleanupAsync()
{
    var hardware = new ContractAudioHardware();
    var provider = new FailingConnectVoiceProvider();
    await using var assistant = new Ai.Tlbx.VoiceAssistant.VoiceAssistant(hardware, provider);

    try
    {
        await assistant.StartAsync(new OpenAiVoiceSettings());
        throw new InvalidOperationException("Contract failed: provider startup failure propagates");
    }
    catch (InvalidOperationException ex) when (ex.Message == FailingConnectVoiceProvider.FailureMessage)
    {
        // Expected test failure.
    }

    Assert(hardware.StopRecordingCalls == 1, "failed startup stops microphone recording");
    Assert(provider.DisconnectCalls == 1, "failed startup disconnects partially initialized provider");
    Assert(!assistant.IsRecording && !assistant.IsInitialized && !assistant.IsConnecting,
        "failed startup resets assistant state");
}

static async Task DeliverOpenAiEventAsync(OpenAiVoiceProvider provider, string json)
{
    var method = typeof(OpenAiVoiceProvider).GetMethod("ProcessReceivedMessage", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(typeof(OpenAiVoiceProvider).FullName, "ProcessReceivedMessage");
    var task = method.Invoke(provider, [json]) as Task
        ?? throw new InvalidOperationException("OpenAI event processor did not return a task.");
    await task;
}

static string FindRepositoryFile(params string[] relativeSegments)
{
    DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        var candidate = Path.Combine([directory.FullName, .. relativeSegments]);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        directory = directory.Parent;
    }

    throw new FileNotFoundException($"Could not locate repository file '{Path.Combine(relativeSegments)}'.");
}

sealed class CaptureHttpMessageHandler : HttpMessageHandler
{
    public Uri? RequestUri { get; private set; }
    public string? Authorization { get; private set; }
    public string? RequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestUri = request.RequestUri;
        Authorization = request.Headers.Authorization?.ToString();
        RequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"value\":\"ephemeral-contract-key\",\"expires_at\":4102444800}")
        };
    }
}

sealed class ContractAudioHardware : IAudioHardwareAccess
{
    public int StopRecordingCalls { get; private set; }

    public event EventHandler<string>? AudioError
    {
        add { }
        remove { }
    }

    public Task InitAudioAsync() => Task.CompletedTask;
    public Task<bool> StartRecordingAudio(
        MicrophoneAudioReceivedEventHandler audioDataReceivedHandler,
        AudioSampleRate targetSampleRate = AudioSampleRate.Rate24000) => Task.FromResult(true);
    public bool PlayAudio(string base64EncodedPcm16Audio, int sampleRate) => true;
    public Task<bool> StopRecordingAudio()
    {
        StopRecordingCalls++;
        return Task.FromResult(true);
    }
    public Task ClearAudioQueueAsync() => Task.CompletedTask;
    public Task<List<AudioDeviceInfo>> GetAvailableMicrophonesAsync() => Task.FromResult(new List<AudioDeviceInfo>());
    public Task<List<AudioDeviceInfo>> RequestMicrophonePermissionAndGetDevicesAsync() => Task.FromResult(new List<AudioDeviceInfo>());
    public Task<bool> SetMicrophoneDeviceAsync(string deviceId) => Task.FromResult(true);
    public Task<string?> GetCurrentMicrophoneDeviceAsync() => Task.FromResult<string?>(null);
    public Task<bool> SetDiagnosticLevelAsync(DiagnosticLevel level) => Task.FromResult(true);
    public Task<DiagnosticLevel> GetDiagnosticLevelAsync() => Task.FromResult(DiagnosticLevel.None);
    public void SetLogAction(Action<LogLevel, string> logAction) { }
    public Task<bool> WaitForPlaybackDrainAsync(TimeSpan? timeout = null) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FailingConnectVoiceProvider : IVoiceProvider
{
    public const string FailureMessage = "Contract provider connection failure";
    public int DisconnectCalls { get; private set; }
    public bool IsConnected => false;
    public AudioSampleRate RequiredInputSampleRate => AudioSampleRate.Rate24000;
    public Action<ChatMessage>? OnMessageReceived { get; set; }
    public Action<string>? OnAudioReceived { get; set; }
    public Func<TimeSpan?, Task<bool>>? WaitForPlaybackDrainAsync { get; set; }
    public Action<string>? OnStatusChanged { get; set; }
    public Action<string>? OnError { get; set; }
    public Action? OnInterruptDetected { get; set; }
    public Action<UsageReport>? OnUsageReceived { get; set; }
    public Action<string>? OnTranscriptionDelta { get; set; }
    public Action<string>? OnTranscriptionCompleted { get; set; }

    public Task ConnectAsync(IVoiceSettings settings) => throw new InvalidOperationException(FailureMessage);
    public Task DisconnectAsync()
    {
        DisconnectCalls++;
        return Task.CompletedTask;
    }
    public Task UpdateSettingsAsync(IVoiceSettings settings) => Task.CompletedTask;
    public Task ProcessAudioAsync(string base64Audio) => Task.CompletedTask;
    public Task SendInterruptAsync() => Task.CompletedTask;
    public Task InjectConversationHistoryAsync(IEnumerable<ChatMessage> messages) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
