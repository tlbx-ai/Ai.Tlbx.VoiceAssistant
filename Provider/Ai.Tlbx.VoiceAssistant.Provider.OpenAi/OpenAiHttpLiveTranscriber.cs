using System;
using System.Globalization;
using System.IO;
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
    /// This avoids the realtime WebSocket API by repeatedly uploading the full
    /// audio captured since the user pressed the push-to-talk button.
    /// </summary>
    public sealed class OpenAiHttpLiveTranscriber : IAsyncDisposable
    {
        private const string TRANSCRIPTION_ENDPOINT = "https://api.openai.com/v1/audio/transcriptions";
        private const int SAMPLE_RATE = 24000;
        private const int BYTES_PER_SECOND = SAMPLE_RATE * 2; // mono PCM16

        private readonly IAudioHardwareAccess _hardwareAccess;
        private readonly HttpClient _httpClient;
        private readonly Action<LogLevel, string> _logAction;
        private readonly object _sync = new();

        private CancellationTokenSource? _runCts;
        private bool _isDisposed;
        private bool _isRunning;
        private bool _snapshotInFlight;
        private int _sessionId;
        private int _lastSnapshotByteCount;
        private MemoryStream _capturedAudio = new();
        private string _latestPublishedText = string.Empty;

        public OpenAiHttpLiveTranscriptionOptions Options { get; }

        public OpenAiHttpLiveTranscriber(
            IAudioHardwareAccess hardwareAccess,
            OpenAiHttpLiveTranscriptionOptions? options = null,
            string? apiKey = null,
            Action<LogLevel, string>? logAction = null)
        {
            _hardwareAccess = hardwareAccess ?? throw new ArgumentNullException(nameof(hardwareAccess));
            _logAction = logAction ?? ((level, message) => { /* no-op */ });
            Options = options ?? new OpenAiHttpLiveTranscriptionOptions();

            var resolvedApiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                ?? throw new InvalidOperationException("OpenAI API key must be provided or set in OPENAI_API_KEY environment variable");

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", resolvedApiKey);
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
        public async Task TranscribeLive(Action<string> onTextChunk, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(onTextChunk);
            ThrowIfDisposed();
            ValidateOptions();

            CancellationTokenSource runCts;
            lock (_sync)
            {
                if (_isRunning)
                {
                    throw new InvalidOperationException("Live transcription is already running.");
                }

                ResetRunState();
                _sessionId++;
                _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                runCts = _runCts;
                _isRunning = true;
            }

            try
            {
                await _hardwareAccess.InitAudioAsync();

                bool started = await _hardwareAccess.StartRecordingAudio(OnAudioDataReceived, AudioSampleRate.Rate24000);
                if (!started)
                {
                    throw new InvalidOperationException("Failed to start microphone recording.");
                }

                await RunLoopAsync(onTextChunk, runCts.Token);
            }
            finally
            {
                await StopRecordingSafeAsync();

                lock (_sync)
                {
                    _isRunning = false;

                    _runCts?.Dispose();
                    _runCts = null;

                    ResetRunState();
                }
            }
        }

        public Task StopAsync()
        {
            lock (_sync)
            {
                _runCts?.Cancel();
            }

            return Task.CompletedTask;
        }

        private async Task RunLoopAsync(Action<string> onTextChunk, CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(Options.SnapshotInterval, cancellationToken);

                    var snapshot = TryCreateSnapshot(forceFinal: false);
                    if (snapshot.HasValue)
                    {
                        await ProcessSnapshotAsync(snapshot.Value, onTextChunk);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }

            // Stop recording before the final upload so the tail of the utterance is included.
            await StopRecordingSafeAsync();

            var finalSnapshot = TryCreateSnapshot(forceFinal: true);
            if (finalSnapshot.HasValue)
            {
                await ProcessSnapshotAsync(finalSnapshot.Value, onTextChunk);
            }
        }

        private SnapshotWorkItem? TryCreateSnapshot(bool forceFinal)
        {
            lock (_sync)
            {
                if (_snapshotInFlight)
                {
                    return null;
                }

                int capturedBytes = (int)_capturedAudio.Length;
                if (capturedBytes == 0)
                {
                    return null;
                }

                if (!forceFinal)
                {
                    if (capturedBytes < DurationToBytes(Options.MinimumUtteranceDuration))
                    {
                        return null;
                    }

                    if (capturedBytes <= _lastSnapshotByteCount)
                    {
                        return null;
                    }
                }

                byte[] fullAudio = _capturedAudio.ToArray();
                byte[] effectiveAudio = TrimLeadingAudio(fullAudio, Options.LeadingTrimDuration);
                if (effectiveAudio.Length == 0)
                {
                    return null;
                }

                _snapshotInFlight = true;
                _lastSnapshotByteCount = capturedBytes;

                return new SnapshotWorkItem(
                    _sessionId,
                    effectiveAudio,
                    forceFinal,
                    _latestPublishedText);
            }
        }

        private async Task ProcessSnapshotAsync(SnapshotWorkItem snapshot, Action<string> onTextChunk)
        {
            try
            {
                await TranscribeSnapshotAsync(snapshot, onTextChunk);
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"HTTP live transcription snapshot failed: {ex.Message}");
            }
            finally
            {
                lock (_sync)
                {
                    _snapshotInFlight = false;
                }
            }
        }

        private async Task TranscribeSnapshotAsync(SnapshotWorkItem snapshot, Action<string> onTextChunk)
        {
            byte[] wavAudio = BuildWav(snapshot.Audio, SAMPLE_RATE);

            using var form = new MultipartFormDataContent();
            using var audioContent = new ByteArrayContent(wavAudio);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audioContent, "file", $"hold-transcribe-{snapshot.SessionId}.wav");
            form.Add(new StringContent(Options.TranscriptionModel.ToApiString()), "model");
            form.Add(new StringContent("true"), "stream");
            form.Add(new StringContent("server_vad"), "chunking_strategy[type]");
            form.Add(new StringContent(((int)Math.Round(Options.PrefixPadding.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture)), "chunking_strategy[prefix_padding_ms]");
            form.Add(new StringContent(((int)Math.Round(Options.SilenceDuration.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture)), "chunking_strategy[silence_duration_ms]");
            form.Add(new StringContent(Options.VadThreshold.ToString("0.###", CultureInfo.InvariantCulture)), "chunking_strategy[threshold]");

            if (!string.IsNullOrWhiteSpace(Options.Language))
            {
                form.Add(new StringContent(Options.Language), "language");
            }

            if (!string.IsNullOrWhiteSpace(Options.Prompt))
            {
                form.Add(new StringContent(Options.Prompt), "prompt");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, TRANSCRIPTION_ENDPOINT)
            {
                Content = form
            };

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                CancellationToken.None);

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"OpenAI transcription failed: {response.StatusCode} - {body}");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(responseStream);
            var hypothesis = new StringBuilder();
            string? currentEvent = null;
            var currentData = new StringBuilder();

            while (true)
            {
                string? line = await reader.ReadLineAsync();
                if (line == null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    ApplyStreamingEvent(snapshot, currentEvent, currentData.ToString(), hypothesis, onTextChunk);
                    currentEvent = null;
                    currentData.Clear();
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

            ApplyStreamingEvent(snapshot, currentEvent, currentData.ToString(), hypothesis, onTextChunk);
        }

        private void ApplyStreamingEvent(
            SnapshotWorkItem snapshot,
            string? eventName,
            string data,
            StringBuilder hypothesis,
            Action<string> onTextChunk)
        {
            if (string.IsNullOrWhiteSpace(data) || string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {
                return;
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

                    case "transcript.text.done":
                        if (root.TryGetProperty("text", out var textElement))
                        {
                            string finalText = textElement.GetString() ?? hypothesis.ToString();
                            if (finalText.Length > 0)
                            {
                                hypothesis.Clear();
                                hypothesis.Append(finalText);
                                PublishHypothesisUpdate(snapshot, finalText, onTextChunk);
                            }
                        }
                        break;
                }
            }
            catch (JsonException)
            {
                _logAction(LogLevel.Warn, $"Could not parse transcription stream event: {data}");
            }
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

                string? mergedHypothesis = snapshot.IsFinal
                    ? latestHypothesis
                    : MergeSnapshotHypothesis(snapshot.DisplayFloor, _latestPublishedText, latestHypothesis);

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
                    _capturedAudio.Write(chunk, 0, chunk.Length);
                }
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error receiving microphone audio for HTTP live transcription: {ex.Message}");
            }
        }

        private void ValidateOptions()
        {
            if (Options.TranscriptionModel == OpenAiTranscriptionModel.Whisper1)
            {
                throw new InvalidOperationException("Whisper-1 does not support streamed HTTP transcription responses.");
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

        private void ResetRunState()
        {
            _snapshotInFlight = false;
            _lastSnapshotByteCount = 0;
            _latestPublishedText = string.Empty;

            _capturedAudio.Dispose();
            _capturedAudio = new MemoryStream();
        }

        private static int DurationToBytes(TimeSpan duration)
        {
            return (int)Math.Max(0, Math.Round(duration.TotalSeconds * BYTES_PER_SECOND));
        }

        private static byte[] TrimLeadingAudio(byte[] audio, TimeSpan trimDuration)
        {
            int trimBytes = DurationToBytes(trimDuration);
            if (trimBytes <= 0)
            {
                return audio;
            }

            // Keep sample alignment for PCM16.
            if ((trimBytes & 1) == 1)
            {
                trimBytes--;
            }

            if (trimBytes >= audio.Length)
            {
                return Array.Empty<byte>();
            }

            byte[] trimmed = new byte[audio.Length - trimBytes];
            Buffer.BlockCopy(audio, trimBytes, trimmed, 0, trimmed.Length);
            return trimmed;
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

        private async Task StopRecordingSafeAsync()
        {
            try
            {
                await _hardwareAccess.StopRecordingAudio();
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Warn, $"Stopping microphone recording failed: {ex.Message}");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(OpenAiHttpLiveTranscriber));
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed)
            {
                return;
            }

            try
            {
                await StopAsync();
                await StopRecordingSafeAsync();
            }
            finally
            {
                lock (_sync)
                {
                    _runCts?.Dispose();
                    _capturedAudio.Dispose();
                    _isDisposed = true;
                }

                _httpClient.Dispose();
            }
        }

        private static string? MergeSnapshotHypothesis(string displayFloor, string currentPublished, string latestHypothesis)
        {
            if (string.IsNullOrWhiteSpace(latestHypothesis))
            {
                return null;
            }

            if (string.IsNullOrEmpty(displayFloor))
            {
                return latestHypothesis;
            }

            if (latestHypothesis.StartsWith(currentPublished, StringComparison.Ordinal))
            {
                return latestHypothesis;
            }

            if (currentPublished.StartsWith(latestHypothesis, StringComparison.Ordinal))
            {
                return null;
            }

            if (latestHypothesis.StartsWith(displayFloor, StringComparison.Ordinal))
            {
                return latestHypothesis;
            }

            if (displayFloor.StartsWith(latestHypothesis, StringComparison.Ordinal))
            {
                return null;
            }

            if (latestHypothesis.Length < displayFloor.Length)
            {
                return null;
            }

            if (HasStrongPrefixOverlap(displayFloor, latestHypothesis) ||
                HasStrongPrefixOverlap(currentPublished, latestHypothesis))
            {
                return latestHypothesis;
            }

            return latestHypothesis.Length > currentPublished.Length + 12
                ? latestHypothesis
                : null;
        }

        private static bool HasStrongPrefixOverlap(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            int commonPrefixLength = GetCommonPrefixLength(left, right);
            int shorterLength = Math.Min(left.Length, right.Length);

            if (shorterLength <= 8)
            {
                return commonPrefixLength == shorterLength;
            }

            return commonPrefixLength >= Math.Min(12, shorterLength) ||
                (commonPrefixLength * 4) >= (shorterLength * 3);
        }

        private static int GetCommonPrefixLength(string left, string right)
        {
            int max = Math.Min(left.Length, right.Length);
            int index = 0;

            while (index < max && left[index] == right[index])
            {
                index++;
            }

            return index;
        }

        private readonly record struct SnapshotWorkItem(
            int SessionId,
            byte[] Audio,
            bool IsFinal,
            string DisplayFloor);
    }
}
