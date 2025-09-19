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
    private Func<AudioMixerService> _createMixer;

    public AgentToAgentRealtimePipeline(
        ILogger<RealtimePipeline> logger,
        Func<AudioPacerService> createPacer,
        Func<string, RealtimeAudioService> createRealtime,
        Func<AudioMixerService> createMixer,
        Func<AudioStreamPlaybackService> createPlayback)
    {
        _logger = logger;
        _joy = createRealtime.Invoke("Joy");
        _sam = createRealtime.Invoke("Sam");
        _joyAudio = createPacer();
        _samAudio = createPacer();
        _playbackJoy = createPlayback();
        _playbackSam = createPlayback();
        _playback = createPlayback();
        _createMixer = createMixer;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await using var mixer = _createMixer();
        await mixer.StartAsync(cancellationToken);

        // Ensure realtime service started
        await _joy.StartAsync(null, null, _joyControlPlane, "Joy", true, _cts.Token).ConfigureAwait(false);
        await _sam.StartAsync(null, null, _samControlPlane, "Sam", true, _cts.Token).ConfigureAwait(false);
        _joy.ParticipantId = 0;
        mixer.Register(_joy.ParticipantId);
        _sam.ParticipantId = 1;
        mixer.Register(_sam.ParticipantId);

        _playbackJoy.ResetControlPlane(_joyControlPlane);
        _playbackJoy.Name = "Joy Playback";
        _playbackSam.ResetControlPlane(_samControlPlane);
        _playbackJoy.Name = "Sam Playback";

        // define block
        var playbackJoy = new ActionBlock<AudioEvent>(_playbackJoy.PipelineAction, _executionOptions);
        var playbackSam = new ActionBlock<AudioEvent>(_playbackSam.PipelineAction, _executionOptions);
        var playback = new ActionBlock<AudioEvent>(_playback.PipelineAction, _executionOptions);

        _joy.Out.LinkTo(_joyAudio.In, _linkOptions);
        _joy.Out.LinkTo(DataflowBlock.NullTarget<AudioEvent>(), _linkOptions);

        _sam.Out.LinkTo(_samAudio.In, _linkOptions);
        _sam.Out.LinkTo(DataflowBlock.NullTarget<AudioEvent>(), _linkOptions);

        _joyAudio.Out.LinkTo(_sam.In, _linkOptions);
        _samAudio.Out.LinkTo(_joy.In, _linkOptions);

        _joyAudio.Out.LinkTo(mixer.In, _linkOptions);
        _samAudio.Out.LinkTo(mixer.In, _linkOptions);

        mixer.Out.LinkTo(playback, _linkOptions);

        _ = Task.Delay(TimeSpan.FromSeconds(2), _cts.Token).ContinueWith(t => 
        {
            _ = _joy.TriggerResponseAsync("Please start conversation. You are in scrum meeting with Sam.", _cts.Token);
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
