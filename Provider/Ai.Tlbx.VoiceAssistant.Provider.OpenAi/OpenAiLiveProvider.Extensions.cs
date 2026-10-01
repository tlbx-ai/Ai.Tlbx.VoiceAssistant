using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi;

public sealed partial class OpenAiLiveProvider
{
    /// <summary>Application-owned client delegation receives the complete image item here.
    /// The application sends it to its backend, then returns concise findings to Live.</summary>
    public Action<JsonObject>? OnClientVisualInput { get; set; }
    private readonly System.Collections.Generic.Queue<JsonObject> _visualInputs = new();
    private int _queuedVisualBytes;

    private static void ValidateSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("Invalid Live session ID.", nameof(sessionId));
    }

    private static string ForkSuffix(OpenAiLiveSettings settings)
    {
        if (settings.ForkFromSessionId == null) return "";
        ValidateSessionId(settings.ForkFromSessionId);
        return $"/{settings.ForkFromSessionId}/fork";
    }

    private JsonObject BuildStartup(OpenAiLiveSettings settings, bool webRtc)
    {
        if (settings.ForkFromSessionId == null) return (JsonObject)_startup!.DeepClone();
        if (settings.InitialHistory.Count != 0 || _history.Count != 0)
            throw new ArgumentException("Forks inherit source history. Append current task state after startup.");
        var session = new JsonObject { ["store"] = settings.Store };
        if (!webRtc) session["audio"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "audio/pcm", ["rate"] = 24000 } };
        if (settings.Responses != null) session["delegation"] = BuildDelegation(settings);
        return session;
    }

    /// <summary>Streams a finalized stored recording into an application-owned destination.
    /// Requires storage access in the source project. Never buffers the entire recording.</summary>
    public async Task DownloadRecordingAsync(string sessionId, Stream destination, OpenAiLiveSettings? settings = null,
        HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Destination must be writable.", nameof(destination));
        var options = settings ?? _settings ?? new OpenAiLiveSettings();
        using var owned = httpClient == null ? new HttpClient() : null;
        using var request = new HttpRequestMessage(HttpMethod.Get, LiveUri(options, $"/{sessionId}/content", false));
        options.Connection.Apply(request, _apiKey);
        using var response = await (httpClient ?? owned!).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendImageAsync(string imageUrl, string? text = null, bool requestResponse = true, CancellationToken cancellationToken = default)
    {
        OpenAiVisualInput.Validate(imageUrl);
        RequireConnected();
        var content = new JsonArray(new JsonObject { ["type"] = "input_image", ["image_url"] = imageUrl });
        if (!string.IsNullOrWhiteSpace(text)) content.Add((JsonNode)new JsonObject { ["type"] = "input_text", ["text"] = text });
        var item = new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = content };
        if (_settings!.Responses != null)
        {
            // The receive loop serializes backend events. Queue images until active Responses/tools finish.
            var command = new JsonObject { ["type"] = "response.item.create", ["event_id"] = Guid.NewGuid().ToString("N"), ["item"] = item };
            var bytes = System.Text.Encoding.UTF8.GetByteCount(command.ToJsonString());
            lock (_backendLock) {
                if (_backendInputItems + _visualInputs.Count >= 128 || bytes > 32768 - _backendInputBytes - _queuedVisualBytes)
                    throw new ArgumentException("Visual input exceeds the managed Live input budget. Use an HTTPS image URL or client delegation.", nameof(imageUrl));
                _queuedVisualBytes += bytes;
                _visualInputs.Enqueue(new JsonObject { ["command"] = command, ["bytes"] = bytes, ["run"] = requestResponse });
            }
            await FlushVisualInputsAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (_clientBackend != null)
        {
            lock (_clientLock) _clientTranscript.Add((JsonNode)new JsonObject { ["visual_input"] = item });
            if (requestResponse) StartClientDelegation(new OpenAiLiveDelegation("visual_" + Guid.NewGuid().ToString("N"), "client", 0, null));
        }
        else
        {
            var handler = OnClientVisualInput ?? throw new InvalidOperationException("Application-owned client delegation must set OnClientVisualInput.");
            handler(new JsonObject { ["item"] = item, ["requestResponse"] = requestResponse });
        }
    }

    private async Task FlushVisualInputsAsync(CancellationToken cancellationToken)
    {
        await _toolLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A created response owns its pending functions until the entire batch has submitted results.
            if (!IsConnected || BackendContinuationError != null || Volatile.Read(ref _managedBusy) != 0) return;
            var run = false;
            while (true)
            {
                JsonObject next;
                lock (_backendLock) {
                    if (_visualInputs.Count == 0) break;
                    next = _visualInputs.Dequeue(); _queuedVisualBytes -= next["bytes"]!.GetValue<int>();
                }
                await SendEventAsync(next["command"]!.AsObject(), cancellationToken).ConfigureAwait(false);
                run |= next["run"]!.GetValue<bool>();
            }
            if (run)
            {
                Interlocked.Exchange(ref _managedBusy, 1);
                await SendEventAsync(new JsonObject { ["type"] = "response.create" }, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _toolLock.Release(); }
    }
    private int _managedBusy;
}
