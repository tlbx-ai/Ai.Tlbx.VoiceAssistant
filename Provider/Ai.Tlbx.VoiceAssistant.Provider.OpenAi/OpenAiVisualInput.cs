using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Protocol;

namespace Ai.Tlbx.VoiceAssistant.Provider.OpenAi;

public static class OpenAiVisualInput
{
    public static void Validate(string imageUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageUrl);
        if (imageUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            var comma = imageUrl.IndexOf(',');
            if (comma < 0 || !imageUrl[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Image data URLs must be base64 encoded.", nameof(imageUrl));
            var format = imageUrl[..comma].ToLowerInvariant();
            if (format is not ("data:image/png;base64" or "data:image/jpeg;base64" or "data:image/webp;base64" or "data:image/gif;base64"))
                throw new ArgumentException("Use PNG, JPEG, WebP or GIF image data.", nameof(imageUrl));
            if (imageUrl.Length > 28 * 1024 * 1024 || Convert.FromBase64String(imageUrl[(comma + 1)..]).Length == 0)
                throw new ArgumentException("Image data must be nonempty and at most 20 MiB.", nameof(imageUrl));
        }
        else if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)
            throw new ArgumentException("Use an HTTPS image URL or a base64 image data URL.", nameof(imageUrl));
    }
}

public sealed partial class OpenAiVoiceProvider
{
    public async Task SendImageAsync(string imageUrl, string? text = null, bool requestResponse = true, CancellationToken cancellationToken = default)
    {
        OpenAiVisualInput.Validate(imageUrl);
        if (!IsConnected) throw new InvalidOperationException("Connect before supplying visual input.");
        long generation;
        lock (_responseGate) generation = _sessionGeneration;
        var parts = new List<ContentPart> { new() { Type = "input_image", ImageUrl = imageUrl } };
        if (!string.IsNullOrWhiteSpace(text)) parts.Add(new() { Type = "input_text", Text = text });
        var item = new ConversationItemCreateMessage { Item = new() { Type = "message", Role = "user", Content = parts } };
        await SendMessageAsync(JsonSerializer.Serialize(item, OpenAiJsonContext.Default.ConversationItemCreateMessage), cancellationToken, generation).ConfigureAwait(false);
        if (requestResponse)
        {
            lock (_responseGate) {
                if (generation != _sessionGeneration) throw new OperationCanceledException("Visual input belongs to a previous session.");
                _responseRequested = true; _responseCreateReason = "visual_input";
            }
            await TryStartRequestedResponseAsync().ConfigureAwait(false);
        }
    }
}
