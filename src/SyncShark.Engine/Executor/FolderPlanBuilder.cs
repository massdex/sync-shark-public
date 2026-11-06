using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Captures and freezes a deleted folder's contents before any effects (section 7.6 step 3).</summary>
public sealed class FolderPlanBuilder
{
    private readonly ExecutorContext _context;

    /// <summary>Creates a builder over one pair's executor context.</summary>
    public FolderPlanBuilder(ExecutorContext context) => _context = context;

    /// <summary>Lists the folder again, classifies every descendant and guards the frozen forget footprint.</summary>
    public Task<FolderPlan> BuildAsync(Row folder, Side deletingFrom, IReadOnlySet<string> restored,
        bool live, CancellationToken ct) => BuildCoreAsync(folder, deletingFrom, restored, live, false, null, null, ct);

    internal Task<FolderPlan> BuildSelectedReplacementAsync(Row folder, IReadOnlySet<string> restored,
        CancellationToken ct) => BuildCoreAsync(folder, Side.Dropbox, restored, false, true, null, null, ct);

    internal Task<FolderPlan> BuildExcludedAsync(FolderPlan captured, Exclusion exclusion, CancellationToken ct) =>
        BuildCoreAsync(captured.Folder, exclusion.Side, System.Collections.Frozen.FrozenSet<string>.Empty,
            false, false, exclusion, captured, ct);

    private async Task<FolderPlan> BuildCoreAsync(Row folder, Side deletingFrom, IReadOnlySet<string> restored,
        bool live, bool selectedReplacement, Exclusion? exclusion, FolderPlan? captured, CancellationToken ct)
    {
        // FolderSync owns re-observation under the folder lock, including the restored-descendant exception and fallback.
        var parent = folder.EntryOn(deletingFrom)!;
        ct.ThrowIfCancellationRequested();
        ActionExecutor.RequireReadableIdentity(_context.Store, _context.Pair.PairId, folder, deletingFrom);
        FolderPlan plan;
        try
        {
            plan = captured ?? _context.Store.OpenFolderPlan(_context.Pair.PairId, folder, deletingFrom);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ItemFailedException(parent.Path, Text.ReadFailedOn(Side.Local), error);
        }

        try
        {
            try
            {
                if (captured is null)
                    await plan.CaptureAsync(_context.For(deletingFrom).ReadAllAsync(parent.Path,
                        new ListOptions(Recursive: true, IncludeOutOfScope: true), ct), ct).ConfigureAwait(false);
            }
            catch (InventoryIncompleteException error)
            {
                throw ActionExecutor.InventoryFailure(error, parent.Path, deletingFrom);
            }

            var unreadable = UnreadableAnswer(folder, deletingFrom);
            var scope = exclusion is null ? _context.Scope : _context.Scope.Without(exclusion);
            var hasSurvivors = false;
            foreach (var candidate in plan.Children(ct))
            {
                var (child, followUp) = await ClassifyAsync(folder, deletingFrom, candidate.Entry,
                    candidate.Synced, candidate.Key, restored, unreadable, scope, live, selectedReplacement, ct).ConfigureAwait(false);
                // The next exclusion application preserves arrivals; ordinary sync excludes them.
                plan.MarkClassified(candidate.Key, candidate.NativeKey, child, exclusion is null && followUp, ct);
                hasSurvivors |= child.Verdict == FolderVerdict.Survives;
            }

            if (hasSurvivors)
            {
                plan.PrepareBranches(ct);
                foreach (var candidate in plan.RemovedChildren(ct))
                {
                    var protectedBranch = plan.HasSurvivorAtOrBelow(candidate.Key, ct);
                    if (FolderDeletes.SelectBranch(candidate.Child.Verdict, protectedBranch,
                        !protectedBranch && plan.HasSelectedAncestor(candidate.Key, ct)))
                        plan.SelectBranch(candidate.Id, ct);
                }
            }
            plan.Complete(FolderDeletes.DeleteWhole(hasSurvivors), ct);
            ActionExecutor.RequireReadable(plan, ct);
            return plan;
        }
        catch (Exception primary)
        {
            try
            {
                plan.Dispose();
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(primary, cleanup);
            }
            throw;
        }
    }

    // Under the folder lock: one range for this plan's children; a cross-linked child outside it asks for itself.
    private Func<string, bool> UnreadableAnswer(Row folder, Side side)
    {
        var pairId = _context.Pair.PairId;
        var root = side == Side.Local ? PathNames.Key(folder.Local!.Path) : folder.Synced?.LocalKey ?? folder.Key;
        var above = folder.Local is null && folder.Synced is null &&
            _context.Store.UnreadableTouches(pairId, root, false);
        var recorded = new PrefixSet();
        foreach (var key in _context.Store.UnreadableUnder(pairId, root)) recorded.Add(key);
        return key => PathNames.IsSelfOrUnderKey(key, root) ? above || recorded.Covers(key)
            : _context.Store.UnreadableTouches(pairId, key, false);
    }

    /// <summary>Classifies one child from Repair evidence: a Repair scan is never live evidence, so the scan probe
    /// runs only for a child without a saved record and the remaining facts decide from the current or saved path.</summary>
    private async ValueTask<(FolderChild Child, bool FollowUp)> ClassifyAsync(Row folder, Side side, Entry? entry,
        SyncedEntry? synced, string key, IReadOnlySet<string> restored, Func<string, bool> unreadable,
        PathScope scope, bool live, bool selectedReplacement, CancellationToken ct)
    {
        var localPath = side == Side.Local ? entry?.Path ?? synced?.LocalPath : synced?.LocalPath;
        var dropboxPath = side == Side.Dropbox ? entry?.Path ?? synced?.DropboxPath : synced?.DropboxPath;
        var path = (side == Side.Local ? localPath : dropboxPath)!;
        var inScope = !scope.Excludes(localPath, dropboxPath, entry?.IsFolder ?? synced!.IsFolder);
        var nested = _context.Scope.OverlapsNestedRoot(side, path, key);
        var isUnreadable = side == Side.Local
            ? unreadable(key)
            : synced is not null ? unreadable(synced.LocalKey) : selectedReplacement && unreadable(key);
        var facts = new FolderChildFacts(
            selectedReplacement || !live && synced is null && _context.Store.InScan(_context.Pair.PairId, side, key),
            inScope, nested, restored.Contains(synced?.LocalKey ?? key), isUnreadable);
        string? localHash = null;
        var dirty = false;
        if (side == Side.Local && inScope && !nested && !facts.Restored && !facts.Unreadable
            && entry is { IsFolder: false, Skip: null } && synced is { IsFolder: false })
        {
            var before = _context.Evidence.Read(entry, synced);
            dirty = before.Dirty;
            if (Decide.LocalChanged(entry, synced, null, dirty) is null)
            {
                localHash = (await _context.Evidence.HashAsync(_context.Local, entry, synced, before, ct).ConfigureAwait(false)).Hash;
            }
        }
        // A replacement identity is new content even when its bytes match the displaced binding.
        var classifiedSaved = live && entry is not null && synced is not null && entry.Id != synced.IdOn(side)
            ? null : synced;
        var row = Row.ForOneSide(side, synced?.LocalKey ?? key, entry, classifiedSaved, [], localHash, folder.Context);
        var verdict = FolderDeletes.Classify(row, side, facts, dirty);
        return (new FolderChild(entry, synced, verdict),
            FolderDeletes.FollowUp(verdict, entry is not null, facts.Restored, facts.Unreadable));
    }
}
