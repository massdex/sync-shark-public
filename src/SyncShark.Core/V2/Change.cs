namespace Massdex.SyncShark.V2;

/// <summary>A single inventory change at a cursor position.</summary>
public sealed record Change(ChangeKind Kind, string ObjectId, string Path, ItemState? State, DateTimeOffset? DeletedUtc)
{
    // Spelling is sync state: a case-only move needs Run's old-path handling too.
    internal bool HasPreviousPath(ItemState? before) => before is not null &&
        !string.Equals(before.Path, Path, StringComparison.Ordinal);

    internal bool NeedsRunAuthority(ItemState? before) =>
        Kind == ChangeKind.Delete || State?.Kind == ItemKind.Folder || HasPreviousPath(before);
}
