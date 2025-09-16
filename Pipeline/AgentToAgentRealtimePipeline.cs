// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.Logging;

using NAudio.Mixer;

using System.Threading.Tasks.Dataflow;

public class AgentToAgentRealtimePipeline : IAsyncDisposable
{
    // Configuration constants aligned with VoiceChatPipeline style
    private const int MaxDegreeOfParallelism = 1;
    private const int BoundedCapacity = 512;
    private const bool EnsureOrdered = true;

    private readonly ExecutionDataflowBlockOptions _executionOptions = new()
    {
        MaxDegreeOfParallelism = MaxDegreeOfParallelism,
        BoundedCapacity = BoundedCapacity,
        EnsureOrdered = EnsureOrdered
    };

    private readonly DataflowLinkOptions _linkOptions = new() { PropagateCompletion = true };
    private readonly ILogger<RealtimePipeline> _logger;

    private readonly RealtimeAudioService _joy;
    private readonly RealtimeAudioService _sam;
    private readonly AudioPacerService _joyAudio;
    private readonly AudioPacerService _samAudio;
    private readonly AudioStreamPlaybackService _playbackJoy;
    private readonly AudioStreamPlaybackService _playbackSam;
    private readonly PipelineControlPlane _joyControlPlane = new();
    private readonly PipelineControlPlane _samControlPlane = new();
    private readonly AudioStreamPlaybackService _playback;

    private CancellationTokenSource? _cts;

    public AgentToAgentRealtimePipeline(
        ILogger<RealtimePipeline> logger,
        Func<AudioPacerService> createPacer,
        Func<string, RealtimeAudioService> createRealtime,
        Func<AudioMixerService> createMixer,
        Func<AudioStreamPlaybackService> createPlayback,
        AudioSchedulerService audioSchedulerService)
    {
        _logger = logger;
        _joy = createRealtime.Invoke("Joy");
        _sam = createRealtime.Invoke("Sam");
        _joyAudio = createPacer();
        _samAudio = createPacer();
        _playbackJoy = createPlayback();
        _playbackSam = createPlayback();
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Ensure realtime service started
        await _joy.StartAsync(_joyControlPlane, "Joy", _cts.Token).ConfigureAwait(false);
        await _sam.StartAsync(_samControlPlane, "Sam", _cts.Token).ConfigureAwait(false);

        _playbackJoy.ResetControlPlane(_joyControlPlane);
        _playbackJoy.Name = "Joy Playback";
        _playbackSam.ResetControlPlane(_samControlPlane);
        _playbackJoy.Name = "Sam Playback";

        // define block
        var playbackJoy = new ActionBlock<AudioEvent>(_playbackJoy.PipelineAction, _executionOptions);
        var playbackSam = new ActionBlock<AudioEvent>(_playbackSam.PipelineAction, _executionOptions);

        _joy.AudioOutput.LinkTo(_joyAudio.In, _linkOptions);
        _joy.AudioOutput.LinkTo(DataflowBlock.NullTarget<AudioEvent>(), _linkOptions);

        _sam.AudioOutput.LinkTo(_samAudio.In, _linkOptions);
        _sam.AudioOutput.LinkTo(DataflowBlock.NullTarget<AudioEvent>(), _linkOptions);

        _joyAudio.Out.LinkTo(_sam.AudioInput, _linkOptions);
        _samAudio.Out.LinkTo(_joy.AudioInput, _linkOptions);

        _joyAudio.Out.LinkTo(playbackJoy, _linkOptions);
        _joyAudio.Out.LinkTo(DataflowBlock.NullTarget<AudioEvent>(), _linkOptions);

        _samAudio.Out.LinkTo(playbackSam, _linkOptions);
        _samAudio.Out.LinkTo(DataflowBlock.NullTarget<AudioEvent>(), _linkOptions);

        _ = Task.Delay(TimeSpan.FromSeconds(2), _cts.Token).ContinueWith(t => 
        {
            _ = _joy.TriggerResponseAsync("Your are talking with Sam. Sam is your old fried you did not see for years. Ask him about his life!", _cts.Token);
        });

        _logger.LogInformation("Realtime pipeline started (Mic -> Realtime -> Playback). Press Ctrl+C to stop.");
        await playbackJoy.Completion;
    }

    private void Link<T>(
        ISourceBlock<PipelineEvent<T>> source,
        ITargetBlock<PipelineEvent<T>> target,
        string blockName,
        Func<T, bool> predicate)
    {
        source.LinkTo(target, this._linkOptions, evt => this.Filter(evt, blockName, predicate, source));
        this.DiscardFiltered(source, blockName);
    }

    private void DiscardFiltered<T>(ISourceBlock<PipelineEvent<T>> block, string blockName) => 
        block.LinkTo(DataflowBlock.NullTarget<PipelineEvent<T>>(), this._linkOptions, evt => this.FilterDiscarded(evt, blockName));

    public ValueTask DisposeAsync()
    {
        _joy.Dispose();
        _sam.Dispose();

        _cts?.Cancel();
        _cts?.Dispose();
        _playback?.Dispose();

        return ValueTask.CompletedTask;
    }

    private bool Filter<T>(PipelineEvent<T> evt, string blockName, Func<T, bool> predicate, IDataflowBlock block)
    {
        return true;
    }

    private bool FilterDiscarded<T>(PipelineEvent<T> evt, string blockName)
    {
        //this._logger.LogWarning($"{blockName} block: Event filtered out due to cancellation or empty.");
        return true;
    }
}
