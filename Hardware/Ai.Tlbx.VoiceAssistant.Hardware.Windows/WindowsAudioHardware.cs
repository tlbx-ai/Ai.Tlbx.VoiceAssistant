using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using NAudio.Wave;
using System.Threading.Channels;

namespace Ai.Tlbx.VoiceAssistant.Hardware.Windows
{
    public class WindowsAudioHardware : IAudioHardwareAccess
    {
        private WaveInEvent? _waveIn;
        private bool _isRecording;
        private CancellationTokenSource? _recordingCts;
        private readonly int _sampleRate;
        private readonly int _channelCount;
        private readonly int _bitsPerSample;
        private int _currentRecordingSampleRate;
        private WaveOutEvent? _waveOut;
        private BufferedWaveProvider? _bufferedWaveProvider;
        private int _playbackSampleRate;
        private MicrophoneAudioReceivedEventHandler? _audioDataReceivedHandler;
        private bool _isInitialized = false;
        private int _selectedDeviceNumber = 0;
        private DiagnosticLevel _diagnosticLevel = DiagnosticLevel.Basic;
        private Action<LogLevel, string>? _logAction;

        // Channel-based audio queue (learning from Web implementation)
        private readonly Channel<(string Audio, int SampleRate)> _audioChannel;
        private Task? _audioProcessorTask;
        private CancellationTokenSource? _audioProcessorCts;
        private bool _playbackSessionLogged = false;
        private bool _isPlayingAudio = false;

        // Mic test support
        private bool _isMicTesting = false;
        private WaveInEvent? _micTestWaveIn;
        private Action<string>? _micTestCallback;

        public event EventHandler<string>? AudioError;

        public WindowsAudioHardware(int sampleRate = 24000, int channelCount = 1, int bitsPerSample = 16)
        {
            _sampleRate = sampleRate;
            _channelCount = channelCount;
            _bitsPerSample = bitsPerSample;
            _currentRecordingSampleRate = sampleRate;
            _playbackSampleRate = sampleRate;
            _isRecording = false;

            // Unbounded channel ensures we never block on write, single reader for ordering
            _audioChannel = Channel.CreateUnbounded<(string, int)>(
                new UnboundedChannelOptions { SingleReader = true });
        }

        public void SetLogAction(Action<LogLevel, string> logAction)
        {
            _logAction = logAction;
        }

        private void Log(LogLevel level, string message)
        {
            _logAction?.Invoke(level, $"[WindowsAudio] {message}");
        }

        public Task InitAudioAsync()
        {
            if (_isInitialized)
            {
                return Task.CompletedTask;
            }

            try
            {
                Log(LogLevel.Info, "Initializing Windows audio hardware...");

                int deviceCount = WaveInEvent.DeviceCount;
                if (deviceCount == 0)
                {
                    string error = "No audio input devices detected";
                    Log(LogLevel.Error, error);
                    AudioError?.Invoke(this, error);
                    return Task.CompletedTask;
                }

                Log(LogLevel.Info, $"Found {deviceCount} input devices");

                _waveOut = new WaveOutEvent();
                _bufferedWaveProvider = new BufferedWaveProvider(new WaveFormat(_sampleRate, _bitsPerSample, _channelCount))
                {
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(5)
                };
                _waveOut.Init(_bufferedWaveProvider);

                // Start the audio processor task (single consumer for the channel)
                StartAudioProcessor();

                _isInitialized = true;
                Log(LogLevel.Info, "Windows audio hardware initialized");
            }
            catch (Exception ex)
            {
                string error = $"Error initializing audio: {ex.Message}";
                Log(LogLevel.Error, error);
                AudioError?.Invoke(this, error);
            }
            return Task.CompletedTask;
        }

        private void StartAudioProcessor()
        {
            if (_audioProcessorTask != null)
                return;

            _audioProcessorCts = new CancellationTokenSource();
            _audioProcessorTask = ProcessAudioChannelAsync(_audioProcessorCts.Token);
            Log(LogLevel.Info, "Audio processor task started");
        }

        private async Task ProcessAudioChannelAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var (audio, sampleRate) in _audioChannel.Reader.ReadAllAsync(ct))
                {
                    if (!_playbackSessionLogged)
                    {
                        Log(LogLevel.Info, "Audio playback session started");
                        _playbackSessionLogged = true;
                    }

                    try
                    {
                        EnsurePlaybackFormat(sampleRate);
                        byte[] audioData = Convert.FromBase64String(audio);

                        if (_bufferedWaveProvider != null)
                        {
                            _bufferedWaveProvider.AddSamples(audioData, 0, audioData.Length);
                        }

                        if (_waveOut?.PlaybackState != PlaybackState.Playing)
                        {
                            _waveOut?.Play();
                            _isPlayingAudio = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(LogLevel.Error, $"Error playing audio chunk: {ex.Message}");
                        AudioError?.Invoke(this, $"Error playing audio: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation during shutdown
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Audio processor error: {ex.Message}");
            }

            Log(LogLevel.Info, "Audio processor task stopped");
        }

        private void EnsurePlaybackFormat(int sampleRate)
        {
            if (_waveOut != null && _bufferedWaveProvider != null && _playbackSampleRate == sampleRate)
            {
                return;
            }

            _waveOut?.Stop();
            _waveOut?.Dispose();

            _playbackSampleRate = sampleRate;
            _waveOut = new WaveOutEvent();
            _bufferedWaveProvider = new BufferedWaveProvider(new WaveFormat(_playbackSampleRate, _bitsPerSample, _channelCount))
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(5)
            };
            _waveOut.Init(_bufferedWaveProvider);
            Log(LogLevel.Info, $"Playback format set to {_playbackSampleRate}Hz");
        }

        public async Task<bool> StartRecordingAudio(MicrophoneAudioReceivedEventHandler audioDataReceivedHandler, AudioSampleRate targetSampleRate = AudioSampleRate.Rate24000)
        {
            if (_isRecording)
            {
                return true;
            }

            try
            {
                if (!_isInitialized)
                {
                    await InitAudioAsync();
                    if (!_isInitialized)
                    {
                        return false;
                    }
                }

                // Reset playback session logging for new recording session
                _playbackSessionLogged = false;

                _currentRecordingSampleRate = (int)targetSampleRate;
                Log(LogLevel.Info, $"Starting audio recording (device: {_selectedDeviceNumber}, rate: {_currentRecordingSampleRate})");

                _audioDataReceivedHandler = audioDataReceivedHandler;
                _recordingCts = new CancellationTokenSource();

                _waveIn = new WaveInEvent
                {
                    DeviceNumber = _selectedDeviceNumber,
                    WaveFormat = new WaveFormat(_currentRecordingSampleRate, _bitsPerSample, _channelCount),
                    BufferMilliseconds = 50
                };

                _waveIn.RecordingStopped += (s, e) =>
                {
                    if (e?.Exception != null)
                    {
                        Log(LogLevel.Error, $"Recording stopped with error: {e.Exception.Message}");
                        AudioError?.Invoke(this, $"Recording error: {e.Exception.Message}");
                    }
                };

                _waveIn.DataAvailable += OnDataAvailable;
                _waveIn.StartRecording();
                _isRecording = true;

                Log(LogLevel.Info, "Recording started");
                return true;
            }
            catch (Exception ex)
            {
                string error = $"Error starting recording: {ex.Message}";
                Log(LogLevel.Error, error);
                AudioError?.Invoke(this, error);
                return false;
            }
        }

        public Task<bool> StopRecordingAudio()
        {
            if (!_isRecording)
            {
                return Task.FromResult(true);
            }

            try
            {
                Log(LogLevel.Info, "Stopping audio recording...");

                _waveIn?.StopRecording();
                if (_waveIn != null)
                {
                    _waveIn.DataAvailable -= OnDataAvailable;
                    _waveIn.Dispose();
                    _waveIn = null;
                }

                _recordingCts?.Cancel();
                _recordingCts?.Dispose();
                _recordingCts = null;

                _isRecording = false;
                _audioDataReceivedHandler = null;

                Log(LogLevel.Info, "Recording stopped");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                string error = $"Error stopping recording: {ex.Message}";
                Log(LogLevel.Error, error);
                AudioError?.Invoke(this, error);
                return Task.FromResult(false);
            }
        }

        public bool PlayAudio(string base64EncodedPcm16Audio, int sampleRate)
        {
            if (string.IsNullOrEmpty(base64EncodedPcm16Audio))
            {
                return false;
            }

            // Non-blocking write to channel - processor task handles playback
            return _audioChannel.Writer.TryWrite((base64EncodedPcm16Audio, sampleRate));
        }

        public async Task ClearAudioQueueAsync()
        {
            try
            {
                // Drain the channel first
                int queuedItems = 0;
                while (_audioChannel.Reader.TryRead(out _))
                {
                    queuedItems++;
                }

                _playbackSessionLogged = false;

                if (queuedItems > 0)
                {
                    Log(LogLevel.Info, $"Drained {queuedItems} items from audio queue");
                }

                // Clear the buffer and stop playback
                int bufferSizeBeforeClear = _bufferedWaveProvider?.BufferedBytes ?? 0;
                _bufferedWaveProvider?.ClearBuffer();
                _waveOut?.Stop();
                _isPlayingAudio = false;

                if (bufferSizeBeforeClear > 0)
                {
                    Log(LogLevel.Info, $"Cleared {bufferSizeBeforeClear} bytes from buffer");
                }
            }
            catch (Exception ex)
            {
                string error = $"Error clearing audio buffer: {ex.Message}";
                Log(LogLevel.Error, error);
                AudioError?.Invoke(this, error);
            }
            await Task.CompletedTask;
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs? e)
        {
            try
            {
                if (e?.BytesRecorded > 0 && _audioDataReceivedHandler != null)
                {
                    var buffer = new byte[e.BytesRecorded];
                    if (e.Buffer != null)
                    {
                        Array.Copy(e.Buffer, buffer, e.BytesRecorded);
                    }

                    string base64Audio = Convert.ToBase64String(buffer);
                    _audioDataReceivedHandler?.Invoke(this, new MicrophoneAudioReceivedEventArgs(base64Audio));
                }
            }
            catch (Exception ex)
            {
                string error = $"Error processing audio data: {ex.Message}";
                Log(LogLevel.Error, error);
                AudioError?.Invoke(this, error);
            }
        }

        public async Task<List<AudioDeviceInfo>> GetAvailableMicrophonesAsync()
        {
            var result = new List<AudioDeviceInfo>();

            try
            {
                if (!_isInitialized)
                {
                    await InitAudioAsync();
                }

                int deviceCount = WaveInEvent.DeviceCount;
                Log(LogLevel.Info, $"Found {deviceCount} microphone devices");

                for (int i = 0; i < deviceCount; i++)
                {
                    var capabilities = WaveInEvent.GetCapabilities(i);
                    result.Add(new AudioDeviceInfo
                    {
                        Id = i.ToString(),
                        Name = capabilities.ProductName,
                        IsDefault = i == 0
                    });
                }
            }
            catch (Exception ex)
            {
                string error = $"Error getting available microphones: {ex.Message}";
                Log(LogLevel.Error, error);
                AudioError?.Invoke(this, error);
            }
            return result;
        }

        public async Task<List<AudioDeviceInfo>> RequestMicrophonePermissionAndGetDevicesAsync()
        {
            return await GetAvailableMicrophonesAsync();
        }

        public async Task<bool> SetMicrophoneDeviceAsync(string deviceId)
        {
            try
            {
                if (!_isInitialized)
                {
                    await InitAudioAsync();
                }

                if (int.TryParse(deviceId, out int deviceNumber))
                {
                    if (deviceNumber >= 0 && deviceNumber < WaveInEvent.DeviceCount)
                    {
                        _selectedDeviceNumber = deviceNumber;
                        Log(LogLevel.Info, $"Microphone set to device {deviceNumber}");
                        return true;
                    }
                    else
                    {
                        string error = $"Invalid device number: {deviceNumber}";
                        Log(LogLevel.Error, error);
                        AudioError?.Invoke(this, error);
                    }
                }
                else
                {
                    string error = $"Invalid device ID format: {deviceId}";
                    Log(LogLevel.Error, error);
                    AudioError?.Invoke(this, error);
                }
            }
            catch (Exception ex)
            {
                string error = $"Error setting microphone device: {ex.Message}";
                Log(LogLevel.Error, error);
                AudioError?.Invoke(this, error);
            }
            return false;
        }

        public Task<string?> GetCurrentMicrophoneDeviceAsync()
        {
            return Task.FromResult<string?>(_selectedDeviceNumber.ToString());
        }

        /// <summary>
        /// Starts a microphone test that loops audio back through the speakers.
        /// </summary>
        /// <param name="callback">Optional callback for each audio chunk (base64 encoded).</param>
        public async Task<bool> StartMicTest(Action<string>? callback = null)
        {
            if (_isMicTesting)
            {
                Log(LogLevel.Warn, "Mic test already running");
                return true;
            }

            try
            {
                if (!_isInitialized)
                {
                    await InitAudioAsync();
                }

                Log(LogLevel.Info, "Starting mic test loopback");
                _isMicTesting = true;
                _micTestCallback = callback;

                _micTestWaveIn = new WaveInEvent
                {
                    DeviceNumber = _selectedDeviceNumber,
                    WaveFormat = new WaveFormat(_sampleRate, _bitsPerSample, _channelCount),
                    BufferMilliseconds = 50
                };

                _micTestWaveIn.DataAvailable += OnMicTestDataAvailable;
                _micTestWaveIn.StartRecording();

                Log(LogLevel.Info, "Mic test started - speak to hear yourself");
                return true;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Mic test start failed: {ex.Message}");
                _isMicTesting = false;
                return false;
            }
        }

        private void OnMicTestDataAvailable(object? sender, WaveInEventArgs? e)
        {
            if (!_isMicTesting || e == null || e.BytesRecorded <= 0 || e.Buffer == null)
                return;

            try
            {
                // Loop back to speakers
                _bufferedWaveProvider?.AddSamples(e.Buffer, 0, e.BytesRecorded);

                if (_waveOut?.PlaybackState != PlaybackState.Playing)
                {
                    _waveOut?.Play();
                }

                // Optional callback
                if (_micTestCallback != null)
                {
                    string base64 = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
                    _micTestCallback(base64);
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Mic test data error: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops the microphone test.
        /// </summary>
        public Task StopMicTest()
        {
            if (!_isMicTesting)
            {
                return Task.CompletedTask;
            }

            Log(LogLevel.Info, "Stopping mic test");
            _isMicTesting = false;
            _micTestCallback = null;

            if (_micTestWaveIn != null)
            {
                _micTestWaveIn.StopRecording();
                _micTestWaveIn.DataAvailable -= OnMicTestDataAvailable;
                _micTestWaveIn.Dispose();
                _micTestWaveIn = null;
            }

            // Clear buffer and stop playback
            _bufferedWaveProvider?.ClearBuffer();
            _waveOut?.Stop();

            Log(LogLevel.Info, "Mic test stopped");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns true if a mic test is currently running.
        /// </summary>
        public bool IsMicTesting => _isMicTesting;

        /// <summary>
        /// Returns true if audio is currently being played back.
        /// </summary>
        public bool IsPlayingAudio => _isPlayingAudio;

        /// <summary>
        /// Waits until all queued audio has been played back.
        /// </summary>
        /// <param name="timeout">Maximum time to wait.</param>
        /// <returns>True if playback completed, false if timed out.</returns>
        public async Task<bool> WaitForPlaybackDrainAsync(TimeSpan? timeout = null)
        {
            var maxWait = timeout ?? TimeSpan.FromSeconds(30);
            var startTime = DateTime.UtcNow;

            // Wait for channel to drain (TryPeek returns false when empty)
            while (_audioChannel.Reader.TryPeek(out _))
            {
                if (DateTime.UtcNow - startTime > maxWait)
                {
                    Log(LogLevel.Warn, "WaitForPlaybackDrain timed out waiting for channel");
                    return false;
                }
                await Task.Delay(50);
            }

            // Wait for buffer to drain
            while (_bufferedWaveProvider?.BufferedBytes > 0)
            {
                if (DateTime.UtcNow - startTime > maxWait)
                {
                    Log(LogLevel.Warn, "WaitForPlaybackDrain timed out waiting for buffer");
                    return false;
                }
                await Task.Delay(50);
            }

            // Small grace period for audio hardware to finish
            await Task.Delay(100);

            Log(LogLevel.Info, $"Playback drain complete after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms");
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                Log(LogLevel.Info, "Disposing Windows audio hardware...");

                // Stop mic test if running
                await StopMicTest();

                // Stop recording
                await StopRecordingAudio();

                // Stop the audio processor task
                if (_audioProcessorCts != null)
                {
                    _audioProcessorCts.Cancel();

                    if (_audioProcessorTask != null)
                    {
                        try
                        {
                            await _audioProcessorTask;
                        }
                        catch
                        {
                            // Task cancellation expected
                        }
                    }

                    _audioProcessorCts.Dispose();
                    _audioProcessorCts = null;
                    _audioProcessorTask = null;
                }

                // Clear audio queue
                while (_audioChannel.Reader.TryRead(out _)) { }

                if (_waveOut != null)
                {
                    _waveOut.Stop();
                    _waveOut.Dispose();
                    _waveOut = null;
                }

                _bufferedWaveProvider = null;
                _isInitialized = false;
                Log(LogLevel.Info, "Windows audio hardware disposed");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Error during disposal: {ex}");
            }
        }

        public void StartAsync()
        {
            try
            {
                _waveIn?.StartRecording();
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Error starting audio client: {ex.Message}");
                AudioError?.Invoke(this, $"Error starting audio: {ex.Message}");
            }
        }

        public void StopAsync()
        {
            try
            {
                _waveIn?.StopRecording();
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Error stopping audio client: {ex.Message}");
                AudioError?.Invoke(this, $"Error stopping audio: {ex.Message}");
            }
        }

        public async Task<bool> SetDiagnosticLevelAsync(DiagnosticLevel level)
        {
            await Task.CompletedTask;
            _diagnosticLevel = level;
            Log(LogLevel.Info, $"Diagnostic level set to: {level}");
            return true;
        }

        public async Task<DiagnosticLevel> GetDiagnosticLevelAsync()
        {
            await Task.CompletedTask;
            return _diagnosticLevel;
        }
    }
}
