using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Microsoft.AspNetCore.Http;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;

public sealed class OpenAiDirectRealtimePreparedSessionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PreparedDirectRealtimeSession> _sessions = new(StringComparer.Ordinal);

    public string Prepare(OpenAiDirectRealtimePreparedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var id = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.Add(session.PreparationTtl ?? TimeSpan.FromMinutes(2));

        lock (_gate)
        {
            RemoveExpiredSessions();
            _sessions[id] = new PreparedDirectRealtimeSession(session, expiresAt);
        }

        return id;
    }

    public bool TryConsume(string id, out OpenAiDirectRealtimePreparedSession session)
    {
        lock (_gate)
        {
            RemoveExpiredSessions();
            if (!_sessions.Remove(id, out var prepared))
            {
                session = null!;
                return false;
            }

            session = prepared.Session;
            return true;
        }
    }

    private void RemoveExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _sessions.Where(entry => entry.Value.ExpiresAt <= now).ToList())
        {
            _sessions.Remove(entry.Key);
        }
    }

    private sealed record PreparedDirectRealtimeSession(
        OpenAiDirectRealtimePreparedSession Session,
        DateTimeOffset ExpiresAt);
}

public sealed class OpenAiDirectRealtimePreparedSession
{
    public required string OpenAiApiKey { get; init; }

    public required OpenAiVoiceSettings Settings { get; init; }

    public string? SafetyIdentifier { get; init; }

    public string Channel { get; init; } = "voice";

    public IOpenAiDirectRealtimeEventSink? EventSink { get; init; }

    public TimeSpan? PreparationTtl { get; init; }
}

public sealed class PreparedOpenAiDirectRealtimeSessionFactory : IOpenAiDirectRealtimeSessionFactory
{
    private readonly OpenAiDirectRealtimePreparedSessionStore _preparedSessions;

    public PreparedOpenAiDirectRealtimeSessionFactory(OpenAiDirectRealtimePreparedSessionStore preparedSessions)
    {
        _preparedSessions = preparedSessions;
    }

    public Task<OpenAiDirectRealtimeSessionSpec> CreateSessionAsync(
        HttpContext httpContext,
        OpenAiDirectRealtimeSessionRequest request,
        string voiceSessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PreparedSessionId) ||
            !_preparedSessions.TryConsume(request.PreparedSessionId, out var prepared))
        {
            throw new OpenAiDirectRealtimePreparedSessionException("The direct realtime session was not prepared or has expired.");
        }

        return Task.FromResult(new OpenAiDirectRealtimeSessionSpec
        {
            OpenAiApiKey = prepared.OpenAiApiKey,
            Settings = prepared.Settings,
            SafetyIdentifier = prepared.SafetyIdentifier,
            Channel = prepared.Channel,
            EventSink = prepared.EventSink
        });
    }
}

internal sealed class OpenAiDirectRealtimePreparedSessionException : Exception
{
    public OpenAiDirectRealtimePreparedSessionException(string message)
        : base(message)
    {
    }
}
