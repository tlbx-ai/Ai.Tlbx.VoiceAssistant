using System.Net;
using System.Text.Json.Nodes;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;

internal static partial class OpenAiLiveTests
{
    private static async Task VerifyRawContextRefreshAsync()
    {
        var tool = new ClientLargeTool("must-not-execute");
        var transcript = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "Friday" });
        var httpCalls = 0;
        var facts = new string('ä', 300);
        using var http = new HttpClient(new ClientHandler(async request => {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            if (++httpCalls == 1)
            {
                transcript.Add(new JsonObject { ["role"] = "user", ["text"] = "Thursday, not Friday. Do not act." });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject {
                    ["id"] = "raw-before-correction", ["model"] = "fake", ["status"] = "completed",
                    ["output"] = new JsonArray(ClientCall("old-action-one"), ClientCall("old-action-two")) }.ToJsonString()) };
            }
            var native = body["input"]!.AsArray().OfType<JsonObject>().ToArray();
            Check(native.Count(item => item["type"]?.GetValue<string>() == "function_call") == 2 &&
                native.Count(item => item["type"]?.GetValue<string>() == "function_call_output" &&
                    item["output"]!.GetValue<string>().Contains("no action performed")) == 2,
                "refresh pairs every old native tool call with a skipped output before the next Responses request");
            Check(body["input"]!.ToJsonString().Contains("Thursday"), "raw runner receives latest correction");
            return ClientResponse(ClientAnswer(facts));
        }));
        var settings = new OpenAiLiveSettings { Tools = [tool], ClientBackend = new() { HttpClient = http } };
        using var backend = new ClientBackendProbe(settings, "key", [], _ => { }, _ => { }, _ => { });
        OpenAiLiveBackendContext Current() => new("raw", new("raw-job", "client", 100, null),
            (JsonArray)transcript.DeepClone(), Array.Empty<ChatMessage>(), [tool], (_, _) => Task.CompletedTask, _ => { }) {
            GetLatestTranscript = () => (JsonArray)transcript.DeepClone(), ToolJournal = settings.ClientBackend.ToolJournal
        };
        backend.Context = Current();
        try { await backend.RunAsync((JsonArray)transcript.DeepClone(), default); throw new Exception("Stale actions were accepted"); }
        catch (OpenAiLiveContextChangedException) { }
        backend.Context = Current();
        Check(await backend.RunAsync((JsonArray)transcript.DeepClone(), default) == facts && tool.Calls == 0 && httpCalls == 2,
            "raw client refresh retains protocol pairing, executes no stale action and accepts facts over 500 bytes");
    }
}
