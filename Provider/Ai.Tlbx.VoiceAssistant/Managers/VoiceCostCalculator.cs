using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Managers;

/// <summary>Calculates model/media cost from usage, without counting cached or reasoning tokens twice.</summary>
public static class VoiceCostCalculator
{
    public static VoiceCostEstimate Estimate(UsageReport usage, VoiceCostRates? rates)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (rates == null) return new(0, false, "No verified price schedule for this model and service tier.");
        decimal cost = 0;
        var complete = usage.HasUsage;
        void Charge(decimal amount, decimal? price)
        {
            if (amount < 0 || price < 0) throw new ArgumentOutOfRangeException(nameof(usage), "Usage and rates must be nonnegative.");
            if (amount == 0) return;
            if (!price.HasValue) { complete = false; return; }
            cost += amount * price.Value;
        }
        void Tokens(int count, decimal? price) => Charge(count / 1_000_000m, price);
        void Minutes(TimeSpan? duration, decimal? price)
        {
            if (!duration.HasValue || duration.Value == TimeSpan.Zero) return;
            if (duration.Value < TimeSpan.Zero || price < 0) throw new ArgumentOutOfRangeException(nameof(usage));
            if (price == null) { complete = false; return; }
            cost += duration.Value.Ticks * price.Value / TimeSpan.TicksPerMinute;
        }
        if ((usage.OperationType == UsageOperationType.VoiceSession || usage.BillingMode == VoiceBillingMode.SessionDuration) && rates.SessionMinute.HasValue)
        {
            if (usage.OperationType == UsageOperationType.VoiceSession && usage.SessionDuration == null) complete = false;
            Minutes(usage.SessionDuration, rates.SessionMinute);
            Charge(usage.BillableTextInputEvents ?? 0, rates.TextInputEvent);
        }
        else if (rates.InputAudioMinute.HasValue || rates.OutputAudioMinute.HasValue)
        {
            // Duration-priced audio is independent of optional token diagnostics.
            Minutes(usage.InputAudioDuration, rates.InputAudioMinute);
            Minutes(usage.OutputAudioDuration, rates.OutputAudioMinute);
            Charge(usage.BillableTextInputEvents ?? 0, rates.TextInputEvent);
            if (usage.InputAudioDuration == null && usage.OutputAudioDuration == null && usage.BillableTextInputEvents == null)
                complete = false;
        }
        else
        {
            if (usage.OperationType == UsageOperationType.VoiceResponse &&
                (rates.AudioInput.HasValue || rates.AudioOutput.HasValue) &&
                ((usage.TotalInputTokens > 0 && !usage.InputAudioTokens.HasValue) ||
                 (usage.TotalOutputTokens > 0 && !usage.OutputAudioTokens.HasValue)))
                return new(0, false, "Voice token usage omitted its audio/text breakdown; no modality prices were guessed.");
            var audioIn = usage.InputAudioTokens ?? 0;
            var imageIn = usage.InputImageTokens ?? 0;
            var textIn = usage.OperationType == UsageOperationType.VoiceResponse
                ? usage.InputTokens ?? 0 : Math.Max(0, usage.TotalInputTokens - audioIn - imageIn);
            var audioOut = usage.OutputAudioTokens ?? 0;
            var imageOut = usage.OutputImageTokens ?? 0;
            var textOut = usage.OperationType == UsageOperationType.VoiceResponse
                ? (usage.OutputTokens ?? 0) + (usage.ReasoningOutputTokens ?? 0)
                : Math.Max(0, usage.TotalOutputTokens - audioOut - imageOut);
            if (textIn + audioIn + imageIn != usage.TotalInputTokens || textOut + audioOut + imageOut != usage.TotalOutputTokens)
                complete = false; // Preserve unclassified provider tokens as a price gap, not invented text tokens.
            // Diagnostic reasoning counts can overlap a provider's text count. Never exceed its authoritative output total.
            textOut = Math.Min(textOut, Math.Max(0, usage.TotalOutputTokens - audioOut - imageOut));
            var cachedAudio = usage.CachedAudioInputTokens ?? 0;
            var cachedImage = usage.CachedImageInputTokens ?? 0;
            var cachedText = usage.CachedTextInputTokens ?? Math.Max(0, (usage.CacheReadInputTokens ?? 0) - cachedAudio - cachedImage);
            if ((usage.CacheReadInputTokens ?? 0) > 0 && (audioIn > 0 || imageIn > 0) &&
                usage.CachedTextInputTokens == null && usage.CachedAudioInputTokens == null && usage.CachedImageInputTokens == null)
                return new(0, false, "Cached voice tokens omitted their modality breakdown; no cache allocation was guessed.");
            var writes = usage.CacheCreationInputTokens ?? 0;
            if (cachedText + writes > textIn || cachedAudio > audioIn || cachedImage > imageIn) complete = false;
            Tokens(Math.Max(0, textIn - cachedText - writes), rates.TextInput);
            Tokens(cachedText, rates.CachedTextInput);
            Tokens(writes, rates.CacheWrite);
            Tokens(Math.Max(0, audioIn - cachedAudio), rates.AudioInput);
            Tokens(cachedAudio, rates.CachedAudioInput);
            Tokens(Math.Max(0, imageIn - cachedImage), rates.ImageInput);
            Tokens(cachedImage, rates.CachedImageInput);
            Tokens(textOut, rates.TextOutput);
            Tokens(audioOut, rates.AudioOutput);
            Tokens(imageOut, rates.ImageOutput);
            if (usage.TotalTokens == 0 && (usage.InputAudioDuration > TimeSpan.Zero || usage.OutputAudioDuration > TimeSpan.Zero)) complete = false;
        }
        return new(decimal.Round(cost, 12), complete, complete ? null : "Some usage dimensions or prices are missing; known cost is a partial estimate.");
    }

    /// <summary>Verified public rates on 2026-10-17. Unknown snapshots and processing tiers require explicit rates.
    /// The schedule does not infer prices for GPT-Live 1.1 before API pricing is published.</summary>
    public static VoiceCostRates? GetRates(UsageReport usage)
    {
        var model = usage.ModelId?.Replace("models/", "", StringComparison.Ordinal);
        const string openai = "https://developers.openai.com/api/docs/pricing";
        VoiceCostRates? rates = (usage.ProviderId, model) switch
        {
            ("openai", "gpt-live-1") => new() { SessionMinute = 0.05m, SourceUrl = openai },
            ("openai", "gpt-realtime-2.1") => new() { TextInput = 4, CachedTextInput = .4m, TextOutput = 24, AudioInput = 32, CachedAudioInput = .4m, AudioOutput = 64, ImageInput = 5, CachedImageInput = .5m, SourceUrl = openai },
            ("openai", "gpt-realtime-2.1-mini") => new() { TextInput = .6m, CachedTextInput = .06m, TextOutput = 2.4m, AudioInput = 10, CachedAudioInput = .3m, AudioOutput = 20, ImageInput = .8m, CachedImageInput = .08m, SourceUrl = openai },
            ("openai", "gpt-6.1-sol") when usage.TotalInputTokens <= 272000 => new() { TextInput = 2, CachedTextInput = .1m, CacheWrite = 2.5m, TextOutput = 10, SourceUrl = openai },
            ("openai", "gpt-6.1-sol") => new() { TextInput = 4, CachedTextInput = .2m, CacheWrite = 5, TextOutput = 15, SourceUrl = openai },
            ("openai", "gpt-6-luna") when usage.TotalInputTokens <= 272000 => new() { TextInput = .1m, CachedTextInput = .01m, CacheWrite = .125m, TextOutput = .5m, SourceUrl = openai },
            ("openai", "gpt-6-luna") => new() { TextInput = .2m, CachedTextInput = .02m, CacheWrite = .25m, TextOutput = .75m, SourceUrl = openai },
            ("openai", "gpt-6-astra") when usage.TotalInputTokens <= 272000 => new() { TextInput = 10, CachedTextInput = 1, CacheWrite = 12.5m, TextOutput = 50, SourceUrl = openai },
            ("openai", "gpt-6-astra") => new() { TextInput = 20, CachedTextInput = 2, CacheWrite = 25, TextOutput = 75, SourceUrl = openai },
            ("google", "gemini-3.8-live" or "gemini-3.8-live-extended-thinking" or "gemini-3.1-flash-live-preview") => new() { TextInput = .75m, TextOutput = 4.5m, AudioInput = 3, AudioOutput = 12, ImageInput = 1, SourceUrl = "https://ai.google.dev/gemini-api/docs/pricing" },
            ("google", "gemini-2.5-flash-native-audio-latest" or "gemini-2.5-flash-native-audio-preview-12-2025") => new() { TextInput = .5m, TextOutput = 2, AudioInput = 3, AudioOutput = 12, ImageInput = 3, SourceUrl = "https://ai.google.dev/gemini-api/docs/pricing" },
            ("openai", "gpt-live-transcribe" or "gpt-realtime-whisper") => new() { InputAudioMinute = .017m, SourceUrl = openai },
            ("openai", "gpt-transcribe") => new() { InputAudioMinute = .0045m, SourceUrl = openai },
            ("openai", "gpt-realtime-translate") => new() { InputAudioMinute = .034m, SourceUrl = openai },
            ("xai", "grok-transcribe") => new() { InputAudioMinute = .20m / 60, SourceUrl = "https://docs.x.ai/developers/pricing" },
            ("xai", "grok-voice-3.0" or "grok-voice-latest" or "grok-voice-2.0") => new() { SessionMinute = .08m, InputAudioMinute = .08m, OutputAudioMinute = .08m, TextInputEvent = .004m, SourceUrl = "https://docs.x.ai/developers/models/speech-to-speech" },
            _ => null
        };
        if (rates == null) return null;
        var multiplier = usage.ServiceTier switch {
            null or "default" or "standard" => 1m,
            "fast" or "priority" when usage.ProviderId == "openai" && model is "gpt-6.1-sol" or "gpt-6-luna" or "gpt-6-astra" => 2m,
            "flex" when usage.ProviderId == "openai" && model is "gpt-6.1-sol" or "gpt-6-luna" or "gpt-6-astra" => .5m,
            "ultrafast" when usage.ProviderId == "openai" && model is "gpt-6.1-sol" or "gpt-6-astra" => 6m,
            _ => 0m
        };
        if (multiplier == 0) return null;
        return rates with { TextInput = rates.TextInput * multiplier, CachedTextInput = rates.CachedTextInput * multiplier,
            CacheWrite = rates.CacheWrite * multiplier, TextOutput = rates.TextOutput * multiplier, VerifiedOn = "2026-10-17" };
    }
}
