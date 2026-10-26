using System.Collections.Concurrent;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task CompleteOwnershipReconciliationAsync(SqliteStateStore store, RootDef root,
        LocalInventory inventory, ItemState item, LocalGapEpoch epoch, string folder)
    {
        var fx = Build(suppliedStore: store, pairId: "ownership-pair");
        var pair = fx.Pair with { A = root };
        store.SavePair(pair);
        store.MarkFirstSyncDone(pair.PairId);
        fx.B.DropboxHashes = true;
        inventory.JournalReadForTest = NoJournalChanges;
        var proof = new OwnershipProofIo(inventory.GapIo!);
        inventory.GapIo = proof;
        fx.Provider.AddInventory(root, inventory);
        fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, pair.PairId,
            LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default,
            isStateDamage: StorageFailureClassifier.IsStateDamage));
        var expectedHash = DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("content"));
        fx.WriterB.OnPut = (action, result) =>
        {
            if (action.Path != item.Path || result is not null) return;
            Require(proof.Reads.Contains(item.Path) && action.Source?.Hash == expectedHash,
                "gap-owned upload is planned only after native content proof matches its source");
        };
        await using var owner = new GapRecoveryRun(fx.Run());
        var report = await owner.Run.ReconcileScopeAsync(pair.PairId, StateMonitor.FromGap(epoch),
            new RecordingProgress(), default);
        var gaps = (ILocalGapStore)store;
        Require(report.Failed == 0 && report.Uncertain == 0 && proof.Reads.Contains(item.Path) &&
            store.ByObject(root.RootId, item.ObjectId) is { Hash: not null } verified && verified.Hash == expectedHash &&
            !verified.Flags.HasFlag(ItemFlags.NeedsRehash) && store.RehashPendingCount(root.RootId) == 0,
            "reconciliation proves the gap-owned item and clears the final rehash debt without losing work");
        Require(gaps.GapObligations(root.RootId, [item.ObjectId]).Count == 0 &&
            !gaps.HasGapProtection(root.RootId) && !store.GapInventoryPending(root.RootId) &&
            gaps.GapEpoch(root.RootId)!.Epoch == epoch.Epoch &&
            (await fx.B.ObserveAsync(item.Path, default))?.Hash == expectedHash,
            "content-proven reconciliation acknowledges the obligation and completes the same epoch");
    }

    private sealed class OwnershipProofIo(ILocalGapIo inner) : ILocalGapIo
    {
        internal ConcurrentBag<string> Reads { get; } = new();
        public bool Paused => inner.Paused;
        public LocalGapProof? Probe(RootDef root, ItemState item) => inner.Probe(root, item);
        public bool Excluded(RootDef root, ItemState item) => inner.Excluded(root, item);
        public async Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct)
        {
            var result = await inner.HashAsync(root, item, ct);
            Reads.Add(item.Path);
            return result;
        }
    }
}
