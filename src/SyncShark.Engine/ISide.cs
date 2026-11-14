namespace Massdex.SyncShark.Engine;

/// <summary>One side of a pair; Local and Dropbox implement it the same way (standards 1.9).</summary>
public interface ISide
{
    Side Side { get; }
    /// <summary>The entries under folder `under` ("" = the root; `under` itself is not yielded), in listing order,
    /// skipped entries included. options.Recursive: the whole subtree, else direct children.
    /// options.IncludeOutOfScope: ignored, excluded and nested-root entries are yielded too, and walked into
    /// (used only by D18 and D26 removals). Throws InventoryIncompleteException if any part can't be read.</summary>
    IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct);
    /// <summary>The entry at path (skipped included), or null if it or its parent folder is absent.</summary>
    Task<Entry?> ReadOneAsync(string path, CancellationToken ct);
    /// <summary>A readable stream of exactly this version (Local: seekable). The caller disposes it. attempt is forwarded unchanged to the transport; Local accepts null without adding transport work.</summary>
    Task<Stream> OpenReadAsync(Entry entry, RequestAttempt? attempt, CancellationToken ct);
    /// <summary>The Dropbox-algorithm content hash of a file entry.</summary>
    Task<string> ContentHashAsync(Entry entry, CancellationToken ct);
    /// <summary>Prepares new content for path without touching the target. On any failure the partial stage is removed
    /// before the exception leaves. Disposing an unpublished stage discards it, unless a pending note owns it.</summary>
    Task<IStaged> StageAsync(string path, Stream content, Entry source, CancellationToken ct);
    /// <summary>Puts staged content at its path: expected == null → the path must be free; otherwise the target
    /// must still match expected (Local: Id/Size/Modified; Dropbox: revision). selection carries the Local losing proof and saved binding, null only for a free target or Dropbox. Returns the classified effect, never a bare entry.</summary>
    Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection, CancellationToken ct);
    /// <summary>Returns the existing folder, or creates it. A file at the path → PathOccupiedException.</summary>
    Task<Entry> EnsureFolderAsync(string path, CancellationToken ct);
    /// <summary>Deletes expected (D18). Local also removes a folder with its contents; Dropbox refuses a folder, whose removal goes through the folder runner. Local: Recycle Bin (D6/D7). selection is the losing proof and saved binding Local requires for a file, null for Dropbox. Returns the classified effect, never a bare path absence.</summary>
    Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct);
    /// <summary>Moves expected to newPath (the destination must be free; case-only allowed).</summary>
    Task<Entry> MoveAsync(Entry expected, string newPath, CancellationToken ct);
    /// <summary>Submits the persisted wire-ordered removal effects under their immutable submission identity. Local rejects before any I/O with NotSupportedException.</summary>
    Task<FolderSubmitResult> SubmitFolderRemovalAsync(FolderSubmission submission,
        IReadOnlyList<FolderRemovalEffect> effects, RequestAttempt attempt, CancellationToken ct);
    /// <summary>Observes a submitted removal job without replaying its effects. Local rejects before any I/O with NotSupportedException.</summary>
    Task<FolderJobResult> ReadFolderRemovalAsync(string jobId, int expectedCount,
        RequestAttempt attempt, CancellationToken ct);
}
