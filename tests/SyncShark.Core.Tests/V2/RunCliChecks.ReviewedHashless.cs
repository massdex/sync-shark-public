namespace Massdex.SyncShark.V2;

internal static partial class RunCliChecks
{
    private static async Task ReviewedHashlessChoiceAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        foreach (var unavailable in new[] { false, true })
        {
            var directory = TempRoot();
            try
            {
                using var store = SqliteStateStore.Open(Path.Combine(directory, "state"), Settings, TimeProvider.System);
                var fx = Build(store: store);
                var target = side == Side.A ? fx.A : fx.B;
                var root = side == Side.A ? fx.Pair.A : fx.Pair.B;
                var item = target.Seed("reviewed.txt", "original bytes");
                var generation = store.BeginGeneration(root.RootId, "reviewed-seed");
                store.WriteSnapshot(new Snapshot(root.RootId, side, generation, "", [item with { Hash = null }], []));
                var action = new SyncAction("reviewed-remove", 40, ActionKind.Delete, side, item.Path,
                    null, null, item with { Hash = null }, true, null) { RemovalReviewChoice = true };
                var retry = new SyncRetry(item.Path, "reviewed-choice", 0, "", DateTimeOffset.MinValue)
                    { ReviewedActions = [action] };
                store.SaveSyncRetry(fx.Pair.PairId, retry);
                if (unavailable) target.Unhashable.Add(item.Path);
                var run = new SyncRun(store, new Executor(store, Settings, ExecutorChecks.Admission()), fx.Provider, Settings, TimeProvider.System);
                var replay = typeof(SyncRun).GetMethod("ApplyReviewedRetriesAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var plan = await (Task<Plan>)replay.Invoke(run,
                    [fx.Pair, new Plan(fx.Pair.PairId, generation, [], []), new[] { retry }, CancellationToken.None])!;
                Require(target.HashedPaths.Contains(item.Path), "reviewed file obtains VERIFY proof before fresh admission on " + side);
                if (unavailable)
                    Require(plan.Actions.Count == 0 && store.SyncRetryAt(fx.Pair.PairId, item.Path)?.ReviewedActions?.Count == 1 &&
                        store.UnresolvedNotices(fx.Pair.PairId).Any(notice => notice.Path == item.Path),
                        "unavailable proof retains explicit choice and notice on " + side);
                else
                    Require(plan.Actions.Single().ExpectedTarget?.Hash == item.Hash,
                        "hashless reviewed choice remains executable on " + side);
            }
            finally { Cleanup(directory); }
        }
    }
}
