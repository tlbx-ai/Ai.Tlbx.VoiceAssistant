using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Interfaces;

/// <summary>
/// Optional provider capability for speaker-, channel-, and timestamp-aware transcription data.
/// </summary>
public interface IStructuredTranscriptionProvider
{
    Action<StructuredTranscript>? OnStructuredTranscriptionReceived { get; set; }
}
