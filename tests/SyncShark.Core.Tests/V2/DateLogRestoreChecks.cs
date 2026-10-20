namespace Massdex.SyncShark.V2;

internal static class DateLogRestoreChecks
{
    internal static async Task RunAsync(string root)
    {
        foreach (var time in new[] { "2026-10-18T12:00:00Z", "2026-10-18T15:00:00.0000000+03:00", "2026-10-18 12:00:00+00:00" })
            LocalChecks.Require(DateProvenanceLog.TryParse($"Sync Shark: [{time}] Applying: Child/file (1,000/2,000)", "Parent", out var path, out var stamp) &&
                path == "Parent/Child/file" && stamp.Hour - stamp.Offset.Hours == 12, "explicit timestamp and prefix parsing");
        LocalChecks.Require(DateProvenanceLog.TryParse("Sync Shark: [2026-10-18T12:00:00Z] Applying: Child/file (5/100) (+1,000 files inside deleted folders)",
            "Parent", out var expandedPath, out _) && expandedPath == "Parent/Child/file", "expanded deletion progress suffix names only the printed item");
        foreach (var line in new[] { "Sync Shark: [2026-10-18T12:00:00] Applying: file (1/2)",
            "Sync Shark: [2026-10-18T12:00:00Z] Applying: ../file (1/2)",
            "Sync Shark: [2026-10-18T12:00:00Z] Applying: 100 so far", "Applying: file (1/2)" })
            LocalChecks.Require(!DateProvenanceLog.TryParse(line, "", out _, out _), "no inferred throttled or ambiguous events");
        var invocation = SyncCliRegistry.Parse(["--cli", "dates", "repair", "--pair", "pair", "--dry-run",
            "--provenance-log", "one.log", "--provenance-log", "two.log", "--provenance-prefix", "Parent", "--provenance-prefix", "."]);
        var sources = DateProvenanceLog.Sources(invocation.ProvenanceLogs, invocation.ProvenancePrefixes);
        LocalChecks.Require(sources.Count == 2 && sources[0].Prefix == "Parent" && sources[1].Prefix == "", "repeatable scope mapping");
        LocalChecks.Require(SyncCliRegistry.Parse(["--cli", "dates", "restore", "--audit-log", "audit.jsonl", "--dry-run"]).Flag("--dry-run"), "restore CLI");
        var files = Path.Combine(root, "files"); Directory.CreateDirectory(files);
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), StorageChecks.Fast, TimeProvider.System);
        var pair = new PairDef("log-repair", "Log repair", LocalChecks.RootOf(files, "a"),
            new RootDef("b", SyncProviders.Dropbox, "account", "/test", "id:root"), [], [], true);
        store.SavePair(pair);
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var gen = store.BeginGeneration("a", "test");
        var locals = new List<ItemState>(); var clouds = new Dictionary<string, ItemState>(); var logs = new List<string>();
        foreach (var (name, delta) in new[] { ("zero", 0L), ("two", TimeSpan.TicksPerSecond * 2L),
            ("before", -1L), ("late", TimeSpan.TicksPerSecond * 2L + 1), ("changed", 1L), ("date-changed", 1L), ("absent", 1L), ("wrong-content", 1L) })
        {
            var full = Path.Combine(files, name); File.WriteAllText(full, "same"); File.SetLastWriteTimeUtc(full, now.UtcDateTime);
            using var file = File.OpenRead(full);
            LocalChecks.Require(PilotDirectoryLease.GetFileInformationByHandle(file.SafeFileHandle, out var info), "log identity");
            var local = new ItemState(name, ItemKind.File,
                SyncLocalEndpointStore.ObjectId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow),
                null, LocalChecks.BlockHash(LocalChecks.Bytes("same")), 4, now, ItemFlags.None);
            var cloud = local with { ObjectId = "id:" + name, Mtime = now.AddYears(-2) }; clouds[name] = cloud;
            store.CommitResult(pair.PairId, gen, new SyncAction(name, 0, ActionKind.Adopt, Side.A, name, null, cloud, local, false, name),
                new ActionResult(name, Outcome.Done, local, cloud, null, null));
            locals.Add(local with { Hash = null }); // fresh locked hash is sufficient even without a stored hash
            if (name != "absent") logs.Add(SyncCliRegistry.FormatProgress($"Applying: {name} (5/100)", now.AddTicks(delta)));
        }
        store.WriteSnapshot(new Snapshot("a", Side.A, gen, "cursor", locals, []));
        var logPath = Path.Combine(root, "apply.err.txt"); File.WriteAllLines(logPath, logs);
        var evidence = DateProvenanceLog.Sources([logPath], []);
        Task<ItemState?> Remote(string path, CancellationToken _) => Task.FromResult<ItemState?>(clouds[path]);
        File.WriteAllText(Path.Combine(files, "wrong-content"), "edit");
        File.SetLastWriteTimeUtc(Path.Combine(files, "wrong-content"), now.UtcDateTime);
        var report = await DateRepair.RunAsync(store, pair, Remote, root, true, [], CancellationToken.None,
            reportMismatch: true, provenanceSources: evidence);
        LocalChecks.Require(report.Mismatches == 8 && File.ReadAllText(report.CsvFile!).Contains("log-correlated"), "mismatch includes both evidence classes");
        var repair = await DateRepair.RunAsync(store, pair, Remote, root, false, [], CancellationToken.None, provenanceSources: evidence);
        LocalChecks.Require(repair.Changed == 4 && repair.Skipped == 4, "inclusive zero/two second edges; absent, outside events and changed content untouched");
        var preview = await DateRepair.RestoreAsync(store, pair, repair.ReportFile, root, true, [], CancellationToken.None);
        LocalChecks.Require(preview.Eligible == 4 && preview.Changed == 0 && File.GetLastWriteTimeUtc(Path.Combine(files, "zero")) == now.AddYears(-2).UtcDateTime,
            "restore dry-run changes no timestamp");
        File.WriteAllText(Path.Combine(files, "changed"), "edit");
        File.SetLastWriteTimeUtc(Path.Combine(files, "changed"), now.AddYears(-2).UtcDateTime);
        File.SetLastWriteTimeUtc(Path.Combine(files, "date-changed"), now.AddHours(1).UtcDateTime);
        var restore = await DateRepair.RestoreAsync(store, pair, repair.ReportFile, root, false, [], CancellationToken.None);
        LocalChecks.Require(restore.Changed == 2 && restore.Skipped == 2 && File.ReadAllText(Path.Combine(files, "changed")) == "edit" &&
            File.GetLastWriteTimeUtc(Path.Combine(files, "date-changed")) == now.AddHours(1).UtcDateTime, "restore refuses content and timestamp changes");
        foreach (var name in new[] { "zero", "two" })
            LocalChecks.Require(File.GetLastWriteTimeUtc(Path.Combine(files, name)) == now.UtcDateTime &&
                store.Live("a", [name]).Single().Mtime == now && store.Baselines(pair.PairId, [name]).Single().A.Mtime == now,
                "restore round trip reconciles file, inventory and baseline");
    }
}
