namespace Massdex.SyncShark;

/// <summary>Releases a staging worker once an effect is queued, independently of its durable completion.</summary>
internal sealed class SyncEffectStaging(Action releaseWorker)
{
    internal static readonly AsyncLocal<SyncEffectStaging?> Current = new();
    private Action? _release = releaseWorker;

    /// <summary>Frees the worker exactly once; the executor still drains the pending effect at its barrier.</summary>
    internal void Release() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
