using System.Text;

namespace Ai.Tlbx.VoiceAssistant.Models;

/// <summary>
/// Provider-neutral transcription data with speaker, channel, timing, and turn metadata.
/// Providers populate the fields they support and leave unavailable metadata null.
/// </summary>
public sealed class StructuredTranscript
{
    public string ProviderId { get; init; } = string.Empty;
    public string? ModelId { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? Language { get; init; }
    public TimeSpan? Duration { get; init; }
    public bool IsFinal { get; init; }
    public bool IsSpeechFinal { get; init; }
    public double? EndOfTurnConfidence { get; init; }
    public IReadOnlyList<TranscriptSegment> Segments { get; init; } = Array.Empty<TranscriptSegment>();

    /// <summary>
    /// Renders a readable transcript while preserving speaker/channel boundaries.
    /// </summary>
    public string ToSpeakerLabeledText()
    {
        if (Segments.Count == 0)
        {
            return Text;
        }

        var result = new StringBuilder();
        foreach (var segment in Segments)
        {
            if (result.Length > 0)
            {
                result.AppendLine();
            }

            var label = segment.Speaker;
            if (string.IsNullOrWhiteSpace(label) && segment.ChannelIndex.HasValue)
            {
                label = $"channel-{segment.ChannelIndex.Value}";
            }

            if (!string.IsNullOrWhiteSpace(label))
            {
                result.Append('[').Append(label).Append("] ");
            }

            result.Append(segment.Text);
        }

        return result.ToString();
    }
}

public sealed class TranscriptSegment
{
    public string Text { get; init; } = string.Empty;
    public string? Speaker { get; init; }
    public int? ChannelIndex { get; init; }
    public TimeSpan? Start { get; init; }
    public TimeSpan? End { get; init; }
    public IReadOnlyList<TranscriptWord> Words { get; init; } = Array.Empty<TranscriptWord>();
}

public sealed class TranscriptWord
{
    public string Text { get; init; } = string.Empty;
    public string? Speaker { get; init; }
    public TimeSpan? Start { get; init; }
    public TimeSpan? End { get; init; }
}
