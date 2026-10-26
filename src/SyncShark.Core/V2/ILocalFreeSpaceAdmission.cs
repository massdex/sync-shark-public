namespace Massdex.SyncShark.V2;

/// <summary>An endpoint whose local writes are admitted against free space before any effect.</summary>
/// <remarks>
/// The verdict may wait: when every demand fits the observed free space alone and only other writers' reservations
/// block it, admission waits (bounded and cancellable) for a release instead of refusing a soon-satisfiable item.
/// </remarks>
public interface ILocalFreeSpaceAdmission
{
    Task<FreeSpaceAdmission> AdmitFreeSpaceAsync(SyncAction action, CancellationToken ct);
}
