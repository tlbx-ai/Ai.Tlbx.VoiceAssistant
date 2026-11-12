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

if (args.Contains("--browser"))
{
    await using var pbx = new LoopbackPbx();
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

foreach (bool reject in new[] { false, true })
{
    await using var pbx = new LoopbackPbx { HoldAnswer = !reject, Reject = reject }; string path = await WriteConfig(pbx);
    try
    {
        await using var session = new SipDemoSession(Config(path)); await session.PrepareAsync("123");
        using var cancellation = new CancellationTokenSource(); var dialing = session.DialAsync(cancellation.Token);
        await pbx.Invited.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (!reject) cancellation.Cancel();
        try { await dialing; throw new InvalidOperationException("Call unexpectedly established."); }
        catch (OperationCanceledException) when (!reject) { }
        catch (SipException e) when (reject && e.StatusCode == 486) { }
        await session.StopAsync();
        Require(session.Call!.Completion.IsCompletedSuccessfully && (reject || pbx.Cancelled == 1), "Call cleanup failed.");
        Console.WriteLine(reject ? "PASS busy result and cleanup" : "PASS cancellation during ringing sends CANCEL and releases registration");
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
Console.WriteLine("7/7 SIP demo checks passed; no external services used.");
