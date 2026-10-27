namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    // A moved folder is one metadata unit, not an oversized content-read exemption.
    // Only captured selection and stored identities participate; fresh children retain
    // their journal/AutoSync owner. No recursive provider discovery is performed here.
    private ReconcileBatch MovingFolderBatch(PairDef pair, SqliteStateStore sql,
        ReconcileBatch batch, CancellationToken ct)
    {
        var folders = MovingFolderAliases(pair, sql, batch, ct, out var identity);
        if (folders.Count == 0) return batch;
        var count = Math.Max(sql.MovingUnitItems(batch.Session + ":work", Side.A, pair.A.RootId, folders),
            sql.MovingUnitItems(batch.Session + ":work", Side.B, pair.B.RootId, folders));
        // Reuse the existing retained-planner-candidate bound, not the smaller metadata page size.
        if (count > V2Settings.SettleCandidateCap)
            return batch with { MovingFolders = folders, MovingFolder = identity, MovingItems = count, MovingUnitTooLarge = true };
        var paths = folders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var a = MovingRows(sql, batch, pair.A, Side.A, folders, paths, ct);
        var b = MovingRows(sql, batch, pair.B, Side.B, folders, paths, ct);
        return new(batch.Session, paths.ToArray(), a.Select(row => row.ObjectId).ToHashSet(StringComparer.Ordinal),
            b.Select(row => row.ObjectId).ToHashSet(StringComparer.Ordinal),
            (side, path) => !paths.Contains(path) && sql.StagedAmong(batch.Session + ":work", side, [path]).Count > 0)
        {
            RowsA = a, RowsB = b, MovingFolders = folders.ToArray(), MovingFolder = identity,
            MovingIdentities = MovingIdentitySet(pair, sql, a, b),
            MovingItems = Math.Max(a.Select(row => row.ObjectId).Distinct().Count(), b.Select(row => row.ObjectId).Distinct().Count()),
            GapCoverage = batch.GapCoverage
        };
    }

    private static IReadOnlyList<string> MovingFolderAliases(PairDef pair, SqliteStateStore sql,
        ReconcileBatch batch, CancellationToken ct, out NoticeRepair? identity)
    {
        identity = null;
        foreach (var (side, root, rows) in new[] { (Side.A, pair.A, batch.RowsA), (Side.B, pair.B, batch.RowsB) })
        {
            var selected = rows.Where(row => row.Kind == ItemKind.Folder).ToArray();
            if (selected.Length == 0) continue;
            ct.ThrowIfCancellationRequested();
            var candidates = selected.SelectMany(row => sql.StagedIdentity(batch.Session + ":work", side, row.ObjectId))
                .Where(row => row.Kind == ItemKind.Folder).Concat(selected).ToArray();
            foreach (var group in candidates.GroupBy(row => row.ObjectId))
            {
                var synced = sql.BaselinesTouching(pair.PairId, side, [group.Key], [])
                    .Select(basis => side == Side.A ? basis.A.Path : basis.B.Path).ToHashSet(StringComparer.Ordinal);
                var aliases = group.Select(row => row.Path)
                    .Concat(sql.ByObject(root.RootId, group.Key) is { } current ? new[] { current.Path } : [])
                    .Concat(synced)
                    .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
                if (aliases.Length > 1)
                {
                    identity = new NoticeRepair(side, group.FirstOrDefault(row => !synced.Contains(row.Path)) ?? group.First());
                    return aliases;
                }
            }
        }
        return [];
    }

    private static ItemState[] MovingRows(SqliteStateStore sql, ReconcileBatch batch, RootDef root,
        Side side, IReadOnlyList<string> folders, HashSet<string> paths, CancellationToken ct)
    {
        var rows = folders.SelectMany(folder => StagedMovingRows(sql, batch.Session, side, folder, ct)).ToList();
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
        Side side, string folder, CancellationToken ct)
    {
        string key = "", path = "";
        while (sql.StagedSubtreePage(session + ":work", side, folder, key, path) is { Count: > 0 } page)
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
        var move = plan.Actions.FirstOrDefault(action => action.Kind == ActionKind.Move &&
            (action.Source?.Kind == ItemKind.Folder || action.ExpectedTarget?.Kind == ItemKind.Folder) && member(action));
        var deferred = plan.DeferredPlanningActions.Concat(plan.SkippedActions).Any(member);
        if (!deferred && !plan.Actions.Any(action => member(action) && BreaksMovingIdentity(action, batch)))
            return move is null ? plan : HoldMovingFolderDeletes(plan with
                { Actions = plan.Actions.Select(action => AfterUnitMove(action, move, batch)).ToArray() }, move);
        // The dependency owner's existing durable notice/retry path retains the whole unit.
        batch.DeferredFolders.UnionWith(batch.MovingFolders);
        return plan with { Actions = [], DeferredPlanningActions = plan.DeferredPlanningActions.Concat(plan.Actions).ToArray() };
    }
}
