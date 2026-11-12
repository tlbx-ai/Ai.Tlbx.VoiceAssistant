using System.Buffers.Binary;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Demo.Web.Services;

internal sealed class ProbeFactory : IVoiceProviderFactory
{
    public IVoiceProvider CreateProvider(VoiceProviderType type, bool serverAudio = false) =>
        new ProbeProvider(type == VoiceProviderType.Google ? AudioSampleRate.Rate16000 : AudioSampleRate.Rate24000, serverAudio);
}

internal sealed class ProbeProvider(AudioSampleRate rate, bool serverAudio = true) : IVoiceProvider
{
    public bool ServerAudio => serverAudio;
    public bool IsConnected { get; private set; }
    public AudioSampleRate RequiredInputSampleRate => rate;
    public int InputChunks;
    public Action<ChatMessage>? OnMessageReceived { get; set; }
    public Action<string>? OnAudioReceived { get; set; }
    public Func<TimeSpan?, Task<bool>>? WaitForPlaybackDrainAsync { get; set; }
    public Action<string>? OnStatusChanged { get; set; }
    public Action<string>? OnError { get; set; }
    public Action? OnInterruptDetected { get; set; }
    public Action<UsageReport>? OnUsageReceived { get; set; }
    public Action<string>? OnTranscriptionDelta { get; set; }
    public Action<string>? OnTranscriptionCompleted { get; set; }
    public Task ConnectAsync(IVoiceSettings settings)
    {
        if (!ServerAudio) throw new InvalidOperationException("The SIP demo must select server audio.");
        IsConnected = true; OnStatusChanged?.Invoke("Connected to local test AI"); return Task.CompletedTask;
    }
    public Task DisconnectAsync() { IsConnected = false; OnStatusChanged?.Invoke("Disconnected"); return Task.CompletedTask; }
    public Task UpdateSettingsAsync(IVoiceSettings settings) => Task.CompletedTask;
    public Task ProcessAudioAsync(string base64Audio)
    {
        byte[] input = Convert.FromBase64String(base64Audio);
        if (input.Length == 0 || input.Length % 2 != 0) throw new InvalidOperationException("Invalid PCM input.");
        if (Interlocked.Increment(ref InputChunks) == 1)
        {
            OnMessageReceived?.Invoke(new("Wie viel ist sieben mal sechs?", "user"));
            OnMessageReceived?.Invoke(new("Berechnung läuft", "activity"));
            OnMessageReceived?.Invoke(new("Calling tool: calculator\nArguments: {\"expression\":\"7*6\"}", "assistant", "test-call", "calculator"));
            OnMessageReceived?.Invoke(new("{\"result\":42,\"details\":\"" + new string('x', 2000) + "\",\"tail\":\"COMPLETE-SIP-RESULT\"}", "tool", "test-call", "calculator"));
            OnMessageReceived?.Invoke(new("Die vollständige Berechnung ergibt 42.", "agent"));
            OnMessageReceived?.Invoke(new("Sieben mal sechs ist zweiundvierzig.", "assistant"));
            byte[] output = new byte[24000 / 2 * 2];
            for (int i = 0; i < output.Length / 2; i++)
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i * 2), (short)(5000 * Math.Sin(i * 2 * Math.PI * 440 / 24000)));
            OnAudioReceived?.Invoke(Convert.ToBase64String(output));
        }
        return Task.CompletedTask;
    }
    public Task SendInterruptAsync() { OnInterruptDetected?.Invoke(); return Task.CompletedTask; }
    public Task InjectConversationHistoryAsync(IEnumerable<ChatMessage> messages) => Task.CompletedTask;
    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
