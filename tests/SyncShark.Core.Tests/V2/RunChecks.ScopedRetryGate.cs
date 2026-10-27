namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static void CheckScopedRetryGate(SqliteStateStore store, Fixture fx, SyncRun run)
    {
        var pair = fx.Pair;
        var future = DateTimeOffset.UtcNow.AddHours(1);
        var decision = new StateMonitorDecision(null, new([], ["target"]), ReconciliationTrigger.JournalReplaced)
            { RootId = pair.A.RootId };
        store.SaveSyncRetry(pair.PairId, new("other", "scan", 1, "unrelated", future, ReadRoots: [pair.A.RootId]));
        store.SaveSyncRetry(pair.PairId, new("target/cloud", "scan", 1, "other root", future, ReadRoots: [pair.B.RootId]));
        Require(!run.ReconciliationRetriesSleeping(pair, decision),
            "unrelated sleeping folder and opposite-root retries cannot block local reconciliation");
        store.SaveSyncRetry(pair.PairId, new("target/local", "scan", 1, "sleeping", future, ReadRoots: [pair.A.RootId]));
        Require(run.ReconciliationRetriesSleeping(pair, decision), "only sleeping relevant retries defer this scope");
        store.SaveSyncRetry(pair.PairId, new("target/due", "scan", 1, "due", DateTimeOffset.MinValue, ReadRoots: [pair.A.RootId]));
        Require(!run.ReconciliationRetriesSleeping(pair, decision) &&
            !run.ReconciliationRetriesSleeping(pair, StateMonitor.PlanningBacklog()),
            "one due retry starts reconciliation and planning backlog never waits on read debt");
        store.ClearSyncRetry(pair.PairId, "target/cloud");
        Require(!run.ReconciliationRetriesSleeping(pair, StateMonitor.DropboxReset() with { RootId = pair.B.RootId }),
            "Dropbox reset is not gated by sleeping local read failures");
        foreach (var path in new[] { "other", "target/local", "target/due" }) store.ClearSyncRetry(pair.PairId, path);
    }
}
