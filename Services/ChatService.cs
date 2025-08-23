// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Runtime.CompilerServices;

public class ChatService
{
    private readonly ILogger<ChatService> _logger;
    private readonly IChatCompletionService _chatCompletionService;
    private readonly ChatHistory _chatHistory;
    private readonly OpenAIPromptExecutionSettings _options;
    private readonly ChatOptions _chatOptions;

    public ChatService(ILogger<ChatService> logger, IChatCompletionService chatCompletionService, IOptions<ChatOptions> chatOptions)
    {
        _logger = logger;
        _chatCompletionService = chatCompletionService;
        _chatOptions = chatOptions.Value;

        _options = new OpenAIPromptExecutionSettings
        {
            Temperature = _chatOptions.Temperature,
            MaxTokens = _chatOptions.MaxTokens,
            TopP = _chatOptions.TopP
        };

        // Initialize chat history with system message from configuration
        _chatHistory = new ChatHistory();
        _chatHistory.AddSystemMessage(_chatOptions.SystemMessage);
    }

    /// <summary>
    /// Pipeline integration method for processing transcription events into chat responses.
    /// </summary>
    public async IAsyncEnumerable<ChatEvent> TransformAsync(TranscriptionEvent evt)
    {
        await foreach (var response in GetResponseStreamAsync(evt.Payload!, evt.CancellationToken).ConfigureAwait(false))
        {
            yield return new ChatEvent(evt.TurnId, evt.CancellationToken, response);
        }
    }

    private async IAsyncEnumerable<string> GetResponseStreamAsync(
        string input,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            yield break;
        }

        var buffer = "";
        _logger.LogInformation($"USER: {input}");
        _chatHistory.AddUserMessage(input);

        await foreach (var result in _chatCompletionService.GetStreamingChatMessageContentsAsync(_chatHistory, _options, cancellationToken: token))
        {
            buffer += result?.Content ?? string.Empty;
            if (buffer.Length >= _chatOptions.StreamingChunkSizeThreshold && (buffer[^1] == '.' || buffer[^1] == '?' || buffer[^1] == '!'))
            {
                _logger.LogInformation($"LLM delta: {buffer}");
                yield return buffer;
                buffer = string.Empty;
            }
        }

        if (!string.IsNullOrWhiteSpace(buffer))
        {
            _logger.LogInformation($"LLM delta: {buffer}");
            yield return buffer;
        }
    }
}