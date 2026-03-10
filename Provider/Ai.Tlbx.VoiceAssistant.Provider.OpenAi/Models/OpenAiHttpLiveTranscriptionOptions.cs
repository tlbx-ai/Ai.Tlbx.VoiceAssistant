using System;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models
{
    /// <summary>
    /// Options for near-live microphone transcription over OpenAI's HTTP transcription API.
    /// This uses short repeated uploads rather than the realtime WebSocket API.
    /// </summary>
    public sealed class OpenAiHttpLiveTranscriptionOptions
    {
        /// <summary>
        /// The transcription model to use. Defaults to the faster mini model.
        /// </summary>
        public OpenAiTranscriptionModel TranscriptionModel { get; set; } = OpenAiTranscriptionModel.Gpt4oMiniTranscribe;

        /// <summary>
        /// Optional spoken language hint (for example "en" or "de").
        /// </summary>
        public string? Language { get; set; }

        /// <summary>
        /// Optional transcription prompt to bias recognition.
        /// </summary>
        public string? Prompt { get; set; }

        /// <summary>
        /// How often a growing utterance snapshot should be uploaded while speech is active.
        /// Lower values reduce latency but increase request volume.
        /// </summary>
        public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromMilliseconds(700);

        /// <summary>
        /// How much audio at the very beginning to trim from each upload.
        /// Useful for suppressing button click noise in push-to-talk scenarios.
        /// </summary>
        public TimeSpan LeadingTrimDuration { get; set; } = TimeSpan.FromMilliseconds(120);

        /// <summary>
        /// Amount of audio to include before server VAD detects speech.
        /// </summary>
        public TimeSpan PrefixPadding { get; set; } = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Silence required for server VAD to close a segment.
        /// Lower values make text appear earlier but can cut on short pauses.
        /// </summary>
        public TimeSpan SilenceDuration { get; set; } = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// Server VAD sensitivity threshold from 0.0 to 1.0.
        /// Higher values require louder speech to trigger.
        /// </summary>
        public double VadThreshold { get; set; } = 0.3;

        /// <summary>
        /// Minimum utterance duration before snapshots are uploaded.
        /// Helps avoid excessive requests for very short noises.
        /// </summary>
        public TimeSpan MinimumUtteranceDuration { get; set; } = TimeSpan.FromMilliseconds(350);
    }
}
