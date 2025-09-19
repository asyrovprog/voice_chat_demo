// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

using NAudio.Mixer;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks.Dataflow;

using WebRtcVadSharp;

public class HumanWithAgentsRealtimePipeline : IAsyncDisposable
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
    private readonly AudioSourceService _audioSource;
    private readonly Kernel _joyKernel;
    private readonly Kernel _samKernel;
    private readonly ConversationalPlugin _joyPlugin;
    private readonly ConversationalPlugin _samPlugin;
    private readonly WebRtcVad _vad = new() { OperatingMode = OperatingMode.VeryAggressive };


    private CancellationTokenSource? _cts;
    private Func<AudioMixerService> _createMixer;

    public HumanWithAgentsRealtimePipeline(
        ILogger<RealtimePipeline> logger,
        Func<AudioPacerService> createPacer,
        Func<string, RealtimeAudioService> createRealtime,
        Func<AudioMixerService> createMixer,
        AudioSourceService audioSource,
        Func<Kernel> createKernel,
        Func<ConversationalPlugin> createPlugin,
        Func<AudioStreamPlaybackService> createPlayback)
    {
        _logger = logger;
        _joyKernel = createKernel();
        _samKernel = createKernel();
        _joyPlugin = createPlugin();
        _samPlugin = createPlugin();
        _joy = createRealtime.Invoke("Joy");
        _sam = createRealtime.Invoke("Sam");
        _joyAudio = createPacer();
        _samAudio = createPacer();
        _playbackJoy = createPlayback();
        _playbackSam = createPlayback();
        _playback = createPlayback();
        _createMixer = createMixer;
        _audioSource = audioSource;
    }

    public bool AutoResponse { get; set; } = true;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await using var mixer = _createMixer();
        await using var mixerJoy = _createMixer();
        await using var mixerSam = _createMixer();

        await mixer.StartAsync(cancellationToken);
        await mixerJoy.StartAsync(cancellationToken);
        await mixerSam.StartAsync(cancellationToken);

        await StartRealtimeSessionAsync(_joy, _joyKernel, _joyPlugin, "Beta", _joyControlPlane).ConfigureAwait(false);
        await StartRealtimeSessionAsync(_sam, _samKernel, _samPlugin, "Sam", _samControlPlane).ConfigureAwait(false);
        _joy.ParticipantId = 0;
        _sam.ParticipantId = 1;
        var userParticipantId = 2;

        mixer.Register(_joy.ParticipantId);
        mixer.Register(_sam.ParticipantId);
        mixerJoy.Register(_sam.ParticipantId);
        mixerJoy.Register(userParticipantId);
        mixerSam.Register(_joy.ParticipantId);
        mixerSam.Register(userParticipantId);

        _audioSource.Configure(24000, 1, 16);

        var userAudio = new TransformBlock<byte[], AudioEvent>(chunk =>
            new AudioEvent(
                _joyControlPlane.TurnManager.CurrentTurnId, 
                _joyControlPlane.TurnManager.CurrentToken, 
                new AudioData(chunk, 24000, 1, 16, Participant: userParticipantId)), _executionOptions);

        _playbackJoy.ResetControlPlane(_joyControlPlane);
        _playbackJoy.Name = "Joy";
        _playbackSam.ResetControlPlane(_samControlPlane);
        _playbackJoy.Name = "Sam";

        var user = new BroadcastBlock<AudioEvent>(e => e);
        userAudio.LinkTo(user, _linkOptions);
        user.LinkTo(DataflowBlock.NullTarget<AudioEvent>(), _linkOptions);

        var playback = new ActionBlock<AudioEvent>(_playback.PipelineAction, _executionOptions);

        mixerJoy.Out.LinkTo(_joy.In, _linkOptions);
        mixerSam.Out.LinkTo(_sam.In, _linkOptions);

        _joy.Out.LinkTo(_joyAudio.In, _linkOptions);
        _joy.Out.LinkTo(DataflowBlock.NullTarget<AudioEvent>(), _linkOptions);

        _sam.Out.LinkTo(_samAudio.In, _linkOptions);
        _sam.Out.LinkTo(DataflowBlock.NullTarget<AudioEvent>(), _linkOptions);

        _joyAudio.Out.LinkTo(mixerSam.In, _linkOptions);
        user.LinkTo(mixerSam.In, _linkOptions);

        _samAudio.Out.LinkTo(mixerJoy.In, _linkOptions);
        user.LinkTo(mixerJoy.In, _linkOptions);

        _joyAudio.Out.LinkTo(mixer.In, _linkOptions);
        _samAudio.Out.LinkTo(mixer.In, _linkOptions);

        mixer.Out.LinkTo(playback, _linkOptions);

        _logger.LogInformation("Realtime pipeline started (Mic -> Realtime -> Playback). Press Ctrl+C to stop.");
        try
        {
            // Keep feeding audio chunks into the VAD pipeline block till RunAsync is not cancelled
            var voicedFrameCount = 0;
            await foreach (var audioChunk in this._audioSource.GetAudioChunksAsync(_cts.Token))
            {
                if (HasVoice(audioChunk))
                {
                    voicedFrameCount++;
                    if (voicedFrameCount == 5)
                    {
                        PipelineControlPlane.PublishToAll(new PipelineControlPlane.ActiveSpeaker("Andrew"));
                    }
                }
                else
                {
                    voicedFrameCount = 0;
                }
                _ = userAudio.Post(audioChunk);
            }
        }
        catch (OperationCanceledException)
        {
            this._logger.LogInformation("Voice Chat pipeline stopping due to cancellation...");
        }
        finally
        {
            await playback.Completion;
        }
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

    private async Task StartRealtimeSessionAsync(RealtimeAudioService service, Kernel kernel, ConversationalPlugin plugin, string name, PipelineControlPlane controlPlane)
    {
        if (AutoResponse)
        {
            await service.StartAsync(null, null, controlPlane, name, AutoResponse, _cts!.Token).ConfigureAwait(false);
        }
        else
        {
            plugin.RealtimeService = service;
            plugin.Logger = _logger;
            var kernelPlugin = KernelPluginFactory.CreateFromObject(plugin, pluginName: nameof(ConversationalPlugin));
            kernel.Plugins.Add(kernelPlugin);
            await service.StartAsync(kernel, kernelPlugin, controlPlane, name, AutoResponse, _cts!.Token).ConfigureAwait(false);
        }
    }

    private bool HasVoice(byte[] src)
    {
        var dst = new byte[src.Length * 2];
        // Guard: need even byte counts (16-bit samples)
        if ((src.Length & 1) == 1) src = src[..(src.Length - 1)];
        var sIn = MemoryMarshal.Cast<byte, short>(src);           // interpret as 16-bit samples
        var sOut = MemoryMarshal.Cast<byte, short>(dst);           // where we write samples

        if (sOut.Length < sIn.Length * 2) throw new ArgumentException("dst too small");

        int j = 0;
        for (int i = 0; i < sIn.Length - 1; i++)
        {
            short a = sIn[i];
            short b = sIn[i + 1];
            sOut[j++] = a;                      // original
            sOut[j++] = (short)((a + b) / 2);   // simple linear interpolation
        }

        // Pad the last pair
        short last = sIn[^1];
        sOut[j++] = last;
        sOut[j++] = last;

        return _vad.HasSpeech(dst, SampleRate.Is48kHz, FrameLength.Is20ms);
    }
}
