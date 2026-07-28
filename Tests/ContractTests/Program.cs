using System.Text.Json;
using Ai.Tlbx.VoiceAssistant.Managers;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.Google;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Models;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Protocol;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.XAi;
using Ai.Tlbx.VoiceAssistant.Provider.XAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.XAi.Protocol;

Assert(Enum.GetValues<AssistantVoice>().Length == 10, "OpenAI voice roster");
Assert(Enum.GetValues<GoogleVoice>().Length == 30, "Gemini voice roster");
Assert(Enum.GetValues<XaiVoice>().Length == 26, "xAI voice roster");
Assert(new OpenAiVoiceSettings().Model == OpenAiRealtimeModel.GptRealtime21, "OpenAI default model");
Assert(new OpenAiVoiceSettings().Voice == AssistantVoice.Marin, "OpenAI default voice");
Assert(OpenAiRealtimeModel.GptRealtime21.ToApiString() == "gpt-realtime-2.1", "OpenAI 2.1 model id");
Assert(OpenAiRealtimeModel.GptRealtime21Mini.ToApiString() == "gpt-realtime-2.1-mini", "OpenAI 2.1 mini model id");
Assert(new XaiVoiceSettings().Model == XaiVoiceModel.GrokVoiceLatest, "xAI default model");
Assert(XaiVoiceModel.GrokVoiceLatest.ToApiString() == "grok-voice-latest", "xAI latest model id");

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
