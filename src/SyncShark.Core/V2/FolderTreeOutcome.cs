namespace Massdex.SyncShark.V2;

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
