namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    // Decision 80: a provider refusal is not the small FileName-rule disposable-content exception.
    private static async Task ProviderRefusedSmallRecoveryAsync(string root, bool inherited)
    {
        var local = Path.Combine(root, "local");
        var recovery = Path.Combine(root, "recovery");
        var relative = inherited ? "folder/cache/desktop.ini" : "folder/desktop.ini";
        var refusedPath = inherited ? "folder/cache" : relative;
        var file = Path.Combine(local, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var bytes = System.Text.Encoding.UTF8.GetBytes("unique refused content, not disposable");
        await File.WriteAllBytesAsync(file, bytes);
        var settings = StorageChecks.Fast with { MaxWorkers = 1, ProgressInterval = TimeSpan.Zero };
        using var h = new Harness();
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var localRoot = LocalChecks.RootOf(local, "root-local");
        var pair = new PairDef("recovery-refusal", "Recovery refusal", localRoot, Root(), [], [], true);
        store.SavePair(pair);
        var writer = await ProductionEndpoints.OpenLocalWriterAsync(store, localRoot, Side.A, pair,
            LocalChecks.InstallationId, recovery, CancellationToken.None, settings);
        var endpoints = new FakeEndpointProvider().AddWriter(localRoot, writer)
            .AddInventory(localRoot, new LocalInventory(localRoot, Side.A, store, new HashSet<string>(), settings, TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage)
                { GapIo = new LocalGapIo(store, settings, new HashSet<string>(), () => false) })
            .AddInventory(pair.B, new DropboxInventory(Root(), Side.B, h.Client, store, pair.PairId))
            .AddWriter(pair.B, h.Writer());
        h.Wire.NameWriteErrors[refusedPath] = "disallowed_name";
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException), endpoints,
            settings, TimeProvider.System);
        var plan = await run.PreviewFirstSyncAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        await run.ApplyAsync(pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
        Require(store.Live(localRoot.RootId).Single(item => item.Path == refusedPath)
            .Flags.HasFlag(ItemFlags.ProviderNameRefused), "setup: real Dropbox response durably marked the refused source");
        if (inherited)
            Require(!store.Live(localRoot.RootId).Single(item => item.Path == relative)
                .Flags.HasFlag(ItemFlags.ProviderNameRefused), "inherited child has no synthetic refusal flag");
        // The user later adds a matching small-file rule. It must not erase the stronger Recovery obligation.
        var withRule = pair with { IgnoreRules = ["FileName:desktop.ini"] };
        store.SavePair(withRule);
        var cleanup = await ProductionEndpoints.OpenLocalWriterAsync(store, localRoot, Side.A, withRule,
            LocalChecks.InstallationId, recovery, CancellationToken.None, settings);
        endpoints.AddWriter(localRoot, cleanup);
        // A real remote parent deletion is propagated by the public run/planner/executor path.
        var remoteFolder = (await h.Writer().ObserveAsync("folder", CancellationToken.None))!;
        await AuthorizedFixtureDeleteAsync(h.Writer(), new SyncAction("user-remote-delete", 6, ActionKind.Delete, Side.B,
            "folder", null, null, remoteFolder, false, null), CancellationToken.None);
        await run.RequestRescanAsync([localRoot.RootId, pair.B.RootId], CancellationToken.None);
        var result = await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(result.Failed == 0 && result.Deleted == 1, "propagated parent deletion completes without standalone refused child effects");
        Require(!Directory.Exists(Path.Combine(local, "folder")), "reviewed enclosing folder deletion completes");
        Require(Directory.Exists(recovery) && Directory.EnumerateFiles(recovery, "*.recovery", SearchOption.AllDirectories)
            .Any(path => File.ReadAllBytes(path).SequenceEqual(bytes)),
            "small FileName match still receives byte-exact Recovery when refusal is direct or inherited");
    }

    private static async Task ProviderRefusedHeldDescendantAsync(string root, bool inherited = false)
    {
        var settings = StorageChecks.Fast with { MaxWorkers = 1, ProgressInterval = TimeSpan.Zero };
        using var store = SqliteStateStore.Open(root, settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = new PairDef("p1", "held refusal",
            new RootDef("ra", SyncProviders.Local, null, "C:\\synthetic", "id:local"),
            new RootDef("rb", SyncProviders.Dropbox, "account", "/synthetic", "id:cloud"), [], [], true);
        store.SavePair(pair);
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        var folder = a.Seed("folder", "", ItemKind.Folder);
        var refusedPath = inherited ? "folder/refused" : "folder/refused.txt";
        if (inherited) a.Seed(refusedPath, "", ItemKind.Folder);
        var child = a.Seed(inherited ? refusedPath + "/child.txt" : refusedPath, "unique content");
        var writerA = new ProviderFollowupWriter(a);
        var writerB = new ProviderFollowupWriter(b);
        writerB.Refused.Add(refusedPath);
        var provider = new FakeEndpointProvider().Add(pair.A, a).Add(pair.B, b)
            .AddWriter(pair.A, writerA).AddWriter(pair.B, writerB);
        var executor = new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
        var run = new SyncRun(store, executor, provider, settings, TimeProvider.System);
        if (inherited)
        {
            // Persist a real folder refusal before the held-parent evidence is discovered. Execute only
            // the planner's folder action, as an interrupted first apply could; no child flag is injected.
            var initial = await run.PreviewFirstSyncAsync("p1", new RecordingProgress(), CancellationToken.None);
            var mkdir = initial.Actions.Single(action => action.Kind == ActionKind.MkDir && action.Path == refusedPath);
            await foreach (var outcome in executor.ApplyAsync(initial with { Actions = [mkdir] }, writerA, writerB,
                               CancellationToken.None))
                Require(outcome.ProviderNameRefused, "setup: actual folder creation was refused");
        }
        provider.AddInventory(pair.B, new ProviderHeldEvidence(b));
        var plan = await run.PreviewFirstSyncAsync("p1", new RecordingProgress(), CancellationToken.None);
        Require(plan.HeldDeletions.Any(held => held.Path == "folder"),
            "setup: exact cloud tombstone holds the local parent folder");
        var reviewed = plan.HeldDeletions.Select(held => FolderDelete.VictimKey(held.Local, held.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = await run.ApplyAsync("p1", plan, new RecordingProgress(), CancellationToken.None, reviewed);
        Require(store.Live("ra").Single(item => item.Path == refusedPath).Flags.HasFlag(ItemFlags.ProviderNameRefused),
            "setup: held descendant received a persisted provider refusal");
        if (inherited)
            Require(!store.Live("ra").Single(item => item.Path == child.Path).Flags.HasFlag(ItemFlags.ProviderNameRefused),
                "held child inherits refusal only from its folder, without a synthetic flag");
        Require(await a.ObserveAsync("folder", CancellationToken.None) == folder &&
            await a.ObserveAsync(child.Path, CancellationToken.None) == child && writerA.Effects.Count == 0,
            "held parent with refused non-disposable content is never considered empty or deleted");
        Require(writerB.Effects.Count(effect => effect.Path == refusedPath && effect.Kind == (inherited ? "mkdir" : "put")) == 1 &&
            (!inherited || writerB.Effects.All(effect => effect.Path != child.Path)),
            "held descendant refusal is attempted once; inherited children are never attempted or retried");
        Require(await b.ObserveAsync("folder", CancellationToken.None) is not null &&
            store.FirstSyncDone("p1") && result.Deferred == 0,
            "nonempty held folder syncs normally and finishes despite settled refused child");
    }

    private sealed class ProviderHeldEvidence(FakeEndpoint inner) : IInventorySource, IDeferredDeletionEvidence
    {
        public bool IsAtOrUnderExactTombstone(string path) => PairPath.Same(path, "folder");
        public IReadOnlyList<string> FolderDeletionCandidates(IReadOnlyList<string> paths) =>
            paths.Where(IsAtOrUnderExactTombstone).ToArray();
        public Task<DeletionEvidencePreparation> PrepareDeletionEvidenceAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            Task.FromResult(new DeletionEvidencePreparation(new Dictionary<string, SyncRemoteDeletionEvidence>(), paths));
        public Task ResolveDeletionEvidenceAsync(IReadOnlyList<string> paths,
            Action<string, SyncRemoteDeletionEvidence> resolved, CancellationToken ct)
        {
            foreach (var path in paths) resolved(path, new SyncRemoteDeletionEvidence(null, "never existed"));
            return Task.CompletedTask;
        }
        public Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> progress, CancellationToken ct) =>
            inner.FullAsync(gen, include, progress, ct);
        public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) =>
            inner.ChangesSinceAsync(cursor, gen, include, ct);
        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            inner.HashAsync(paths, ct);
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            inner.DeletionTimesAsync(paths, ct);
    }
}
