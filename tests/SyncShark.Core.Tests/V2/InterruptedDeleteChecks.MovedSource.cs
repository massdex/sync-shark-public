namespace Massdex.SyncShark.V2;

internal static partial class InterruptedDeleteChecks
{
    private static async Task MovedSourceReplansAsync(string folder, Side target)
    {
        var pair = Pair();
        var a = new FakeEndpoint(Side.A, "a");
        var b = new FakeEndpoint(Side.B, "b");
        var sa = a.Seed("File", "original");
        var sb = b.Seed("File", "original");
        var source = target == Side.A ? b : a;
        var victim = target == Side.A ? a : b;
        var historical = target == Side.A ? sb : sa;
        var delete = new SyncAction("moved-source", 6, ActionKind.Delete, target, "File", null, null,
            target == Side.A ? sa : sb, false, "linked")
            { ProviderEffect = new("delete-submit", "submission", DateTimeOffset.UnixEpoch) };
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(pair);
            Seed(store, pair, sa, sb);
            await victim.DeleteAsync(delete, default);
            await source.MoveAsync(new("source-move", 9, ActionKind.Move, target == Side.A ? Side.B : Side.A,
                "Moved", "File", historical, historical, false, null), default);
            var executor = new Executor(store, V2Settings.Default, ExecutorChecks.Admission(),
                isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                store.RecordIntent(pair.PairId, delete);
                var result = (await executor.ReconcileAsync([delete], 2, a, b, default)).Single();
                Require(result is { Outcome: Outcome.Skipped, DeleteSourceUnconfirmed: true, ForgetUnsafeDeleteBaseline: false },
                    "moved source retires deletion as not applied, never Done or indefinitely Uncertain");
            }
            Require(store.UnresolvedIntents(pair.PairId).Count == 0 && store.Baselines(pair.PairId).Count == 1,
                "replan retains correspondence without a stuck intent");
            Require(store.OpenNotices(pair.PairId).Count(n => n.ReasonKey == DeleteSourceNotice.ReasonKey) == 1,
                "repeated moved-source failure has exactly one notice");
        }
        using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        Require(reopened.SyncRetryAt(pair.PairId, "File")?.DeleteBlockedDetail is not null &&
            (await source.ObserveAsync("Moved", default))?.ObjectId == historical.ObjectId,
            "replan debt survives reopen and the moved source remains untouched");
    }
}
