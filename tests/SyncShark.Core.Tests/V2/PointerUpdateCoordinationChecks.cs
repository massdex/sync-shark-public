namespace Massdex.SyncShark.V2;

internal static class PointerUpdateCoordinationChecks
{
    // Minimal neutral helpers so this check compiles in the standalone Files-on-Demand test project
    // without pulling the larger local/executor suites.
    private const string InstallationId = "localv2install0001";

    private static RootDef RootOf(string root, string rootId) =>
        new(rootId, SyncProviders.Local, null, root, ObjectIdOf(root));

    private static string ObjectIdOf(string root)
    {
        using var lease = PilotDirectoryLease.Open(root);
        return SyncLocalEndpointStore.ObjectId(lease.Identity.VolumeSerialNumber, lease.Identity.FileId);
    }

    private static SyncTransferResourceController Admission() =>
        new(new SyncTransferSettings(ResourceMode: "Performance"), TimeProvider.System,
            () => new SyncResourcePressure(0, 0.1, 8L << 30));

    internal static async Task RunAsync(string root)
    {
        using var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = new PairDef("pair", "Pair A",
            new("local", SyncProviders.Local, null, root, "root-object"),
            new("remote", SyncProviders.Dropbox, "account", "/Remote", "remote-root"), [], [], true);
        store.SavePair(pair);
        store.EnableStorage();
        // Database ownership record only: this test never registers or connects a real sync root.
        store.RecordStorageRegistration(pair.A.RootId, root, Guid.NewGuid().ToString("D"));
        var local = new ItemState("file.txt", ItemKind.File, "file-object", null, new string('a', 64),
            4, DateTimeOffset.UnixEpoch, ItemFlags.OwnedPointer);
        var oldRemote = local with { ObjectId = "id:file", VersionId = "rev-1", Flags = ItemFlags.None };
        var remote = oldRemote with { VersionId = "rev-2", Hash = new string('b', 64), Size = 8 };
        store.WriteSnapshot(new Snapshot(pair.A.RootId, Side.A, store.BeginGeneration(pair.A.RootId, "snapshot"), "local", [local], []));
        void IndexRemote(ItemState value) => store.WriteSnapshot(new Snapshot(pair.B.RootId, Side.B,
            store.BeginGeneration(pair.B.RootId, "snapshot"), value.VersionId!, [value], []));
        IndexRemote(oldRemote);
        var seed = new SyncAction("seed", 1, ActionKind.CopyNew, Side.A, local.Path, null, oldRemote, local, false, "object");
        store.RecordIntent(pair.PairId, seed);
        store.CommitResult(pair.PairId, 1, seed, new(seed.ActionId, Outcome.Done, local, oldRemote, null, null));
        var action = new SyncAction("update", 2, ActionKind.Update, Side.A, local.Path, null, remote, local, false, "object");
        IndexRemote(remote);
        var writer = new UnavailablePointerWriter();
        using var gate = new SemaphoreSlim(1, 1);
        var committer = new StoreCommitter(store, gate, pair.PairId, 2);
        foreach (var cause in new[] { "host", "binding" })
        {
            writer.Cause = cause;
            var runner = new ActionRunner(committer, V2Settings.Default, Admission(), pair.PairId,
                writer, writer, [], new(), isStateDamage: StorageFailureClassifier.IsStateDamage);
            var result = await runner.RunAsync(action, CancellationToken.None);
            Require(result?.Outcome == Outcome.Skipped, "pair failure is deferred, not an item failure");
            Require(store.UnresolvedIntents(pair.PairId).Count == 0, "no unperformed effect remains for replay");
            Require(store.SyncRetryAt(pair.PairId, action.Path)?.ReadRoots?.Count == 2,
                "durable targeted retry retains both roots for next run");
            Require(store.BaselinesAt(pair.PairId, Side.A, [local.Path]).Single().B.VersionId == "rev-1",
                "deferred metadata leaves baseline unsynced");
        }
        Require(store.OpenNotices(pair.PairId).Count == 1 && store.OpenNotices(pair.PairId)[0].Path == "",
            "multiple blocked updates produce one pair notice");
        Require(writer.ByteCalls == 0, "blocked updates never download, read, replace or archive bytes");
        using (var reopened = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash))
            Require(reopened.SyncRetryAt(pair.PairId, action.Path)?.ReadRoots?.Count == 2 &&
                reopened.BaselinesAt(pair.PairId, Side.A, [local.Path]).Single().B.VersionId == "rev-1",
                "deferred retry and unsynced baseline survive a new store connection");
        if (OperatingSystem.IsWindows())
        {
            var localPath = Path.Combine(root, "direct-local");
            Directory.CreateDirectory(localPath);
            var recoveryPath = Path.Combine(root, "recovery");
            Directory.CreateDirectory(recoveryPath);
            var directRoot = RootOf(localPath, "direct-local");
            var directPair = pair with { PairId = "direct", A = directRoot };
            store.SavePair(directPair);
            store.RecordStorageRegistration(directRoot.RootId, localPath, Guid.NewGuid().ToString("D"));
            var direct = await ProductionEndpoints.OpenLocalWriterAsync(store, directRoot, Side.A, directPair,
                InstallationId, recoveryPath, CancellationToken.None, V2Settings.Default);
            foreach (var cause in new[] { "host", "binding" })
            {
                direct.PointerUpdateProvider = cause == "host" ? null :
                    _ => throw new StoragePairException("binding", "Foreign or overlapping registration.");
                var deferred = false;
                try { await ((IOwnedPointerUpdateTarget)direct).UpdateOwnedPointerAsync(action, writer, CancellationToken.None); }
                catch (OwnedPointerDeferredException error) { deferred = error.Cause.Cause == cause; }
                Require(deferred, "real local writer defers unavailable pair before source observation or native reads");
            }
            // LOW-4 real path: the provider recheck can be wrapped by the native callback. The actual
            // LocalWriter catch must walk that chain and defer, not surface an item failure.
            foreach (var wrap in new Func<Exception, Exception>[]
            {
                inner => new IOException("native callback failed", inner),
                inner => new AggregateException("native callback failed", new InvalidOperationException("worker"), inner)
            })
            {
                direct.PointerUpdateProvider = _ => throw wrap(new StoragePairException("binding",
                    "Foreign or overlapping registration."));
                var deferred = false;
                try { await ((IOwnedPointerUpdateTarget)direct).UpdateOwnedPointerAsync(action, writer, CancellationToken.None); }
                catch (OwnedPointerDeferredException error) { deferred = error.Cause.Cause == "binding"; }
                Require(deferred, "the real writer catch unwraps a wrapped callback pair cause into a deferral");
            }
            // Through ActionRunner the same wrapped cause is one pair notice and a Skipped result,
            // never an item failure with its own notice.
            direct.PointerUpdateProvider = _ => throw new IOException("native callback failed",
                new StoragePairException("binding", "Foreign or overlapping registration."));
            var directCommitter = new StoreCommitter(store, gate, directPair.PairId, 1);
            var directRunner = new ActionRunner(directCommitter, V2Settings.Default, Admission(), directPair.PairId,
                direct, writer, [], new(), isStateDamage: StorageFailureClassifier.IsStateDamage);
            var deferredRun = await directRunner.RunAsync(action, CancellationToken.None);
            Require(deferredRun?.Outcome == Outcome.Skipped &&
                store.OpenNotices(directPair.PairId).Count == 1 &&
                store.OpenNotices(directPair.PairId)[0].Storage is { Operation: "availability", ObjectId: "", Scope: "binding" },
                "a wrapped callback pair cause is a skipped pair notice, not an item failure");
            Require(store.SyncRetryAt(directPair.PairId, action.Path)?.ReadRoots?.Count == 2,
                "a wrapped callback pair cause keeps a durable targeted retry");
        }

        // Exercise success notification independently of receipt publication below: only Done
        // is positive evidence, and the per-apply failure latch must win either completion order.
        Task<ActionResult?> SuccessfulEffect() => Task.FromResult<ActionResult?>(
            new(action.ActionId, Outcome.Done, local, remote, null, null));
        await committer.RunPointerUpdateAsync(action, SuccessfulEffect, CancellationToken.None);
        Require(store.OpenNotices(pair.PairId).Count == 1, "same-apply success cannot hide an earlier blocked pointer");
        var nextApply = new StoreCommitter(store, gate, pair.PairId, 3);
        await nextApply.RunPointerUpdateAsync(action, SuccessfulEffect, CancellationToken.None);
        Require(store.OpenNotices(pair.PairId).Count == 1 &&
            store.OpenNotices(pair.PairId)[0].Storage?.Scope == "host",
            "pointer success keeps a non-connectivity (host/binding) pair cause");
        // Positive connectivity evidence closes a network/auth/provider cause and clears its root backoff.
        StorageNotices.CompletePair(store, pair, "pointer");
        foreach (var cause in new[] { "network", "auth", "provider" })
        {
            StorageNotices.Report(store, pair, string.Empty,
                new StorageNotice(pair.A.RootId, string.Empty, pair.B.RootId, "pointer"),
                new StoragePairException(cause, "Connectivity failure."));
            store.SetStorageRootBackoff(pair.A.RootId, TimeSpan.FromMinutes(1));
            await nextApply.RunPointerUpdateAsync(action, SuccessfulEffect, CancellationToken.None);
            Require(store.OpenNotices(pair.PairId).Count == 0 && !store.StorageRootBackedOff(pair.A.RootId),
                "pointer success closes a " + cause + " pair cause and clears its root backoff");
        }
        // Coexistence, volume and unknown causes are not connectivity evidence: they survive with their backoff.
        foreach (var cause in new[] { "coexistence", "volume", "unknown" })
        {
            StorageNotices.Report(store, pair, string.Empty,
                new StorageNotice(pair.A.RootId, string.Empty, pair.B.RootId, "pointer"),
                new StoragePairException(cause, "Still blocked."));
            store.SetStorageRootBackoff(pair.A.RootId, TimeSpan.FromMinutes(1));
            await nextApply.RunPointerUpdateAsync(action, SuccessfulEffect, CancellationToken.None);
            Require(store.OpenNotices(pair.PairId).Count == 1 &&
                store.OpenNotices(pair.PairId)[0].Storage?.Scope == cause &&
                store.StorageRootBackedOff(pair.A.RootId),
                "pointer success preserves a " + cause + " pair cause and its backoff");
            StorageNotices.CompletePair(store, pair, "pointer");
            store.ClearStorageRootBackoff(pair.A.RootId);
        }
        await nextApply.DeferPointerUpdateAsync(action, new StoragePairException("binding", "Registration changed."));
        Require(store.OpenNotices(pair.PairId).Count == 1, "later failure remains visible after an earlier success");

        // LOW-4: the provider recheck throws inside the native update callback. If the provider wraps that
        // exception (directly, in an inner chain or as an aggregate), the deferral must still find the pair cause.
        Require(OwnedPointerUpdates.PairCause(new IOException("callback",
                new StoragePairException("binding", "Foreign registration.")))?.Cause == "binding",
            "a pair cause wrapped in the native callback chain is still recognised");
        Require(OwnedPointerUpdates.PairCause(new AggregateException(
                new InvalidOperationException("worker"),
                new IOException("callback", new StoragePairException("host", "No provider."))))?.Cause == "host",
            "a pair cause inside an aggregated native callback failure is still recognised");
        Require(OwnedPointerUpdates.PairCause(new ExpectedTargetMismatchException("changed")) is null,
            "an item target mismatch is not a pair cause");

        Require(!((IStoragePointerCommit)store).StoragePointerUpdateIsStale(pair, action), "original stored revision authorizes update");
        var receipt = store.StoragePointerFor(pair.A.RootId, local.ObjectId, pair.B.RootId)!;
        var next = DropboxItems.ToEntry(remote);
        store.CompleteStoragePointerUpdate(receipt, next);
        Require(((IStoragePointerCommit)store).StoragePointerUpdateIsStale(pair, action), "same applied revision is deduplicated");
        store.CompleteStoragePointerUpdate(receipt, receipt.Remote with { Revision = "obsolete" });
        Require(store.StoragePointerFor(pair.A.RootId, local.ObjectId, pair.B.RootId)!.Remote.Revision == "rev-2",
            "stale completion cannot overwrite stored revision");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = FilesOnDemandSettlement.SerializeAsync(store, pair.A.RootId, local.ObjectId, async () =>
        { entered.SetResult(); await release.Task; }, CancellationToken.None);
        await entered.Task;
        var effects = 0;
        var run = committer.RunPointerUpdateAsync(action, () =>
        { effects++; return Task.FromResult<ActionResult?>(null); }, CancellationToken.None);
        Require(!run.IsCompleted, "run and refresh share entity gate");
        release.SetResult();
        await Task.WhenAll(refresh, run);
        Require(effects == 0 && (await run)?.Outcome == Outcome.Skipped &&
            store.SyncRetryAt(pair.PairId, action.Path) is not null,
            "run rechecks receipt after waiting, avoids duplicate effects and retains targeted replan");
        // Run-first: a durable run result consumes only the dirty revision it actually published.
        var updated = local with { Hash = remote.Hash, Size = remote.Size };
        store.RecordIntent(pair.PairId, action);
        store.CommitResult(pair.PairId, 2, action, new(action.ActionId, Outcome.Done, updated, remote, null, null));
        Require(store.ChangedStoragePointers().Count == 0, "run-first leaves no refresh API/native work");
        var newer = remote with { VersionId = "rev-3", Hash = new string('c', 64) };
        IndexRemote(newer);
        Require(store.ChangedStoragePointers().Count == 1, "new remote revision queues entity refresh");
        // Simulate a revision arriving between native update and its result commit.
        store.RecordIntent(pair.PairId, action);
        store.CommitResult(pair.PairId, 2, action, new(action.ActionId, Outcome.Done, updated, remote, null, null));
        Require(store.ByObject(pair.B.RootId, remote.ObjectId)?.VersionId == "rev-3" &&
            store.ChangedStoragePointers().Count == 1, "older run receipt preserves newer indexed revision and dirty work");
        Require(((IStoragePointerCommit)store).StoragePointerUpdateIsStale(pair, action), "indexed newer revision rejects stale run plan");
        Console.WriteLine("PASS pointer pair deferral, durable retries and shared revision CAS");
    }

    private sealed class UnavailablePointerWriter : IEndpointWriter, IOwnedPointerUpdateTarget
    {
        internal string Cause = "host";
        internal int ByteCalls;
        public Task<ItemState> UpdateOwnedPointerAsync(SyncAction action, IEndpointWriter source, CancellationToken ct) =>
            throw new OwnedPointerDeferredException(new StoragePairException(Cause, "Cloud Files unavailable. Start the host or review the registration and Retry."));
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => throw new InvalidOperationException("No observation expected.");
        public Task<Stream> OpenReadAsync(ItemState item, CancellationToken ct) { ByteCalls++; throw new InvalidOperationException(); }
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct) { ByteCalls++; throw new InvalidOperationException(); }
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) { ByteCalls++; throw new InvalidOperationException(); }
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
