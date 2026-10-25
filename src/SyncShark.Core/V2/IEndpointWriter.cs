namespace Massdex.SyncShark.V2;

/// <summary>Applies planned effects to one endpoint.</summary>
/// <remarks>
/// Writer contract (change 6): every effect is conditional on <see cref="SyncAction.ExpectedTarget"/> — PutAsync/MoveAsync
/// require the item at the action's path (Move: FromPath) to match its VersionId and Hash and the destination to be free,
/// else <see cref="ExpectedTargetMismatchException"/> (that type also means the source changed since planning, so nothing
/// was sent); DeleteAsync requires the same match. OpenReadAsync returns exactly the requested version or throws
/// <see cref="ExpectedTargetMismatchException"/> when the source changed since planning. PreserveAsync is idempotent per
/// action id: a second call returns the first <see cref="RecoveryRef"/> and never archives twice.
/// </remarks>
public interface IEndpointWriter
{
    Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct);
    Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct);
    Task DeleteAsync(SyncAction a, CancellationToken ct);
    Task<ItemState> MkDirAsync(string path, CancellationToken ct);
    Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct);
    Task<ItemState?> ObserveAsync(string path, CancellationToken ct);
    Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct);
}
