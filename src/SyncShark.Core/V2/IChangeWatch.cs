namespace Massdex.SyncShark.V2;

/// <summary>A wake-up signal for one root: it says the root may have changed, never what changed.</summary>
/// <remarks>
/// Contract (continuous-sync.md section 1): <see cref="ChangedAsync"/> completes when a signal is pending and consumes
/// it in the same step; a later signal stays pending for the next call. Cancellation never consumes. Only one wait may
/// be outstanding; a second concurrent call throws <see cref="InvalidOperationException"/>. A new watch starts with a
/// signal pending. A dead watch faults its wait; <see cref="EndpointAccessException"/> means the account needs the user.
/// </remarks>
public interface IChangeWatch : IDisposable
{
    /// <summary>Completes, consuming the signal, once the root may have changed since the last completed wait.</summary>
    Task ChangedAsync(CancellationToken ct);
}
