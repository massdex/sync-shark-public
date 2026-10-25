namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task Incremental_SplitBaselineRetrySettlesAsync()
    {
        foreach (var mode in new[] { "settle", "edited", "uncertain", "other-baseline" })
        {
            var folder = FixtureFolder("split-retry-" + mode);
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            const string path = "Parent/capsules/work/item.json";
            fx.A.Seed(path, "original");
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            var originalKey = store.Baselines(fx.Pair.PairId).Single().ObjectKey;
            fx.A.Seed(path, "local-edit");
            fx.B.Seed(path, "cloud-edit");
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            var baseline = store.Baselines(fx.Pair.PairId).Single(item => item.ObjectKey == PlanIds.Split(originalKey, Side.B));
            fx.A.InventoryWithoutHashes = true;
            fx.A.OmitVersionIds = true;
            // A metadata change invalidates the carried hash. Restoring the saved timestamp
            // leaves ordinary hashless local inventory, without bypassing the store's hash owner.
            fx.A.Seed(baseline.A.Path, "cloud-edit", mtime: baseline.A.Mtime.AddMinutes(1));
            store.WriteSnapshot(await fx.A.FullAsync(store.BeginGeneration(fx.Pair.A.RootId, "metadata-change"), _ => true, null!, default));
            fx.A.Seed(baseline.A.Path, "cloud-edit", mtime: baseline.A.Mtime);
            if (mode == "edited")
            {
                fx.A.OmitVersionIds = true;
                fx.A.Seed(baseline.A.Path, "later-edit", mtime: baseline.A.Mtime.AddMinutes(1));
                fx.A.Unhashable.Add(baseline.A.Path);
            }
            var gen = store.BeginGeneration(fx.Pair.A.RootId, "null-hash-retry");
            store.WriteSnapshot(await fx.A.FullAsync(gen, _ => true, null!, default));
            Require(store.Live(fx.Pair.A.RootId, [baseline.A.Path]).Single().Hash is null && baseline.B.Hash is not null,
                "retry fixture has a null local hash and a known remote baseline hash");
            if (mode == "uncertain")
            {
                var action = new SyncAction("pending-update", 2, ActionKind.Update, Side.A, baseline.A.Path, null,
                    baseline.B, baseline.A with { Hash = "previous", Mtime = baseline.A.Mtime.AddMinutes(-1) }, false, baseline.ObjectKey);
                store.RecordIntents(fx.Pair.PairId, [action]);
                store.CommitResult(fx.Pair.PairId, gen, action, new(action.ActionId, Outcome.Uncertain, null, null, null, "effect outcome unknown"));
                fx.Provider.AddWriter(fx.Pair.A, new HashlessObserver(fx.WriterA));
            }
            if (mode == "other-baseline")
            {
                var other = fx.B.Seed("Parent/other.json", "cloud-edit");
                var action = new SyncAction("other-link", 16, ActionKind.Adopt, Side.B, other.Path, null,
                    baseline.A, other, false, "other-link");
                store.CommitResult(fx.Pair.PairId, gen, action, new(action.ActionId, Outcome.Done, baseline.A, other, null, null));
                fx.B.OmitVersionIds = true;
                fx.B.InventoryWithoutHashes = true;
                fx.B.Seed(other.Path, "later-edit", mtime: other.Mtime.AddMinutes(1));
                fx.B.Unhashable.Add(other.Path);
            }
            store.SaveSyncRetry(fx.Pair.PairId, new SyncRetry(baseline.A.Path, "sync", 4,
                "effect outcome unknown", DateTimeOffset.MinValue));
            store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.Info, baseline.A.Path,
                "effect outcome unknown", "sync-retry")]);
            store.Dispose();
            using var reopened = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var restarted = new SyncRun(reopened, NewExecutor(reopened), fx.Provider, Settings, TimeProvider.System);
            await restarted.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            var settled = reopened.SyncRetryAt(fx.Pair.PairId, baseline.A.Path) is null;
            Require(settled == (mode == "settle"), "only fully proved unprotected correspondences settle after cold reopen: " + mode);
            Require(reopened.OpenNotices(fx.Pair.PairId).Any(n => n.Path == baseline.A.Path && n.ReasonKey == "sync-retry") != settled,
                "retry notice closes exactly when all affected correspondences settle: " + mode);
            if (mode == "uncertain") Require(reopened.UnresolvedIntents(fx.Pair.PairId).Count > 0,
                "unknown effect remains pending rather than becoming equality settlement");
        }
        Console.WriteLine("PASS Incremental_SplitBaselineRetrySettles");
    }
}
