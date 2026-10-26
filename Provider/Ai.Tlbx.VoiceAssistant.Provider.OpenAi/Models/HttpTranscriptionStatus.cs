using System;
using System.Collections.Generic;
namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

public enum HttpTranscriptionState { Recording, Finalizing, Completed, Partial, Failed }

public sealed record TranscriptionAudioRange(TimeSpan Start, TimeSpan End);

/// <summary>Session completion and confirmed audio coverage are separate facts.</summary>
public sealed class HttpTranscriptionStatus
{
    public HttpTranscriptionState State { get; init; }
    public string Text { get; init; } = string.Empty;
    public TimeSpan RecordedAudioEnd { get; init; }
    public TimeSpan ConfirmedAudioEnd { get; init; }
    public bool CaptureStopConfirmed { get; init; }
    public IReadOnlyList<TranscriptionAudioRange> MissingSections { get; init; } = Array.Empty<TranscriptionAudioRange>();
    public bool IsComplete => State == HttpTranscriptionState.Completed && CaptureStopConfirmed && MissingSections.Count == 0;
}

/// <summary>Metadata only; messages exclude provider bodies and conversation text.</summary>
public sealed class HttpTranscriptionError
{
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool IsFatal { get; init; }
    public bool WillRetry { get; init; }
    public int Attempt { get; init; }
    public long SectionId { get; init; }
    public TimeSpan AudioStart { get; init; }
    public TimeSpan AudioEnd { get; init; }
    public int? HttpStatus { get; init; }
    public TimeSpan? RetryAfter { get; init; }
    public string? ProviderRequestId { get; init; }
}
