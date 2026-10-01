using System.Threading;
using System.Threading.Tasks;

namespace Ai.Tlbx.VoiceAssistant.Interfaces;

/// <summary>Supplies an image URL or data URL with optional user text to a vision-capable conversation.</summary>
public interface IVisualInputProvider
{
    /// <summary>Queue the image; requestResponse starts a response when pending work permits it.</summary>
    Task SendImageAsync(string imageUrl, string? text = null, bool requestResponse = true, CancellationToken cancellationToken = default);
}
