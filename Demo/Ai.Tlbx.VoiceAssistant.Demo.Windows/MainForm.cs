using Ai.Tlbx.VoiceAssistant;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Hardware.Windows;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Models;
using System.Diagnostics;

namespace Ai.Tlbx.VoiceAssistant.Demo.Windows
{
    public partial class MainForm : Form
    {
        private readonly IAudioHardwareAccess _audioHardware;
        private readonly OpenAiVoiceProvider _voiceProvider;
        private readonly VoiceAssistant _voiceAssistant;
        private bool _isRecording = false;
        
        public MainForm()
        {
            InitializeComponent();
            
            // Create the audio hardware instance for Windows
            _audioHardware = new WindowsAudioHardware();
            
            // Hook up audio error events directly
            _audioHardware.AudioError += OnAudioError;
            
            // Create the voice provider and assistant
            _voiceProvider = new OpenAiVoiceProvider(null, LogMessage);
            _voiceAssistant = new VoiceAssistant(_audioHardware, _voiceProvider, LogMessage);
            
            // Hook up events
            _voiceAssistant.OnMessageAdded = OnMessageAdded;
            _voiceAssistant.OnConnectionStatusChanged = OnConnectionStatusChanged;
            
            // Initial UI state
            UpdateUIState();
            
            LogMessage(LogLevel.Info, "MainForm initialized");
        }
        
        private void OnAudioError(object? sender, string errorMessage)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnAudioError(sender, errorMessage)));
                return;
            }
            
            // Display the error message in the UI
            lblStatus.Text = $"Audio Error: {errorMessage}";
            LogMessage(LogLevel.Error, $"Audio Error: {errorMessage}");
            MessageBox.Show(errorMessage, "Audio Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        
        private void OnMessageAdded(ChatMessage message)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnMessageAdded(message)));
                return;
            }
            
            // Add the message to the transcript
            string rolePrefix = message.Role == ChatMessage.UserRole ? "You: " : 
                              message.Role == ChatMessage.ToolRole ? "[Tool]: " : "AI: ";
            txtTranscription.AppendText($"{rolePrefix}{message.Content}\r\n\r\n");
        }
        
        private void OnConnectionStatusChanged(string status)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => OnConnectionStatusChanged(status)));
                return;
            }
            
            lblStatus.Text = status;
            LogMessage(LogLevel.Info, $"Connection status: {status}");
            UpdateUIState();
        }
        
        
        private async void btnTestMic_Click(object sender, EventArgs e)
        {
            try
            {
                btnTestMic.Enabled = false;
                lblStatus.Text = "Testing microphone...";
                LogMessage(LogLevel.Info, "Starting microphone test");
                
                // Use the voice assistant for microphone testing
                bool success = await _voiceAssistant.TestMicrophoneAsync();
                
                if (!success)
                {
                    lblStatus.Text = "Microphone test failed";
                    LogMessage(LogLevel.Error, "Microphone test failed");
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"Error testing microphone: {ex.Message}";
                LogMessage(LogLevel.Error, $"Microphone test error: {ex.Message}\nStackTrace: {ex.StackTrace}");
                MessageBox.Show($"Error testing microphone: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnTestMic.Enabled = true;
                UpdateUIState();
            }
        }
        
        private async void btnStart_Click(object sender, EventArgs e)
        {
            if (_isRecording)
            {
                return;
            }
            
            try
            {
                btnStart.Enabled = false;
                lblStatus.Text = "Starting...";
                LogMessage(LogLevel.Info, "Starting recording session");
                
                var settings = new OpenAiVoiceSettings
                {
                    Instructions = "You are a helpful AI assistant. Be friendly, conversational, helpful, and engaging.",
                    Voice = AssistantVoice.Alloy,
                    Model = OpenAiRealtimeModel.GptRealtime
                };
                
                await _voiceAssistant.StartAsync(settings);
                _isRecording = true;
                lblStatus.Text = "Recording in progress...";
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"Error starting: {ex.Message}";
                LogMessage(LogLevel.Error, $"Start recording error: {ex.Message}\nStackTrace: {ex.StackTrace}");
                MessageBox.Show($"Error starting recording: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UpdateUIState();
            }
        }
        
        private async void btnEnd_Click(object sender, EventArgs e)
        {
            if (!_isRecording)
            {
                return;
            }
            
            try
            {
                btnEnd.Enabled = false;
                lblStatus.Text = "Ending recording...";
                LogMessage(LogLevel.Info, "Ending recording session");
                
                await _voiceAssistant.StopAsync();
                _isRecording = false;
                lblStatus.Text = "Recording ended";
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"Error ending recording: {ex.Message}";
                LogMessage(LogLevel.Error, $"End recording error: {ex.Message}\nStackTrace: {ex.StackTrace}");
                MessageBox.Show($"Error ending recording: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UpdateUIState();
            }
        }
        
        private async void btnInterrupt_Click(object sender, EventArgs e)
        {
            if (!_isRecording)
            {
                return;
            }
            
            try
            {
                btnInterrupt.Enabled = false;
                lblStatus.Text = "Interrupting...";
                LogMessage(LogLevel.Info, "Interrupting audio session");
                
                await _voiceAssistant.InterruptAsync();
                lblStatus.Text = "Interrupted";
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"Error interrupting: {ex.Message}";
                LogMessage(LogLevel.Error, $"Interrupt error: {ex.Message}\nStackTrace: {ex.StackTrace}");
                MessageBox.Show($"Error interrupting: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UpdateUIState();
            }
        }
        
        private void LogMessage(LogLevel level, string message)
        {
            var logPrefix = level switch
            {
                LogLevel.Error => "[Error]",
                LogLevel.Warn => "[Warn]",
                LogLevel.Info => "[Info]",
                _ => "[Info]"
            };
            Debug.WriteLine($"{logPrefix} {message}");
        }

        private void UpdateUIState()
        {
            bool isConnecting = _voiceAssistant?.IsConnecting ?? false;
            bool isInitialized = _voiceAssistant?.IsInitialized ?? false;
            bool isMicTesting = _voiceAssistant?.IsMicrophoneTesting ?? false;
            
            btnTestMic.Enabled = !_isRecording && !isMicTesting && !isConnecting;
            btnStart.Enabled = !_isRecording && !isMicTesting && !isConnecting;
            btnInterrupt.Enabled = isInitialized && !isConnecting;
            btnEnd.Enabled = _isRecording && !isConnecting;
        }
        
        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            // If we're already closing, just continue
            if (e.CloseReason == CloseReason.ApplicationExitCall)
            {
                base.OnFormClosing(e);
                return;
            }
            
            // Cancel the close for now
            e.Cancel = true;
            
            // Cleanup
            if (_voiceAssistant != null)
            {
                _voiceAssistant.OnMessageAdded = null;
                _voiceAssistant.OnConnectionStatusChanged = null;
                await _voiceAssistant.DisposeAsync();
            }
            
            if (_audioHardware != null)
            {
                _audioHardware.AudioError -= OnAudioError;
            }
            
            // Now actually close the form
            Application.Exit();
        }
    }
}
