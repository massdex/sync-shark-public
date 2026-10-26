namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    internal async Task<SelectionCounts> SelectAsync(PairDef pair, Side side, StateMonitorDecision decision,
        string session, ProgressThrottle throttle, CancellationToken ct, Action<string>? listingBasis = null)
    {
        var sql = _store as SqliteStateStore ?? throw new InvalidOperationException("Reconciliation requires persistent staging.");
        var root = side == Side.A ? pair.A : pair.B;
        var scope = decision.UnknownScope ?? ReconciliationScope.Root;
        var inventory = _endpoints.Inventory(root);
        var eligibility = new ItemEligibility(_store.Pairs(), _productOwned(pair));
        var accounted = SelectionAccounted(pair, side);
        var counts = new SelectionCounts(0, 0, 0, 0, 0);
        Task accept(IReadOnlyList<ItemState> rows)
        {
            foreach (var page in rows.Where(item => scope.Contains(item.Path)).Chunk(V2Settings.PlanningObservationBatchRows))
            {
                ct.ThrowIfCancellationRequested();
                sql.StageReconcile(session + ":seen", side, page);
            }
            return Task.CompletedTask;
        }
        try
        {
            if (decision.Trigger == ReconciliationTrigger.PlanningBacklog)
                return SelectPlanningBacklog(pair, side, scope, session, sql, ct);
            if (RehearsalEndpoints.Unwrap(inventory) is LocalInventory local)
                counts = await SelectLocalAsync(pair, side, local, decision, scope, session, sql, eligibility, accounted, ct);
            else
            {
                await EnumerateSelectionAsync(root, inventory, scope, accept, throttle, ct, listingBasis);
                counts = SelectStaged(pair, side, session, sql, eligibility, accounted,
                    RehearsalEndpoints.Unwrap(inventory) is DropboxInventory, ct);
            }
            counts = counts with { Missing = SelectMissing(pair, side, scope, session, sql, eligibility, ct) };
            RunTrace.Emit($"reconciliation selection: enumerated={counts.Enumerated} changed={counts.Changed} new={counts.New} missing={counts.Missing} unaffected={counts.Unaffected} S-orphan={counts.Orphan} S-moved={counts.Moved} S-case={counts.Case}");
            return counts;
        }
        finally { sql.ClearRemovalObservation(session + ":seen"); }
    }

    private SelectionCounts SelectPlanningBacklog(PairDef pair, Side side, ReconciliationScope scope,
        string session, SqliteStateStore sql, CancellationToken ct)
    {
        var root = side == Side.A ? pair.A : pair.B;
        long afterRoot = -1, selected = 0, enumerated = 0;
        var afterId = string.Empty;
        while (sql.PlanningBacklogPage(pair.PairId, afterRoot, afterId) is { Count: > 0 } page)
        {
            ct.ThrowIfCancellationRequested();
            var ids = page.Where(row => row.RootId == root.RootId).Select(row => row.ObjectId).ToArray();
            var rows = sql.DeferredPlanningFor(pair.PairId, root.RootId, ids);
            enumerated += rows.Count;
            foreach (var row in rows)
            {
                var current = _store.ByObject(root.RootId, row.ObjectId);
                var paths = row.Paths.Count > 0 ? row.Paths : new[] { current?.Path ?? row.Path };
                var items = paths.Where(scope.Contains).Select(path => current is not null
                    ? current with { Path = path }
                    : new ItemState(path, ItemKind.File, row.ObjectId, null, null, 0,
                        DateTimeOffset.MinValue, ItemFlags.None)).ToArray();
                sql.StageReconcile(session + ":work", side, items);
                selected += items.Length;
            }
            afterRoot = page[^1].Root; afterId = page[^1].ObjectId;
        }
        RunTrace.Emit($"reconciliation backlog selection: enumerated={enumerated} selected={selected}");
        return new(enumerated, selected, 0, 0, enumerated - Math.Min(enumerated, selected));
    }

    private async Task EnumerateSelectionAsync(RootDef root, IInventorySource inventory, ReconciliationScope scope,
        Func<IReadOnlyList<ItemState>, Task> accept, ProgressThrottle throttle, CancellationToken ct,
        Action<string>? listingBasis)
    {
        if (RehearsalEndpoints.Unwrap(inventory) is DropboxInventory dropbox)
        {
            RunTrace.Emit("reconciliation selection source: Dropbox prefix hydration");
            var failures = await RehearsalEndpoints.HydrateAsync(inventory, dropbox, scope.ReadScopes.ToArray(), rows =>
                accept(rows.Where(row => row.Entry is not null).Select(row => DropboxItems.ToItem(row.Entry!)).ToArray()), ct, listingBasis);
            if (failures.Count > 0) throw new IOException(string.Join("; ", failures.Values));
            return;
        }
        RunTrace.Emit("reconciliation selection source: full metadata inventory");
        var snapshot = await inventory.FullAsync(0, path => scope.Touches(path, folder: true),
            new InventoryProgress(throttle, root), ct);
        if (snapshot.IncompleteSubtrees.Count > 0)
            throw new IOException(string.Join("; ", snapshot.IncompleteReasons.Values));
        listingBasis?.Invoke(snapshot.CursorAfter);
        foreach (var page in snapshot.Items.Chunk(V2Settings.PlanningObservationBatchRows)) await accept(page);
    }

    private SelectionCounts SelectStaged(PairDef pair, Side side, string session, SqliteStateStore sql,
        ItemEligibility eligibility, (HashSet<string> Paths, HashSet<string> Ids) accounted, bool dropbox, CancellationToken ct)
    {
        var counts = new SelectionCounts(0, 0, 0, 0, 0);
        string key = "", path = "";
        while (sql.ReconcilePage(session + ":seen", side, key, path, V2Settings.PlanningObservationBatchRows) is { Count: > 0 } page)
        {
            ct.ThrowIfCancellationRequested();
            counts = SelectPage(pair, side, page, session, sql, eligibility, accounted, counts, dropbox);
            key = PathKey.Of(page[^1].Path); path = page[^1].Path;
        }
        return counts;
    }

    private SelectionCounts SelectPage(PairDef pair, Side side, IReadOnlyList<ItemState> page, string session,
        SqliteStateStore sql, ItemEligibility eligibility, (HashSet<string> Paths, HashSet<string> Ids) accounted,
        SelectionCounts counts, bool dropbox, Func<ItemState, ItemState, bool>? changedLocal = null)
    {
        var root = side == Side.A ? pair.A : pair.B;
        var live = _store.Live(root.RootId, page.Select(item => item.Path)).ToLookup(item => PathKey.Of(item.Path));
        var linked = _store.BaselinesTouching(pair.PairId, side, page.Select(item => item.ObjectId), [])
            .ToLookup(baseline => (side == Side.A ? baseline.A : baseline.B).ObjectId, StringComparer.Ordinal);
        var atPath = _store.BaselinesAt(pair.PairId, side, page.Select(item => item.Path))
            .ToLookup(baseline => PathKey.Of((side == Side.A ? baseline.A : baseline.B).Path));
        var work = new List<ItemState>();
        foreach (var item in page)
        {
            counts = counts with { Enumerated = counts.Enumerated + 1 };
            var baselines = linked[item.ObjectId].ToArray();
            var previous = _store.ByObject(root.RootId, item.ObjectId) ?? live[PathKey.Of(item.Path)].FirstOrDefault();
            var eligible = eligibility.EvaluateObservation(pair, side, item, !_settings.ReplaceOnlineOnlyStubs).Eligible;
            var orphan = eligible && previous is not null && baselines.Length == 0 && !accounted.Ids.Contains(item.ObjectId) &&
                !accounted.Paths.Contains(item.Path) && !_store.SyncRetryExists(pair.PairId, item.Path);
            var moved = eligible && baselines.Any(baseline => SelectionMoved(pair, side, item, baseline));
            var casing = eligible && baselines.Any(baseline => SelectionCase(pair, side, item, baseline));
            var added = eligible && previous is null && !atPath[PathKey.Of(item.Path)].Any() && baselines.Length == 0;
            var changed = eligible && (previous is null ||
                (changedLocal?.Invoke(item, previous) ?? SelectionChanged(item, previous, dropbox)));
            if (added || changed || orphan || moved || casing) work.Add(item);
            counts = counts with { New = counts.New + (added ? 1 : 0),
                Changed = counts.Changed + (!added && (changed || orphan || moved || casing) ? 1 : 0),
                Unaffected = counts.Unaffected + (!(added || changed || orphan || moved || casing) ? 1 : 0),
                Orphan = counts.Orphan + (orphan ? 1 : 0), Moved = counts.Moved + (moved ? 1 : 0), Case = counts.Case + (casing ? 1 : 0) };
        }
        sql.StageReconcile(session + ":work", side, work);
        return counts;
    }

    private static bool SelectionChanged(ItemState current, ItemState previous, bool dropbox) => dropbox
        ? current.VersionId != previous.VersionId || current.Hash != previous.Hash
        : current.ObjectId != previous.ObjectId || current.Size != previous.Size || current.Mtime != previous.Mtime;

    private bool SelectionMoved(PairDef pair, Side side, ItemState item, Baseline baseline)
    {
        var own = side == Side.A ? baseline.A : baseline.B;
        var other = side == Side.A ? baseline.B : baseline.A;
        var root = side == Side.A ? pair.B : pair.A;
        return item.Path != own.Path || _store.ByObject(root.RootId, other.ObjectId) is { } current && current.Path != other.Path;
    }

    private bool SelectionCase(PairDef pair, Side side, ItemState item, Baseline baseline)
    {
        var own = side == Side.A ? baseline.A : baseline.B;
        var other = side == Side.A ? baseline.B : baseline.A;
        var root = side == Side.A ? pair.B : pair.A;
        var currentOther = _store.ByObject(root.RootId, other.ObjectId);
        var leafA = Path.GetFileName(DropboxNameMapping.MatchPath(baseline.A));
        var leafB = Path.GetFileName(DropboxNameMapping.MatchPath(baseline.B));
        return PairPath.Same(leafA, leafB) && leafA != leafB && SelectionMatchesBaseline(item, own) &&
            currentOther is not null && SelectionMatchesBaseline(currentOther, other);
    }

    private static bool SelectionMatchesBaseline(ItemState item, ItemState baseline) => item.Path == baseline.Path &&
        !SelectionChanged(item, baseline, false) && !SelectionChanged(item, baseline, true);

    private (HashSet<string> Paths, HashSet<string> Ids) SelectionAccounted(PairDef pair, Side side)
    {
        var paths = _store.UnresolvedNotices(pair.PairId).Where(PlanNotices.HasPathScope)
            .Where(notice => notice.Path.Length > 0 && !string.IsNullOrEmpty(notice.ReasonKey))
            .Select(notice => notice.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in _store.StreamUnresolvedIntents(pair.PairId))
        {
            var item = action.Target == side ? action.ExpectedTarget : action.Source;
            if (item is not null) { ids.Add(item.ObjectId); paths.Add(item.Path); }
            if (action.Target == side) paths.Add(action.Path);
        }
        return (paths, ids);
    }

    private long SelectMissing(PairDef pair, Side side, ReconciliationScope scope, string session,
        SqliteStateStore sql, ItemEligibility eligibility, CancellationToken ct)
    {
        var root = side == Side.A ? pair.A : pair.B;
        long count = 0;
        var scopes = scope.ReadScopes.ToArray();
        foreach (var prefix in scopes.Where(path => !scopes.Any(parent => parent != path && PairPath.IsUnder(path, parent))))
        {
            long dir = 0;
            string key = "", name = "";
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = sql.MissingPage(session + ":seen", side, root.RootId, prefix, dir, key, name);
                if (page.Count == 0) break;
                var missing = page.Where(row => !row.Seen && scope.Contains(row.Row.Item.Path) &&
                    eligibility.Evaluate(pair, side, row.Row.Item, ItemEligibility.Purpose.Plan).Eligible)
                    .Select(row => row.Row.Item).ToArray();
                sql.StageReconcile(session + ":work", side, missing);
                count += missing.Length;
                var last = page[^1].Row;
                dir = last.DirNo; key = last.NameKey; name = last.Name;
            }
        }
        return count;
    }
}
