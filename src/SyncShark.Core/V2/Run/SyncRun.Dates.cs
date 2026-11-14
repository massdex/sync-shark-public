namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    internal async Task<T> WithDateRepairLockAsync<T>(Func<Task<T>> repair, CancellationToken ct)
    {
        using var admission = await _admission.EnterAsync(null, ct, immediate: true).ConfigureAwait(false);
        return await repair().ConfigureAwait(false);
    }
}
