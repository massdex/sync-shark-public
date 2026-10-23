namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task OwnedPointerUpdates_NoDownloadAsync()
    {
        foreach (var move in new[] { false, true })
        {
            var (store, a, b, executor) = Rig();
            using var ownedStore = store;
            var local = a.Seed("old.txt", "old", versionId: "v1") with { Flags = ItemFlags.OwnedPointer };
            var oldRemote = b.Seed("old.txt", "old", versionId: "r1");
            var remote = b.Seed(move ? "new.txt" : "old.txt", "new content", versionId: "r2")
                with { ObjectId = oldRemote.ObjectId };
            SeedBaseline(store, "pointer", local, oldRemote);
            var target = new PointerMetadataWriter(local, () => store.UnresolvedIntents("p1").Count > 0);
            var source = new PointerMetadataWriter(remote, () => true);
            var options = new PlanOptions(TimeSpan.FromSeconds(2), false, new LabelRule("{name} ({side}){ext}", "yyyyMMdd", TimeSpan.Zero, "A", "B"), 10)
                { ProviderA = SyncProviders.Local, ProviderB = SyncProviders.Dropbox };
            var plan = Planner.Decide("p1", 2,
                [new PlanRow(new Baseline("p1", "pointer", local, oldRemote, 1), local, remote,
                    new Evidence(false, false, false, false))], options);
            Require(!plan.Notices.Any(n => n.ReasonKey == "sync-retry"), "pointer edit has no false planning hold");
            var results = await Apply(executor, plan, target, source);
            Require(results.Count == (move ? 2 : 1) && results.All(r => r.Outcome == Outcome.Done),
                "pointer edit and independent move converge through executor");
            Require(target.Updates == 1 && target.Moves == (move ? 1 : 0) &&
                target.Current.Path == remote.Path && target.Current.Hash == remote.Hash &&
                target.Current.ObjectId == local.ObjectId && target.Current.Flags == ItemFlags.OwnedPointer,
                "metadata update retains local entity identity and online-only state");
            Require(source.Reads == 0 && target.Reads == 0 && target.Puts == 0 && target.Preserves == 0 &&
                store.UnresolvedIntents("p1").Count == 0,
                "pointer effects consume ordinary intents with no byte read, download, or archive");
        }
    }

    private sealed class PointerMetadataWriter(ItemState initial, Func<bool> hasIntent)
        : IEndpointWriter, IOwnedPointerUpdateTarget, ISyncDirectDownloadSource
    {
        internal ItemState Current = initial;
        internal int Updates, Moves, Reads, Puts, Preserves;
        public async Task<ItemState> UpdateOwnedPointerAsync(SyncAction action, IEndpointWriter source, CancellationToken ct)
        {
            Require(hasIntent(), "metadata-only effect follows durable intent");
            Require(Current.Path == action.Path && Current.ObjectId == action.ExpectedTarget!.ObjectId &&
                Current.Hash == action.ExpectedTarget.Hash, "move completes before pointer metadata update");
            var remote = await source.ObserveAsync(action.Source!.Path, ct)
                ?? throw new InvalidOperationException("The planned remote revision is missing.");
            Require(remote == action.Source, "metadata source is exactly the planned revision");
            Updates++;
            return Current = Current with { Hash = remote.Hash, Size = remote.Size, Mtime = remote.Mtime };
        }
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct)
        {
            Require(hasIntent() && Current.Path == action.FromPath, "pointer move follows intent at exact source");
            Moves++;
            return Task.FromResult(Current = Current with { Path = action.Path });
        }
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) =>
            Task.FromResult<ItemState?>(PairPath.Same(path, Current.Path) ? Current : null);
        public Task<Stream> OpenReadAsync(ItemState item, CancellationToken ct)
        { Reads++; throw new InvalidOperationException("Pointer update must not open content."); }
        public Task<SyncDownloadResult> DownloadToAsync(ItemState item, Stream destination, CancellationToken ct)
        { Reads++; throw new InvalidOperationException("Pointer update must not download content."); }
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct)
        { Puts++; throw new InvalidOperationException("Pointer update must not replace content."); }
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct)
        { Preserves++; throw new InvalidOperationException("Pointer update must not archive content."); }
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    }
}
