using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;

public sealed class AudioSchedulerService : IAsyncDisposable
{
    private const int MarginMilliseconds = 5;
    private readonly TurnManager _turnManager;
    private readonly ILogger _logger;

    private readonly ActionBlock<AudioEvent> _input;
    private readonly BroadcastBlock<AudioEvent> _output;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource _waitCts = new();

    private Task? _streamingTask;
    private Stopwatch? _clock;

    // State guarded by _lock
    private readonly Queue<AudioEvent> _pendingAudio = new();
    private int _currentTurn = -1;
    private TimeSpan _nextStart = TimeSpan.Zero;
    private bool _completing;

    public AudioSchedulerService(ILogger<AudioSchedulerService> logger, PipelineControlPlane controlPlane)
    {
        _logger = logger;
        _turnManager = controlPlane.TurnManager;

        _input = new ActionBlock<AudioEvent>(evt =>
        {
            lock (_gate)
            {
                if (evt.TurnId > _currentTurn)
                {
                    _pendingAudio.Clear();
                    _currentTurn = evt.TurnId;
                    _nextStart = _clock?.Elapsed ?? TimeSpan.Zero;
                }
            }
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

        _turnManager.OnTurnInterrupted += _ => Interrupt();
        _streamingTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public ITargetBlock<AudioEvent> AudioInput => _input;

    public ISourceBlock<AudioEvent> AudioOutput => _output;

    public void Complete()
    {
        _completing = true;
        _input.Complete();
        _signal.Release();
    }

    private async Task RunAsync(CancellationToken token)
    {
        _clock = Stopwatch.StartNew();
        try
        {
            while (!token.IsCancellationRequested)
            {
                AudioEvent? next = null;

                lock (_gate)
                {
                    if (_pendingAudio.Count > 0)
                    {
                        next = _pendingAudio.Peek();
                    }
                }

                if (next is null)
                {
                    if (_completing && _input.Completion.IsCompleted) break;
                    try { await _signal.WaitAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                // pacing
                var now = _clock.Elapsed;
                TimeSpan wait;
                TimeSpan updatedNextStart;
                lock (_gate)
                {
                    wait = _nextStart - now - TimeSpan.FromMilliseconds(MarginMilliseconds);
                    updatedNextStart = _nextStart;
                }

                if (wait > TimeSpan.FromMicroseconds(MarginMilliseconds))
                {
                    CancellationToken delayToken;
                    lock (_gate)
                    {
                        delayToken = CancellationTokenSource.CreateLinkedTokenSource(token, _waitCts.Token).Token;
                    }
                    try
                    {
                        await Task.Delay(wait, delayToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Interrupted or shutting down; loop to re-evaluate
                        continue;
                    }
                }

                // Emit
                lock (_gate)
                {
                    // Stale check after potential wait
                    if (_pendingAudio.Count == 0) continue;
                    var head = _pendingAudio.Peek();
                    if (head.TurnId != _currentTurn)
                    {
                        _pendingAudio.Dequeue(); // stale
                        continue;
                    }

                    _pendingAudio.Dequeue();
                    if (!_output.Post(head))
                    {
                        _logger.LogWarning("AudioSchedulerService: Failed to post to output audio chunk.");
                    }

                    // NOTE: we must start the next slightly before the end of the current to avoid gaps
                    _nextStart = updatedNextStart + head.Payload.Duration - TimeSpan.FromMilliseconds(MarginMilliseconds);
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

    private void Interrupt()
    {
        lock (_gate)
        {
            if (_pendingAudio.Count > 0) _pendingAudio.Clear();
            _nextStart = _clock?.Elapsed ?? TimeSpan.Zero;

            _waitCts.Cancel();
            _waitCts.Dispose();
            _waitCts = new CancellationTokenSource();
        }
        _signal.Release(); // wake loop
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
}