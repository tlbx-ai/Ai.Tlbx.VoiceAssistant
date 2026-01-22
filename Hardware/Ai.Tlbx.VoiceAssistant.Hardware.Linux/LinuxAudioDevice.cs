using System.Runtime.InteropServices;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Hardware.Linux
{
    /// <summary>
    /// Linux implementation of the IAudioHardwareAccess interface using ALSA.
    /// </summary>
    public class LinuxAudioDevice : IAudioHardwareAccess
    {
        private const int DEFAULT_SAMPLE_RATE = 16000;
        private const uint DEFAULT_CHANNELS = 1; // Mono
        private const int DEFAULT_PERIOD_SIZE = 1024; // Frames per period
        private const int DEFAULT_BUFFER_SIZE = 8192; // Total buffer size in frames
        
        private bool _isInitialized = false;
        private bool _isRecording = false;
        private string _currentMicrophoneId = "default";
        private List<AudioDeviceInfo>? _availableMicrophones = null;
        private IntPtr _captureHandle = IntPtr.Zero;
        private IntPtr _playbackHandle = IntPtr.Zero;
        private Task? _recordingTask = null;
        private CancellationTokenSource? _recordingCts = null;
        private MicrophoneAudioReceivedEventHandler? _audioDataHandler = null;
        private Action<LogLevel, string>? _logger;
        private DiagnosticLevel _diagnosticLevel = DiagnosticLevel.Basic;
        
        /// <summary>
        /// Event that fires when an audio error occurs in the ALSA hardware
        /// </summary>
        public event EventHandler<string>? AudioError;

        /// <summary>
        /// Initializes a new instance of the LinuxAudioDevice class.
        /// </summary>
        public LinuxAudioDevice()
        {
            // Check if we're running on Linux
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Log(LogLevel.Warn, "LinuxAudioDevice is designed for Linux systems only.");
            }
        }

        /// <summary>
        /// Initializes a new instance of the LinuxAudioDevice class with a logger.
        /// </summary>
        public LinuxAudioDevice(Action<LogLevel, string> logger) : this()
        {
            _logger = logger;
        }

        /// <summary>
        /// Sets the logging action for this hardware component.
        /// </summary>
        /// <param name="logAction">Action to be called with log level and message.</param>
        public void SetLogAction(Action<LogLevel, string> logAction)
        {
            _logger = logAction;
        }

        private void Log(LogLevel level, string message)
        {
            // Apply diagnostic level filtering
            bool shouldLog = _diagnosticLevel switch
            {
                DiagnosticLevel.None => false,
                DiagnosticLevel.Basic => level >= LogLevel.Warn,
                DiagnosticLevel.Detailed => level >= LogLevel.Info,
                DiagnosticLevel.Verbose => true,
                _ => true
            };

            if (!shouldLog) return;

            _logger?.Invoke(level, $"[LinuxAudioDevice] {message}");
        }

        /// <summary>
        /// Initializes the ALSA audio hardware and prepares it for recording and playback.
        /// </summary>
        public async Task InitAudioAsync()
        {
            if (_isInitialized)
            {
                Log(LogLevel.Info, "Audio already initialized");
                return;
            }

            try
            {
                await Task.Run(() => 
                {
                    Log(LogLevel.Info, "Initializing ALSA audio subsystem");
                    
                    // Check if ALSA library is available
                    if (!NativeLibrary.TryLoad(AlsaNative.ALSA_LIBRARY, out IntPtr alsaHandle))
                    {
                        var error = $"Could not load ALSA library. Please ensure '{AlsaNative.ALSA_LIBRARY}' is installed on your system.";
                        Log(LogLevel.Error, error);
                        throw new DllNotFoundException(error);
                    }
                    
                    NativeLibrary.Free(alsaHandle);
                    Log(LogLevel.Info, $"Successfully loaded {AlsaNative.ALSA_LIBRARY}");
                    
                    // Initialize capture device (microphone)
                    InitializeCapture();
                    
                    // Initialize playback device (speakers)
                    InitializePlayback();
                    
                    _isInitialized = true;
                    Log(LogLevel.Info, "ALSA audio subsystem initialized successfully");
                });
            }
            catch (DllNotFoundException ex)
            {
                var errorMsg = $"ALSA library not found: {ex.Message}";
                Log(LogLevel.Error, errorMsg);
                AudioError?.Invoke(this, errorMsg);
                throw;
            }
            catch (AlsaException ex)
            {
                var errorMsg = $"ALSA initialization error: {ex.Message}, Error code: {ex.ErrorCode}, Operation: {ex.Operation}";
                Log(LogLevel.Error, errorMsg);
                AudioError?.Invoke(this, errorMsg);
                throw;
            }
            catch (Exception ex)
            {
                var errorMsg = $"Failed to initialize ALSA audio: {ex.Message}";
                Log(LogLevel.Error, errorMsg);
                AudioError?.Invoke(this, errorMsg);
                throw;
            }
        }

        private void InitializeCapture()
        {
            Log(LogLevel.Info, $"Initializing capture device: {_currentMicrophoneId}");
            
            // Open the PCM device for capture
            int err = AlsaNative.snd_pcm_open(
                out _captureHandle, 
                _currentMicrophoneId, 
                AlsaNative.SndPcmStreamType.SND_PCM_STREAM_CAPTURE, 
                0);
            
            if (err < 0)
            {
                string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                throw new AlsaException(
                    $"Failed to open capture device '{_currentMicrophoneId}': {errorMsg}", 
                    err, 
                    "snd_pcm_open");
            }
            
            Log(LogLevel.Info, $"Capture device opened: {_currentMicrophoneId}");
            
            // Set hardware parameters
            ConfigurePcmDevice(_captureHandle, DEFAULT_SAMPLE_RATE, DEFAULT_CHANNELS, "capture");
        }

        private void InitializePlayback()
        {
            Log(LogLevel.Info, "Initializing playback device: default");
            
            // Open the PCM device for playback
            int err = AlsaNative.snd_pcm_open(
                out _playbackHandle, 
                "default", 
                AlsaNative.SndPcmStreamType.SND_PCM_STREAM_PLAYBACK, 
                0);
            
            if (err < 0)
            {
                string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                throw new AlsaException(
                    $"Failed to open playback device 'default': {errorMsg}", 
                    err, 
                    "snd_pcm_open");
            }
            
            Log(LogLevel.Info, "Playback device opened: default");
            
            // Set hardware parameters
            ConfigurePcmDevice(_playbackHandle, DEFAULT_SAMPLE_RATE, DEFAULT_CHANNELS, "playback");
        }

        private void ConfigurePcmDevice(IntPtr pcmHandle, uint sampleRate, uint channels, string deviceType)
        {
            Log(LogLevel.Info, $"Configuring {deviceType} device: rate={sampleRate}Hz, channels={channels}");
            
            IntPtr hwParams = IntPtr.Zero;
            
            try
            {
                // Allocate hardware parameters object
                int err = AlsaNative.snd_pcm_hw_params_malloc(out hwParams);
                if (err < 0)
                {
                    throw new AlsaException(
                        $"Failed to allocate hardware parameters: {AlsaNative.GetAlsaErrorMessage(err)}", 
                        err, 
                        "snd_pcm_hw_params_malloc");
                }
                
                // Fill hw_params with default values
                err = AlsaNative.snd_pcm_hw_params_any(pcmHandle, hwParams);
                if (!HandleAlsaError(err, pcmHandle, "snd_pcm_hw_params_any"))
                {
                    throw new AlsaException(
                        $"Failed to initialize hardware parameters: {AlsaNative.GetAlsaErrorMessage(err)}", 
                        err, 
                        "snd_pcm_hw_params_any");
                }
                
                // Set access type
                err = AlsaNative.snd_pcm_hw_params_set_access(
                    pcmHandle, 
                    hwParams, 
                    AlsaNative.SndPcmAccessType.SND_PCM_ACCESS_RW_INTERLEAVED);
                
                if (!HandleAlsaError(err, pcmHandle, "snd_pcm_hw_params_set_access"))
                {
                    throw new AlsaException(
                        $"Failed to set access type: {AlsaNative.GetAlsaErrorMessage(err)}", 
                        err, 
                        "snd_pcm_hw_params_set_access");
                }
                
                // Set sample format (16-bit signed little-endian)
                err = AlsaNative.snd_pcm_hw_params_set_format(
                    pcmHandle, 
                    hwParams, 
                    AlsaNative.SndPcmFormat.SND_PCM_FORMAT_S16_LE);
                
                if (!HandleAlsaError(err, pcmHandle, "snd_pcm_hw_params_set_format"))
                {
                    throw new AlsaException(
                        $"Failed to set sample format: {AlsaNative.GetAlsaErrorMessage(err)}", 
                        err, 
                        "snd_pcm_hw_params_set_format");
                }
                
                // Set sample rate
                err = AlsaNative.snd_pcm_hw_params_set_rate(pcmHandle, hwParams, sampleRate, 0);
                if (!HandleAlsaError(err, pcmHandle, "snd_pcm_hw_params_set_rate"))
                {
                    throw new AlsaException(
                        $"Failed to set sample rate: {AlsaNative.GetAlsaErrorMessage(err)}", 
                        err, 
                        "snd_pcm_hw_params_set_rate");
                }
                
                // Set channels (mono/stereo)
                err = AlsaNative.snd_pcm_hw_params_set_channels(pcmHandle, hwParams, channels);
                if (!HandleAlsaError(err, pcmHandle, "snd_pcm_hw_params_set_channels"))
                {
                    throw new AlsaException(
                        $"Failed to set channels: {AlsaNative.GetAlsaErrorMessage(err)}", 
                        err, 
                        "snd_pcm_hw_params_set_channels");
                }
                
                // Set buffer size
                err = AlsaNative.snd_pcm_hw_params_set_buffer_size(pcmHandle, hwParams, DEFAULT_BUFFER_SIZE);
                if (err < 0)
                {
                    // Non-critical, we'll log but continue
                    string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                    Log(LogLevel.Warn, $"Failed to set buffer size: {errorMsg}");
                }
                
                // Set period size
                err = AlsaNative.snd_pcm_hw_params_set_period_size(pcmHandle, hwParams, DEFAULT_PERIOD_SIZE, 0);
                if (err < 0)
                {
                    // Non-critical, we'll log but continue
                    string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                    Log(LogLevel.Warn, $"Failed to set period size: {errorMsg}");
                }
                
                // Apply hardware parameters
                err = AlsaNative.snd_pcm_hw_params(pcmHandle, hwParams);
                if (!HandleAlsaError(err, pcmHandle, "snd_pcm_hw_params"))
                {
                    throw new AlsaException(
                        $"Failed to set hardware parameters: {AlsaNative.GetAlsaErrorMessage(err)}", 
                        err, 
                        "snd_pcm_hw_params");
                }
                
                // Prepare the PCM device
                err = AlsaNative.snd_pcm_prepare(pcmHandle);
                if (!HandleAlsaError(err, pcmHandle, "snd_pcm_prepare"))
                {
                    throw new AlsaException(
                        $"Failed to prepare PCM device: {AlsaNative.GetAlsaErrorMessage(err)}", 
                        err, 
                        "snd_pcm_prepare");
                }
                
                Log(LogLevel.Info, $"{deviceType} device configured successfully");
            }
            finally
            {
                // Free hardware parameters
                if (hwParams != IntPtr.Zero)
                {
                    AlsaNative.snd_pcm_hw_params_free(hwParams);
                }
            }
        }

        /// <summary>
        /// Retrieves a list of available microphone devices through ALSA.
        /// </summary>
        public async Task<List<AudioDeviceInfo>> GetAvailableMicrophonesAsync()
        {
            if (!_isInitialized)
            {
                await InitAudioAsync();
            }

            if (_availableMicrophones != null)
            {
                Log(LogLevel.Info, "Returning cached microphone list");
                return _availableMicrophones;
            }

            try
            {
                Log(LogLevel.Info, "Enumerating ALSA capture devices");
                _availableMicrophones = new List<AudioDeviceInfo>();
                bool defaultFound = false;

                // Get all sound devices (both input and output)
                IntPtr hintsPtr = IntPtr.Zero;
                int err = AlsaNative.snd_device_name_hint(-1, "pcm", out hintsPtr);
                
                if (err < 0)
                {
                    string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                    throw new AlsaException(
                        $"Failed to get device hints: {errorMsg}", 
                        err, 
                        "snd_device_name_hint");
                }

                try
                {
                    // Loop through the hints
                    IntPtr hintPtr = hintsPtr;
                    int count = 0;
                    
                    while (hintPtr != IntPtr.Zero && Marshal.ReadIntPtr(hintPtr) != IntPtr.Zero)
                    {
                        IntPtr namePtr = AlsaNative.snd_device_name_get_hint(Marshal.ReadIntPtr(hintPtr), "NAME");
                        IntPtr descPtr = AlsaNative.snd_device_name_get_hint(Marshal.ReadIntPtr(hintPtr), "DESC");
                        IntPtr ioPtr = AlsaNative.snd_device_name_get_hint(Marshal.ReadIntPtr(hintPtr), "IOID");
                        
                        string name = Marshal.PtrToStringAnsi(namePtr) ?? string.Empty;
                        string desc = Marshal.PtrToStringAnsi(descPtr) ?? string.Empty;
                        string io = Marshal.PtrToStringAnsi(ioPtr) ?? string.Empty;
                        
                        // Free allocated strings
                        if (namePtr != IntPtr.Zero) Marshal.FreeHGlobal(namePtr);
                        if (descPtr != IntPtr.Zero) Marshal.FreeHGlobal(descPtr);
                        if (ioPtr != IntPtr.Zero) Marshal.FreeHGlobal(ioPtr);
                        
                        // If IOID is null or "Input", it's a capture device
                        if (!string.IsNullOrEmpty(name) && (string.IsNullOrEmpty(io) || io.Equals("Input", StringComparison.OrdinalIgnoreCase)))
                        {
                            bool isDefault = name.Equals("default", StringComparison.OrdinalIgnoreCase);
                            
                            // Don't add special devices like "null", "pulse", etc.
                            if (!name.Equals("null", StringComparison.OrdinalIgnoreCase))
                            {
                                var device = new AudioDeviceInfo
                                {
                                    Id = name,
                                    Name = !string.IsNullOrEmpty(desc) ? desc : name,
                                    IsDefault = isDefault
                                };
                                
                                _availableMicrophones.Add(device);
                                
                                if (isDefault)
                                {
                                    defaultFound = true;
                                }
                                
                                count++;
                                Log(LogLevel.Info, $"Found capture device: {name} ({desc})");
                            }
                        }
                        
                        // Move to next hint
                        hintPtr = IntPtr.Add(hintPtr, IntPtr.Size);
                    }
                    
                    // If no default device was found but we have devices, mark the first one as default
                    if (!defaultFound && _availableMicrophones.Count > 0)
                    {
                        _availableMicrophones[0].IsDefault = true;
                        Log(LogLevel.Info, $"No default device found, setting {_availableMicrophones[0].Id} as default");
                    }
                    
                    // Ensure we always have at least the "default" device
                    if (_availableMicrophones.Count == 0)
                    {
                        _availableMicrophones.Add(new AudioDeviceInfo
                        {
                            Id = "default",
                            Name = "Default ALSA Capture Device",
                            IsDefault = true
                        });
                        
                        Log(LogLevel.Warn, "No capture devices found, using 'default' as fallback");
                    }
                    
                    Log(LogLevel.Info, $"Found {count} capture devices");
                }
                finally
                {
                    // Free hints
                    if (hintsPtr != IntPtr.Zero)
                    {
                        AlsaNative.snd_device_name_free_hint(hintsPtr);
                    }
                }
                
                return _availableMicrophones;
            }
            catch (AlsaException ex)
            {
                string errorMsg = $"Error enumerating ALSA devices: {ex.Message}";
                Log(LogLevel.Error, errorMsg);
                AudioError?.Invoke(this, errorMsg);
                
                // Fallback to a default device
                _availableMicrophones = new List<AudioDeviceInfo>
                {
                    new AudioDeviceInfo
                    {
                        Id = "default",
                        Name = "Default ALSA Capture Device (Fallback)",
                        IsDefault = true
                    }
                };
                
                return _availableMicrophones;
            }
            catch (Exception ex)
            {
                string errorMsg = $"Error getting microphones: {ex.Message}";
                Log(LogLevel.Error, errorMsg);
                AudioError?.Invoke(this, errorMsg);
                
                // Fallback to a default device
                _availableMicrophones = new List<AudioDeviceInfo>
                {
                    new AudioDeviceInfo
                    {
                        Id = "default",
                        Name = "Default ALSA Capture Device (Fallback)",
                        IsDefault = true
                    }
                };
                
                return _availableMicrophones;
            }
        }

        public async Task<List<AudioDeviceInfo>> RequestMicrophonePermissionAndGetDevicesAsync()
        {
            // On Linux, this is the same as GetAvailableMicrophonesAsync() since Linux doesn't have web-style permission prompts
            return await GetAvailableMicrophonesAsync();
        }

        /// <summary>
        /// Gets the current microphone device identifier.
        /// </summary>
        public Task<string?> GetCurrentMicrophoneDeviceAsync()
        {
            if (!_isInitialized)
            {
                InitAudioAsync().Wait();
            }

            return Task.FromResult<string?>(_currentMicrophoneId);
        }

        /// <summary>
        /// Sets the microphone device to use for recording.
        /// </summary>
        public async Task<bool> SetMicrophoneDeviceAsync(string deviceId)
        {
            if (!_isInitialized)
            {
                await InitAudioAsync();
            }

            try
            {
                if (string.IsNullOrEmpty(deviceId))
                {
                    Log(LogLevel.Error, "Cannot set microphone: Device ID is null or empty");
                    return false;
                }
                
                Log(LogLevel.Info, $"Switching microphone device to: {deviceId}");
                
                // Check if device ID exists in available microphones
                var availableMics = await GetAvailableMicrophonesAsync();
                bool deviceExists = availableMics.Any(m => m.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
                
                if (!deviceExists)
                {
                    Log(LogLevel.Warn, $"Microphone device '{deviceId}' not found in available devices, will try anyway");
                }
                
                // If we're recording, we need to stop first
                bool wasRecording = _isRecording;
                MicrophoneAudioReceivedEventHandler? savedHandler = null;
                
                if (wasRecording)
                {
                    Log(LogLevel.Info, "Recording in progress, temporarily stopping to change device");
                    savedHandler = _audioDataHandler;
                    await StopRecordingAudio();
                }
                
                // Close the existing capture handle if it's open
                if (_captureHandle != IntPtr.Zero)
                {
                    Log(LogLevel.Info, "Closing existing capture handle");
                    AlsaNative.snd_pcm_close(_captureHandle);
                    _captureHandle = IntPtr.Zero;
                }
                
                // Save the new device ID
                _currentMicrophoneId = deviceId;
                
                // Open the new device
                int err = AlsaNative.snd_pcm_open(
                    out _captureHandle, 
                    _currentMicrophoneId, 
                    AlsaNative.SndPcmStreamType.SND_PCM_STREAM_CAPTURE, 
                    0);
                
                if (err < 0)
                {
                    string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                    throw new AlsaException(
                        $"Failed to open capture device '{_currentMicrophoneId}': {errorMsg}", 
                        err, 
                        "snd_pcm_open");
                }
                
                // Configure the new device
                ConfigurePcmDevice(_captureHandle, DEFAULT_SAMPLE_RATE, DEFAULT_CHANNELS, "capture");
                
                // Restart recording if it was active
                if (wasRecording && savedHandler != null)
                {
                    Log(LogLevel.Info, "Restarting recording with new device");
                    await StartRecordingAudio(savedHandler);
                }
                
                Log(LogLevel.Info, $"Microphone successfully changed to: {deviceId}");
                return true;
            }
            catch (Exception ex)
            {
                string errorMsg = $"Error setting microphone device: {ex.Message}";
                Log(LogLevel.Error, errorMsg);
                AudioError?.Invoke(this, errorMsg);
                return false;
            }
        }

        /// <summary>
        /// Starts recording audio from the selected microphone.
        /// </summary>
        public async Task<bool> StartRecordingAudio(MicrophoneAudioReceivedEventHandler audioDataReceivedHandler, AudioSampleRate targetSampleRate = AudioSampleRate.Rate24000)
        {
            if (!_isInitialized)
            {
                await InitAudioAsync();
            }

            if (_isRecording)
            {
                Log(LogLevel.Warn, "Recording already in progress");
                return true; // Already recording
            }

            try
            {
                _audioDataHandler = audioDataReceivedHandler ?? throw new ArgumentNullException(nameof(audioDataReceivedHandler));
                _recordingCts = new CancellationTokenSource();
                
                // Make sure the capture device is prepared
                int err = AlsaNative.snd_pcm_prepare(_captureHandle);
                if (err < 0)
                {
                    string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                    throw new AlsaException(
                        $"Failed to prepare capture device for recording: {errorMsg}", 
                        err, 
                        "snd_pcm_prepare");
                }
                
                // Start the recording task
                _recordingTask = Task.Run(async () =>
                {
                    try
                    {
                        _isRecording = true;
                        Log(LogLevel.Info, "Starting ALSA recording");
                        
                        // Start the PCM device
                        err = AlsaNative.snd_pcm_start(_captureHandle);
                        if (err < 0)
                        {
                            string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                            throw new AlsaException(
                                $"Failed to start capture device: {errorMsg}", 
                                err, 
                                "snd_pcm_start");
                        }
                        
                        // Calculate buffer size (100ms of audio at 16kHz, 16-bit mono)
                        // 2 bytes per sample * 1 channel * 16000 samples per second * 0.1 seconds = 3200 bytes
                        int bytesPerFrame = 2 * (int)DEFAULT_CHANNELS; // 16-bit = 2 bytes per sample
                        int framesPerBuffer = DEFAULT_SAMPLE_RATE / 10; // 100ms of audio
                        int bufferSize = framesPerBuffer * bytesPerFrame;
                        
                        byte[] buffer = new byte[bufferSize];
                        int packetsRead = 0;
                        int totalBytesRead = 0;
                        
                        Log(LogLevel.Info, $"Recording buffer size: {bufferSize} bytes ({framesPerBuffer} frames)");
                        
                        // Recording loop
                        while (!_recordingCts.Token.IsCancellationRequested)
                        {
                            // Pin buffer during P/Invoke to prevent GC from moving it
                            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                            long framesRead;
                            try
                            {
                                framesRead = AlsaNative.snd_pcm_readi(_captureHandle, buffer, (ulong)framesPerBuffer);
                            }
                            finally
                            {
                                handle.Free();
                            }

                            // Handle errors
                            if (framesRead < 0)
                            {
                                if (framesRead == -AlsaNative.EPIPE) // Overrun
                                {
                                    Log(LogLevel.Warn, "Buffer overrun detected, recovering");
                                    AlsaNative.snd_pcm_recover(_captureHandle, -AlsaNative.EPIPE, 1);
                                    continue;
                                }
                                else if (framesRead == -AlsaNative.ESTRPIPE) // Suspended
                                {
                                    Log(LogLevel.Warn, "PCM device suspended, recovering");
                                    while ((err = AlsaNative.snd_pcm_resume(_captureHandle)) == -AlsaNative.EAGAIN)
                                    {
                                        await Task.Delay(100, _recordingCts.Token);
                                    }
                                    
                                    if (err < 0)
                                    {
                                        AlsaNative.snd_pcm_prepare(_captureHandle);
                                    }
                                    continue;
                                }
                                
                                // Other error, try to recover
                                string errorMsg = AlsaNative.GetAlsaErrorMessage((int)framesRead);
                                Log(LogLevel.Error, $"PCM read error: {errorMsg}, attempting recovery");
                                
                                if (AlsaNative.snd_pcm_recover(_captureHandle, (int)framesRead, 1) < 0)
                                {
                                    throw new AlsaException(
                                        $"Failed to recover from PCM read error: {errorMsg}", 
                                        (int)framesRead, 
                                        "snd_pcm_recover");
                                }
                                continue;
                            }
                            
                            // If we got some data, convert and send it
                            if (framesRead > 0)
                            {
                                int bytesRead = (int)framesRead * bytesPerFrame;
                                totalBytesRead += bytesRead;
                                packetsRead++;
                                
                                // If we read less than a full buffer, trim it
                                byte[] dataToSend = buffer;
                                if (bytesRead < buffer.Length)
                                {
                                    dataToSend = new byte[bytesRead];
                                    Array.Copy(buffer, dataToSend, bytesRead);
                                }
                                
                                // Convert the PCM data to Base64 string and send
                                string base64Audio = Convert.ToBase64String(dataToSend);
                                _audioDataHandler?.Invoke(this, new MicrophoneAudioReceivedEventArgs(base64Audio));
                                
                                if (packetsRead % 100 == 0) // Log every 100 packets (10 seconds)
                                {
                                    Log(LogLevel.Info, $"Recording stats: {packetsRead} packets, {totalBytesRead / 1024} KB total");
                                }
                            }
                            
                            // Small delay to prevent CPU overuse on slower systems
                            if (_recordingCts.Token.IsCancellationRequested)
                            {
                                break;
                            }
                            
                            await Task.Delay(1, _recordingCts.Token);
                        }
                        
                        Log(LogLevel.Info, $"Recording stopped after {packetsRead} packets ({totalBytesRead / 1024} KB)");
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal cancellation, just log it
                        Log(LogLevel.Info, "Recording task cancelled");
                    }
                    catch (Exception ex)
                    {
                        Log(LogLevel.Error, $"Recording error: {ex.Message}");
                        AudioError?.Invoke(this, $"Recording error: {ex.Message}");
                    }
                    finally
                    {
                        _isRecording = false;
                    }
                }, _recordingCts.Token);
                
                Log(LogLevel.Info, "Recording started successfully");
                return true;
            }
            catch (Exception ex)
            {
                string errorMsg = $"Error starting recording: {ex.Message}";
                Log(LogLevel.Error, errorMsg);
                AudioError?.Invoke(this, errorMsg);
                _isRecording = false;
                return false;
            }
        }

        /// <summary>
        /// Stops the current audio recording session.
        /// </summary>
        public async Task<bool> StopRecordingAudio()
        {
            if (!_isRecording)
            {
                Log(LogLevel.Info, "Not recording, nothing to stop");
                return true; // Not recording
            }

            try
            {
                Log(LogLevel.Info, "Stopping recording");
                _recordingCts?.Cancel();
                
                // Immediately stop the PCM device to ensure no further data is captured
                if (_captureHandle != IntPtr.Zero)
                {
                    int err = AlsaNative.snd_pcm_drop(_captureHandle);
                    if (err < 0)
                    {
                        string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                        Log(LogLevel.Warn, $"Failed to drop capture data: {errorMsg}");
                    }
                    else
                    {
                        Log(LogLevel.Info, "Capture data dropped successfully");
                    }
                }
                
                if (_recordingTask != null)
                {
                    try
                    {
                        await _recordingTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when we cancel the task
                        Log(LogLevel.Info, "Recording task cancelled successfully");
                    }
                }
                
                // Stop the PCM device
                if (_captureHandle != IntPtr.Zero)
                {
                    int err = AlsaNative.snd_pcm_prepare(_captureHandle);
                    if (err < 0)
                    {
                        string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                        Log(LogLevel.Warn, $"Failed to prepare capture device after stopping: {errorMsg}");
                    }
                    else
                    {
                        Log(LogLevel.Info, "Capture device prepared for next use");
                    }
                }

                _recordingTask = null;
                _recordingCts = null;
                _audioDataHandler = null;
                _isRecording = false;
                
                Log(LogLevel.Info, "Recording stopped successfully");
                return true;
            }
            catch (Exception ex)
            {
                string errorMsg = $"Error stopping recording: {ex.Message}";
                Log(LogLevel.Error, errorMsg);
                AudioError?.Invoke(this, errorMsg);
                _isRecording = false;
                return false;
            }
        }

        /// <summary>
        /// Plays the provided audio through the system's audio output.
        /// </summary>
        public bool PlayAudio(string base64EncodedPcm16Audio, int sampleRate)
        {
            if (!_isInitialized)
            {
                try
                {
                    InitAudioAsync().Wait();
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Error, $"Failed to initialize audio for playback: {ex.Message}");
                    AudioError?.Invoke(this, $"Failed to initialize audio for playback: {ex.Message}");
                    return false;
                }
            }

            try
            {
                if (string.IsNullOrEmpty(base64EncodedPcm16Audio))
                {
                    Log(LogLevel.Warn, "Empty audio data provided for playback");
                    return false;
                }
                
                // Check if sample rate matches our device settings
                if (sampleRate != DEFAULT_SAMPLE_RATE)
                {
                    Log(LogLevel.Warn, $"Sample rate mismatch: Audio is {sampleRate}Hz, device is {DEFAULT_SAMPLE_RATE}Hz");
                    // In a production system, we would implement resampling here
                }
                
                // Make sure the playback device is prepared
                int err = AlsaNative.snd_pcm_prepare(_playbackHandle);
                if (err < 0)
                {
                    string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                    throw new AlsaException(
                        $"Failed to prepare playback device: {errorMsg}", 
                        err, 
                        "snd_pcm_prepare");
                }
                
                // Decode base64 to byte array
                byte[] audioData = Convert.FromBase64String(base64EncodedPcm16Audio);
                
                // The number of frames is the byte count divided by (bytes per sample * channels)
                int bytesPerFrame = 2 * (int)DEFAULT_CHANNELS; // 16-bit = 2 bytes per sample
                ulong frames = (ulong)(audioData.Length / bytesPerFrame);
                
                Log(LogLevel.Info, $"Playing {audioData.Length} bytes ({frames} frames) of audio");
                
                // Write all frames to the PCM device
                long framesWritten = 0;
                ulong framesRemaining = frames;
                int offset = 0;
                
                while (framesRemaining > 0)
                {
                    // Try to write all remaining frames
                    framesWritten = AlsaNative.snd_pcm_writei(_playbackHandle, audioData, framesRemaining);
                    
                    // Handle errors
                    if (framesWritten < 0)
                    {
                        if (framesWritten == -AlsaNative.EPIPE) // Underrun
                        {
                            Log(LogLevel.Warn, "Buffer underrun detected, recovering");
                            err = AlsaNative.snd_pcm_recover(_playbackHandle, -AlsaNative.EPIPE, 1);
                            if (err < 0)
                            {
                                string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                                throw new AlsaException(
                                    $"Failed to recover from underrun: {errorMsg}", 
                                    err, 
                                    "snd_pcm_recover");
                            }
                            continue;
                        }
                        else if (framesWritten == -AlsaNative.ESTRPIPE) // Suspended
                        {
                            Log(LogLevel.Warn, "PCM device suspended, recovering");
                            while ((err = AlsaNative.snd_pcm_resume(_playbackHandle)) == -AlsaNative.EAGAIN)
                            {
                                Thread.Sleep(100);
                            }
                            
                            if (err < 0)
                            {
                                err = AlsaNative.snd_pcm_prepare(_playbackHandle);
                                if (err < 0)
                                {
                                    string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                                    throw new AlsaException(
                                        $"Failed to prepare device after suspend: {errorMsg}", 
                                        err, 
                                        "snd_pcm_prepare");
                                }
                            }
                            continue;
                        }
                        
                        // Other error, try to recover
                        string otherErrorMsg = AlsaNative.GetAlsaErrorMessage((int)framesWritten);
                        Log(LogLevel.Error, $"PCM write error: {otherErrorMsg}, attempting recovery");
                        
                        err = AlsaNative.snd_pcm_recover(_playbackHandle, (int)framesWritten, 1);
                        if (err < 0)
                        {
                            throw new AlsaException(
                                $"Failed to recover from PCM write error: {otherErrorMsg}", 
                                (int)framesWritten, 
                                "snd_pcm_recover");
                        }
                        continue;
                    }
                    
                    // Update counters for next iteration
                    framesRemaining -= (ulong)framesWritten;
                    offset += (int)(framesWritten * bytesPerFrame);
                }
                
                // Wait until all frames have been played
                err = AlsaNative.snd_pcm_drain(_playbackHandle);
                if (err < 0)
                {
                    string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                    Log(LogLevel.Warn, $"Failed to drain playback device: {errorMsg}");
                }
                
                Log(LogLevel.Info, $"Finished playing {frames} frames of audio");
                return true;
            }
            catch (Exception ex)
            {
                string errorMsg = $"Error playing audio: {ex.Message}";
                Log(LogLevel.Error, errorMsg);
                AudioError?.Invoke(this, errorMsg);
                return false;
            }
        }

        /// <summary>
        /// Clears any pending audio in the queue and stops the current playback
        /// </summary>
        public async Task ClearAudioQueueAsync()
        {
            if (!_isInitialized)
            {
                await InitAudioAsync();
            }

            try
            {
                if (_playbackHandle == IntPtr.Zero)
                {
                    Log(LogLevel.Info, "No active playback handle to clear");
                    return;
                }
                
                Log(LogLevel.Info, "Clearing audio playback queue");
                
                // Lock to prevent concurrent access issues
                lock (this)
                {
                    // Drop immediately stops playback and drops all pending frames
                    int err = AlsaNative.snd_pcm_drop(_playbackHandle);
                    if (err < 0)
                    {
                        string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                        Log(LogLevel.Warn, $"Failed to drop playback: {errorMsg}");
                    }
                    else
                    {
                        Log(LogLevel.Info, "Playback dropped successfully");
                    }
                    
                    // Prepare the device for future playback
                    err = AlsaNative.snd_pcm_prepare(_playbackHandle);
                    if (err < 0)
                    {
                        string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                        Log(LogLevel.Warn, $"Failed to prepare playback device after dropping: {errorMsg}");
                    }
                    else
                    {
                        Log(LogLevel.Info, "Audio queue cleared and device prepared successfully");
                    }
                }
            }
            catch (Exception ex)
            {
                string errorMsg = $"Error clearing audio queue: {ex.Message}";
                Log(LogLevel.Error, errorMsg);
                AudioError?.Invoke(this, errorMsg);
            }
        }

        /// <summary>
        /// Releases all resources used by the LinuxAudioDevice.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            Log(LogLevel.Info, "Disposing LinuxAudioDevice");
            
            // Use a flag to prevent double-disposal
            if (!_isInitialized) 
            {
                Log(LogLevel.Info, "Already disposed or not initialized");
                return;
            }
            
            // Stop any ongoing recording
            if (_isRecording)
            {
                Log(LogLevel.Info, "Stopping active recording during disposal");
                await StopRecordingAudio();
            }
            
            // Release the recording task and handler
            _recordingTask = null;
            _audioDataHandler = null;
            
            // Cancel any pending tasks
            if (_recordingCts != null)
            {
                Log(LogLevel.Info, "Cancelling recording token source");
                try
                {
                    _recordingCts.Cancel();
                    _recordingCts.Dispose();
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Warn, $"Error disposing cancellation token source: {ex.Message}");
                }
                _recordingCts = null;
            }
            
            try
            {
                // Close the capture device
                if (_captureHandle != IntPtr.Zero)
                {
                    Log(LogLevel.Info, "Closing capture handle");
                    int err = AlsaNative.snd_pcm_close(_captureHandle);
                    if (err < 0)
                    {
                        string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                        Log(LogLevel.Warn, $"Error closing capture device: {errorMsg}");
                    }
                    _captureHandle = IntPtr.Zero;
                }
                
                // Close the playback device
                if (_playbackHandle != IntPtr.Zero)
                {
                    Log(LogLevel.Info, "Closing playback handle");
                    int err = AlsaNative.snd_pcm_close(_playbackHandle);
                    if (err < 0)
                    {
                        string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
                        Log(LogLevel.Warn, $"Error closing playback device: {errorMsg}");
                    }
                    _playbackHandle = IntPtr.Zero;
                }
                
                // Clear the microphone list
                _availableMicrophones = null;
                
                _isInitialized = false;
                Log(LogLevel.Info, "LinuxAudioDevice disposed successfully");
            }
            catch (Exception ex)
            {
                string errorMsg = $"Error during disposal of LinuxAudioDevice: {ex.Message}";
                Log(LogLevel.Error, errorMsg);
                AudioError?.Invoke(this, errorMsg);
                throw;
            }
            finally
            {
                // Ensure state is reset even if an exception occurred
                _captureHandle = IntPtr.Zero;
                _playbackHandle = IntPtr.Zero;
                _isInitialized = false;
                
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// Handles ALSA errors and attempts recovery if possible.
        /// </summary>
        /// <param name="err">The error code returned by an ALSA function</param>
        /// <param name="pcmHandle">The PCM device handle</param>
        /// <param name="operation">Name of the operation that failed</param>
        /// <returns>True if recovery was successful, false otherwise</returns>
        /// <exception cref="AlsaException">Thrown if recovery fails and the error is not recoverable</exception>
        private bool HandleAlsaError(int err, IntPtr pcmHandle, string operation)
        {
            if (err >= 0)
            {
                return true; // Not an error
            }
            
            string errorMsg = AlsaNative.GetAlsaErrorMessage(err);
            
            // Handle common error cases
            if (err == -AlsaNative.EPIPE) // Xrun (buffer over/underrun)
            {
                Log(LogLevel.Warn, $"{operation}: Buffer xrun error (over/underrun), attempting recovery");
                
                int recoverErr = AlsaNative.snd_pcm_recover(pcmHandle, -AlsaNative.EPIPE, 1);
                if (recoverErr < 0)
                {
                    string recoverErrorMsg = AlsaNative.GetAlsaErrorMessage(recoverErr);
                    Log(LogLevel.Error, $"Failed to recover from xrun: {recoverErrorMsg}");
                    throw new AlsaException(
                        $"Failed to recover from xrun: {recoverErrorMsg}", 
                        recoverErr, 
                        "snd_pcm_recover");
                }
                
                Log(LogLevel.Info, "Successfully recovered from xrun");
                return true;
            }
            else if (err == -AlsaNative.ESTRPIPE) // PCM suspended
            {
                Log(LogLevel.Warn, $"{operation}: PCM device suspended, attempting recovery");
                
                // Wait for the device to be resumed
                while ((err = AlsaNative.snd_pcm_resume(pcmHandle)) == -AlsaNative.EAGAIN)
                {
                    Log(LogLevel.Info, "Device busy, waiting for resume...");
                    Thread.Sleep(100);
                }
                
                if (err < 0)
                {
                    // If resume fails, try to prepare the device
                    Log(LogLevel.Warn, "Resume failed, attempting to prepare device");
                    err = AlsaNative.snd_pcm_prepare(pcmHandle);
                    if (err < 0)
                    {
                        string recoverErrorMsg = AlsaNative.GetAlsaErrorMessage(err);
                        Log(LogLevel.Error, $"Failed to prepare device after suspend: {recoverErrorMsg}");
                        throw new AlsaException(
                            $"Failed to prepare device after suspend: {recoverErrorMsg}", 
                            err, 
                            "snd_pcm_prepare");
                    }
                }
                
                Log(LogLevel.Info, "Successfully recovered from suspend");
                return true;
            }
            else if (err == -AlsaNative.EBADFD) // PCM in wrong state
            {
                Log(LogLevel.Warn, $"{operation}: PCM device in wrong state, attempting recovery");
                
                // Get the current state
                AlsaNative.SndPcmState state = AlsaNative.snd_pcm_state(pcmHandle);
                Log(LogLevel.Info, $"Current PCM state: {state}");
                
                // Attempt to reset to a known state
                if (state != AlsaNative.SndPcmState.SND_PCM_STATE_PREPARED)
                {
                    err = AlsaNative.snd_pcm_prepare(pcmHandle);
                    if (err < 0)
                    {
                        string recoverErrorMsg = AlsaNative.GetAlsaErrorMessage(err);
                        Log(LogLevel.Error, $"Failed to prepare PCM device: {recoverErrorMsg}");
                        throw new AlsaException(
                            $"Failed to prepare PCM device: {recoverErrorMsg}", 
                            err, 
                            "snd_pcm_prepare");
                    }
                }
                
                Log(LogLevel.Info, "Successfully recovered from bad file descriptor state");
                return true;
            }
            else if (err == -AlsaNative.EAGAIN) // Resource temporarily unavailable
            {
                Log(LogLevel.Warn, $"{operation}: Resource temporarily unavailable, retrying may help");
                return false; // Caller should retry
            }
            else
            {
                // General recovery attempt for other errors
                Log(LogLevel.Warn, $"{operation}: ALSA error: {errorMsg}, attempting general recovery");
                
                int recoverErr = AlsaNative.snd_pcm_recover(pcmHandle, err, 1);
                if (recoverErr < 0)
                {
                    string recoverErrorMsg = AlsaNative.GetAlsaErrorMessage(recoverErr);
                    Log(LogLevel.Error, $"Failed to recover from error: {recoverErrorMsg}");
                    throw new AlsaException(
                        $"Failed to recover from error: {errorMsg}", 
                        err, 
                        operation);
                }
                
                Log(LogLevel.Info, "Successfully recovered from error");
                return true;
            }
        }

        /// <summary>
        /// Waits until all queued audio has been played back.
        /// Linux uses blocking I/O with snd_pcm_drain, so playback is already synchronous.
        /// </summary>
        /// <param name="timeout">Maximum time to wait (not used in Linux implementation).</param>
        /// <returns>Always returns true since Linux playback is synchronous.</returns>
        public Task<bool> WaitForPlaybackDrainAsync(TimeSpan? timeout = null)
        {
            Log(LogLevel.Info, "WaitForPlaybackDrain: Linux uses blocking I/O, playback already complete");
            return Task.FromResult(true);
        }

        /// <summary>
        /// Sets the diagnostic logging level for the Linux audio device.
        /// </summary>
        /// <param name="level">The diagnostic level to set.</param>
        /// <returns>A task that resolves to true if the level was set successfully, false otherwise.</returns>
        public async Task<bool> SetDiagnosticLevelAsync(DiagnosticLevel level)
        {
            await Task.CompletedTask;
            _diagnosticLevel = level;
            Log(LogLevel.Info, $"Diagnostic level set to: {level}");
            return true;
        }

        /// <summary>
        /// Gets the current diagnostic logging level.
        /// </summary>
        /// <returns>The current diagnostic level.</returns>
        public async Task<DiagnosticLevel> GetDiagnosticLevelAsync()
        {
            await Task.CompletedTask;
            return _diagnosticLevel;
        }

    }
} 