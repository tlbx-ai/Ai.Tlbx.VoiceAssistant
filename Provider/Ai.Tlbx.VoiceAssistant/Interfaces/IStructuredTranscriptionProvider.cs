using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Interfaces;

/// <summary>
/// Optional provider capability for speaker-, channel-, and timestamp-aware transcription data.
/// </summary>
public interface IStructuredTranscriptionProvider
{
    Action<StructuredTranscript>? OnStructuredTranscriptionReceived { get; set; }
}

/// <summary>
/// Optional capability for revision-scoped transcription progress that is not yet an
/// authoritative complete snapshot.
/// </summary>
public interface IStructuredTranscriptionProgressProvider
{
    Action<StructuredTranscript>? OnStructuredTranscriptionProgress { get; set; }
}
