using System.Buffers.Binary;
using System.Text;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.Mistral;

/// <summary>Half-duplex Voxtral STT → application reasoning → TTS pipeline with local silence detection.</summary>
public sealed class MistralVoiceProvider : IVoiceProvider
{
    private readonly MistralAudioClient _audio;
    private readonly Action<LogLevel, string> _log;
    private readonly List<ChatMessage> _history = new();
    private readonly object _sync = new();
    private readonly MemoryStream _utterance = new();
    private MistralVoiceSettings? _settings;
    private CancellationTokenSource? _session, _turn;
    private Task _processing = Task.CompletedTask;
    private int _silenceBytes;
    private bool _busy, _disposed;
    public bool IsConnected { get; private set; }
    public AudioSampleRate RequiredInputSampleRate => AudioSampleRate.Rate16000;
    public Action<ChatMessage>? OnMessageReceived { get; set; }
    public Action<string>? OnAudioReceived { get; set; }
    public Func<TimeSpan?, Task<bool>>? WaitForPlaybackDrainAsync { get; set; }
    public Action<string>? OnStatusChanged { get; set; }
    public Action<string>? OnError { get; set; }
    public Action? OnInterruptDetected { get; set; }
    public Action<UsageReport>? OnUsageReceived { get; set; }
    public Action<string>? OnTranscriptionDelta { get; set; }
    public Action<string>? OnTranscriptionCompleted { get; set; }

    public MistralVoiceProvider(MistralAudioClient audio, Action<LogLevel, string>? logAction = null)
    { _audio = audio; _log = logAction ?? ((_, _) => { }); }

    /// <summary>Forward application-owned tool/progress messages through the shared chat.</summary>
    public void ReportBackendMessage(ChatMessage message) => OnMessageReceived?.Invoke(message);
    /// <summary>Forward application-owned backend usage through the shared usage ledger.</summary>
    public void ReportBackendUsage(UsageReport usage) => OnUsageReceived?.Invoke(usage);

    public Task ConnectAsync(IVoiceSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnected) throw new InvalidOperationException("Disconnect before reconnecting.");
        var value = Validate(settings);
        lock (_sync)
        {
            _settings = value; _history.Clear(); _utterance.SetLength(0); _silenceBytes = 0;
            _session?.Dispose(); _session = new CancellationTokenSource(); IsConnected = true;
        }
        OnStatusChanged?.Invoke("Voxtral 3 ready (speech pipeline)");
        return Task.CompletedTask;
    }

    public Task UpdateSettingsAsync(IVoiceSettings settings)
    {
        var value = Validate(settings);
        lock (_sync)
        {
            if (_busy) throw new InvalidOperationException("Wait for the current turn before updating settings.");
            _settings = value;
        }
        return Task.CompletedTask;
    }

    private static MistralVoiceSettings Validate(IVoiceSettings settings)
    {
        if (settings is not MistralVoiceSettings value) throw new ArgumentException("Expected MistralVoiceSettings.", nameof(settings));
        if (!value.TranscriptionOnly && value.RespondAsync == null) throw new ArgumentException("Configure RespondAsync or TranscriptionOnly.", nameof(settings));
        if (value.SilenceDurationMs < 100 || value.MaxUtteranceSeconds is < 1 or > 120 || value.SpeechThreshold is <= 0 or >= 1)
            throw new ArgumentException("Invalid silence detection limits.", nameof(settings));
        if (value.TalkingSpeed != 1) throw new ArgumentException("Voxtral uses inline pace tags; TalkingSpeed must be 1.", nameof(settings));
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Voice);
        return value;
    }

    public Task ProcessAudioAsync(string base64Audio)
    {
        var pcm = Convert.FromBase64String(base64Audio);
        if (pcm.Length % 2 != 0) throw new ArgumentException("Expected aligned PCM16 samples.", nameof(base64Audio));
        if (pcm.Length == 0) return Task.CompletedTask;
        lock (_sync)
        {
            if (!IsConnected || _busy) return Task.CompletedTask;
            var settings = _settings!;
            double energy = 0;
            for (var i = 0; i < pcm.Length; i += 2) { var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i)) / 32768.0; energy += sample * sample; }
            var speech = Math.Sqrt(energy / (pcm.Length / 2)) >= settings.SpeechThreshold;
            if (!speech && _utterance.Length == 0) return Task.CompletedTask;
            _utterance.Write(pcm);
            _silenceBytes = speech ? 0 : _silenceBytes + pcm.Length;
            if (_silenceBytes >= settings.SilenceDurationMs * 32 || _utterance.Length >= settings.MaxUtteranceSeconds * 32000)
                StartTurnLocked();
        }
        return Task.CompletedTask;
    }

    private void StartTurnLocked()
    {
        if (_utterance.Length == 0 || _busy) return;
        var pcm = _utterance.ToArray(); _utterance.SetLength(0); _silenceBytes = 0; _busy = true;
        _turn?.Dispose(); _turn = CancellationTokenSource.CreateLinkedTokenSource(_session!.Token);
        var settings = _settings!;
        _processing = Task.Run(() => RunTurnAsync(pcm, settings, _turn.Token));
    }

    private async Task RunTurnAsync(byte[] pcm, MistralVoiceSettings settings, CancellationToken ct)
    {
        try
        {
            OnStatusChanged?.Invoke("Transcribing...");
            var text = await _audio.TranscribeAsync(ToWav(pcm), settings, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            OnUsageReceived?.Invoke(new UsageReport { ProviderId = _audio.UseOpenRouter ? "openrouter" : "mistral", ModelId = settings.TranscriptionModel,
                OperationType = UsageOperationType.Transcription, InputAudioDuration = TimeSpan.FromSeconds(pcm.Length / 32000.0),
                MeasurementSource = UsageMeasurementSource.ClientMeasured, IsEstimated = true });
            if (string.IsNullOrWhiteSpace(text)) return;
            var user = ChatMessage.CreateUserMessage(text);
            // The orchestrator creates the shared user message from this callback.
            _history.Add(user); OnTranscriptionCompleted?.Invoke(text);
            if (settings.TranscriptionOnly) return;
            OnStatusChanged?.Invoke("Thinking...");
            var answer = await settings.RespondAsync!(_history.ToArray(), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(answer)) return;
            var assistant = ChatMessage.CreateAssistantMessage(answer);
            _history.Add(assistant); OnMessageReceived?.Invoke(assistant);
            OnStatusChanged?.Invoke("Speaking...");
            var mp3 = await _audio.SpeakAsync(answer, settings, ct).ConfigureAwait(false);
            OnUsageReceived?.Invoke(new UsageReport { ProviderId = _audio.UseOpenRouter ? "openrouter" : "mistral", ModelId = settings.SpeechModel,
                OperationType = UsageOperationType.SpeechGeneration, InputCharacters = answer.EnumerateRunes().Count(),
                MeasurementSource = UsageMeasurementSource.ClientMeasured, IsEstimated = true });
            var output = DecodeMp3(mp3);
            var playbackStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var offset = 0; offset < output.Length; offset += 4800)
            {
                ct.ThrowIfCancellationRequested();
                OnAudioReceived?.Invoke(Convert.ToBase64String(output, offset, Math.Min(4800, output.Length - offset)));
            }
            if (WaitForPlaybackDrainAsync != null &&
                !await WaitForPlaybackDrainAsync(TimeSpan.FromSeconds(output.Length / 48000.0 + 5)).WaitAsync(ct).ConfigureAwait(false))
            {
                OnInterruptDetected?.Invoke();
                throw new TimeoutException("Voxtral playback did not drain before the timeout.");
            }
            // Some hardware callbacks acknowledge dispatch before the physical queue has drained.
            // Keep this half-duplex microphone closed for at least the emitted audio's duration.
            var remaining = TimeSpan.FromSeconds(output.Length / 48000.0) - System.Diagnostics.Stopwatch.GetElapsedTime(playbackStarted);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _log(LogLevel.Error, ex.ToString()); OnError?.Invoke(ex.Message); }
        finally { lock (_sync) _busy = false; if (IsConnected) OnStatusChanged?.Invoke("Listening..."); }
    }

    public async Task SendInterruptAsync()
    {
        Task active;
        lock (_sync) { _turn?.Cancel(); _utterance.SetLength(0); _silenceBytes = 0; active = _processing; }
        OnInterruptDetected?.Invoke();
        await active.ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        Task active;
        lock (_sync) { if (!IsConnected && !_busy) return; IsConnected = false; _session?.Cancel(); _utterance.SetLength(0); active = _processing; }
        OnInterruptDetected?.Invoke();
        await active.ConfigureAwait(false);
        OnStatusChanged?.Invoke("Disconnected");
    }

    public Task InjectConversationHistoryAsync(IEnumerable<ChatMessage> messages)
    {
        lock (_sync) { if (_busy) throw new InvalidOperationException("Wait for the current turn."); _history.Clear(); _history.AddRange(messages); }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await DisconnectAsync().ConfigureAwait(false);
        _disposed = true; _session?.Dispose(); _turn?.Dispose(); _utterance.Dispose();
    }

    public static byte[] ToWav(byte[] pcm)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(pcm.Length + 36); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
        writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
        return stream.ToArray();
    }

    public static byte[] DecodeMp3(byte[] mp3)
    {
        using var stream = new MemoryStream(mp3); using var decoder = new NLayer.MpegFile(stream);
        if (decoder.Channels != 1) throw new InvalidDataException("Expected mono speech.");
        var decoded = new List<float>();
        var samples = new float[4096]; int count;
        while ((count = decoder.ReadSamples(samples, 0, samples.Length)) > 0)
            decoded.AddRange(samples.AsSpan(0, count).ToArray());
        if (decoded.Count == 0) throw new InvalidDataException("The speech response contains no decoded audio samples.");
        // The live OpenRouter MP3 is 22,050 Hz despite Mistral's documented 24 kHz.
        // Decode the actual stream rate and normalize to the hardware playback contract.
        var output = new byte[(int)Math.Round(decoded.Count * 24000.0 / decoder.SampleRate) * 2];
        for (var i = 0; i < output.Length / 2; i++)
        {
            var position = (double)i * decoder.SampleRate / 24000.0;
            var left = Math.Min((int)position, decoded.Count - 1);
            var right = Math.Min(left + 1, decoded.Count - 1);
            var value = decoded[left] + (decoded[right] - decoded[left]) * (position - left);
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2), (short)Math.Clamp(value * 32767, short.MinValue, short.MaxValue));
        }
        return output;
    }
}
