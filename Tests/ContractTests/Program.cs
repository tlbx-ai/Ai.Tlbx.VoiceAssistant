using System.Text.Json;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Models;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Protocol;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
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
