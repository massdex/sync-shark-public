namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    internal static async Task CachedIdentityDeleteRefusalAsync()
    {
        foreach (var missingIdentity in new[] { false, true })
        {
            var (store, a, b, executor) = Rig();
            var original = a.Seed("item", "same");
            var survivor = b.Seed("item", "same");
            if (!missingIdentity) SeedBaseline(store, "key", original, survivor);
            await RemoveSourceAsync(a, "item");
            var action = Act(ActionKind.Delete, Side.B, "item", expected: survivor, rf: true,
                key: missingIdentity ? null : "key");
            // A retired source ID cannot be opened. That is still UNKNOWN to delete admission,
            // even though the indexing path separately requests a guarded path rediscovery.
            var source = new RecordingWriter(a) { UnknownAbsencePath = "item", UnknownAbsenceReason = "open-failed(87)" };
            var target = new RecordingWriter(b);
            var pair = Pair();
            store.SavePair(pair);
            store.WriteSnapshot(new(pair.A.RootId, Side.A, store.BeginGeneration(pair.A.RootId, "full"),
                "cursor", [original], []));
            Require(CachedIdentityRefresh.TryQueue(store, pair.A, original, "open-failed(87)"),
                "indexing queues rediscovery independently of destructive authority");
            var results = await Apply(executor, Plan(action), source, target);
            Require(results.Single().Outcome == Outcome.Skipped && results.Single().DeleteSourceUnconfirmed &&
                target.Deletes == 0 && target.Preserves == 0 && await b.ObserveAsync("item", default) is not null,
                "cached ID error 87 cannot authorize deletion or Recovery when original source proof is unavailable");
            Require(missingIdentity ? results.Single().DeleteSourceIdentityMissing : source.AbsenceBatchItems > 0,
                "missing original identity remains a review refusal; baseline-backed refusal consumes the unknown source-ID probe");
            Require(store.SyncRetryAt(pair.PairId, "item") is not null,
                "refused deletion retains retry after rediscovery was requested");
        }
    }
}
