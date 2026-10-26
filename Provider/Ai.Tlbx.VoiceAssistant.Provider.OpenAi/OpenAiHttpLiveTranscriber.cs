using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi
{
    /// <summary>
    /// Near-live microphone transcription over OpenAI's HTTP transcription API.
    /// Uploads bounded, non-overlapping PCM sections and retains confirmed text across requests.
    /// </summary>
    public sealed class OpenAiHttpLiveTranscriber : IAsyncDisposable, IStructuredTranscriptionProvider, IStructuredTranscriptionProgressProvider
    {
        private readonly string _apiKey;
        private const int SAMPLE_RATE = 24000;
        private const int BYTES_PER_SECOND = SAMPLE_RATE * 2; // mono PCM16

        private readonly IAudioHardwareAccess _hardwareAccess;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly Action<LogLevel, string> _logAction;
        private readonly object _sync = new();

        private CancellationTokenSource? _runCts;
        private bool _isDisposed;
        private bool _isRunning;
        private bool _snapshotInFlight;
        private int _sessionId;
        private long _snapshotRevision;
        private long _capturedBytes;
        private long _confirmedBytes;
        private long _bufferStartBytes;
        private TaskCompletionSource? _completion;
        private TaskCompletionSource? _startup;
        private CancellationTokenSource? _finalizationCts;
        private Task? _microphoneStop;
        private bool _acceptAudio;
        private bool _hardwareFailed;
        private bool _fatal;
        private bool _retryExhausted;
        private bool _disposing;
        private readonly List<TranscriptSegment> _confirmedSegments = new();
        private string _confirmedText = string.Empty;
        private readonly SemaphoreSlim _audioReady = new(0, 1);
        private readonly Dictionary<string, string> _speakerReferences = new(StringComparer.Ordinal);
        private string _speakerSessionId = string.Empty;
        private int _nextSpeakerId;
        private MemoryStream _capturedAudio = new();
        private string _latestPublishedText = string.Empty;
        private StructuredTranscript? _latestStructuredTranscript;

        public OpenAiHttpLiveTranscriptionOptions Options { get; }

        /// <summary>
        /// Callback invoked after each successful transcription upload with the
        /// exact PCM duration submitted to OpenAI for that request.
        /// </summary>
        public Action<UsageReport>? OnUsageReceived { get; set; }

        /// <summary>
        /// Receives cumulative confirmed sections. Request-local speaker labels are kept separate
        /// from reference-backed session identities; incomplete requests never replace confirmed text.
        /// </summary>
        public Action<StructuredTranscript>? OnStructuredTranscriptionReceived { get; set; }

        /// <summary>
        /// Receives individual completed speaker segments while a snapshot request is still streaming.
        /// These events are revision-scoped progress and must not replace an authoritative session snapshot.
        /// </summary>
        public Action<StructuredTranscript>? OnStructuredTranscriptionProgress { get; set; }

        /// <summary>
        /// The most complete authoritative structured state confirmed for the current or last session.
        /// </summary>
        public StructuredTranscript? LatestStructuredTranscript
        {
            get
            {
                lock (_sync)
                {
                    return _latestStructuredTranscript;
                }
            }
        }

        public OpenAiHttpLiveTranscriber(
            IAudioHardwareAccess hardwareAccess,
            OpenAiHttpLiveTranscriptionOptions? options = null,
            string? apiKey = null,
            Action<LogLevel, string>? logAction = null)
            : this(hardwareAccess, options, apiKey, logAction, null)
        {
        }

        /// <summary>Uses a caller-owned HTTP client, including its proxy and TLS configuration.</summary>
        public OpenAiHttpLiveTranscriber(
            IAudioHardwareAccess hardwareAccess,
            OpenAiHttpLiveTranscriptionOptions? options,
            string? apiKey,
            Action<LogLevel, string>? logAction,
            HttpClient? httpClient)
        {
            _hardwareAccess = hardwareAccess ?? throw new ArgumentNullException(nameof(hardwareAccess));
            _logAction = logAction ?? ((level, message) => { /* no-op */ });
            Options = options ?? new OpenAiHttpLiveTranscriptionOptions();

            var resolvedApiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                ?? throw new InvalidOperationException("OpenAI API key must be provided or set in OPENAI_API_KEY environment variable");

            _ownsHttpClient = httpClient == null;
            _httpClient = httpClient ?? new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _apiKey = resolvedApiKey;
        }

        /// <summary>
        /// Starts near-live transcription and keeps running until <see cref="StopAsync"/> is called,
        /// the instance is disposed, or the optional cancellation token is cancelled.
        /// </summary>
        public Task TranscribeLive(Action<string> onTextChunk)
        {
            return TranscribeLive(onTextChunk, CancellationToken.None);
        }

        /// <summary>
        /// Starts near-live transcription and keeps running until <see cref="StopAsync"/> is called,
        /// the instance is disposed, or the provided cancellation token is cancelled.
        /// </summary>
        public Task TranscribeLive(Action<string> onTextChunk, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(onTextChunk);
            lock (_sync)
            {
                ThrowIfDisposed();
                ValidateOptions();
                if (_isRunning) throw new InvalidOperationException("Live transcription is already running.");
                ResetRunState(true);
                _sessionId++;
                _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _finalizationCts = new();
                _microphoneStop = null;
                _isRunning = true;
                _acceptAudio = true;
            }
            return RunSessionAsync(onTextChunk);
        }

        /// <summary>Latest lifecycle, confirmed text, audio coverage and missing ranges. Safe to read while finalizing.</summary>
        public HttpTranscriptionStatus? LatestStatus { get; private set; }
        public Action<HttpTranscriptionStatus>? OnStatusChanged { get; set; }
        public Action<HttpTranscriptionError>? OnTranscriptionError { get; set; }
        public string LatestText { get { lock (_sync) return _latestPublishedText; } }
        public int BufferedAudioBytes { get { lock (_sync) return (int)_capturedAudio.Length; } }

        private async Task RunSessionAsync(Action<string> onTextChunk)
        {
            var runToken = _runCts!.Token;
            _hardwareAccess.AudioError += OnHardwareError;
            try
            {
                PublishStatus(HttpTranscriptionState.Recording);
                try
                {
                    await _hardwareAccess.InitAudioAsync();
                    var sessionId = _sessionId;
                    if (!await _hardwareAccess.StartRecordingAudio((sender, audio) =>
                    {
                        lock (_sync) { if (sessionId != _sessionId) return; OnAudioDataReceived(sender, audio); }
                    }, AudioSampleRate.Rate24000))
                        throw new InvalidOperationException("Failed to start microphone recording.");
                }
                finally { _startup!.TrySetResult(); }
                // Cancellation during initialization still stops the newly opened microphone.
                using var registration = runToken.Register(() => { _ = StopMicrophoneAsync(); });
                try
                {
                    while (!runToken.IsCancellationRequested && !_fatal)
                    {
                        await _audioReady.WaitAsync(Options.SnapshotInterval, runToken);
                        var snapshot = TryCreateSnapshot(false);
                        if (snapshot.HasValue && !await ProcessSnapshotAsync(snapshot.Value, onTextChunk, runToken)) break;
                    }
                }
                catch (OperationCanceledException) when (runToken.IsCancellationRequested) { }
                await StopMicrophoneAsync();
                PublishStatus(HttpTranscriptionState.Finalizing);
                _finalizationCts!.CancelAfter(Options.FinalizationTimeout);
                while (!_fatal && !_disposing && !_retryExhausted)
                {
                    var tail = TryCreateSnapshot(true);
                    if (!tail.HasValue) break;
                    if (!await ProcessSnapshotAsync(tail.Value, onTextChunk, _finalizationCts.Token)) break;
                }
            }
            catch (OperationCanceledException) when (_finalizationCts?.IsCancellationRequested == true)
            {
                ReportError(new HttpTranscriptionError { Code = "finalization_cancelled", IsFatal = false,
                    Message = "Finalization deadline or disposal ended the unconfirmed tail." });
            }
            catch (Exception ex)
            {
                ReportError(new HttpTranscriptionError { Code = "session_failed", IsFatal = true, Message = ex.GetType().Name });
                _fatal = true;
            }
            finally
            {
                await StopMicrophoneAsync();
                _hardwareAccess.AudioError -= OnHardwareError;
                PublishSessionFinal(onTextChunk);
                PublishStatus(_fatal ? HttpTranscriptionState.Failed :
                    _hardwareFailed || _confirmedBytes < _capturedBytes ? HttpTranscriptionState.Partial : HttpTranscriptionState.Completed);
                lock (_sync)
                {
                    _isRunning = false;
                    _acceptAudio = false;
                    _capturedAudio.SetLength(0);
                    _speakerReferences.Clear();
                    _runCts!.Dispose();
                    _runCts = null;
                    _finalizationCts!.Dispose();
                    _finalizationCts = null;
                    _completion!.TrySetResult();
                }
            }
        }

        /// <summary>Stops capture and acknowledges the last hardware block; await TranscribeLive for bounded finalization.</summary>
        public async Task StopAsync()
        {
            lock (_sync) { _runCts?.Cancel(); }
            await StopMicrophoneAsync();
        }

        private Task StopMicrophoneAsync()
        {
            lock (_sync)
            {
                if (!_isRunning) return Task.CompletedTask;
                return _microphoneStop ??= StopMicrophoneCoreAsync();
            }
        }

        private async Task StopMicrophoneCoreAsync()
        {
            // Publish the shared stop task before hardware callbacks can re-enter StopAsync.
            await Task.Yield();
            await _startup!.Task;
            try
            {
                if (!await _hardwareAccess.StopRecordingAudio().WaitAsync(Options.HardwareStopTimeout))
                    throw new IOException("Hardware did not acknowledge capture stop.");
            }
            catch (Exception ex)
            {
                _hardwareFailed = true;
                ReportError(new HttpTranscriptionError { Code = "hardware_stop_unconfirmed", IsFatal = false, Message = ex.GetType().Name });
            }
            finally { lock (_sync) _acceptAudio = false; }
        }

        private void OnHardwareError(object? sender, string message)
        {
            _hardwareFailed = true;
            ReportError(new HttpTranscriptionError { Code = "hardware_failed", IsFatal = true, Message = "Audio capture or delivery failed." });
            lock (_sync) _runCts?.Cancel();
        }

        private SnapshotWorkItem? TryCreateSnapshot(bool forceFinal)
        {
            lock (_sync)
            {
                int available = (int)_capturedAudio.Length;
                if (_snapshotInFlight || available == 0) return null;
                var minimum = Options.TranscriptionModel.SupportsDiarizedJson()
                    ? TimeSpan.FromTicks(Math.Min(Options.DiarizationMinimumSectionDuration.Ticks, Options.MaxSectionDuration.Ticks))
                    : Options.MinimumUtteranceDuration;
                if (!forceFinal && available < DurationToBytes(minimum)) return null;
                int count = Math.Min(available, DurationToBytes(Options.MaxSectionDuration));
                // Prefer a short quiet window near the end; never re-send a confirmed sample.
                var buffer = _capturedAudio.GetBuffer();
                int window = DurationToBytes(Options.SilenceDuration);
                for (int end = count; end >= Math.Max(window, count / 2); end -= 960)
                {
                    bool quiet = true;
                    for (int pos = end - window; pos < end; pos += 2)
                    {
                        short sample = (short)(buffer[pos] | buffer[pos + 1] << 8);
                        if (Math.Abs((int)sample) > 300) { quiet = false; break; }
                    }
                    if (quiet) { count = end; break; }
                }
                var audio = new byte[count];
                Buffer.BlockCopy(buffer, 0, audio, 0, count);
                _snapshotInFlight = true;
                return new SnapshotWorkItem(_sessionId, ++_snapshotRevision, audio, forceFinal, _confirmedText,
                    TimeSpan.FromSeconds(_bufferStartBytes / (double)BYTES_PER_SECOND),
                    TimeSpan.FromSeconds((_bufferStartBytes + count) / (double)BYTES_PER_SECOND))
                { SpeakerReferences = new Dictionary<string, string>(_speakerReferences, StringComparer.Ordinal) };
            }
        }

        private async Task<bool> ProcessSnapshotAsync(SnapshotWorkItem snapshot, Action<string> onTextChunk, CancellationToken token)
        {
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        requestCts.CancelAfter(Options.RequestTimeout);
                        await TranscribeSnapshotAsync(snapshot, onTextChunk, requestCts.Token);
                        return true;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        var http = ex as TranscriptionRequestException;
                        bool retryable = ex is HttpRequestException or IOException or OperationCanceledException || http?.Retryable == true;
                        bool retry = retryable && attempt < Options.MaxRetries;
                        ReportError(new HttpTranscriptionError
                        {
                            Code = ex is OperationCanceledException ? "request_timeout" : "section_failed",
                            IsFatal = !retryable, WillRetry = retry, Attempt = attempt + 1,
                            SectionId = snapshot.Revision, AudioStart = snapshot.AudioStart, AudioEnd = snapshot.AudioEnd,
                            HttpStatus = http?.Status, RetryAfter = http?.RetryAfter, ProviderRequestId = http?.RequestId,
                            Message = ex.GetType().Name
                        });
                        if (!retry) { _fatal |= !retryable; _retryExhausted = retryable; return false; }
                        var delay = http?.RetryAfter ?? TimeSpan.FromMilliseconds(Options.RetryDelay.TotalMilliseconds * Math.Pow(2, attempt));
                        await Task.Delay(delay, token);
                    }
                }
            }
            finally { lock (_sync) _snapshotInFlight = false; }
        }

        private async Task TranscribeSnapshotAsync(SnapshotWorkItem snapshot, Action<string> onTextChunk, CancellationToken cancellationToken)
        {
            _logAction(LogLevel.Info,
                $"Processing transcription snapshot {snapshot.Revision}: audio_end={FormatCoverage(snapshot.AudioEnd)}, final={snapshot.IsFinal}.");
            byte[] wavAudio = BuildWav(snapshot.Audio, SAMPLE_RATE);

            using var form = new MultipartFormDataContent();
            using var audioContent = new ByteArrayContent(wavAudio);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audioContent, "file", $"hold-transcribe-{snapshot.SessionId}.wav");
            form.Add(new StringContent(Options.GetModelId()), "model");
            form.Add(new StringContent("true"), "stream");

            var useDiarizedJson = Options.TranscriptionModel.SupportsDiarizedJson();
            form.Add(new StringContent(useDiarizedJson ? "diarized_json" : "text"), "response_format");

            if (useDiarizedJson)
            {
                form.Add(new StringContent("auto"), "chunking_strategy");
            }
            else
            {
                form.Add(new StringContent("server_vad"), "chunking_strategy[type]");
                form.Add(new StringContent(((int)Math.Round(Options.PrefixPadding.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture)), "chunking_strategy[prefix_padding_ms]");
                form.Add(new StringContent(((int)Math.Round(Options.SilenceDuration.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture)), "chunking_strategy[silence_duration_ms]");
                form.Add(new StringContent(Options.VadThreshold.ToString("0.###", CultureInfo.InvariantCulture)), "chunking_strategy[threshold]");
            }

            if (!string.IsNullOrWhiteSpace(Options.Language))
            {
                if (Options.TranscriptionModel.SupportsContextLists())
                {
                    form.Add(new StringContent(Options.Language), "languages[]");
                }
                else
                {
                    form.Add(new StringContent(Options.Language), "language");
                }
            }

            if (Options.TranscriptionModel.SupportsContextLists())
            {
                foreach (var language in Options.Languages
                    .Where(language => !string.IsNullOrWhiteSpace(language))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!string.Equals(language, Options.Language, StringComparison.OrdinalIgnoreCase))
                    {
                        form.Add(new StringContent(language), "languages[]");
                    }
                }

                foreach (var keyword in Options.Keywords)
                {
                    form.Add(new StringContent(keyword), "keywords[]");
                }
            }

            if (Options.TranscriptionModel.SupportsTranscriptionPrompt() &&
                !string.IsNullOrWhiteSpace(Options.Prompt))
            {
                form.Add(new StringContent(Options.Prompt), "prompt");
            }

            if (Options.IncludeLogProbabilities &&
                Options.TranscriptionModel.SupportsTranscriptionLogProbabilities() &&
                !useDiarizedJson)
            {
                form.Add(new StringContent("logprobs"), "include[]");
            }

            if (useDiarizedJson)
            {
                foreach (var reference in snapshot.SpeakerReferences)
                {
                    form.Add(new StringContent(reference.Key), "known_speaker_names[]");
                    form.Add(new StringContent(reference.Value), "known_speaker_references[]");
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, Options.Connection.BuildUri(fallbackApiKey: _apiKey))
            {
                Content = form
            };

            Options.Connection.Apply(request, _apiKey);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var retryAfter = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : null);
                throw new TranscriptionRequestException(status, status == 429 || status >= 500,
                    retryAfter > TimeSpan.Zero ? retryAfter : null,
                    response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null);
            }

            OnUsageReceived?.Invoke(new UsageReport
            {
                ProviderId = "openai",
                ModelId = Options.GetModelId(),
                OperationType = UsageOperationType.Transcription,
                MeasurementSource = UsageMeasurementSource.ClientMeasured,
                InputAudioDuration = TimeSpan.FromSeconds(snapshot.Audio.Length / (double)BYTES_PER_SECOND),
                IsEstimated = true
            });

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(responseStream);
            var hypothesis = new StringBuilder();
            var structuredSegments = new List<TranscriptSegment>();
            var structuredSegmentKeys = new HashSet<string>(StringComparer.Ordinal);
            var snapshotCompleted = false;
            string? currentEvent = null;
            var currentData = new StringBuilder();

            while (true)
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idleCts.CancelAfter(Options.StreamIdleTimeout);
                string? line = await reader.ReadLineAsync(idleCts.Token);
                if (line?.Length > 1_000_000 || currentData.Length > 1_000_000)
                    throw new InvalidOperationException("Transcription stream event exceeds its size limit.");
                if (line == null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    snapshotCompleted |= ApplyStreamingEvent(snapshot, currentEvent, currentData.ToString(), hypothesis, structuredSegments, structuredSegmentKeys, onTextChunk);
                    currentEvent = null;
                    currentData.Clear();
                    if (snapshotCompleted) break;
                    continue;
                }

                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    currentEvent = line["event:".Length..].Trim();
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (currentData.Length > 0)
                    {
                        currentData.Append('\n');
                    }

                    currentData.Append(line["data:".Length..].TrimStart());
                }
            }

            snapshotCompleted |= ApplyStreamingEvent(snapshot, currentEvent, currentData.ToString(), hypothesis, structuredSegments, structuredSegmentKeys, onTextChunk);

            if (!snapshotCompleted) throw new IOException("Stream ended without transcript.text.done.");
        }

        private bool ApplyStreamingEvent(
            SnapshotWorkItem snapshot,
            string? eventName,
            string data,
            StringBuilder hypothesis,
            List<TranscriptSegment> structuredSegments,
            HashSet<string> structuredSegmentKeys,
            Action<string> onTextChunk)
        {
            if (string.IsNullOrWhiteSpace(data) || string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(data);
                var root = document.RootElement;

                string? type = eventName;
                if (root.TryGetProperty("type", out var typeElement))
                {
                    type = typeElement.GetString() ?? type;
                }

                switch (type)
                {
                    case "transcript.text.delta":
                        if (Options.TranscriptionModel.SupportsDiarizedJson())
                        {
                            break;
                        }

                        if (root.TryGetProperty("delta", out var deltaElement))
                        {
                            string delta = deltaElement.GetString() ?? string.Empty;
                            if (delta.Length > 0)
                            {
                                hypothesis.Append(delta);
                                PublishHypothesisUpdate(snapshot, hypothesis.ToString(), onTextChunk);
                            }
                        }
                        break;

                    case "transcript.text.segment":
                        var segment = TryParseDiarizedSegment(root);
                        if (segment != null && structuredSegmentKeys.Add(GetSegmentKey(segment)))
                        {
                            structuredSegments.Add(segment);
                            if (hypothesis.Length > 0 && hypothesis[^1] != '\n')
                            {
                                hypothesis.AppendLine();
                            }

                            hypothesis.Append(FormatSegment(segment));
                            PublishStructuredProgress(snapshot, segment);
                        }
                        break;

                    case "transcript.text.done":
                        string finalText = root.TryGetProperty("text", out var textElement)
                            ? textElement.GetString() ?? string.Empty
                            : string.Empty;
                        if (string.IsNullOrWhiteSpace(finalText))
                        {
                            finalText = hypothesis.ToString();
                        }

                        {
                            PublishCompletedSnapshot(
                                snapshot,
                                structuredSegments,
                                finalText,
                                GetDetectedLanguage(root),
                                onTextChunk);
                            return true;
                        }
                }
            }
            catch (JsonException)
            {
                _logAction(LogLevel.Warn, "Could not parse transcription stream event.");
            }

            return false;
        }

        private void PublishStructuredProgress(SnapshotWorkItem snapshot, TranscriptSegment segment)
        {
            try
            {
                OnStructuredTranscriptionProgress?.Invoke(new StructuredTranscript
                {
                    ProviderId = "openai",
                    ModelId = Options.GetModelId(),
                    Text = segment.Text,
                    Language = Options.Language,
                    IsFinal = false,
                    IsSpeechFinal = true,
                    SnapshotRevision = snapshot.Revision,
                    IsSnapshotComplete = false,
                    IsSessionFinal = false,
                    AudioStart = snapshot.AudioStart,
                    AudioEnd = snapshot.AudioEnd,
                    Segments = new[] { OffsetSegment(snapshot, segment) }
                });
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Structured transcription progress callback failed: {ex.Message}");
            }
        }

        private TranscriptSegment OffsetSegment(SnapshotWorkItem snapshot, TranscriptSegment segment,
            IReadOnlyDictionary<string, string>? identities = null)
        {
            var speaker = ResolveSpeaker(snapshot, segment.Speaker, identities);
            return new TranscriptSegment
            {
                Id = $"{snapshot.Revision}:{segment.Id}",
                Speaker = speaker,
                LocalSpeaker = segment.Speaker,
                SpeakerScope = speaker is null ? TranscriptSpeakerScope.Request : TranscriptSpeakerScope.Session,
                SpeakerScopeId = GetSpeakerScopeId(snapshot, speaker),
                Text = segment.Text, ChannelIndex = segment.ChannelIndex,
                Start = segment.Start + snapshot.AudioStart, End = segment.End + snapshot.AudioStart,
                Words = segment.Words.Select(word =>
                {
                    var local = word.Speaker ?? segment.Speaker;
                    var identity = ResolveSpeaker(snapshot, local, identities);
                    return new TranscriptWord
                    {
                        Text = word.Text, Speaker = identity, LocalSpeaker = local,
                        SpeakerScope = identity is null ? TranscriptSpeakerScope.Request : TranscriptSpeakerScope.Session,
                        SpeakerScopeId = GetSpeakerScopeId(snapshot, identity),
                        Start = word.Start + snapshot.AudioStart, End = word.End + snapshot.AudioStart
                    };
                }).ToArray()
            };
        }

        private string GetSpeakerScopeId(SnapshotWorkItem snapshot, string? identity) =>
            identity is null ? $"{_speakerSessionId}/{snapshot.Revision}" : _speakerSessionId;

        private static string? ResolveSpeaker(SnapshotWorkItem snapshot, string? label, IReadOnlyDictionary<string, string>? identities)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            if (snapshot.SpeakerReferences.ContainsKey(label)) return label;
            return identities is not null && identities.TryGetValue(label, out var identity) ? identity : null;
        }

        private IReadOnlyDictionary<string, string> LearnSpeakerReferences(SnapshotWorkItem snapshot, IReadOnlyList<TranscriptSegment> segments)
        {
            var identities = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!Options.EnableAutomaticSpeakerReferences || !Options.TranscriptionModel.SupportsDiarizedJson()) return identities;
            foreach (var group in segments.Where(s => !string.IsNullOrWhiteSpace(s.Speaker)).GroupBy(s => s.Speaker!))
            {
                if (snapshot.SpeakerReferences.ContainsKey(group.Key) || _speakerReferences.Count >= 4) continue;
                foreach (var segment in group.OrderByDescending(s => s.End - s.Start))
                {
                    if (!segment.Start.HasValue || !segment.End.HasValue) continue;
                    var start = segment.Start.Value + TimeSpan.FromMilliseconds(150);
                    var end = segment.End.Value - TimeSpan.FromMilliseconds(150);
                    if (end - start < TimeSpan.FromSeconds(2) || start < TimeSpan.Zero ||
                        end > TimeSpan.FromSeconds(snapshot.Audio.Length / (double)BYTES_PER_SECOND)) continue;
                    end = start + TimeSpan.FromSeconds(Math.Min(5, (end - start).TotalSeconds));
                    // Never enroll a clip that overlaps another speaker or lacks reliable boundaries.
                    if (segments.Any(other => other.Speaker != segment.Speaker &&
                        (!other.Start.HasValue || !other.End.HasValue || other.Start.Value < end && other.End.Value > start))) continue;
                    int offset = DurationToBytes(start);
                    int count = Math.Min(DurationToBytes(end - start), snapshot.Audio.Length - offset) & ~1;
                    if (count < BYTES_PER_SECOND * 2) continue;
                    int audible = 0;
                    for (int i = offset; i < offset + count; i += 2)
                        if (Math.Abs((int)(short)(snapshot.Audio[i] | snapshot.Audio[i + 1] << 8)) > 200) audible++;
                    if (audible < count / 20) continue;
                    var clip = new byte[count];
                    Buffer.BlockCopy(snapshot.Audio, offset, clip, 0, count);
                    string name;
                    do { name = $"speaker_{++_nextSpeakerId}"; } while (_speakerReferences.ContainsKey(name));
                    _speakerReferences.Add(name, "data:audio/wav;base64," + Convert.ToBase64String(BuildWav(clip, SAMPLE_RATE)));
                    identities.Add(group.Key, name);
                    break;
                }
            }
            return identities;
        }

        private void PublishCompletedSnapshot(SnapshotWorkItem snapshot, IReadOnlyList<TranscriptSegment> segments,
            string providerText, string? detectedLanguage, Action<string> onTextChunk)
        {
            StructuredTranscript transcript;
            lock (_sync)
            {
                if (snapshot.SessionId != _sessionId) return;
                if (snapshot.AudioEnd <= TimeSpan.FromSeconds(_confirmedBytes / (double)BYTES_PER_SECOND)) return;
                var identities = LearnSpeakerReferences(snapshot, segments);
                var additions = segments.Select(segment => OffsetSegment(snapshot, segment, identities)).ToArray();
                var text = additions.Length > 0 ? string.Join(Environment.NewLine, additions.Select(FormatSegment)) : providerText;
                _confirmedText = JoinText(_confirmedText, text);
                if (additions.Length > 0) _confirmedSegments.AddRange(additions);
                else if (!string.IsNullOrEmpty(text)) _confirmedSegments.Add(new TranscriptSegment
                { Id = $"{snapshot.Revision}", Text = text, Start = snapshot.AudioStart, End = snapshot.AudioEnd });
                _confirmedBytes = (long)Math.Round(snapshot.AudioEnd.TotalSeconds * BYTES_PER_SECOND);
                int remaining = (int)_capturedAudio.Length - snapshot.Audio.Length;
                if (remaining < 0) throw new InvalidOperationException("Audio buffer coverage mismatch.");
                var buffer = _capturedAudio.GetBuffer();
                Buffer.BlockCopy(buffer, snapshot.Audio.Length, buffer, 0, remaining);
                _capturedAudio.SetLength(remaining);
                _capturedAudio.Position = remaining;
                _bufferStartBytes = _confirmedBytes;
                _latestPublishedText = _confirmedText;
                transcript = CreateTranscript(false, detectedLanguage);
                _latestStructuredTranscript = transcript;
            }
            PublishCallbacks(transcript, transcript.Text, onTextChunk);
            PublishStatus(_acceptAudio ? HttpTranscriptionState.Recording : HttpTranscriptionState.Finalizing);
        }

        private StructuredTranscript CreateTranscript(bool final, string? language = null) => new()
        {
            ProviderId = "openai", ModelId = Options.GetModelId(), Text = _confirmedText,
            Language = language ?? Options.Language, Duration = TimeSpan.FromSeconds(_confirmedBytes / (double)BYTES_PER_SECOND),
            IsFinal = true, IsSpeechFinal = true, SnapshotRevision = _snapshotRevision,
            IsSnapshotComplete = true, IsSessionFinal = final,
            AudioStart = TimeSpan.Zero, AudioEnd = TimeSpan.FromSeconds(_confirmedBytes / (double)BYTES_PER_SECOND),
            Segments = _confirmedSegments.ToArray()
        };

        private void PublishSessionFinal(Action<string> onTextChunk)
        {
            StructuredTranscript transcript;
            lock (_sync)
            {
                _latestPublishedText = _confirmedText;
                transcript = CreateTranscript(true);
                _latestStructuredTranscript = transcript;
            }
            PublishCallbacks(transcript, transcript.Text, onTextChunk);
        }

        private void PublishStatus(HttpTranscriptionState state)
        {
            HttpTranscriptionStatus status;
            lock (_sync)
            {
                var captured = TimeSpan.FromSeconds(_capturedBytes / (double)BYTES_PER_SECOND);
                var confirmed = TimeSpan.FromSeconds(_confirmedBytes / (double)BYTES_PER_SECOND);
                status = new HttpTranscriptionStatus
                {
                    State = state, Text = _confirmedText, RecordedAudioEnd = captured, ConfirmedAudioEnd = confirmed,
                    CaptureStopConfirmed = !_acceptAudio && !_hardwareFailed,
                    MissingSections = confirmed < captured ? new[] { new TranscriptionAudioRange(confirmed, captured) } : Array.Empty<TranscriptionAudioRange>()
                };
                LatestStatus = status;
            }
            try { OnStatusChanged?.Invoke(status); }
            catch { _logAction(LogLevel.Warn, "Transcription status callback failed."); }
        }

        private void ReportError(HttpTranscriptionError error)
        {
            _logAction(error.IsFatal ? LogLevel.Error : LogLevel.Warn, $"HTTP transcription: {error.Code}; section={error.SectionId}; status={error.HttpStatus}; retry={error.WillRetry}.");
            try { OnTranscriptionError?.Invoke(error); }
            catch { _logAction(LogLevel.Warn, "Transcription error callback failed."); }
        }

        private static string JoinText(string prefix, string text) => string.IsNullOrEmpty(prefix) ? text :
            string.IsNullOrEmpty(text) ? prefix : prefix + Environment.NewLine + text;

        private sealed class TranscriptionRequestException(int status, bool retryable, TimeSpan? retryAfter, string? requestId) : Exception
        {
            public int Status { get; } = status;
            public bool Retryable { get; } = retryable;
            public TimeSpan? RetryAfter { get; } = retryAfter;
            public string? RequestId { get; } = requestId;
        }

        private void PublishCallbacks(
            StructuredTranscript transcript,
            string? textToPublish,
            Action<string> onTextChunk)
        {
            if (textToPublish is not null)
            {
                try
                {
                    onTextChunk(textToPublish);
                }
                catch (Exception ex)
                {
                    _logAction(LogLevel.Error, $"Text chunk callback failed: {ex.Message}");
                }
            }

            try
            {
                OnStructuredTranscriptionReceived?.Invoke(transcript);
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Structured transcription callback failed: {ex.Message}");
            }
        }

        private static string? GetDetectedLanguage(JsonElement root)
        {
            if (!root.TryGetProperty("languages", out var languages) || languages.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var language in languages.EnumerateArray())
            {
                if (language.TryGetProperty("code", out var code))
                {
                    return code.GetString();
                }
            }

            return null;
        }

        private void PublishHypothesisUpdate(SnapshotWorkItem snapshot, string latestHypothesis, Action<string> onTextChunk)
        {
            if (string.IsNullOrEmpty(latestHypothesis))
            {
                return;
            }

            string? textToPublish = null;

            lock (_sync)
            {
                if (snapshot.SessionId != _sessionId)
                {
                    return;
                }

                string? mergedHypothesis = JoinText(snapshot.DisplayFloor, latestHypothesis);

                if (string.IsNullOrEmpty(mergedHypothesis))
                {
                    return;
                }

                if (!string.Equals(_latestPublishedText, mergedHypothesis, StringComparison.Ordinal))
                {
                    _latestPublishedText = mergedHypothesis;
                    textToPublish = mergedHypothesis;
                }
            }

            if (textToPublish != null)
            {
                try
                {
                    onTextChunk(textToPublish);
                }
                catch (Exception ex)
                {
                    _logAction(LogLevel.Error, $"Text chunk callback failed: {ex.Message}");
                }
            }
        }

        private void OnAudioDataReceived(object sender, MicrophoneAudioReceivedEventArgs e)
        {
            try
            {
                byte[] chunk = Convert.FromBase64String(e.Base64EncodedPcm16Audio);

                lock (_sync)
                {
                    if (!_acceptAudio) return;
                    if ((chunk.Length & 1) != 0) throw new InvalidOperationException("PCM16 data must be sample-aligned.");
                    _capturedBytes += chunk.Length;
                    if (_capturedAudio.Length + chunk.Length > DurationToBytes(Options.MaxBufferedDuration))
                    {
                        _fatal = true;
                        ReportError(new HttpTranscriptionError { Code = "audio_buffer_full", IsFatal = true,
                            Message = "Provider cannot keep up with capture; unconfirmed tail is incomplete." });
                        _runCts?.Cancel();
                        return;
                    }
                    _capturedAudio.Write(chunk, 0, chunk.Length);
                    if (_capturedAudio.Length >= DurationToBytes(Options.MaxSectionDuration) && _audioReady.CurrentCount == 0)
                        _audioReady.Release();
                }
            }
            catch (Exception ex)
            {
                OnHardwareError(this, ex.GetType().Name);
            }
        }

        private void ValidateOptions()
        {
            if (Options.DiarizationMinimumSectionDuration <= TimeSpan.Zero ||
                Options.MaxSectionDuration < TimeSpan.FromMilliseconds(1) || Options.MaxSectionDuration > TimeSpan.FromMinutes(5) ||
                Options.MaxBufferedDuration < Options.MaxSectionDuration || Options.MaxBufferedDuration > TimeSpan.FromMinutes(30) ||
                Options.MaxRetries < 0 || Options.MaxRetries > 10 || Options.RetryDelay < TimeSpan.Zero ||
                Options.RequestTimeout <= TimeSpan.Zero || Options.StreamIdleTimeout <= TimeSpan.Zero ||
                Options.FinalizationTimeout <= TimeSpan.Zero || Options.HardwareStopTimeout <= TimeSpan.Zero ||
                Options.MinimumUtteranceDuration > Options.MaxSectionDuration)
                throw new InvalidOperationException("Invalid section, buffer, retry or timeout limits.");

            if (!Options.TranscriptionModel.SupportsHttpStreamingTranscription())
            {
                throw new InvalidOperationException($"{Options.TranscriptionModel} does not support streamed HTTP transcription responses.");
            }

            if (Options.TranscriptionModel.SupportsDiarizedJson() &&
                !string.IsNullOrWhiteSpace(Options.Prompt))
            {
                _logAction(LogLevel.Warn, "Ignoring transcription prompt because gpt-4o-transcribe-diarize does not support prompting.");
            }

            if (Options.KnownSpeakerReferences.Count > 4)
            {
                throw new InvalidOperationException("OpenAI diarization supports at most four known speaker references.");
            }

            if (Options.KnownSpeakerReferences.Count > 0 &&
                !Options.TranscriptionModel.SupportsDiarizedJson())
            {
                throw new InvalidOperationException("Known speaker references require Gpt4oTranscribeDiarize.");
            }

            foreach (var reference in Options.KnownSpeakerReferences)
            {
                if (string.IsNullOrWhiteSpace(reference.Key) ||
                    !reference.Value.StartsWith("data:audio/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Known speaker references require a non-empty label and an audio data URL.");
                }
            }

            foreach (var keyword in Options.Keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword) || keyword.IndexOfAny(new[] { '<', '>', '\r', '\n' }) >= 0)
                {
                    throw new InvalidOperationException("OpenAI transcription keywords must be non-empty single-line text without angle brackets.");
                }
            }

            if (Options.SnapshotInterval <= TimeSpan.Zero)
            {
                throw new InvalidOperationException("SnapshotInterval must be greater than zero.");
            }

            if (Options.MinimumUtteranceDuration <= TimeSpan.Zero)
            {
                throw new InvalidOperationException("MinimumUtteranceDuration must be greater than zero.");
            }

            if (Options.LeadingTrimDuration < TimeSpan.Zero)
            {
                throw new InvalidOperationException("LeadingTrimDuration cannot be negative.");
            }

            if (Options.PrefixPadding < TimeSpan.Zero)
            {
                throw new InvalidOperationException("PrefixPadding cannot be negative.");
            }

            if (Options.SilenceDuration <= TimeSpan.Zero)
            {
                throw new InvalidOperationException("SilenceDuration must be greater than zero.");
            }

            if (Options.VadThreshold <= 0 || Options.VadThreshold >= 1)
            {
                throw new InvalidOperationException("VadThreshold must be between 0 and 1.");
            }
        }

        private void ResetRunState(bool clearTranscriptResult)
        {
            _snapshotInFlight = false;
            _snapshotRevision = 0;
            _capturedBytes = _confirmedBytes = _bufferStartBytes = 0;
            _hardwareFailed = _fatal = _retryExhausted = false;
            _confirmedText = string.Empty;
            _confirmedSegments.Clear();
            _speakerReferences.Clear();
            foreach (var reference in Options.KnownSpeakerReferences) _speakerReferences.Add(reference.Key, reference.Value);
            _nextSpeakerId = 0;
            _speakerSessionId = Guid.NewGuid().ToString("N");
            LatestStatus = null;
            while (_audioReady.Wait(0)) { }
            _latestPublishedText = string.Empty;
            if (clearTranscriptResult)
            {
                _latestStructuredTranscript = null;
            }

            _capturedAudio.Dispose();
            _capturedAudio = new MemoryStream();
        }

        private static int DurationToBytes(TimeSpan duration)
        {
            return (int)Math.Max(0, Math.Round(duration.TotalSeconds * BYTES_PER_SECOND)) & ~1;
        }

        private static byte[] BuildWav(byte[] pcmData, int sampleRate)
        {
            const int channels = 1;
            const int bitsPerSample = 16;
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            int blockAlign = channels * bitsPerSample / 8;

            using var ms = new MemoryStream(44 + pcmData.Length);
            using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            writer.Write(36 + pcmData.Length);
            writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });

            writer.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write((short)blockAlign);
            writer.Write((short)bitsPerSample);

            writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            writer.Write(pcmData.Length);
            writer.Write(pcmData);

            writer.Flush();
            return ms.ToArray();
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed || _disposing) throw new ObjectDisposedException(nameof(OpenAiHttpLiveTranscriber));
        }

        public async ValueTask DisposeAsync()
        {
            Task? completion;
            lock (_sync)
            {
                if (_isDisposed) return;
                _disposing = true;
                _runCts?.Cancel();
                _finalizationCts?.Cancel();
                completion = _completion?.Task;
            }
            await StopMicrophoneAsync();
            if (completion is not null) await completion;
            lock (_sync)
            {
                if (_isDisposed) return;
                _capturedAudio.Dispose();
                _audioReady.Dispose();
                _isDisposed = true;
                if (_ownsHttpClient) _httpClient.Dispose();
            }
        }

        private static TranscriptSegment? TryParseDiarizedSegment(JsonElement root)
        {
            var segment = root.TryGetProperty("segment", out var segmentElement)
                ? segmentElement
                : root;

            string? text = null;
            if (segment.TryGetProperty("text", out var textElement))
            {
                text = textElement.GetString();
            }
            else if (segment.TryGetProperty("delta", out var deltaElement))
            {
                text = deltaElement.GetString();
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            string? speaker = null;
            if (segment.TryGetProperty("speaker", out var speakerElement))
            {
                speaker = speakerElement.GetString() ?? speaker;
            }
            else if (segment.TryGetProperty("speaker_label", out var speakerLabelElement))
            {
                speaker = speakerLabelElement.GetString() ?? speaker;
            }

            return new TranscriptSegment
            {
                Id = TryGetSegmentId(root, segment),
                Speaker = speaker,
                Text = text.Trim(),
                Start = TryGetSeconds(segment, "start"),
                End = TryGetSeconds(segment, "end"),
                Words = ParseWords(segment, speaker)
            };
        }

        private static IReadOnlyList<TranscriptWord> ParseWords(JsonElement segment, string? speaker)
        {
            if (!segment.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array)
                return Array.Empty<TranscriptWord>();
            return words.EnumerateArray().Select(word => new TranscriptWord
            {
                Text = word.TryGetProperty("word", out var text) || word.TryGetProperty("text", out text) ? text.GetString() ?? string.Empty : string.Empty,
                Speaker = word.TryGetProperty("speaker", out var label) ? label.GetString() : speaker,
                Start = TryGetSeconds(word, "start"), End = TryGetSeconds(word, "end")
            }).ToArray();
        }

        private static string? TryGetSegmentId(JsonElement root, JsonElement segment)
        {
            foreach (var element in new[] { segment, root })
            {
                if (element.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                {
                    return idElement.GetString();
                }

                if (element.TryGetProperty("segment_id", out var segmentIdElement) &&
                    segmentIdElement.ValueKind == JsonValueKind.String)
                {
                    return segmentIdElement.GetString();
                }
            }

            return null;
        }

        private static string GetSegmentKey(TranscriptSegment segment) =>
            !string.IsNullOrWhiteSpace(segment.Id)
                ? $"id:{segment.Id}"
                : string.Create(CultureInfo.InvariantCulture,
                    $"value:{segment.Speaker}|{segment.Start?.Ticks}|{segment.End?.Ticks}|{segment.Text}");

        private static TimeSpan? GetSegmentCoverageEnd(IReadOnlyList<TranscriptSegment> segments)
        {
            TimeSpan? result = null;
            foreach (var segment in segments)
            {
                if (segment.End.HasValue && (!result.HasValue || segment.End.Value > result.Value))
                {
                    result = segment.End;
                }
            }

            return result;
        }

        private static string FormatCoverage(TimeSpan? coverage) =>
            coverage.HasValue
                ? $"{coverage.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s"
                : "unknown";

        private static string FormatSegment(TranscriptSegment segment) =>
            string.IsNullOrWhiteSpace(segment.Speaker)
                ? segment.Text
                : $"[{segment.Speaker}] {segment.Text}";

        private static TimeSpan? TryGetSeconds(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : null;
        }

        private readonly record struct SnapshotWorkItem(
            int SessionId,
            long Revision,
            byte[] Audio,
            bool IsFinal,
            string DisplayFloor,
            TimeSpan AudioStart,
            TimeSpan AudioEnd)
        {
            public IReadOnlyDictionary<string, string> SpeakerReferences { get; init; } = new Dictionary<string, string>();
        }
    }
}
