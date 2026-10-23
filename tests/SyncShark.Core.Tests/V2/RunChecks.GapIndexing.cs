namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // These fixtures simulate journal identity/cursors, not content. Keep native file identity,
    // metadata and full content hashing, translating only the checkpoint into the fixture journal.
    private sealed class GapRecoveryIndexIo(LocalGapIo native, Func<NtfsUsnCheckpoint> checkpoint) : ILocalGapIo
    {
        public bool Paused => native.Paused;
        public bool Excluded(RootDef root, ItemState item) => native.Excluded(root, item);
        public LocalGapProof? Probe(RootDef root, ItemState item) => native.Probe(root, item) is { } proof
            ? proof with { JournalCursor = checkpoint().Cursor } : null;
        public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct) =>
            native.HashAsync(root, item, ct);
    }

    private static async Task IndexRecoveredGapAsync(SqliteStateStore store, RootDef root,
        LocalInventory inventory, Func<NtfsUsnCheckpoint> checkpoint)
    {
        var native = new LocalGapIo(store, Settings with { LocalGapBytesPerSecond = 64 * 1024 * 1024 },
            new HashSet<string>(), () => false) { CheckpointForCheck = _ => checkpoint() };
        var io = new GapRecoveryIndexIo(native, checkpoint);
        inventory.GapIo = io;
        await using var worker = new LocalGapWorker((ILocalGapStore)store, io);
        await using var index = new LocalIndexingRuns(store, (_, ct) => inventory.VerifyIndexingPassAsync(ct),
            worker, io, _ => checkpoint());
        // One bounded manual pass may discover after its hash lane captured its page. Another
        // independent pass consumes that durable frontier; sync never performs either step.
        for (var pass = 0; pass < 16; pass++)
        {
            var outcome = await index.RunAsync(root, default);
            if (outcome == "complete") return;
            Require(outcome == "pending", "independent fixture indexing remains eligible: " + outcome);
        }
        throw new InvalidOperationException("Independent gap indexing did not discharge the fixture's retained debt: " +
            ((ILocalGapStore)store).GapProgress(root.RootId));
    }
}
