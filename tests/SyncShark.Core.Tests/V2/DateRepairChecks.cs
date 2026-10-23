namespace Massdex.SyncShark.V2;

internal static class DateRepairChecks
{
    internal static async Task RunAsync(string root)
    {
        var invocation = SyncCliRegistry.Parse(["--cli", "dates", "repair", "--pair", "dates", "--dry-run"]);
        LocalChecks.Require(invocation.Flag("--dry-run"), "dry-run does not require confirmation");
        var needsYes = false;
        try { SyncCliRegistry.Parse(["--cli", "dates", "repair", "--pair", "dates"]); }
        catch (SyncCliUsageException) { needsYes = true; }
        LocalChecks.Require(needsYes, "real repair requires explicit yes");
        Directory.CreateDirectory(root);
        var localRoot = Path.Combine(root, "files");
        Directory.CreateDirectory(localRoot);
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), StorageChecks.Fast, TimeProvider.System);
        var pair = new PairDef("dates", "Dates", LocalChecks.RootOf(localRoot, "local"),
            new RootDef("cloud", SyncProviders.Dropbox, "account", "/test", "id:cloud"), [], [], true);
        store.SavePair(pair);
        var stamp = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var original = new DateTimeOffset(2015, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var remote = new Dictionary<string, ItemState>();
        var names = new[] { "fix", "changed", "held", "locked", "cloud-changed", "cloud-newer", "small-gap" };
        var gen = store.BeginGeneration(pair.A.RootId, "test");
        foreach (var name in names)
        {
            var path = Path.Combine(localRoot, name);
            File.WriteAllText(path, "same content");
            File.SetLastWriteTimeUtc(path, stamp.UtcDateTime);
            using var file = File.OpenRead(path);
            LocalChecks.Require(PilotDirectoryLease.GetFileInformationByHandle(file.SafeFileHandle, out var info), "identity readable");
            var local = new ItemState(name, ItemKind.File,
                SyncLocalEndpointStore.ObjectId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow),
                null, LocalChecks.BlockHash(LocalChecks.Bytes("same content")), file.Length, stamp, ItemFlags.None);
            var cloud = local with { ObjectId = "id:" + name, VersionId = "rev", Mtime = original };
            remote.Add(name, cloud);
            var action = new SyncAction("adopt-" + name, 0, ActionKind.Adopt, Side.A, name, null, cloud, local, false, name);
            store.CommitResult(pair.PairId, gen, action, new ActionResult(action.ActionId, Outcome.Done, local, cloud, null, null));
        }
        foreach (var b in store.Baselines(pair.PairId)) DateProvenanceChecks.Seed(store, pair, b.A, Path.Combine(root, "state"));
        store.WriteSnapshot(new Snapshot(pair.A.RootId, Side.A, gen, "dates-cursor",
            store.Baselines(pair.PairId).Select(b => b.A).ToArray(), []));
        store.AddNotices([new Notice(pair.PairId, 0, NoticeKind.Info, "", "Pair information", "pair-excluded")]);
        store.PrepareDateRepair(pair.PairId);
        LocalChecks.Require(!store.DateRepairHeld(pair, store.Baselines(pair.PairId, ["fix"]).Single()),
            "empty pair notice does not hold items");
        store.AddNotices([new Notice(pair.PairId, 30, NoticeKind.UserDecision, "held", "Held", "test-hold")]);
        File.WriteAllText(Path.Combine(localRoot, "changed"), "edit content");
        File.SetLastWriteTimeUtc(Path.Combine(localRoot, "changed"), stamp.UtcDateTime); // same length/time, different hash
        remote["cloud-changed"] = remote["cloud-changed"] with { Hash = new string('0', 64) };
        remote["cloud-newer"] = remote["cloud-newer"] with { Mtime = stamp.AddDays(2) };
        remote["small-gap"] = remote["small-gap"] with { Mtime = stamp.AddHours(-23) };
        using var locked = new FileStream(Path.Combine(localRoot, "locked"), FileMode.Open, FileAccess.Read, FileShare.None);
        Task<ItemState?> Observe(string path, CancellationToken _) => Task.FromResult<ItemState?>(remote[path]);
        var dry = await DateRepair.RunAsync(store, pair, Observe, root, true, [], CancellationToken.None, StorageFailureClassifier.IsStateDamage);
        LocalChecks.Require(dry.Eligible == 1 && dry.Changed == 0 && dry.Skipped == 4 && dry.Examples.Count == 1,
            "dry run counts eligible, changed, held, locked and remote mismatches");
        LocalChecks.Require(File.GetLastWriteTimeUtc(Path.Combine(localRoot, "fix")) == stamp.UtcDateTime &&
            store.Baselines(pair.PairId, ["fix"]).Single().A.Mtime == stamp, "dry run changes neither file nor baseline");
        var done = await DateRepair.RunAsync(store, pair, Observe, root, false, [], CancellationToken.None, StorageFailureClassifier.IsStateDamage);
        LocalChecks.Require(done.Changed == 1 && done.Skipped == 4, "only identical unheld unlocked file repaired");
        LocalChecks.Require(File.GetLastWriteTimeUtc(Path.Combine(localRoot, "fix")) == original.UtcDateTime &&
            store.Baselines(pair.PairId, ["fix"]).Single().A.Mtime == original, "local and baseline dates agree");
        LocalChecks.Require(File.ReadAllText(Path.Combine(localRoot, "fix")) == "same content" &&
            File.ReadAllText(Path.Combine(localRoot, "changed")) == "edit content", "content never changed");
        LocalChecks.Require(File.ReadAllText(done.ReportFile).Contains("repaired"), "durable change report exists");
        var again = await DateRepair.RunAsync(store, pair, Observe, root, false, [], CancellationToken.None, StorageFailureClassifier.IsStateDamage);
        LocalChecks.Require(again.Changed == 0 && again.Eligible == 0, "repair is idempotent");
        var first = store.DateRepairPage(pair.PairId, "", 2);
        var second = store.DateRepairPage(pair.PairId, first[^1].ObjectKey, 2);
        LocalChecks.Require(first.Count == 2 && second.Count == 2 && !first.Select(b => b.ObjectKey).Intersect(second.Select(b => b.ObjectKey)).Any(),
            "keyset pages are bounded and disjoint");
        var run = new SyncRun(store, new Executor(store, StorageChecks.Fast, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage),
            new FakeEndpointProvider(), StorageChecks.Fast, TimeProvider.System);
        await run.WithDateRepairLockAsync(async () =>
        {
            var busy = false;
            try { await run.WithDateRepairLockAsync(() => Task.FromResult(true), CancellationToken.None); }
            catch (SyncCliBusyException) { busy = true; }
            LocalChecks.Require(busy, "date repair cannot enter an occupied run gate");
            return true;
        }, CancellationToken.None);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" +
            SqliteStateStore.DatabasePath(Path.Combine(root, "state"))))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER test_repair_failure BEFORE UPDATE OF a_mtime ON sync_point BEGIN SELECT RAISE(ABORT,'test rollback'); END;";
            command.ExecuteNonQuery();
        }
        remote["fix"] = remote["fix"] with { Mtime = original.AddDays(-2) };
        var failed = false;
        try
        {
            store.RepairDate(store.Baselines(pair.PairId, ["fix"]).Single(), Side.A, original.AddDays(-2), time =>
            {
                File.SetLastWriteTimeUtc(Path.Combine(localRoot, "fix"), time.UtcDateTime);
                return time;
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { failed = true; }
        LocalChecks.Require(failed && File.GetLastWriteTimeUtc(Path.Combine(localRoot, "fix")) == original.UtcDateTime &&
            store.Baselines(pair.PairId, ["fix"]).Single().A.Mtime == original,
            "database failure restores filesystem time and rolls back the baseline");
        await DateProvenanceChecks.RunAsync(Path.Combine(root, "provenance"));
        await DateRepairRangeChecks.RunAsync(Path.Combine(root, "range"));
        await DateRepairReviewChecks.RunAsync(Path.Combine(root, "review"));
        Console.WriteLine("v2 date repair checks passed.");
    }
}
