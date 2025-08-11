public class TurnManager : IDisposable
{
    private int _currentTurnId = 0;
    private CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    public int CurrentTurnId { get { lock (_lock) { return _currentTurnId; } } }
    public CancellationToken CurrentToken { get { lock (_lock) { return _cts.Token; } } }

    public void Interrupt()
    {
        lock (_lock)
        {
            _currentTurnId++;
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
        }
    }

    public void Dispose() => _cts?.Dispose();
}

