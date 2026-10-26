namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private static Task StageSelectionRows(SqliteStateStore sql, string session, Side side,
        ReconciliationScope scope, IReadOnlyList<ItemState> rows, CancellationToken ct)
    {
        foreach (var page in rows.Where(item => scope.Contains(item.Path)).Chunk(V2Settings.PlanningObservationBatchRows))
        {
            ct.ThrowIfCancellationRequested();
            sql.StageReconcile(session + ":seen", side, page);
        }
        return Task.CompletedTask;
    }

    private static void CollectSelectionIncomplete(IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, string> causes, List<string> incomplete, Dictionary<string, string> reasons)
    {
        incomplete.AddRange(paths);
        foreach (var cause in causes) reasons[cause.Key] = cause.Value;
    }

    private Task PublishSelectionIncompleteAsync(PairDef pair, Side side, IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, string> reasons, CancellationToken ct) =>
        _admission.WithAsync(pair.PairId, () =>
        {
            RecordSelectionIncomplete(pair, side, paths, reasons);
            return Task.CompletedTask;
        }, ct);

    private async Task InvalidateUnreadableSelectionAsync(PairDef pair, RootDef root, Side side,
        IReadOnlyList<string> incomplete, IReadOnlyDictionary<string, string> reasons, CancellationToken ct)
    {
        if (!incomplete.Any()) return;
        if (root.Provider != SyncProviders.Local || _store is not SqliteStateStore sql)
        {
            await PublishSelectionIncompleteAsync(pair, side, incomplete, reasons, ct);
            return;
        }
        foreach (var prefix in incomplete.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            long dir = 0, invalidated = 0;
            var publish = true;
            string key = "", name = "";
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await _admission.WithAsync(pair.PairId, () =>
                {
                    if (publish) RecordSelectionIncomplete(pair, side, [prefix], reasons);
                    publish = false;
                    var rows = sql.MissingPage(string.Empty, Side.A, root.RootId, prefix, dir, key, name);
                    var files = rows.Select(row => row.Row.Item).Where(item => item.Kind == ItemKind.File &&
                        !item.Flags.HasFlag(ItemFlags.NeedsRehash)).ToArray();
                    _store.RequireRehashBatch(root.RootId, files, MessageCatalog.Default.Template("reconcile-unreadable-proof"));
                    invalidated += files.Length;
                    return Task.FromResult(rows);
                }, ct);
                if (page.Count == 0) break;
                var last = page[^1].Row;
                dir = last.DirNo; key = last.NameKey; name = last.Name;
            }
            RunTrace.Emit($"reconciliation unreadable proof invalidated: scope={prefix} items={invalidated}");
        }
    }
}
