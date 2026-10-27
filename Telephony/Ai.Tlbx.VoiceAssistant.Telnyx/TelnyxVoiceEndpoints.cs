using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Ai.Tlbx.VoiceAssistant.Configuration;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Ai.Tlbx.VoiceAssistant.Telnyx;

public sealed record TelnyxWebhook(string Id, string EventType, JsonElement Payload, JsonElement NativeEvent)
{
    public string? CallControlId => Payload.TryGetProperty("call_control_id", out var id) ? id.GetString() : null;
    public static TelnyxWebhook Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement; var data = root.GetProperty("data");
        return new(data.GetProperty("id").GetString() ?? throw new JsonException("Missing webhook ID."),
            data.GetProperty("event_type").GetString() ?? throw new JsonException("Missing event type."),
            data.GetProperty("payload").Clone(), root.Clone());
    }
}

/// <summary>Native telephone metadata and an isolated DI scope/audio device for this call.</summary>
public sealed class TelnyxCall(JsonElement nativeStart, TelnyxAudioHardware audio, IServiceProvider services)
{
    public JsonElement NativeStart { get; } = nativeStart;
    public TelnyxAudioHardware Audio { get; } = audio;
    public IServiceProvider Services { get; } = services;
    public string CallControlId => Audio.CallControlId!;
    public string? From => Field("from");
    public string? To => Field("to");
    private string? Field(string name) => NativeStart.GetProperty("start").TryGetProperty(name, out var value) ? value.GetString() : null;

    /// <summary>Run the scoped, already configured VoiceAssistant until this telephone connection ends.</summary>
    public async Task RunAssistantAsync(IVoiceSettings settings, CancellationToken cancellationToken)
    {
        var assistant = Services.GetRequiredService<global::Ai.Tlbx.VoiceAssistant.VoiceAssistant>();
        try
        {
            await assistant.StartAsync(settings, cancellationToken).WaitAsync(Services.GetRequiredService<TelnyxOptions>().CallSetupTimeout, cancellationToken);
            await Audio.Completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { await assistant.StopAsync(); }
    }
}

public static class TelnyxVoiceExtensions
{
    /// <summary>Register server credentials, REST client, webhook worker and one audio device per request/call.</summary>
    public static IServiceCollection AddTelnyxVoice(this IServiceCollection services, Action<TelnyxOptions> configure)
    {
        var options = new TelnyxOptions(); configure(options); options.ValidateHost();
        services.AddSingleton(options);
        services.AddHttpClient<TelnyxClient>(http => http.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<TelnyxSignatureVerifier>();
        services.AddScoped<TelnyxAudioHardware>();
        services.AddScoped<IAudioHardwareAccess>(sp => sp.GetRequiredService<TelnyxAudioHardware>());
        services.TryAddSingleton<TelnyxVoiceHost>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TelnyxVoiceHost>());
        return services;
    }

    /// <summary>Use telephone audio with the existing provider/tool registrations.</summary>
    public static VoiceAssistantBuilder WithTelnyx(this VoiceAssistantBuilder builder, Action<TelnyxOptions> configure)
    { builder.Services.AddTelnyxVoice(configure); return builder; }

    /// <summary>Map signed public webhook/media endpoints. Does not expose an unauthenticated outbound dialing endpoint.</summary>
    public static IEndpointRouteBuilder MapTelnyxVoice(this IEndpointRouteBuilder endpoints, Func<TelnyxCall, CancellationToken, Task> handleCall)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<TelnyxOptions>();
        string prefix = options.RoutePrefix.TrimEnd('/');
        endpoints.MapPost(prefix + "/webhook", (RequestDelegate)(async context =>
        {
            if (context.Request.ContentLength > options.MaxWebSocketMessageBytes) { context.Response.StatusCode = 413; return; }
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, false, 4096, true);
            char[] buffer = new char[4096]; var body = new StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), context.RequestAborted)) > 0)
            {
                if (body.Length + count > options.MaxWebSocketMessageBytes) { context.Response.StatusCode = 413; return; }
                body.Append(buffer, 0, count);
            }
            if (!Verify(context, body.ToString())) { context.Response.StatusCode = 401; return; }
            try
            {
                var webhook = TelnyxWebhook.Parse(body.ToString());
                var host = context.RequestServices.GetRequiredService<TelnyxVoiceHost>();
                context.Response.StatusCode = host.TryQueue(webhook) ? 200 : 503;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            { context.Response.StatusCode = 400; }
        }));

        endpoints.MapGet(prefix + "/media", (RequestDelegate)(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            // Verify the configured external URL, independent of proxy Host/forwarded-header rewriting.
            if (!Verify(context, options.MediaUri.AbsoluteUri)) { context.Response.StatusCode = 401; return; }
            var host = context.RequestServices.GetRequiredService<TelnyxVoiceHost>();
            if (!host.TryReserve()) { context.Response.StatusCode = 503; return; }
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var audio = context.RequestServices.GetRequiredService<TelnyxAudioHardware>();
            Task transport = Task.CompletedTask;
            Task? application = null;
            string? callId = null;
            bool registered = false;
            WebSocket? socket = null;
            try
            {
                socket = await context.WebSockets.AcceptWebSocketAsync();
                transport = audio.RunAsync(socket, lifetime.Token);
                var start = await audio.Started.WaitAsync(options.MediaStartTimeout, lifetime.Token);
                callId = audio.CallControlId!;
                registered = host.TryRegister(callId, lifetime);
                if (!registered) throw new InvalidOperationException("A media connection for this call already exists.");
                var call = new TelnyxCall(start, audio, context.RequestServices);
                application = handleCall(call, lifetime.Token);
                var ended = await Task.WhenAny(application, transport);
                if (ended == application) await application;
                else await transport;
                lifetime.Cancel();
                await application.WaitAsync(options.CallSetupTimeout);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception ex)
            {
                host.ReportError(ex);
            }
            finally
            {
                lifetime.Cancel(); await audio.DisposeAsync();
                try { await transport; } catch (Exception ex) { host.ReportError(ex); }
                if (application is not null)
                {
                    try { await application.WaitAsync(options.CallSetupTimeout); }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                    catch (Exception ex) { host.ReportError(ex); }
                }
                socket?.Dispose();
                if (registered && !audio.RemoteStopped && !host.HasEnded(callId!))
                {
                    using var hangup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await context.RequestServices.GetRequiredService<TelnyxClient>().HangupAsync(callId!, hangup.Token); }
                    catch (Exception hangupError) { host.ReportError(hangupError); }
                }
                host.Release(callId, lifetime);
            }
        }));
        return endpoints;
    }

    private static bool Verify(HttpContext context, string text) => context.RequestServices.GetRequiredService<TelnyxSignatureVerifier>().Verify(
        context.Request.Headers["telnyx-timestamp"].ToString(), context.Request.Headers["telnyx-signature-ed25519"].ToString(), text);
}

/// <summary>Bounded in-process call lifecycle. Webhooks are authenticated and queued before HTTP 200 is returned.</summary>
public sealed class TelnyxVoiceHost(TelnyxOptions options, TelnyxClient client, Action<LogLevel, string>? logAction = null) : BackgroundService
{
    private readonly Channel<TelnyxWebhook> _events = Channel.CreateBounded<TelnyxWebhook>(256);
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _ended = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _reserved;
    public Action<TelnyxWebhook>? OnWebhook { get; set; }
    public Action<Exception>? OnError { get; set; }
    public int ActiveCallCount => _active.Count;

    internal bool TryQueue(TelnyxWebhook webhook)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _seen.Where(pair => now - pair.Value > TimeSpan.FromMinutes(10)).Select(pair => pair.Key).ToArray()) _seen.Remove(key);
            foreach (var key in _ended.Where(pair => now - pair.Value > TimeSpan.FromMinutes(10)).Select(pair => pair.Key).ToArray()) _ended.Remove(key);
            if (_seen.ContainsKey(webhook.Id)) return true;
            if (_seen.Count >= 10000 || !_events.Writer.TryWrite(webhook)) return false;
            _seen[webhook.Id] = now; return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var webhook in _events.Reader.ReadAllAsync(stoppingToken))
            {
                try { await ProcessAsync(webhook, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { ReportError(ex); }
            }
        }
        finally { foreach (var pair in _active) pair.Value.Cancel(); }
    }

    /// <summary>Process a verified native webhook. Can also be called by an application's durable queue consumer.</summary>
    public async Task ProcessAsync(TelnyxWebhook webhook, CancellationToken cancellationToken = default)
    {
        OnWebhook?.Invoke(webhook);
        if (webhook.CallControlId is not string callId) return;
        if (webhook.EventType == "call.hangup")
        {
            lock (_gate)
            {
                if (_ended.Count >= 10000) _ended.Remove(_ended.MinBy(pair => pair.Value).Key);
                _ended[callId] = DateTimeOffset.UtcNow;
            }
            if (_active.TryGetValue(callId, out var active)) active.Cancel(); return;
        }
        if (webhook.EventType != "call.initiated" || !webhook.Payload.TryGetProperty("direction", out var direction) || direction.GetString() != "incoming") return;
        if (options.ConnectionId is not null && (!webhook.Payload.TryGetProperty("connection_id", out var connection) || connection.GetString() != options.ConnectionId)) return;
        if (options.FromNumber is not null && (!webhook.Payload.TryGetProperty("to", out var to) || to.GetString() != options.FromNumber)) return;
        if (HasEnded(callId)) return;
        var body = options.Stream.ToRequest(options.MediaUri);
        body["command_id"] = webhook.Id;
        await client.AnswerAsync(callId, body, cancellationToken);
    }

    internal bool TryReserve()
    { if (Interlocked.Increment(ref _reserved) <= options.MaxConcurrentCalls) return true; Interlocked.Decrement(ref _reserved); return false; }
    internal bool TryRegister(string id, CancellationTokenSource lifetime) => _active.TryAdd(id, lifetime);
    internal bool HasEnded(string id) { lock (_gate) return _ended.ContainsKey(id); }
    internal void Release(string? id, CancellationTokenSource lifetime)
    {
        if (id is not null) ((ICollection<KeyValuePair<string, CancellationTokenSource>>)_active).Remove(new(id, lifetime));
        Interlocked.Decrement(ref _reserved);
    }
    internal void ReportError(Exception ex)
    {
        logAction?.Invoke(LogLevel.Error, "Telnyx call operation failed: " + ex.GetType().Name);
        OnError?.Invoke(ex);
    }
}
