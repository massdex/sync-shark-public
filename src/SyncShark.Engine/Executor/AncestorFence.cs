using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Owns pending-ancestor refusal and the current listing or selection coverage.</summary>
internal sealed class AncestorFence(ExecutorContext _context)
{
    private long? _listedThrough;
    private readonly AsyncLocal<System.Runtime.CompilerServices.StrongBox<WorkSelection?>?> _selection = new();
    private readonly AsyncLocal<Action?> _refused = new();

    internal void ReportContainedRefusal() => _refused.Value?.Invoke();

    /// <summary>The ancestor fence. Outside Repair any pending ancestor but the running selection's own refuses. Repair's coverage omits work its
    /// listing saw; later work refuses this row. The fence never executes or settles the ancestor selection.</summary>
    internal bool AncestorsClear(Row row, string? target, Side? targetSide, bool restatable = false)
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var ancestors = DependencyKeys(side, row.EntryOn(side)?.Path, null, row.Synced,
                row.Context.Destination, target, targetSide);
            if (ancestors.Length == 0 || AncestorPending(side, ancestors, restatable) is not { } blocker) continue;
            if (_listedThrough is null) throw Stale(blocker, side);
            return false;
        }
        return true;
    }

    /// <summary>The ancestor keys one side must look up for an item: its path on that side, its old path, its saved
    /// path, its mapped destination when that side has no entry, and the action target on a written side. The one
    /// owner of that key set, shared by the execution fence, the pre-observation refusal and the pre-run.</summary>
    internal static string[] DependencyKeys(Side side, string? path, string? oldPath, SyncedEntry? saved,
        string? destination, string? target, Side? targetSide)
    {
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        Add(path);
        Add(oldPath);
        Add(saved?.PathOn(side));
        if (path is null) Add(destination);
        if (side == targetSide) Add(target);
        return ancestors.Count == 0 ? Array.Empty<string>() : [.. ancestors];

        void Add(string? value)
        {
            if (value is null) return;
            foreach (var ancestor in PathNames.Ancestors(value)) ancestors.Add(PathNames.KeyFor(side, ancestor));
        }
    }

    internal static IEnumerable<(Side Side, string[] Keys)> NoticeDependencies(ChangeNotice? notice,
        SyncedEntry? saved, Parents? parents)
    {
        var path = notice?.Observed?.Path ?? notice?.Path;
        var destination = notice is null ? null : parents!.Destination(PathNames.Other(notice.Side), notice.Path);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
            yield return (side, DependencyKeys(side, side == notice?.Side ? path : null,
                side == notice?.Side ? notice?.OldPath : null, saved, destination, null, null));
    }

    /// <summary>The same fence before any provider call, from the notice's own side and path evidence. Repair keeps
    /// its later path, which raises the item's notice.</summary>
    internal void RequireClear(ChangeNotice? notice, SyncedEntry? saved, Parents? parents)
    {
        if (_listedThrough is not null || notice is null && saved is null) return;
        foreach (var (check, ancestors) in NoticeDependencies(notice, saved, parents))
        {
            if (ancestors.Length == 0 || AncestorPending(check, ancestors) is not { } blocker) continue;
            throw Stale(blocker, check);
        }
    }

    /// <summary>The shallowest dependency, shared by scheduling and survivor cycle diagnosis.</summary>
    internal (WorkSelection Ancestor, Side Side, string[] Keys)? PendingAncestor(WorkSelection selection, Parents parents)
    {
        (WorkSelection Ancestor, Side Side, string[] Keys)? first = null;
        SyncedEntry? saved = null;
        var readSaved = false;
        foreach (var change in _context.Store.ReadWork(selection))
        {
            if (change.Before is null && !readSaved)
            {
                saved = _context.Store.SelectionBaseline(selection);
                readSaved = true;
            }
            foreach (var (side, keys) in NoticeDependencies(change.Notice, change.Before ?? saved, parents))
            {
                if (keys.Length == 0) continue;
                foreach (var candidate in _context.Store.PendingAncestors(_context.Pair.PairId,
                    _context.ExpectedGeneration, side, keys))
                    if (candidate.AnchorWorkId != selection.AnchorWorkId &&
                        (first is null || PathNames.Depth(candidate.Key) < PathNames.Depth(first.Value.Ancestor.Key)))
                        first = (candidate, side, keys);
            }
        }
        return first;
    }

    internal bool ReachesSelection(WorkSelection blocker, WorkSelection own, Parents parents)
    {
        if (blocker.AnchorWorkId == own.AnchorWorkId) return false;
        var visited = new HashSet<long> { blocker.AnchorWorkId };
        while (PendingAncestor(blocker, parents) is { } next)
        {
            if (IsOwnSelection(next.Ancestor, own, next.Side, next.Keys)) return true;
            if (!visited.Add(next.Ancestor.AnchorWorkId)) return false;
            blocker = next.Ancestor;
        }
        return false;
    }

    internal static LiveAdmissionStaleException Stale(WorkSelection blocker, Side side,
        IEnumerable<WorkSelection>? dependencies = null)
    {
        var detail = Text.Get(TextKey.StaleAncestor, (TextArg.Path, blocker.Key), (TextArg.Side, side.ToString()));
        if (dependencies is not null)
            detail = string.Join(Environment.NewLine, dependencies.Where(work => work.AnchorWorkId != blocker.AnchorWorkId)
                .Select(work => Text.Get(TextKey.StaleAncestor, (TextArg.Path, work.Key),
                    (TextArg.Side, work.Side.ToString()))).Prepend(detail));
        return new(LiveAdmissionStaleReason.RelevantEvidenceChanged, detail) { AncestorBlocker = (blocker, side) };
    }


    internal async ValueTask<AncestorNews> BeforeExecutionAsync(SyncAction action, Parents parents, CancellationToken ct)
    {
        Side? targetSide = action.Target is null ? null : action.WrittenSide();
        if (_listedThrough is not { } through || !action.CreatesNewItem())
            return AncestorsClear(action.Row, action.Target, targetSide)
                ? AncestorNews.None : AncestorNews.Changed;
        var news = AncestorNews.None;
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var keys = DependencyKeys(side, action.Row.EntryOn(side)?.Path, null, action.Row.Synced,
                action.Row.Context.Destination, action.Target, targetSide);
            if (keys.Length == 0) continue;
            HashSet<string>? joined = null;
            while (true)
            {
                var answer = _context.Store.LaterAncestorNews(_context.Pair.PairId, _context.ExpectedGeneration, side, keys, through);
                if (answer.News == AncestorNews.Changed) return AncestorNews.Changed;
                if (answer.News != AncestorNews.Unbound)
                {
                    if (answer.News == AncestorNews.Restated) news = AncestorNews.Restated;
                    break;
                }
                joined ??= new(StringComparer.Ordinal);
                if (!joined.Add(answer.UnboundKey!)) return AncestorNews.Changed;
                await parents.JoinCreationAsync(side, answer.UnboundKey!, ct);
            }
        }
        return news;
    }

    /// <summary>A scoped pass leaves a row owned by a pending ancestor to that ancestor's ordinary selection; a create-only
    /// row's later arrivals are answered at execution.</summary>
    internal bool BatchAdmits(Row row)
    {
        var paths = Row.Paths(row);
        var restatable = row.Synced is null && (row.Local is null || row.Dropbox is null);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var keys = DependencyKeys(side, side == Side.Local ? paths.Local : paths.Dropbox, null, row.Synced,
                row.Context.Destination, null, null);
            if (keys.Length == 0 || AncestorPending(side, keys, restatable) is not { } owner) continue;
            if (_selection.Value?.Value is { } own && owner.AnchorWorkId > own.ThroughWorkId) throw Stale(owner, side);
            return false;
        }
        return true;
    }

    // A selection's own work never refuses its rows (8.7); work arriving after it, or anyone else's, still does.
    // A restatable (create-only) row skips arrivals after its selection's pass watermark; the caller answers them.
    private WorkSelection? AncestorPending(Side side, string[] keys, bool restatable = false)
    {
        var pending = _context.Store.PendingAncestors(_context.Pair.PairId, _context.ExpectedGeneration, side, keys,
            _listedThrough);
        if (_selection.Value?.Value is not { } own) return pending.FirstOrDefault();
        WorkSelection? fallback = null;
        foreach (var owner in pending)
        {
            if (owner.AnchorWorkId != own.AnchorWorkId)
            {
                if (restatable && owner.AnchorWorkId > own.ThroughWorkId || IsOwnSelection(owner, own, side, keys)) continue;
                return owner;
            }
            fallback ??= owner;
        }
        return fallback is null || restatable ? null : _context.Store.PendingAncestors(_context.Pair.PairId,
            _context.ExpectedGeneration, side, keys, own.ThroughWorkId).FirstOrDefault();
    }

    private bool IsOwnSelection(WorkSelection owner, WorkSelection own, Side side, string[] keys) =>
        owner.AnchorWorkId == own.AnchorWorkId ||
        owner.Side != own.Side && own.Peer is { } peer && owner.Key == peer.Key &&
        !_context.Store.PendingAncestors(_context.Pair.PairId, _context.ExpectedGeneration,
            side, keys, peer.ThroughWorkId).Any();

    /// <summary>The current flow's selection; peer updates remain visible across nested continuations.</summary>
    internal void RunSelection(WorkSelection? selection, Action? refused = null)
    {
        if (selection is null || refused is not null) _refused.Value = refused;
        if (_selection.Value is { } box &&
            (selection is null || box.Value?.AnchorWorkId == selection.AnchorWorkId)) box.Value = selection;
        else _selection.Value = new(selection);
    }

    /// <summary>Repair's listing covers acquired work through its pre-list watermark, so the ancestor fence refuses
    /// only later arrivals in this session. No other path sets it.</summary>
    internal void CoverListedWork(long watermark) => _listedThrough = watermark;

}
