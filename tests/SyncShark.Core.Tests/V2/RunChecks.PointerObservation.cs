namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task PointerObservationRetryAsync(string folder)
    {
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var item = fx.A.Seed("pointer.txt", "same bytes");
        fx.B.Seed(item.Path, "same bytes");
        await FirstSyncAsync(fx.Run(), fx);
        var before = store.ByObject(fx.Pair.A.RootId, item.ObjectId)! with { Flags = ItemFlags.OwnedPointer };
        MappingSnapshot(store, fx.Pair.A, Side.A, before);
        var inventory = new UnobservedPointerInventory(fx.A, item.Path);
        fx.Provider.AddInventory(fx.Pair.A, inventory);
        var writesA = fx.WriterA.Calls;
        var writesB = fx.WriterB.Calls;
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var retry = store.SyncRetryAt(fx.Pair.PairId, item.Path);
        Require(retry is not null && retry.LastError.StartsWith(OwnedPointerProof.ObservationMessage, StringComparison.Ordinal),
            "unobserved native metadata creates the existing durable observation retry");
        var notice = store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == "sync-retry" && n.Path == item.Path);
        Require(store.ByObject(fx.Pair.A.RootId, item.ObjectId) == before,
            "incomplete native observation preserves the exact durable entity rather than inferring absence");
        // Force the existing retry due without a sleep, then execute the real subsequent run.
        store.RequestOwnedPointerObservationRetriesNow(fx.Pair.PairId, OwnedPointerProof.ObservationMessage);
        var readsBefore = inventory.MetadataReads;
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(inventory.MetadataReads > readsBefore, "the next run re-observes due metadata instead of stranding it");
        var repeated = store.OpenNotices(fx.Pair.PairId).Where(n => n.ReasonKey == "sync-retry" && n.Path == item.Path).ToArray();
        Require(repeated.Length == 1 && repeated[0].NoticeId == notice.NoticeId,
            "repeated unobserved metadata keeps one deduplicated actionable notice");
        Require(inventory.HashCalls == 0 && fx.WriterA.Calls == writesA && fx.WriterB.Calls == writesB,
            "unobserved metadata cannot hash, upload or delete on either side");
        Require(!store.OpenNotices(fx.Pair.PairId).Any(n => n.Storage?.Operation == "pointer-verify"),
            "observation retry is not false pointer verification debt");
    }

    private sealed class UnobservedPointerInventory(FakeEndpoint inner, string path) : IInventorySource
    {
        internal int MetadataReads { get; private set; }
        internal int HashCalls { get; private set; }
        public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> progress, CancellationToken ct)
        {
            MetadataReads++;
            var snapshot = await inner.FullAsync(gen, include, progress, ct);
            return snapshot with { Items = snapshot.Items.Where(item => item.Path != path).ToArray(),
                IncompleteSubtrees = [path], IncompleteReasons = new Dictionary<string, string> { [path] = OwnedPointerProof.ObservationMessage },
                IncludesPath = candidate => candidate != path && include(candidate) };
        }
        public async Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct)
        {
            MetadataReads++;
            var delta = await inner.ChangesSinceAsync(cursor, gen, include, ct);
            return delta with { Changes = delta.Changes.Where(change => change.Path != path).ToArray(),
                IncompleteSubtrees = [path], IncompleteReasons = new Dictionary<string, string> { [path] = OwnedPointerProof.ObservationMessage } };
        }
        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct)
        {
            HashCalls++;
            throw new IOException("Unobserved native metadata must never authorize content hashing.");
        }
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            inner.DeletionTimesAsync(paths, ct);
    }
}
