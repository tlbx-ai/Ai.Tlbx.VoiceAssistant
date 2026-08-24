using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.XAi.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.XAi;

/// <summary>
/// Low-latency xAI Speech-to-Text provider with interim results, Smart Turn,
/// word timestamps, speaker diarization, and optional multichannel assignment.
/// </summary>
public sealed class XaiTranscriptionProvider : IVoiceProvider, IStructuredTranscriptionProvider
{
    private const string ENDPOINT = "wss://api.x.ai/v1/stt";
    private const int RECEIVE_BUFFER_SIZE = 32 * 1024;
    private const int PCM16_BYTES_PER_SECOND = 16000 * 2;

    private readonly string _apiKey;
    private readonly Action<LogLevel, string> _logAction;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private TaskCompletionSource<bool>? _ready;
    private TaskCompletionSource<bool>? _done;
    private XaiTranscriptionSettings? _settings;
    private long _inputAudioBytes;
    private int _doneChannelCount;
    private bool _isDisposed;

    public XaiTranscriptionProvider(string? apiKey = null, Action<LogLevel, string>? logAction = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("XAI_API_KEY")
            ?? throw new InvalidOperationException("xAI API key must be provided or set in XAI_API_KEY environment variable");
        _logAction = logAction ?? ((_, _) => { });
    }

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public AudioSampleRate RequiredInputSampleRate => AudioSampleRate.Rate16000;

    public Action<StructuredTranscript>? OnStructuredTranscriptionReceived { get; set; }
    public Action<ChatMessage>? OnMessageReceived { get; set; }
    public Action<string>? OnAudioReceived { get; set; }
    public Func<TimeSpan?, Task<bool>>? WaitForPlaybackDrainAsync { get; set; }
    public Action<string>? OnStatusChanged { get; set; }
    public Action<string>? OnError { get; set; }
    public Action? OnInterruptDetected { get; set; }
    public Action<UsageReport>? OnUsageReceived { get; set; }
    public Action<string>? OnTranscriptionDelta { get; set; }
    public Action<string>? OnTranscriptionCompleted { get; set; }

    public async Task ConnectAsync(IVoiceSettings settings)
    {
        ThrowIfDisposed();
        if (settings is not XaiTranscriptionSettings transcriptionSettings)
        {
            throw new ArgumentException("Settings must be XaiTranscriptionSettings.", nameof(settings));
        }

        ValidateSettings(transcriptionSettings);
        _webSocket?.Dispose();
        _cts?.Dispose();
        _settings = transcriptionSettings;
        _inputAudioBytes = 0;
        _doneChannelCount = 0;
        _cts = new CancellationTokenSource();
        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _webSocket = new ClientWebSocket();
        _webSocket.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");

        OnStatusChanged?.Invoke("Connecting to xAI transcription...");
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        connectionCts.CancelAfter(TimeSpan.FromSeconds(10));
        await _webSocket.ConnectAsync(BuildEndpoint(transcriptionSettings), connectionCts.Token);
        _receiveTask = ReceiveLoopAsync(_cts.Token);

        using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        readyCts.CancelAfter(TimeSpan.FromSeconds(10));
        await _ready.Task.WaitAsync(readyCts.Token);
        OnStatusChanged?.Invoke("xAI transcription ready");
    }

    public async Task DisconnectAsync()
    {
        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await SendTextAsync("{\"type\":\"audio.done\"}", CancellationToken.None);
                if (_done != null)
                {
                    using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _done.Task.WaitAsync(flushCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                _logAction(LogLevel.Warn, "Timed out waiting for xAI's final transcript.");
            }
            catch (WebSocketException ex)
            {
                _logAction(LogLevel.Warn, $"Could not finalize xAI transcription: {ex.Message}");
            }
        }

        _cts?.Cancel();
        if (_webSocket?.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Transcription complete", CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
        }

        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        PublishMeasuredUsage();
        OnStatusChanged?.Invoke("Disconnected");
    }

    public async Task UpdateSettingsAsync(IVoiceSettings settings)
    {
        if (settings is not XaiTranscriptionSettings transcriptionSettings)
        {
            throw new ArgumentException("Settings must be XaiTranscriptionSettings.", nameof(settings));
        }

        ValidateSettings(transcriptionSettings);
        if (IsConnected)
        {
            await DisconnectAsync();
            await ConnectAsync(transcriptionSettings);
        }
        else
        {
            _settings = transcriptionSettings;
        }
    }

    public async Task ProcessAudioAsync(string base64Audio)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(base64Audio))
        {
            return;
        }

        var audio = Convert.FromBase64String(base64Audio);
        await _sendLock.WaitAsync();
        try
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.SendAsync(audio, WebSocketMessageType.Binary, true, _cts?.Token ?? CancellationToken.None);
                Interlocked.Add(ref _inputAudioBytes, audio.Length);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Forces the current utterance to become speech-final without closing the stream.</summary>
    public Task FinalizeUtteranceAsync() => SendTextAsync("{\"type\":\"Finalize\"}", CancellationToken.None);

    public Task SendInterruptAsync() => FinalizeUtteranceAsync();
    public Task InjectConversationHistoryAsync(IEnumerable<ChatMessage> messages) => Task.CompletedTask;

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[RECEIVE_BUFFER_SIZE];
        try
        {
            while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    ProcessReceivedMessage(Encoding.UTF8.GetString(message.ToArray()));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _ready?.TrySetException(ex);
            OnError?.Invoke($"xAI transcription receive error: {ex.Message}");
            _logAction(LogLevel.Error, $"xAI transcription receive error: {ex.Message}");
        }
    }

    private void ProcessReceivedMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;

        switch (type)
        {
            case "transcript.created":
                _ready?.TrySetResult(true);
                break;
            case "transcript.partial":
                PublishTranscript(ParseTranscript(root));
                break;
            case "transcript.done":
                PublishTranscript(ParseTranscript(root, forceFinal: true));
                if (Interlocked.Increment(ref _doneChannelCount) >= (_settings?.Multichannel == true ? _settings.Channels : 1))
                {
                    _done?.TrySetResult(true);
                }
                break;
            case "error":
                var error = root.TryGetProperty("message", out var message) ? message.GetString() : json;
                _ready?.TrySetException(new InvalidOperationException(error ?? "Unknown xAI transcription error"));
                OnError?.Invoke(error ?? "Unknown xAI transcription error");
                _logAction(LogLevel.Error, error ?? json);
                break;
            default:
                _logAction(LogLevel.Info, $"Unhandled xAI transcription event: {type}");
                break;
        }
    }

    private void PublishTranscript(StructuredTranscript transcript)
    {
        OnStructuredTranscriptionReceived?.Invoke(transcript);
        if (transcript.IsSpeechFinal)
        {
            OnTranscriptionCompleted?.Invoke(transcript.Text);
        }
        else
        {
            OnTranscriptionDelta?.Invoke(transcript.Text);
        }
    }

    private static StructuredTranscript ParseTranscript(JsonElement root, bool forceFinal = false)
    {
        var text = root.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
        var channel = root.TryGetProperty("channel_index", out var channelElement) && channelElement.TryGetInt32(out var channelIndex)
            ? channelIndex
            : (int?)null;
        var words = ParseWords(root);
        var isFinal = forceFinal || GetBoolean(root, "is_final");

        return new StructuredTranscript
        {
            ProviderId = "xai",
            ModelId = "grok-transcribe",
            Text = text,
            Language = root.TryGetProperty("language", out var languageElement) ? languageElement.GetString() : null,
            Duration = GetSeconds(root, "duration"),
            IsFinal = isFinal,
            IsSpeechFinal = forceFinal || GetBoolean(root, "speech_final"),
            IsSnapshotComplete = isFinal,
            EndOfTurnConfidence = root.TryGetProperty("end_of_turn_confidence", out var confidence) && confidence.TryGetDouble(out var score)
                ? score
                : null,
            Segments = BuildSegments(text, words, channel)
        };
    }

    private static IReadOnlyList<TranscriptWord> ParseWords(JsonElement root)
    {
        if (!root.TryGetProperty("words", out var wordsElement) || wordsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TranscriptWord>();
        }

        var words = new List<TranscriptWord>();
        foreach (var word in wordsElement.EnumerateArray())
        {
            string? speaker = null;
            if (word.TryGetProperty("speaker", out var speakerElement))
            {
                speaker = speakerElement.ValueKind == JsonValueKind.Number && speakerElement.TryGetInt32(out var speakerNumber)
                    ? $"speaker-{speakerNumber}"
                    : speakerElement.ToString();
            }

            words.Add(new TranscriptWord
            {
                Text = word.TryGetProperty("text", out var wordText) ? wordText.GetString() ?? string.Empty : string.Empty,
                Speaker = speaker,
                Start = GetSeconds(word, "start"),
                End = GetSeconds(word, "end")
            });
        }

        return words;
    }

    private static IReadOnlyList<TranscriptSegment> BuildSegments(
        string text,
        IReadOnlyList<TranscriptWord> words,
        int? channel)
    {
        if (words.Count == 0)
        {
            return string.IsNullOrWhiteSpace(text)
                ? Array.Empty<TranscriptSegment>()
                : new[] { new TranscriptSegment { Text = text, ChannelIndex = channel } };
        }

        var segments = new List<TranscriptSegment>();
        var currentWords = new List<TranscriptWord>();
        string? currentSpeaker = words[0].Speaker;

        foreach (var word in words)
        {
            if (currentWords.Count > 0 && !string.Equals(currentSpeaker, word.Speaker, StringComparison.Ordinal))
            {
                segments.Add(CreateSegment(currentWords, currentSpeaker, channel));
                currentWords = new List<TranscriptWord>();
                currentSpeaker = word.Speaker;
            }

            currentWords.Add(word);
        }

        if (currentWords.Count > 0)
        {
            segments.Add(CreateSegment(currentWords, currentSpeaker, channel));
        }

        return segments;
    }

    private static TranscriptSegment CreateSegment(List<TranscriptWord> words, string? speaker, int? channel) => new()
    {
        Text = string.Join(" ", words.Select(word => word.Text)).Trim(),
        Speaker = speaker,
        ChannelIndex = channel,
        Start = words[0].Start,
        End = words[^1].End,
        Words = words.ToArray()
    };

    private static bool GetBoolean(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

    private static TimeSpan? GetSeconds(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;

    private static Uri BuildEndpoint(XaiTranscriptionSettings settings)
    {
        var query = new List<string>
        {
            "sample_rate=16000",
            "encoding=pcm",
            $"interim_results={settings.InterimResults.ToString().ToLowerInvariant()}",
            $"endpointing={settings.EndpointingMs.ToString(CultureInfo.InvariantCulture)}",
            $"diarize={settings.Diarize.ToString().ToLowerInvariant()}",
            $"filler_words={settings.IncludeFillerWords.ToString().ToLowerInvariant()}",
            $"multichannel={settings.Multichannel.ToString().ToLowerInvariant()}",
            $"channels={settings.Channels.ToString(CultureInfo.InvariantCulture)}",
            $"vad_threshold={settings.VadThreshold.ToString("0.###", CultureInfo.InvariantCulture)}"
        };

        AddQuery(query, "language", settings.Language);
        foreach (var keyterm in settings.Keyterms)
        {
            AddQuery(query, "keyterm", keyterm);
        }

        if (settings.SmartTurnThreshold.HasValue)
        {
            AddQuery(query, "smart_turn", settings.SmartTurnThreshold.Value.ToString("0.###", CultureInfo.InvariantCulture));
            if (settings.SmartTurnTimeoutMs.HasValue)
            {
                AddQuery(query, "smart_turn_timeout", settings.SmartTurnTimeoutMs.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        return new Uri($"{ENDPOINT}?{string.Join("&", query)}");
    }

    private static void AddQuery(List<string> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    private static void ValidateSettings(XaiTranscriptionSettings settings)
    {
        if (settings.EndpointingMs is < 0 or > 5000)
            throw new InvalidOperationException("xAI endpointing must be between 0 and 5000 ms.");
        if (settings.Channels is < 1 or > 8)
            throw new InvalidOperationException("xAI transcription supports one to eight channels.");
        if (settings.Multichannel && settings.Channels < 2)
            throw new InvalidOperationException("xAI multichannel transcription requires at least two channels.");
        if (settings.SmartTurnThreshold is < 0 or > 1)
            throw new InvalidOperationException("xAI Smart Turn threshold must be between 0 and 1.");
        if (settings.SmartTurnTimeoutMs is < 1 or > 5000)
            throw new InvalidOperationException("xAI Smart Turn timeout must be between 1 and 5000 ms.");
        if (settings.VadThreshold is < 0 or > 1)
            throw new InvalidOperationException("xAI VAD threshold must be between 0 and 1.");
        if (settings.Keyterms.Count > 100 || settings.Keyterms.Any(term => term.Length > 50))
            throw new InvalidOperationException("xAI supports at most 100 keyterms of at most 50 characters each.");
    }

    private async Task SendTextAsync(string json, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void PublishMeasuredUsage()
    {
        var bytes = Interlocked.Exchange(ref _inputAudioBytes, 0);
        if (bytes <= 0)
        {
            return;
        }

        OnUsageReceived?.Invoke(new UsageReport
        {
            ProviderId = "xai",
            ModelId = "grok-transcribe",
            OperationType = UsageOperationType.Transcription,
            MeasurementSource = UsageMeasurementSource.ClientMeasured,
            InputAudioDuration = TimeSpan.FromSeconds(
                bytes / (double)(PCM16_BYTES_PER_SECOND * (_settings?.Multichannel == true ? _settings.Channels : 1))),
            IsEstimated = true
        });
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(XaiTranscriptionProvider));
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;
        await DisconnectAsync();
        _webSocket?.Dispose();
        _cts?.Dispose();
        _sendLock.Dispose();
        _isDisposed = true;
    }
}
