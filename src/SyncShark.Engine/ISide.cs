namespace Massdex.SyncShark.Engine;

/// <summary>One side of a pair; Local and Dropbox implement it the same way (standards 1.9).</summary>
public interface ISide
{
    Side Side { get; }
    /// <summary>Read the actual settled journal prefix for this member, or null if unavailable.</summary>
    Task<JournalPosition?> SettledJournalAsync(CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Notify a journal source when its pair membership changes.</summary>
    Task SetMembershipAsync(PairConfig pair, bool paused, bool removed, CancellationToken ct) => Task.CompletedTask;
    /// <summary>Native root identity for this side; PC also supplies drive evidence and observed drive type for its scope, recycle and startup trim; Dropbox has no PC drive type. A missing or unreadable root never implies a delete.</summary>
    Task<(DirectoryIdentity? Local, DropboxRootIdentity? Dropbox, DriveEvidence? Drive, DriveType? LocalDriveType)> RootAsync(CancellationToken ct);
    /// <summary>The entries under folder `under` ("" = the root; `under` itself is not yielded), in listing order,
    /// skipped entries included. options.Recursive: the whole subtree, else direct children.
    /// options.IncludeOutOfScope: ignored, excluded and nested-root entries are yielded too, and walked into
    /// (used only by D18 and D26 removals). A failed child is yielded with SkipUnreadable: withhold coverage beneath it and continue siblings. Throw InventoryIncompleteException only when the acquisition root or range is invalid.</summary>
    IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct);
    /// <summary>The entry at path (skipped included), or null if it or its parent folder is absent.</summary>
    Task<Entry?> ReadOneAsync(string path, CancellationToken ct);
    /// <summary>A readable stream of exactly this version (Local: seekable). The caller disposes it.</summary>
    Task<Stream> OpenReadAsync(Entry entry, CancellationToken ct);
    /// <summary>The Dropbox-algorithm content hash of a file entry.</summary>
    Task<string> ContentHashAsync(Entry entry, CancellationToken ct);
    /// <summary>Prepares new content for path without touching the target. On any failure the partial stage is removed
    /// before the exception leaves. A selected folder replacement requires fully consuming the source now, including a small upload session. Disposal preserves stages owned by the exact armed note.</summary>
    Task<IStaged> StageAsync(string path, Stream content, Entry source, Entry? selectedTarget, CancellationToken ct);
    /// <summary>Puts staged content at its path: expected == null → the path must be free; otherwise the target
    /// must still match expected (Local: Id/Size/Modified; Dropbox: revision). selection carries the Local losing proof and saved binding, null only for a free target or Dropbox. Returns the classified effect, never a bare entry.</summary>
    Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection, CancellationToken ct);
    /// <summary>Returns the existing folder, or creates it. A file at the path → PathOccupiedException.</summary>
    Task<RemovalResult> EnsureFolderAsync(string path, CancellationToken ct);
    /// <summary>Removes the selected file or folder. Dropbox addresses the selected identity through Dispatcher; Local rechecks the held victim. An accepted but Running request is not removal.</summary>
    Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct);
    /// <summary>Resolve only the selected create collision without repeating the accepted write.</summary>
    Task<JobReply.Terminal> ResolveCreateClashAsync(JobMember member, JobReply.Terminal selected, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Pass the originating action through deletion so an accepted job retains its removal policy.</summary>
    Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, ActionKind origin, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Moves expected to newPath (the destination must be free; case-only allowed).</summary>
    Task<RemovalResult> MoveAsync(Entry expected, string newPath, CancellationToken ct);
    /// <summary>Locate the selected identity; null denotes proven absence, not unavailable evidence.</summary>
    Task<(IdentityLocation Location, bool InScope)?> ReadByIdAsync(string id, CancellationToken ct);
    /// <summary>Creates a folder only if its name is free.</summary>
    Task<RemovalResult> CreateFolderExclusiveAsync(string path, CancellationToken ct);
    /// <summary>Reads the next change page and the provider boundary it covers.</summary>
    Task<ChangePage> ReadChangesAsync(string? cursor, JournalPosition? position, CancellationToken ct);
    /// <summary>Capture the current source cursor or journal position before listing; this observes a boundary without advancing the stored position.</summary>
    Task<(string? Cursor,JournalPosition? Position)> BoundaryAsync(CancellationToken ct);
    /// <summary>Finite read through the captured source boundary; never long-poll. Local reaches the same journal ID and at least the captured USN. Dropbox cursors are opaque: use equality or a completed provider continuation known to cover the capture, never string order. A continuity loss is not completion.</summary>
    Task<ChangePage> ReadChangesAsync(string? cursor, JournalPosition? position, (string? Cursor,JournalPosition? Position) until, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Deliver the PC pair’s contiguous settled prefix to the shared volume owner, independently of another read. Only used on Local.</summary>
    Task SettleJournalAsync(JournalPosition position, CancellationToken ct);
    /// <summary>The latest observed provider clock offset and its uncertainty; null without a measurement.</summary>
    (TimeSpan Offset, TimeSpan Uncertainty)? ClockOffset { get; }
    /// <summary>Substantiated deleted-history evidence for the first comparison; a lookup failure is not absence.</summary>
    Task<DeletionEvidence> DeletedAsync(string path, CancellationToken ct);
    /// <summary>Use folder ancestor tombstones only for a folder; a file needs coherent exact-version evidence.</summary>
    Task<DeletionEvidence> DeletedAsync(string path, bool folder, CancellationToken ct);
    /// <summary>Recovers attributable file bytes or folder scope; a substitute is never the selected version.</summary>
    Task<Recovery?> RecoverAsync(IdentityLocation actual, Entry version, CancellationToken ct);
    /// <summary>Restore the entire selected bin item to its original path only if the name is vacant.</summary>
    Task<RemovalResult> RestoreRecycledAsync(IdentityLocation actual, Entry version, string originalPath, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Reopens only the armed victim’s stage, verified by held volume/id, size and full digest. Its own stamped metadata supplies the source time. Ordinary redecision precedes create-only publication; no live source Entry is required.</summary>
    Task<IStaged> ReopenStageAsync(RemovalSelection armedVictim, StageProof expected, CancellationToken ct);
    /// <summary>Observes a returned job without submitting its members again.</summary>
    Task<JobReply> ReadJobAsync(AcceptedJob job, CancellationToken ct);
}

/// <summary>One page with its next source position, also when the page is empty.</summary>
public sealed record ChangePage(IReadOnlyList<Change> Changes, string? Cursor, JournalPosition? Position)
{
    /// <summary>True only for proven lost continuity; Position or Cursor then supplies the verified fresh source base. A transient read failure throws instead.</summary>
    public bool ContinuityLost { get; init; }
    /// <summary>This acquisition reached its captured finite boundary; live arrivals do not extend it.</summary>
    public bool CaughtUp { get; init; }
}
/// <summary>Only proven bytes or a proven folder listing are offered to the recovery caller.</summary>
public abstract record Recovery
{
    public sealed record File(Entry Version, Stream Bytes) : Recovery;
    public sealed record Folder(Entry Root, IAsyncEnumerable<Listed> Contents) : Recovery;
}
/// <summary>A returned job and its original wire-ordered subjects.</summary>
public sealed record AcceptedJob(string PairId, string Route, string JobId, IReadOnlyList<JobMember> Launch);
/// <summary>Selected identity and path footprint for moves/deletes; creates carry a name only.</summary>
public sealed record JobMember(string? Id, string Path, string? Destination)
{
    /// <summary>Request a new name rather than adopting an existing folder.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExclusiveCreate { get; init; }
    /// <summary>Original action for this launch member; absent on legacy jobs and never inferred from the route.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ActionKind? Origin { get; init; }
}
/// <summary>A provider observation; an invalid job does not imply any particular member outcome.</summary>
public abstract record JobReply
{
    public sealed record Running : JobReply;
    public sealed record Invalid : JobReply;
    public sealed record Terminal(IReadOnlyList<(Entry? Entry, TextKey? Cause, FailureDisposition? Failure)> Members) : JobReply
    {
        /// <summary>Zero-based indices into Members whose selected ID was observed absent, not proof this request removed it. Reindex when projecting a subset.</summary>
        public IReadOnlySet<int> AbsentOrdinals { get; init; } = System.Collections.Frozen.FrozenSet<int>.Empty;
    }
}

/// <summary>The Executor's selected losing proof together with the exact saved binding it was selected against,
/// conveyed to the Local removal arm boundary. Saved is null only when the selection observed no saved binding,
/// never as "not supplied"; Local copies it unchanged into the pending note.</summary>
public sealed record RemovalSelection(RemovalProof Proof, SyncedEntry? Saved);

/// <summary>Authoritative proof of the exact losing entry the Executor selected for local removal.
/// VolumeId and ContentHash describe those selected bytes, never the incoming stage; a folder carries its selected identity and binding, not a file digest.
/// Local identity text is invariant decimal: Expected.Id exactly as LocalListing wrote it and VolumeId from the
/// volume serial. LocalBin compares both ordinally with the held file, so another spelling fails closed.</summary>
public sealed record RemovalProof
{
    public Entry Expected { get; }
    public string VolumeId { get; }
    public string? ContentHash { get; }
    public RemovalProof(Entry expected, string volumeId, string? contentHash)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentException.ThrowIfNullOrWhiteSpace(expected.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(expected.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);
        // Local revalidates the selected placeholder's identity, metadata and still-dehydrated state on the held handle.
        bool metadataOnly = expected.IsFolder || expected.Skip == nameof(TextKey.SkipOnlineOnly);
        if (expected.Size < 0 || (metadataOnly ? contentHash is not null : !global::Massdex.SyncShark.Engine.ContentHash.IsValid(contentHash ?? string.Empty)))
            throw new ArgumentException(nameof(contentHash));
        Expected = expected; VolumeId = volumeId; ContentHash = contentHash;
    }
}

/// <summary>The classified result of one Local removal or publication (C-10); only a verified permitted removal authorizes publication or settlement.</summary>
public enum RemovalOutcome { NoEffect, Removed, Published, WrongRestored, Retained, Unknown, Pending }

/// <summary>The classified effect of one Delete or Publish (C-10). Callers read Outcome, never void return or path absence;
/// only a verified permitted removal may authorize publication or deletion settlement. On NoEffect, Retained, Unknown or
/// WrongRestored, Final names only a positively verified restored or current entry and authorizes no baseline or forget.
/// An accepted nonterminal Dropbox operation returns Pending; no binding or deletion is settled from that result.</summary>
public sealed record RemovalResult(RemovalOutcome Outcome, Entry? Final, TextKey? Cause)
{
    /// <summary>Shared acceptance saved once by Dispatcher before releasing a member.</summary>
    public AcceptedJob? Accepted { get; init; }
    /// <summary>Only this member's attributed observation, never a neighbour's receipt.</summary>
    public JobReply? Reply { get; init; }
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
        new(RemovalOutcome.NoEffect, null, TextKey.FailChanged) { ObservedLocal = refusal.ObservedLocal };
}

/// <summary>The verified closed set of values identifying staged content. Never a bin receipt.</summary>
public sealed record StageProof
{
    public string Path { get; }
    public string VolumeId { get; }
    public string FileId { get; }
    public long Size { get; }
    public string ContentHash { get; }
    public StageProof(string path, string volumeId, string fileId, long size, string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId); ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (!global::Massdex.SyncShark.Engine.ContentHash.IsValid(contentHash ?? string.Empty)) throw new ArgumentException(nameof(contentHash));
        Path=path; VolumeId=volumeId; FileId=fileId; Size=size; ContentHash=contentHash!;
    }
}
