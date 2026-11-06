using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Builds each row's RowContext for one execution (section 8.1 step 6): scope and held facts are asked of the
/// Store per row; only facts that exist nowhere else stay here, under one gate: rename footprints, the dependents of a
/// failed Local-folder-to-file replacement, the listing's folder sets and the identity of covering first-run roots.
/// completeInventory is the run's completed-acquisition authority; deletedName probes the completed scan's deleted-name
/// relation by path key and is null when that relation held no names, so no per-row membership work runs (P4-04c).
/// subjects carries conflict proofs. retryIssues are the Failures one Repair's user chose to retry.</summary>
internal sealed class RowContexts(ExecutorContext context, bool firstRun, bool completeInventory,
    IReadOnlySet<long>? retryIssues, IReadOnlyDictionary<string, IssueSubject>? subjects, Func<string, bool>? deletedName)
{
    internal static RowContexts ForSelection(ExecutorContext context) =>
        new(context, !context.Store.FirstRepairDone(context.Pair.PairId), false, null, null, null);

    private readonly object _gate = new();
    private readonly string _pairId = context.Pair.PairId;
    private RowContext _ordinary = new(false, false, false, null, false, firstRun, completeInventory, false);

    internal RowContexts ForScan(bool initialRun, IReadOnlySet<long>? retries,
        IReadOnlyDictionary<string, IssueSubject>? proofs, Func<string, bool>? deleted)
    {
        firstRun = initialRun;
        completeInventory = true;
        retryIssues = retries;
        subjects = proofs;
        deletedName = deleted;
        _ordinary = _ordinary with { FirstRun = firstRun, CompleteInventory = true };
        _roots.Clear();
        return this;
    }
    // Transient observations and execution holds belong to one selection and its continuations.
    internal RowContexts ForSelection()
    {
        _roots.Clear();
        _renameHolds.Clear();
        _dependentRoots?.Clear();
        _refusedRetries?.Clear();
        return this;
    }

    private readonly PrefixSet _renameHolds = new();
    private readonly Dictionary<string, bool> _roots = new(StringComparer.Ordinal);
    private PrefixSet? _dependentRoots;
    private PrefixSet? _refusedRetries;

    /// <summary>Skipped local folder keys in join space.</summary>
    public PrefixSet SkippedLocal { get; } = new();
    /// <summary>Skipped Dropbox folder keys in join space.</summary>
    public PrefixSet SkippedDropbox { get; } = new();
    /// <summary>Deferred local folder-delete keys in join space.</summary>
    public PrefixSet DeletingLocal { get; } = new();
    /// <summary>Deferred Dropbox folder-delete keys in join space.</summary>
    public PrefixSet DeletingDropbox { get; } = new();

    // All four sets hold keys in the row's join (local-spelling) space; Repair fills them from each side's listing.
    /// <summary>The skipped set of one side: the existing set, never a copy.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PrefixSet Skipped(Side side) => side == Side.Local ? SkippedLocal : SkippedDropbox;

    /// <summary>The deferred deleting set of one side: the existing set, never a copy.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PrefixSet Deleting(Side side) => side == Side.Local ? DeletingLocal : DeletingDropbox;

    /// <summary>Reuses a conflict hash only when its subject still matches the observed Local entry.</summary>
    public Row WithProof(Row row)
    {
        // A Local join key is its own key, so row.Key already identifies the proof.
        if (subjects is null || subjects.Count == 0 || row.Local is not { } local ||
            !subjects.TryGetValue(row.Key, out var subject) ||
            subject.ReusableLocalHash(local) is not { } hash)
            return row;
        return row with { LocalHash = hash };
    }

    /// <summary>Whether a stored hold or this execution's rename footprint covers the key (the prefix rule).</summary>
    public bool Covers(string key)
    {
        lock (_gate)
            if (_renameHolds.Covers(key)) return true;
        return context.Store.HeldCovers(_pairId, key, retryIssues);
    }

    /// <summary>Holds a rename's whole footprint for the rest of the execution; it may have no stored issue.</summary>
    public void HoldRename(IEnumerable<RenameCandidate> group) =>
        HoldRenamePaths(group.SelectMany(candidate => new[]
        {
            (Side.Local, candidate.Synced.LocalPath), (Side.Dropbox, candidate.Synced.DropboxPath),
            (candidate.Side, candidate.Moved.Path)
        }));

    internal void HoldRenamePaths(IEnumerable<(Side Side, string Path)> paths)
    {
        lock (_gate)
            foreach (var path in paths) _renameHolds.Add(PathNames.KeyFor(path.Side, path.Path));
    }

    /// <summary>A rename touching any held key is held whole, and its footprint then holds later groups.</summary>
    public bool HoldsRename(IReadOnlyCollection<RenameCandidate> group)
    {
        if (!group.Any(candidate => Covers(candidate.Synced.LocalKey) || Covers(candidate.Synced.DropboxKey) ||
                Covers(PathNames.KeyFor(candidate.Side, candidate.Moved.Path)))) return false;
        HoldRename(group);
        return true;
    }

    /// <summary>Holds the subtrees of a failed Local-folder-to-file replacement for the rest of the run (P1-C2-01).
    /// The original scheduled row and the effective re-observed row are both supplied; overlapping aliases are
    /// idempotent. Only roots are added: PrefixSet already covers every descendant.</summary>
    public void HoldDependents(Row row)
    {
        lock (_gate) AddExecutionRoots(_dependentRoots ??= new PrefixSet(), row);
    }

    internal void Observe(SyncAction action, SyncOutcome outcome)
    {
        if (outcome.BlocksDependentRows(action.Kind, action.Side, action.Row)) HoldDependents(action.Row);
        if (retryIssues is not { Count: > 0 } || outcome.Raised != IssueKind.Failure ||
            outcome.Cause is not { } cause || !IssuePolicy.Holds(IssueKind.Failure, cause)) return;
        // A continuing refusal keeps its issue ID; Retry authority is consumed for its dependents in this execution.
        lock (_gate) AddExecutionRoots(_refusedRetries ??= new PrefixSet(), action.Row);
    }

    private IReadOnlySet<long>? RetriesFor(Row row)
    {
        if (retryIssues is not { Count: > 0 }) return retryIssues;
        lock (_gate)
            return _refusedRetries is { } refused && IssuePolicy.CoveredKey(row, refused) is not null
                ? null : retryIssues;
    }

    private static void AddExecutionRoots(PrefixSet roots, Row row)
    {
        roots.Add(row.Key);
        if (row.Local is { } local)
        {
            var localKey = PathNames.KeyFor(Side.Local, local.Path);
            if (localKey != row.Key) roots.Add(localKey);
        }
        if (row.Dropbox is { } dropbox) roots.Add(PathNames.KeyFor(Side.Dropbox, dropbox.Path));
        if (row.Synced is { } synced)
        {
            roots.Add(synced.LocalKey);
            roots.Add(synced.DropboxKey);
        }
    }

    /// <summary>Builds a row's context, validating a covering first-run root against the scan's entry.</summary>
    public RowContext For(Row row)
    {
        var retained = context.Store.RetainedFirstRun(_pairId);
        return Build(row, retained, retained.Count != 0 && IssuePolicy.UnbaselinedLocal(row) &&
            UnderRetainedSubtree(retained, row.Key), false);
    }

    /// <summary>Builds a targeted row's context, validating a covering first-run root by a fresh Local read. A failed
    /// read escapes as the row's own failure: it is never absence.</summary>
    internal async ValueTask<RowContext> ForSelectionAsync(Row row, CancellationToken ct)
    {
        var retained = context.Store.RetainedFirstRun(_pairId);
        var covered = false;
        if (retained.Count != 0 && IssuePolicy.UnbaselinedLocal(row))
            foreach (var root in CoveringRoots(retained, row.Key))
            {
                if (!Known(root.Key, out var holds))
                    holds = Remember(root.Key, RootHolds(await context.Local.ReadOneAsync(root.Path, ct), retained));
                covered |= holds;
            }
        return Build(row, retained, covered, UnderUnreadable(row), false);
    }

    /// <summary>A follow-up's or survivor's context (7.6, 7.9): the targeted row's facts at this moment. No inventory
    /// stands behind it, so it selects nothing for a history check; the Observer's destination stays.</summary>
    internal async ValueTask<RowContext> ForFollowUpAsync(Row row, CancellationToken ct) =>
        await ForSelectionAsync(row, ct) with
        { Destination = row.Context.Destination, CompleteInventory = false, DeletionEligible = false };

    // Repair's listing marks these rows through its folder sets; a targeted row asks at its point of use.
    private bool UnderUnreadable(Row row)
    {
        if (context.Store.UnreadableTouches(_pairId, row.Key, false)) return true;
        if (row.Local is { } local && PathNames.Key(local.Path) is var key && key != row.Key &&
            context.Store.UnreadableTouches(_pairId, key, false)) return true;
        return row.Synced is { } saved && saved.LocalKey != row.Key &&
            context.Store.UnreadableTouches(_pairId, saved.LocalKey, false);
    }

    /// <summary>Whether a retained subtree still identified by this scan's entry covers the key.</summary>
    public bool UnderRetainedSubtree(IReadOnlyDictionary<string, IssueSubject> retained, string key)
    {
        var covered = false;
        foreach (var root in CoveringRoots(retained, key))
        {
            if (!Known(root.Key, out var holds))
                holds = Remember(root.Key, RootHolds(context.Store.ScanEntry(_pairId, Side.Local, root.Key), retained));
            covered |= holds;
        }
        return covered;
    }

    private static IEnumerable<(string Key, string Path)> CoveringRoots(
        IReadOnlyDictionary<string, IssueSubject> retained, string key)
    {
        foreach (var subject in IssuePolicy.CoveringSubtrees(retained, key))
            foreach (var item in subject.Items)
                if (item.Side == Side.Local) yield return (PathNames.Key(item.Path), item.Path);
    }

    private static bool RootHolds(Entry? entry, IReadOnlyDictionary<string, IssueSubject> retained) =>
        entry is not null && IssuePolicy.FirstRunKind(
            Row.ForOneSide(Side.Local, PathNames.Key(entry.Path), entry, null, [], null, RowContext.Neutral),
            retained, false) == IssueSubject.FirstRunDeletionKind.UnscannedSubtree;

    private bool Known(string root, out bool holds)
    {
        lock (_gate) return _roots.TryGetValue(root, out holds);
    }

    private bool Remember(string root, bool holds)
    {
        lock (_gate) _roots[root] = holds;
        return holds;
    }

    private RowContext Build(Row row, IReadOnlyDictionary<string, IssueSubject> retained, bool underRetainedSubtree,
        bool underUnreadable, bool inventory = true)
    {
        var kept = retained.Count != 0
            ? IssuePolicy.FirstRunKind(row, retained, underRetainedSubtree)
            : IssueSubject.FirstRunDeletionKind.None;
        var heldKind = context.Store.HeldKind(_pairId, row, RetriesFor(row));
        lock (_gate)
        {
            // A row under two deleting folders cannot happen: a folder is deferred only while present on one side.
            var underFolderDelete = inventory && DeletingLocal.Covers(row.Key) ? Side.Local
                : inventory && DeletingDropbox.Covers(row.Key) ? Side.Dropbox
                : (Side?)null;
            var skipped = underUnreadable || row.Local?.Skip is not null || row.Dropbox?.Skip is not null ||
                inventory && (SkippedLocal.Covers(row.Key) || SkippedDropbox.Covers(row.Key));
            // A rename footprint names no kind: a prefix with no recorded kind counts as Tie (2.4).
            if (heldKind is null && IssuePolicy.CoveredKey(row, _renameHolds) is not null) heldKind = IssueKind.Tie;
            // A failed Local-folder-to-file replacement outranks every ordinary hold, whatever alias carries the other
            // kind, so no issue's refresh rule can release a dependent row while its prerequisite is unresolved.
            if (_dependentRoots is { } dependents && IssuePolicy.CoveredKey(row, dependents) is not null)
                heldKind = IssueKind.Failure;
            return RowContext.ForRow(row, context.Scope, _ordinary, skipped, heldKind, underFolderDelete,
                DeletionEligible(row, kept, inventory), kept);
        }
    }

    // Membership has authority only over a completed acquisition (RQ-178, engine 8.1): before one a selected row waits
    // in None(Held); after one a match selects and a nonmatch is ordinary first-run sync, retained or not.
    private bool DeletionEligible(Row row, IssueSubject.FirstRunDeletionKind kept, bool inventory)
    {
        if (!IssuePolicy.InFirstRunScope(row, firstRun, kept)) return false;
        if (!inventory || !completeInventory) return true;
        var probe = deletedName;
        if (probe is null) return false;
        var path = row.Local!.Path;
        foreach (var ancestor in PathNames.Ancestors(path))
            if (probe(PathNames.DropboxKey(ancestor))) return true;
        return probe(PathNames.DropboxKey(path));
    }
}
