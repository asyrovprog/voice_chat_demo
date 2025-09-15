// Copyright (c) Microsoft. All rights reserved.

using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;

public class RealtimePipeline : IDisposable
{
    // Configuration constants aligned with VoiceChatPipeline style
    private const int MaxDegreeOfParallelism = 1;
    private const int BoundedCapacity = 50;
    private const bool EnsureOrdered = true;

    private readonly ExecutionDataflowBlockOptions _executionOptions = new()
    {
        MaxDegreeOfParallelism = MaxDegreeOfParallelism,
        BoundedCapacity = BoundedCapacity,
        EnsureOrdered = EnsureOrdered
    };

    private readonly DataflowLinkOptions _linkOptions = new() { PropagateCompletion = true };
    private readonly ILogger<RealtimePipeline> _logger;
    private readonly AudioSourceService _audioSourceService;
    private readonly RealtimeAudioService _realtimeAudioService;
    private readonly AudioStreamPlaybackService _audioStreamPlaybackService;
    private readonly AudioSchedulerService _audioSchedulerService;
    private readonly TurnManager _turnManager; // reserved for future turn-based interruption
    private readonly PipelineControlPlane _controlPlane;

    private CancellationTokenSource? _cts;

    public RealtimePipeline(
        ILogger<RealtimePipeline> logger,
        AudioSourceService audioSourceService,
        RealtimeAudioService realtimeAudioService,
        AudioStreamPlaybackService audioStreamPlaybackService,
        AudioSchedulerService audioSchedulerService,
        PipelineControlPlane controlPlane)
    {
        _logger = logger;
        _audioSourceService = audioSourceService;
        _realtimeAudioService = realtimeAudioService;
        _audioStreamPlaybackService = audioStreamPlaybackService;
        _audioSchedulerService = audioSchedulerService;
        _controlPlane = controlPlane;
        _turnManager = controlPlane.TurnManager;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Ensure realtime service started
        await _realtimeAudioService.StartAsync(_cts.Token).ConfigureAwait(false);

        // Reconfigure to be compatible with Realtime API, which is 24000 Hz, mono, 16-bit PCM audio
        _audioSourceService.Configure(24000, 1, 16);

        var audioEventBlock = new TransformBlock<byte[], AudioEvent>(chunk => new AudioEvent(_turnManager.CurrentTurnId, _turnManager.CurrentToken, new AudioData(chunk, 24000, 1, 16)), _executionOptions);
        var realtimeIn = _realtimeAudioService.AudioInput;
        var realtimeOut = _realtimeAudioService.AudioOutput;
        var audioSchedulerIn = _audioSchedulerService.AudioInput;
        var audioSchedulerOut = _audioSchedulerService.AudioOutput;
        var playback = new ActionBlock<AudioEvent>(_audioStreamPlaybackService.PipelineActionAsync, _executionOptions);

        // Link internal blocks
        audioEventBlock.LinkTo(realtimeIn, _linkOptions);
        audioEventBlock.LinkTo(DataflowBlock.NullTarget<AudioEvent>(), _linkOptions); // discard filtered (none currently)

        realtimeOut.LinkTo(audioSchedulerIn, _linkOptions);
        realtimeOut.LinkTo(DataflowBlock.NullTarget<AudioEvent>(), _linkOptions);

        audioSchedulerOut.LinkTo(playback, _linkOptions);
        audioSchedulerOut.LinkTo(DataflowBlock.NullTarget<AudioEvent>(), _linkOptions);

        _logger.LogInformation("Realtime pipeline started (Mic -> Realtime -> Playback). Press Ctrl+C to stop.");

        try
        {
            await foreach (var chunk in _audioSourceService.GetAudioChunksAsync(_cts.Token).ConfigureAwait(false))
            {
                await audioEventBlock.SendAsync(chunk, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Realtime pipeline stopping due to cancellation...");
        }
        finally
        {
            audioEventBlock.Complete();
            await playback.Completion.ConfigureAwait(false);
            _logger.LogInformation("Realtime pipeline completed.");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _audioStreamPlaybackService?.Dispose();
    }
}
