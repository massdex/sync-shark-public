namespace Massdex.SyncShark.V2;

/// <summary>Projects active reconciliation knowledge into the ordinary planner's coverage question.</summary>
internal sealed class ReconciliationCoverage
{
    private readonly StateMonitorDecision _decision;
    private readonly bool _selecting;
    private readonly string _session;
    private readonly SqliteStateStore _store;
    private readonly IReadOnlySet<Side> _sides;
    private readonly SyncRun.BothScan _scan;
    private readonly Func<string, bool>? _unobservedAlias;
    private readonly Dictionary<Side, HashSet<string>> _changed;
    private readonly Dictionary<(Side Side, string Path), bool> _loaded = new();

    internal ReconciliationCoverage(StateMonitorDecision decision, bool selecting, string session,
        SqliteStateStore store, IReadOnlySet<Side> sides, SyncRun.BothScan scan, Func<string, bool>? unobservedAlias = null)
    {
        _decision = decision; _selecting = selecting; _session = session; _store = store; _sides = sides; _scan = scan;
        _unobservedAlias = unobservedAlias;
        _changed = new()
        {
            [Side.A] = scan.A.ChangePaths.ToHashSet(StringComparer.OrdinalIgnoreCase),
            [Side.B] = scan.B.ChangePaths.ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
    }

    internal static void ObserveBatch(ReconcileBatch batch, SyncRun.SideScan a, SyncRun.SideScan b) =>
        batch.GapCoverage?.Observe(a.ExactCreationObserved.Intersect(b.ExactCreationObserved, StringComparer.OrdinalIgnoreCase)
            .Where(path => PairPath.Observed(path, a.IncompleteSubtrees) && PairPath.Observed(path, b.IncompleteSubtrees)),
            batch.ObjectIdsA.Concat(batch.ObjectIdsB).Distinct().ToArray());

    internal void Load(RunScope.Scope scope)
    {
        if (_selecting) return;
        var paths = scope.A.Concat(scope.B).SelectMany(item => new[] { item.Path, DropboxNameMapping.MatchPath(item) })
            .Concat(scope.Baselines.SelectMany(basis => new[] { basis.A.Path, basis.B.Path }))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _loaded.Clear();
        foreach (var side in _sides)
        {
            var remaining = Remaining(side, paths);
            foreach (var path in paths) _loaded[(side, PathKey.Of(path))] = remaining.Contains(path);
        }
    }

    internal bool Unknown(Side side, string path)
    {
        if (_unobservedAlias?.Invoke(path) == true) return true;
        if (!_sides.Contains(side) || Fresh(side, path)) return false;
        if (_selecting) return (_decision.UnknownScope ?? ReconciliationScope.Root).Contains(path);
        var key = (side, PathKey.Of(path));
        if (!_loaded.TryGetValue(key, out var unknown))
            _loaded[key] = unknown = Remaining(side, [path]).Count > 0;
        return unknown;
    }

    private HashSet<string> Remaining(Side side, IReadOnlyList<string> paths) =>
        _store.StagedAmong(_session + ":work", side, paths)
            .Concat(_store.StagedAmong(_session + ":deferred", side, paths))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private bool Fresh(Side side, string path)
    {
        var scan = side == Side.A ? _scan.A : _scan.B;
        return _changed[side].Contains(path) || scan.ExactCreationObserved.Contains(path) ||
            scan.Full && scan.Complete && PairPath.Observed(path, scan.IncompleteSubtrees);
    }

    private bool FolderDependency(string path) => _sides.Any(side => _selecting
        ? (_decision.UnknownScope ?? ReconciliationScope.Root).Touches(path, folder: true)
        : Unknown(side, path) || _store.StagedUnder(_session + ":work", side, path) ||
            _store.StagedUnder(_session + ":deferred", side, path));

    internal Plan DeferDependencies(Plan plan)
    {
        var deferred = plan.Actions.Where(action =>
            _unobservedAlias?.Invoke(action.Path) == true ||
            action.FromPath is { } alias && _unobservedAlias?.Invoke(alias) == true ||
            action.Source is { } source && _unobservedAlias?.Invoke(source.Path) == true ||
            action.ExpectedTarget is { } target && _unobservedAlias?.Invoke(target.Path) == true ||
            action.FromPath is { } from && Unknown(action.Target, from) ||
            action.Kind is ActionKind.Move or ActionKind.Delete &&
            (action.Source?.Kind == ItemKind.Folder || action.ExpectedTarget?.Kind == ItemKind.Folder) &&
            (FolderDependency(action.Path) || action.FromPath is { } folder && FolderDependency(folder)))
            .ToArray();
        if (deferred.Length == 0) return plan;
        RunTrace.Emit(MessageCatalog.Default.Template("reconciliation-deferred-dependencies") + deferred.Length);
        var ids = deferred.Select(action => action.ActionId).ToHashSet(StringComparer.Ordinal);
        return plan with
        {
            Actions = plan.Actions.Where(action => !ids.Contains(action.ActionId)).ToArray(),
            DeferredPlanningActions = plan.DeferredPlanningActions.Concat(deferred).ToArray()
        };
    }
}
