namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private static bool UntrustedLocalJournal(SqliteStateStore store, RootDef root, StateMonitorDecision decision) =>
        decision.Trigger is ReconciliationTrigger.JournalReplaced or ReconciliationTrigger.JournalPositionInvalid ||
        decision.Trigger == ReconciliationTrigger.GapSameJournal &&
            !NtfsUsnCheckpoint.TryParseCursor(decision.SavedCursor, out _, out _) ||
        ((ILocalGapStore)store).GapEpoch(root.RootId) is { TrustFileUsn: false } &&
            (store.GapInventoryPending(root.RootId) || ((ILocalGapStore)store).HasGapProtection(root.RootId));

    private async Task<RunReport> ReconcileTracedBatchAsync(SqliteStateStore store, PairDef pair,
        StateMonitorDecision decision, ReconcileBatch batch, IProgress<RunProgress> progress, CancellationToken ct)
    {
        var report = await ReconcileBatchAsync(pair, batch, progress, ct);
        TraceUntrustedBatch(store, pair, decision, batch);
        return report;
    }

    private static void TraceUntrustedBatch(SqliteStateStore store, PairDef pair,
        StateMonitorDecision decision, ReconcileBatch batch)
    {
        foreach (var side in new[] { Side.A, Side.B })
        {
            var root = side == Side.A ? pair.A : pair.B;
            if (root.Provider != SyncProviders.Local || !UntrustedLocalJournal(store, root, decision)) continue;
            var files = (side == Side.A ? batch.RowsA : batch.RowsB).Where(item => item.Kind == ItemKind.File).ToArray();
            var proven = files.Count(item => store.ByObject(root.RootId, item.ObjectId) is { Hash: not null } current &&
                !current.Flags.HasFlag(ItemFlags.NeedsRehash));
            RunTrace.Emit($"untrusted journal: {proven} of {files.Length} retained files proven (batch side={side})");
        }
    }

    private static bool JournalSelectionChanged(ItemState item, ItemState previous, long? usn,
        ILocalGapProbe? probes, ulong journal, long savedNext, List<ItemState> invalidated)
    {
        // Cursor stores NextUsn: the next UNPROCESSED record, so equality is not proof of unchanged content.
        // This supersedes the design's original <= saved-cursor shorthand.
        bool changed;
        if (usn is { } current) changed = current >= savedNext;
        else if (SelectionChanged(item, previous, false)) changed = true;
        else
        {
            var proof = probes?.Probe(item);
            changed = proof is null || !NtfsUsnCheckpoint.TryParseCursor(proof.JournalCursor, out var currentJournal, out _) ||
                currentJournal != journal || proof.File.Usn >= savedNext;
        }
        if (changed && previous.Kind == ItemKind.File && !previous.Flags.HasFlag(ItemFlags.NeedsRehash))
            invalidated.Add(previous);
        return changed;
    }
}
