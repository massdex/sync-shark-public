namespace Massdex.SyncShark.V2;

internal static partial class LocalJournalChecks
{
    private static async Task NativeProofRestartAsync(string folder)
    {
        var directory = Path.Combine(folder, "root");
        LocalChecks.Seed(directory, "proven.txt", "content");
        LocalChecks.Seed(directory, "pending.txt", "content");
        var root = LocalChecks.RootOf(directory, "proof-restart-root");
        var product = Path.Combine(folder, "store");
        var checkpoint = new NtfsUsnCheckpoint(1, 0, 300, 0);
        ItemState pending;
        LocalGapEpoch epoch;
        using (var store = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var inventory = RestartInventory(store, root, checkpoint);
            var gen = store.BeginGeneration(root.RootId, "full");
            store.WriteSnapshot(await inventory.FullAsync(gen, _ => true, null!, default));
            store.CompleteGeneration(gen, checkpoint.Cursor, true);
            var gaps = (ILocalGapStore)store;
            gaps.BeginGap(root.RootId, checkpoint.Cursor, 0, false, protect: false);
            epoch = gaps.GapEpoch(root.RootId)!;
            while (store.InvalidateReconciliationPage(root.RootId, epoch.Epoch, _ => true)) { }
            var proven = store.Live(root.RootId).Single(item => item.Path == "proven.txt");
            var proof = await inventory.GapIo!.HashAsync(root, proven, default);
            LocalChecks.Require(proof.Hash is not null && store.CompleteRehash(root.RootId, proven, proof.Hash),
                "first part of the epoch establishes native content proof before process restart");
            pending = store.Live(root.RootId).Single(item => item.Path == "pending.txt");
        }
        using var reopened = SqliteStateStore.Open(product, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        await RunChecks.CompleteOwnershipReconciliationAsync(reopened, root, RestartInventory(reopened, root, checkpoint),
            pending, epoch, folder, alreadyProven: "proven.txt");
        await VerifyPostProofJournalEditAsync(reopened, root, checkpoint);
        await NativeRereadProofAsync(folder);
    }

    private static LocalInventory RestartInventory(SqliteStateStore store, RootDef root, NtfsUsnCheckpoint checkpoint) =>
        new(root, Side.A, store, new HashSet<string>(), StorageChecks.Fast, TimeProvider.System,
            isStateDamage: StorageFailureClassifier.IsStateDamage)
        {
            WalkCheckpointForTest = () => (checkpoint, false),
            DiscoveryReadForTest = (_, _) => Task.FromResult<SyncDiscoveryResult?>(null),
            GapIo = new LocalGapIo(store, StorageChecks.Fast, new HashSet<string>(), () => false)
                { CheckpointForCheck = _ => checkpoint }
        };
}
