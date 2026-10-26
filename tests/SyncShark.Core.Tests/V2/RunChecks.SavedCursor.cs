namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task SavedCursorReconciliationAsync(SqliteStateStore store, RootDef root,
        LocalInventory inventory, ItemState[] rows, LocalGapEpoch epoch, NtfsUsnCheckpoint through, string folder,
        bool discovery = false)
    {
        var fx = Build(suppliedStore: store);
        var pair = fx.Pair with { A = root };
        store.SavePair(pair);
        store.MarkFirstSyncDone(pair.PairId);
        fx.B.DropboxHashes = true;
        var remote = rows.Select(item => fx.B.Seed(item.Path, "old", mtime: item.Mtime)).ToArray();
        SelectionSnapshot(store, pair.B, Side.B, remote);
        for (var index = 0; index < rows.Length; index++)
        {
            var item = rows[index];
            var adopt = new SyncAction(item.Path, 0, ActionKind.Adopt, Side.B, item.Path, null,
                item, remote[index], false, item.ObjectId);
            store.CommitResult(pair.PairId, 1, adopt, new(adopt.ActionId, Outcome.Done, item, remote[index], null, null));
        }
        LocalChecks.Seed(root.RootPath, rows[1].Path, "new");
        File.SetLastWriteTimeUtc(Path.Combine(root.RootPath, rows[1].Path), rows[1].Mtime.UtcDateTime);
        var io = new CursorBoundaryIo(new LocalGapIo(store, Settings, new HashSet<string>(), () => false)
            { CheckpointForCheck = _ => through }, rows[0].Path, epoch.Boundary, through);
        inventory.GapIo = io;
        inventory.WalkCheckpointForTest = () => (through, false);
        inventory.JournalReadForTest = NoJournalChanges;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inventory.DiscoveryReadForTest = async (roots, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return discovery ? BoundaryDiscovery(roots[0], rows, epoch.Boundary, through) : null;
        };
        fx.Provider.AddInventory(root, inventory);
        fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, pair.PairId,
            LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default, isStateDamage: StorageFailureClassifier.IsStateDamage));
        var executor = new BoundaryPlanCapture(fx.Executor);
        await using var owner = new GapRecoveryRun(new SyncRun(store, executor, fx.Provider, Settings, TimeProvider.System));
        var trace = new BoundaryTraceCapture();
        var previousTrace = RunTrace.Slot.Value;
        Task<RunReport> repair;
        try
        {
            RunTrace.Slot.Value = trace;
            repair = owner.Run.ReconcileScopeAsync(pair.PairId, StateMonitor.FromGap(epoch), new RecordingProgress(), default);
        }
        finally { RunTrace.Slot.Value = previousTrace; }
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(store.GapInventoryPending(root.RootId) && executor.Actions.Count == 0 && fx.WriterB.Calls == 0,
                "open inventory-only epoch holds both rows before reconciliation observation: no plan or effect");
            release.TrySetResult();
            await io.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            CheckBoundaryPending(store, root, rows, epoch, executor, fx.WriterB, io);
            Require(trace.Lines.Any(line => line.StartsWith(
                "reconciliation selection: enumerated=2 changed=1 new=0 missing=0 unaffected=1", StringComparison.Ordinal)),
                "boundary pair: savedNext-1 is not selected; equality is the only selected row");
            io.Release.TrySetResult();
            var report = await repair;
            CheckBoundarySettled(store, root, rows, epoch, io, discovery, report);
            Require(executor.Actions.All(action => action.Path != rows[0].Path),
                "below-boundary row is not selected for any effect");
            Require((await fx.B.ObserveAsync(rows[1].Path, default))?.Hash ==
                DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("new")) && fx.WriterB.Puts == 1,
                "USN equal to next-unprocessed cursor rehashes a same-size same-mtime change and propagates it once");
        }
        finally { release.TrySetResult(); io.Release.TrySetResult(); await repair; }
    }

    private static SyncDiscoveryResult BoundaryDiscovery(SyncDiscoveryRoot root, ItemState[] rows,
        long boundary, NtfsUsnCheckpoint through) => new([new(root, rows.ToDictionary(item => item.Path,
            item => new SyncDiscoveryItem(item.Path, LocalItems.ParseObjectId(item.ObjectId).FileId,
                root.FileId, item == rows[0] ? boundary - 1 : boundary, FileAttributes.Normal, 1,
                item.Size, item.Mtime.UtcDateTime)))], through, through);

    private static void CheckBoundaryPending(SqliteStateStore store, RootDef root, ItemState[] rows,
        LocalGapEpoch epoch, BoundaryPlanCapture executor, CountingWriter writer, CursorBoundaryIo io)
    {
        Require(executor.Actions.Count == 0 && writer.Calls == 0 && store.GapInventoryPending(root.RootId),
            "neither row is planned or applied before equality content proof returns; epoch remains open");
        Require(store.ByObject(root.RootId, rows[1].ObjectId) is { Hash: null } pending &&
            pending.Flags.HasFlag(ItemFlags.NeedsRehash) && store.RehashPendingCount(root.RootId) == 1,
            "equality alone is untrusted and retains precisely one content-proof debt");
        Require(io.ReadsFor(rows[0].Path) == 0 && store.ByObject(root.RootId, rows[0].ObjectId)!.Hash == rows[0].Hash &&
            !store.ByObject(root.RootId, rows[0].ObjectId)!.Flags.HasFlag(ItemFlags.NeedsRehash) &&
            ((ILocalGapStore)store).GapHashPage(root.RootId, epoch, 128).Count == 0,
            "below-boundary row retains its hash without reads or debt; no legacy mass promotion");
    }

    private static void CheckBoundarySettled(SqliteStateStore store, RootDef root, ItemState[] rows,
        LocalGapEpoch epoch, CursorBoundaryIo io, bool discovery, RunReport report)
    {
        // Discovery supplies two USNs directly; fallback uses two probes. Only the equality row reads content.
        Require(io.Probes == (discovery ? 0 : 2) && io.ReadsFor(rows[0].Path) == 0 && io.ReadsFor(rows[1].Path) == 1,
            "both selection sources skip below-boundary content and prove equality exactly once");
        Require(report.Failed == 0 && report.Uncertain == 0 && store.RehashWatermark(root.RootId) == 0 &&
            store.RehashPendingCount(root.RootId) == 0 && !store.GapInventoryPending(root.RootId) &&
            ((ILocalGapStore)store).GapEpoch(root.RootId)!.Epoch == epoch.Epoch,
            "same epoch clears only after unchanged and content-proven rows are acknowledged, with no remaining debt");
        Require(store.ByObject(root.RootId, rows[0].ObjectId)!.Hash == rows[0].Hash,
            "trusted unchanged entity retains its reusable content hash");
    }
}
