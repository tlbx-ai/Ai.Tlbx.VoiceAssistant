using Ai.Tlbx.VoiceAssistant.Models;
using System;
using System.Collections.Generic;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models
{
    /// <summary>
    /// Options for near-live microphone transcription over OpenAI's HTTP transcription API.
    /// This uses short repeated uploads rather than the realtime WebSocket API.
    /// </summary>
    public sealed class OpenAiHttpLiveTranscriptionOptions
    {
        /// <summary>Routing and authentication, read on each request or reconnect.</summary>
        public ProviderEndpointOptions Connection { get; set; } = new("https://api.openai.com/v1/audio/transcriptions");

        /// <summary>Exact gateway model/deployment ID. Null uses the built-in model; the enum still selects protocol capabilities.</summary>
        public string? ModelId { get; set; }
        public string GetModelId() => string.IsNullOrWhiteSpace(ModelId) ? TranscriptionModel.ToApiString() : ModelId;

        /// <summary>
        /// The transcription model to use. Defaults to OpenAI's current recommended HTTP transcription model.
        /// </summary>
        public OpenAiTranscriptionModel TranscriptionModel { get; set; } = OpenAiTranscriptionModel.GptTranscribe;

        /// <summary>
        /// Optional spoken language hint (for example "en" or "de").
        /// </summary>
        public string? Language { get; set; }

        /// <summary>
        /// Optional transcription prompt to bias recognition.
        /// </summary>
        public string? Prompt { get; set; }

        /// <summary>Literal domain terms to favor with gpt-transcribe.</summary>
        public List<string> Keywords { get; set; } = new();

        /// <summary>Expected language codes for gpt-transcribe.</summary>
        public List<string> Languages { get; set; } = new();

        /// <summary>
        /// Optional known speaker labels mapped to short audio reference data URLs.
        /// OpenAI accepts at most four references for diarized transcription.
        /// </summary>
        public Dictionary<string, string> KnownSpeakerReferences { get; set; } = new();

        /// <summary>Learn up to four total speaker references from clean, timed segments (2-5 seconds).
        /// References remain in this recording's memory and are sent only to its configured transcription endpoint.</summary>
        public bool EnableAutomaticSpeakerReferences { get; set; } = true;

        /// <summary>Minimum new audio before a diarization upload, capped by MaxSectionDuration. Stop still sends short tails.</summary>
        public TimeSpan DiarizationMinimumSectionDuration { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Include token log probabilities when supported by the selected model and response format.
        /// </summary>
        public bool IncludeLogProbabilities { get; set; }

        /// <summary>
        /// Hard duration limit for one non-overlapping upload; at most five minutes.
        /// </summary>
        public TimeSpan MaxSectionDuration { get; set; } = TimeSpan.FromSeconds(30);
        /// <summary>Maximum pending PCM duration before capture stops with an explicit failure.</summary>
        public TimeSpan MaxBufferedDuration { get; set; } = TimeSpan.FromSeconds(120);
        /// <summary>Deadline for upload, headers and complete response body, per attempt.</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
        /// <summary>Maximum wait for one response-stream read.</summary>
        public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>Total deadline to drain all unconfirmed sections after capture stops.</summary>
        public TimeSpan FinalizationTimeout { get; set; } = TimeSpan.FromSeconds(15);
        /// <summary>Deadline for hardware stop and its final audio-delivery acknowledgment.</summary>
        public TimeSpan HardwareStopTimeout { get; set; } = TimeSpan.FromSeconds(3);
        /// <summary>Retries after the first attempt for network, 429 and 5xx failures.</summary>
        public int MaxRetries { get; set; } = 2;
        /// <summary>Initial exponential retry delay when no Retry-After is supplied.</summary>
        public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>Polling delay for new audio. Full sections wake the loop immediately.</summary>
        public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromMilliseconds(700);

        /// <summary>
        /// Reserved compatibility setting; sectioned transcription preserves all captured audio.
        /// No samples are discarded, so coverage remains contiguous.
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
