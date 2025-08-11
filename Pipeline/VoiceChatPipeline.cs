// Copyright (c) Microsoft. All rights reserved.

using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class VoiceChatPipeline : IDisposable
{
    // Pipeline configuration constants
    private const int MaxDegreeOfParallelism = 1;    // Number of parallel operations in dataflow blocks
    private const int BoundedCapacity = 5;           // Maximum capacity for dataflow block buffers
    private const bool EnsureOrdered = true;         // Ensure order preservation in pipeline
    
    // Dataflow options fields - initialized inline
    private readonly ExecutionDataflowBlockOptions _executionOptions = new()
    {
        MaxDegreeOfParallelism = MaxDegreeOfParallelism,
        BoundedCapacity = BoundedCapacity,
        EnsureOrdered = EnsureOrdered
    };

    private readonly DataflowLinkOptions _linkOptions = new()
    { 
        PropagateCompletion = true 
    };
    
    private readonly ILogger<VoiceChatPipeline> _logger;
    private readonly AudioPlaybackService _audioPlaybackService;
    private readonly SpeechToTextService _speechToTextService;
    private readonly TextToSpeechService _textToSpeechService;
    private readonly ChatService _chatService;
    private readonly TurnManager _turnManager;
    private readonly VadService _vadService;
    private readonly AudioSourceService _audioSourceService;

    private CancellationTokenSource? _cancellationTokenSource;

    public VoiceChatPipeline(
        ILogger<VoiceChatPipeline> logger,
        AudioPlaybackService audioPlaybackService,
        SpeechToTextService speechToTextService,
        TextToSpeechService textToSpeechService,
        ChatService chatService,
        VadService vadService,
        AudioSourceService audioSourceService,
        TurnManager turnManager,
        IOptions<AudioOptions> audioOptions)
    {
        _logger = logger;
        _audioPlaybackService = audioPlaybackService;
        _speechToTextService = speechToTextService;
        _textToSpeechService = textToSpeechService;
        _chatService = chatService;
        _vadService = vadService;
        _audioSourceService = audioSourceService;
        _turnManager = turnManager;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Create pipeline blocks - VAD now accepts raw audio chunks directly
        var vadBlock = DataflowBlocks.TransformToManyAsync<byte[], AudioEvent>(_vadService.TransformAsync, _executionOptions, logger: _logger);
        var sttBlock = new TransformBlock<AudioEvent, TranscriptionEvent>(_speechToTextService.TransformAsync, _executionOptions);
        var chatBlock = DataflowBlocks.TransformToManyAsync<TranscriptionEvent, ChatEvent>(_chatService.TransformAsync, _executionOptions, logger: _logger);
        var ttsBlock = new TransformBlock<ChatEvent, SpeechEvent>(_textToSpeechService.TransformAsync, _executionOptions);
        var playbackBlock = new ActionBlock<SpeechEvent>(_audioPlaybackService.PipelineActionAsync, _executionOptions);

        // Connect the blocks in the pipeline
        LinkWithFilter(vadBlock, sttBlock, "VAD", audioData => audioData.Data.Length > 0);
        LinkWithFilter(sttBlock, chatBlock, "STT", t => !string.IsNullOrEmpty(t));
        LinkWithFilter(chatBlock, ttsBlock, "Chat", t => !string.IsNullOrEmpty(t));
        LinkWithFilter(ttsBlock, playbackBlock, "TTS", t => t.Length > 0);

        _logger.LogInformation("Voice Chat started. You can start conversation now, or press Ctrl+C to exit.");

        try
        {
            // Feed audio chunks directly into the VAD pipeline block
            await foreach (var audioChunk in _audioSourceService.GetAudioChunksAsync(_cancellationTokenSource.Token))
            {
                await vadBlock.SendAsync(audioChunk, _cancellationTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInterrupted();
        }
        finally
        {
            vadBlock.Complete();
            await playbackBlock.Completion;
        }
    }

    public void Dispose()
    {
        _vadService?.Dispose();
        _cancellationTokenSource?.Dispose();
    }

    // Generic filter methods for pipeline events
    private bool Filter<T>(PipelineEvent<T> evt, string blockName, Func<T, bool> predicate, IDataflowBlock block)
    {
        var valid = PipelineEvent<T>.IsValid(evt, _turnManager.CurrentTurnId, predicate);
        if (!valid)
        {
            _logger.LogWarning($"{blockName} block: Event filtered out due to cancellation or empty payload.");
        }
        return valid;
    }

    private bool FilterDiscarded<T>(PipelineEvent<T> evt, string blockName)
    {
        _logger.LogWarning($"{blockName} block: Event filtered out due to cancellation or empty.");
        return true;
    }

    private void LinkWithFilter<T>(
        ISourceBlock<PipelineEvent<T>> source, 
        ITargetBlock<PipelineEvent<T>> target, 
        string blockName, 
        Func<T, bool> predicate)
    {
        source.LinkTo(target, _linkOptions, evt => Filter(evt, blockName, predicate, source));
        DiscardFiltered(source, blockName);
    }

    private void DiscardFiltered<T>(ISourceBlock<PipelineEvent<T>> block, string blockName) =>
        block.LinkTo(DataflowBlock.NullTarget<PipelineEvent<T>>(), _linkOptions, evt => FilterDiscarded(evt, blockName));
}