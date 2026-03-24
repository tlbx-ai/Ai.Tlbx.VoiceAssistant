using Ai.Tlbx.VoiceAssistant;
using Ai.Tlbx.VoiceAssistant.Hardware.Linux;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Models;
using System.Diagnostics;
using System.Text.Json;

namespace Ai.Tlbx.VoiceAssistant.Demo.Linux
{
    class Program
    {
        private static LinuxAudioDevice? _audioDevice;
        private static VoiceAssistant? _voiceAssistant;
        private static OpenAiVoiceProvider? _voiceProvider;
        private static string _openAiApiKey = string.Empty;
        private static OpenAiRealtimeModel _openAiModel = OpenAiRealtimeModel.GptRealtime15;
        private static readonly ManualResetEvent _exitEvent = new ManualResetEvent(false);
        private static readonly CancellationTokenSource _cts = new CancellationTokenSource();

        static async Task Main(string[] args)
        {
            // Set up logging - direct console output only
            Action<LogLevel, string> logAction = (level, message) => 
            {
                var logPrefix = level switch
                {
                    LogLevel.Error => "[Error]",
                    LogLevel.Warn => "[Warn]",
                    LogLevel.Info => "[Info]",
                    _ => "[Info]"
                };
                Debug.WriteLine($"{logPrefix} {message}");
            };

            logAction(LogLevel.Info, "Linux Voice Assistant Demo");
            logAction(LogLevel.Info, "==========================");

            // Load configuration
            LoadConfiguration(logAction);

            // Set up audio
            try
            {
                _audioDevice = new LinuxAudioDevice();
                await _audioDevice.InitAudioAsync();
                logAction(LogLevel.Info, "Audio device initialized successfully");


                // Create voice provider and assistant
                if (string.IsNullOrEmpty(_openAiApiKey))
                {
                    logAction(LogLevel.Error, "No OpenAI API key found. Please update the appsettings.json file.");
                    return;
                }

                // Set the API key as an environment variable
                Environment.SetEnvironmentVariable("OPENAI_API_KEY", _openAiApiKey);
                
                _voiceProvider = new OpenAiVoiceProvider(null, logAction);
                _voiceAssistant = new VoiceAssistant(_audioDevice, _voiceProvider, logAction);
                
                // Subscribe to status updates
                _voiceAssistant.OnConnectionStatusChanged = status => logAction(LogLevel.Info, $"Status: {status}");
                _voiceAssistant.OnMessageAdded = message => logAction(LogLevel.Info, $"{message.Role}: {message.Content}");
                
                // UI and controls
                logAction(LogLevel.Info, "\nCommands:");
                logAction(LogLevel.Info, " 's' - Start conversation");
                logAction(LogLevel.Info, " 'p' - Pause/Resume");
                logAction(LogLevel.Info, " 'q' - Quit");

                // Start input handling in a separate task
                _ = Task.Run(() => HandleUserInput(logAction));
                
                // Wait for exit signal
                _exitEvent.WaitOne();
            }
            catch (Exception ex)
            {
                logAction(LogLevel.Error, $"Error: {ex.Message}");
                logAction(LogLevel.Error, $"Details: {ex}");
            }
            finally
            {
                // Clean up
                if (_voiceAssistant != null)
                {
                    await _voiceAssistant.DisposeAsync();
                }
                
                if (_audioDevice != null) 
                {
                    await _audioDevice.DisposeAsync();
                }
            }
        }

        private static void LoadConfiguration(Action<LogLevel, string> logAction)
        {
            try
            {
                if (File.Exists("appsettings.json"))
                {
                    var config = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText("appsettings.json"));
                    
                    if (config.TryGetProperty("OpenAI", out var openAi))
                    {
                        if (openAi.TryGetProperty("ApiKey", out var apiKey))
                        {
                            _openAiApiKey = apiKey.GetString() ?? string.Empty;
                        }
                        
                        if (openAi.TryGetProperty("Model", out var model))
                        {
                            var modelString = model.GetString() ?? "";
                            if (!OpenAiRealtimeModelExtensions.TryParseApiString(modelString, out _openAiModel))
                            {
                                _openAiModel = OpenAiRealtimeModel.GptRealtime15;
                            }
                        }
                    }
                }
                else
                {
                    logAction(LogLevel.Warn, "Warning: appsettings.json not found.");
                }
                
                // Try to load from environment variables (useful for containers)
                var envApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (!string.IsNullOrEmpty(envApiKey))
                {
                    _openAiApiKey = envApiKey;
                }
            }
            catch (Exception ex)
            {
                logAction(LogLevel.Error, $"Error loading configuration: {ex.Message}");
                logAction(LogLevel.Error, $"Details: {ex}");
            }
        }

        private static async void HandleUserInput(Action<LogLevel, string> logAction)
        {
            try
            {
                bool isRunning = false;
                
                while (!_cts.Token.IsCancellationRequested)
                {
                    var key = Console.ReadKey(true);
                    
                    switch (key.KeyChar)
                    {
                        case 's':
                            if (!isRunning)
                            {
                                isRunning = true;
                                logAction(LogLevel.Info, "Starting conversation...");
                                var settings = new OpenAiVoiceSettings
                                {
                                    Instructions = "You are a helpful AI assistant. Be friendly, conversational, helpful, and engaging. When the user speaks interrupt your answer and listen and then answer the new question.",
                                    Voice = AssistantVoice.Alloy,
                                    Model = _openAiModel
                                };
                                await _voiceAssistant!.StartAsync(settings);
                            }
                            break;
                            
                        case 'p':
                            if (isRunning)
                            {
                                // Send interrupt signal to stop current AI response
                                logAction(LogLevel.Info, "Sending interrupt signal...");
                                await _voiceAssistant!.InterruptAsync();
                            }
                            break;
                            
                        case 'q':
                            logAction(LogLevel.Info, "Exiting...");
                            if (isRunning && _voiceAssistant != null)
                            {
                                await _voiceAssistant.StopAsync();
                            }
                            _cts.Cancel();
                            _exitEvent.Set();
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                logAction(LogLevel.Error, $"Input handling error: {ex.Message}");
                logAction(LogLevel.Error, $"Details: {ex}");
                _exitEvent.Set();
            }
        }
    }
}
