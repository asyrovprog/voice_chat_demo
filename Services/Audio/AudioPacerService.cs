using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Dataflow;

public sealed class AudioPacerService : IAsyncDisposable
{
    private readonly ActionBlock<AudioEvent> _in;
    private readonly BroadcastBlock<AudioEvent> _out;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _warmupMs;
    private readonly int _lateToleranceMs;

    private CancellationTokenSource _shutdown = new();
    private int _currentTurn = -1;
    private TimeSpan _nextStart;
    private ILogger<AudioPacerService> _logger;

    public AudioPacerService(
        ILogger<AudioPacerService> logger,
        int warmupMs = 60, int lateToleranceMs = 10, int inCapacity = 512)
    {
        _logger = logger;
        _warmupMs = warmupMs;
        _lateToleranceMs = lateToleranceMs;

        _out = new BroadcastBlock<AudioEvent>(c => c,
            new DataflowBlockOptions
            {
                MaxMessagesPerTask = DataflowBlockOptions.Unbounded,
                EnsureOrdered = true,
                BoundedCapacity = DataflowBlockOptions.Unbounded
            });

        _in = new ActionBlock<AudioEvent>(PaceAndPostAsync,
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = 1,
                EnsureOrdered = true,
                BoundedCapacity = inCapacity
            });

        // Relay completion and faults from In → Out
        _in.Completion.ContinueWith(t =>
        {
            if (t.IsFaulted)
                ((IDataflowBlock)_out).Fault(t.Exception!.Flatten());
            else
                _out.Complete();
        }, TaskScheduler.Default);
    }

    public ITargetBlock<AudioEvent> In => _in;

    public ISourceBlock<AudioEvent> Out => _out;

    private async Task PaceAndPostAsync(AudioEvent audio)
    {
        if (audio.TurnId < _currentTurn)
        {
            _logger.LogWarning("Dropping audio from old turn {TurnId} < {_currentTurn}. Participant: {ParticipantId}", audio.TurnId, _currentTurn, audio.Payload.Participant);
            return;
        }
        if (audio.CancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Cancelling audio. Participant: {ParticipantId}", audio.Payload.Participant);
            return;
        }

        if (audio.TurnId > _currentTurn)
        {
            _currentTurn = audio.TurnId;
            _nextStart = _clock.Elapsed + TimeSpan.FromMilliseconds(_warmupMs);
        }

        var wait = _nextStart - _clock.Elapsed;
        if (wait > TimeSpan.Zero)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, audio.CancellationToken);
            try { await Task.Delay(wait, linked.Token).ConfigureAwait(true); } catch (OperationCanceledException) 
            {
                _logger.LogWarning("Cancelling audio delay. Participant: {ParticipantId}", audio.Payload.Participant);
                return; 
            }
        }
        else if (wait < TimeSpan.FromMilliseconds(-_lateToleranceMs))
        {
            _nextStart = _clock.Elapsed;
        }

        if (audio.CancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Cancelling audio after wait. Participant: {ParticipantId}", audio.Payload.Participant);
            return;
        }

        _nextStart += audio.Payload.Duration;
        _ = _out.Post(audio);
    }

    public async ValueTask DisposeAsync()
    {
        try { _in.Complete(); } catch { }

        _shutdown.Cancel();
        try { await _in.Completion.ConfigureAwait(false); } catch { }

        _shutdown.Dispose();
    }
}
