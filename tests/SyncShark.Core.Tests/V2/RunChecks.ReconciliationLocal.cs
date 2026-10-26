namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static Task ReconciliationSelectsByUsn() => ReconciliationLocalSelection(
        Path.Combine(Path.GetTempPath(), "reconcile-local-" + Guid.NewGuid().ToString("N")), discovery: true);

    internal static async Task ReconciliationLocalSelection(string folder, bool discovery)
    {
        var directory = Path.Combine(folder, "root");
        LocalChecks.Seed(directory, "unchanged.txt", "same");
        LocalChecks.Seed(directory, "changed.txt", "same");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var root = LocalChecks.RootOf(directory, "ra");
        var pair = fx.Pair with { A = root };
        store.SavePair(pair);
        var checkpoint = new NtfsUsnCheckpoint(1, 0, 200, 0);
        var probes = new SelectionProbe(checkpoint.Cursor);
        var local = new LocalInventory(root, Side.A, store, new HashSet<string>(), Settings, TimeProvider.System,
            StorageFailureClassifier.IsStateDamage, pairId: pair.PairId)
        { WalkCheckpointForTest = () => (checkpoint, false), DiscoveryReadForTest = (_, _) => Task.FromResult<SyncDiscoveryResult?>(null), GapIo = probes };
        var items = new List<ItemState>();
        await local.EnumerateMetadataAsync(rows => { items.AddRange(rows.Select(row => row.Item)); return Task.CompletedTask; }, default);
        SelectionSnapshot(store, root, Side.A, items.ToArray());
        foreach (var item in items) SelectionBaseline(store, pair, item, item with { ObjectId = "other-" + item.ObjectId });
        if (discovery) local.DiscoveryReadForTest = (roots, _) => Task.FromResult<SyncDiscoveryResult?>(new(
            [new(roots[0], items.ToDictionary(item => item.Path, item => new SyncDiscoveryItem(item.Path,
                LocalItems.ParseObjectId(item.ObjectId).FileId, roots[0].FileId,
                item.Path == "changed.txt" ? 150 : 50, FileAttributes.Normal, 1, item.Size, item.Mtime.UtcDateTime)))], checkpoint, checkpoint));
        fx.Provider.AddInventory(root, local);
        var counts = await fx.Run().SelectAsync(pair, Side.A,
            new(null, ReconciliationScope.Root, ReconciliationTrigger.GapSameJournal, new NtfsUsnCheckpoint(1, 0, 100, 0).Cursor),
            "local-selection", new ProgressThrottle(new RecordingProgress(), TimeProvider.System, TimeSpan.Zero), default);
        Require(counts.Changed == 1 && counts.Unaffected == 1 &&
            store.ReconcilePage("local-selection:work", Side.A, "", "", 256).Single().Path == "changed.txt",
            "same-journal selection detects changed USN despite equal size and mtime");
        Require(probes.Calls == (discovery ? 0 : 2), discovery ?
            "Discovery USNs avoid every per-file probe and hash" : "walk fallback probes equal-metadata files without hashing");
    }

    private sealed class SelectionProbe(string cursor) : ILocalGapIo
    {
        internal int Calls { get; private set; }
        public bool Paused => false;
        public LocalGapProof? Probe(RootDef root, ItemState item)
        {
            Calls++;
            var identity = LocalItems.ParseObjectId(item.ObjectId);
            return new(cursor, new(identity.Volume, identity.FileId, item.Path == "changed.txt" ? 150 : 50,
                item.Size, item.Mtime.UtcDateTime.ToFileTimeUtc()));
        }
        public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct) =>
            throw new InvalidOperationException("Selection must never hash file content.");
    }
}
