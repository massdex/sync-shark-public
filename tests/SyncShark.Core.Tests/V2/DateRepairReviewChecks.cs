namespace Massdex.SyncShark.V2;

internal static class DateRepairReviewChecks
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var files = Path.Combine(root, "files");
        Directory.CreateDirectory(files);
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = new PairDef("review", "review", LocalChecks.RootOf(files, "a"),
            new RootDef("b", SyncProviders.Dropbox, "account", "/test", "id:root"), [], [], true);
        store.SavePair(pair);
        var old = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 18, 12, 0, 0), TimeZoneInfo.Local));
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
        foreach (var b in store.Baselines(pair.PairId)) DateProvenanceChecks.Seed(store, pair, b.A, Path.Combine(root, "state"));
        store.WriteSnapshot(new Snapshot("a", Side.A, gen, "review-cursor",
            store.Baselines(pair.PairId, remote.Keys.ToArray()).Select(row => row.A).ToArray(), []));
        File.SetLastWriteTimeUtc(Path.Combine(files, "000"), target.UtcDateTime); // crash after stamping, before commit
        File.SetAttributes(Path.Combine(files, "130"), FileAttributes.ReadOnly);
        remote["131"] = remote["131"] with { Mtime = DateTimeOffset.UnixEpoch };
        var calls = 0;
        Task<ItemState?> Observe(string path, CancellationToken _)
        {
            LocalChecks.Require(path is not ("132" or "133"), "SQL rejects matching dates and other local dates before remote calls");
            Interlocked.Increment(ref calls);
            return Task.FromResult<ItemState?>(remote[path]);
        }
        var date = new DateOnly(2026, 10, 18);
        var dry = await DateRepair.RunAsync(store, pair, Observe, root, true, [], CancellationToken.None, [], [], false, StorageFailureClassifier.IsStateDamage, StorageFailureClassifier.IsDirectStateDamage, StorageFailureClassifier.IsBusy, date);
        LocalChecks.Require(dry.Examined == 132 && dry.Eligible == 130 && dry.Skipped == 2 && calls == 132,
            "SQL candidates only; read-only and implausible dates skipped in dry run");
        var pages = 0;
        var releases = 0;
        async Task Gate(Func<Task> work)
        {
            pages++;
            Console.Error.WriteLine($"dates-review gate acquired={pages} released={releases}");
            try { await work(); }
            finally
            {
                releases++;
                Console.Error.WriteLine($"dates-review gate acquired={pages} released={releases}");
            }
        }
        store.BeforeDateRepairCommitForTest = _ =>
        {
            var audit = Directory.EnumerateFiles(Path.Combine(root, "v2", "logs"), "dates-repair-*.jsonl")
                .Single(path => path != dry.ReportFile);
            // The report writer remains open: the probe must permit its existing write access.
            using var input = new FileStream(audit, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(input);
            LocalChecks.Require(reader.ReadToEnd().Contains("\"Outcome\":\"page-intent\""),
                "page intent is flushed before repair reaches the filesystem setter");
        };
        var done = await DateRepair.RunAsync(store, pair, Observe, root, false, [], CancellationToken.None, [], [], false, StorageFailureClassifier.IsStateDamage, StorageFailureClassifier.IsDirectStateDamage, StorageFailureClassifier.IsBusy, date, withPageLock: Gate);
        store.BeforeDateRepairCommitForTest = null;
        Console.Error.WriteLine($"dates-review dry examined={dry.Examined} eligible={dry.Eligible} changed={dry.Changed} skipped={dry.Skipped}; real examined={done.Examined} eligible={done.Eligible} changed={done.Changed} skipped={done.Skipped}; acquired={pages} released={releases}");
        foreach (var line in File.ReadLines(done.ReportFile))
        {
            using var row = System.Text.Json.JsonDocument.Parse(line);
            if (row.RootElement.TryGetProperty("Outcome", out var outcome) && outcome.GetString() == "skipped")
                Console.Error.WriteLine("dates-review skipped " + line);
        }
        LocalChecks.Require(pages == 3 && done.Eligible == dry.Eligible && done.Skipped == dry.Skipped,
            "gate released each 128-row page; real and dry-run counts agree");
        LocalChecks.Require(store.Baselines(pair.PairId, ["000"]).Single().A.Mtime == target,
            "rerun settles already-stamped crash survivor");
        var live = store.Live("a", ["000"]).Single();
        LocalChecks.Require(live.Mtime == target && live.Hash == remote["000"].Hash,
            "live timestamp and hash updated in baseline transaction");
        // Exercise the merged local refresh and hash-carry path without requiring a volume journal.
        var refresher = new LocalInventory(pair.A, Side.A, store,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), V2Settings.Default, TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage);
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
        var offline = await DateRepair.RunAsync(store, pair, (_, _) => throw new HttpRequestException("offline"), root, true,
            [], CancellationToken.None, [], [], false, StorageFailureClassifier.IsStateDamage, StorageFailureClassifier.IsDirectStateDamage, StorageFailureClassifier.IsBusy, date, "129");
        LocalChecks.Require(offline.Skipped == 2 && File.ReadAllText(offline.ReportFile).Contains("Dropbox lookup: offline"),
            "per-item network failures are reported and the page completes");
        var attempts = 0;
        var busyRetry = await DateRepair.RunAsync(store, pair, Observe, root, true, [], CancellationToken.None,
            [], [], false, StorageFailureClassifier.IsStateDamage, StorageFailureClassifier.IsDirectStateDamage,
            StorageFailureClassifier.IsBusy, date, "130", withPageLock: async work =>
            {
                if (++attempts == 1) throw new Microsoft.Data.Sqlite.SqliteException("page busy", 5);
                await work();
            });
        LocalChecks.Require(attempts == 3 && busyRetry.Examined == 1 &&
            File.ReadAllText(busyRetry.ReportFile).Contains("\"Outcome\":\"waiting\",\"After\":\"130\""),
            "page busy retry preserves the original cursor and finishes the independent candidate");
        var failed = false;
        try
        {
            await DateRepair.RunAsync(store, pair, Observe, root, true, [], CancellationToken.None, [], [], false, StorageFailureClassifier.IsStateDamage, StorageFailureClassifier.IsDirectStateDamage, StorageFailureClassifier.IsBusy, date, "129",
                withPageLock: _ => throw new SyncCliBusyException("exclusive run lock unavailable"));
        }
        catch (SyncCliBusyException) { failed = true; }
        LocalChecks.Require(failed && Directory.EnumerateFiles(Path.Combine(root, "v2", "logs"))
            .Any(path => File.ReadAllText(path).Contains("\"Outcome\":\"aborted\",\"After\":\"129\"")),
            "unavailable exclusive gate records a clear resumable cursor");
        var resumed = await DateRepair.RunAsync(store, pair, Observe, root, true, [], CancellationToken.None, [], [], false, StorageFailureClassifier.IsStateDamage, StorageFailureClassifier.IsDirectStateDamage, StorageFailureClassifier.IsBusy, date, "130");
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
