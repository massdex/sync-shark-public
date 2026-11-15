namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Holds one posted continuation so a lifecycle fence can be tested before its cancellation is delivered.</summary>
internal sealed class DeferredSynchronizationContext : SynchronizationContext
{
    private Action? _continuation;

    public override void Post(SendOrPostCallback callback, object? state)
    {
        if (Interlocked.CompareExchange(ref _continuation, () => callback(state), null) is not null)
            throw new InvalidOperationException("Only one continuation may be held.");
    }

    internal void Release() => Interlocked.Exchange(ref _continuation, null)?.Invoke();
}
