namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // Registered by LocalIgnoredChildrenChecks: these variants need real NTFS roots and writers.
    internal static async Task HeldRecovery_RealIgnoredOnlyFolderAsync()
    {
        var cases = new (string Name, bool Tombstone, string Child, int Bytes, string ExtraRule, bool Delete)[]
        {
            ("small", true, "desktop.ini", 24, "", true),
            ("boundary", true, "desktop.ini", 64 * 1024, "", true),
            ("no-tombstone", false, "desktop.ini", 24, "", false),
            ("one-megabyte", true, "desktop.ini", 1024 * 1024, "", false),
            ("extension", true, "notes.tmp", 24, "FileExtension:tmp", false),
            ("folder-overlap", true, "cache/desktop.ini", 24, "FolderName:cache", false),
            ("empty-folder", true, "cache", -1, "FolderName:cache", false)
        };
        foreach (var test in cases)
        {
            var root = LocalChecks.FreshRoot("held-ignored-" + test.Name);
            var childPath = "folder/" + test.Child;
            var bytes = Enumerable.Repeat((byte)73, Math.Max(0, test.Bytes)).ToArray();
            if (test.Bytes < 0) Directory.CreateDirectory(Path.Combine(root, "folder", test.Child));
            else LocalChecks.Seed(root, childPath, bytes);
            var recovery = Path.Combine(Path.GetDirectoryName(root)!, "recovery");
            // Exercise production result-transaction subtree retirement, not the baseline-only fake store.
            using var store = SqliteStateStore.Open(Path.Combine(Path.GetDirectoryName(root)!, "state"),
                V2Settings.Default, TimeProvider.System);
            var localRoot = LocalChecks.RootOf(root, "root-local");
            var remoteRoot = new RootDef("root-cloud", SyncProviders.Dropbox, "test-account", "/remote", "id:remote");
            string[] rules = test.ExtraRule.Length == 0 ? ["FileName:desktop.ini"] : ["FileName:desktop.ini", test.ExtraRule];
            var pair = new PairDef(LocalChecks.PairId, "held ignored child", localRoot, remoteRoot, [], rules, true);
            store.SavePair(pair);
            var writer = await LocalWriter.OpenAsync(localRoot, Side.A, pair.PairId, LocalChecks.InstallationId,
                recovery, default, isStateDamage: StorageFailureClassifier.IsStateDamage, ignoreRules: PairInclude.IgnoreRules(pair));
            var remote = new FakeEndpoint(Side.B, remoteRoot.RootId) { DropboxHashes = true, RequireHashes = true };
            var provider = new FakeEndpointProvider().Add(remoteRoot, remote)
                .AddInventory(remoteRoot, new RecoveryEvidenceInventory(remote) { Tombstone = test.Tombstone })
                .AddWriter(localRoot, writer)
                .AddInventory(localRoot, LocalChecks.Inventory(root, store, TimeProvider.System));
            var settings = V2Settings.Default with { MaxWorkers = 1, ProgressInterval = TimeSpan.Zero };
            var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage),
                provider, settings, TimeProvider.System);
            var progress = new RecordingProgress();
            var plan = await run.PreviewFirstSyncAsync(pair.PairId, progress, default);
            Require(plan.HeldDeletions.Any(item => item.Path == "folder") == test.Tombstone &&
                plan.HeldDeletions.All(item => !PairPath.IsUnder(item.Path, "folder")),
                "exact tombstone holds the decision-68 folder, but ignored children need no evidence: " + test.Name);
            var report = await run.ApplyAsync(pair.PairId, plan, progress, default, ReviewedHeld(plan));
            Require(report.Outcome == RunOutcomes.Complete && store.FirstSyncDone(pair.PairId),
                "held ignored-only folder resolves completely: " + test.Name);
            var childFullPath = Path.Combine(root, childPath.Replace('/', Path.DirectorySeparatorChar));
            var childExists = test.Bytes < 0 ? Directory.Exists(childFullPath) : File.Exists(childFullPath);
            Require(Directory.Exists(Path.Combine(root, "folder")) == !test.Delete &&
                childExists == !test.Delete &&
                (await remote.ObserveAsync("folder", default) is not null) == !test.Delete &&
                await remote.ObserveAsync(childPath, default) is null,
                "held folder discounts only small FileName files; other ignored content keeps and syncs its parent: " + test.Name);
            if (!test.Delete && test.Bytes >= 0)
                Require(File.ReadAllBytes(childFullPath).SequenceEqual(bytes), "kept ignored content is unchanged: " + test.Name);
            Require(test.Delete
                    ? !store.Live(localRoot.RootId).Any(item => PairPath.IsSelfOrUnder(item.Path, "folder"))
                    : store.Live(localRoot.RootId).Any(item => item.Path == childPath),
                "held folder deletion retires ignored inventory descendants without live orphans: " + test.Name);
            Require(!Directory.GetFiles(recovery, "*.recovery", SearchOption.AllDirectories).Any(),
                "held cleanup never carries ignored data requiring Recovery: " + test.Name);
        }
    }
}
