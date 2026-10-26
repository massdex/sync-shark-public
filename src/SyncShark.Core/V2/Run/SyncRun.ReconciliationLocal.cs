namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private async Task<SelectionCounts> SelectLocalAsync(PairDef pair, Side side, LocalInventory local,
        StateMonitorDecision decision, ReconciliationScope scope, string session, SqliteStateStore sql,
        ItemEligibility eligibility, (HashSet<string> Paths, HashSet<string> Ids) accounted, CancellationToken ct)
    {
        var root = side == Side.A ? pair.A : pair.B;
        var sameJournal = decision.Trigger == ReconciliationTrigger.GapSameJournal &&
            NtfsUsnCheckpoint.TryParseCursor(decision.SavedCursor, out _, out _);
        NtfsUsnCheckpoint.TryParseCursor(decision.SavedCursor, out var journal, out var savedNext);
        using var probes = sameJournal ? local.GapIo?.OpenProbes(root) : null;
        var counts = new SelectionCounts(0, 0, 0, 0, 0);
        await local.EnumerateMetadataAsync(rows =>
        {
            ct.ThrowIfCancellationRequested();
            var selected = rows.Where(row => scope.Contains(row.Item.Path)).ToArray();
            var page = selected.Select(row => row.Item).ToArray();
            var usns = selected.ToDictionary(row => row.Item.Path, row => row.Usn, StringComparer.Ordinal);
            sql.StageReconcile(session + ":seen", side, page);
            bool changed(ItemState item, ItemState previous)
            {
                if (!sameJournal) return SelectionChanged(item, previous, false);
                if (usns[item.Path] is { } usn) return usn > savedNext;
                if (SelectionChanged(item, previous, false)) return true;
                var proof = probes?.Probe(item);
                return proof is null || !NtfsUsnCheckpoint.TryParseCursor(proof.JournalCursor, out var currentJournal, out _) ||
                    currentJournal != journal || proof.File.Usn > savedNext;
            }
            counts = SelectPage(pair, side, page, session, sql, eligibility, accounted, counts, false, changed);
            return Task.CompletedTask;
        }, ct);
        return counts;
    }
}
