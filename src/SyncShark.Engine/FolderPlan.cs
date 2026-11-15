namespace Massdex.SyncShark.Engine;

/// <summary>A deleting folder's frozen, streamed plan (section 6.5). No per-descendant list is retained.</summary>
public interface FolderPlan : IDisposable
{
    Row Folder { get; }
    Side DeletingFrom { get; }
    bool DeleteWhole { get; }

    /// <summary>Single-use and legal before Entries/Children; exhausts and disposes the source before any row is
    /// exposed, and a failed or cancelled capture is discarded rather than reused.</summary>
    Task CaptureAsync(IAsyncEnumerable<Listed> source, CancellationToken ct);

    /// <summary>Refreshes the same root after preservation, only before classification; captured children stay fixed.</summary>
    void PrepareRemoval(Row observed);

    /// <summary>Sequential inventory after capture, own-key ascending and parent before child.</summary>
    IEnumerable<Entry> Entries(CancellationToken ct);

    /// <summary>One complete classification pass; legal only after capture and before selection.</summary>
    IEnumerable<(string Key, string NativeKey, Entry? Entry,
        SyncedEntry? Synced)> Children(CancellationToken ct);

    /// <summary>Freezes every classified child, including saved-only children, exactly once during Children;
    /// later branch selection changes only selection, never these facts or the follow-up flag.</summary>
    void MarkClassified(string key, string nativeKey, FolderChild child,
        bool followUp, CancellationToken ct);

    /// <summary>Enters branch selection after classification when survivors exist, without reclassification.</summary>
    void PrepareBranches(CancellationToken ct);

    /// <summary>Parent-first removed candidates for the Planner's pure selection rule.</summary>
    IEnumerable<(long Id, string Key, FolderChild Child)> RemovedChildren(
        CancellationToken ct);

    bool HasSurvivorAtOrBelow(string key, CancellationToken ct);
    bool HasSelectedAncestor(string key, CancellationToken ct);

    /// <summary>Marks one frozen branch; never hashes or reclassifies.</summary>
    void SelectBranch(long id, CancellationToken ct);

    /// <summary>Checks normal phase exhaustion, commits scratch and stores the Planner's deleteWhole result.</summary>
    void Complete(bool deleteWhole, CancellationToken ct);

    /// <summary>Pre-effect guard over both exact forget ranges; true refuses the plan with zero effects.</summary>
    bool ForgetTouchesUnreadable(CancellationToken ct);

    /// <summary>Streams all classified descendants, including saved-only and unselected children even for
    /// DeleteWhole, only after Complete and before disposal. Facts and selection are immutable and repeatable;
    /// stable ordinals start at one in storage (SQLite BINARY, UTF-8 byte) Key/NativeKey/presence order, with Folder reserved for ordinal zero.
    /// Enumeration uses captured evidence without recapture, reclassification or whole-plan materialization.</summary>
    IEnumerable<FrozenFolderEntry> FrozenEvidence(int pageSize, CancellationToken ct);

    /// <summary>Frozen branches, deepest first; premature or disposed use is refused.</summary>
    IEnumerable<FolderChild> Branches(CancellationToken ct);

    /// <summary>Frozen follow-up survivors only; premature or disposed use is refused.</summary>
    IEnumerable<FolderChild> Survivors(CancellationToken ct);
}
