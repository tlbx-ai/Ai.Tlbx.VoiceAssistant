using System.Net.Http.Headers;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;

public static class OpenAiDirectRealtimeEndpointExtensions
{
    private const string ClientSecretsEndpoint = "https://api.openai.com/v1/realtime/client_secrets";

    public static IServiceCollection AddOpenAiDirectRealtimeVoice(
        this IServiceCollection services,
        Action<OpenAiDirectRealtimeOptions>? configure = null)
    {
        var options = new OpenAiDirectRealtimeOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<OpenAiDirectRealtimeSessionRegistry>();
        services.AddSingleton<IOpenAiDirectRealtimeClientActionDispatcher>(sp =>
            sp.GetRequiredService<OpenAiDirectRealtimeSessionRegistry>());

        return services;
    }

    public static IEndpointRouteBuilder MapOpenAiDirectRealtimeVoice(
        this IEndpointRouteBuilder endpoints,
        string? routePrefix = null)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<OpenAiDirectRealtimeOptions>();
        var prefix = NormalizePrefix(routePrefix ?? options.RoutePrefix);

        endpoints.MapMethods($"{prefix}/session", [HttpMethods.Post], (RequestDelegate)(async httpContext =>
        {
            var request = await JsonSerializer.DeserializeAsync(
                httpContext.Request.Body,
                OpenAiDirectRealtimeJsonContext.Default.OpenAiDirectRealtimeSessionRequest,
                httpContext.RequestAborted) ?? new OpenAiDirectRealtimeSessionRequest();

            var result = await CreateSessionAsync(
                httpContext,
                request,
                httpContext.RequestServices.GetRequiredService<IOpenAiDirectRealtimeSessionFactory>(),
                httpContext.RequestServices.GetRequiredService<OpenAiDirectRealtimeSessionRegistry>(),
                httpContext.RequestServices.GetRequiredService<OpenAiDirectRealtimeOptions>(),
                httpContext.RequestAborted);

            await result.ExecuteAsync(httpContext);
        }));

        endpoints.Map($"{prefix}/control/{{voiceSessionId}}", (RequestDelegate)(async httpContext =>
        {
            var voiceSessionId = Convert.ToString(httpContext.Request.RouteValues["voiceSessionId"], System.Globalization.CultureInfo.InvariantCulture) ?? "";
            await HandleControlSocketAsync(
                httpContext,
                voiceSessionId,
                httpContext.RequestServices.GetRequiredService<OpenAiDirectRealtimeSessionRegistry>(),
                httpContext.RequestServices.GetRequiredService<OpenAiDirectRealtimeOptions>());
        }));

        return endpoints;
    }

    private static async Task<IResult> CreateSessionAsync(
        HttpContext httpContext,
        OpenAiDirectRealtimeSessionRequest request,
        IOpenAiDirectRealtimeSessionFactory sessionFactory,
        OpenAiDirectRealtimeSessionRegistry registry,
        OpenAiDirectRealtimeOptions options,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized(httpContext, options))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var voiceSessionId = Guid.NewGuid().ToString("N");
        var spec = await sessionFactory.CreateSessionAsync(httpContext, request, voiceSessionId, cancellationToken);
        var expiresAt = DateTimeOffset.UtcNow.Add(options.SessionTtl);
        registry.Register(voiceSessionId, spec, expiresAt);

        try
        {
            var clientSecret = await CreateClientSecretAsync(spec, registry, options, cancellationToken);
            var response = new OpenAiDirectRealtimeSessionResponse
            {
                VoiceSessionId = voiceSessionId,
                ClientSecret = clientSecret,
                Model = spec.Settings.Model.ToApiString(),
                Voice = spec.Settings.Voice.ToString().ToLowerInvariant(),
                ControlUrl = $"{NormalizePrefix(options.RoutePrefix)}/control/{voiceSessionId}",
                Channel = spec.Channel
            };

            return Results.Json(response, OpenAiDirectRealtimeJsonContext.Default.OpenAiDirectRealtimeSessionResponse);
        }
        catch (OpenAiDirectRealtimeClientSecretException ex)
        {
            await registry.RemoveAsync(voiceSessionId, cancellationToken);
            var response = new DirectRealtimeSessionErrorResponse
            {
                Error = ex.Message,
                UpstreamStatus = (int)ex.StatusCode
            };

            return Results.Json(
                response,
                OpenAiDirectRealtimeJsonContext.Default.DirectRealtimeSessionErrorResponse,
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (InvalidOperationException ex)
        {
            await registry.RemoveAsync(voiceSessionId, cancellationToken);
            var response = new DirectRealtimeSessionErrorResponse
            {
                Error = ex.Message
            };

            return Results.Json(
                response,
                OpenAiDirectRealtimeJsonContext.Default.DirectRealtimeSessionErrorResponse,
                statusCode: StatusCodes.Status500InternalServerError);
        }
        catch
        {
            await registry.RemoveAsync(voiceSessionId, cancellationToken);
            throw;
        }
    }

    private static async Task HandleControlSocketAsync(
        HttpContext httpContext,
        string voiceSessionId,
        OpenAiDirectRealtimeSessionRegistry registry,
        OpenAiDirectRealtimeOptions options)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!IsAuthorized(httpContext, options))
        {
            httpContext.Response.StatusCode = httpContext.User?.Identity?.IsAuthenticated == true
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized;
            return;
        }

        if (!registry.TryGet(voiceSessionId, out var session))
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();
        session.ControlSocket = webSocket;
        await SendJsonAsync(webSocket, new DirectRealtimeStatusMessage { Status = "Connected" }, OpenAiDirectRealtimeJsonContext.Default.DirectRealtimeStatusMessage, httpContext.RequestAborted);

        try
        {
            var receiveBuffer = new byte[64 * 1024];
            while (webSocket.State == WebSocketState.Open && !httpContext.RequestAborted.IsCancellationRequested)
            {
                var message = await ReceiveTextMessageAsync(webSocket, receiveBuffer, httpContext.RequestAborted);
                if (message is null)
                {
                    break;
                }

                await HandleControlMessageAsync(
                    voiceSessionId,
                    session,
                    registry,
                    webSocket,
                    message,
                    httpContext.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await registry.RemoveAsync(voiceSessionId, CancellationToken.None);
        }
    }

    private static async Task HandleControlMessageAsync(
        string voiceSessionId,
        DirectRealtimeSessionState session,
        OpenAiDirectRealtimeSessionRegistry registry,
        WebSocket webSocket,
        string json,
        CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Deserialize(json, OpenAiDirectRealtimeJsonContext.Default.DirectRealtimeControlMessage);
        switch (message?.Type)
        {
            case "client_action_response":
                if (!string.IsNullOrWhiteSpace(message.RequestId))
                {
                    registry.HandleClientActionResponse(
                        voiceSessionId,
                        message.RequestId,
                        message.Result ?? CreateEmptyJsonObject(),
                        message.Error,
                        message.Declined ?? false);
                }
                break;

            case "tool_call":
                await HandleToolCallAsync(session, webSocket, message, cancellationToken);
                break;

            case "event":
                if (message.Event is not null && session.Spec.EventSink is not null)
                {
                    await session.Spec.EventSink.OnClientEventAsync(voiceSessionId, message.Event, cancellationToken);
                }
                break;

            case "stop":
                await registry.RemoveAsync(voiceSessionId, cancellationToken);
                break;

            default:
                await SendJsonAsync(
                    webSocket,
                    new DirectRealtimeErrorMessage { Message = $"Unknown control message type '{message?.Type}'." },
                    OpenAiDirectRealtimeJsonContext.Default.DirectRealtimeErrorMessage,
                    cancellationToken);
                break;
        }
    }

    private static async Task HandleToolCallAsync(
        DirectRealtimeSessionState session,
        WebSocket webSocket,
        DirectRealtimeControlMessage message,
        CancellationToken cancellationToken)
    {
        var requestId = message.RequestId ?? Guid.NewGuid().ToString("N")[..8];
        var toolName = message.Action ?? "";
        var tool = session.Spec.Settings.Tools.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, toolName, StringComparison.Ordinal));

        string result;
        if (tool is null)
        {
            result = $"Tool not found: {toolName}";
        }
        else
        {
            try
            {
                result = await tool.ExecuteAsync(message.Args?.GetRawText() ?? "{}");
            }
            catch (Exception ex)
            {
                result = $"Error: {ex.Message}";
            }
        }

        await SendJsonAsync(
            webSocket,
            new DirectRealtimeToolResultMessage
            {
                RequestId = requestId,
                Result = result
            },
            OpenAiDirectRealtimeJsonContext.Default.DirectRealtimeToolResultMessage,
            cancellationToken);
    }

    private static async Task<string> CreateClientSecretAsync(
        OpenAiDirectRealtimeSessionSpec spec,
        OpenAiDirectRealtimeSessionRegistry registry,
        OpenAiDirectRealtimeOptions options,
        CancellationToken cancellationToken)
    {
        var request = new DirectClientSecretRequest
        {
            ExpiresAfter = new ExpiresAfter
            {
                Anchor = "created_at",
                Seconds = Math.Max(60, (int)options.SessionTtl.TotalSeconds)
            },
            Session = registry.BuildSessionConfig(spec.Settings)
        };

        var requestJson = JsonSerializer.Serialize(request, OpenAiDirectRealtimeJsonContext.Default.DirectClientSecretRequest);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ClientSecretsEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", spec.OpenAiApiKey);
        if (!string.IsNullOrWhiteSpace(spec.SafetyIdentifier))
        {
            httpRequest.Headers.TryAddWithoutValidation("OpenAI-Safety-Identifier", spec.SafetyIdentifier);
        }

        httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var httpClient = new HttpClient();
        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            options.Log?.Invoke(LogLevel.Error, $"Failed to create OpenAI direct realtime client secret: {response.StatusCode} - {error}");
            throw new OpenAiDirectRealtimeClientSecretException(response.StatusCode, $"Failed to create OpenAI direct realtime client secret: {(int)response.StatusCode} {response.StatusCode} - {NormalizeError(error)}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var payload = JsonSerializer.Deserialize(json, OpenAiDirectRealtimeJsonContext.Default.DirectClientSecretResponse);
        if (string.IsNullOrWhiteSpace(payload?.Value))
        {
            throw new InvalidOperationException("OpenAI direct realtime client secret response did not include a value.");
        }

        return payload.Value;
    }

    private static bool IsAuthorized(HttpContext httpContext, OpenAiDirectRealtimeOptions options)
    {
        return options.AuthorizeRequest?.Invoke(httpContext)
            ?? httpContext.User.Identity?.IsAuthenticated == true;
    }

    private static string NormalizeError(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "OpenAI returned an empty error response.";
        }

        return error.Length <= 500 ? error : error[..500];
    }

    private static JsonElement CreateEmptyJsonObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static async Task<string?> ReceiveTextMessageAsync(
        WebSocket webSocket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await webSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static async Task SendJsonAsync<T>(
        WebSocket webSocket,
        T message,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(message, jsonTypeInfo);
        var bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static string NormalizePrefix(string routePrefix)
    {
        if (string.IsNullOrWhiteSpace(routePrefix))
        {
            return "";
        }

        var trimmed = routePrefix.Trim().TrimEnd('/');
        return trimmed.StartsWith("/", StringComparison.Ordinal) ? trimmed : "/" + trimmed;
    }
}

internal sealed class OpenAiDirectRealtimeClientSecretException : Exception
{
    public OpenAiDirectRealtimeClientSecretException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}
