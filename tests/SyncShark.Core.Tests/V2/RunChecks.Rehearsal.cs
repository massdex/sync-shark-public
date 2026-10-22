namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private sealed class DenyingRehearsalGapReads : ILocalGapIo
    {
        public bool Paused => false;
        public LocalGapProof? Probe(RootDef root, ItemState item) => throw new InvalidOperationException("Real gap probe reached");
        public Task<(string Hash, LocalGapProof Proof)?> HashAsync(RootDef root, ItemState item, CancellationToken ct) =>
            throw new InvalidOperationException("Real gap hash reached");
    }

    internal static async Task RehearsalAsync()
    {
        PlanOnlyPathChecks(); // Includes live-root, alias, state-hardlink and export refusal.
        using var store = SqliteStateStore.Open(LocalChecks.FreshRoot("rehearsal-store"), StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("edit.txt", "old");
        await FirstSyncAsync(fx.Run(), fx);
        fx.A.Seed("edit.txt", "new");
        fx.A.Seed("upload.txt", "upload");
        fx.B.Seed("download.txt", "download");
        var writes = fx.WriterA.Calls + fx.WriterB.Calls;
        fx.WriterA.OnContentRead = fx.WriterB.OnContentRead = _ => throw new InvalidOperationException("Rehearsal opened real content.");
        var effects = new List<RehearsalEffect>();
        using var overlay = new RehearsalOverlay(Path.Combine(LocalChecks.FreshRoot("rehearsal"), "overlay.db"));
        var endpoints = new RehearsalEndpoints(fx.Provider, overlay, effect => { lock (effects) effects.Add(effect); });
        var capture = new PlanCaptureExecutor(fx.Executor);
        var run = new SyncRun(fx.Store, capture, endpoints, Settings, TimeProvider.System);
        var first = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(first.Failed == 0 && first.Done >= 3, "rehearsal runs real executor and finishes both directions");
        Require(fx.WriterA.Calls + fx.WriterB.Calls == writes, "rehearsal never calls a real writer");
        Require(effects.Any(effect => effect.Path == "upload.txt") && effects.Any(effect => effect.Path == "download.txt"),
            "rehearsal records both directions");
        Require(fx.Store.Baselines(fx.Pair.PairId).Any(row => row.A.Path == "upload.txt"),
            "rehearsal commits successful bookkeeping");
        var before = effects.Count;
        var second = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(second.Failed == 0 && effects.Count == before, "second rehearsal sees first run effects and performs no further writes");
        Require(store.Live(fx.Pair.A.RootId, ["download.txt"]).Count == 1 &&
            store.Live(fx.Pair.B.RootId, ["upload.txt"]).Count == 1,
            "virtual provider echoes install simulated effects in file inventory, not only baselines");
        Require(fx.WriterA.Calls + fx.WriterB.Calls == writes, "two rehearsal runs never call a real writer");
        Require(await fx.B.ObserveAsync("upload.txt", default) is null && await fx.A.ObserveAsync("download.txt", default) is null,
            "real endpoints remain unchanged");
        ((ILocalGapStore)store).BeginGap(fx.Pair.A.RootId, new NtfsUsnCheckpoint(1, 0, 100, 0).Cursor, 0, false);
        store.ForgetCursor(fx.Pair.A.RootId);
        Require(store.GapInventoryPending(fx.Pair.A.RootId), "rehearsal begins with an inventory gap");
        fx.A.Seed("after-gap.txt", "new after gap");
        var gapRun = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(gapRun.Failed == 0 && !store.GapInventoryPending(fx.Pair.A.RootId), "rehearsal executes finishing gap settlement and AdvanceCursors even when content holds defer actions");
        var writer = endpoints.Writer(fx.Pair.B);
        var localWriter = endpoints.Writer(fx.Pair.A);
        var refusal = new FreeSpaceRefusal("fixture space hold", "free-space-unknown");
        fx.WriterA.FreeSpaceAdmissionFor = _ => new FreeSpaceAdmission(null, refusal);
        var admission = await ((ILocalFreeSpaceAdmission)localWriter).AdmitFreeSpaceAsync(
            new SyncAction("space-fixture", 0, ActionKind.CopyNew, Side.A, "space.txt", null, null, null, false, null), default);
        Require(admission.Refusal == refusal, "rehearsal delegates the real free-space admission decision");
        fx.WriterA.FreeSpaceAdmissionFor = null;
        Require(localWriter is ISyncDirectDownloadTarget && writer is ISyncDirectDownloadSource && writer is IFolderTreeDeleter,
            "rehearsal preserves local download and cloud whole-folder capability selection");
        var stages = new List<bool>();
        var directSource = (await writer.ObserveAsync("download.txt", default))!;
        await ((ISyncDirectDownloadTarget)localWriter).PutDownloadedAsync(
            new SyncAction("direct-fixture", 0, ActionKind.CopyNew, Side.A, "direct.txt", null, directSource, null, false, null),
            (ISyncDirectDownloadSource)writer, (stage, _) => { stages.Add(stage.Verified); return Task.CompletedTask; }, default);
        Require(stages.SequenceEqual(new[] { false, true }), "direct download checkpoints creation then verification before publication");
        fx.WriterB.RecoveryUnavailable = "fixture recovery unavailable";
        Require(((IRecoveryAvailability)writer).RecoveryUnavailable == fx.WriterB.RecoveryUnavailable,
            "rehearsal reports actual recovery availability to production safety gates");
        fx.WriterB.RecoveryUnavailable = null;
        Require(((IRecoveryAvailability)writer).RecoveryUnavailable is null, "rehearsal observes restored recovery availability");
        var uploaded = (await writer.ObserveAsync("upload.txt", default))!;
        var recovery = await writer.PreserveAsync(uploaded, "preserve-fixture", default);
        Require(recovery.Hash == uploaded.Hash && recovery.Size == uploaded.Size, "simulated recovery retains source metadata");
        var moved = await writer.MoveAsync(new SyncAction("move-fixture", 0, ActionKind.Move, Side.B,
            "renamed.txt", "upload.txt", null, uploaded, false, null), default);
        Require(await writer.ObserveAsync("upload.txt", default) is null && moved.Hash == uploaded.Hash,
            "virtual move retains metadata and tombstones the old name");
        await writer.MkDirAsync("new-folder", default);
        await writer.DeleteAsync(new SyncAction("delete-fixture", 0, ActionKind.Delete, Side.B,
            moved.Path, null, null, moved, false, null), default);
        var view = await endpoints.Inventory(fx.Pair.B).FullAsync(100, _ => true, null!, default);
        Require(view.Items.Any(item => item.Path == "new-folder") && !view.Items.Any(item => item.Path is "upload.txt" or "renamed.txt"),
            "full inventory overlays mkdir and deletion tombstones");
        Require(effects.Any(effect => effect.Kind == "Delete" && effect.Hash == moved.Hash && effect.Size == moved.Size),
            "delete effects record the victim size and hash");
        Require(fx.WriterA.Calls + fx.WriterB.Calls == writes, "all simulated writer methods avoid real writers");
        var synthetic = (await endpoints.Writer(fx.Pair.A).ObserveAsync("download.txt", default))!;
        var proofGen = store.BeginGeneration(fx.Pair.A.RootId, "fixture");
        store.CompleteGeneration(proofGen, new NtfsUsnCheckpoint(1, 0, 100, 0).Cursor, true);
        var gapIo = new RehearsalGapIo(new DenyingRehearsalGapReads(), overlay, store);
        Require(gapIo.Probe(fx.Pair.A, synthetic)?.File.Size == synthetic.Size &&
            (await gapIo.HashAsync(fx.Pair.A, synthetic, default))?.Hash == synthetic.Hash,
            "gap verification sees virtual metadata without opening a synthetic identity on disk");
        for (var index = 0; index < 600; index++)
        {
            var item = synthetic with { Path = "Tree/" + index.ToString("D4") };
            overlay.Save("range-fixture", item.Path, item);
        }
        overlay.Save("range-fixture", "Tree-extra", synthetic with { Path = "Tree-extra" });
        overlay.Save("range-fixture", "Tree/deleted", null);
        Require(overlay.Items("range-fixture", "tree").Count() == 600,
            "indexed subtree pages are case insensitive, exclude prefix siblings and tombstones");
        Console.WriteLine("PASS rehearsal virtual effects, full executor, two-run convergence and isolated-root guards");
    }
}
