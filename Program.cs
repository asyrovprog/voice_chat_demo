// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

internal static class Program
{
    internal static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Adding configuration from appsettings.json and environment variables
        builder.Services.ConfigureOptions<OpenAIOptions>(OpenAIOptions.SectionName);
        builder.Services.ConfigureOptions<ChatOptions>(ChatOptions.SectionName);
        builder.Services.ConfigureOptions<RealtimeModelsOptions>(RealtimeModelsOptions.SectionName);

        // Configure Semantic Kernel in DI container
        builder.Services
            .AddKernel()
            .AddOpenAIChatCompletion(
                modelId: builder.Configuration[$"{OpenAIOptions.SectionName}:ChatModelId"]!,
                apiKey: builder.Configuration[$"{OpenAIOptions.SectionName}:ApiKey"]!
            );

        // Register shared services
        builder.Services.AddSingleton<PipelineControlPlane>();

        // Classic multi-stage pipeline services (VAD -> STT -> Chat -> TTS -> Playback)
        builder.Services.AddSingleton<SpeechToTextService>();
        builder.Services.AddSingleton<TextToSpeechService>();
        builder.Services.AddSingleton<ChatService>();
        builder.Services.AddSingleton<Func<string, RealtimeAudioService>>(sp =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<RealtimeModelsOptions>>();
            return key =>
            {
                var template = Options.Create(options.CurrentValue.Templates[key]);
                return ActivatorUtilities.CreateInstance<RealtimeAudioService>(sp, template);
            };
        });

        // Audio services
        builder.Services.AddSingleton<AudioSourceService>();
        builder.Services.AddSingleton<VadService>();
        builder.Services.AddTransient<AudioPlaybackService>();
        builder.Services.AddSingleton<Func<AudioPlaybackService>>(sp => () => sp.GetRequiredService<AudioPlaybackService>());
        builder.Services.AddTransient<AudioMixerService>();
        builder.Services.AddTransient<Func<AudioMixerService>>(sp => () => sp.GetRequiredService<AudioMixerService>());
        builder.Services.AddTransient<AudioSchedulerService>();
        builder.Services.AddSingleton<Func<AudioSchedulerService>>(sp => () => sp.GetRequiredService<AudioSchedulerService>());
        builder.Services.AddTransient<AudioStreamPlaybackService>();
        builder.Services.AddTransient<Func<AudioStreamPlaybackService>>(sp => () => sp.GetRequiredService<AudioStreamPlaybackService>());
        builder.Services.AddTransient<AudioPacerService>();
        builder.Services.AddTransient<Func<AudioPacerService>>(sp => () => sp.GetRequiredService<AudioPacerService>());

        // Realtime pipeline services (Mic -> Realtime -> Stream Playback)
        builder.Services.AddTransient<RealtimePipeline>();
        builder.Services.AddTransient<VoiceChatPipeline>();
        builder.Services.AddTransient<AgentToAgentRealtimePipeline>();

        using var host = builder.Build();

        // Setting up graceful shutdown on Ctrl+C
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine("Select pipeline:\n" +
            "[1] Classic (VAD->STT->Chat->TTS)\n" +
            "[2] Realtime (Mic->Realtime->Playback)\n" +
            "[3] Agent-to-Agent Realtime (Mic->Realtime->Playback)\n");
        Console.Write("\nEnter Pipeline id: ");

        var choice = Console.ReadLine();
        _ = int.TryParse(choice, out int mode);
        switch (mode)
        {
            case 1:
                Console.WriteLine("\nStarting Classic Voice Chat Pipeline (VAD->STT->Chat->TTS). Press Ctrl+C to stop.");
                var p1 = host.Services.GetRequiredService<VoiceChatPipeline>();
                await p1.RunAsync(cts.Token);
                break;

            case 2:
                Console.WriteLine("\nStarting Realtime Pipeline (Mic->Realtime->Playback). Press Ctrl+C to stop.");
                var p2 = host.Services.GetRequiredService<RealtimePipeline>();
                await p2.RunAsync(cts.Token);
                break;

            case 3:
                Console.WriteLine("\nStarting Agent-to-Agent Realtime Pipeline (Mic->Realtime->Playback). Press Ctrl+C to stop.");
                var p3 = host.Services.GetRequiredService<AgentToAgentRealtimePipeline>();
                await p3.RunAsync(cts.Token);
                break;

            default:
                Console.WriteLine("\nInvalid choice. Exiting.");
                return;
        }
    }

    private static void ConfigureOptions<TOptions>(this IServiceCollection services, string sectionName) where TOptions : class =>
            services
                .AddOptions<TOptions>()
                .BindConfiguration(sectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();
}
