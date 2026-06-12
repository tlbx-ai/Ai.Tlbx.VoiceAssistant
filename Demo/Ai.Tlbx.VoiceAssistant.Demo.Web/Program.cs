using Ai.Tlbx.VoiceAssistant.Demo.Web.Components;
using Ai.Tlbx.VoiceAssistant.Demo.Web.Services;
using Ai.Tlbx.VoiceAssistant.Extensions;
using Ai.Tlbx.VoiceAssistant.Hardware.Web;
using Ai.Tlbx.VoiceAssistant.Interfaces;
using Ai.Tlbx.VoiceAssistant.Models;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.Extensions;
using Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore;
using Ai.Tlbx.VoiceAssistant.Provider.Google.Extensions;
using Ai.Tlbx.VoiceAssistant.BuiltInTools;
using Ai.Tlbx.VoiceAssistant.Demo.Web.Tools;
using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ai.Tlbx.VoiceAssistant.Demo.Web;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();


        // Register provider factory
        builder.Services.AddScoped<IVoiceProviderFactory, VoiceProviderFactory>();

        // Register services needed by VoiceAssistant (without registering VoiceAssistant itself)
        // since we create it manually in Home.razor with the factory pattern
        builder.Services.AddScoped<IAudioHardwareAccess, WebAudioAccess>();
        builder.Services.AddScoped<OpenAiDirectRealtimeVoiceProvider>();
        builder.Services.AddSingleton<Action<LogLevel, string>>(sp => (level, message) => Debug.WriteLine($"[{level}] {message}"));
        builder.Services.AddOpenAiDirectRealtimeVoice(options =>
        {
            options.AuthorizeRequest = _ => true;
            options.Log = (level, message) => Debug.WriteLine($"[DirectRealtime:{level}] {message}");
        });

        // Register all built-in tools directly
        builder.Services.AddTransient<IVoiceTool, TimeTool>();
        builder.Services.AddTransient<IVoiceTool, WeatherTool>();
        builder.Services.AddTransient<IVoiceTool, CalculatorTool>();
        builder.Services.AddTransient<IVoiceTool, BusinessPlanTool>();
        
        builder.Services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 1024 * 1024; // 1 MB, adjust as needed
            options.StreamBufferCapacity = 100; // Buffer for streaming
        });

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseHttpsRedirection();

        app.UseAntiforgery();
        app.UseWebSockets();

        app.MapStaticAssets();
        app.MapOpenAiDirectRealtimeVoice();
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        app.Run();
    }
}
