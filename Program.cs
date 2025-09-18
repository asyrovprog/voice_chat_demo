// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

using System.Security.Cryptography.X509Certificates;

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
        builder.Services.AddTransient<Func<Kernel>>(sp => () => sp.GetRequiredService<Kernel>());
        builder.Services.AddTransient<ConversationalPlugin>();
        builder.Services.AddTransient<Func<ConversationalPlugin>>(sp => () => sp.GetRequiredService<ConversationalPlugin>());

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
        builder.Services.AddTransient<AudioStreamPlaybackService>();
        builder.Services.AddTransient<Func<AudioStreamPlaybackService>>(sp => () => sp.GetRequiredService<AudioStreamPlaybackService>());
        builder.Services.AddTransient<AudioPacerService>();
        builder.Services.AddTransient<Func<AudioPacerService>>(sp => () => sp.GetRequiredService<AudioPacerService>());

        // Realtime pipeline services (Mic -> Realtime -> Stream Playback)
        builder.Services.AddTransient<RealtimePipeline>();
        builder.Services.AddTransient<VoiceChatPipeline>();
        builder.Services.AddTransient<AgentToAgentRealtimePipeline>();
        builder.Services.AddTransient<HumanWithAgentsRealtimePipeline>();

        using var host = builder.Build();

        // Setting up graceful shutdown on Ctrl+C
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine("Select pipeline:\n" +
            "[1] Classic STT->Chat->TTS\n" +
            "[2] Realtime: Automatic Response enabled.\n" +
            "[3] Realtime: Probabilistic Response based on context.\n" +
            "[4] Realtime: 2 Agents talk to each other with Automatic Response.\n" +
            "[5] Realtime: Human and 2 Agents. Probabilistic Response based on context.\n");
        Console.Write("\nEnter Pipeline id: ");

        var choice = Console.ReadLine();
        var color = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        _ = int.TryParse(choice, out int mode);
        Task? t = null;
        switch (mode)
        {
            case 1:
                Console.WriteLine(
                    "\nClassic Voice Chat Pipeline (VAD->STT->Chat->TTS):" +
                    "\n- Slow, yet simple and inexpective." +
                    "\n- You can interrupt agent by voice." +
                    "\n\nHave fun!\n\n");

                var p1 = host.Services.GetRequiredService<VoiceChatPipeline>();
                t = p1.RunAsync(cts.Token);
                break;

            case 2:
                Console.WriteLine(
                    "\nAgent (fictionary coding agent Beta) and human." +
                    "\n- Automatic turn detection based on speech activity." +
                    "\n- No intelligent response. Such as still respond to 'Please keep quiet.'" + 
                    "\n- You can interrupt agent by voice." +
                    "\n\nHave fun!\n\n");

                var p2 = host.Services.GetRequiredService<RealtimePipeline>();
                t = p2.RunAsync(cts.Token);
                break;

            case 3:
                Console.WriteLine(
                    "\nAgent (fictionary coding agent Beta) and human." +
                    "\n- Automatic turn detection based on speech activity." +
                    "\n- More intelligent engagement based on estimation of probability to respond." +
                    "\nYou can interrupt agent by voice." +
                    "\n\nHave fun!\n\n");

                var p2_1 = host.Services.GetRequiredService<RealtimePipeline>();
                p2_1.AutoResponse = false;
                t = p2_1.RunAsync(cts.Token);
                break;

            case 4:
                Console.WriteLine(
                    "\n2 Agents (fictionary coding agents Beta and Sam) Realtime Pipeline." +
                    "\nAutomatic turn detection based on voice activity." +
                    "\nAgents CANNOT hear you they just talk to each other." +
                    "\n\nHave fun!\n\n");

                var p3 = host.Services.GetRequiredService<AgentToAgentRealtimePipeline>();
                t = p3.RunAsync(cts.Token);
                break;

            case 5:
                Console.WriteLine(
                    "\n2 Agents (fictionary coding agents Beta and Sam) and Human Pipeline." +
                    "\n- Automatic turn detection based on voice activity." +
                    "\n- More intelligent engagement based on estimation of probability to respond." +
                    "\n- Agents can hear you." +
                    "\n- You can interrupt agent by voice." +
                    "\n\nHave fun!\n\n");

                var p4 = host.Services.GetRequiredService<HumanWithAgentsRealtimePipeline>();
                p4.AutoResponse = false;
                await p4.RunAsync(cts.Token);
                break;

            default:
                Console.WriteLine("\nInvalid choice. Exiting.");
                return;
        }

        if (t != null)
        {
            Console.ForegroundColor = color;
            await t;
            Console.WriteLine("\nExiting. Bye!");
        }
    }

    private static void ConfigureOptions<TOptions>(this IServiceCollection services, string sectionName) where TOptions : class =>
            services
                .AddOptions<TOptions>()
                .BindConfiguration(sectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();
}
