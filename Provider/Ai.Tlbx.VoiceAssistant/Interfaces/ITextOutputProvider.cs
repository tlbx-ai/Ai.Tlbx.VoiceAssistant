namespace Ai.Tlbx.VoiceAssistant.Interfaces;

/// <summary>Optional streaming text output. Final text arrives through OnMessageReceived.</summary>
public interface ITextOutputProvider
{
    Action<string>? OnTextDelta { get; set; }
}
