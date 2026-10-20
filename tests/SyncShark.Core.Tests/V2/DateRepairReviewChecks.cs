namespace Massdex.SyncShark.V2;

internal static class DateRepairReviewChecks
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var files = Path.Combine(root, "files");
        Directory.CreateDirectory(files);
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), StorageChecks.Fast, TimeProvider.System);
        var pair = new PairDef("review", "review", LocalChecks.RootOf(files, "a"),
            new RootDef("b", SyncProviders.Dropbox, "account", "/test", "id:root"), [], [], true);
        store.SavePair(pair);
        var old = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var target = new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var remote = new Dictionary<string, ItemState>();
        var gen = store.BeginGeneration("a", "test");
        for (var i = 0; i < 134; i++)
        {
            var name = i.ToString("D3");
            var full = Path.Combine(files, name);
            File.WriteAllText(full, "unchanged");
            var stamp = i == 133 ? old.AddDays(-1) : old;
            File.SetLastWriteTimeUtc(full, stamp.UtcDateTime);
            using var file = File.OpenRead(full);
            LocalChecks.Require(PilotDirectoryLease.GetFileInformationByHandle(file.SafeFileHandle, out var info), "review identity");
            var local = new ItemState(name, ItemKind.File,
                SyncLocalEndpointStore.ObjectId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow),
                null, LocalChecks.BlockHash(LocalChecks.Bytes("unchanged")), file.Length, stamp, ItemFlags.None);
            var cloud = local with { ObjectId = "id:" + name, Mtime = i == 132 ? stamp : target };
            remote[name] = cloud;
            var action = new SyncAction(name, 0, ActionKind.Adopt, Side.A, name, null, cloud, local, false, name);
            store.CommitResult(pair.PairId, gen, action, new ActionResult(name, Outcome.Done, local, cloud, null, null));
        }
        store.WriteSnapshot(new Snapshot("a", Side.A, gen, "review-cursor",
            store.Baselines(pair.PairId, remote.Keys.ToArray()).Select(row => row.A).ToArray(), []));
        File.SetLastWriteTimeUtc(Path.Combine(files, "000"), target.UtcDateTime); // crash after stamping, before commit
        File.SetAttributes(Path.Combine(files, "130"), FileAttributes.ReadOnly);
        remote["131"] = remote["131"] with { Mtime = DateTimeOffset.UnixEpoch };
        var calls = 0;
        Task<ItemState?> Observe(string path, CancellationToken _)
        {
            LocalChecks.Require(path is not ("132" or "133"), "SQL rejects matching dates and other UTC dates before remote calls");
            Interlocked.Increment(ref calls);
            return Task.FromResult<ItemState?>(remote[path]);
        }
        var date = new DateOnly(2026, 10, 18);
        var dry = await DateRepair.RunAsync(store, pair, Observe, root, true, [], CancellationToken.None, date);
        LocalChecks.Require(dry.Examined == 132 && dry.Eligible == 130 && dry.Skipped == 2 && calls == 132,
            "SQL candidates only; read-only and implausible dates skipped in dry run");
        var pages = 0;
        async Task Gate(Func<Task> work)
        {
            pages++;
            await work();
        }
        var done = await DateRepair.RunAsync(store, pair, Observe, root, false, [], CancellationToken.None, date, withPageLock: Gate);
        LocalChecks.Require(pages == 3 && done.Eligible == dry.Eligible && done.Skipped == dry.Skipped,
            "gate released each 128-row page; real and dry-run counts agree");
        LocalChecks.Require(store.Baselines(pair.PairId, ["000"]).Single().A.Mtime == target,
            "rerun settles already-stamped crash survivor");
        var live = store.Live("a", ["000"]).Single();
        LocalChecks.Require(live.Mtime == target && live.Hash == remote["000"].Hash,
            "live timestamp and hash updated in baseline transaction");
        // Exercise the merged local refresh and hash-carry path without requiring a volume journal.
        var refresher = new LocalInventory(pair.A, Side.A, store,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), V2Settings.Default, TimeProvider.System);
        var refreshGen = store.BeginGeneration("a", "delta");
        var refreshed = await refresher.ChangesForPathsAsync(["000"], "review-cursor", refreshGen, _ => true,
            CancellationToken.None);
        var change = refreshed.Changes.Single(c => c.State?.Path == "000").State!;
        LocalChecks.Require(!refreshed.CursorInvalid && change.ObjectId == live.ObjectId && change.Size == live.Size &&
            change.Mtime.UtcTicks == live.Mtime.UtcTicks && (change.Flags & ItemFlags.NeedsRehash) == 0,
            "refresh after dates repair observes repaired identity and mtime without a rehash flag");
        store.WriteDelta(refreshed);
        store.CompleteGeneration(refreshGen, "review-cursor", true);
        var carried = store.Live("a", ["000"]).Single();
        LocalChecks.Require(carried.Hash == remote["000"].Hash && carried.Mtime == target &&
            (carried.Flags & ItemFlags.NeedsRehash) == 0 && store.RehashWatermark("a") == 0,
            "delta after dates repair carries its stored hash and queues no rehash debt");
        var baseline = store.Baselines(pair.PairId, ["000"]).Single();
        var carriedRow = new PlanRow(baseline, carried, remote["000"], new Evidence(false, false, true, true));
        LocalChecks.Require(Planner.HashRequests([carriedRow]) is { PathsA.Count: 0, PathsB.Count: 0 },
            "refreshed repaired row requests no hash");
        var options = new PlanOptions(TimeSpan.FromSeconds(2), false,
            new LabelRule("{name}-{side}{ext}", "yyyyMMdd", TimeSpan.Zero, "local", "cloud"), 10);
        LocalChecks.Require(Planner.Decide(pair.PairId, refreshGen, [carriedRow], options).Actions.Count == 0,
            "refreshed repaired row plans nothing");
        var hashRequests = Planner.HashRequests([new PlanRow(baseline, live with { Hash = null }, remote["000"],
            new Evidence(false, false, true, true))]);
        LocalChecks.Require(hashRequests.PathsA.Count == 0 && hashRequests.PathsB.Count == 0,
            "next sync requests no rehash after repaired baseline/live timestamp");
        var plan = Planner.Decide(pair.PairId, gen,
            [new PlanRow(baseline, live with { Hash = null }, remote["000"], new Evidence(false, false, true, true))],
            options);
        LocalChecks.Require(plan.Actions.Count == 0, "next sync planner emits no actions even if a scan omits cached hash");
        // A network failure records the start of the uncompleted page, not the last scheduled worker.
        var failed = false;
        try
        {
            await DateRepair.RunAsync(store, pair, (_, _) => throw new HttpRequestException("offline"), root, true,
                [], CancellationToken.None, date, "129");
        }
        catch (HttpRequestException) { failed = true; }
        LocalChecks.Require(failed && Directory.EnumerateFiles(Path.Combine(root, "v2", "logs"))
            .Any(path => File.ReadAllText(path).Contains("\"Outcome\":\"aborted\",\"After\":\"129\"")),
            "network failure logs resumable page cursor");
        var resumed = await DateRepair.RunAsync(store, pair, Observe, root, true, [], CancellationToken.None, date, "130");
        LocalChecks.Require(resumed.Examined == 1 && resumed.Skipped == 1, "resume cursor skips prior completed candidates");
        var originalError = new IOException("original failure");
        try
        {
            store.RepairDate(baseline, Side.A, target, _ => throw originalError);
        }
        catch (IOException error)
        {
            LocalChecks.Require(ReferenceEquals(error, originalError) && error.Data.Contains("DateRepairRollbackError"),
                "rollback failure never masks the original failure");
        }
        await DropboxChecks.DateRepairMetadataPageAsync();
    }
}
