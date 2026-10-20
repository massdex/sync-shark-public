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
        foreach (var name in new[] { "fix", "held/child", "excluded/child", "ignored" })
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
            live.Add(item);
            var remote = item with { ObjectId = "id:" + name, Mtime = target };
            cloud[name] = remote;
            // The old baseline already has the right date and a pre-redownload identity. Only live selection finds this.
            var saved = item with { ObjectId = "old:" + name, Mtime = target };
            var action = new SyncAction(name, 0, ActionKind.Adopt, Side.A, name, null, remote, saved, false, name);
            store.CommitResult(pair.PairId, gen, action, new ActionResult(name, Outcome.Done, saved, remote, null, null));
        }
        store.WriteSnapshot(new Snapshot("a", Side.A, gen, "cursor", live, []));
        store.AddNotices([
            new Notice(pair.PairId, 0, NoticeKind.Info, "", "Pair-wide", "pair-excluded"),
            new Notice(pair.PairId, 30, NoticeKind.UserDecision, "held", "Ancestor held", "test")]);
        store.PrepareDateRepair(pair.PairId);
        LocalChecks.Require(!store.DateRepairHeld(pair, store.Baselines(pair.PairId, ["fix"]).Single()) &&
            store.DateRepairHeld(pair, store.Baselines(pair.PairId, ["held/child"]).Single()),
            "empty notice is not a hold, real ancestor is");
        LocalChecks.Require(store.DateRepairPage(pair.PairId, "", localDate: day).Count == 4 &&
            store.DateRepairPage(pair.PairId, "", localDate: day.AddDays(-1)).Count == 0,
            "candidate selection uses live mtime instead of already-correct stale baseline mtime");
        Task<ItemState?> Observe(string path, CancellationToken _) => Task.FromResult<ItemState?>(cloud[path]);
        var dry = await DateRepair.RunAsync(store, pair, Observe, root, true, [], CancellationToken.None, from: day, to: day);
        var log = File.ReadAllText(dry.ReportFile);
        LocalChecks.Require(dry.Eligible == 1 && dry.Skipped == 3 &&
            log.Contains("Excluded by pair selection.") && log.Contains("Ignored by rule.") && log.Contains("Held or quarantined."),
            "each guard has its own auditable skip reason");
        var done = await DateRepair.RunAsync(store, pair, Observe, root, false, [], CancellationToken.None, from: day, to: day);
        var repaired = store.Baselines(pair.PairId, ["fix"]).Single().A;
        LocalChecks.Require(done.Changed == 1 && repaired.Mtime == target && repaired.ObjectId == live[0].ObjectId &&
            store.Live("a", ["fix"]).Single().Mtime == target && File.ReadAllText(Path.Combine(files, "fix")) == "same",
            "repair reconciles re-downloaded identity and date without changing content");
    }
}
