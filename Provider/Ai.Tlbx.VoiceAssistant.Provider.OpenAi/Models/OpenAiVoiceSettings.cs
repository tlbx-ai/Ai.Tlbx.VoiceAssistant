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
        /// <summary>
        /// Instructions for the AI assistant's behavior and personality.
        /// </summary>
        public string Instructions { get; set; } = "You are a helpful assistant.";
        
        /// <summary>
        /// List of tools available to the AI assistant.
        /// </summary>
        public List<IVoiceTool> Tools { get; set; } = new();

        /// <summary>
        /// The OpenAI model to use for the conversation.
        /// </summary>
        public OpenAiRealtimeModel Model { get; set; } = OpenAiRealtimeModel.GptRealtime15;

        /// <summary>
        /// The voice to use for AI responses.
        /// </summary>
        public AssistantVoice Voice { get; set; } = AssistantVoice.Alloy;

        /// <summary>
        /// The speed of the AI model's spoken response.
        /// OpenAI supports 0.25 to 1.5, where 1.0 is normal speed.
        /// </summary>
        public double TalkingSpeed { get; set; } = 1.0;

        /// <summary>
        /// Used only for semantic_vad mode. The eagerness of the model to respond. low will wait longer for the user to continue speaking, high will respond more quickly. auto is the default and is equivalent to medium
        /// </summary>
        public Eagerness Eagerness { get; set; } = Eagerness.auto;

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
        public OpenAiTranscriptionModel Model { get; set; } = OpenAiTranscriptionModel.Gpt4oMiniTranscribe;

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
