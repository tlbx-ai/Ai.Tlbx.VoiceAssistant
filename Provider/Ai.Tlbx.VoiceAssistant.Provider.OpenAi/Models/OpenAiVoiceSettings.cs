using System;
using System.Collections.Generic;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models
{
    /// <summary>
    /// OpenAI-specific voice assistant settings that control model behavior and configuration.
    /// </summary>
    public class OpenAiVoiceSettings : IVoiceSettings
    {
        /// <summary>Routing and authentication, read on each request or reconnect.</summary>
        public ProviderEndpointOptions Connection { get; set; } = new("wss://api.openai.com/v1/realtime");

        /// <summary>Exact gateway model/deployment ID. Null uses the built-in model; the enum still selects protocol capabilities.</summary>
        public string? ModelId { get; set; }
        public string GetModelId() => string.IsNullOrWhiteSpace(ModelId) ? Model.ToApiString() : ModelId;

        public ProviderEndpointOptions ClientSecretsConnection { get; set; } = new("https://api.openai.com/v1/realtime/client_secrets");
        /// <summary>False connects directly with the API key, for gateways without client_secrets.</summary>
        public bool UseEphemeralKey { get; set; } = true;
        /// <summary>Browser-accessible SDP endpoint. Requires CORS and ephemeral bearer authentication.</summary>
        public string RealtimeCallsEndpoint { get; set; } = "https://api.openai.com/v1/realtime/calls";

        /// <summary>
        /// Instructions for the AI assistant's behavior and personality.
        /// </summary>
        public string Instructions { get; set; } = "You are a helpful assistant.";
        
        /// <summary>
        /// List of tools available to the AI assistant.
        /// </summary>
        public List<IVoiceTool> Tools { get; set; } = new();

        /// <summary>
        /// Whether OpenAI may issue multiple tool calls in parallel.
        /// Disabled by default because many voice-agent tools operate on shared,
        /// stateful UI/session targets and should be executed in conversational order.
        /// </summary>
        public bool ParallelToolCalls { get; set; } = false;

        /// <summary>
        /// The OpenAI model to use for the conversation.
        /// </summary>
        public OpenAiRealtimeModel Model { get; set; } = OpenAiRealtimeModel.GptRealtime21;

        /// <summary>
        /// The voice to use for AI responses.
        /// </summary>
        public AssistantVoice Voice { get; set; } = AssistantVoice.Marin;

        /// <summary>
        /// Optional stable, non-identifying end-user identifier for OpenAI abuse monitoring.
        /// OpenAI recommends hashing a username or email before assigning this value.
        /// </summary>
        public string? SafetyIdentifier { get; set; }

        /// <summary>
        /// The speed of the AI model's spoken response.
        /// OpenAI supports 0.25 to 1.5, where 1.0 is normal speed.
        /// </summary>
        public double TalkingSpeed { get; set; } = 1.0;

        /// <summary>
        /// Used only for semantic_vad mode. Low waits longer for the user to continue speaking;
        /// high responds more quickly and is the library default. OpenAI's auto value is equivalent to medium.
        /// </summary>
        public Eagerness Eagerness { get; set; } = Eagerness.high;

        /// <summary>
        /// Maximum number of tokens for the response.
        /// </summary>
        public int? MaxTokens { get; set; }

        /// <summary>
        /// Controls automatic context truncation when token limit is reached.
        /// If true (default), uses retention_ratio strategy. If false, throws error when limit reached.
        /// </summary>
        public bool AutomaticContextTruncation { get; set; } = true;

        /// <summary>
        /// Retention ratio for context truncation (0.0 to 1.0).
        /// When set, keeps this percentage of context and drops the rest when limit approached.
        /// Default is 0.8 (keep 80%, drop 20% proactively to preserve prompt cache).
        /// </summary>
        public double RetentionRatio { get; set; } = 0.8;

        /// <summary>
        /// Turn detection settings for conversation flow.
        /// </summary>
        public TurnDetection TurnDetection { get; set; } = new();

        /// <summary>
        /// Convenience access to the numeric server-VAD activation threshold (0.0 to 1.0).
        /// Higher values require stronger input before speech is detected and can reduce false
        /// interruptions in noisy environments. This value is ignored when semantic VAD is used.
        /// </summary>
        public double VadThreshold
        {
            get => TurnDetection.Threshold;
            set => TurnDetection.Threshold = value;
        }

        public string MostLikelySpokenLanguage { get; set; } = "de";

        public string TranscriptionHint { get; set; } = "expect german business/IT/Contstruction and Tender law terms";

        /// <summary>
        /// Input audio transcription settings.
        /// </summary>
        public InputAudioTranscription InputAudioTranscription { get; set; } = new();

        /// <summary>
        /// Output audio format settings.
        /// </summary>
        public string OutputAudioFormat { get; set; } = "pcm16";

        public NoiseReductionMode NoiseReduction { get; set; } = NoiseReductionMode.FarField;

        /// <summary>
        /// Reasoning effort. Defaults to low for latency-sensitive voice conversations.
        /// Supported by full GPT Realtime 2 and 2.1 models; ignored by other OpenAI realtime models.
        /// </summary>
        public SessionReasoningEffort? ReasoningEffort { get; set; } = SessionReasoningEffort.Low;

        /// <summary>
        /// Spoken bridge policy for tool calls. Applied through OpenAI Realtime instructions.
        /// </summary>
        public ToolCallPreambleMode ToolCallPreambleMode { get; set; } = ToolCallPreambleMode.ProviderDefault;

        /// <summary>
        /// OpenAI Realtime currently ignores the shared thinking configuration.
        /// </summary>
        public SessionThinkingConfig Thinking { get; set; } = new();
    }

    /// <summary>
    /// Available assistant voices for OpenAI real-time API.
    /// </summary>
    public enum AssistantVoice
    {
        Alloy,
        Ash,
        Ballad,
        Coral,
        Echo,
        Sage,
        Shimmer,
        Verse,
        Marin,
        Cedar
    }

    /// <summary>
    /// Turn detection configuration for conversation management.
    /// </summary>
    public class TurnDetection
    {
        /// <summary>
        /// Type of turn detection (server_vad for server-side voice activity detection).
        /// </summary>
        public string Type { get; set; } = "server_vad";

        /// <summary>
        /// Threshold for voice activity detection (0.0 to 1.0).
        /// </summary>
        public double Threshold { get; set; } = 0.5;

        /// <summary>
        /// Prefix padding in milliseconds.
        /// </summary>
        public int PrefixPaddingMs { get; set; } = 300;

        /// <summary>
        /// Silence duration in milliseconds before considering turn complete.
        /// </summary>
        public int SilenceDurationMs { get; set; } = 200;

        /// <summary>
        /// Model should generate a response for everything
        /// </summary>
        public bool CreateResponse { get; set; } = true;

        /// <summary>
        /// Model is interruptable
        /// </summary>
        public bool InterruptResponse { get; set; } = true;

        /// <summary>
        /// Idle timeout in milliseconds. If set, triggers a prompt when user is silent for this duration.
        /// Available in GA API for automatic engagement during silence.
        /// </summary>
        public int? IdleTimeoutMs { get; set; }
    }

    /// <summary>
    /// Input audio transcription configuration.
    /// </summary>
    public class InputAudioTranscription
    {
        public string? ModelId { get; set; }
        public string GetModelId() => string.IsNullOrWhiteSpace(ModelId) ? Model.ToApiString() : ModelId;
        public OpenAiTranscriptionModel Model { get; set; } = OpenAiTranscriptionModel.Gpt4oTranscribe;

        /// <summary>
        /// Whether to enable transcription.
        /// </summary>
        public bool Enabled { get; set; } = true;


        /// <summary>
        /// An optional text to guide the model's style or continue a previous audio segment. For whisper-1, the prompt is a list of keywords. For gpt-4o-transcribe models, the prompt is a free text string, for example "expect words related to technology".
        /// </summary>
        public string? Prompt { get; set; }
    }
}
