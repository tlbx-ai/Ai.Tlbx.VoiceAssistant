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
    /// <summary>
    /// Provider-native finality. Its exact scope can be an utterance or request; use
    /// <see cref="IsSnapshotComplete"/> and <see cref="IsSessionFinal"/> when consuming
    /// repeated HTTP live-transcription snapshots.
    /// </summary>
    public bool IsFinal { get; init; }
    public bool IsSpeechFinal { get; init; }
    /// <summary>
    /// Monotonically increasing identifier for a provider request within the current session.
    /// Providers that do not use repeated snapshots leave this at zero.
    /// </summary>
    public long SnapshotRevision { get; init; }
    /// <summary>
    /// True when the provider request represented by <see cref="SnapshotRevision"/> completed.
    /// </summary>
    public bool IsSnapshotComplete { get; init; }
    /// <summary>
    /// True only for the authoritative state emitted when the complete live session ends.
    /// </summary>
    public bool IsSessionFinal { get; init; }
    /// <summary>
    /// Start of the audio range covered by this update, relative to the live session.
    /// </summary>
    public TimeSpan? AudioStart { get; init; }
    /// <summary>
    /// End of the audio range covered by this update, relative to the live session.
    /// </summary>
    public TimeSpan? AudioEnd { get; init; }
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
    /// <summary>
    /// Provider segment identifier when one is available. It is scoped to a provider request
    /// and must not be assumed stable across separate snapshot requests.
    /// </summary>
    public string? Id { get; init; }
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
