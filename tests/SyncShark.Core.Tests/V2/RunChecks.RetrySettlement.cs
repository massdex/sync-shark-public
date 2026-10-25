namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task Incremental_SplitBaselineRetrySettlesAsync()
    {
        foreach (var mode in new[] { "settle", "equal-new-identity", "edited", "uncertain", "uncertain-other", "other-baseline" })
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
            if (mode == "equal-new-identity")
            {
                await fx.A.DeleteAsync(new SyncAction("replace-local", 6, ActionKind.Delete, Side.A,
                    baseline.A.Path, null, null, null, false, null), default);
                fx.A.InventoryWithoutHashes = false;
                var replacement = fx.A.Seed(baseline.A.Path, "cloud-edit", mtime: baseline.A.Mtime);
                Require(replacement.ObjectId != baseline.A.ObjectId && Content.SameContent(replacement, baseline.B) == true,
                    "atomic replacement has a new identity and proven equal content");
            }
            var unavailableReads = 0;
            if (mode == "uncertain")
            {
                var action = new SyncAction("pending-update", 2, ActionKind.Update, Side.A, baseline.A.Path, null,
                    baseline.B, baseline.A with { Hash = "previous", Mtime = baseline.A.Mtime.AddMinutes(-1) }, false, baseline.ObjectKey);
                store.RecordIntents(fx.Pair.PairId, [action]);
                store.CommitResult(fx.Pair.PairId, gen, action, new(action.ActionId, Outcome.Uncertain, null, null, null, "effect outcome unknown"));
                // Keep the effect genuinely unresolved: a hashless read alone retires this
                // checkpoint-free update as superseded instead of exercising pending protection.
                fx.WriterA.OnRead = observedPath =>
                {
                    if (PairPath.Same(observedPath, baseline.A.Path))
                    {
                        Interlocked.Increment(ref unavailableReads);
                        throw new HttpRequestException("Fixture observation temporarily unavailable", null,
                            System.Net.HttpStatusCode.ServiceUnavailable);
                    }
                };
            }
            if (mode is "other-baseline" or "uncertain-other")
            {
                var other = fx.B.Seed("Parent/other.json", "cloud-edit");
                var action = new SyncAction("other-link", 16, ActionKind.Adopt, Side.B, other.Path, null,
                    baseline.A, other, false, "other-link");
                store.CommitResult(fx.Pair.PairId, gen, action, new(action.ActionId, Outcome.Done, baseline.A, other, null, null));
                if (mode == "other-baseline")
                {
                    var editedOther = fx.B.Seed(other.Path, "later-edit", mtime: other.Mtime.AddMinutes(1));
                    Require(editedOther.Hash is not null && Content.SameContent(editedOther, other) == false,
                        "second correspondence is readable but edited against its baseline");
                }
                else
                {
                    // Every footprint belongs only to the second correspondence's remote path.
                    var pendingOther = new SyncAction("pending-other", 2, ActionKind.Update, Side.B, other.Path, null,
                        other, other with { Hash = "previous", Mtime = other.Mtime.AddMinutes(-1) }, false, "other-link");
                    store.RecordIntents(fx.Pair.PairId, [pendingOther]);
                    store.CommitResult(fx.Pair.PairId, gen, pendingOther,
                        new(pendingOther.ActionId, Outcome.Uncertain, null, null, null, "effect outcome unknown"));
                    fx.WriterB.OnRead = observedPath =>
                    {
                        if (PairPath.Same(observedPath, other.Path))
                        {
                            Interlocked.Increment(ref unavailableReads);
                            throw new HttpRequestException("Fixture observation temporarily unavailable", null,
                                System.Net.HttpStatusCode.ServiceUnavailable);
                        }
                    };
                }
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
            Require(settled == (mode is "settle" or "equal-new-identity"), "only fully proved unprotected correspondences settle after cold reopen: " + mode);
            if (mode == "edited")
            {
                Require(!settled && reopened.OpenNotices(fx.Pair.PairId).Any(n =>
                    n.Path == baseline.A.Path && n.Rule == 30 && n.ReasonKey == "unreadable"),
                    "edited item retains retry debt and its rule-30 unreadable notice");
            }
            else
                Require(reopened.OpenNotices(fx.Pair.PairId).Any(n => n.Path == baseline.A.Path && n.ReasonKey == "sync-retry") != settled,
                    "retry notice closes exactly when all affected correspondences settle: " + mode);
            if (mode is "uncertain" or "uncertain-other")
            {
                Require(unavailableReads > 0, "pending effect observation hit the injected 503: " + mode);
                Require(reopened.UnresolvedIntents(fx.Pair.PairId).Count > 0,
                    "unknown effect remains pending rather than becoming equality settlement: " + mode);
            }
        }
        Console.WriteLine("PASS Incremental_SplitBaselineRetrySettles");
    }
}
