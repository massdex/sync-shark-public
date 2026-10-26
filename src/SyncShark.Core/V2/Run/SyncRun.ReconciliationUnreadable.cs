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

    private async Task InvalidateUnreadableSelectionAsync(PairDef pair, RootDef root,
        IReadOnlyList<string> incomplete, CancellationToken ct)
    {
        if (root.Provider != SyncProviders.Local || _store is not SqliteStateStore sql) return;
        foreach (var prefix in incomplete.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            long dir = 0, invalidated = 0;
            string key = "", name = "";
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await _admission.WithAsync(pair.PairId, () =>
                {
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
