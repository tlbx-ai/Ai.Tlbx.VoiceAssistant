using System;
using System.IO;

namespace Ai.Tlbx.VoiceAssistant.Demo.Web.Services;

// Demo-only configuration. The provider libraries retain their environment-variable fallback.
internal sealed class DemoOpenAiCredentials
{
    public string? ApiKey { get; }

    public DemoOpenAiCredentials(string? keyFile)
    {
        if (string.IsNullOrWhiteSpace(keyFile))
        {
            ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            return;
        }
        ApiKey = File.ReadAllText(Path.GetFullPath(keyFile)).Trim();
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("The OpenAI key file is empty.");
    }
}
