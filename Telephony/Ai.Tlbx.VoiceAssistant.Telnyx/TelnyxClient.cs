using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ai.Tlbx.VoiceAssistant.Telnyx;

/// <summary>Thin authenticated REST wrapper. Native fields, errors and responses remain available.</summary>
public sealed class TelnyxClient(HttpClient httpClient, TelnyxOptions options)
{
    public async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new InvalidOperationException("Telnyx ApiKey is required.");
        var baseUri = options.ApiBaseUri;
        if (!baseUri.IsAbsoluteUri || (!baseUri.IsLoopback && baseUri.Scheme != "https"))
            throw new InvalidOperationException("The Telnyx API endpoint must use HTTPS.");
        var uri = new Uri(baseUri, path);
        if (uri.GetLeftPart(UriPartial.Authority) != baseUri.GetLeftPart(UriPartial.Authority) ||
            !uri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
            throw new ArgumentException("API paths must remain within ApiBaseUri.", nameof(path));
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new TelnyxApiException(response.StatusCode, text);
        if (string.IsNullOrWhiteSpace(text)) return default;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public Task<JsonElement> DialAsync(JsonObject request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "calls", request, cancellationToken);

    /// <summary>Dial using configured caller/application and the built-in bidirectional audio endpoint.</summary>
    public Task<JsonElement> DialAsync(string to, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionId) || string.IsNullOrWhiteSpace(options.FromNumber))
            throw new InvalidOperationException("Configure ConnectionId and FromNumber before dialing.");
        var body = options.Stream.ToRequest(options.MediaUri);
        body["connection_id"] = options.ConnectionId; body["from"] = options.FromNumber; body["to"] = to;
        body["webhook_url"] = options.WebhookUri.AbsoluteUri;
        return DialAsync(body, cancellationToken);
    }

    public Task<JsonElement> CommandAsync(string callControlId, string command, JsonObject? request = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"calls/{Uri.EscapeDataString(callControlId)}/actions/{Uri.EscapeDataString(command)}", request ?? new(), cancellationToken);

    public Task<JsonElement> AnswerAsync(string callControlId, JsonObject? request = null, CancellationToken cancellationToken = default) =>
        CommandAsync(callControlId, "answer", request, cancellationToken);
    public Task<JsonElement> HangupAsync(string callControlId, CancellationToken cancellationToken = default) =>
        CommandAsync(callControlId, "hangup", cancellationToken: cancellationToken);
    public Task<JsonElement> TransferAsync(string callControlId, JsonObject request, CancellationToken cancellationToken = default) =>
        CommandAsync(callControlId, "transfer", request, cancellationToken);
    public Task<JsonElement> StartStreamingAsync(string callControlId, JsonObject request, CancellationToken cancellationToken = default) =>
        CommandAsync(callControlId, "streaming_start", request, cancellationToken);
    public Task<JsonElement> StopStreamingAsync(string callControlId, CancellationToken cancellationToken = default) =>
        CommandAsync(callControlId, "streaming_stop", cancellationToken: cancellationToken);

    public Task<JsonElement> GetAvailableNumbersAsync(string query, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, "available_phone_numbers?" + query, cancellationToken: cancellationToken);
    /// <summary>Explicit paid operation; never invoked automatically by provisioning.</summary>
    public Task<JsonElement> OrderNumbersAsync(JsonObject request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "number_orders", request, cancellationToken);
    public Task<JsonElement> CreateApplicationAsync(JsonObject request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "call_control_applications", request, cancellationToken);
    public Task<JsonElement> CreateOutboundProfileAsync(JsonObject request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "outbound_voice_profiles", request, cancellationToken);
    public Task<JsonElement> AssignNumberAsync(string phoneNumberId, string connectionId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Patch, "phone_numbers/" + Uri.EscapeDataString(phoneNumberId), new JsonObject { ["connection_id"] = connectionId }, cancellationToken);

    /// <summary>Provision or reuse a named application/profile, then bind an already-owned number. Does not buy numbers.</summary>
    public async Task<TelnyxProvisioningResult> ProvisionAsync(TelnyxProvisioningRequest request, CancellationToken cancellationToken = default)
    {
        options.ValidateHost();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ApplicationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PhoneNumberId);
        string profileId = request.OutboundVoiceProfileId ?? string.Empty;
        if (profileId.Length == 0)
        {
            profileId = await FindNamedIdAsync("outbound_voice_profiles", "name", request.ApplicationName, cancellationToken) ?? string.Empty;
            if (profileId.Length == 0)
            {
                var profile = request.OutboundProfile?.DeepClone().AsObject() ?? new JsonObject();
                profile["name"] = request.ApplicationName;
                profileId = Id(await CreateOutboundProfileAsync(profile, cancellationToken));
            }
        }
        string connectionId = request.ConnectionId ?? string.Empty;
        var application = new JsonObject
        {
            ["application_name"] = request.ApplicationName, ["webhook_event_url"] = options.WebhookUri.AbsoluteUri,
            ["webhook_api_version"] = "2", ["active"] = true,
            ["outbound"] = new JsonObject { ["outbound_voice_profile_id"] = profileId }
        };
        if (connectionId.Length == 0)
        {
            connectionId = await FindNamedIdAsync("call_control_applications", "application_name", request.ApplicationName, cancellationToken) ?? string.Empty;
        }
        if (connectionId.Length == 0) connectionId = Id(await CreateApplicationAsync(application, cancellationToken));
        else await SendAsync(HttpMethod.Patch, "call_control_applications/" + Uri.EscapeDataString(connectionId), application, cancellationToken);
        var number = await AssignNumberAsync(request.PhoneNumberId, connectionId, cancellationToken);
        var from = number.GetProperty("data").GetProperty("phone_number").GetString()
            ?? throw new InvalidOperationException("Telnyx did not return the assigned phone_number.");
        return new(connectionId, profileId, request.PhoneNumberId, from);
    }

    private static string Id(JsonElement response) => response.GetProperty("data").GetProperty("id").GetString()
        ?? throw new InvalidOperationException("Telnyx response has no resource ID.");
    private async Task<string?> FindNamedIdAsync(string resource, string field, string name, CancellationToken cancellationToken)
    {
        string? id = null;
        for (int page = 1; ; page++)
        {
            var response = await SendAsync(HttpMethod.Get, $"{resource}?filter[{field}][contains]={Uri.EscapeDataString(name)}&page[number]={page}&page[size]=250", cancellationToken: cancellationToken);
            foreach (var match in response.GetProperty("data").EnumerateArray().Where(x => x.TryGetProperty(field, out var value) && value.GetString() == name))
            {
                if (id is not null) throw new InvalidOperationException("Multiple Telnyx resources share this name; supply an explicit resource ID.");
                id = match.GetProperty("id").GetString();
            }
            if (!response.TryGetProperty("meta", out var meta) || !meta.TryGetProperty("total_pages", out var total) || page >= total.GetInt32()) return id;
        }
    }
}

public sealed class TelnyxProvisioningRequest
{
    public required string ApplicationName { get; init; }
    public required string PhoneNumberId { get; init; }
    public string? ConnectionId { get; init; }
    public string? OutboundVoiceProfileId { get; init; }
    /// <summary>Native settings for creation, e.g. whitelisted_destinations and daily_spend_limit. Existing profiles are not overwritten.</summary>
    public JsonObject? OutboundProfile { get; init; }
}

public sealed record TelnyxProvisioningResult(string ConnectionId, string OutboundVoiceProfileId, string PhoneNumberId, string PhoneNumber);

public sealed class TelnyxApiException(HttpStatusCode statusCode, string responseBody)
    : HttpRequestException($"Telnyx API returned HTTP {(int)statusCode}.", null, statusCode)
{
    public string ResponseBody { get; } = responseBody;
}
