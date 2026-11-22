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
    Task<Stream> OpenReadAsync(Entry entry, CancellationToken ct);
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
    /// <summary>Locate the selected identity; null denotes proven absence, not unavailable evidence.</summary>
    Task<(IdentityLocation Location, bool InScope)?> ReadByIdAsync(string id, CancellationToken ct);
    /// <summary>Creates a folder only if its name is free.</summary>
    Task<Entry> CreateFolderExclusiveAsync(string path, CancellationToken ct);
    /// <summary>Reads the next change page and the provider boundary it covers.</summary>
    Task<ChangePage> ReadChangesAsync(string? cursor, JournalPosition? position, CancellationToken ct);
    /// <summary>The latest observed provider clock offset and its uncertainty; null without a measurement.</summary>
    (TimeSpan Offset, TimeSpan Uncertainty)? ClockOffset { get; }
    /// <summary>Substantiated deleted-history evidence for the first comparison; a lookup failure is not absence.</summary>
    Task<DeletionEvidence> DeletedAsync(string path, CancellationToken ct);
    /// <summary>Recovers attributable file bytes or folder scope; a substitute is never the selected version.</summary>
    Task<Recovery?> RecoverAsync(IdentityLocation actual, Entry version, CancellationToken ct);
    /// <summary>Observes a returned job without submitting its members again.</summary>
    Task<JobReply> ReadJobAsync(AcceptedJob job, CancellationToken ct);
}

/// <summary>One page with its next source position, also when the page is empty.</summary>
public sealed record ChangePage(IReadOnlyList<Change> Changes, string? Cursor, JournalPosition? Position);
/// <summary>Only proven bytes or a proven folder listing are offered to the recovery caller.</summary>
public abstract record Recovery
{
    public sealed record File(Entry Version, Stream Bytes) : Recovery;
    public sealed record Folder(Entry Root, IAsyncEnumerable<Listed> Contents) : Recovery;
}
/// <summary>A returned job and its original wire-ordered subjects.</summary>
public sealed record AcceptedJob(string PairId, string Route, string JobId, IReadOnlyList<JobMember> Launch);
/// <summary>Selected identity and path footprint for moves/deletes; creates carry a name only.</summary>
public sealed record JobMember(string? Id, string Path, string? Destination);
/// <summary>A provider observation; an invalid job does not imply any particular member outcome.</summary>
public abstract record JobReply
{
    public sealed record Running : JobReply;
    public sealed record Invalid : JobReply;
    public sealed record Terminal(IReadOnlyList<(Entry? Entry, TextKey? Cause, FailureDisposition? Failure)> Members) : JobReply;
}

/// <summary>The Executor's selected losing proof together with the exact saved binding it was selected against,
/// conveyed to the Local removal arm boundary. Saved is null only when the selection observed no saved binding,
/// never as "not supplied"; Local copies it unchanged into the pending note.</summary>
public sealed record RemovalSelection(RemovalProof Proof, SyncedEntry? Saved);

/// <summary>Authoritative proof of the exact losing entry the Executor selected for local removal.
/// VolumeId and ContentHash describe those selected bytes, never the incoming stage; a folder carries its selected identity and binding, not a file digest.
/// Local identity text is invariant decimal: Expected.Id exactly as LocalListing wrote it and VolumeId from the
/// volume serial. LocalBin compares both ordinally with the held file, so another spelling fails closed.</summary>
public sealed record RemovalProof(Entry Expected, string VolumeId, string? ContentHash)
{
    public Entry Expected { get; init; } = RequireEntry(Expected);
    public string VolumeId { get; init; } = RequireId(VolumeId, nameof(VolumeId));
    public string? ContentHash { get; init; } = Expected.IsFolder
        ? ContentHash is null ? null : throw new ArgumentException(nameof(ContentHash))
        : global::Massdex.SyncShark.Engine.ContentHash.IsValid(ContentHash ?? string.Empty) ? ContentHash : throw new ArgumentException(nameof(ContentHash));

    private static Entry RequireEntry(Entry expected)
    {
        if (expected is null) throw new ArgumentNullException(nameof(Expected));
        if (string.IsNullOrWhiteSpace(expected.Id))
            throw new ArgumentException(null, nameof(Expected));
        return expected;
    }

    private static string RequireId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(null, name);
        return value;
    }
}

/// <summary>The classified result of one Local removal or publication (C-10); only a verified permitted removal authorizes publication or settlement.</summary>
public enum RemovalOutcome { NoEffect, Removed, Published, WrongRestored, Retained, Unknown }

/// <summary>The classified effect of one Delete or Publish (C-10). Callers read Outcome, never void return or path absence;
/// only a verified permitted removal may authorize publication or deletion settlement. On NoEffect, Retained, Unknown or
/// WrongRestored, Final names only a positively verified restored or current entry and authorizes no baseline or forget.
/// OperationId is absent when no pending note exists, as for free-target publication.</summary>
public sealed record RemovalResult(RemovalOutcome Outcome, string? OperationId, Entry? Final, TextKey? Cause)
{
    /// <summary>The journal position of the last record a synchronous Local publication wrote for Final, or null when
    /// none is proved; the settlement stores it so the dirty owner can recognize this write's own echo.</summary>
    public JournalPosition? LocalPublished { get; init; }

    /// <summary>The complete selected Local content from a definite no-effect refusal; never durable authority.</summary>
    public Entry? ObservedLocal { get; init; }

    /// <summary>The recycled folder's contents at their original paths, read lazily from the shell's
    /// receipt; null when no folder receipt exists.</summary>
    public IAsyncEnumerable<Listed>? Recycled { get; init; }

    /// <summary>Observed identity and binding of the actual bin item, including a substitute. Never persisted as a receipt or used as proof of the selected victim.</summary>
    public IdentityLocation? RecycledVictim { get; init; }

    internal static Entry? CompleteObservation(Entry selected, string? hash) =>
        hash is null ? null : selected with { ContentHash = hash };

    internal static RemovalResult Refused(ChangedUnderneathException refusal) =>
        new(RemovalOutcome.NoEffect, null, null, TextKey.FailChanged) { ObservedLocal = refusal.ObservedLocal };
}

/// <summary>Exact identity and content proof of one file: an owned stage, or a receipt observed as the victim of a wrong restore.
/// Publication and recovery compare against this object, never size alone.</summary>
public sealed record StageProof(string Path, string VolumeId, string FileId, long Size,
    string ContentHash);
