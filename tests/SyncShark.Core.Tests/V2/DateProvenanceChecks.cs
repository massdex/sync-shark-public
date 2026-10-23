using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static class DateProvenanceChecks
{
    // Synthetic completed restore evidence; never seed provenance by a baseline or a pending action.
    internal static void Seed(SqliteStateStore store, PairDef pair, ItemState local, string stateRoot)
    {
        store.AddNotices([new Notice(pair.PairId, 0, NoticeKind.Info, local.Path, "Fixture completed restore", "retained-version-restored")
        { RetainedVersion = new RetainedNoticeVersion("restore-" + local.Path, Side.A, local, local) }]);
        using var connection = new SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(stateRoot));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE notice SET state='closed',created=$time WHERE path=$path AND reason_key='retained-version-restored';";
        command.Parameters.AddWithValue("$path", local.Path);
        command.Parameters.AddWithValue("$time", local.Mtime.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    internal static async Task RunAsync(string root)
    {
        await DateLogRestoreChecks.RunAsync(Path.Combine(root, "log-restore"));
        Directory.CreateDirectory(root);
        var files = Path.Combine(root, "files"); Directory.CreateDirectory(files);
        var state = Path.Combine(root, "state");
        using var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System);
        var pair = new PairDef("provenance", "Provenance", LocalChecks.RootOf(files, "a"),
            new RootDef("b", SyncProviders.Dropbox, "account", "/test", "id:root"), [], [], true);
        store.SavePair(pair);
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var cloudTime = now.AddYears(-2);
        var locals = new List<ItemState>(); var clouds = new Dictionary<string, ItemState>();
        var gen = store.BeginGeneration("a", "test");
        foreach (var name in new[] { "written", "user-date", "no-history" })
        {
            var full = Path.Combine(files, name); File.WriteAllText(full, "same"); File.SetLastWriteTimeUtc(full, now.UtcDateTime);
            using var file = File.OpenRead(full);
            LocalChecks.Require(PilotDirectoryLease.GetFileInformationByHandle(file.SafeFileHandle, out var info), "provenance identity");
            var local = new ItemState(name, ItemKind.File,
                SyncLocalEndpointStore.ObjectId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow),
                null, LocalChecks.BlockHash(LocalChecks.Bytes("same")), 4, now, ItemFlags.None);
            locals.Add(local); var cloud = local with { ObjectId = "id:" + name, Mtime = cloudTime }; clouds[name] = cloud;
            var action = new SyncAction(name, 0, ActionKind.Adopt, Side.A, name, null, cloud, local, false, name);
            store.CommitResult(pair.PairId, gen, action, new ActionResult(name, Outcome.Done, local, cloud, null, null));
            if (name != "no-history") Seed(store, pair, local with { Mtime = name == "written" ? now : now.AddHours(-1) }, state);
        }
        store.WriteSnapshot(new Snapshot("a", Side.A, gen, "cursor", locals, []));
        Task<ItemState?> Remote(string path, CancellationToken _) => Task.FromResult<ItemState?>(clouds[path]);
        var report = await DateRepair.RunAsync(store, pair, Remote, root, true, [], CancellationToken.None, reportMismatch: true);
        var csv = File.ReadAllText(report.CsvFile!);
        LocalChecks.Require(report.Mismatches == 3 && report.MissingWriteHistory == 1 && report.LocalDateNotWritten == 1 &&
            csv.Contains("user-date") && csv.Contains("no-history") && csv.Contains("\"written\"") && csv.Contains("no evidence"),
            "read-only mismatch CSV distinguishes nonmatching write from missing history");
        LocalChecks.Require(locals.All(item => File.GetLastWriteTimeUtc(Path.Combine(files, item.Path)) == now.UtcDateTime),
            "mismatch reporting changes no dates");
        var repaired = await DateRepair.RunAsync(store, pair, Remote, root, false, [], CancellationToken.None);
        LocalChecks.Require(repaired.Changed == 1 && repaired.Examples.Single().Evidence == "restore-notice" && repaired.MissingWriteHistory == 1 && repaired.LocalDateNotWritten == 1 &&
            File.GetLastWriteTimeUtc(Path.Combine(files, "user-date")) == now.UtcDateTime &&
            File.GetLastWriteTimeUtc(Path.Combine(files, "no-history")) == now.UtcDateTime &&
            File.ReadAllText(repaired.ReportFile).Contains("OldUtc"),
            "repair only stamps a proven write, preserves user dates and audits original time");
        var invocation = SyncCliRegistry.Parse(["--cli", "dates", "report-mismatch", "--pair", pair.PairId]);
        LocalChecks.Require(invocation.Command == "dates report-mismatch" &&
            !SyncCliRegistry.Rows.Single(row => row.Command == invocation.Command).Mutating,
            "mismatch command is read-only and needs no yes");
    }
}
