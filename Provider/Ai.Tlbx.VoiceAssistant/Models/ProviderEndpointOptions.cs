using System.Net.Http;
using System.Net.WebSockets;

namespace Ai.Tlbx.VoiceAssistant.Models;

/// <summary>Mutable routing and authentication settings, read for each request or connection.
/// Update between operations; concurrent mutation is not supported.</summary>
public sealed class ProviderEndpointOptions
{
    public ProviderEndpointOptions(string endpoint) => Endpoint = endpoint;

    /// <summary>Full endpoint URL including any gateway path prefix and query parameters.</summary>
    public string Endpoint { get; set; }
    /// <summary>Overrides the constructor API key; read again on each operation.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Null disables automatic authentication headers.</summary>
    public string? AuthenticationHeaderName { get; set; } = "Authorization";
    /// <summary>Use null for a raw API key header.</summary>
    public string? AuthenticationScheme { get; set; } = "Bearer";
    /// <summary>Optional API key query parameter (Google defaults to key).</summary>
    public string? ApiKeyQueryParameter { get; set; }
    /// <summary>Additional headers; entries override automatic authentication.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Runs after authentication and headers, before each HTTP send.</summary>
    public Action<HttpRequestMessage>? ConfigureHttpRequest { get; set; }
    /// <summary>Runs before each connect; supports proxies, certificates and dynamic headers.</summary>
    public Action<ClientWebSocketOptions>? ConfigureWebSocket { get; set; }

    /// <summary>Preserves endpoint query parameters, which override generated defaults.</summary>
    public Uri BuildUri(string? defaultQuery = null, string? fallbackApiKey = null)
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https" or "ws" or "wss") ||
            !string.IsNullOrEmpty(endpoint.Fragment) || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("Endpoint must be an absolute HTTP(S) or WS(S) URL without credentials or a fragment.", nameof(Endpoint));
        var supplied = endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var names = supplied.Select(p => Uri.UnescapeDataString(p.Split('=')[0])).ToHashSet(StringComparer.Ordinal);
        var query = (defaultQuery ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !names.Contains(Uri.UnescapeDataString(p.Split('=')[0]))).Concat(supplied).ToList();
        if (!string.IsNullOrEmpty(ApiKeyQueryParameter) && !names.Contains(ApiKeyQueryParameter))
            query.Add($"{Uri.EscapeDataString(ApiKeyQueryParameter)}={Uri.EscapeDataString(ApiKey ?? fallbackApiKey ?? "")}");
        return new UriBuilder(endpoint) { Query = string.Join("&", query) }.Uri;
    }

    public void Apply(HttpRequestMessage request, string fallbackApiKey)
    {
        if (!string.IsNullOrEmpty(AuthenticationHeaderName))
        {
            request.Headers.Remove(AuthenticationHeaderName);
            request.Headers.Add(AuthenticationHeaderName, AuthenticationValue(fallbackApiKey));
        }
        foreach (var header in Headers)
        {
            request.Headers.Remove(header.Key);
            request.Headers.Add(header.Key, header.Value);
        }
        ConfigureHttpRequest?.Invoke(request);
    }

    public void Apply(ClientWebSocketOptions options, string fallbackApiKey)
    {
        if (!string.IsNullOrEmpty(AuthenticationHeaderName))
            options.SetRequestHeader(AuthenticationHeaderName, AuthenticationValue(fallbackApiKey));
        foreach (var header in Headers)
            options.SetRequestHeader(header.Key, header.Value);
        ConfigureWebSocket?.Invoke(options);
    }

    private string AuthenticationValue(string fallbackApiKey) =>
        string.IsNullOrEmpty(AuthenticationScheme) ? ApiKey ?? fallbackApiKey : $"{AuthenticationScheme} {ApiKey ?? fallbackApiKey}";
}
