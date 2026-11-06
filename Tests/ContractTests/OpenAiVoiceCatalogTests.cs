using System.Reflection;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

internal static class OpenAiVoiceCatalogTests
{
    public static void Run()
    {
        string[] realtime = ["alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar"];
        string[] live = ["marin", "quartz", "ripple", "vesper", "willow", "stone", "gleam", "meridian", "bossa", "tempo",
            "beacon", "delta", "cinder", "nuri", "juni", "flitz", "brise", "sillage", "nira", "harema", "shida", "noeul"];
        foreach (var model in OpenAiVoiceCatalog.GetModels())
        {
            var expected = model.Api == OpenAiVoiceApi.Live ? live : realtime;
            if (!model.Voices.Select(voice => voice.Id).SequenceEqual(expected)) throw new Exception("Wrong roster for " + model.Id);
            if (model.Voices.Any(voice => voice.Id != voice.Voice.ToApiString() || string.IsNullOrWhiteSpace(voice.DisplayName)))
                throw new Exception("Incomplete public voice metadata");
            try { ((IList<OpenAiVoiceInfo>)model.Voices).Clear(); throw new Exception("Mutable public roster"); }
            catch (NotSupportedException) { }
        }
        if (OpenAiVoice.Nuri.GetDisplayName() != "Nuri · German" ||
            !OpenAiVoiceCatalog.IsSupported("gpt-live-1", OpenAiVoice.Nuri) ||
            OpenAiVoiceCatalog.IsSupported("gpt-realtime-2.1", OpenAiVoice.Nuri) ||
            OpenAiVoiceCatalog.IsSupported("gpt-live-1", OpenAiVoice.Cedar)) throw new Exception("Cross-model compatibility is wrong");
        Reject<ArgumentException>(() => OpenAiVoiceCatalog.Validate("gpt-realtime-2.1", OpenAiVoice.Nuri));
        Reject<ArgumentException>(() => OpenAiVoiceCatalog.Validate("gpt-live-1", OpenAiVoice.Echo));
        Reject<ArgumentOutOfRangeException>(() => OpenAiVoiceCatalog.Validate("gpt-live-1", (OpenAiVoice)(-1)));
        Reject<NotSupportedException>(() => OpenAiVoiceCatalog.GetVoices("gpt-live-1.1"));
        Reject<NotSupportedException>(() => OpenAiVoiceCatalog.GetVoices("gpt-realtime-2.1-unknown"));
        if (OpenAiVoiceCatalog.IsSupported("gpt-live-1.1", OpenAiVoice.Marin)) throw new Exception("Unknown model acquired an inferred roster");
        var liveSettings = new OpenAiLiveSettings { Voice = OpenAiVoice.Nuri };
        liveSettings.ValidateVoice();
        if (liveSettings.GetAvailableVoices().Count != 22) throw new Exception("Live settings do not expose their model voices");
        var realtimeSettings = new OpenAiVoiceSettings { Voice = OpenAiVoice.Cedar };
        realtimeSettings.ValidateVoice();
        if (realtimeSettings.GetAvailableVoices().Count != 10) throw new Exception("Realtime settings do not expose their model voices");
        realtimeSettings.ModelId = "team/realtime";
        Reject<NotSupportedException>(realtimeSettings.ValidateVoice);
        realtimeSettings.VoiceModelId = "gpt-realtime-2.1";
        realtimeSettings.ValidateVoice();
        if (realtimeSettings.GetModelId() != "team/realtime" || realtimeSettings.GetVoiceModelId() != "gpt-realtime-2.1")
            throw new Exception("Gateway routing was overwritten by its voice capability model");
        realtimeSettings.ModelId = "gpt-live-1";
        Reject<ArgumentException>(realtimeSettings.ValidateVoice);
        liveSettings.ModelId = "team/live"; liveSettings.VoiceModelId = "gpt-live-1";
        liveSettings.ValidateVoice();
        liveSettings.ModelId = "gpt-realtime-2.1";
        Reject<ArgumentException>(liveSettings.ValidateVoice);
        // Direct WebRTC registry uses exactly the same validation before producing a server payload.
        var type = typeof(OpenAiDirectRealtimeOptions).Assembly.GetType("Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore.OpenAiDirectRealtimeSessionRegistry")!;
        var registry = Activator.CreateInstance(type, new OpenAiDirectRealtimeOptions())!;
        try { type.GetMethod("BuildSessionConfig")!.Invoke(registry, [new OpenAiVoiceSettings { Voice = OpenAiVoice.Nuri }]); throw new Exception("WebRTC accepted a Live-only voice"); }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { }
        Console.WriteLine("OpenAI model/voice catalog passed: exact rosters, display/API names, immutable lists, settings discovery, aliases and cross-model preflight rejection.");
    }

    public static async Task VerifyPreflightAsync()
    {
        await using var realtime = new OpenAiVoiceProvider("contract-no-network");
        try { await realtime.ConnectAsync(new OpenAiVoiceSettings { Voice = OpenAiVoice.Nuri }); throw new Exception("Invalid Realtime voice reached network code"); }
        catch (ArgumentException) { }
        await using var live = new OpenAiLiveProvider("contract-no-network");
        try { await live.ConnectAsync(new OpenAiLiveSettings { Voice = OpenAiVoice.Echo }); throw new Exception("Invalid Live voice reached network code"); }
        catch (ArgumentException) { }
        try { await realtime.UpdateSettingsAsync(new OpenAiVoiceSettings { Voice = OpenAiVoice.Flitz }); throw new Exception("Invalid update was accepted"); }
        catch (ArgumentException) { }
        Console.WriteLine("Invalid model/voice combinations rejected before WebSocket/HTTP creation and settings update.");
    }

    private static void Reject<T>(Action action) where T : Exception
    { try { action(); throw new Exception("Expected " + typeof(T).Name); } catch (T) { } }
}
