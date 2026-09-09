## Gateways, custom model IDs and runtime configuration

All provider settings (including transcription settings/options) expose a mutable
`Connection` (`ProviderEndpointOptions`). `Connection.Endpoint` is the **full URL**,
including the gateway path prefix. HTTP and WebSocket URLs are independent. Defaults
still connect to the vendor directly.

```csharp
var settings = new OpenAiVoiceSettings
{
    ModelId = "company-realtime", // model_name configured in LiteLLM
    UseEphemeralKey = false      // authenticate the WebSocket directly
};
settings.Connection.Endpoint = "wss://gateway.example.com/v1/realtime";
settings.Connection.Headers["X-Cost-Centre"] = "engineering";
await using var provider = new OpenAiVoiceProvider("gateway-virtual-key");
await provider.ConnectAsync(settings);

await provider.DisconnectAsync();
settings.Connection.Endpoint = "wss://backup.example.com/team/v1/realtime?api-version=custom";
settings.Connection.ApiKey = "rotated-gateway-key";
settings.ModelId = "company-realtime-backup";
await provider.ConnectAsync(settings); // same instance, new routing and credentials
```

LiteLLM documents the `/v1/realtime?model=<model_name>` route and gateway
Bearer authentication in its [Realtime guide](https://docs.litellm.ai/docs/realtime).
The library URL-encodes the model alias. Existing endpoint query parameters are
preserved and override generated parameters of the same name. Supply the complete
route; `HttpClient.BaseAddress` does not override these explicit endpoint URLs.

`ModelId` overrides the wire model name on OpenAI, Google and xAI settings, as well
as OpenAI HTTP/STT options and xAI STT settings. For nested input transcription use
`OpenAiVoiceSettings.InputAudioTranscription.ModelId` or
`XaiVoiceSettings.InputAudioTranscriptionModelId`. Google IDs are sent exactly as
provided, including `models/` if the receiving API requires it. OpenAI STT sends
its model in the session configuration, not as a connection query parameter.
xAI's native STT route has no model selector: `ModelId` labels transcript and usage
metadata. If a gateway needs a routing model for either STT provider, specify it
explicitly in `Connection.Endpoint`, for example `...?model=company-transcription`.
The native endpoints reject such connection-level transcription model parameters.

Keep the existing `Model` / `TranscriptionModel` enum set to the underlying model's
capability profile: it still controls reasoning, VAD, transcription prompts,
streaming and diarized response formats. An alias changes routing; it does not
infer capabilities or translate one vendor's wire protocol into another. A gateway
must implement the selected provider's protocol (or provide that translation).
Azure/AWS routing through a compatible gateway is supported by these configuration
hooks; this is not a native AWS SigV4 or Azure protocol adapter.

For a raw API-key header instead of Bearer authentication:

```csharp
settings.Connection.AuthenticationHeaderName = "api-key";
settings.Connection.AuthenticationScheme = null;
settings.Connection.ApiKey = "gateway-key";
```

`Headers` entries override automatic authentication headers. Set
`AuthenticationHeaderName = null` to disable automatic header authentication.
Google defaults to `ApiKeyQueryParameter = "key"` and no auth header; set that query
parameter property to `null` when switching Google to gateway header auth.
`ApiKey` overrides the constructor key and is read again for each operation.
For callback-only or unauthenticated gateways, pass `apiKey: ""` to the provider
constructor and disable the automatic auth field(s).

`ConfigureWebSocket` runs on each new socket after headers are applied; use it for
`ClientWebSocketOptions.Proxy`, client certificates or rotating headers.
`ConfigureHttpRequest` runs immediately before each HTTP send, after authentication
and content are assigned, and can supply dynamic headers or request signing.

For HTTP transcription, set `OpenAiHttpLiveTranscriptionOptions.Connection.Endpoint`
and `ModelId`. The transcriber reads these for every upload. The five-argument
constructor accepts an optional caller-owned `HttpClient` for proxy/TLS handlers;
it neither changes that client's default authentication headers nor disposes it.

OpenAI's additional routes are configurable independently:

- `OpenAiVoiceSettings.ClientSecretsConnection`: HTTP client-secret endpoint,
  credentials and headers. Used when `UseEphemeralKey` is true (the default).
- `OpenAiVoiceSettings.Connection`: Realtime WebSocket endpoint and authentication.
  For gateways without client-secret support, set `UseEphemeralKey = false`.
- `OpenAiVoiceSettings.RealtimeCallsEndpoint`: browser WebRTC SDP endpoint. The
  server returns this configured URL in each direct-session response. It must be
  browser-accessible, allow the required CORS requests and accept ephemeral Bearer
  credentials. Server-side gateway headers and long-lived keys are not copied to
  the browser. Direct browser sessions always use ephemeral credentials;
  `UseEphemeralKey` applies only to the server-side WebSocket provider.
- `OpenAiDirectRealtimeOptions.ClientSecretsHttpClient`: optional caller-owned
  HTTP client for the direct-browser session's server-side client-secret request.

Change settings **between operations**: disconnect before changing a WebSocket
session, then reconnect with the same provider and updated settings. Finish an
in-flight HTTP upload before mutating its options. Concurrent mutation is not
supported, and changing a property does not migrate an already-open connection.
Local contract tests exercise all five WebSocket providers through two successful
connections on the same instances, plus HTTP URL/auth/model changes. They do not
constitute an end-to-end test against a deployed LiteLLM/Azure/AWS installation.

To verify initialization against the real vendor endpoints using explicitly supplied
`Connection`, `ApiKey` and `ModelId` values, run:

```powershell
dotnet run --project Tests/ContractTests/ContractTests.csproj -c Release -- --live-custom-config-smoke
```

This opt-in paid API smoke requires `OPENAI_API_KEY`, `XAI_API_KEY` and
`GEMINI_API_KEY` (or `GOOGLE_API_KEY`). It checks provider acknowledgements, both
OpenAI WebSocket authentication modes and one HTTP upload of synthetic silence.
It does not record microphone audio or request a generated spoken answer.
