using System.Diagnostics;
using System.Threading.Tasks.Dataflow;

public class PipelineControlPlane
{
    #region Fields

    private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly BroadcastBlock<PipelineControlEvent> _hub = new(e => e);

    #endregion

    public PipelineControlPlane()
    {
        PipelineControlPlaneExtensions.All.Add(this);
    }

    #region Wall Clock

    public static TimeSpan Timestamp => _stopwatch.Elapsed;

    public Task DelayAsync(TimeSpan delay, CancellationToken ct = default) => Task.Delay(delay, ct);

    public DateTimeOffset Now => DateTimeOffset.Now;

    #endregion

    #region Event Hub

    public static bool PublishToAll(PipelineControlEvent evt)
    {
        bool result = true;
        foreach (var p in PipelineControlPlaneExtensions.All)
        {
            result &= p.Publish(evt);
        }
        return result;
    }

    public bool Publish(PipelineControlEvent evt) => _hub.Post(evt);

    public IDisposable Subscribe<T>(Action<T> handler, Predicate<T>? filter = null) where T : PipelineControlEvent
    {
        var target = new ActionBlock<PipelineControlEvent>(e =>
        {
            if (e is T typed && (filter?.Invoke(typed) ?? true))
            {
                handler(typed);
            }
        }, new ExecutionDataflowBlockOptions { MaxDegreeOfParallelism = 1 });

        var link = _hub.LinkTo(target, new DataflowLinkOptions { PropagateCompletion = true });
        return new Subscription(() =>
        {
            link.Dispose();
            target.Complete();
        });
    }

    #endregion

    #region Turn Management

    public TurnManager TurnManager { get; } = new();

    #endregion

    #region Utility Classes

    private sealed class Subscription : IDisposable
    {
        private readonly Action _dispose;
        private int _disposed;
        public Subscription(Action dispose) => _dispose = dispose;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _dispose();
        }
    }

    #endregion

    public class ActiveSpeaker: PipelineControlEvent
    {
        public string? Name { get; }
        public ActiveSpeaker(string? name) => Name = name;
    }
}

public static class PipelineControlPlaneExtensions
{
    public static List<PipelineControlPlane> All = new();
}
