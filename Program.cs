// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;

internal static class Program
{
    internal static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Adding configuration from appsettings.json and environment variables
        builder.Services.ConfigureOptions<OpenAIOptions>(OpenAIOptions.SectionName);
        builder.Services.ConfigureOptions<ChatOptions>(ChatOptions.SectionName);

        // Configure Semantic Kernel in DI container
        builder.Services
            .AddKernel()
            .AddOpenAIChatCompletion(
                modelId: builder.Configuration[$"{OpenAIOptions.SectionName}:ChatModelId"]!,
                apiKey: builder.Configuration[$"{OpenAIOptions.SectionName}:ApiKey"]!
            );

        // Register shared services
        builder.Services.AddSingleton<AudioSourceService>();
        builder.Services.AddSingleton<PipelineControlPlane>();
        builder.Services.AddSingleton<AudioSchedulerService>();

        // Classic multi-stage pipeline services (VAD -> STT -> Chat -> TTS -> Playback)
        builder.Services.AddSingleton<AudioPlaybackService>();
        builder.Services.AddSingleton<SpeechToTextService>();
        builder.Services.AddSingleton<TextToSpeechService>();
        builder.Services.AddSingleton<ChatService>();
        builder.Services.AddSingleton<VadService>();
        builder.Services.AddTransient<VoiceChatPipeline>();

        // Realtime pipeline services (Mic -> Realtime -> Stream Playback)
        builder.Services.AddSingleton<RealtimeAudioService>();
        builder.Services.AddSingleton<AudioStreamPlaybackService>();
        builder.Services.AddTransient<RealtimePipeline>();

        using var host = builder.Build();

        // Setting up graceful shutdown on Ctrl+C
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine("Select pipeline:\n[1] Classic (VAD->STT->Chat->TTS)\n[2] Realtime (Mic->Realtime->Playback)");
        Console.Write("\nEnter 1 or 2 (default 1): ");
        var choice = Console.ReadLine();
        var mode = (choice?.Trim() == "2") ? 2 : 1;

        if (mode == 1)
        {
            var pipeline = host.Services.GetRequiredService<VoiceChatPipeline>();
            await pipeline.RunAsync(cts.Token);
        }
        else
        {
            var pipeline = host.Services.GetRequiredService<RealtimePipeline>();
            await pipeline.RunAsync(cts.Token);
        }
    }

    private static void ConfigureOptions<TOptions>(this IServiceCollection services, string sectionName) where TOptions : class =>
            services
                .AddOptions<TOptions>()
                .BindConfiguration(sectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();
}
