using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;

public static class DataflowBlocks
{
    public static IPropagatorBlock<TIn, TOut> TransformToManyAsync<TIn, TOut>(
        Func<TIn, IAsyncEnumerable<TOut>> transform,
        ExecutionDataflowBlockOptions? execution = null,
        DataflowBlockOptions? outputOptions = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        execution ??= new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 1,
            BoundedCapacity = DataflowBlockOptions.Unbounded,
            EnsureOrdered = true
        };

        outputOptions ??= new DataflowBlockOptions
        {
            BoundedCapacity = DataflowBlockOptions.Unbounded
        };

        var outBuffer = new BufferBlock<TOut>(outputOptions);

        var action = new ActionBlock<TIn>(async input =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (input is null) return;

            try
            {
                await foreach (var item in transform(input).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    // Respect backpressure
                    await outBuffer.SendAsync(item, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                logger?.LogInterrupted();
            }
        }, execution);

        // Propagate completion and faults
        action.Completion.ContinueWith(t =>
        {
            if (t.IsFaulted)
                ((IDataflowBlock)outBuffer).Fault(t.Exception!);
            else
                outBuffer.Complete();
        }, TaskScheduler.Default);

        return DataflowBlock.Encapsulate(action, outBuffer);
    }
}