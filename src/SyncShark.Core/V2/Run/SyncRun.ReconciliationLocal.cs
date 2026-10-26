using System.Globalization;

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task<SelectionCounts> SelectLocalAsync(PairDef pair, Side side, LocalInventory local,
        StateMonitorDecision decision, ReconciliationScope scope, string session, SqliteStateStore sql,
        ItemEligibility eligibility, (HashSet<string> Paths, HashSet<string> Ids) accounted, CancellationToken ct,
        Action<IReadOnlyList<string>, IReadOnlyDictionary<string, string>> incomplete)
    {
        var root = side == Side.A ? pair.A : pair.B;
        var untrusted = UntrustedLocalJournal(sql, root, decision);
        var sameJournal = !untrusted && decision.Trigger == ReconciliationTrigger.GapSameJournal &&
            NtfsUsnCheckpoint.TryParseCursor(decision.SavedCursor, out _, out _);
        NtfsUsnCheckpoint.TryParseCursor(decision.SavedCursor, out var journal, out var savedNext);
        RunTrace.Emit(string.Format(CultureInfo.InvariantCulture,
            MessageCatalog.Default.Template("reconciliation-source-trace"),
            MessageCatalog.Default.Template(sameJournal ? "source-local-journal" : "source-local-walk")));
        using var probes = sameJournal ? local.GapIo?.OpenProbes(root) : null;
        var counts = new SelectionCounts(0, 0, 0, 0, 0);
        await local.EnumerateMetadataAsync(async rows =>
        {
            ct.ThrowIfCancellationRequested();
            var selected = rows.Where(row => scope.Contains(row.Item.Path)).ToArray();
            var page = selected.Select(row => row.Item).ToArray();
            var usns = selected.ToDictionary(row => row.Item.Path, row => row.Usn, StringComparer.Ordinal);
            sql.StageReconcile(session + ":seen", side, page);
            var invalidated = new List<ItemState>();
            bool changed(ItemState item, ItemState previous) => untrusted && previous.Kind == ItemKind.File
                ? InvalidateUntrustedFile(previous, invalidated)
                : sameJournal
                    ? JournalSelectionChanged(item, previous, usns[item.Path], probes, journal, savedNext, invalidated)
                    : SelectionChanged(item, previous, false);
            counts = await _admission.WithAsync(pair.PairId, () =>
            {
                var result = SelectPage(pair, side, page, session, sql, eligibility, accounted, counts, false, changed);
                _store.RequireRehashBatch(root.RootId, invalidated,
                    MessageCatalog.Default.Template("reconcile-journal-proof"));
                return Task.FromResult(result);
            }, ct);
        }, ct, incomplete);
        return counts;
    }
}
