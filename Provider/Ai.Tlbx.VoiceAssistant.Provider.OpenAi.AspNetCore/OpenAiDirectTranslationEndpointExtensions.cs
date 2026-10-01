using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;

public sealed class OpenAiDirectTranslationOptions
{
    public string RoutePrefix { get; set; } = "/api/voice/translation";
    public Func<HttpContext, bool>? AuthorizeRequest { get; set; }
    public HttpClient? HttpClient { get; set; }
    public Action<LogLevel, string>? Log { get; set; }
}
public sealed class OpenAiDirectTranslationPreparedSessionStore
{
    private readonly OpenAiDirectLivePreparedSessionStore _capabilities = new();
    public string Prepare(Func<string, CancellationToken, Task<OpenAiTranslationWebRtcSession>> create) =>
        _capabilities.Prepare(async (sdp, ct) => { var session = await create(sdp, ct); return new(session.SessionId, session.Sdp); });
    public void Remove(string id) => _capabilities.Remove(id);
    internal bool TryConsume(string id, out Func<string, CancellationToken, Task<OpenAiLiveWebRtcSession>> create) => _capabilities.TryConsume(id, out create);
}
public static class OpenAiDirectTranslationEndpointExtensions
{
    public static IServiceCollection AddOpenAiDirectTranslation(this IServiceCollection services, Action<OpenAiDirectTranslationOptions>? configure = null)
    {
        var options = new OpenAiDirectTranslationOptions(); configure?.Invoke(options);
        options.RoutePrefix = "/" + options.RoutePrefix.Trim('/');
        services.AddSingleton(options); services.AddSingleton<OpenAiDirectTranslationPreparedSessionStore>();
        services.AddScoped<OpenAiDirectTranslationVoiceProvider>();
        return services;
    }
    public static IEndpointRouteBuilder MapOpenAiDirectTranslation(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<OpenAiDirectTranslationOptions>();
        endpoints.MapPost(options.RoutePrefix + "/session", (RequestDelegate)(async context => {
            context.Response.Headers.CacheControl = "no-store";
            if (!(options.AuthorizeRequest?.Invoke(context) ?? context.User.Identity?.IsAuthenticated == true)) { context.Response.StatusCode = 403; return; }
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length != 0 && !string.Equals(origin, $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 403; return; }
            if (!context.Request.HasJsonContentType()) { context.Response.StatusCode = 415; return; }
            try
            {
                using var body = new MemoryStream(); var buffer = new byte[8192]; int count;
                while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
                { if (body.Length + count > 128 * 1024) { context.Response.StatusCode = 413; return; } body.Write(buffer, 0, count); }
                var input = JsonNode.Parse(body.ToArray());
                if (input is not JsonObject obj || obj["preparedSessionId"] is not JsonValue tokenValue || !tokenValue.TryGetValue<string>(out _) ||
                    obj["sdp"] is not JsonValue sdpValue || !sdpValue.TryGetValue<string>(out _)) { context.Response.StatusCode = 400; return; }
                var token = input?["preparedSessionId"]?.GetValue<string>(); var sdp = input?["sdp"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(sdp)) { context.Response.StatusCode = 400; return; }
                var store = context.RequestServices.GetRequiredService<OpenAiDirectTranslationPreparedSessionStore>();
                if (!store.TryConsume(token, out var create)) { context.Response.StatusCode = 410; return; }
                var result = await create(sdp, context.RequestAborted);
                context.Response.StatusCode = 201; context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(new JsonObject { ["session"] = new JsonObject { ["id"] = result.SessionId },
                    ["transport"] = new JsonObject { ["sdp"] = result.Sdp, ["type"] = "webrtc" } }.ToJsonString(), context.RequestAborted);
            }
            catch (System.Text.Json.JsonException) { context.Response.StatusCode = 400; }
            catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested) { options.Log?.Invoke(LogLevel.Error, ex.Message); context.Response.StatusCode = 502; }
        }));
        return endpoints;
    }
}
