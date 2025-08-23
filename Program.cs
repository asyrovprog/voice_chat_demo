// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

internal static class Program
{
    internal static async Task Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddOptions<OpenAIOptions>()
            .Bind(builder.Configuration.GetSection(OpenAIOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddOptions<ChatOptions>()
            .Bind(builder.Configuration.GetSection(ChatOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AudioOptions no longer needs configuration binding - uses constants

        // Configure Semantic Kernel in DI container
        builder.Services
            .AddKernel()
            .AddOpenAIChatCompletion(
                modelId: builder.Configuration[$"{OpenAIOptions.SectionName}:ChatModelId"]!,
                apiKey: builder.Configuration[$"{OpenAIOptions.SectionName}:ApiKey"]!
            );

        // Register audio chat pipeline services
        builder.Services.AddSingleton<AudioPlaybackService>();
        builder.Services.AddSingleton<SpeechToTextService>();
        builder.Services.AddSingleton<TextToSpeechService>();
        builder.Services.AddSingleton<ChatService>();
        builder.Services.AddSingleton<TurnManager>();
        builder.Services.AddSingleton<VadService>();
        builder.Services.AddSingleton<AudioSourceService>();

        // Register audio chat pipeline
        builder.Services.AddTransient<VoiceChatPipeline>();

        using var host = builder.Build();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var pipeline = host.Services.GetRequiredService<VoiceChatPipeline>();
        await pipeline.RunAsync(cts.Token);
    }
}
