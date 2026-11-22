using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine;

/// <summary>One item to decide. Extra = the further entries that share this key (D23 ambiguity); empty when
/// unambiguous. LocalHash = the local file's content hash once the Executor has computed it; null until requested.
/// Deletion = the prepared DeletionEvidence for this row's target; null until the Executor asks for it and null again
/// after any re-observation that invalidates it (P4-06). Null is "no answer yet", never a deletion answer.</summary>
public sealed record Row(string Key, Entry? Local, Entry? Dropbox, SyncedEntry? Synced,
    IReadOnlyList<Entry> Extra, string? LocalHash, RowContext Context, DeletionEvidence? Deletion = null)
{
    /// <summary>Builds the context from this row's own facts without allocating a placeholder copy.</summary>
    public Row(Func<Row, RowContext> context, string key, Entry? local, Entry? dropbox, SyncedEntry? synced,
        IReadOnlyList<Entry> extra, string? localHash)
        : this(key, local, dropbox, synced, extra, localHash, null!) => Context = context(this);

    /// <summary>Measured Dropbox time minus PC time, with uncertainty, only for comparing a server deletion against the PC file stamp. No measurement means no confirmed later deletion.</summary>
    public (TimeSpan Offset, TimeSpan Uncertainty)? DeletionClock { get; init; }

    // An absent observation still has an exact path; its comparison key cannot recover the spelling.
    internal string? ObservationPath { get; init; }

    // A counterpart was observed but omitted because another key owns it; the row does not prove absence.
    internal bool CounterpartElsewhere { get; init; }

    /// <summary>True when more than one entry shares this row's key.</summary>
    public bool Ambiguous => Extra.Count > 0;

    /// <summary>The observed entry on a side; the saved entry is not a fallback, and any non-local side selects Dropbox.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entry? EntryOn(Side side) => side == Side.Local ? Local : Dropbox;

    /// <summary>The row's path on a side: the observed entry, else the target, else the saved path.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string? PathOn(Side side, string? target = null) =>
        EntryOn(side)?.Path ?? target ?? Synced?.PathOn(side);

    /// <summary>True when the observed or saved item is a folder; the local observation wins. Extra is not consulted.
    /// A method, not a property, so the record's ToString and JSON shape stay unchanged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsFolder() => (Local ?? Dropbox)?.IsFolder ?? Synced?.IsFolder ?? false;

    /// <summary>The display/failure path, preferring current Local then Dropbox, then the saved Local path.</summary>
    public static string Path(Row row) => row.Local?.Path ?? row.Dropbox?.Path ?? row.Synced?.LocalPath ?? row.ObservationPath ?? row.Key;

    /// <summary>Both endpoint paths with the observed, target, saved and destination fallbacks.</summary>
    public static (string Local, string Dropbox) Paths(Row row, string? target = null) =>
        (row.PathOn(Side.Local, target) ?? row.Context.Destination ?? row.ObservationPath ?? row.Key,
         row.PathOn(Side.Dropbox, target) ?? row.Context.Destination ?? row.ObservationPath ?? row.Key);

    /// <summary>The comparison key followed by observed and saved side keys, retaining their order and duplicates.</summary>
    public static IEnumerable<string> Keys(Row row)
    {
        yield return row.Key;
        if (row.Local is { } local) yield return PathNames.KeyFor(Side.Local, local.Path);
        if (row.Dropbox is { } remote) yield return PathNames.KeyFor(Side.Dropbox, remote.Path);
        if (row.Synced is { } saved)
        {
            yield return saved.LocalKey;
            yield return saved.DropboxKey;
        }
    }

    /// <summary>One row for a single-side observation; an invalid side populates neither entry.</summary>
    public static Row ForOneSide(Side side, string key, Entry? entry, SyncedEntry? synced,
        IReadOnlyList<Entry> extra, string? localHash, RowContext context) =>
        new(key, side == Side.Local ? entry : null, side == Side.Dropbox ? entry : null, synced, extra, localHash,
            context);
}

/// <summary>Facts about a row's surroundings, gathered by Run (or the Observer); Destination is filled by the Executor
/// before deciding (section 7.1 step 0): for a row missing one side, that side's path. HoldsNestedRoot: on the
/// UnderFolderDelete side, another pair's root is at or under this row's path (D12; FolderChildFacts for step 4).
/// HeldKind: the kind of the notice holding the row (2.4), so a hold refreshes only into its own kind.
/// CompleteInventory: the current acquisition is complete, so P4 discovery membership has authority; DeletionEligible:
/// P4 selected this row for investigation or folder finalization, never deletion authority. FirstRunDeletion carries
/// identity-validated retained disposition; its persisted folder context is not authority for a later acquisition.
/// The Executor supplies current comparison time to TimeRules (P6 phase 1); no clock is carried here.</summary>
public sealed record RowContext(bool OutOfScope, bool Skipped, bool Held, Side? UnderFolderDelete,
    bool HoldsNestedRoot, bool FirstRun, bool CompleteInventory, bool DeletionEligible,
    string? Destination = null, NoticeKind? HeldKind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    IssueSubject.FirstRunDeletionKind FirstRunDeletion = IssueSubject.FirstRunDeletionKind.None)
{
    /// <summary>Null until the ordered folder contents have been acquired.</summary>
    public bool? ProtectedContents { get; init; }
    /// <summary>Cause reported by an unreadable descendant.</summary>
    public TextKey? UnreadableCause { get; init; }
    /// <summary>The row facts no observation has added yet; one shared immutable instance for every caller that
    /// observes before projecting.</summary>
    public static RowContext Neutral { get; } = new(false, false, false, null, false, false, false, false);

    /// <summary>Evaluates one row's context from facts Run already acquired. The ordinary path is inlined and
    /// returns neutral itself; only an exceptional row allocates a context, copying FirstRun and CompleteInventory.
    /// neutral must carry no row facts: OutOfScope/Skipped/Held/HoldsNestedRoot/DeletionEligible false,
    /// UnderFolderDelete/HeldKind/Destination null.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RowContext ForRow(Row row, PathScope scope, RowContext neutral, bool skipped,
        NoticeKind? heldKind, Side? underFolderDelete, bool deletionEligible,
        IssueSubject.FirstRunDeletionKind firstRunDeletion, Scope? mapped = null)
    {
        Debug.Assert(neutral is { OutOfScope: false, Skipped: false, Held: false, UnderFolderDelete: null,
            HoldsNestedRoot: false, DeletionEligible: false, Destination: null, HeldKind: null,
            FirstRunDeletion: IssueSubject.FirstRunDeletionKind.None });
        // Scope evaluates the listed path, else the saved path, on each side (the corrected exclusion rule).
        var outOfScope = scope.Excludes(row.Local?.Path ?? row.Synced?.LocalPath ?? mapped?.LocalPath,
            row.Dropbox?.Path ?? row.Synced?.DropboxPath ?? mapped?.DropboxPath,
            row.IsFolder());
        if (!outOfScope && !skipped && heldKind is null && underFolderDelete is null && !deletionEligible &&
            firstRunDeletion == IssueSubject.FirstRunDeletionKind.None)
            return neutral;
        return Exceptional(row, scope, neutral, outOfScope, skipped, heldKind, underFolderDelete, deletionEligible,
            firstRunDeletion);
    }

    // Kept out of ForRow so the ordinary path stays small enough to inline; nested-root scope is only asked here.
    private static RowContext Exceptional(Row row, PathScope scope, RowContext neutral, bool outOfScope,
        bool skipped, NoticeKind? heldKind, Side? underFolderDelete, bool deletionEligible,
        IssueSubject.FirstRunDeletionKind firstRunDeletion) =>
        new(OutOfScope: outOfScope,
            Skipped: skipped,
            Held: heldKind is not null,
            UnderFolderDelete: underFolderDelete,
            HoldsNestedRoot: underFolderDelete is { } side &&
                (side == Side.Local ? row.Local?.Path ?? row.Synced?.LocalPath
                    : row.Dropbox?.Path ?? row.Synced?.DropboxPath) is { } path &&
                scope.HoldsNestedRoot(side, path),
            FirstRun: neutral.FirstRun,
            CompleteInventory: neutral.CompleteInventory,
            DeletionEligible: deletionEligible,
            HeldKind: heldKind,
            FirstRunDeletion: firstRunDeletion);
}

/// <summary>One exact target's deletion evidence: a non-destructive failure, a live location, or a substantiated
/// deletion. Variants are distinct outcomes, never boolean flags, so a listing flag or name match alone cannot
/// construct a deletion. Only a deletion variant can reach a removal decision, and only after the current-identity,
/// scope and freshness owners answered.</summary>
public abstract record DeletionEvidence(string Path)
{
    /// <summary>A specific identity, mapping, reply-shape or current-fact failure after no other variant applies.
    /// Cancellation propagates as an exception, never as this variant.</summary>
    public sealed record Unknown(string Path, TextKey Cause) : DeletionEvidence(Path);

    /// <summary>History stayed unreadable after the ordinary retry owner finished. Non-destructive; it never infers
    /// that the file never existed.</summary>
    public sealed record HistoryUnavailable(string Path, TextKey Cause) : DeletionEvidence(Path);

    /// <summary>The documented path-mode revision listing answered path/not_found: a successful history negative,
    /// not a generic miss and not a deletion.</summary>
    public sealed record NoRetainedHistory(string Path) : DeletionEvidence(Path);

    /// <summary>The shared identity owner proved a live incarnation at the exact target.</summary>
    public sealed record LiveAtTarget(string Path, IdentityLocation Location) : DeletionEvidence(Path);

    /// <summary>The shared identity owner proved a live incarnation elsewhere; its location is preserved intact, even
    /// outside the current pair.</summary>
    public sealed record LiveElsewhere(string Path, IdentityLocation Location) : DeletionEvidence(Path);

    /// <summary>A deleted file substantiated by coherent head history and a proved-null known-ID lookup. Exactly one of
    /// DeletedAt/TimeUnavailable and one of ContentHash/HashUnavailable is set, each unavailable reason naming that
    /// field's own failure so a coherent remainder stays usable.</summary>
    public sealed record DeletedFile(
        string Path, string Domain, string RemoteId, string? Revision,
        DateTimeOffset? DeletedAt, TextKey? TimeUnavailable,
        string? ContentHash, TextKey? HashUnavailable) : DeletionEvidence(Path)
    {
        public string Domain { get; init; } = RequireValue(Domain, nameof(Domain));

        public string RemoteId { get; init; } = RequireValue(RemoteId, nameof(RemoteId));

        public DateTimeOffset? DeletedAt { get; init; } = RequireDeletionTime(DeletedAt, TimeUnavailable);

        public string? ContentHash { get; init; } = RequireContentHash(ContentHash, HashUnavailable);

        private static string RequireValue(string value, string name) =>
            string.IsNullOrWhiteSpace(value) ? throw new ArgumentException(null, name) : value;

        private static DateTimeOffset? RequireDeletionTime(DateTimeOffset? deletedAt, TextKey? unavailable)
        {
            if ((deletedAt is null) == (unavailable is null))
                throw new ArgumentException(null, nameof(DeletedAt));
            if (unavailable is { } reason && reason is not (TextKey.DeletionTimeMissing
                    or TextKey.DeletionTimeMalformed or TextKey.DeletionTimeUnavailable))
                throw new ArgumentException(null, nameof(TimeUnavailable));
            return deletedAt;
        }

        private static string? RequireContentHash(string? contentHash, TextKey? unavailable)
        {
            if ((contentHash is null) == (unavailable is null))
                throw new ArgumentException(null, nameof(ContentHash));
            if (unavailable is { } reason && reason is not (TextKey.DeletionHashMissing
                    or TextKey.DeletionHashMalformed or TextKey.DeletionHashUnavailable))
                throw new ArgumentException(null, nameof(HashUnavailable));
            if (contentHash is not null &&
                !global::Massdex.SyncShark.Engine.ContentHash.IsValid(contentHash))
                throw new ArgumentException(null, nameof(ContentHash));
            return contentHash;
        }
    }

    /// <summary>A folder-only tombstone: the exact deleted path or a proper ancestor the path owner validated.
    /// It never authorizes recursive first-run removal.</summary>
    public sealed record DeletedPath(string Path, string TombstonePath) : DeletionEvidence(Path);

    /// <summary>A folder with no tombstone after every necessary exact and ancestor lookup returned a validated
    /// current answer.</summary>
    public sealed record NoFolderTombstone(string Path) : DeletionEvidence(Path);
}
