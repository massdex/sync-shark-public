namespace Massdex.SyncShark.V2;

internal static class DateRepairRangeChecks
{
    internal static async Task RunAsync(string root)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");
        var day = new DateOnly(2026, 10, 18);
        var (start, end) = DateRepair.UtcRange(day, null, null, zone);
        LocalChecks.Require(start == new DateTime(2026, 10, 17, 21, 0, 0, DateTimeKind.Utc).Ticks &&
            end == new DateTime(2026, 10, 18, 21, 0, 0, DateTimeKind.Utc).Ticks,
            "single local day crosses the UTC date boundary in UTC+3");
        var range = DateRepair.UtcRange(null, day.AddDays(-1), day.AddDays(1), zone);
        LocalChecks.Require(range.Start == start - TimeSpan.TicksPerDay && range.End == end + TimeSpan.TicksPerDay,
            "both local range days are inclusive");
        var hours = DateRepair.UtcRange(null, DateRepairBound.Parse("2026-10-18T01:15"),
            DateRepairBound.Parse("2026-10-18T03:45"), zone);
        LocalChecks.Require(hours.Start == start + TimeSpan.FromMinutes(75).Ticks &&
            hours.End == start + TimeSpan.FromMinutes(225).Ticks,
            "local minute bounds convert UTC+3 and timed --to is exclusive");
        foreach (var args in new[] {
            new[] { "--from", "2026-10-19", "--to", "2026-10-17" },
            new[] { "--local-date", "2026-10-18", "--from", "2026-10-17" },
            new[] { "--to", "not-a-date" } })
        {
            var rejected = false;
            try { SyncCliRegistry.Parse(["--cli", "dates", "repair", "--pair", "p", "--dry-run", ..args]); }
            catch (SyncCliUsageException) { rejected = true; }
            LocalChecks.Require(rejected, "invalid/ambiguous local date selection is explained");
        }
        Directory.CreateDirectory(root);
        var files = Path.Combine(root, "files");
        Directory.CreateDirectory(files);
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), StorageChecks.Fast, TimeProvider.System);
        var pair = new PairDef("range", "range", LocalChecks.RootOf(files, "a"),
            new RootDef("b", SyncProviders.Dropbox, "account", "/test", "id:root"), ["excluded"], ["FileName:ignored"], true);
        store.SavePair(pair);
        var target = new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var stamp = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(12, 0)), TimeZoneInfo.Local));
        var live = new List<ItemState>();
        var cloud = new Dictionary<string, ItemState>();
        var gen = store.BeginGeneration("a", "test");
        foreach (var name in new[] { "fix", "held/child", "excluded/child", "ignored", "listed", "bad-hash", "db-busy", "remote-failed" })
        {
            var full = Path.Combine(files, name);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "same");
            File.SetLastWriteTimeUtc(full, stamp.UtcDateTime);
            using var handle = File.OpenRead(full);
            LocalChecks.Require(PilotDirectoryLease.GetFileInformationByHandle(handle.SafeFileHandle, out var info), "range identity");
            var item = new ItemState(name, ItemKind.File,
                SyncLocalEndpointStore.ObjectId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow),
                null, LocalChecks.BlockHash(LocalChecks.Bytes("same")), handle.Length, stamp, ItemFlags.None);
            live.Add(item with { Hash = null });
            var remote = item with { ObjectId = "id:" + name, Mtime = target };
            cloud[name] = remote;
            // The old baseline already has the right date and a pre-redownload identity. Only live selection finds this.
            var saved = item with { ObjectId = "old:" + name, Mtime = target };
            var action = new SyncAction(name, 0, ActionKind.Adopt, Side.A, name, null, remote, saved, false, name);
            store.CommitResult(pair.PairId, gen, action, new ActionResult(name, Outcome.Done, saved, remote, null, null));
        }
        foreach (var local in live) DateProvenanceChecks.Seed(store, pair, local with { Hash = cloud[local.Path].Hash }, Path.Combine(root, "state"));
        store.WriteSnapshot(new Snapshot("a", Side.A, gen, "cursor", live, []));
        store.AddNotices([
            new Notice(pair.PairId, 0, NoticeKind.Info, "", "Pair-wide", "pair-excluded"),
            new Notice(pair.PairId, 0, NoticeKind.Info, "fix", "Date retry", "local-download-date"),
            new Notice(pair.PairId, 30, NoticeKind.UserDecision, "held", "Ancestor held", "test")]);
        store.PrepareDateRepair(pair.PairId);
        LocalChecks.Require(!store.DateRepairHeld(pair, store.Baselines(pair.PairId, ["fix"]).Single()) &&
            store.DateRepairHeld(pair, store.Baselines(pair.PairId, ["held/child"]).Single()),
            "empty notice is not a hold, real ancestor is");
        LocalChecks.Require(store.DateRepairPage(pair.PairId, "", localDate: day).Count == 8 &&
            store.DateRepairPage(pair.PairId, "", localDate: day.AddDays(-1)).Count == 0,
            "candidate selection uses live mtime instead of already-correct stale baseline mtime");
        LocalChecks.Require(store.DateRepairPage(pair.PairId, "", from: DateRepairBound.Parse("2026-10-18T11:00"),
            to: DateRepairBound.Parse("2026-10-18T12:00")).Count == 0 &&
            store.DateRepairPage(pair.PairId, "", from: DateRepairBound.Parse("2026-10-18T12:00"),
            to: DateRepairBound.Parse("2026-10-18T12:01")).Count == 8,
            "SQL minute selection includes from and excludes timed to");
        File.WriteAllText(Path.Combine(files, "bad-hash"), "edit");
        File.SetLastWriteTimeUtc(Path.Combine(files, "bad-hash"), stamp.UtcDateTime);
        var list = Path.Combine(root, "exclude.txt");
        File.WriteAllLines(list, ["listed"]);
        Task<ItemState?> Observe(string path, CancellationToken _)
        {
            if (path == "remote-failed") throw new HttpRequestException("single lookup failure");
            return Task.FromResult<ItemState?>(cloud[path]);
        }
        var dry = await DateRepair.RunAsync(store, pair, Observe, root, true, [], CancellationToken.None, StorageFailureClassifier.IsStateDamage,
            from: DateRepairBound.Parse("2026-10-18T11:00"), to: DateRepairBound.Parse("2026-10-18T13:00"), excludeList: list);
        var log = File.ReadAllText(dry.ReportFile);
        LocalChecks.Require(dry.Eligible == 2 && dry.Skipped == 6 &&
            log.Contains("Excluded by pair selection.") && log.Contains("Ignored by rule.") && log.Contains("Held or quarantined.") &&
            log.Contains("Excluded by --exclude-list.") && log.Contains("Locked file content differs") &&
            log.Contains("single lookup failure"),
            "hour window, exclusion list, null hashes, and independent failures have exact reasons");
        foreach (var code in new[] { 11, 26, 5 })
        {
            var fault = new IOException("wrapped date failure", new Microsoft.Data.Sqlite.SqliteException("synthetic", code));
            var before = store.Baselines(pair.PairId, ["fix"]).Single();
            store.BeforeDateRepairCommitForTest = _ => throw fault;
            Exception? observed = null;
            DateRepairReport? report = null;
            try
            {
                report = await DateRepair.RunAsync(store, pair, Observe, root, false, [], CancellationToken.None,
                    StorageFailureClassifier.IsStateDamage, from: day, to: day, excludeList: list);
            }
            catch (Exception error) { observed = error; }
            finally { store.BeforeDateRepairCommitForTest = null; }
            LocalChecks.Require(store.Baselines(pair.PairId, ["fix"]).Single() == before,
                "wrapped commit failure never publishes a repaired baseline");
            LocalChecks.Require(code == 5
                ? observed is null && report is { Changed: 0 }
                : observed is not null && StorageFailureClassifier.ScopeOf(observed, default) == FailureRetryOwner.FailureScope.State,
                "date repair propagates wrapped corruption but retains non-damage item handling");
        }
        store.BeforeDateRepairCommitForTest = baseline =>
        {
            if (baseline.ObjectKey == "db-busy") throw new Microsoft.Data.Sqlite.SqliteException("single item busy", 5);
        };
        var done = await DateRepair.RunAsync(store, pair, Observe, root, false, [], CancellationToken.None, StorageFailureClassifier.IsStateDamage, from: day, to: day, excludeList: list);
        store.BeforeDateRepairCommitForTest = null;
        LocalChecks.Require(done.Skipped == 7 && File.ReadAllText(done.ReportFile).Contains("single item busy"),
            "a transient database failure skips its item and independent repairs finish");
        var repaired = store.Baselines(pair.PairId, ["fix"]).Single().A;
        LocalChecks.Require(done.Changed == 1 && repaired.Mtime == target && repaired.ObjectId == live[0].ObjectId &&
            store.Live("a", ["fix"]).Single() is { Hash: not null } fixedLive && fixedLive.Mtime == target &&
            File.ReadAllText(Path.Combine(files, "fix")) == "same",
            "repair reconciles null hash, re-downloaded identity and date without changing content");
        LocalChecks.Require(!store.OpenNotices(pair.PairId).Any(n => n.ReasonKey == "local-download-date"),
            "successful repair closes its date-only retry notice");
        LocalChecks.Require(!DateRepair.ItemFailure(new Microsoft.Data.Sqlite.SqliteException("corrupt", 11), StorageFailureClassifier.IsStateDamage) &&
            DateRepair.ItemFailure(new Microsoft.Data.Sqlite.SqliteException("busy", 5), StorageFailureClassifier.IsStateDamage),
            "corruption stops, transient item busy skips");
    }
}
