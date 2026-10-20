using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ai.Tlbx.VoiceAssistant.Provider.Mistral;

/// <summary>Bounded audio requests. Does not emulate a realtime WebSocket service.</summary>
public sealed class MistralAudioClient
{
    private readonly HttpClient _http;
    private readonly string _key;
    public bool UseOpenRouter { get; }

    public MistralAudioClient(HttpClient httpClient, string apiKey, bool useOpenRouter = true)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _http = httpClient; _key = apiKey; UseOpenRouter = useOpenRouter;
    }

    private string Model(string model) => UseOpenRouter && !model.Contains('/') ? "mistralai/" + model : model;
    private HttpRequestMessage Request(string operation)
    {
        var message = new HttpRequestMessage(HttpMethod.Post,
            (UseOpenRouter ? "https://openrouter.ai/api/v1/audio/" : "https://api.mistral.ai/v1/audio/") + operation);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        return message;
    }
    private static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    public async Task<string> TranscribeAsync(byte[] wav, MistralVoiceSettings settings, CancellationToken ct = default)
    {
        using var message = Request("transcriptions");
        if (UseOpenRouter)
        {
            var body = new JsonObject { ["model"] = Model(settings.TranscriptionModel),
                ["input_audio"] = new JsonObject { ["data"] = Convert.ToBase64String(wav), ["format"] = "wav" } };
            if (settings.Language != null) body["language"] = settings.Language;
            message.Content = Json(body);
        }
        else
        {
            var form = new MultipartFormDataContent(); message.Content = form;
            form.Add(new StringContent(settings.TranscriptionModel), "model");
            form.Add(new ByteArrayContent(wav), "file", "audio.wav");
            if (settings.Language != null) form.Add(new StringContent(settings.Language), "language");
        }
        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        await CheckAsync(response, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("text").GetString() ?? "";
    }

    public async Task<byte[]> SpeakAsync(string text, MistralVoiceSettings settings, CancellationToken ct = default)
    {
        using var message = Request("speech");
        message.Content = Json(new JsonObject { ["model"] = Model(settings.SpeechModel), ["input"] = text,
            [UseOpenRouter ? "voice" : "voice_id"] = settings.Voice, ["response_format"] = "mp3" });
        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        await CheckAsync(response, ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return Convert.FromBase64String(doc.RootElement.GetProperty("audio_data").GetString()!);
        }
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Mistral audio returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)}", null, response.StatusCode);
    }
}
