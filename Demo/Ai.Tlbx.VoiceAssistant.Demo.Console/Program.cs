using System.ComponentModel;
using System.Runtime.InteropServices;
using Ai.Tlbx.VoiceAssistant;
using Ai.Tlbx.VoiceAssistant.BuiltInTools;
using Ai.Tlbx.VoiceAssistant.Hardware.Linux;
using Ai.Tlbx.VoiceAssistant.Hardware.Windows;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.Google;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.XAi;
using Ai.Tlbx.VoiceAssistant.Provider.XAi.Models;
using Spectre.Console;

namespace Ai.Tlbx.VoiceAssistant.Demo.Console;

internal static class Program
{
    private enum ProviderChoice
    {
        OpenAI,
        Google,
        XAi
    }

    private enum SessionMode
    {
        None,
        Voice,
        Transcription
    }

    private static readonly List<IVoiceTool> AllTools =
    [
        new TimeTool(),
        new WeatherTool(),
        new CalculatorTool()
    ];

    private static readonly List<LogEntry> Logs = [];
    private static readonly List<ChatMessage> Messages = [];

    private static IAudioHardwareAccess? hardware;
    private static VoiceAssistant? assistant;
    private static OpenAiHttpLiveTranscriber? pttTranscriber;
    private static CancellationTokenSource? pttCts;
    private static Task? pttTask;
    private static List<AudioDeviceInfo> microphones = [];
    private static string selectedMicrophoneId = string.Empty;
    private static ProviderChoice selectedProvider = ProviderChoice.OpenAI;
    private static string selectedVoice = nameof(AssistantVoice.Alloy);
    private static string selectedModel = nameof(OpenAiRealtimeModel.GptRealtime2);
    private static OpenAiTranscriptionModel transcriptionModel = OpenAiTranscriptionModel.GptRealtimeWhisper;
    private static bool includeTranscriptionLogProbabilities;
    private static double talkingSpeed = 1.0;
    private static SessionReasoningEffort? reasoningEffort = SessionReasoningEffort.Medium;
    private static SessionThinkingConfig thinking = new();
    private static ToolCallPreambleMode toolCallPreambleMode = ToolCallPreambleMode.BeforeToolBurst;
    private static DiagnosticLevel diagnosticLevel = DiagnosticLevel.Basic;
    private static readonly HashSet<string> enabledTools = AllTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
    private static SessionMode mode = SessionMode.None;
    private static string status = "Ready";
    private static string pttTranscript = string.Empty;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            hardware = CreateHardware();
            hardware.SetLogAction(Log);
            await hardware.SetDiagnosticLevelAsync(diagnosticLevel);
            await RefreshMicrophonesAsync(requestPermission: false);

            if (args.Any(arg => string.Equals(arg, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
            {
                AnsiConsole.MarkupLine("[green]Console demo smoke test passed.[/]");
                AnsiConsole.MarkupLine($"Platform: {Markup.Escape(RuntimeInformation.OSDescription)}");
                AnsiConsole.MarkupLine($"Microphones: {microphones.Count}");
                AnsiConsole.MarkupLine($"Selected microphone: {Markup.Escape(GetSelectedMicrophoneName())}");
                return 0;
            }

            await RunMenuAsync();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
        finally
        {
            await StopPttAsync();
            await DisposeAssistantAsync();

            if (hardware != null)
            {
                await hardware.DisposeAsync();
            }
        }
    }

    private static IAudioHardwareAccess CreateHardware()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WindowsAudioHardware();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new LinuxAudioDevice();
        }

        throw new PlatformNotSupportedException("The console demo currently supports Windows and Linux audio hardware.");
    }

    private static async Task RunMenuAsync()
    {
        while (true)
        {
            RenderDashboard();

            var choices = new List<string>
            {
                mode == SessionMode.Voice ? "Stop voice session" : "Start voice session",
                mode == SessionMode.Transcription ? "Stop streaming transcription" : "Start streaming transcription",
                "Hold-to-transcribe once",
                "Interrupt current response",
                "Test microphone",
                "Provider / model / voice",
                "Reasoning / thinking / tools",
                "Microphone / diagnostics",
                "Clear chat and logs",
                "Quit"
            };

            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Action")
                    .PageSize(12)
                    .AddChoices(choices));

            switch (selected)
            {
                case "Start voice session":
                    await StartVoiceSessionAsync();
                    break;
                case "Stop voice session":
                    await StopAssistantSessionAsync();
                    break;
                case "Start streaming transcription":
                    await StartStreamingTranscriptionAsync();
                    break;
                case "Stop streaming transcription":
                    await StopAssistantSessionAsync();
                    break;
                case "Hold-to-transcribe once":
                    await RunPushToTalkTranscriptionAsync();
                    break;
                case "Interrupt current response":
                    await InterruptAsync();
                    break;
                case "Test microphone":
                    await TestMicrophoneAsync();
                    break;
                case "Provider / model / voice":
                    await ConfigureProviderAsync();
                    break;
                case "Reasoning / thinking / tools":
                    await ConfigureBehaviorAsync();
                    break;
                case "Microphone / diagnostics":
                    await ConfigureHardwareAsync();
                    break;
                case "Clear chat and logs":
                    Messages.Clear();
                    Logs.Clear();
                    pttTranscript = string.Empty;
                    break;
                case "Quit":
                    return;
            }
        }
    }

    private static void RenderDashboard()
    {
        AnsiConsole.Clear();

        var title = new Rule("[bold]AI Voice Assistant Terminal Demo[/]").RuleStyle("grey").Centered();
        AnsiConsole.Write(title);

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow(BuildSettingsPanel(), BuildStatusPanel());
        grid.AddRow(BuildConversationPanel(), BuildLogPanel());
        AnsiConsole.Write(grid);
    }

    private static Panel BuildSettingsPanel()
    {
        var tools = enabledTools.Count == AllTools.Count
            ? "all"
            : string.Join(", ", enabledTools);

        var table = new Table().NoBorder().AddColumn("Key").AddColumn("Value");
        table.AddRow("Provider", selectedProvider.ToString());
        table.AddRow("Model", Markup.Escape(selectedModel));
        table.AddRow("Voice", Markup.Escape(selectedVoice));
        table.AddRow("Speed", talkingSpeed.ToString("0.00"));
        table.AddRow("Reasoning", reasoningEffort?.ToString() ?? "default");
        table.AddRow("Thinking", FormatThinking());
        table.AddRow("Tool preambles", toolCallPreambleMode.ToString());
        table.AddRow("Tools", Markup.Escape(tools));
        table.AddRow("Mic", Markup.Escape(GetSelectedMicrophoneName()));
        table.AddRow("Diagnostics", diagnosticLevel.ToString());
        table.AddRow("Transcription", $"{transcriptionModel}, logprobs={includeTranscriptionLogProbabilities}");
        return new Panel(table).Header("Settings").Expand();
    }

    private static Panel BuildStatusPanel()
    {
        var table = new Table().NoBorder().AddColumn("Key").AddColumn("Value");
        table.AddRow("Mode", mode.ToString());
        table.AddRow("Status", Markup.Escape(status));
        table.AddRow("Platform", RuntimeInformation.OSDescription);
        table.AddRow("API keys", $"OpenAI={HasKey("OPENAI_API_KEY")}, Google={HasKey("GOOGLE_API_KEY")}, xAI={HasKey("XAI_API_KEY")}");

        if (!string.IsNullOrWhiteSpace(pttTranscript))
        {
            table.AddRow("Hold transcript", Markup.Escape(pttTranscript));
        }

        return new Panel(table).Header("Runtime").Expand();
    }

    private static Panel BuildConversationPanel()
    {
        var lines = Messages.Count == 0
            ? "No messages yet."
            : string.Join(Environment.NewLine + Environment.NewLine, Messages.TakeLast(10).Select(m => $"{m.Role}: {m.Content}"));

        return new Panel(Markup.Escape(lines)).Header("Conversation").Expand();
    }

    private static Panel BuildLogPanel()
    {
        var lines = Logs.Count == 0
            ? "No logs yet."
            : string.Join(Environment.NewLine, Logs.TakeLast(16).Select(l => $"[{l.Timestamp:HH:mm:ss}] {l.Level}: {l.Message}"));

        return new Panel(Markup.Escape(lines)).Header("Logs").Expand();
    }

    private static async Task ConfigureProviderAsync()
    {
        EnsureNotRunning();

        selectedProvider = AnsiConsole.Prompt(
            new SelectionPrompt<ProviderChoice>()
                .Title("Provider")
                .AddChoices(Enum.GetValues<ProviderChoice>()));

        selectedVoice = selectedProvider switch
        {
            ProviderChoice.OpenAI => PromptEnum("OpenAI voice", AssistantVoice.Alloy).ToString(),
            ProviderChoice.Google => PromptEnum("Google voice", GoogleVoice.Puck).ToString(),
            ProviderChoice.XAi => PromptEnum("xAI voice", XaiVoice.Ara).ToString(),
            _ => selectedVoice
        };

        selectedModel = selectedProvider switch
        {
            ProviderChoice.OpenAI => PromptEnum("OpenAI realtime model", OpenAiRealtimeModel.GptRealtime2, includeObsolete: true).ToString(),
            ProviderChoice.Google => PromptEnum("Google Live model", GoogleModel.Gemini31FlashLivePreview, includeObsolete: true).ToString(),
            ProviderChoice.XAi => PromptEnum("xAI realtime model", XaiVoiceModel.GrokVoiceThinkFast10).ToString(),
            _ => selectedModel
        };

        talkingSpeed = AnsiConsole.Prompt(
            new TextPrompt<double>("Talking speed")
                .DefaultValue(talkingSpeed)
                .Validate(v => v is >= 0.25 and <= 1.5
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Use a value between 0.25 and 1.5.")));

        await Task.CompletedTask;
    }

    private static async Task ConfigureBehaviorAsync()
    {
        EnsureNotRunning();

        var reasoningChoices = new[] { "Provider default" }
            .Concat(Enum.GetNames<SessionReasoningEffort>())
            .ToArray();
        var reasoning = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Reasoning effort")
                .AddChoices(reasoningChoices));
        reasoningEffort = reasoning == "Provider default"
            ? null
            : Enum.Parse<SessionReasoningEffort>(reasoning);

        toolCallPreambleMode = PromptEnum("Tool preamble mode", toolCallPreambleMode);

        var thinkingLevelChoices = new[] { "Provider default" }
            .Concat(Enum.GetNames<SessionThinkingLevel>())
            .ToArray();
        var levelText = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Google thinking level")
                .AddChoices(thinkingLevelChoices));
        var level = levelText == "Provider default"
            ? (SessionThinkingLevel?)null
            : Enum.Parse<SessionThinkingLevel>(levelText);

        var budgetText = AnsiConsole.Prompt(
            new TextPrompt<string>("Google thinking budget (-1/default/number)")
                .DefaultValue(thinking.Budget?.ToString() ?? "default"));
        var budget = string.Equals(budgetText, "default", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(budgetText)
                ? (int?)null
                : int.Parse(budgetText);

        var includeThoughts = AnsiConsole.Confirm("Include Google thought summaries?", thinking.IncludeThoughts);
        thinking = new SessionThinkingConfig
        {
            Level = level,
            Budget = budget,
            IncludeThoughts = includeThoughts
        };

        enabledTools.Clear();
        foreach (var tool in AllTools)
        {
            if (AnsiConsole.Confirm($"Enable tool {tool.Name}?", enabledTools.Contains(tool.Name)))
            {
                enabledTools.Add(tool.Name);
            }
        }

        transcriptionModel = PromptEnum("Transcription model", transcriptionModel);
        includeTranscriptionLogProbabilities = AnsiConsole.Confirm("Include transcription log probabilities when supported?", includeTranscriptionLogProbabilities);

        await Task.CompletedTask;
    }

    private static async Task ConfigureHardwareAsync()
    {
        EnsureNotRunning();

        var reload = AnsiConsole.Confirm("Request/refresh microphone list?", true);
        await RefreshMicrophonesAsync(requestPermission: reload);

        if (microphones.Count > 0)
        {
            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<AudioDeviceInfo>()
                    .Title("Microphone")
                    .UseConverter(m => $"{(m.IsDefault ? "* " : "")}{m.Name} [{m.Id}]")
                    .AddChoices(microphones));

            selectedMicrophoneId = selected.Id;
            await hardware!.SetMicrophoneDeviceAsync(selectedMicrophoneId);
        }

        diagnosticLevel = PromptEnum("Diagnostic level", diagnosticLevel);
        await hardware!.SetDiagnosticLevelAsync(diagnosticLevel);
    }

    private static async Task StartVoiceSessionAsync()
    {
        EnsureNotRunning();

        var (provider, settings) = CreateProviderAndSettings();
        await CreateAssistantAsync(provider);
        await ApplySelectedMicrophoneAsync();

        mode = SessionMode.Voice;
        status = "Starting voice session...";
        await assistant!.StartAsync(settings);
        status = "Voice session active";
    }

    private static async Task StartStreamingTranscriptionAsync()
    {
        EnsureNotRunning();
        EnsureOpenAiKey();
        if (!transcriptionModel.SupportsRealtimeTranscription())
        {
            throw new InvalidOperationException($"{transcriptionModel} is not supported by OpenAI's realtime transcription stream. Use hold-to-transcribe.");
        }

        var provider = new OpenAiTranscriptionProvider(logAction: Log);
        await CreateAssistantAsync(provider);
        await ApplySelectedMicrophoneAsync();

        var settings = BuildTranscriptionSettings();
        mode = SessionMode.Transcription;
        status = "Starting streaming transcription...";
        await assistant!.StartAsync(settings);
        status = "Streaming transcription active";
    }

    private static async Task RunPushToTalkTranscriptionAsync()
    {
        EnsureNotRunning();
        EnsureOpenAiKey();
        if (!transcriptionModel.SupportsHttpStreamingTranscription())
        {
            throw new InvalidOperationException($"{transcriptionModel} is not supported by streamed HTTP transcription. Use streaming transcription.");
        }

        await ApplySelectedMicrophoneAsync();

        pttTranscript = string.Empty;
        pttCts = new CancellationTokenSource();
        pttTranscriber = new OpenAiHttpLiveTranscriber(
            hardware!,
            BuildPttOptions(),
            logAction: Log);

        AnsiConsole.MarkupLine("[yellow]Recording. Press Enter to stop.[/]");
        pttTask = pttTranscriber.TranscribeLive(text =>
        {
            pttTranscript = text;
            Log(LogLevel.Info, $"PTT transcript: {text}");
        }, pttCts.Token);

        await Task.Run(System.Console.ReadLine);
        await StopPttAsync();
    }

    private static async Task StopPttAsync()
    {
        var transcriber = pttTranscriber;
        var task = pttTask;
        var cts = pttCts;

        pttTranscriber = null;
        pttTask = null;
        pttCts = null;

        cts?.Cancel();

        if (transcriber != null)
        {
            await transcriber.StopAsync();
        }

        if (task != null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }

        cts?.Dispose();

        if (transcriber != null)
        {
            await transcriber.DisposeAsync();
        }
    }

    private static async Task InterruptAsync()
    {
        if (assistant == null || mode == SessionMode.None)
        {
            status = "No active session";
            return;
        }

        await assistant.InterruptAsync();
        status = "Interrupt sent";
    }

    private static async Task TestMicrophoneAsync()
    {
        EnsureNotRunning();

        await CreateAssistantAsync(provider: null);
        await ApplySelectedMicrophoneAsync();

        status = "Running microphone test...";
        var ok = await assistant!.TestMicrophoneAsync();
        status = ok ? "Microphone test passed" : "Microphone test failed";
        await DisposeAssistantAsync();
    }

    private static async Task StopAssistantSessionAsync()
    {
        if (assistant == null)
        {
            mode = SessionMode.None;
            status = "Ready";
            return;
        }

        await assistant.StopAsync();
        await DisposeAssistantAsync();
        mode = SessionMode.None;
        status = "Stopped";
    }

    private static async Task CreateAssistantAsync(IVoiceProvider? provider)
    {
        await DisposeAssistantAsync();
        assistant = new VoiceAssistant(hardware!, provider, Log);
        assistant.OnConnectionStatusChanged = s => status = s;
        assistant.OnMessageAdded = message =>
        {
            Messages.Add(message);
            Log(LogLevel.Info, $"{message.Role}: {message.Content}");
        };
        assistant.OnTranscriptionDelta = transcript => status = $"Transcribing: {transcript}";
        assistant.OnTranscriptionCompleted = transcript =>
        {
            pttTranscript = transcript;
            Log(LogLevel.Info, $"Transcript: {transcript}");
        };
        assistant.OnSessionUsageUpdated = usage =>
            Log(LogLevel.Info, $"Usage update: {usage.LocalSessionDuration.TotalMinutes:F1} min, {usage.TotalTokens} tokens");
    }

    private static async Task DisposeAssistantAsync()
    {
        if (assistant == null)
        {
            return;
        }

        assistant.OnConnectionStatusChanged = null;
        assistant.OnMessageAdded = null;
        assistant.OnTranscriptionDelta = null;
        assistant.OnTranscriptionCompleted = null;
        assistant.OnSessionUsageUpdated = null;
        await assistant.DisposeAsync();
        assistant = null;
    }

    private static (IVoiceProvider Provider, IVoiceSettings Settings) CreateProviderAndSettings()
    {
        return selectedProvider switch
        {
            ProviderChoice.OpenAI => CreateOpenAi(),
            ProviderChoice.Google => CreateGoogle(),
            ProviderChoice.XAi => CreateXai(),
            _ => throw new InvalidOperationException($"Unsupported provider: {selectedProvider}")
        };
    }

    private static (IVoiceProvider Provider, IVoiceSettings Settings) CreateOpenAi()
    {
        EnsureOpenAiKey();
        var voice = ParseOrDefault(selectedVoice, AssistantVoice.Alloy);
        var model = ParseOrDefault(selectedModel, OpenAiRealtimeModel.GptRealtime2);
        var settings = new OpenAiVoiceSettings
        {
            Voice = voice,
            Model = model,
            Instructions = DefaultInstructions,
            InputAudioTranscription = new InputAudioTranscription
            {
                Model = OpenAiTranscriptionModel.GptRealtimeWhisper,
                Prompt = "Expect German with slight accent",
                Enabled = true
            },
            TalkingSpeed = talkingSpeed,
            ReasoningEffort = reasoningEffort,
            ToolCallPreambleMode = toolCallPreambleMode,
            Thinking = thinking,
            Tools = GetEnabledTools(),
            MostLikelySpokenLanguage = "de"
        };

        return (new OpenAiVoiceProvider(logAction: Log), settings);
    }

    private static (IVoiceProvider Provider, IVoiceSettings Settings) CreateGoogle()
    {
        EnsureKey("GOOGLE_API_KEY");
        var voice = ParseOrDefault(selectedVoice, GoogleVoice.Puck);
        var model = ParseOrDefault(selectedModel, GoogleModel.Gemini31FlashLivePreview);
        var settings = new GoogleVoiceSettings
        {
            Voice = voice,
            Model = model,
            Instructions = DefaultInstructions,
            TalkingSpeed = talkingSpeed,
            ReasoningEffort = reasoningEffort,
            ToolCallPreambleMode = toolCallPreambleMode,
            Thinking = thinking,
            ResponseModality = "AUDIO",
            LanguageCode = "de-DE",
            Tools = GetEnabledTools(),
            TranscriptionConfig = new AudioTranscriptionConfig
            {
                EnableInputTranscription = true,
                EnableOutputTranscription = true
            }
        };

        return (new GoogleVoiceProvider(logAction: Log), settings);
    }

    private static (IVoiceProvider Provider, IVoiceSettings Settings) CreateXai()
    {
        EnsureKey("XAI_API_KEY");
        var voice = ParseOrDefault(selectedVoice, XaiVoice.Ara);
        var model = ParseOrDefault(selectedModel, XaiVoiceModel.GrokVoiceThinkFast10);
        var settings = new XaiVoiceSettings
        {
            Voice = voice,
            Model = model,
            Instructions = DefaultInstructions,
            TalkingSpeed = talkingSpeed,
            ReasoningEffort = reasoningEffort,
            ToolCallPreambleMode = toolCallPreambleMode,
            Thinking = thinking,
            Tools = GetEnabledTools(),
            EnableWebSearch = false,
            EnableXSearch = false,
            InputAudioLanguage = "de"
        };

        return (new XaiVoiceProvider(logAction: Log), settings);
    }

    private static OpenAiTranscriptionSettings BuildTranscriptionSettings()
    {
        return new OpenAiTranscriptionSettings
        {
            TranscriptionModel = transcriptionModel,
            TranscriptionPrompt = transcriptionModel.SupportsTranscriptionPrompt()
                ? "Expect German with slight accent, business/IT/construction terms"
                : null,
            Language = "de",
            IncludeLogProbabilities = includeTranscriptionLogProbabilities &&
                transcriptionModel.SupportsTranscriptionLogProbabilities()
        };
    }

    private static OpenAiHttpLiveTranscriptionOptions BuildPttOptions()
    {
        return new OpenAiHttpLiveTranscriptionOptions
        {
            TranscriptionModel = transcriptionModel,
            Language = "de",
            Prompt = transcriptionModel.SupportsTranscriptionPrompt()
                ? "Expect German with slight accent, business/IT/construction terms"
                : null,
            IncludeLogProbabilities = false,
            SnapshotInterval = TimeSpan.FromMilliseconds(250),
            MinimumUtteranceDuration = TimeSpan.FromMilliseconds(120),
            LeadingTrimDuration = TimeSpan.FromMilliseconds(120),
            PrefixPadding = TimeSpan.FromMilliseconds(250),
            SilenceDuration = TimeSpan.FromMilliseconds(180),
            VadThreshold = 0.28
        };
    }

    private static async Task RefreshMicrophonesAsync(bool requestPermission)
    {
        if (hardware == null)
        {
            return;
        }

        microphones = requestPermission
            ? await hardware.RequestMicrophonePermissionAndGetDevicesAsync()
            : await hardware.GetAvailableMicrophonesAsync();

        if (microphones.Count == 0)
        {
            selectedMicrophoneId = string.Empty;
            return;
        }

        var selectedStillExists = microphones.Any(m => string.Equals(m.Id, selectedMicrophoneId, StringComparison.Ordinal));
        if (!selectedStillExists)
        {
            selectedMicrophoneId = microphones.FirstOrDefault(m => m.IsDefault)?.Id ?? microphones[0].Id;
        }

        await hardware.SetMicrophoneDeviceAsync(selectedMicrophoneId);
    }

    private static async Task ApplySelectedMicrophoneAsync()
    {
        if (hardware == null || string.IsNullOrWhiteSpace(selectedMicrophoneId))
        {
            return;
        }

        await hardware.SetMicrophoneDeviceAsync(selectedMicrophoneId);
    }

    private static List<IVoiceTool> GetEnabledTools()
    {
        return AllTools
            .Where(t => enabledTools.Contains(t.Name))
            .ToList();
    }

    private static T PromptEnum<T>(string title, T current, bool includeObsolete = false)
        where T : struct, Enum
    {
        var values = Enum.GetValues<T>()
            .Where(v => includeObsolete || typeof(T).GetField(v.ToString())?.GetCustomAttributes(typeof(ObsoleteAttribute), false).Length == 0)
            .ToArray();

        return AnsiConsole.Prompt(
            new SelectionPrompt<T>()
                .Title(title)
                .UseConverter(v => v.ToString())
                .AddChoices(values)
                .HighlightStyle("green")
                .MoreChoicesText("[grey](Move up and down to reveal more choices)[/]"));
    }

    private static T ParseOrDefault<T>(string value, T fallback)
        where T : struct, Enum
    {
        return Enum.TryParse<T>(value, out var parsed)
            ? parsed
            : fallback;
    }

    private static void EnsureNotRunning()
    {
        if (mode != SessionMode.None)
        {
            throw new InvalidOperationException("Stop the active session before changing this setting.");
        }
    }

    private static void EnsureOpenAiKey()
    {
        EnsureKey("OPENAI_API_KEY");
    }

    private static void EnsureKey(string variable)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
        {
            throw new InvalidOperationException($"{variable} is not set.");
        }
    }

    private static string HasKey(string variable)
    {
        return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable))
            ? "[red]missing[/]"
            : "[green]set[/]";
    }

    private static string GetSelectedMicrophoneName()
    {
        if (microphones.Count == 0)
        {
            return "none";
        }

        var mic = microphones.FirstOrDefault(m => string.Equals(m.Id, selectedMicrophoneId, StringComparison.Ordinal));
        return mic == null ? "default" : $"{(mic.IsDefault ? "* " : "")}{mic.Name}";
    }

    private static string FormatThinking()
    {
        var parts = new List<string>();
        if (thinking.Level.HasValue)
        {
            parts.Add($"level={thinking.Level.Value}");
        }

        if (thinking.Budget.HasValue)
        {
            parts.Add($"budget={thinking.Budget.Value}");
        }

        if (thinking.IncludeThoughts)
        {
            parts.Add("thought summaries");
        }

        return parts.Count == 0 ? "provider default" : string.Join(", ", parts);
    }

    private static void Log(LogLevel level, string message)
    {
        Logs.Add(new LogEntry(DateTime.Now, level, message));
        if (Logs.Count > 500)
        {
            Logs.RemoveRange(0, Logs.Count - 500);
        }
    }

    private const string DefaultInstructions =
        "Du bist ein hilfreicher Voice Assistant. Antworte kurz, klar und auf Deutsch, ausser der Nutzer spricht Englisch. " +
        "Unterbrich dich, wenn der Nutzer neu spricht, und nutze Tools nur wenn es passt.";

    private sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message);
}
