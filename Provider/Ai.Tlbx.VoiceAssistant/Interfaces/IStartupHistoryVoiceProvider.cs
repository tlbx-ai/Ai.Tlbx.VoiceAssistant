using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Interfaces;

/// <summary>A provider whose history must be supplied atomically with session creation.</summary>
public interface IStartupHistoryVoiceProvider
{
    void SetStartupHistory(IEnumerable<ChatMessage> messages);
}
