using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Telnyx;

/// <summary>One telephone call's PCM16 input/output. Resolve once per call; never share it across callers.</summary>
public sealed class TelnyxAudioHardware(TelnyxOptions options) : IAudioHardwareAccess
{
    private readonly object _gate = new();
    private readonly Channel<Playback> _playback = Channel.CreateBounded<Playback>(new BoundedChannelOptions(256) { SingleReader = true });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _marks = new();
    private readonly Queue<byte[]> _earlyAudio = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<JsonElement> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MicrophoneAudioReceivedEventHandler? _receive;
    private Action<LogLevel, string> _log = (_, _) => { };
    private Pcm16Resampler? _inputResampler;
    private string _inputCodec = "";
    private int _inputRate;
    private int _targetRate = 24000;
    private int _generation;
    private double _queuedMilliseconds;
    private bool _hasRecorded;
    private bool _attached;
    private bool _disposed;
    private DiagnosticLevel _diagnostic;
    public event EventHandler<string>? AudioError;
    /// <summary>Exact native events, including start/DTMF/mark and unsupported event types.</summary>
    public Action<JsonElement>? OnNativeEvent { get; set; }
    public string? StreamId { get; private set; }
    public string? CallControlId { get; private set; }
    public bool RemoteStopped { get; private set; }
    public Task<JsonElement> Started => _started.Task;
    public Task Completion => _completed.Task;

    /// <summary>Run an already authenticated media socket. One receive loop and one ordered send loop own the connection.</summary>
    public async Task RunAsync(WebSocket socket, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_attached || _disposed) throw new InvalidOperationException("The audio transport is single-call and single-use.");
            _attached = true;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var send = SendLoopAsync(socket, linked.Token);
        try
        {
            byte[] buffer = new byte[8192];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(), linked.Token);
                if (read.MessageType == WebSocketMessageType.Close) break;
                if (read.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Telnyx media events must be JSON text.");
                if (message.Length + read.Count > options.MaxWebSocketMessageBytes) throw new InvalidDataException("Telnyx media event exceeds the configured size limit.");
                message.Write(buffer, 0, read.Count);
                if (!read.EndOfMessage) continue;
                using var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                message.SetLength(0);
                var root = document.RootElement;
                OnNativeEvent?.Invoke(root.Clone());
                string? type = root.TryGetProperty("event", out var eventName) ? eventName.GetString() : null;
                if (type == "start") Start(root);
                else if (type == "media") Receive(root);
                else if (type == "mark" && root.TryGetProperty("mark", out var mark) && mark.TryGetProperty("name", out var name) &&
                    _marks.TryRemove(name.GetString() ?? "", out var waiter)) waiter.TrySetResult(true);
                else if (type == "stop") { RemoteStopped = true; break; }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Fail(ex); throw;
        }
        finally
        {
            linked.Cancel(); _playback.Writer.TryComplete();
            try { await send; } catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            foreach (var pair in _marks) pair.Value.TrySetResult(false);
            _marks.Clear();
            _started.TrySetCanceled(); _completed.TrySetResult();
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Call ended", close.Token); }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
            }
        }
    }

    private void Start(JsonElement root)
    {
        if (_started.Task.IsCompleted) throw new InvalidDataException("Duplicate media start.");
        var start = root.GetProperty("start"); var format = start.GetProperty("media_format");
        _inputCodec = format.GetProperty("encoding").GetString() ?? "";
        _inputRate = format.GetProperty("sample_rate").GetInt32();
        if (format.GetProperty("channels").GetInt32() != 1 || _inputRate is < 8000 or > 48000)
            throw new NotSupportedException("The telephone adapter requires mono audio at 8-48 kHz.");
        _ = TelnyxPcmCodec.Decode([], _inputCodec);
        StreamId = root.GetProperty("stream_id").GetString();
        CallControlId = start.GetProperty("call_control_id").GetString();
        if (string.IsNullOrEmpty(StreamId) || string.IsNullOrEmpty(CallControlId)) throw new InvalidDataException("Media start requires stream and call IDs.");
        lock (_gate) _inputResampler = new(_inputRate, _targetRate);
        _started.TrySetResult(root.Clone());
    }

    private void Receive(JsonElement root)
    {
        if (!_started.Task.IsCompletedSuccessfully) throw new InvalidDataException("Audio received before media start.");
        if (root.TryGetProperty("stream_id", out var stream) && stream.GetString() != StreamId)
            throw new InvalidDataException("Audio belongs to another stream.");
        var media = root.GetProperty("media");
        if (media.TryGetProperty("track", out var track) && track.GetString() is "outbound" or "outbound_track") return;
        var pcm = TelnyxPcmCodec.Decode(Convert.FromBase64String(media.GetProperty("payload").GetString() ?? ""), _inputCodec);
        lock (_gate)
        {
            if (_receive is not null) Deliver(pcm);
            else if (!_hasRecorded)
            {
                if (_earlyAudio.Sum(x => x.Length) + pcm.Length > _inputRate * 2 * 5)
                    throw new InvalidDataException("Provider startup exceeded the five-second input audio buffer.");
                _earlyAudio.Enqueue(pcm);
            }
        }
    }

    private void Deliver(byte[] pcm)
    {
        var output = _inputResampler!.Process(pcm);
        if (output.Length > 0) _receive?.Invoke(this, new(Convert.ToBase64String(output)));
    }

    public Task InitAudioAsync() => Started;
    public async Task<bool> StartRecordingAudio(MicrophoneAudioReceivedEventHandler handler, AudioSampleRate targetSampleRate = AudioSampleRate.Rate24000)
    {
        await Started;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _targetRate = (int)targetSampleRate;
            _inputResampler = new(_inputRate, _targetRate);
            _receive = handler; _hasRecorded = true;
            while (_earlyAudio.TryDequeue(out var pcm)) Deliver(pcm);
        }
        return true;
    }
    public Task<bool> StopRecordingAudio()
    {
        lock (_gate)
        {
            var tail = _inputResampler?.Process([], true);
            if (tail is { Length: > 0 }) _receive?.Invoke(this, new(Convert.ToBase64String(tail)));
            _receive = null; _earlyAudio.Clear();
        }
        return Task.FromResult(true);
    }

    public bool PlayAudio(string base64EncodedPcm16Audio, int sampleRate)
    {
        byte[] bytes = Convert.FromBase64String(base64EncodedPcm16Audio);
        if (sampleRate is < 8000 or > 192000 || (bytes.Length & 1) != 0) throw new ArgumentException("Playback requires sample-aligned PCM16 at a supported rate.");
        if (bytes.Length == 0) return true;
        double duration = bytes.Length * 500.0 / sampleRate;
        lock (_gate)
        {
            if (_disposed || Completion.IsCompleted) return false;
            if (_queuedMilliseconds + duration > options.MaxQueuedAudioMilliseconds || !_playback.Writer.TryWrite(new(bytes, sampleRate, _generation)))
            { Fail(new InvalidOperationException("Telephone playback queue is full.")); return false; }
            _queuedMilliseconds += duration;
            return true;
        }
    }

    public Task ClearAudioQueueAsync()
    {
        lock (_gate)
        {
            _generation++; _queuedMilliseconds = 0;
            foreach (var pair in _marks) pair.Value.TrySetResult(false);
            _marks.Clear();
            if (!_playback.Writer.TryWrite(new([], 0, _generation, "clear")) && !Completion.IsCompleted)
                Fail(new InvalidOperationException("Cannot clear telephone playback."));
        }
        return Task.CompletedTask;
    }

    public async Task<bool> WaitForPlaybackDrainAsync(TimeSpan? timeout = null)
    {
        string name = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_disposed || Completion.IsCompleted) return false;
            _marks[name] = waiter;
            if (!_playback.Writer.TryWrite(new([], 0, _generation, "mark", name))) { _marks.TryRemove(name, out _); return false; }
        }
        try { return await waiter.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30), _shutdown.Token); }
        catch (TimeoutException) { return false; }
        catch (OperationCanceledException) { return false; }
        finally { _marks.TryRemove(name, out _); }
    }

    private async Task SendLoopAsync(WebSocket socket, CancellationToken token)
    {
        Pcm16Resampler? resampler = null; int sourceRate = 0, generation = 0;
        var pending = new List<byte>();
        // 20 ms at fractional rates such as 22050 Hz still yields a whole sample count.
        int frameBytes = options.Stream.SendSampleRate / 50 * 2;
        async Task SendJson(JsonObject message) => await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()).AsMemory(), WebSocketMessageType.Text, true, token);
        async Task FlushFrames(bool final)
        {
            while (pending.Count >= frameBytes || (final && pending.Count > 0))
            {
                if (generation != Volatile.Read(ref _generation)) { pending.Clear(); return; }
                int count = Math.Min(frameBytes, pending.Count);
                byte[] frame = new byte[frameBytes]; pending.CopyTo(0, frame, 0, count); pending.RemoveRange(0, count);
                var encoded = TelnyxPcmCodec.Encode(frame, options.Stream.SendCodec);
                await SendJson(new() { ["event"] = "media", ["media"] = new JsonObject { ["payload"] = Convert.ToBase64String(encoded) } });
            }
        }
        try
        {
            await Started.WaitAsync(token);
            while (true)
            {
                var available = _playback.Reader.WaitToReadAsync(token).AsTask();
                if (pending.Count > 0 && await Task.WhenAny(available, Task.Delay(40, token)) != available)
                {
                    if (resampler is not null) pending.AddRange(resampler.Process([], true));
                    await FlushFrames(true); resampler = null; sourceRate = 0;
                }
                if (!await available || !_playback.Reader.TryRead(out var item)) break;
                if (item.Generation != Volatile.Read(ref _generation)) continue;
                if (generation != item.Generation || item.Command == "clear")
                { pending.Clear(); resampler = null; sourceRate = 0; generation = item.Generation; }
                if (item.Command == "clear") { await SendJson(new() { ["event"] = "clear" }); continue; }
                if (item.Command == "mark")
                {
                    if (resampler is not null) pending.AddRange(resampler.Process([], true));
                    await FlushFrames(true); resampler = null; sourceRate = 0;
                    await SendJson(new() { ["event"] = "mark", ["mark"] = new JsonObject { ["name"] = item.Name } });
                    continue;
                }
                if (sourceRate != item.SampleRate)
                {
                    if (resampler is not null) { pending.AddRange(resampler.Process([], true)); await FlushFrames(true); }
                    sourceRate = item.SampleRate; resampler = new(sourceRate, options.Stream.SendSampleRate);
                }
                pending.AddRange(resampler!.Process(item.Audio)); await FlushFrames(false);
                lock (_gate) if (item.Generation == _generation) _queuedMilliseconds = Math.Max(0, _queuedMilliseconds - item.Audio.Length * 500.0 / item.SampleRate);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex); }
    }

    private void Fail(Exception exception)
    {
        _log(LogLevel.Error, "Telnyx audio transport failed: " + exception.GetType().Name);
        try { AudioError?.Invoke(this, exception.Message); } finally { _shutdown.Cancel(); }
    }
    public Task<List<AudioDeviceInfo>> GetAvailableMicrophonesAsync() => Task.FromResult(new List<AudioDeviceInfo> { new() { Id = "telnyx", Name = "Telephone caller", IsDefault = true } });
    public Task<List<AudioDeviceInfo>> RequestMicrophonePermissionAndGetDevicesAsync() => GetAvailableMicrophonesAsync();
    public Task<bool> SetMicrophoneDeviceAsync(string deviceId) => Task.FromResult(deviceId == "telnyx");
    public Task<string?> GetCurrentMicrophoneDeviceAsync() => Task.FromResult<string?>("telnyx");
    public Task<bool> SetDiagnosticLevelAsync(DiagnosticLevel level) { _diagnostic = level; return Task.FromResult(true); }
    public Task<DiagnosticLevel> GetDiagnosticLevelAsync() => Task.FromResult(_diagnostic);
    public void SetLogAction(Action<LogLevel, string> logAction) => _log = logAction;
    public ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return ValueTask.CompletedTask; _disposed = true; _receive = null; _earlyAudio.Clear(); }
        _shutdown.Cancel(); _playback.Writer.TryComplete();
        _started.TrySetCanceled();
        return ValueTask.CompletedTask;
    }
    private sealed record Playback(byte[] Audio, int SampleRate, int Generation, string? Command = null, string? Name = null);
}
