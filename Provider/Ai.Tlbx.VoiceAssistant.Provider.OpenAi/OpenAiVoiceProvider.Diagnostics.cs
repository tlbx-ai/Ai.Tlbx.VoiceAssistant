using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi;

public sealed partial class OpenAiVoiceProvider
{
    private long _snapshotRevision;
    private OpenAiRealtimeSessionSnapshot? _requestedSessionSnapshot;
    private OpenAiRealtimeSessionSnapshot? _sentSessionSnapshot;
    private OpenAiRealtimeSessionSnapshot? _serverSessionSnapshot;

    public OpenAiRealtimeSessionSnapshot? RequestedSessionSnapshot => Volatile.Read(ref _requestedSessionSnapshot);
    public OpenAiRealtimeSessionSnapshot? SentSessionSnapshot => Volatile.Read(ref _sentSessionSnapshot);
    public OpenAiRealtimeSessionSnapshot? ServerSessionSnapshot => Volatile.Read(ref _serverSessionSnapshot);
    public Action<OpenAiRealtimeSessionSnapshot>? OnSessionSnapshot { get; set; }
    public Action<OpenAiRealtimeProtocolEvent>? OnProtocolEvent { get; set; }

    private void ResetSessionDiagnostics()
    {
        Volatile.Write(ref _requestedSessionSnapshot, null);
        Volatile.Write(ref _sentSessionSnapshot, null);
        Volatile.Write(ref _serverSessionSnapshot, null);
    }

    private OpenAiRealtimeSessionSnapshot RecordRequestedSession(string json, string transport, OpenAiInstructionsSnapshot? instructions = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var snapshot = new OpenAiRealtimeSessionSnapshot(Interlocked.Increment(ref _snapshotRevision), null,
            DateTimeOffset.UtcNow, OpenAiSessionSnapshotStage.Requested, transport, DiagnosticString(root, "event_id"),
            SanitizeSessionJson(root.GetProperty("session")), instructions);
        Volatile.Write(ref _requestedSessionSnapshot, snapshot);
        NotifySnapshot(snapshot);
        return snapshot;
    }

    private void RecordSentSession(OpenAiRealtimeSessionSnapshot requested)
    {
        var snapshot = requested with { LocalRevision = Interlocked.Increment(ref _snapshotRevision),
            RequestedRevision = requested.LocalRevision, Timestamp = DateTimeOffset.UtcNow, Stage = OpenAiSessionSnapshotStage.Sent };
        Volatile.Write(ref _sentSessionSnapshot, snapshot);
        NotifySnapshot(snapshot);
    }

    private void RecordServerSession(JsonElement root, string transport)
    {
        if (!root.TryGetProperty("session", out var session) || session.ValueKind != JsonValueKind.Object) return;
        var snapshot = new OpenAiRealtimeSessionSnapshot(Interlocked.Increment(ref _snapshotRevision), null,
            DateTimeOffset.UtcNow, OpenAiSessionSnapshotStage.ServerReturned, transport,
            DiagnosticString(root, "event_id"), SanitizeSessionJson(session), null);
        Volatile.Write(ref _serverSessionSnapshot, snapshot);
        NotifySnapshot(snapshot);
    }

    private void NotifySnapshot(OpenAiRealtimeSessionSnapshot snapshot)
    {
        try { OnSessionSnapshot?.Invoke(snapshot); }
        catch { _logAction(LogLevel.Warn, "Session snapshot observer failed."); }
    }

    private void ObserveProtocol(string json, OpenAiRealtimeProtocolDirection direction, string? createReason = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = DiagnosticString(root, "type") ?? "unknown";
        if (direction == OpenAiRealtimeProtocolDirection.Received && (type == "session.created" || type == "session.updated"))
            RecordServerSession(root, "websocket");
        if (OnProtocolEvent is null) return;
        var response = root.TryGetProperty("response", out var r) ? r : default;
        var item = root.TryGetProperty("item", out var i) ? i : default;
        var error = root.TryGetProperty("error", out var e) ? e : default;
        var entry = new OpenAiRealtimeProtocolEvent(DateTimeOffset.UtcNow, direction, type,
            DiagnosticString(root, "event_id"), DiagnosticString(root, "response_id") ?? DiagnosticString(response, "id"),
            DiagnosticString(root, "item_id") ?? DiagnosticString(item, "id"), DiagnosticString(root, "call_id") ?? DiagnosticString(item, "call_id"),
            DiagnosticString(error, "code"), DiagnosticString(error, "event_id"),
            direction == OpenAiRealtimeProtocolDirection.Sent ? "library" : "unobserved",
            type == "response.create" ? createReason : null, json.Length,
            _settings?.IncludeProtocolContent == true && !type.Contains("audio.delta", StringComparison.Ordinal)
                && type != "input_audio_buffer.append" ? SanitizeDiagnosticJson(root) : null);
        try { OnProtocolEvent?.Invoke(entry); }
        catch { _logAction(LogLevel.Warn, "Protocol diagnostic observer failed."); }
    }

    private static string? DiagnosticString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string SanitizeSessionJson(JsonElement element)
    {
        // Redact transport credentials only at the session boundary. Tool schemas may
        // legitimately have fields called audio or api_key and must remain exact.
        var session = JsonNode.Parse(element.GetRawText())!.AsObject();
        session.Remove("client_secret");
        session.Remove("api_key");
        session.Remove("authorization");
        return session.ToJsonString();
    }

    private static string SanitizeDiagnosticJson(JsonElement element)
    {
        var node = JsonNode.Parse(element.GetRawText());
        RemovePrivateTransportFields(node);
        return node?.ToJsonString() ?? "null";
    }

    private static void RemovePrivateTransportFields(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "client_secret", "api_key", "authorization", "audio" })
            {
                // Session audio configuration is useful metadata; only audio data strings are removed.
                if (key != "audio" || obj[key] is JsonValue) obj.Remove(key);
            }
            foreach (var pair in obj) RemovePrivateTransportFields(pair.Value);
        }
        else if (node is JsonArray array) foreach (var child in array) RemovePrivateTransportFields(child);
    }
}
