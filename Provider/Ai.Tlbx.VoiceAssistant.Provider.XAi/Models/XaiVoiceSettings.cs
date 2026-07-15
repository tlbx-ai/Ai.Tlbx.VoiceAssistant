using System.Collections.Generic;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.XAi.Models
{
    /// <summary>
    /// xAI-specific voice assistant settings that control behavior and configuration.
    /// </summary>
    public class XaiVoiceSettings : IVoiceSettings
    {
        /// <summary>
        /// Instructions for the AI assistant's behavior and personality.
        /// </summary>
        public string Instructions { get; set; } = "You are a helpful assistant.";

        /// <summary>
        /// List of tools available to the AI assistant.
        /// </summary>
        public List<IVoiceTool> Tools { get; set; } = new();

        /// <summary>
        /// The voice to use for AI responses.
        /// Built-in voice to use when <see cref="VoiceId"/> is not set.
        /// </summary>
        public XaiVoice Voice { get; set; } = XaiVoice.Eve;

        /// <summary>
        /// Optional built-in or custom xAI voice ID. When set, this takes precedence over <see cref="Voice"/>.
        /// </summary>
        public string? VoiceId { get; set; }

        /// <summary>
        /// The xAI realtime voice model to use for the conversation.
        /// Defaults to xAI's latest alias. Select GrokVoiceThinkFast10 to pin the current release.
        /// </summary>
        public XaiVoiceModel Model { get; set; } = XaiVoiceModel.GrokVoiceLatest;

        /// <summary>
        /// The speed of the AI model's spoken response.
        /// xAI supports 0.7 to 1.5, where 1.0 is normal speed.
        /// </summary>
        public double TalkingSpeed { get; set; } = 1.0;

        /// <summary>
        /// Audio sample rate in Hz.
        /// The current toolkit implementation supports 24000 Hz only.
        /// </summary>
        public int AudioSampleRate { get; set; } = 24000;

        /// <summary>
        /// Audio format type.
        /// The current toolkit implementation supports "audio/pcm" only.
        /// </summary>
        public string AudioFormatType { get; set; } = "audio/pcm";

        /// <summary>
        /// Turn detection settings for conversation flow.
        /// The current toolkit implementation requires xAI server VAD and does not support null/manual turn control.
        /// </summary>
        public XaiTurnDetection? TurnDetection { get; set; } = new();

        /// <summary>
        /// Enable xAI's built-in web search tool for real-time internet search.
        /// </summary>
        public bool EnableWebSearch { get; set; } = false;

        /// <summary>
        /// Enable xAI's built-in X (Twitter) search tool for posts, trends, and discussions.
        /// </summary>
        public bool EnableXSearch { get; set; } = false;

        /// <summary>
        /// xAI-hosted collection searches to expose as file_search tools.
        /// </summary>
        public List<XaiFileSearchSettings> FileSearchTools { get; set; } = new();

        /// <summary>
        /// Remote MCP servers that xAI should connect to and execute server-side.
        /// </summary>
        public List<XaiMcpServerSettings> McpServers { get; set; } = new();

        /// <summary>
        /// ISO-639-1 language code hint for input audio recognition (e.g. "de", "en").
        /// Improves accuracy and latency when the expected language is known.
        /// </summary>
        public string? InputAudioLanguage { get; set; }

        /// <summary>
        /// Whether to request streaming input transcription with grok-transcribe.
        /// </summary>
        public bool EnableInputAudioTranscription { get; set; } = true;

        /// <summary>
        /// Terms that should be favored by input transcription. Maximum 100 terms, 50 characters each.
        /// </summary>
        public List<string> InputAudioKeyterms { get; set; } = new();

        /// <summary>
        /// Spoken pronunciation substitutions. Keys remain unchanged in the transcript.
        /// </summary>
        public Dictionary<string, string> PronunciationReplacements { get; set; } = new();

        /// <summary>
        /// Enables xAI session resumption. The provider stores the latest conversation ID in
        /// <see cref="ConversationId"/> so a later ConnectAsync call can resume the server-cached context.
        /// </summary>
        public bool EnableSessionResumption { get; set; } = true;

        /// <summary>
        /// Conversation ID captured from xAI or supplied by the caller for reconnection.
        /// </summary>
        public string? ConversationId { get; set; }

        public NoiseReductionMode NoiseReduction { get; set; } = NoiseReductionMode.FarField;

        /// <summary>
        /// xAI reasoning effort. The current Voice Agent API accepts None or High for latest/think-fast models.
        /// </summary>
        public SessionReasoningEffort? ReasoningEffort { get; set; }

        /// <summary>
        /// xAI Voice Agent currently ignores provider-neutral tool-call preamble policy.
        /// </summary>
        public ToolCallPreambleMode ToolCallPreambleMode { get; set; } = ToolCallPreambleMode.ProviderDefault;

        /// <summary>
        /// xAI Voice Agent currently ignores the shared thinking configuration.
        /// </summary>
        public SessionThinkingConfig Thinking { get; set; } = new();
    }

    /// <summary>
    /// Turn detection configuration for conversation management.
    /// </summary>
    public class XaiTurnDetection
    {
        public string Type { get; set; } = "server_vad";
        public double Threshold { get; set; } = 0.85;
        public int PrefixPaddingMs { get; set; } = 333;
        public int SilenceDurationMs { get; set; } = 200;
        public int? IdleTimeoutMs { get; set; }
        public bool CreateResponse { get; set; } = true;
        public bool InterruptResponse { get; set; } = true;
    }

    public class XaiFileSearchSettings
    {
        public List<string> VectorStoreIds { get; set; } = new();
        public int? MaxResults { get; set; }
    }

    public class XaiMcpServerSettings
    {
        public string ServerUrl { get; set; } = string.Empty;
        public string ServerLabel { get; set; } = string.Empty;
        public string? ServerDescription { get; set; }
        public List<string> AllowedTools { get; set; } = new();
        public string? Authorization { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new();
    }
}
