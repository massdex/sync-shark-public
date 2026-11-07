using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Owns pending-ancestor refusal and the current listing or selection coverage.</summary>
internal sealed class AncestorFence(ExecutorContext _context)
{
    private long? _listedThrough;
    private readonly AsyncLocal<System.Runtime.CompilerServices.StrongBox<WorkSelection?>?> _selection = new();

    /// <summary>The ancestor fence. Outside Repair any pending ancestor but the running selection's own refuses. Repair's coverage omits work its
    /// listing saw; later work refuses this row and the Repair ends incomplete, so its obligation covers the row
    /// next time. The fence never executes or settles the ancestor selection.</summary>
    internal bool AncestorsClear(Row row, string? target, Side? targetSide)
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var ancestors = DependencyKeys(side, row.EntryOn(side)?.Path, null, row.Synced,
                row.Context.Destination, target, targetSide);
            if (ancestors.Length == 0 || AncestorPending(side, ancestors) is not { } blocker) continue;
            if (_listedThrough is null) throw Stale(blocker, side);
            AncestorRefused = true;
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

    internal static IEnumerable<(Side Side, string[] Keys)> NoticeDependencies(ChangeNotice notice,
        SyncedEntry? saved, Parents parents)
    {
        var path = notice.Observed?.Path ?? notice.Path;
        var destination = parents.Destination(PathNames.Other(notice.Side), notice.Path);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
            yield return (side, DependencyKeys(side, side == notice.Side ? path : null,
                side == notice.Side ? notice.OldPath : null, saved, destination, null, null));
    }

    /// <summary>The same fence before any provider call, from the notice's own side and path evidence. Repair keeps
    /// its later path, which raises the item's notice.</summary>
    internal void RequireClear(ChangeNotice notice, SyncedEntry? saved, Parents parents)
    {
        if (_listedThrough is not null) return;
        foreach (var (check, ancestors) in NoticeDependencies(notice, saved, parents))
        {
            if (ancestors.Length == 0 || AncestorPending(check, ancestors) is not { } blocker) continue;
            throw Stale(blocker, check);
        }
    }

    internal static LiveAdmissionStaleException Stale(WorkSelection blocker, Side side,
        IEnumerable<WorkSelection>? dependencies = null)
    {
        var detail = Text.Get(TextKey.StaleAncestor, (TextArg.Path, blocker.Key), (TextArg.Side, side.ToString()));
        if (dependencies is not null)
            detail = string.Join(Environment.NewLine, dependencies.Where(work => work.AnchorWorkId != blocker.AnchorWorkId)
                .Select(work => Text.Get(TextKey.StaleAncestor, (TextArg.Path, work.Key),
                    (TextArg.Side, work.Side.ToString()))).Prepend(detail));
        return new(LiveAdmissionStaleReason.RelevantEvidenceChanged, detail);
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
                if (answer.News == AncestorNews.Changed) return Refuse();
                if (answer.News != AncestorNews.Unbound)
                {
                    if (answer.News == AncestorNews.Restated) news = AncestorNews.Restated;
                    break;
                }
                joined ??= new(StringComparer.Ordinal);
                if (!joined.Add(answer.UnboundKey!)) return Refuse();
                await parents.JoinCreationAsync(side, answer.UnboundKey!, ct);
            }
        }
        return news;
    }

    internal AncestorNews Refuse()
    {
        AncestorRefused = true;
        return AncestorNews.Changed;
    }

    /// <summary>A scoped pass leaves a row owned by a pending ancestor to that ancestor's ordinary selection.</summary>
    internal bool BatchAdmits(Row row)
    {
        var paths = Row.Paths(row);
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            var keys = DependencyKeys(side, side == Side.Local ? paths.Local : paths.Dropbox, null, row.Synced,
                row.Context.Destination, null, null);
            if (keys.Length == 0 || AncestorPending(side, keys) is not { } owner) continue;
            if (_selection.Value?.Value is { } own && owner.AnchorWorkId > own.ThroughWorkId) throw Stale(owner, side);
            return false;
        }
        return true;
    }

    // A selection's own work never refuses its rows (8.7); work arriving after it, or anyone else's, still does.
    private WorkSelection? AncestorPending(Side side, string[] keys)
    {
        var pending = _context.Store.PendingAncestors(_context.Pair.PairId, _context.ExpectedGeneration, side, keys,
            _listedThrough);
        if (_selection.Value?.Value is not { } own) return pending.FirstOrDefault();
        WorkSelection? fallback = null;
        foreach (var owner in pending)
        {
            if (owner.AnchorWorkId != own.AnchorWorkId)
            {
                if (owner.Side != own.Side && own.Peer is { } peer && owner.Key == peer.Key &&
                    !_context.Store.PendingAncestors(_context.Pair.PairId, _context.ExpectedGeneration,
                        side, keys, peer.ThroughWorkId).Any()) continue;
                return owner;
            }
            fallback ??= owner;
        }
        return fallback is null ? null : _context.Store.PendingAncestors(_context.Pair.PairId, _context.ExpectedGeneration,
            side, keys, own.ThroughWorkId).FirstOrDefault();
    }

    /// <summary>The current flow's selection; peer updates remain visible across nested continuations.</summary>
    internal void RunSelection(WorkSelection? selection)
    {
        if (_selection.Value is { } box &&
            (selection is null || box.Value?.AnchorWorkId == selection.AnchorWorkId)) box.Value = selection;
        else _selection.Value = new(selection);
    }

    /// <summary>Repair's listing covers acquired work through its pre-list watermark, so the ancestor fence refuses
    /// only later arrivals in this session. No other path sets it.</summary>
    internal void CoverListedWork(long watermark) => _listedThrough = watermark;

    /// <summary>True once a Repair refused a row on an unresolved ancestor; that Repair must not clear its obligation.</summary>
    internal bool AncestorRefused { get; private set; }

}
