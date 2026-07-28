namespace Ai.Tlbx.VoiceAssistant.Models
{
    /// <summary>
    /// Identifies the provider operation that generated usage.
    /// </summary>
    public enum UsageOperationType
    {
        /// <summary>
        /// The provider did not identify a more specific operation.
        /// </summary>
        Unknown,

        /// <summary>
        /// A conversational voice model response.
        /// </summary>
        VoiceResponse,

        /// <summary>
        /// Input audio transcription attached to a voice conversation.
        /// </summary>
        InputTranscription,

        /// <summary>
        /// A dedicated streaming or HTTP transcription operation.
        /// </summary>
        Transcription
    }

    /// <summary>
    /// Describes where the metering values in a report came from.
    /// </summary>
    public enum UsageMeasurementSource
    {
        /// <summary>
        /// Usage was returned by the provider.
        /// </summary>
        ProviderReported,

        /// <summary>
        /// Usage was measured from media or events sent and received by the client.
        /// </summary>
        ClientMeasured,

        /// <summary>
        /// The report combines provider-reported and client-measured values.
        /// </summary>
        Mixed
    }

    /// <summary>
    /// Represents provider-reported and client-measured usage for a voice or
    /// transcription operation.
    /// </summary>
    public sealed class UsageReport
    {
        /// <summary>
        /// Identifier of the provider that generated this report (openai, xai, google).
        /// </summary>
        public string ProviderId { get; init; } = string.Empty;

        /// <summary>
        /// Provider model identifier used for this operation, when known.
        /// </summary>
        public string? ModelId { get; init; }

        /// <summary>
        /// Provider response, item, or request identifier, when available.
        /// </summary>
        public string? OperationId { get; init; }

        /// <summary>
        /// Provider operation that generated the usage.
        /// </summary>
        public UsageOperationType OperationType { get; init; } = UsageOperationType.Unknown;

        /// <summary>
        /// Origin of the metering values in this report.
        /// </summary>
        public UsageMeasurementSource MeasurementSource { get; init; } = UsageMeasurementSource.ProviderReported;

        /// <summary>
        /// Timestamp when the usage report was created.
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Number of text input tokens consumed.
        /// </summary>
        public int? InputTokens { get; init; }

        /// <summary>
        /// Number of text output tokens generated.
        /// </summary>
        public int? OutputTokens { get; init; }

        /// <summary>
        /// Number of audio input tokens consumed.
        /// </summary>
        public int? InputAudioTokens { get; init; }

        /// <summary>
        /// Number of audio output tokens generated.
        /// </summary>
        public int? OutputAudioTokens { get; init; }

        /// <summary>
        /// Number of image input tokens consumed.
        /// </summary>
        public int? InputImageTokens { get; init; }

        /// <summary>
        /// Number of image output tokens generated.
        /// </summary>
        public int? OutputImageTokens { get; init; }

        /// <summary>
        /// Number of input tokens attributed to tool use.
        /// </summary>
        public int? ToolUseInputTokens { get; init; }

        /// <summary>
        /// Number of output tokens attributed to model reasoning or thoughts.
        /// </summary>
        public int? ReasoningOutputTokens { get; init; }

        /// <summary>
        /// Number of tokens used for cache creation (OpenAI-specific billing modifier).
        /// Cache tokens are a subset of input tokens and are not added to TotalInputTokens.
        /// </summary>
        public int? CacheCreationInputTokens { get; init; }

        /// <summary>
        /// Number of cached tokens read from cache (OpenAI-specific billing modifier).
        /// Cache tokens are a subset of input tokens and are not added to TotalInputTokens.
        /// </summary>
        public int? CacheReadInputTokens { get; init; }

        /// <summary>
        /// Cached text input tokens, when the provider reports a modality breakdown.
        /// This is a subset of <see cref="CacheReadInputTokens"/>.
        /// </summary>
        public int? CachedTextInputTokens { get; init; }

        /// <summary>
        /// Cached audio input tokens, when the provider reports a modality breakdown.
        /// This is a subset of <see cref="CacheReadInputTokens"/>.
        /// </summary>
        public int? CachedAudioInputTokens { get; init; }

        /// <summary>
        /// Cached image input tokens, when the provider reports a modality breakdown.
        /// This is a subset of <see cref="CacheReadInputTokens"/>.
        /// </summary>
        public int? CachedImageInputTokens { get; init; }

        /// <summary>
        /// Provider-reported total input tokens. When present, this is authoritative
        /// and prevents modality details from being counted twice.
        /// </summary>
        public int? ReportedInputTokens { get; init; }

        /// <summary>
        /// Provider-reported total output tokens. When present, this is authoritative
        /// and prevents modality details from being counted twice.
        /// </summary>
        public int? ReportedOutputTokens { get; init; }

        /// <summary>
        /// Provider-reported total tokens across input and output. This can include
        /// provider-specific dimensions such as reasoning or tool-use tokens.
        /// </summary>
        public int? ReportedTotalTokens { get; init; }

        /// <summary>
        /// Total input tokens. Uses the provider total when available, otherwise
        /// adds the known text, audio, image, and tool-use components.
        /// </summary>
        public int TotalInputTokens =>
            ReportedInputTokens ??
            ((InputTokens ?? 0) +
             (InputAudioTokens ?? 0) +
             (InputImageTokens ?? 0) +
             (ToolUseInputTokens ?? 0));

        /// <summary>
        /// Total output tokens. Uses the provider total when available, otherwise
        /// adds the known text, audio, image, and reasoning components.
        /// </summary>
        public int TotalOutputTokens =>
            ReportedOutputTokens ??
            ((OutputTokens ?? 0) +
             (OutputAudioTokens ?? 0) +
             (OutputImageTokens ?? 0) +
             (ReasoningOutputTokens ?? 0));

        /// <summary>
        /// Total tokens consumed (input + output).
        /// </summary>
        public int TotalTokens => ReportedTotalTokens ?? (TotalInputTokens + TotalOutputTokens);

        /// <summary>
        /// Indicates whether any billable dimension was derived from client
        /// measurement rather than returned directly by the provider.
        /// </summary>
        public bool IsEstimated { get; init; }

        /// <summary>
        /// Duration of input audio used for estimation purposes.
        /// </summary>
        public TimeSpan? InputAudioDuration { get; init; }

        /// <summary>
        /// Duration of output audio used for estimation purposes.
        /// </summary>
        public TimeSpan? OutputAudioDuration { get; init; }

        /// <summary>
        /// Number of billable text conversation-item events sent to the provider.
        /// xAI Speech-to-Speech bills these events independently of audio duration.
        /// </summary>
        public int? BillableTextInputEvents { get; init; }

        /// <summary>
        /// Original provider usage object for auditability and forward compatibility.
        /// </summary>
        public string? RawProviderUsageJson { get; init; }

        /// <summary>
        /// Whether this report contains any token, duration, or event usage.
        /// </summary>
        public bool HasUsage =>
            TotalTokens > 0 ||
            InputAudioDuration > TimeSpan.Zero ||
            OutputAudioDuration > TimeSpan.Zero ||
            BillableTextInputEvents > 0;
    }
}
