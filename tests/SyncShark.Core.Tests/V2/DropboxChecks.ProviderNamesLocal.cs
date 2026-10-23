namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task ProviderNamesLocalAsync(string root)
    {
        var local = Path.Combine(root, "local");
        var recovery = Path.Combine(root, "recovery");
        Directory.CreateDirectory(Path.Combine(local, "folder"));
        var file = Path.Combine(local, "folder", "refused.txt");
        await File.WriteAllTextAsync(file, "original bytes remain here");
        var beforeBytes = await File.ReadAllBytesAsync(file);
        var beforeTime = File.GetLastWriteTimeUtc(file);
        var beforeId = LocalChecks.ObjectIdOf(Path.GetDirectoryName(file)!);
        var settings = StorageChecks.Fast with { MaxWorkers = 1, ProgressInterval = TimeSpan.Zero };
        using var h = new Harness();
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), settings, TimeProvider.System);
        var localRoot = LocalChecks.RootOf(local, "root-local");
        var pair = new PairDef("provider-names-local", "real local name", localRoot, Root(), [], [], true);
        store.SavePair(pair);
        var writer = await LocalWriter.OpenAsync(localRoot, Side.A, "provider-names-local", LocalChecks.InstallationId,
            recovery, CancellationToken.None, isStateDamage: StorageFailureClassifier.IsStateDamage, settings, providerNameRefused: item =>
                store.LiveSpellings(localRoot.RootId, item.Path).Any(known => known.Path == item.Path &&
                    known.ObjectId == item.ObjectId && known.Flags.HasFlag(ItemFlags.ProviderNameRefused)));
        var provider = new FakeEndpointProvider().AddWriter(localRoot, writer)
            .AddInventory(localRoot, new LocalInventory(localRoot, Side.A, store, new HashSet<string>(), settings, TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage))
            .AddInventory(pair.B, new DropboxInventory(Root(), Side.B, h.Client, store, "provider-names-local"))
            .AddWriter(pair.B, h.Writer());
        h.Wire.NameWriteErrors["folder/refused.txt"] = "disallowed_name";
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage), provider, settings,
            TimeProvider.System);
        var plan = await run.PreviewFirstSyncAsync("provider-names-local", new RecordingProgress(), CancellationToken.None);
        var report = await run.ApplyAsync("provider-names-local", plan, new RecordingProgress(), CancellationToken.None);
        Require(report.Failed == 0 && store.FirstSyncDone("provider-names-local") && File.Exists(file) &&
            (await File.ReadAllBytesAsync(file)).SequenceEqual(beforeBytes) && File.GetLastWriteTimeUtc(file) == beforeTime &&
            LocalChecks.ObjectIdOf(Path.GetDirectoryName(file)!) == beforeId,
            "real NTFS refusal leaves bytes, timestamps, path and enclosing identity untouched");
        var folder = (await writer.ObserveAsync("folder", CancellationToken.None))!;
        var deletion = new SyncAction("explicit-parent-delete", 6, ActionKind.Delete, Side.A, "folder", null,
            null, folder, true, null);
        var budget = CarriedIgnoredDeletes.Snapshot(store, pair, deletion);
        Require(budget.Files == 1 && budget.Bytes == beforeBytes.Length &&
            FolderDelete.SyncedDescendants(store, pair, localRoot.RootId, "folder").Count == 0,
            "decision 80 discloses refused child as carried ignored content, not a standalone delete");
        // An independent explicit parent deletion must retain the existing carried-child semantics and Recovery.
        await AuthorizedFixtureDeleteAsync(writer, deletion, CancellationToken.None);
        Require(!Directory.Exists(Path.Combine(local, "folder")) &&
            Directory.EnumerateFiles(recovery, "*.recovery", SearchOption.AllDirectories)
                .Any(path => File.ReadAllBytes(path).SequenceEqual(beforeBytes)),
            "a later explicit parent delete carries its refused child with Recovery, without blocking the folder");
    }
}
