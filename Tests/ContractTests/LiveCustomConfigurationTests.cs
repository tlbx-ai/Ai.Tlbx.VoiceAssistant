using System.Reflection;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.Google;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Models;
using Ai.Tlbx.VoiceAssistant.Provider.XAi;
using Ai.Tlbx.VoiceAssistant.Provider.XAi.Models;

static class LiveCustomConfigurationTests
{
    private static readonly List<string> Secrets = new();
    public static async Task RunAsync()
    {
        var failed = 0;
        async Task Run(string name, Func<Task> test)
        {
            try { await test(); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex)
            {
                failed++;
                var message = ex.GetBaseException().Message;
                foreach (var secret in Secrets.Where(s => !string.IsNullOrEmpty(s))) message = message.Replace(secret, "[redacted]");
                Console.WriteLine($"FAIL {name}: {message}");
            }
        }

        foreach (var ephemeral in new[] { true, false })
            await Run($"OpenAI voice custom endpoints, ephemeral={ephemeral}: session.updated", async () =>
            {
                var key = Key("OPENAI_API_KEY");
                var settings = new OpenAiVoiceSettings
                {
                    ModelId = "gpt-realtime-2.1", UseEphemeralKey = ephemeral,
                    Instructions = "Initialization check only. Do not speak.",
                    Connection = new("wss://api.openai.com/v1/realtime"),
                    ClientSecretsConnection = new("https://api.openai.com/v1/realtime/client_secrets") { ApiKey = key }
                };
                // In ephemeral mode the socket receives the freshly minted key.
                if (!ephemeral) settings.Connection.ApiKey = key;
                await Connect(settings, log => new OpenAiVoiceProvider("", log), "Session updated by OpenAI");
            });

        await Run("OpenAI STT custom endpoint/model: session.updated", async () =>
        {
            var settings = new OpenAiTranscriptionSettings
            {
                ModelId = "gpt-live-transcribe",
                Connection = new("wss://api.openai.com/v1/realtime") { ApiKey = Key("OPENAI_API_KEY") }
            };
            await Connect(settings, log => new OpenAiTranscriptionProvider("", log), "Transcription session updated");
        });

        await Run("xAI voice custom endpoint/model: session.updated", async () =>
        {
            var settings = new XaiVoiceSettings
            {
                Model = XaiVoiceModel.GrokVoiceThinkFast20, ModelId = "grok-voice-think-fast-2.0",
                EnableSessionResumption = false, EnableInputAudioTranscription = false,
                Instructions = "Initialization check only. Do not speak.",
                Connection = new("wss://api.x.ai/v1/realtime") { ApiKey = Key("XAI_API_KEY") }
            };
            await Connect(settings, log => new XaiVoiceProvider("", log));
        });

        await Run("xAI STT custom endpoint/model: transcript.created", async () =>
        {
            var settings = new XaiTranscriptionSettings
            {
                ModelId = "grok-transcribe",
                Connection = new("wss://api.x.ai/v1/stt") { ApiKey = Key("XAI_API_KEY") }
            };
            await Connect(settings, log => new XaiTranscriptionProvider("", log));
        });

        await Run("Google custom endpoint/model: setupComplete", async () =>
        {
            var settings = new GoogleVoiceSettings
            {
                ModelId = "models/gemini-3.1-flash-live-preview",
                Instructions = "Initialization check only. Do not speak.",
                Connection = new("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent")
                {
                    ApiKey = Key("GEMINI_API_KEY", "GOOGLE_API_KEY"),
                    AuthenticationHeaderName = null, ApiKeyQueryParameter = "key"
                }
            };
            await Connect(settings, log => new GoogleVoiceProvider("", log));
        });

        await Run("OpenAI HTTP custom endpoint/model: successful streaming upload", async () =>
        {
            var options = new OpenAiHttpLiveTranscriptionOptions
            {
                ModelId = "gpt-transcribe",
                Connection = new("https://api.openai.com/v1/audio/transcriptions") { ApiKey = Key("OPENAI_API_KEY") }
            };
            var accepted = false;
            await using var transcriber = new OpenAiHttpLiveTranscriber(new ContractAudioHardware(), options, "");
            transcriber.OnUsageReceived = _ => accepted = true;
            var type = typeof(OpenAiHttpLiveTranscriber);
            var snapshotType = type.GetNestedType("SnapshotWorkItem", BindingFlags.NonPublic)!;
            // One second of synthetic silence. No microphone or personal audio.
            var snapshot = Activator.CreateInstance(snapshotType, 1, 1L, new byte[48000], true, "", TimeSpan.Zero, TimeSpan.FromSeconds(1))!;
            var send = type.GetMethod("TranscribeSnapshotAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)send.Invoke(transcriber, [snapshot, (Action<string>)(_ => { })])!;
            if (!accepted) throw new InvalidOperationException("No successful HTTP response.");
        });
        Console.WriteLine($"Live custom configuration: {7 - failed}/7 passed. No user audio or generated response requested.");
        if (failed != 0) Environment.ExitCode = 1;
    }

    private static async Task Connect(IVoiceSettings settings, Func<Action<LogLevel, string>, IVoiceProvider> factory, string? acknowledgement = null)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = factory((level, message) =>
        {
            if (acknowledgement != null && message == acknowledgement) ready.TrySetResult();
        });
        provider.OnError = message => error.TrySetException(new InvalidOperationException(message));
        try
        {
            await provider.ConnectAsync(settings).WaitAsync(TimeSpan.FromSeconds(30));
            if (acknowledgement != null)
                await await Task.WhenAny(ready.Task, error.Task).WaitAsync(TimeSpan.FromSeconds(15));
            if (!provider.IsConnected) throw new InvalidOperationException("Socket is not connected after initialization.");
            if (error.Task.IsCompleted) await error.Task;
        }
        finally { await provider.DisconnectAsync(); }
    }

    private static string Key(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) { Secrets.Add(value); return value; }
        }
        throw new InvalidOperationException("Missing credential: " + string.Join(" / ", names));
    }
}
