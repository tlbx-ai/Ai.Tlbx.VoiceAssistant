using System;
using System.Threading;
using System.Threading.Tasks;
using Ai.Tlbx.VoiceAssistant.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi;

public sealed partial class OpenAiVoiceProvider
{
    private bool _sessionConfigured;
    private int _pendingSessionConfigurations;
    private bool _conversationStartAttempted;
    private bool _conversationStartSendStarted;
    private bool _conversationHasResponse;
    private bool _serverResponsePending;
    private string? _conversationStartRequestId;
    private string? _conversationStartResponseId;
    private CancellationTokenSource _conversationStartCancellation = new();

    /// <inheritdoc />
    public bool IsConversationStartReady
    {
        get { lock (_responseGate) return CanStartConversation(); }
    }

    private bool CanStartConversation() => IsConnected && _cts?.IsCancellationRequested == false &&
        _sessionConfigured && !_conversationStartAttempted && !_conversationHasResponse &&
        !_hasActiveResponse && !_responseRequested && !_serverResponsePending && !_userSpeaking &&
        _pendingToolBatches == 0 && !_toolExecutionUncertain;

    // Called under _responseGate when incoming speech or server VAD owns the turn.
    private void InvalidateUnsentConversationStart()
    {
        if (_pendingCreateEventId == null || _pendingCreateEventId != _conversationStartRequestId || _conversationStartSendStarted) return;
        _turnGeneration++;
        _hasActiveResponse = false;
        _pendingCreateEventId = null;
    }

    private void ResetConversationStart()
    {
        _conversationStartCancellation.Cancel();
        _conversationStartCancellation.Dispose();
        _conversationStartCancellation = new CancellationTokenSource();
        _sessionConfigured = false;
        _pendingSessionConfigurations = 0;
        _conversationStartAttempted = false;
        _conversationStartSendStarted = false;
        _conversationHasResponse = false;
        _serverResponsePending = false;
        _conversationStartRequestId = null;
        _conversationStartResponseId = null;
    }

    /// <inheritdoc />
    public async Task<string> StartConversationAsync(CancellationToken cancellationToken = default)
    {
        long session, turn;
        string eventId;
        CancellationTokenSource lifetime;
        lock (_responseGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanStartConversation())
            {
                _logAction(LogLevel.Info, $"conversation_start rejected session={_sessionGeneration}: not ready, busy, or already started.");
                throw new InvalidOperationException("Conversation start requires a configured, idle session with no previous response or start attempt.");
            }
            // Reserve before awaiting the send lock or response.created. Never queue a greeting behind another turn.
            _conversationStartAttempted = true;
            _hasActiveResponse = true;
            _currentResponseId = "";
            session = _sessionGeneration;
            turn = _turnGeneration;
            eventId = _pendingCreateEventId = _conversationStartRequestId = "create_" + Guid.NewGuid().ToString("N");
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _conversationStartCancellation.Token);
        }
        using (lifetime)
            await SendReservedResponseAsync(session, turn, eventId, "conversation_start", lifetime.Token).ConfigureAwait(false);
        return eventId;
    }

    private async Task SendReservedResponseAsync(long session, long turn, string eventId, string reason,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Correlate the opening without a fictional user message or an instructions override.
            var metadata = reason == "conversation_start"
                ? ",\"response\":{\"metadata\":{\"conversation_start_request_id\":\"" + eventId + "\"}}" : "";
            var json = "{\"type\":\"response.create\",\"event_id\":\"" + eventId + "\"" + metadata + "}";
            _logAction(LogLevel.Info, $"{reason} requested session={session} request={eventId}");
            await SendMessageAsync(json, cancellationToken, session, reason, turn).ConfigureAwait(false);
        }
        catch
        {
            lock (_responseGate)
            {
                if (session == _sessionGeneration && _pendingCreateEventId == eventId)
                { _hasActiveResponse = false; _pendingCreateEventId = null; }
            }
            _logAction(LogLevel.Warn, $"{reason} aborted_or_uncertain session={session} request={eventId}; no automatic retry.");
            throw;
        }
    }
}
