using System;
using System.Collections.Generic;
using System.Linq;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

/// <summary>The API used by a documented OpenAI voice model. Backend text models have no voice catalog.</summary>
public enum OpenAiVoiceApi { Realtime, Live }

/// <summary>A built-in voice with its exact API ID and documented display information.</summary>
public sealed record OpenAiVoiceInfo(OpenAiVoice Voice, string Id, string Name,
    string? Language = null, string? RegionalInfluence = null)
{
    public string DisplayName => Language == null ? Name : Name + " · " + Language
        + (RegionalInfluence == null ? "" : " (" + RegionalInfluence + ")");
}

/// <summary>A model and the built-in voices documented for its API. This is not an account-access guarantee.</summary>
public sealed record OpenAiVoiceModelInfo(string Id, string Name, OpenAiVoiceApi Api,
    IReadOnlyList<OpenAiVoiceInfo> Voices);

/// <summary>Offline model-to-voice discovery, also used by settings validation and the demo.
/// Unknown models, future versions and deprecated previews with no verified roster are never inferred from a prefix.</summary>
public static class OpenAiVoiceCatalog
{
    // Documented roster: https://developers.openai.com/api/docs/guides/realtime-conversations#voice-options
    // Live additions: https://developers.openai.com/api/docs/guides/live-conversations#voice-options
    private static readonly OpenAiVoiceInfo[] All = CreateVoices();
    private static readonly IReadOnlyList<OpenAiVoiceInfo> Realtime = VoiceSet(OpenAiVoice.Alloy, OpenAiVoice.Ash, OpenAiVoice.Ballad, OpenAiVoice.Coral, OpenAiVoice.Echo, OpenAiVoice.Sage, OpenAiVoice.Shimmer, OpenAiVoice.Verse, OpenAiVoice.Marin, OpenAiVoice.Cedar);
    private static readonly IReadOnlyList<OpenAiVoiceInfo> Live = VoiceSet(OpenAiVoice.Marin, OpenAiVoice.Quartz, OpenAiVoice.Ripple, OpenAiVoice.Vesper, OpenAiVoice.Willow, OpenAiVoice.Stone, OpenAiVoice.Gleam, OpenAiVoice.Meridian, OpenAiVoice.Bossa, OpenAiVoice.Tempo, OpenAiVoice.Beacon, OpenAiVoice.Delta, OpenAiVoice.Cinder, OpenAiVoice.Nuri, OpenAiVoice.Juni, OpenAiVoice.Flitz, OpenAiVoice.Brise, OpenAiVoice.Sillage, OpenAiVoice.Nira, OpenAiVoice.Harema, OpenAiVoice.Shida, OpenAiVoice.Noeul);
    private static readonly IReadOnlyList<OpenAiVoiceModelInfo> Models = Array.AsReadOnly(new[] {
        new OpenAiVoiceModelInfo("gpt-realtime-2.1", "GPT Realtime 2.1", OpenAiVoiceApi.Realtime, Realtime),
        new OpenAiVoiceModelInfo("gpt-realtime-2.1-mini", "GPT Realtime 2.1 mini", OpenAiVoiceApi.Realtime, Realtime),
        new OpenAiVoiceModelInfo("gpt-realtime-2", "GPT Realtime 2", OpenAiVoiceApi.Realtime, Realtime),
        new OpenAiVoiceModelInfo("gpt-realtime-1.5", "GPT Realtime 1.5", OpenAiVoiceApi.Realtime, Realtime),
        new OpenAiVoiceModelInfo("gpt-realtime", "GPT Realtime (alias)", OpenAiVoiceApi.Realtime, Realtime),
        new OpenAiVoiceModelInfo("gpt-realtime-mini", "GPT Realtime mini (legacy)", OpenAiVoiceApi.Realtime, Realtime),
        new OpenAiVoiceModelInfo("gpt-realtime-2025-08-28", "GPT Realtime (2025-08-28)", OpenAiVoiceApi.Realtime, Realtime),
        new OpenAiVoiceModelInfo("gpt-live-1", "GPT-Live 1", OpenAiVoiceApi.Live, Live)
    });

    /// <summary>Models with a documented built-in voice roster, including their API kind and display name.</summary>
    public static IReadOnlyList<OpenAiVoiceModelInfo> GetModels() => Models;
    public static bool IsKnownModel(string? modelId) => Models.Any(model => model.Id == modelId);
    public static OpenAiVoiceModelInfo GetModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return Models.FirstOrDefault(model => model.Id == modelId)
            ?? throw new NotSupportedException($"No verified OpenAI voice catalog for model '{modelId}'. For a gateway alias, set VoiceModelId to its documented underlying model.");
    }
    public static IReadOnlyList<OpenAiVoiceInfo> GetVoices(string modelId) => GetModel(modelId).Voices;
    public static IReadOnlyList<OpenAiVoiceInfo> GetVoices(OpenAiRealtimeModel model) => GetVoices(model.ToApiString());
    public static OpenAiVoiceInfo GetVoice(OpenAiVoice voice) => All.FirstOrDefault(item => item.Voice == voice)
        ?? throw new ArgumentOutOfRangeException(nameof(voice), voice, "Unknown OpenAI voice.");
    /// <summary>False for unknown models or a voice outside the model's roster.</summary>
    public static bool IsSupported(string modelId, OpenAiVoice voice) =>
        Models.FirstOrDefault(model => model.Id == modelId)?.Voices.Any(item => item.Voice == voice) == true;
    public static void Validate(string modelId, OpenAiVoice voice)
    {
        var model = GetModel(modelId);
        var info = GetVoice(voice);
        if (!model.Voices.Any(item => item.Voice == voice))
            throw new ArgumentException($"Voice '{info.Id}' is not supported by '{modelId}'. Use OpenAiVoiceCatalog.GetVoices(modelId) to select a supported voice.", nameof(voice));
    }

    internal static string ResolveModel(string modelId, string? voiceModelId, OpenAiVoiceApi expectedApi)
    {
        var known = Models.FirstOrDefault(model => model.Id == modelId);
        if (known != null && !string.IsNullOrWhiteSpace(voiceModelId) && voiceModelId != modelId)
            throw new ArgumentException("VoiceModelId is for unknown gateway aliases; it cannot override a known model's voice roster.", nameof(voiceModelId));
        var model = known ?? GetModel(string.IsNullOrWhiteSpace(voiceModelId) ? modelId : voiceModelId);
        if (model.Api != expectedApi)
            throw new ArgumentException($"Model '{modelId}' uses {model.Api}; these settings require {expectedApi}.", nameof(modelId));
        return model.Id;
    }

    private static IReadOnlyList<OpenAiVoiceInfo> VoiceSet(params OpenAiVoice[] voices) => Array.AsReadOnly(voices.Select(GetVoice).ToArray());

    private static OpenAiVoiceInfo[] CreateVoices() => [
        new(OpenAiVoice.Alloy, "alloy", "Alloy"),
        new(OpenAiVoice.Ash, "ash", "Ash"),
        new(OpenAiVoice.Ballad, "ballad", "Ballad"),
        new(OpenAiVoice.Coral, "coral", "Coral"),
        new(OpenAiVoice.Echo, "echo", "Echo"),
        new(OpenAiVoice.Sage, "sage", "Sage"),
        new(OpenAiVoice.Shimmer, "shimmer", "Shimmer"),
        new(OpenAiVoice.Verse, "verse", "Verse"),
        new(OpenAiVoice.Marin, "marin", "Marin"),
        new(OpenAiVoice.Cedar, "cedar", "Cedar"),
        new(OpenAiVoice.Quartz, "quartz", "Quartz", "English", "Australian"),
        new(OpenAiVoice.Ripple, "ripple", "Ripple", "English", "Australian"),
        new(OpenAiVoice.Vesper, "vesper", "Vesper", "English", "British"),
        new(OpenAiVoice.Willow, "willow", "Willow", "English", "Irish"),
        new(OpenAiVoice.Stone, "stone", "Stone", "English", "Irish"),
        new(OpenAiVoice.Gleam, "gleam", "Gleam", "English", "North American"),
        new(OpenAiVoice.Meridian, "meridian", "Meridian", "English", "North American"),
        new(OpenAiVoice.Bossa, "bossa", "Bossa", "Portuguese", "Brazilian"),
        new(OpenAiVoice.Tempo, "tempo", "Tempo", "Portuguese", "Brazilian"),
        new(OpenAiVoice.Beacon, "beacon", "Beacon", "English", "Filipino"),
        new(OpenAiVoice.Delta, "delta", "Delta", "English", "Southern U.S."),
        new(OpenAiVoice.Cinder, "cinder", "Cinder", "English", "Southern U.S."),
        new(OpenAiVoice.Nuri, "nuri", "Nuri", "German"),
        new(OpenAiVoice.Juni, "juni", "Juni", "German"),
        new(OpenAiVoice.Flitz, "flitz", "Flitz", "German"),
        new(OpenAiVoice.Brise, "brise", "Brise", "French"),
        new(OpenAiVoice.Sillage, "sillage", "Sillage", "French"),
        new(OpenAiVoice.Nira, "nira", "Nira", "Hindi"),
        new(OpenAiVoice.Harema, "harema", "Harema", "Japanese"),
        new(OpenAiVoice.Shida, "shida", "Shida", "Japanese"),
        new(OpenAiVoice.Noeul, "noeul", "Noeul", "Korean"),
    ];
}

public static class OpenAiVoiceExtensions
{
    public static string ToApiString(this OpenAiVoice voice) => OpenAiVoiceCatalog.GetVoice(voice).Id;
    public static string GetDisplayName(this OpenAiVoice voice) => OpenAiVoiceCatalog.GetVoice(voice).DisplayName;
}
