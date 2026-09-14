using System.Reflection;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

internal static class OpenAiRealtimeToolContinuationTests
{
    public static async Task RunAsync()
    {
        var calls = new List<string>();
        var logs = new List<string>();
        await using var provider = new OpenAiVoiceProvider("test", (_, message) => logs.Add(message));
        typeof(OpenAiVoiceProvider).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(provider, new OpenAiVoiceSettings { Tools = [new Tool(calls)] });
        var process = typeof(OpenAiVoiceProvider).GetMethod("ProcessReceivedMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task Event(string json) => await (Task)process.Invoke(provider, [json])!;

        await Event("""{"type":"response.created","response":{"id":"r1"}}""");
        await Event("""{"type":"response.function_call_arguments.delta","name":"probe","call_id":"c1","delta":"{\"n\":1}"}""");
        await Event("""{"type":"response.function_call_arguments.done","name":"probe","call_id":"c1","arguments":"{\"n\":1}"}""");
        Check(calls.Count == 0, "Kein Tool und keine Folgeantwort vor response.done.");
        Check(!logs.Any(x => x.Contains("Sending response.create")), "Keine konkurrierende Antwort.");
        await Event("""{"type":"response.done","response":{"id":"r1","status":"completed","output":[{"type":"function_call","name":"probe","call_id":"c1","arguments":"{\"n\":1}"},{"type":"function_call","name":"probe","call_id":"c2","arguments":"{\"n\":2}"}]}}""");
        Check(calls.SequenceEqual(new[] { "{\"n\":1}", "{\"n\":2}" }), "Alle finalen Argumente seriell und unverändert ausführen.");
        Check(logs.Count(x => x.Contains("Sending response.create")) == 1, "Genau eine Fortsetzung nach allen Ergebnissen.");
        await Event("""{"type":"response.created","response":{"id":"r2"}}""");
        await Event("""{"type":"response.done","response":{"id":"r2","status":"cancelled","output":[{"type":"function_call","name":"probe","call_id":"c3","arguments":"{}"}]}}""");
        Check(calls.Count == 2, "Abgebrochene Antworten führen keine Aktionen aus.");
        Check(logs.Count(x => x.Contains("Sending response.create")) == 1, "Keine Fortsetzung nach Abbruch.");
        Console.WriteLine("Realtime tool continuation regressions passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Tool(List<string> calls) : IVoiceTool
    {
        public string Name => "probe";
        public string Description => "Test";
        public Type ArgsType => typeof(object);
        public Task<string> ExecuteAsync(string argumentsJson)
        {
            calls.Add(argumentsJson);
            return Task.FromResult("ok");
        }
    }
}
