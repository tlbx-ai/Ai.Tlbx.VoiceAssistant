namespace Ai.Tlbx.VoiceAssistant.Interfaces;

/// <summary>Optional capability for an explicit first assistant turn using the existing session context.</summary>
public interface IConversationStartProvider
{
    /// <summary>Whether the configured session currently accepts a first-turn request. This is a snapshot, not a reservation.</summary>
    bool IsConversationStartReady { get; }

    /// <summary>
    /// Requests one first assistant turn per connection. Returns the transport request ID after sending,
    /// not after server acceptance, generation or playback. Busy/not-ready/repeated requests are rejected.
    /// Cancellation after sending does not retract the response; use the normal interrupt/stop path.
    /// Never retry an uncertain transport outcome automatically.
    /// </summary>
    Task<string> StartConversationAsync(CancellationToken cancellationToken = default);
}
