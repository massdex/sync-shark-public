namespace Massdex.SyncShark.V2;

/// <summary>Immutable entity stamps for a conflict choice, including the shared merge base before Forget.</summary>
public sealed record ConflictNoticeVersions(ItemState A, ItemState B, Baseline? Base = null,
    string? CopyOnA = null, string? CopyOnB = null, SyncAction? Incoming = null,
    ItemState? PendingOccupantSource = null);
