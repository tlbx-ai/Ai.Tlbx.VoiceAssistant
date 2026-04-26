using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Managers;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant
{
    /// <summary>
    /// Main orchestrator for voice assistant functionality across different AI providers.
    /// Manages the interaction between hardware, AI providers, and UI components.
    /// </summary>
    public sealed class VoiceAssistant : IAsyncDisposable
    {
        private readonly IAudioHardwareAccess _hardwareAccess;
        private readonly IVoiceProvider? _provider;
        private readonly ChatHistoryManager _chatHistory;
        private readonly UsageManager _usageManager;
        private readonly Action<LogLevel, string> _logAction;
        
        // Pre-connect audio buffer
        private ConcurrentQueue<string>? _preConnectBuffer;

        // State management
        private bool _isInitialized = false;
        private bool _isConnecting = false;
        private bool _isDisposed = false;
        private bool _isMicrophoneTesting = false;
        private string? _lastErrorMessage = null;
        private string _connectionStatus = string.Empty;
        private DateTime? _sessionStartTime = null;
        private DateTime _lastUsageUpdateTime = DateTime.MinValue;
        
        // Static cache for generated beeps (thread-safe)
        private static readonly ConcurrentDictionary<(int frequency, int duration, int sampleRate), string> _beepCache = new();

        // UI Callbacks - Direct actions for simple 1:1 communication
        /// <summary>
        /// Callback that fires when the connection status changes.
        /// </summary>
        public Action<string>? OnConnectionStatusChanged { get; set; }
        
        /// <summary>
        /// Callback that fires when a new message is added to the chat history.
        /// </summary>
        public Action<ChatMessage>? OnMessageAdded { get; set; }
        
        /// <summary>
        /// Callback that fires when the list of microphone devices changes.
        /// </summary>
        public Action<List<AudioDeviceInfo>>? OnMicrophoneDevicesChanged { get; set; }

        /// <summary>
        /// Callback that fires when usage data is received from the AI provider.
        /// For per-response granular token data.
        /// </summary>
        public Action<UsageReport>? OnUsageReceived { get; set; }

        /// <summary>
        /// Callback that fires with cumulative session usage (tokens + duration).
        /// Fires on: token usage received, every minute elapsed, and session end.
        /// Subscribe to this single callback for unified usage tracking.
        /// </summary>
        public Action<SessionUsageUpdate>? OnSessionUsageUpdated { get; set; }

        /// <summary>
        /// Callback that fires with partial transcription text as the user speaks.
        /// </summary>
        public Action<string>? OnTranscriptionDelta { get; set; }

        /// <summary>
        /// Callback that fires with the finalized transcript when an utterance completes.
        /// </summary>
        public Action<string>? OnTranscriptionCompleted { get; set; }

        // Public properties
        /// <summary>
        /// Gets a value indicating whether the voice assistant is initialized.
        /// </summary>
        public bool IsInitialized => _isInitialized;
        
        /// <summary>
        /// Gets a value indicating whether audio recording is active.
        /// </summary>
        public bool IsRecording { get; private set; }
        
        /// <summary>
        /// Gets a value indicating whether the voice assistant is currently connecting.
        /// </summary>
        public bool IsConnecting => _isConnecting;
        
        /// <summary>
        /// Gets a value indicating whether microphone testing is active.
        /// </summary>
        public bool IsMicrophoneTesting => _isMicrophoneTesting;
        
        /// <summary>
        /// Gets the last error message, if any.
        /// </summary>
        public string? LastErrorMessage => _lastErrorMessage;
        
        /// <summary>
        /// Gets the current connection status.
        /// </summary>
        public string ConnectionStatus => _connectionStatus;
        
        /// <summary>
        /// Gets the chat history as a read-only list.
        /// </summary>
        public IReadOnlyList<ChatMessage> ChatHistory => _chatHistory.GetMessages();

        /// <summary>
        /// Gets the usage reports as a read-only list.
        /// </summary>
        public IReadOnlyList<UsageReport> UsageReports => _usageManager.GetReports();

        /// <summary>
        /// Gets the total tokens used in the current session.
        /// </summary>
        public int TotalTokensUsed => _usageManager.TotalTokens;

        /// <summary>
        /// Gets the total audio input tokens used in the current session.
        /// </summary>
        public int TotalAudioInputTokens => _usageManager.TotalAudioInputTokens;

        /// <summary>
        /// Gets the total audio output tokens used in the current session.
        /// </summary>
        public int TotalAudioOutputTokens => _usageManager.TotalAudioOutputTokens;

        /// <summary>
        /// Gets a value indicating whether a session is currently active.
        /// </summary>
        public bool IsSessionActive => _sessionStartTime.HasValue;

        /// <summary>
        /// Gets a value indicating whether the provider is connected and ready for audio.
        /// </summary>
        public bool IsProviderConnected => _isInitialized && (_provider?.IsConnected ?? false);

        /// <summary>
        /// Gets the session start time (UTC) measured locally, or null if no session is active.
        /// </summary>
        public DateTime? LocalSessionStartTime => _sessionStartTime;

        /// <summary>
        /// Gets the current session duration measured locally (client-side).
        /// Note: May differ from provider billing duration due to network latency,
        /// connection establishment time, and clock differences.
        /// </summary>
        public TimeSpan LocalSessionDuration => _sessionStartTime.HasValue
            ? DateTime.UtcNow - _sessionStartTime.Value
            : TimeSpan.Zero;

        /// <summary>
        /// Initializes a new instance of the <see cref="VoiceAssistant"/> class.
        /// </summary>
        /// <param name="hardwareAccess">The audio hardware access implementation.</param>
        /// <param name="provider">The AI voice provider implementation.</param>
        /// <param name="logAction">Optional logging action for compatibility.</param>
        public VoiceAssistant(
            IAudioHardwareAccess hardwareAccess,
            IVoiceProvider? provider,
            Action<LogLevel, string>? logAction = null)
        {
            _hardwareAccess = hardwareAccess ?? throw new ArgumentNullException(nameof(hardwareAccess));
            _provider = provider; // Provider can be null for mic testing only
            _logAction = logAction ?? ((level, message) => { /* no-op */ });
            _chatHistory = new ChatHistoryManager();
            _usageManager = new UsageManager();

            // Set up hardware logging
            _hardwareAccess.SetLogAction(_logAction);

            // Wire up provider callbacks (only if provider is set)
            if (_provider != null)
            {
                WireUpProviderCallbacks();
            }
        }

        /// <summary>
        /// Starts the voice assistant with the specified settings.
        /// </summary>
        /// <param name="settings">Provider-specific settings for the voice assistant.</param>
        /// <returns>A task representing the start operation.</returns>
        public async Task StartAsync(IVoiceSettings settings)
        {
            await StartAsync(settings, CancellationToken.None);
        }

        /// <summary>
        /// Starts the voice assistant with the specified settings.
        /// </summary>
        /// <param name="settings">Provider-specific settings for the voice assistant.</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
        /// <returns>A task representing the start operation.</returns>
        public async Task StartAsync(IVoiceSettings settings, CancellationToken cancellationToken)
        {
            if (_provider == null)
            {
                throw new InvalidOperationException("Cannot start voice assistant: no provider configured. VoiceAssistant was created for mic testing only.");
            }

            try
            {
                _lastErrorMessage = null;

                // Initialize hardware and start recording early so audio is captured during connection
                await _hardwareAccess.InitAudioAsync();

                if (!_isInitialized || !_provider.IsConnected)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Start pre-connect buffer and recording before connecting
                    _preConnectBuffer = new ConcurrentQueue<string>();
                    bool recordingStarted = await _hardwareAccess.StartRecordingAudio(OnAudioDataReceived, _provider.RequiredInputSampleRate);
                    if (!recordingStarted)
                    {
                        _preConnectBuffer = null;
                        throw new InvalidOperationException("Failed to start audio recording");
                    }
                    IsRecording = true;

                    _isConnecting = true;
                    ReportStatus("Connecting to AI provider...");
                    await _provider.ConnectAsync(settings);
                    _isInitialized = true;
                    _isConnecting = false;

                    // Flush pre-connect buffer
                    var buffer = _preConnectBuffer;
                    _preConnectBuffer = null;
                    if (buffer != null)
                    {
                        int flushed = 0;
                        while (buffer.TryDequeue(out var audio))
                        {
                            await _provider.ProcessAudioAsync(audio);
                            flushed++;
                        }
                        _logAction(LogLevel.Info, $"Flushed {flushed} pre-connect audio chunks");
                    }

                    // Inject conversation history if available
                    var history = _chatHistory.GetMessages();
                    if (history.Any())
                    {
                        var messagesToInject = new List<ChatMessage>();
                        for (int i = 0; i < history.Count; i++)
                        {
                            messagesToInject.Add(history[i]);
                        }

                        if (messagesToInject.Count > 0 &&
                            messagesToInject[messagesToInject.Count - 1].Role == ChatMessage.AssistantRole)
                        {
                            messagesToInject.RemoveAt(messagesToInject.Count - 1);
                        }

                        if (messagesToInject.Any())
                        {
                            _logAction(LogLevel.Info, $"Injecting {messagesToInject.Count} messages from conversation history (excluded last assistant message if any)");
                            await _provider.InjectConversationHistoryAsync(messagesToInject);
                        }
                    }
                }
                else
                {
                    // Provider is already connected, just update the settings
                    _logAction(LogLevel.Info, "Provider already connected, updating settings");
                    await _provider.UpdateSettingsAsync(settings);

                    bool recordingStarted = await _hardwareAccess.StartRecordingAudio(OnAudioDataReceived, _provider.RequiredInputSampleRate);
                    if (!recordingStarted)
                    {
                        throw new InvalidOperationException("Failed to start audio recording");
                    }
                    IsRecording = true;
                }

                // Start session timing
                _sessionStartTime = DateTime.UtcNow;
                _lastUsageUpdateTime = DateTime.UtcNow;

                ReportStatus("Voice assistant started and recording");
                _logAction(LogLevel.Info, "Voice assistant started successfully");
            }
            catch (Exception ex)
            {
                _lastErrorMessage = ex.Message;
                _isConnecting = false;
                ReportStatus($"Error: {ex.Message}");
                _logAction(LogLevel.Error, $"Failed to start voice assistant: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Stops the voice assistant and disconnects from the AI provider.
        /// </summary>
        /// <returns>A task representing the stop operation.</returns>
        public async Task StopAsync()
        {
            try
            {
                // First stop recording to prevent new audio
                await _hardwareAccess.StopRecordingAudio();
                IsRecording = false;
                
                // Clear any queued audio immediately
                await _hardwareAccess.ClearAudioQueueAsync();

                // Then disconnect from provider (if connected)
                if (_provider != null)
                {
                    await _provider.DisconnectAsync();
                }

                // Fire final session usage update
                FireSessionUsageUpdate(SessionUsageUpdateTrigger.SessionEnded);
                _sessionStartTime = null;

                _isInitialized = false;

                ReportStatus("Voice assistant stopped");
                _logAction(LogLevel.Info, "Voice assistant stopped");
            }
            catch (Exception ex)
            {
                _lastErrorMessage = ex.Message;
                ReportStatus($"Error stopping: {ex.Message}");
                _logAction(LogLevel.Error, $"Error stopping voice assistant: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Connects the provider without starting audio recording.
        /// Use with <see cref="StartRecordingOnlyAsync"/> for push-to-talk workflows.
        /// </summary>
        public async Task ConnectProviderAsync(IVoiceSettings settings)
        {
            if (_provider == null)
                throw new InvalidOperationException("Cannot connect: no provider configured.");

            try
            {
                _lastErrorMessage = null;
                await _hardwareAccess.InitAudioAsync();

                if (!_isInitialized || !_provider.IsConnected)
                {
                    _isConnecting = true;
                    ReportStatus("Connecting...");
                    await _provider.ConnectAsync(settings);
                    _isInitialized = true;
                    _isConnecting = false;
                    ReportStatus("Connected (idle)");
                    _logAction(LogLevel.Info, "Provider connected (idle, not recording)");
                }
                else
                {
                    await _provider.UpdateSettingsAsync(settings);
                    _logAction(LogLevel.Info, "Provider settings updated");
                }
            }
            catch (Exception ex)
            {
                _lastErrorMessage = ex.Message;
                _isConnecting = false;
                ReportStatus($"Error: {ex.Message}");
                _logAction(LogLevel.Error, $"Failed to connect provider: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Starts audio recording when the provider is already connected.
        /// Use after <see cref="ConnectProviderAsync"/> for push-to-talk workflows.
        /// </summary>
        public async Task StartRecordingOnlyAsync()
        {
            if (_provider == null || !_provider.IsConnected)
                throw new InvalidOperationException("Provider not connected. Call ConnectProviderAsync first.");

            if (IsRecording) return;

            var started = await _hardwareAccess.StartRecordingAudio(OnAudioDataReceived, _provider.RequiredInputSampleRate);
            if (!started)
                throw new InvalidOperationException("Failed to start audio recording");

            IsRecording = true;
            _sessionStartTime ??= DateTime.UtcNow;
            _lastUsageUpdateTime = DateTime.UtcNow;
            ReportStatus("Recording...");
            _logAction(LogLevel.Info, "Audio recording started (provider already connected)");
        }

        /// <summary>
        /// Stops audio recording but keeps the provider connected.
        /// Remaining audio in the provider pipeline will still be processed.
        /// </summary>
        public async Task StopRecordingOnlyAsync()
        {
            if (!IsRecording) return;

            await _hardwareAccess.StopRecordingAudio();
            IsRecording = false;
            ReportStatus("Processing...");
            _logAction(LogLevel.Info, "Audio recording stopped (provider still connected)");
        }

        /// <summary>
        /// Sends an interrupt signal to stop the current AI response.
        /// </summary>
        /// <returns>A task representing the interrupt operation.</returns>
        public async Task InterruptAsync()
        {
            try
            {
                // Send interrupt to provider (if connected)
                if (_provider != null)
                {
                    await _provider.SendInterruptAsync();
                }

                // Clear any pending audio to stop playback immediately
                await _hardwareAccess.ClearAudioQueueAsync();
                
                _logAction(LogLevel.Info, "Interrupt signal sent to AI provider and audio queue cleared");
            }
            catch (Exception ex)
            {
                _lastErrorMessage = ex.Message;
                _logAction(LogLevel.Error, $"Error sending interrupt: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Tests the microphone by recording a brief sample and playing it back.
        /// </summary>
        /// <returns>A task representing the microphone test operation.</returns>
        public async Task<bool> TestMicrophoneAsync()
        {
            List<string> recordedAudioChunks = new List<string>();
            
            try
            {
                _isMicrophoneTesting = true;
                ReportStatus("Testing speakers...");
                
                // Initialize audio hardware first
                await _hardwareAccess.InitAudioAsync();
                
                // Play initial beep to test speakers (440 Hz for 200ms)
                _logAction(LogLevel.Info, "Playing initial beep to test speakers");
                _hardwareAccess.PlayAudio(GenerateBeep(440, 200), 24000);
                await Task.Delay(300); // Wait a bit after beep
                
                ReportStatus("Testing microphone - recording...");
                
                // Start recording with a callback that collects audio chunks
                bool testRecordingStarted = await _hardwareAccess.StartRecordingAudio((sender, e) =>
                {
                    recordedAudioChunks.Add(e.Base64EncodedPcm16Audio);
                    _logAction(LogLevel.Info, $"Test audio chunk received: {e.Base64EncodedPcm16Audio.Length} chars");
                });
                
                if (!testRecordingStarted)
                {
                    ReportStatus("Microphone test failed: Could not start recording");
                    return false;
                }
                
                // Record for 5 seconds
                await Task.Delay(5000);
                
                // Stop recording
                await _hardwareAccess.StopRecordingAudio();
                
                // Play end-of-recording beep (880 Hz for 200ms - higher pitch)
                _logAction(LogLevel.Info, "Playing end-of-recording beep");
                _hardwareAccess.PlayAudio(GenerateBeep(880, 200), 24000);
                await Task.Delay(300); // Wait a bit after beep
                
                if (recordedAudioChunks.Count == 0)
                {
                    ReportStatus("Microphone test failed: No audio data received");
                    return false;
                }
                
                ReportStatus("Playing back recorded audio...");
                _logAction(LogLevel.Info, $"Playing back {recordedAudioChunks.Count} audio chunks");

                // Play back all recorded chunks
                foreach (var audioChunk in recordedAudioChunks)
                {
                    _hardwareAccess.PlayAudio(audioChunk, 24000);
                }

                // Wait for playback buffer to drain properly
                _logAction(LogLevel.Info, "Waiting for playback to drain...");
                await _hardwareAccess.WaitForPlaybackDrainAsync(TimeSpan.FromSeconds(15));

                // Play final success beep (660 Hz for 300ms)
                _logAction(LogLevel.Info, "Playing success beep");
                _hardwareAccess.PlayAudio(GenerateBeep(660, 300), 24000);
                await _hardwareAccess.WaitForPlaybackDrainAsync(TimeSpan.FromSeconds(2));

                ReportStatus("Microphone test completed successfully");
                _logAction(LogLevel.Info, "Microphone test completed successfully");
                return true;
            }
            catch (Exception ex)
            {
                _lastErrorMessage = ex.Message;
                ReportStatus($"Microphone test failed: {ex.Message}");
                _logAction(LogLevel.Error, $"Microphone test failed: {ex.Message}");
                return false;
            }
            finally
            {
                _isMicrophoneTesting = false;
            }
        }

        /// <summary>
        /// Generates a simple sine wave beep as base64 encoded PCM audio.
        /// </summary>
        /// <param name="frequency">The frequency of the beep in Hz.</param>
        /// <param name="durationMs">The duration of the beep in milliseconds.</param>
        /// <param name="sampleRate">The sample rate in Hz (default 24000).</param>
        /// <returns>Base64 encoded PCM16 audio data.</returns>
        private string GenerateBeep(int frequency = 440, int durationMs = 200, int sampleRate = 24000)
        {
            // Check cache first
            var cacheKey = (frequency, durationMs, sampleRate);
            if (_beepCache.TryGetValue(cacheKey, out var cachedBeep))
            {
                return cachedBeep;
            }
            
            // Ensure frequency is below Nyquist frequency to prevent aliasing
            if (frequency > sampleRate / 2)
            {
                _logAction(LogLevel.Warn, $"Beep frequency {frequency}Hz exceeds Nyquist limit for {sampleRate}Hz sample rate");
                frequency = sampleRate / 2 - 100; // Set to just below Nyquist
            }
            
            int numSamples = (sampleRate * durationMs) / 1000;
            byte[] pcmData = new byte[numSamples * 2]; // 16-bit PCM = 2 bytes per sample
            
            // Fade in/out duration (5ms each)
            int fadeSamples = (sampleRate * 5) / 1000;
            fadeSamples = Math.Min(fadeSamples, numSamples / 4); // Don't fade more than 25% of the signal
            
            for (int i = 0; i < numSamples; i++)
            {
                double angle = 2.0 * Math.PI * frequency * i / sampleRate;
                double amplitude = 16000; // Base amplitude
                
                // Apply fade-in envelope
                if (i < fadeSamples)
                {
                    amplitude *= (double)i / fadeSamples;
                }
                // Apply fade-out envelope
                else if (i >= numSamples - fadeSamples)
                {
                    amplitude *= (double)(numSamples - i) / fadeSamples;
                }
                
                short sample = (short)(Math.Sin(angle) * amplitude);
                
                // Convert to little-endian bytes
                pcmData[i * 2] = (byte)(sample & 0xFF);
                pcmData[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }
            
            var beepData = Convert.ToBase64String(pcmData);
            _beepCache[cacheKey] = beepData; // Cache for future use
            return beepData;
        }

        /// <summary>
        /// Gets the list of available microphone devices.
        /// </summary>
        /// <returns>A list of available audio devices.</returns>
        public async Task<List<AudioDeviceInfo>> GetAvailableMicrophonesAsync()
        {
            try
            {
                return await _hardwareAccess.GetAvailableMicrophonesAsync();
            }
            catch (Exception ex)
            {
                _lastErrorMessage = ex.Message;
                _logAction(LogLevel.Error, $"Error getting available microphones: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Clears the chat history.
        /// </summary>
        public void ClearChatHistory()
        {
            _chatHistory.ClearHistory();
            _logAction(LogLevel.Info, "Chat history cleared");
        }

        /// <summary>
        /// Clears all usage reports from the session.
        /// </summary>
        public void ClearUsageReports()
        {
            _usageManager.ClearReports();
            _logAction(LogLevel.Info, "Usage reports cleared");
        }

        private void FireSessionUsageUpdate(SessionUsageUpdateTrigger trigger)
        {
            if (!_sessionStartTime.HasValue)
            {
                return;
            }

            var update = new SessionUsageUpdate
            {
                Trigger = trigger,
                LocalSessionDuration = DateTime.UtcNow - _sessionStartTime.Value,
                TotalInputTokens = _usageManager.TotalInputTokens,
                TotalOutputTokens = _usageManager.TotalOutputTokens,
                TotalAudioInputTokens = _usageManager.TotalAudioInputTokens,
                TotalAudioOutputTokens = _usageManager.TotalAudioOutputTokens,
                TotalTokens = _usageManager.TotalTokens
            };

            _lastUsageUpdateTime = DateTime.UtcNow;
            _logAction(LogLevel.Info, $"Session usage update ({trigger}): {update.LocalSessionDuration.TotalMinutes:F1} min, {update.TotalTokens} tokens");

            // Fire non-blocking on thread pool
            if (OnSessionUsageUpdated != null)
            {
                System.Threading.ThreadPool.QueueUserWorkItem(_ => OnSessionUsageUpdated?.Invoke(update));
            }
        }

        private void CheckAndFireMinuteUpdate()
        {
            if (!_sessionStartTime.HasValue)
            {
                return;
            }

            var elapsed = DateTime.UtcNow - _lastUsageUpdateTime;
            if (elapsed >= TimeSpan.FromMinutes(1))
            {
                FireSessionUsageUpdate(SessionUsageUpdateTrigger.MinuteElapsed);
            }
        }

        private void WireUpProviderCallbacks()
        {
            if (_provider == null) return;

            _provider.OnMessageReceived = (message) =>
            {
                _chatHistory.AddMessage(message);
                OnMessageAdded?.Invoke(message);
                _logAction(LogLevel.Info, $"Message received from provider: {message.Role} - {message.Content?.Length ?? 0} chars");
            };
            
            _provider.OnAudioReceived = (base64Audio) =>
            {
                try
                {
                    // Forward audio to hardware for playback at 24kHz (OpenAI default)
                    _hardwareAccess.PlayAudio(base64Audio, 24000);
                }
                catch (Exception ex)
                {
                    _logAction(LogLevel.Error, $"Error playing audio: {ex.Message}");
                }
            };

            _provider.WaitForPlaybackDrainAsync = _hardwareAccess.WaitForPlaybackDrainAsync;
            
            _provider.OnStatusChanged = async (status) =>
            {
                ReportStatus(status);
                
                // Handle interruption status from provider
                if (status.Contains("interrupted", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _hardwareAccess.ClearAudioQueueAsync();
                        _logAction(LogLevel.Info, "Audio queue cleared due to interruption");
                    }
                    catch (Exception ex)
                    {
                        _logAction(LogLevel.Error, $"Error clearing audio queue: {ex.Message}");
                    }
                }
            };
            
            _provider.OnError = (error) =>
            {
                _lastErrorMessage = error;
                ReportStatus($"Provider error: {error}");
                _logAction(LogLevel.Error, $"Provider error: {error}");
            };
            
            // Wire up interruption detection to clear audio immediately
            _provider.OnInterruptDetected = async () =>
            {
                try
                {
                    await _hardwareAccess.ClearAudioQueueAsync();
                    // Audio queue cleared for interruption - normal operation
                }
                catch (Exception ex)
                {
                    _logAction(LogLevel.Error, $"Error clearing audio queue on interruption: {ex.Message}");
                }
            };

            // Wire up usage reporting
            _provider.OnUsageReceived = (usage) =>
            {
                _usageManager.AddReport(usage);
                OnUsageReceived?.Invoke(usage);
                _logAction(LogLevel.Info, $"Usage: {usage.TotalInputTokens} in, {usage.TotalOutputTokens} out (session total: {_usageManager.TotalTokens})");

                // Also fire combined session usage update
                FireSessionUsageUpdate(SessionUsageUpdateTrigger.TokenUsageReceived);
            };

            _provider.OnTranscriptionDelta = (delta) =>
            {
                OnTranscriptionDelta?.Invoke(delta);
            };

            _provider.OnTranscriptionCompleted = (transcript) =>
            {
                var message = ChatMessage.CreateUserMessage(transcript);
                _chatHistory.AddMessage(message);
                OnMessageAdded?.Invoke(message);
                OnTranscriptionCompleted?.Invoke(transcript);
                _logAction(LogLevel.Info, $"Transcription completed: {transcript.Length} chars");
            };
        }

        private int _audioReceivedCount = 0;

        private void OnAudioDataReceived(object sender, MicrophoneAudioReceivedEventArgs e)
        {
            _ = ProcessAudioDataSafeAsync(e);
        }

        private async Task ProcessAudioDataSafeAsync(MicrophoneAudioReceivedEventArgs e)
        {
            try
            {
                _audioReceivedCount++;
                var isConnected = _provider?.IsConnected ?? false;
                if (_audioReceivedCount % 50 == 1)
                {
                    _logAction(LogLevel.Info, $"[VA-AUDIO] OnAudioDataReceived called {_audioReceivedCount} times, IsConnected: {isConnected}, IsTesting: {_isMicrophoneTesting}, AudioLength: {e.Base64EncodedPcm16Audio?.Length ?? 0}");
                }

                var preBuffer = _preConnectBuffer;
                if (!isConnected && preBuffer != null && !_isMicrophoneTesting)
                {
                    preBuffer.Enqueue(e.Base64EncodedPcm16Audio ?? "");
                }
                else if (isConnected && !_isMicrophoneTesting)
                {
                    if (_audioReceivedCount % 50 == 1)
                    {
                        _logAction(LogLevel.Info, $"[VA-AUDIO] Calling ProcessAudioAsync on provider");
                    }

                    if (_provider != null)
                    {
                        await _provider.ProcessAudioAsync(e.Base64EncodedPcm16Audio ?? "");
                    }

                    // Check if a minute has elapsed since last usage update
                    CheckAndFireMinuteUpdate();
                }
                else
                {
                    if (_audioReceivedCount % 50 == 1)
                    {
                        _logAction(LogLevel.Warn, $"[VA-AUDIO] Skipping audio - IsConnected: {isConnected}, IsTesting: {_isMicrophoneTesting}");
                    }
                }
            }
            catch (Exception ex)
            {
                _lastErrorMessage = ex.Message;
                _logAction(LogLevel.Error, $"Error processing audio data: {ex.Message}");
                _logAction(LogLevel.Error, $"Stack trace: {ex.StackTrace}");
            }
        }


        private void ReportStatus(string status)
        {
            _connectionStatus = status;
            OnConnectionStatusChanged?.Invoke(status);
            _logAction(LogLevel.Info, $"Status: {status}");
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources asynchronously.
        /// </summary>
        /// <returns>A task that represents the asynchronous dispose operation.</returns>
        public async ValueTask DisposeAsync()
        {
            if (_isDisposed)
                return;

            try
            {
                if (_isInitialized)
                {
                    await StopAsync();
                }
                
                if (_provider != null)
                {
                    _provider.OnMessageReceived = null;
                    _provider.OnAudioReceived = null;
                    _provider.WaitForPlaybackDrainAsync = null;
                    _provider.OnStatusChanged = null;
                    _provider.OnError = null;
                    _provider.OnInterruptDetected = null;
                    _provider.OnUsageReceived = null;
                    _provider.OnTranscriptionDelta = null;
                    _provider.OnTranscriptionCompleted = null;
                    await _provider.DisposeAsync();
                }
                await _hardwareAccess.DisposeAsync();
                
                _isDisposed = true;
                _logAction(LogLevel.Info, "Voice assistant disposed");
            }
            catch (Exception ex)
            {
                _logAction(LogLevel.Error, $"Error during disposal: {ex.Message}");
            }
        }
    }
}
