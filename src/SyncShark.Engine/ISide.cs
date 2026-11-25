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
    /// <summary>The operation of a returned job, from the adapter’s own route map; no provider call.</summary>
    ActionKind JobOperation(AcceptedJob job) => throw new NotImplementedException();
    /// <summary>Observes a returned job without submitting its members again.</summary>
    Task<JobReply> ReadJobAsync(AcceptedJob job, CancellationToken ct);
}
