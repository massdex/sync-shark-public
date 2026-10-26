namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task NativeRereadProofAsync(string folder)
    {
        var directory = Path.Combine(folder, "reread-root");
        LocalChecks.Seed(directory, "file.txt", "original");
        LocalChecks.Seed(directory, "other.txt", "original");
        var root = LocalChecks.RootOf(directory, "reread-root");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "reread-store"),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var checkpoint = new NtfsUsnCheckpoint(1, 0, 300, 0);
        var inventory = RestartInventory(store, root, checkpoint);
        var gen = store.BeginGeneration(root.RootId, "full");
        store.WriteSnapshot(await inventory.FullAsync(gen, _ => true, null!, default));
        store.CompleteGeneration(gen, checkpoint.Cursor, true);
        await inventory.HashAsync(["other.txt"], default);
        store.AcknowledgeRehash(root.RootId, store.RehashWatermark(root.RootId), path => path == "other.txt");
        var item = store.Live(root.RootId).Single(row => row.Path == "file.txt");
        store.RequireRehash(root.RootId, item, "test proof");
        LocalChecks.Require((await inventory.HashAsync([item.Path], default)).ContainsKey(item.Path),
            "native preproof succeeds");
        var through = store.RehashWatermark(root.RootId);
        LocalChecks.Require((await inventory.HashAsync([item.Path], default)).ContainsKey(item.Path) &&
            store.RehashWatermark(root.RootId) == through,
            "executor-style unchanged reread preserves the captured planning order");
        store.AcknowledgeRehash(root.RootId, through, _ => true);
        LocalChecks.Require(store.RehashPendingCount(root.RootId) == 0 &&
            (await inventory.HashAsync([item.Path], default)).ContainsKey(item.Path) &&
            store.RehashPendingCount(root.RootId) == 0,
            "acknowledgement empties the queue and settled rereads do not recreate debt");
        await VerifyScopedProofWatermarkAsync(store, root, inventory);
        await VerifyConcurrentRereadAsync(store, root, inventory);
        await VerifyChangedAndFailedRereadAsync(store, root, inventory);
    }

    private static async Task VerifyScopedProofWatermarkAsync(SqliteStateStore store, RootDef root, LocalInventory inventory)
    {
        var item = store.Live(root.RootId).Single(row => row.Path == "file.txt");
        var other = store.Live(root.RootId).Single(row => row.Path == "other.txt");
        await inventory.HashAsync([other.Path], default);
        store.AcknowledgeRehash(root.RootId, store.RehashWatermark(root.RootId), _ => true);
        var captured = store.RehashWatermark(root.RootId);
        store.RequireRehash(root.RootId, item, "test inline proof");
        store.RequireRehash(root.RootId, other, "test invalidation after capture");
        var proof = await inventory.HashAsync([item.Path], default);
        var through = Math.Max(captured, store.RehashProofOrder(root.RootId, item.ObjectId, proof[item.Path]));
        store.AcknowledgeRehash(root.RootId, through, _ => true);
        LocalChecks.Require(store.RehashPendingCount(root.RootId) == 1 &&
            store.ByObject(root.RootId, other.ObjectId)!.Flags.HasFlag(ItemFlags.NeedsRehash) &&
            store.RehashProofOrder(root.RootId, item.ObjectId, proof[item.Path]) == 0,
            "scoped inline watermark acknowledges its proof but not another row invalidated after capture");
        await inventory.HashAsync([other.Path], default);
        store.AcknowledgeRehash(root.RootId, store.RehashWatermark(root.RootId), _ => true);
    }

    private static async Task VerifyConcurrentRereadAsync(SqliteStateStore store, RootDef root, LocalInventory inventory)
    {
        var item = store.Live(root.RootId).Single(row => row.Path == "file.txt");
        var path = Path.Combine(root.RootPath, item.Path);
        var native = inventory.GapIo!;
        LocalChecks.Seed(root.RootPath, item.Path, "first-ed");
        File.SetLastWriteTimeUtc(path, item.Mtime.UtcDateTime);
        long newer = 0;
        inventory.GapIo = new VerificationTestIo(async (r, row, ct) =>
        {
            var proof = await native.HashAsync(r, row, ct);
            LocalChecks.Seed(root.RootPath, item.Path, "nextedit");
            File.SetLastWriteTimeUtc(path, item.Mtime.UtcDateTime);
            store.RequireRehash(root.RootId, item, "second metadata-preserving journal edit");
            newer = store.RehashRevision(root.RootId, item.ObjectId);
            return proof;
        });
        try
        {
            var hashes = await inventory.HashAsync([item.Path], default);
            LocalChecks.Require(!hashes.ContainsKey(item.Path) && newer > 0 &&
                store.RehashRevision(root.RootId, item.ObjectId) == newer &&
                store.ByObject(root.RootId, item.ObjectId) is { Hash: null } pending && pending.Flags.HasFlag(ItemFlags.NeedsRehash),
                "two quick metadata-identical edits retain the newer evidence instead of publishing the first edit's stale proof");
        }
        finally { inventory.GapIo = native; }
        var fresh = await inventory.HashAsync([item.Path], default);
        LocalChecks.Require(fresh[item.Path] == DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("nextedit")),
            "the subsequent proof reads the second edit");
    }

    private static async Task VerifyChangedAndFailedRereadAsync(SqliteStateStore store, RootDef root, LocalInventory inventory)
    {
        var item = store.Live(root.RootId).Single(row => row.Path == "file.txt");
        store.RequireRehash(root.RootId, item, "test next proof");
        await inventory.HashAsync([item.Path], default);
        var through = store.RehashWatermark(root.RootId);
        LocalChecks.Seed(root.RootPath, item.Path, "modified");
        var path = Path.Combine(root.RootPath, item.Path);
        File.SetLastWriteTimeUtc(path, item.Mtime.UtcDateTime);
        var hashes = await inventory.HashAsync([item.Path], default);
        LocalChecks.Require(hashes.TryGetValue(item.Path, out var hash) &&
            hash == DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("modified")) &&
            store.RehashWatermark(root.RootId) > through,
            "changed same-metadata native reread advances the planning order");
        store.AcknowledgeRehash(root.RootId, through, _ => true);
        LocalChecks.Require(store.RehashPendingCount(root.RootId) == 1,
            "stale acknowledgement cannot retire the later content proof");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        hashes = await inventory.HashAsync([item.Path], default);
        LocalChecks.Require(!hashes.ContainsKey(item.Path) &&
            store.ByObject(root.RootId, item.ObjectId)!.Flags.HasFlag(ItemFlags.NeedsRehash),
            "failed native reread durably reflags the previously proven row");
    }
}
