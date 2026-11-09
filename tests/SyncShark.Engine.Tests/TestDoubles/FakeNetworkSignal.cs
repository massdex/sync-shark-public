namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>An INetworkSignal the limiter checks switch on and off.</summary>
public sealed class FakeNetworkSignal : INetworkSignal
{
    private readonly object _gate = new();
    private TaskCompletionSource _waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _available;

    public bool Available { get { lock (_gate) return _available; } }

    public Task WaitAvailableAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return _available ? Task.CompletedTask : _waiter.Task.WaitAsync(ct);
    }

    /// <summary>Switching on completes the pending waits.</summary>
    public void SetAvailable(bool available)
    {
        lock (_gate)
        {
            _available = available;
            if (available) _waiter.TrySetResult();
            else if (_waiter.Task.IsCompleted) _waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
