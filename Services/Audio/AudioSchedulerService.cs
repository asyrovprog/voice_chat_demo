using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;

public sealed class AudioSchedulerService : IAsyncDisposable
{
    private const int MarginMilliseconds = 15;
    private readonly TurnManager _turnManager;
    private readonly ILogger _logger;

    private readonly ActionBlock<AudioEvent> _input;
    private readonly BroadcastBlock<AudioEvent> _output;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock;
    private readonly ConcurrentQueue<AudioEvent> _pendingAudio = new();

    private CancellationTokenSource _waitCts = new();
    private Task? _streamingTask;
    private int _currentTurn = -1;
    private TimeSpan _nextStart = TimeSpan.Zero;

    public AudioSchedulerService(ILogger<AudioSchedulerService> logger, PipelineControlPlane controlPlane)
    {
        _logger = logger;
        _turnManager = controlPlane.TurnManager;
        _clock = Stopwatch.StartNew();

        _input = new ActionBlock<AudioEvent>(evt =>
        {
            Interrupt(evt.TurnId);
            _pendingAudio.Enqueue(evt);
            _signal.Release();
        },
        new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 1,
            BoundedCapacity = 512,
            EnsureOrdered = true
        });

        _output = new BroadcastBlock<AudioEvent>(e => e);

        _turnManager.OnTurnInterrupted += newTurnId => Interrupt(newTurnId);
        _streamingTask = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
    }

    public ITargetBlock<AudioEvent> AudioInput => _input;

    public ISourceBlock<AudioEvent> AudioOutput => _output;

    public void Complete()
    {
        _input.Complete();
        _signal.Release();
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_signal.Wait(0, token))
                {
                    await _signal.WaitAsync(token).ConfigureAwait(false);
                }

                // pacing
                TimeSpan startAt;
                CancellationToken abortToken;
                lock (_gate)
                {
                    startAt = _nextStart;
                    abortToken = _waitCts.Token;
                }
                var now = _clock.Elapsed;
                TimeSpan wait = startAt - now;

                if (wait > TimeSpan.FromMilliseconds(MarginMilliseconds))
                {
                    var delayToken = CancellationTokenSource.CreateLinkedTokenSource(token, abortToken).Token;
                    try
                    {
                        await Task.Delay(wait, delayToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        continue;
                    }
                }

                token.ThrowIfCancellationRequested();
                if (_pendingAudio.TryDequeue(out var audio))
                {
                    if (!_output.Post(audio))
                    {
                        _logger.LogWarning("AudioSchedulerService: Failed to post to output audio chunk.");
                    }

                    // NOTE: we must start the next slightly before the end of the current to avoid gaps
                    _nextStart = startAt + audio.Payload.Duration - TimeSpan.FromMilliseconds(MarginMilliseconds);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _output.Complete();
            _clock?.Stop();
        }
    }

    private void Interrupt(int? currentTurn = null)
    {
        CancellationTokenSource cts;

        lock (_gate)
        {
            if (currentTurn is not null && currentTurn == _currentTurn) return;

            _currentTurn = currentTurn ?? _currentTurn;
            ClearPendingAudio();
            _nextStart = _clock.Elapsed;
            cts = _waitCts;
            _waitCts = new CancellationTokenSource();
        }

        try { cts.Cancel(); } catch { }
        _logger.LogInformation("AudioSchedulerService: Interrupted -> cleared pending queue.");
    }

    public async ValueTask DisposeAsync()
    {
        try { Complete(); } catch { }
        try { _cts.Cancel(); } catch { }
        _signal.Release();

        if (_streamingTask is not null)
        {
            try { await _streamingTask.ConfigureAwait(false); } catch { }
        }

        _waitCts.Cancel();
        _waitCts.Dispose();
        _cts.Dispose();
        _signal.Dispose();
    }

    private void ClearPendingAudio()
    {
        while (_signal.Wait(0)) { }
        while (_pendingAudio.TryDequeue(out _)) { }
    }
}