using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Ai.Tlbx.Sip;
using Ai.Tlbx.VoiceAssistant.Sip;

namespace Ai.Tlbx.VoiceAssistant.Demo.Web.Services;

/// <summary>One browser circuit owns one outbound call. Secrets stay on the demo server.</summary>
public sealed class SipDemoSession(IConfiguration configuration) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private SipClientManager? _manager;
    private SipClient? _client;
    private bool _disposed;
    private string? ConfigFile => configuration["Sip:ConfigFile"] ?? Environment.GetEnvironmentVariable("SIP_CONFIG_FILE");
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConfigFile);
    public SipCall? Call { get; private set; }
    public SipAudioHardwareAccess? Hardware { get; private set; }
    public SipClientState ClientState { get; private set; } = SipClientState.Disconnected;
    public event Action? Changed;

    public static string ValidateNumber(string? value)
    {
        string number = value?.Trim() ?? "";
        if (number.Length is < 1 or > 64 || number.Any(c => !char.IsAsciiDigit(c) && c is not ('+' or '*' or '#')) ||
            number.Skip(1).Contains('+'))
            throw new ArgumentException("Enter a phone number or extension using digits, +, * or #.");
        return number;
    }

    public async Task PrepareAsync(string number, CancellationToken cancellationToken = default)
    {
        number = ValidateNumber(number);
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_manager is not null) throw new InvalidOperationException("End the current SIP session first.");
            Call = null;
            if (!IsConfigured) throw new InvalidOperationException("Set Sip:ConfigFile or SIP_CONFIG_FILE on the demo server.");
            SipClientConfig config;
            try
            {
                using var file = File.OpenRead(ConfigFile!);
                config = await JsonSerializer.DeserializeAsync<SipClientConfig>(file, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter(), new AddressConverter() }
                }, cancellationToken) ?? throw new JsonException();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { throw new InvalidOperationException("The server SIP configuration could not be read. Check its path, permissions and JSON."); }

            _manager = new SipClientManager();
            _client = _manager.CreateClient(config);
            _client.StatusChanged += OnClientStatus;
            await _client.ConnectAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Call = _client.CreateCall(number);
            Call.StatusChanged += OnCallStatus;
            Hardware = new SipAudioHardwareAccess(Call);
            Changed?.Invoke();
        }
        catch
        {
            await ReleaseAsync();
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public Task DialAsync(CancellationToken cancellationToken = default) =>
        (Call ?? throw new InvalidOperationException("Prepare a call first.")).StartAsync(cancellationToken);

    private void OnClientStatus(object? sender, SipClientStatus status)
    { ClientState = status.State; Changed?.Invoke(); }
    private void OnCallStatus(object? sender, SipCallStatus status) => Changed?.Invoke();

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try { await ReleaseAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task ReleaseAsync()
    {
        try
        {
            if (Call is not null) await Call.HangupAsync();
        }
        finally
        {
            if (Call is not null) Call.StatusChanged -= OnCallStatus;
            if (Hardware is not null) { await Hardware.DisposeAsync(); Hardware = null; }
            if (_client is not null) _client.StatusChanged -= OnClientStatus;
            if (_manager is not null) await _manager.DisposeAsync();
            _manager = null; _client = null; ClientState = SipClientState.Disconnected;
            Changed?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }

    private sealed class AddressConverter : JsonConverter<IPAddress>
    {
        public override IPAddress Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && IPAddress.TryParse(reader.GetString(), out var address)
                ? address : throw new JsonException("Expected an IP address string.");
        public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
