// Copyright (c) Microsoft. All rights reserved.

using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

public class RealtimePipeline : IAsyncDisposable
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
    private readonly AudioPacerService _audioPacerService;
    private TurnManager _turnManager;
    private PipelineControlPlane _controlPlane;
    private readonly Kernel _kernel;
    private readonly ConversationalPlugin _conversationalPlugin;

    private CancellationTokenSource? _cts;

    public RealtimePipeline(
        ILogger<RealtimePipeline> logger,
        AudioSourceService audioSourceService,
        Func<AudioPacerService> createAudioPacer,
        Func<string, RealtimeAudioService> realtimeFactory,
        AudioStreamPlaybackService audioStreamPlaybackService,
        Kernel kernel,
        ConversationalPlugin conversationalPlugin,
        PipelineControlPlane controlPlane)
    {
        _logger = logger;
        _audioSourceService = audioSourceService;
        _realtimeAudioService = realtimeFactory.Invoke("Joy");
        _audioStreamPlaybackService = audioStreamPlaybackService;
        _audioPacerService = createAudioPacer.Invoke();
        _controlPlane = controlPlane;
        _turnManager = controlPlane.TurnManager;
        _kernel = kernel;
        _conversationalPlugin = conversationalPlugin;
    }

    public bool AutoResponse { get; set; } = true;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Ensure realtime service started
        await StartRealtimeSessionAsync(_realtimeAudioService, _conversationalPlugin).ConfigureAwait(false);

        // Reconfigure to be compatible with Realtime API, which is 24000 Hz, mono, 16-bit PCM audio
        _audioSourceService.Configure(24000, 1, 16);

        var audioEventBlock = new TransformBlock<byte[], AudioEvent>(chunk => new AudioEvent(_turnManager.CurrentTurnId, _turnManager.CurrentToken, new AudioData(chunk, 24000, 1, 16)), _executionOptions);
        var realtimeIn = _realtimeAudioService.In;
        var realtimeOut = _realtimeAudioService.Out;
        var pacerIn = _audioPacerService.In;
        var pacerOut = _audioPacerService.Out;
        var playback = new ActionBlock<AudioEvent>(_audioStreamPlaybackService.PipelineAction, _executionOptions);

        // Link internal blocks
        audioEventBlock.LinkTo(realtimeIn, _linkOptions);
        audioEventBlock.LinkTo(DataflowBlock.NullTarget<AudioEvent>(), _linkOptions); // discard filtered (none currently)

        realtimeOut.LinkTo(pacerIn, _linkOptions);
        realtimeOut.LinkTo(DataflowBlock.NullTarget<AudioEvent>(), _linkOptions);

        pacerOut.LinkTo(playback, _linkOptions);
        pacerOut.LinkTo(DataflowBlock.NullTarget<AudioEvent>(), _linkOptions);

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

    public async ValueTask DisposeAsync()
    {
        await _audioPacerService.DisposeAsync();
        _realtimeAudioService.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        _audioStreamPlaybackService?.Dispose();
    }

    private async Task StartRealtimeSessionAsync(RealtimeAudioService service, ConversationalPlugin plugin)
    {
        if (AutoResponse)
        {
            await service.StartAsync(null, null, _controlPlane, "", AutoResponse, _cts!.Token).ConfigureAwait(false);
        }
        else
        {
            plugin.RealtimeService = service;
            plugin.Logger = _logger;
            var kernelPlugin = KernelPluginFactory.CreateFromObject(plugin, pluginName: nameof(ConversationalPlugin));
            _kernel.Plugins.Add(kernelPlugin);
            await service.StartAsync(_kernel, kernelPlugin, _controlPlane, "", AutoResponse, _cts!.Token).ConfigureAwait(false);
        }
    }
}
