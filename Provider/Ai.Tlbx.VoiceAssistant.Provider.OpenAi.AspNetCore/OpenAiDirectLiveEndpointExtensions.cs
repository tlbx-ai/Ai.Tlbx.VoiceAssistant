using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;

public sealed class OpenAiDirectLiveOptions
{
    public string RoutePrefix { get; set; } = "/api/voice/live";
    /// <summary>Defaults to authenticated callers. Application authorization is required before preparing sessions too.</summary>
    public Func<HttpContext, bool>? AuthorizeRequest { get; set; }
    public HttpClient? HttpClient { get; set; }
    public Action<LogLevel, string>? Log { get; set; }
}

/// <summary>Short-lived, single-use capabilities prepared by trusted application code. No client-supplied settings or tool definitions.</summary>
public sealed class OpenAiDirectLivePreparedSessionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    public string Prepare(Func<string, CancellationToken, Task<Models.OpenAiLiveWebRtcSession>> createSession)
    {
        ArgumentNullException.ThrowIfNull(createSession);
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            foreach (var expired in _entries.Where(e => e.Value.Expires <= DateTimeOffset.UtcNow).Select(e => e.Key).ToArray()) _entries.Remove(expired);
            _entries.Add(id, new Entry(createSession, DateTimeOffset.UtcNow.AddMinutes(2)));
        }
        return id;
    }
    public void Remove(string id) { lock (_gate) _entries.Remove(id); }
    internal bool TryConsume(string id, out Func<string, CancellationToken, Task<Models.OpenAiLiveWebRtcSession>> create)
    {
        lock (_gate)
        {
            if (_entries.Remove(id, out var entry) && entry.Expires > DateTimeOffset.UtcNow) { create = entry.Create; return true; }
        }
        create = null!;
        return false;
    }
    private sealed record Entry(Func<string, CancellationToken, Task<Models.OpenAiLiveWebRtcSession>> Create, DateTimeOffset Expires);
}

public static class OpenAiDirectLiveEndpointExtensions
{
    public static IServiceCollection AddOpenAiDirectLiveVoice(this IServiceCollection services, Action<OpenAiDirectLiveOptions>? configure = null)
    {
        var options = new OpenAiDirectLiveOptions();
        configure?.Invoke(options);
        options.RoutePrefix = "/" + options.RoutePrefix.Trim('/');
        services.AddSingleton(options);
        services.AddSingleton<OpenAiDirectLivePreparedSessionStore>();
        services.AddScoped<OpenAiDirectLiveVoiceProvider>();
        return services;
    }

    public static IEndpointRouteBuilder MapOpenAiDirectLiveVoice(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<OpenAiDirectLiveOptions>();
        endpoints.MapPost(options.RoutePrefix + "/session", (RequestDelegate)(async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!(options.AuthorizeRequest?.Invoke(context) ?? context.User.Identity?.IsAuthenticated == true))
            { context.Response.StatusCode = 403; return; }
            // JSON plus an exact same-origin check prevents ambient-cookie cross-site session creation.
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length != 0 && !string.Equals(origin, $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase))
            { context.Response.StatusCode = 403; return; }
            if (!context.Request.HasJsonContentType()) { context.Response.StatusCode = 415; return; }
            try
            {
                using var stream = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    if (stream.Length + count > 128 * 1024) { context.Response.StatusCode = 413; return; }
                    stream.Write(buffer, 0, count);
                }
                var body = JsonNode.Parse(stream.ToArray())?.AsObject();
                var token = body?["preparedSessionId"]?.GetValue<string>();
                var sdp = body?["sdp"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(sdp)) { context.Response.StatusCode = 400; return; }
                var store = context.RequestServices.GetRequiredService<OpenAiDirectLivePreparedSessionStore>();
                if (!store.TryConsume(token, out var create)) { context.Response.StatusCode = 410; return; }
                var session = await create(sdp, context.RequestAborted);
                context.Response.StatusCode = 201;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(new JsonObject {
                    ["session"] = new JsonObject { ["id"] = session.SessionId },
                    ["transport"] = new JsonObject { ["type"] = "webrtc", ["sdp"] = session.Sdp }
                }.ToJsonString(), context.RequestAborted);
            }
            catch (System.Text.Json.JsonException) { context.Response.StatusCode = 400; }
            catch (InvalidOperationException ex) { options.Log?.Invoke(LogLevel.Error, ex.Message); context.Response.StatusCode = 400; }
            catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
            {
                options.Log?.Invoke(LogLevel.Error, $"Live WebRTC creation failed: {ex.Message}");
                context.Response.StatusCode = 502; // Never expose upstream error bodies, API keys, or server settings.
            }
        }));
        return endpoints;
    }
}
