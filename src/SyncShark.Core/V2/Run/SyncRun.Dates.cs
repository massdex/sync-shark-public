namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    internal async Task<T> WithDateRepairLockAsync<T>(Func<Task<T>> repair, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new SyncCliBusyException("A sync is running. Let it finish or stop it, then retry dates repair; no dates were changed.");
        try { return await repair().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
