namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // A moved folder is one metadata unit, not an oversized content-read exemption.
    // Only captured selection and stored identities participate; fresh children retain
    // their journal/AutoSync owner. No recursive provider discovery is performed here.
    private ReconcileBatch MovingFolderBatch(PairDef pair, SqliteStateStore sql,
        ReconcileBatch batch, CancellationToken ct)
    {
        var folders = MovingFolderAliases(pair, sql, batch, ct);
        if (folders.Count == 0) return batch;
        var paths = folders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var a = MovingRows(sql, batch, pair.A, Side.A, folders, paths, ct);
        var b = MovingRows(sql, batch, pair.B, Side.B, folders, paths, ct);
        return new(batch.Session, paths.ToArray(), a.Select(row => row.ObjectId).ToHashSet(StringComparer.Ordinal),
            b.Select(row => row.ObjectId).ToHashSet(StringComparer.Ordinal),
            (side, path) => !paths.Contains(path) && sql.StagedAmong(batch.Session + ":work", side, [path]).Count > 0)
        {
            RowsA = a, RowsB = b, MovingFolders = folders.ToArray(),
            MovingItems = Math.Max(a.Select(row => row.ObjectId).Distinct().Count(), b.Select(row => row.ObjectId).Distinct().Count()),
            GapCoverage = batch.GapCoverage
        };
    }

    private static IReadOnlyList<string> MovingFolderAliases(PairDef pair, SqliteStateStore sql,
        ReconcileBatch batch, CancellationToken ct)
    {
        foreach (var (side, root, rows) in new[] { (Side.A, pair.A, batch.RowsA), (Side.B, pair.B, batch.RowsB) })
        {
            var selected = rows.Where(row => row.Kind == ItemKind.Folder).ToArray();
            if (selected.Length == 0) continue;
            var ids = selected.Select(row => row.ObjectId).ToHashSet(StringComparer.Ordinal);
            var candidates = StagedMovingRows(sql, batch.Session, side, ct)
                .Where(row => row.Kind == ItemKind.Folder && ids.Contains(row.ObjectId)).Concat(selected).ToArray();
            foreach (var group in candidates.GroupBy(row => row.ObjectId))
            {
                var aliases = group.Select(row => row.Path)
                    .Concat(sql.ByObject(root.RootId, group.Key) is { } current ? new[] { current.Path } : [])
                    .Concat(sql.BaselinesTouching(pair.PairId, side, [group.Key], [])
                        .Select(basis => side == Side.A ? basis.A.Path : basis.B.Path))
                    .Distinct(StringComparer.Ordinal).ToArray();
                if (aliases.Length > 1) return aliases;
            }
        }
        return [];
    }

    private static ItemState[] MovingRows(SqliteStateStore sql, ReconcileBatch batch, RootDef root,
        Side side, IReadOnlyList<string> folders, HashSet<string> paths, CancellationToken ct)
    {
        var rows = StagedMovingRows(sql, batch.Session, side, ct)
            .Where(row => folders.Any(folder => PairPath.IsSelfOrUnder(row.Path, folder))).ToList();
        foreach (var folder in folders)
        {
            long dir = -1; string key = "", name = "";
            while (sql.MissingPage(batch.Session + ":work", side, root.RootId, folder, dir, key, name) is { Count: > 0 } page)
            {
                ct.ThrowIfCancellationRequested();
                rows.AddRange(page.Select(entry => entry.Row.Item));
                var last = page[^1].Row;
                dir = last.DirNo; key = last.NameKey; name = last.Name;
            }
        }
        foreach (var row in rows)
        {
            paths.Add(row.Path);
            foreach (var from in folders.Where(folder => PairPath.IsSelfOrUnder(row.Path, folder)))
                foreach (var to in folders) paths.Add(PairPath.Rebase(row.Path, from, to));
        }
        return rows.DistinctBy(row => (row.ObjectId, row.Path)).ToArray();
    }

    private static IEnumerable<ItemState> StagedMovingRows(SqliteStateStore sql, string session,
        Side side, CancellationToken ct)
    {
        string key = "", path = "";
        while (sql.ReconcilePage(session + ":work", side, key, path, V2Settings.PlanningObservationBatchRows) is { Count: > 0 } page)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var row in page) yield return row;
            key = PathKey.Of(page[^1].Path); path = page[^1].Path;
        }
    }

    private static Plan ProtectMovingUnit(Plan plan, ReconcileBatch? batch)
    {
        if (batch is not { MovingUnit: true }) return plan;
        bool member(SyncAction action) => batch.OwnsMovingPath(action.Path) ||
            action.FromPath is { } from && batch.OwnsMovingPath(from);
        var move = plan.Actions.Any(action => action.Kind == ActionKind.Move &&
            (action.Source?.Kind == ItemKind.Folder || action.ExpectedTarget?.Kind == ItemKind.Folder) && member(action));
        if (move && !plan.DeferredPlanningActions.Concat(plan.SkippedActions).Any(member) &&
            !plan.Actions.Any(action => member(action) && action.Kind is ActionKind.CopyNew or ActionKind.Delete)) return plan;
        // The dependency owner's existing durable notice/retry path retains the whole unit.
        batch.DeferredFolders.UnionWith(batch.MovingFolders);
        return plan with { Actions = [], DeferredPlanningActions = plan.DeferredPlanningActions.Concat(plan.Actions).ToArray() };
    }
}
