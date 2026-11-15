namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    internal static async Task DateRepairMetadataOutcomesAsync(string root)
    {
        using var h = new Harness();
        var localRoot = Directory.CreateDirectory(Path.Combine(root, "files")).FullName;
        var stateRoot = Path.Combine(root, "state");
        using var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = new PairDef("dates", "Dates", LocalChecks.RootOf(localRoot, "local"), Root(), [], [], true);
        store.SavePair(pair);
        var stamp = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        var original = stamp.AddYears(-1);
        var names = new[] { "a.txt", "b.txt", "c.txt" };
        foreach (var name in names) h.Wire.SeedFile(name, "same content", original);
        var remote = await h.Writer(Path.Combine(root, "spool")).ObservePageAsync(names, CancellationToken.None);
        var gen = store.BeginGeneration(pair.A.RootId, "test");
        foreach (var name in names)
        {
            var path = Path.Combine(localRoot, name);
            File.WriteAllText(path, "same content");
            File.SetLastWriteTimeUtc(path, stamp.UtcDateTime);
            using var file = File.OpenRead(path);
            Require(PilotDirectoryLease.GetFileInformationByHandle(file.SafeFileHandle, out var info), "fixture identity readable");
            var cloud = remote[name]!;
            var local = cloud with { Mtime = stamp, VersionId = null,
                ObjectId = SyncLocalEndpointStore.ObjectId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow) };
            var action = new SyncAction("adopt-" + name, 0, ActionKind.Adopt, Side.A, name, null, cloud, local, false, name);
            store.CommitResult(pair.PairId, gen, action, new ActionResult(action.ActionId, Outcome.Done, local, cloud, null, null));
        }
        foreach (var baseline in store.Baselines(pair.PairId)) DateProvenanceChecks.Seed(store, pair, baseline.A, stateRoot);
        store.WriteSnapshot(new Snapshot(pair.A.RootId, Side.A, gen, "dates-cursor", store.Baselines(pair.PairId).Select(b => b.A).ToArray(), []));
        h.Wire.MetadataFailurePath = DropboxWire.RootId + "/c.txt";
        var before = h.Wire.Calls.Count(call => call.EndsWith("/get_current_account"));
        var report = await DateRepair.RunAsync(store, pair, (_, _) => throw new InvalidOperationException("unexpected individual lookup"),
            root, false, [], CancellationToken.None, [], [], false, StorageFailureClassifier.IsStateDamage,
            StorageFailureClassifier.IsDirectStateDamage, StorageFailureClassifier.IsBusy,
            remotePage: (paths, failed, token) => h.Client.ReadMetadataPagesAsync(paths, 2,
                entry => entry is { ClientModifiedUtc: not null } ? DropboxItems.ToItem(entry) : null, token, failed));
        Require(report.Changed == 2 && report.Skipped == 1, "actual date repair preserves successes beside a failed later-page lookup");
        Require(h.Wire.Calls.Count(call => call.EndsWith("/get_current_account")) - before == 2,
            "date repair uses two verified metadata pages when its candidate count exceeds the configured page size");
        foreach (var name in names)
        {
            var expected = name == "c.txt" ? stamp : original;
            Require(File.GetLastWriteTimeUtc(Path.Combine(localRoot, name)) == expected.UtcDateTime &&
                store.Baselines(pair.PairId, [name]).Single().A.Mtime == expected,
                "file and baseline dates agree for every successful and failed candidate");
            Require(File.ReadAllText(Path.Combine(localRoot, name)) == "same content", "date repair never rewrites content");
        }
    }

    internal static async Task DateRepairMetadataPageAsync()
    {
        await SourceAbsenceBatchAsync();
        await ReviewedRemovalRootListingAsync();
        using var fixture = new Harness();
        fixture.Wire.SeedFile("a.txt", "a");
        fixture.Wire.SeedFile("b.txt", "b");
        var rows = await fixture.Client.ReadMetadataPagesAsync(["a.txt", "b.txt"], V2Tunables.Default.MetadataPagePaths, entry => entry, CancellationToken.None);
        LocalChecks.Require(rows.Count == 2 && rows.Values.All(row => row is not null), "page returns exact metadata");
        LocalChecks.Require(fixture.Wire.Calls.Count(call => call.EndsWith("/get_current_account")) == 1 &&
            fixture.Wire.Calls.Count(call => call.EndsWith("/get_metadata")) == 3,
            "page performs one account and one root check, plus one metadata request per candidate");
        await fixture.Client.ReadMetadataPagesAsync(["a.txt"], V2Tunables.Default.MetadataPagePaths, entry => entry, CancellationToken.None);
        LocalChecks.Require(fixture.Wire.Calls.Count(call => call.EndsWith("/get_current_account")) == 2 &&
            fixture.Wire.Calls.Count(call => call.EndsWith("/get_metadata")) == 5,
            "next page freshly verifies account and root");
        fixture.Wire.MetadataFailurePath = DropboxWire.RootId + "/a.txt";
        var errors = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var partial = await fixture.Client.ReadMetadataPagesAsync(["a.txt", "b.txt"], V2Tunables.Default.MetadataPagePaths, entry => entry, CancellationToken.None,
            (path, error) => errors[path] = error.Message);
        LocalChecks.Require(errors.ContainsKey("a.txt") && partial.ContainsKey("b.txt") && !partial.ContainsKey("a.txt"),
            "one failed Dropbox lookup does not discard successful sibling metadata");
        LocalChecks.Require(fixture.Wire.Calls.All(call => call.EndsWith("/get_current_account") || call.EndsWith("/get_metadata")),
            "metadata repair sends no content, upload or deletion requests");
    }
}
