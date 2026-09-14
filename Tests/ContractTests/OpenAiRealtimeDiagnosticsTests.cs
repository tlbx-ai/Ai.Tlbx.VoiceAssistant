using System.Reflection;
using System.Text.Json;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

internal static class OpenAiRealtimeDiagnosticsTests
{
    public static async Task RunAsync()
    {
        await using var provider = new OpenAiVoiceProvider("test-key");
        var settings = new OpenAiVoiceSettings { Instructions = "  GrÃ¼ÃŸe\r\n\nÎ©  ", AppendToolCallPreambleInstructions = false };
        typeof(OpenAiVoiceProvider).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(provider, settings);
        var composed = OpenAiInstructionsComposer.Compose(settings);
        Check(composed.FinalText == settings.Instructions && composed.LibraryPreamble is null, "Exact application prompt");
        settings.AppendToolCallPreambleInstructions = true;
        settings.ToolCallPreambleInstructionsOverride = "Werkzeuge still ausfÃ¼hren.";
        var augmented = OpenAiInstructionsComposer.Compose(settings);
        Check(OpenAiInstructionsComposer.Compose(settings) == augmented, "Repeated composition is deterministic");
        Check(augmented.ApplicationText == composed.FinalText, "Composition does not mutate settings");

        var requested = (OpenAiRealtimeSessionSnapshot)Call(provider, "RecordRequestedSession",
            "{\"event_id\":\"a\",\"session\":{\"instructions\":\"first\",\"tools\":[{\"name\":\"probe\",\"parameters\":{\"type\":\"object\"}}]}}", "websocket", null)!;
        settings.Instructions = "mutated";
        Check(provider.SentSessionSnapshot is null, "Unsent or failed request never becomes sent");
        var second = (OpenAiRealtimeSessionSnapshot)Call(provider, "RecordRequestedSession",
            "{\"event_id\":\"b\",\"session\":{\"instructions\":\"second\"}}", "websocket", null)!;
        Call(provider, "RecordSentSession", requested);
        Check(provider.RequestedSessionSnapshot == second && provider.SentSessionSnapshot!.RequestedRevision == requested.LocalRevision,
            "Rapid requests retain the actual sent request association");
        Check(requested.SessionJson.Contains("first") && requested.SessionJson.Contains("parameters") && !requested.SessionJson.Contains("mutated"),
            "Immutable wire snapshot includes translated tool schemas");
        Call(provider, "ObserveProtocol", "{\"type\":\"session.updated\",\"event_id\":\"server-a\",\"session\":{\"id\":\"s\"}}", OpenAiRealtimeProtocolDirection.Received, null);
        Check(provider.ServerSessionSnapshot!.RequestedRevision is null && !provider.ServerSessionSnapshot.SessionJson.Contains("instructions"),
            "Missing server fields are not fabricated and updates are not falsely correlated");

        OpenAiRealtimeProtocolEvent? observed = null;
        provider.OnProtocolEvent = entry => observed = entry;
        Call(provider, "ObserveProtocol", "{\"type\":\"error\",\"event_id\":\"e\",\"error\":{\"code\":\"response_cancel_not_active\",\"event_id\":\"cancel-1\",\"message\":\"private\"}}", OpenAiRealtimeProtocolDirection.Received, null);
        Check(observed!.ErrorCode == "response_cancel_not_active" && observed.ReferencedEventId == "cancel-1" && observed.ContentJson is null,
            "Default trace records structured error correlation without contents");
        Check(observed.Origin == "unobserved", "Server origin is not guessed");
        settings.IncludeProtocolContent = true;
        Call(provider, "ObserveProtocol", "{\"type\":\"session.updated\",\"session\":{\"instructions\":\"visible by opt-in\",\"client_secret\":{\"value\":\"secret\"},\"audio\":{\"input\":{\"format\":\"pcm16\"}}}}", OpenAiRealtimeProtocolDirection.Received, null);
        Check(observed!.ContentJson!.Contains("visible by opt-in") && !observed.ContentJson.Contains("secret") && observed.ContentJson.Contains("pcm16"), "Opt-in content excludes secrets but retains audio configuration");
        Call(provider, "ObserveProtocol", "{\"type\":\"response.output_audio.delta\",\"delta\":\"private-audio\"}", OpenAiRealtimeProtocolDirection.Received, null);
        Check(observed!.ContentJson is null, "Audio excluded even after opt-in");
        provider.OnProtocolEvent = _ => throw new InvalidOperationException("observer");
        provider.OnSessionSnapshot = _ => throw new InvalidOperationException("observer");
        Call(provider, "ObserveProtocol", "{\"type\":\"session.updated\",\"session\":{\"id\":\"safe\"}}", OpenAiRealtimeProtocolDirection.Received, null);
        Check(provider.ServerSessionSnapshot!.SessionJson.Contains("safe"), "Observer failure does not interrupt protocol");
        Call(provider, "ResetSessionDiagnostics");
        Check(provider.RequestedSessionSnapshot is null && provider.SentSessionSnapshot is null && provider.ServerSessionSnapshot is null,
            "Reconnect clears previous session observations");

        var handler = new SessionHandler();
        using var http = new HttpClient(handler);
        await using var httpProvider = new OpenAiVoiceProvider("fake-key", httpClient: http);
        var httpSettings = new OpenAiVoiceSettings { Instructions = "  HTTP Grüße\r\n", AppendToolCallPreambleInstructions = false };
        typeof(OpenAiVoiceProvider).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(httpProvider, httpSettings);
        var key = await (Task<string>)Call(httpProvider, "CreateSessionAsync")!;
        using var sent = JsonDocument.Parse(handler.Body!);
        Check(sent.RootElement.GetProperty("session").GetProperty("instructions").GetString() == httpSettings.Instructions,
            "Client-secret wire preserves exact instructions");
        Check(key == "ephemeral-test" && httpProvider.SentSessionSnapshot!.Instructions!.FinalText == httpSettings.Instructions,
            "Client-secret sent snapshot captures actual prompt provenance");
        Check(!httpProvider.ServerSessionSnapshot!.SessionJson.Contains("ephemeral-test"), "Client secret is excluded from snapshot");
        var successful = httpProvider.SentSessionSnapshot;
        handler.FailTransport = true;
        try { await (Task<string>)Call(httpProvider, "CreateSessionAsync")!; throw new InvalidOperationException("Expected transport failure"); }
        catch (HttpRequestException) { }
        Check(httpProvider.SentSessionSnapshot == successful && httpProvider.RequestedSessionSnapshot!.LocalRevision > successful!.LocalRevision,
            "Actual HTTP transport failure retains earlier sent state and newer requested state");

    }

    private sealed class SessionHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public bool FailTransport { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsStringAsync(token);
            if (FailTransport) throw new HttpRequestException("Simulated transport failure");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":\"ephemeral-test\",\"session\":{\"id\":\"http-session\",\"client_secret\":{\"value\":\"ephemeral-test\"}}}")
            };
        }
    }

    private static object? Call(OpenAiVoiceProvider provider, string method, params object?[] args) =>
        typeof(OpenAiVoiceProvider).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(provider, args);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
