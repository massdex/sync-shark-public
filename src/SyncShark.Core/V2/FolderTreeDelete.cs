namespace Massdex.SyncShark.V2;

/// <summary>One whole-folder delete the folder and the live descendants the plan froze.</summary>
public sealed record FolderTree(ItemState Folder, IReadOnlyList<ItemState> Victims)
{
    // Transient Executor-owned source admission; never part of the recovery checkpoint.
    internal Func<Task<bool>>? RevalidateDelete { get; init; }
}

/// <summary>How a whole-folder delete ended.</summary>
public enum FolderTreeStatus
{
    /// <summary>Nothing was sent; the executor deletes this folder per item instead.</summary>
    NotSubmitted,

    /// <summary>The delete ran and the change-feed post-check finished.</summary>
    Completed,

    /// <summary>The delete may have run but its post-check (feed read or restore) did not finish.</summary>
    Uncertain,
}

/// <summary>
/// The outcome of one whole-folder delete. <c>Kept</c> are victim paths (the folder included) that exist again
/// because something under them was restored, or that are live after the delete. <c>Unconfirmed</c> are victims the
/// change feed did not report deleted. <c>Restored</c> and <c>Unrestored</c> name the items added or changed after
/// the scan that the delete also removed.
/// </summary>
public sealed record FolderTreeOutcome(FolderTree Tree, FolderTreeStatus Status, string? Reason,
    IReadOnlySet<string> Kept, IReadOnlySet<string> Unconfirmed, IReadOnlyList<string> Restored,
    IReadOnlyList<string> Unrestored)
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>An outcome that carries no per-path detail.</summary>
    public static FolderTreeOutcome Of(FolderTree tree, FolderTreeStatus status, string? reason = null) =>
        new(tree, status, reason, None, None, [], []);
}

/// <summary>
/// An endpoint that can delete a whole folder in one provider call and then prove, from its change feed, which items
/// that call removed . Only Dropbox implements it.
/// </summary>
public interface IFolderTreeDeleter
{
    /// <summary>
    /// Deletes each folder whole. <paramref name="scanCursor"/> is the change-feed cursor taken before the plan's
    /// scan. <paramref name="beforeSubmit"/> runs with the trees about to be sent, before any effect, so their intents
    /// are durable first. A tree returned NotSubmitted was never sent.
    /// </summary>
    Task<IReadOnlyList<FolderTreeOutcome>> DeleteFolderTreesAsync(string scanCursor, IReadOnlyList<FolderTree> trees,
        Func<IReadOnlyList<FolderTree>, string, Task> beforeSubmit, CancellationToken ct);

    /// <summary>Resumes the stored group check/restoration, never resubmits the deletion. Unknown acceptance
    /// remains Uncertain. Checkpoints are durable before returning; restored notices survive retries.</summary>
    Task<IReadOnlyList<FolderTreeOutcome>> ReconcileFolderTreesAsync(string scanCursor,
        IReadOnlyList<FolderTree> trees, string recoveryState, Func<string, Task> checkpoint, CancellationToken ct);
}
