using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ai.Tlbx.Sip;
using Ai.Tlbx.VoiceAssistant.Demo.Web.Components;
using Ai.Tlbx.VoiceAssistant.Demo.Web.Services;
using Ai.Tlbx.VoiceAssistant.Hardware.Web;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi;

static async Task Eventually(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    while (!condition()) await Task.Delay(20, timeout.Token);
}
static void Require(bool condition, string message)
{ if (!condition) throw new InvalidOperationException(message); }
static async Task<string> WriteConfig(LoopbackPbx pbx)
{
    string path = Path.Combine(Path.GetTempPath(), "sip-demo-test-" + Guid.NewGuid().ToString("N") + ".json");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
    {
        Id = "local-test", Registrar = "127.0.0.1", Port = pbx.Port, User = "demo", Domain = "example.test",
        Credentials = new { Username = "demo", Password = "test-only" }, LocalAddress = "127.0.0.1",
        TransactionTimeout = TimeSpan.FromSeconds(1), InitialRetransmitInterval = TimeSpan.FromMilliseconds(50), AutoReconnect = false
    }));
    return path;
}
static IConfiguration Config(string path) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sip:ConfigFile"] = path }).Build();

// Explicit opt-in: paid OpenAI response, localhost SIP peer only, no real phone number.
if (args.Contains("--conversation-start-live"))
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    await using var pbx = new LoopbackPbx { SilentInput = true };
    string path = await WriteConfig(pbx);
    try
    {
        await using var session = new SipDemoSession(Config(path));
        await session.PrepareAsync("123", deadline.Token); await session.DialAsync(deadline.Token);
        Require(session.Call!.Status.State == SipCallState.Established, "Call must be accepted first.");
        var traces = new System.Collections.Concurrent.ConcurrentQueue<Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Models.OpenAiRealtimeResponseTrace>();
        var usage = new System.Collections.Concurrent.ConcurrentQueue<UsageReport>();
        var messages = new System.Collections.Concurrent.ConcurrentQueue<ChatMessage>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new OpenAiVoiceProvider();
        await using var assistant = new Ai.Tlbx.VoiceAssistant.VoiceAssistant(session.Hardware!, provider);
        assistant.OnMessageAdded = messages.Enqueue; assistant.OnUsageReceived = usage.Enqueue;
        var previousError = provider.OnError;
        provider.OnError = error => { previousError?.Invoke(error); done.TrySetException(new InvalidOperationException(error)); };
        provider.OnResponseTraceCompleted = trace => { traces.Enqueue(trace); done.TrySetResult(); };
        await assistant.StartAsync(new OpenAiVoiceSettings
        {
            UseEphemeralKey = false, ClientResponseControl = true, MaxTokens = 200,
            AppendToolCallPreambleInstructions = false,
            Instructions = "Du bist ein KI-Assistent. Eröffne das Gespräch auf Deutsch mit einer kurzen Vorstellung als KI-Assistent und frage danach, welches Anliegen die angerufene Person hat. Höchstens zwei kurze Sätze."
        }, deadline.Token);
        while (!assistant.IsConversationStartReady) await Task.Delay(20, deadline.Token);
        await Task.Delay(250, deadline.Token);
        Require(traces.IsEmpty && messages.IsEmpty, "StartAsync alone generated a response.");
        pbx.ReceivedPayloads.Clear();
        var request = await assistant.StartConversationAsync(deadline.Token);
        await done.Task.WaitAsync(deadline.Token);
        Require(await session.Hardware!.WaitForPlaybackDrainAsync(TimeSpan.FromSeconds(20)), "Opening playback failed to drain.");
        await Task.Delay(150, deadline.Token);
        byte[] received = pbx.ReceivedPayloads.SelectMany(p => p).ToArray();
        Require(received.Count(b => b is not (0xd5 or 0x55)) > 4000, "No substantial AI audio reached the SIP peer.");
        Require(traces.Count == 1 && usage.Count > 0 && messages.Any(m => m.Role == ChatMessage.AssistantRole), "Missing normal transcript, response or final usage.");
        string output = Path.GetFullPath(".artifacts/conversation-start"); Directory.CreateDirectory(output);
        using (var wav = new BinaryWriter(File.Create(Path.Combine(output, "sip-opening.wav"))))
        {
            wav.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); wav.Write(36 + received.Length * 2);
            wav.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); wav.Write(16); wav.Write((short)1); wav.Write((short)1);
            wav.Write(8000); wav.Write(16000); wav.Write((short)2); wav.Write((short)16);
            wav.Write(System.Text.Encoding.ASCII.GetBytes("data")); wav.Write(received.Length * 2);
            foreach (byte sample in received)
            {
                int a = sample ^ 0x55, segment = (a & 0x70) >> 4;
                int magnitude = ((a & 15) << 4) + (segment == 0 ? 8 : 264);
                if (segment > 1) magnitude <<= segment - 1;
                wav.Write((short)((a & 0x80) != 0 ? magnitude : -magnitude));
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "live-result.json"), JsonSerializer.Serialize(new
        {
            request, responseCount = traces.Count, usageCount = usage.Count, rtpSamples = received.Length,
            transcript = messages.Where(m => m.Role == ChatMessage.AssistantRole).Select(m => m.Content).ToArray(),
            responseStatus = traces.Single().Status
        }), deadline.Token);
        await assistant.StopAsync(); await session.StopAsync();
        Console.WriteLine($"PASS real AI opening: {request}; {received.Length} SIP PCMA samples, transcript, final usage and playback drain verified; WAV: {output}");
    }
    finally { File.Delete(path); }
    return;
}

if (args.Contains("--browser"))
{
    await using var pbx = new LoopbackPbx { Reject = args.Contains("--reject"), RejectionCode = 404 };
    string path = await WriteConfig(pbx);
    try
    {
        string root = Path.GetFullPath("Demo/Ai.Tlbx.VoiceAssistant.Demo.Web");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = typeof(App).Assembly.GetName().Name, ContentRootPath = root, EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://localhost:7091");
        builder.Configuration["Sip:ConfigFile"] = path;
        builder.WebHost.UseSetting(WebHostDefaults.StaticWebAssetsKey, Path.Combine(root, "bin/Debug/net11.0/Ai.Tlbx.VoiceAssistant.Demo.Web.staticwebassets.runtime.json"));
        builder.WebHost.UseStaticWebAssets();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddScoped<IVoiceProviderFactory, ProbeFactory>();
        builder.Services.AddScoped<IAudioHardwareAccess, WebAudioAccess>();
        builder.Services.AddScoped<SipDemoSession>();
        builder.Services.AddSingleton<Action<Ai.Tlbx.VoiceAssistant.Models.LogLevel, string>>((_, _) => { });
        Ai.Tlbx.Inference.Configuration.ServiceCollectionExtensions.AddAiInference(builder.Services, o => o.AddOpenAi("test-unused"));
        builder.Services.AddScoped<InferenceHandoffBackend>();
        builder.Services.AddScoped<MistralDemoBackend>();
        var app = builder.Build(); app.UseAntiforgery();
        app.MapStaticAssets(Path.Combine(root, "bin/Debug/net11.0/Ai.Tlbx.VoiceAssistant.Demo.Web.staticwebassets.endpoints.json"));
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        Console.WriteLine("LOCAL TEST PEERS ONLY: normal demo components; no real AI or phone calls.");
        await app.RunAsync();
    }
    finally { File.Delete(path); }
    return;
}

foreach (var invalid in new[] { "", "sip:other@example.test", "123\r\nX: injected", "123;transport=tcp", "12+34", "   " })
{
    try { SipDemoSession.ValidateNumber(invalid); throw new InvalidOperationException("Accepted invalid destination."); }
    catch (ArgumentException) { }
}
Require(SipDemoSession.ValidateNumber(" **610 ") == "**610", "Internal extension rejected.");
Console.WriteLine("PASS destination validation prevents arbitrary SIP targets/header injection");

string keyFile = Path.GetTempFileName();
await File.WriteAllTextAsync(keyFile, "test-placeholder-no-network");
try
{
    using var services = new ServiceCollection().AddSingleton(new DemoOpenAiCredentials(keyFile)).BuildServiceProvider();
    var factory = new VoiceProviderFactory(services);
    await using var realtime = factory.CreateProvider(VoiceProviderType.OpenAI, serverAudio: true);
    await using var live = factory.CreateProvider(VoiceProviderType.OpenAiLive, serverAudio: true);
    Require(realtime is OpenAiVoiceProvider && live is OpenAiLiveProvider,
        "SIP selected browser audio instead of the server-side OpenAI providers.");
    Console.WriteLine("PASS OpenAI Realtime and GPT-Live select server-side PCM providers");
}
finally { File.Delete(keyFile); }

foreach (var rate in new[] { AudioSampleRate.Rate16000, AudioSampleRate.Rate24000 })
{
    await using var pbx = new LoopbackPbx(); string path = await WriteConfig(pbx);
    try
    {
        await using var session = new SipDemoSession(Config(path));
        await session.PrepareAsync("123"); await session.DialAsync();
        var provider = new ProbeProvider(rate);
        await using var assistant = new Ai.Tlbx.VoiceAssistant.VoiceAssistant(session.Hardware!, provider);
        await assistant.StartAsync(new OpenAiVoiceSettings());
        await Eventually(() => provider.InputChunks > 0 && pbx.ReceivedAudio > 0);
        Require(assistant.ChatHistory.Any(m => m.Content.Contains("COMPLETE-SIP-RESULT")), "Complete tool output was lost.");
        await pbx.RemoteHangupAsync(); await session.Call!.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Require(session.Call.Status.EndReason == SipCallEndReason.RemoteHangup, "Remote hangup was lost.");
        await assistant.StopAsync(); await session.StopAsync();
        Require(!provider.IsConnected && session.ClientState == SipClientState.Disconnected && pbx.Unregistered > 0, "Teardown leaked a session.");
        Console.WriteLine($"PASS SIP ↔ VoiceAssistant PCM at {(int)rate} Hz, complete chat events and remote teardown");
    }
    finally { File.Delete(path); }
}

foreach (int code in new[] { 0, 404, 484, 486, 604 })
{
    bool reject = code != 0;
    await using var pbx = new LoopbackPbx { HoldAnswer = !reject, Reject = reject, RejectionCode = code }; string path = await WriteConfig(pbx);
    try
    {
        await using var session = new SipDemoSession(Config(path)); await session.PrepareAsync("123");
        using var cancellation = new CancellationTokenSource(); var dialing = session.DialAsync(cancellation.Token);
        await pbx.Invited.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (!reject) cancellation.Cancel();
        try { await dialing; throw new InvalidOperationException("Call unexpectedly established."); }
        catch (OperationCanceledException) when (!reject) { }
        catch (SipException e) when (reject && e.StatusCode == code) { }
        await session.StopAsync();
        Require(session.Call!.Completion.IsCompletedSuccessfully && (reject || pbx.Cancelled == 1), "Call cleanup failed.");
        if (reject) Require(session.Call.Status.SipStatusCode == code && session.Call.Status.Error!.Contains(code.ToString()), "Native call failure disappeared during demo cleanup.");
        Console.WriteLine(reject ? $"PASS SIP {code} failure remains visible after demo cleanup" : "PASS cancellation during ringing sends CANCEL and releases registration");
    }
    finally { File.Delete(path); }
}
await using (var pbx = new LoopbackPbx())
{
    string path = await WriteConfig(pbx);
    try
    {
        await using var session = new SipDemoSession(Config(path));
        await session.PrepareAsync("123"); await session.DialAsync();
        var provider = new ProbeProvider(AudioSampleRate.Rate24000);
        await using var assistant = new Ai.Tlbx.VoiceAssistant.VoiceAssistant(session.Hardware!, provider);
        await assistant.StartAsync(new OpenAiVoiceSettings());
        await Eventually(() => provider.InputChunks > 0);
        Require(await session.Hardware!.WaitForPlaybackDrainAsync(TimeSpan.FromSeconds(3)), "Initial probe did not drain.");
        pbx.ReceivedPayloads.Clear();
        byte[] markers = [0xfa, 0x7a, 0xea, 0x6a, 0x9a, 0x1a, 0x8a, 0x0a];
        foreach (byte marker in markers)
        {
            // Independent A-law reference decoder; verify actual RTP payloads.
            int a = marker ^ 0x55, segment = (a & 0x70) >> 4;
            int magnitude = ((a & 15) << 4) + (segment == 0 ? 8 : 264);
            if (segment > 1) magnitude <<= segment - 1;
            short value = (short)((a & 0x80) != 0 ? magnitude : -magnitude);
            byte[] pcm = new byte[14400 * 2]; // Eight 600-ms chunks arrive in a single AI burst.
            for (int i = 0; i < 14400; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), value);
            provider.OnAudioReceived!(Convert.ToBase64String(pcm));
        }
        Require(await session.Hardware.WaitForPlaybackDrainAsync(TimeSpan.FromSeconds(12)), "AI burst did not drain.");
        await Task.Delay(100);
        byte[] received = pbx.ReceivedPayloads.SelectMany(p => p).ToArray();
        foreach (byte marker in markers)
            Require(received.Count(b => b == marker) is >= 4700 and <= 4900, $"AI burst lost PCM section {marker:x2}: {received.Count(b => b == marker)}/4800 samples.");
        var order = new List<byte>();
        foreach (byte section in received.Where(markers.Contains).Chunk(160).Select(p => p.GroupBy(b => b).MaxBy(g => g.Count())!.Key))
            if (order.Count == 0 || order[^1] != section) order.Add(section);
        Require(order.SequenceEqual(markers), "AI burst changed section order.");
        await assistant.StopAsync(); await session.StopAsync();
        Console.WriteLine("PASS complete 4.8-second AI burst reaches RTP in order, including middle sections");
    }
    finally { File.Delete(path); }
}
Console.WriteLine("10/10 SIP demo checks passed; no external services used.");
