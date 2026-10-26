namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReconciliationLegacyPartialCoverage()
    {
        foreach (var side in new[] { Side.A, Side.B }) await LegacyPartialCoverage(side);
    }

    private static async Task LegacyPartialCoverage(Side side)
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "legacy-transfer-" + Guid.NewGuid().ToString("N")),
            StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("Parent/observed.txt", "original"); fx.A.Seed("Parent/held.txt", "original");
        await FirstSyncAsync(fx.Run(), fx);
        var pair = fx.Pair with { B = fx.Pair.B with { Provider = SyncProviders.Local } };
        store.SavePair(pair);
        await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        var root = side == Side.A ? pair.A : pair.B;
        var endpoint = side == Side.A ? fx.A : fx.B;
        fx.A.ContentProofStore = fx.B.ContentProofStore = store;
        var counterpart = side == Side.A ? pair.B : pair.A;
        var other = side == Side.A ? fx.B : fx.A;
        var gaps = (ILocalGapStore)store;
        gaps.BeginGap(root.RootId, new NtfsUsnCheckpoint(1, 0, 200, 0).Cursor, 100, false);
        var epoch = gaps.GapEpoch(root.RootId);
        Require(epoch is not null, $"legacy fixture opened a gap epoch on side {side} root");
        var items = store.Live(root.RootId, ["Parent/observed.txt", "Parent/held.txt"]).ToArray();
        Require(items.Length == 2 && items.All(item => item is not null),
            $"legacy fixture has both live items on side {side}: {string.Join(",", items.Select(i => i?.Path ?? "<null>"))}");
        gaps.GapDiscoveredBatch(root.RootId, epoch!, items.Select(item => (item, true)).ToArray());
        endpoint.Seed("Parent/observed.txt", "fresh observed content");
        endpoint.Seed("Parent/held.txt", "fresh held content");
        ReconcileBatch Selected(ItemState item) => new("legacy-batch", [item.Path],
            side == Side.A ? new HashSet<string> { item.ObjectId } : new HashSet<string>(),
            side == Side.B ? new HashSet<string> { item.ObjectId } : new HashSet<string>(), (_, _) => false);
        var run = fx.Run();
        var before = fx.WriterA.Calls + fx.WriterB.Calls;
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(fx.WriterA.Calls + fx.WriterB.Calls == before,
            "ordinary AutoSync cannot borrow the reconciliation exemption");
        var puts = fx.WriterA.Puts + fx.WriterB.Puts;
        var preserves = fx.WriterA.Preserves + fx.WriterB.Preserves;
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), default,
            new RunOptions { Batch = Selected(items.Single(item => item.Path == "Parent/observed.txt")) });
        Require(fx.WriterA.Puts + fx.WriterB.Puts == puts + 1 &&
            fx.WriterA.Calls + fx.WriterB.Calls - before ==
                1 + fx.WriterA.Preserves + fx.WriterB.Preserves - preserves,
            "fresh two-root observation performs one copy plus its recovery preservation only");
        Require(gaps.GapObligations(root.RootId,
            [items.Single(item => item.Path == "Parent/observed.txt").ObjectId]).Count == 0,
            "fresh two-root observation acknowledges its settled gap item");
        store.CompleteGapInventory(root.RootId, epoch!.Epoch);
        Require(store.PendingGapInventory(root.RootId) &&
            store.ProtectionBatch(root.RootId, ItemFlags.NeedsRehash, ["Parent/held.txt"])[0].Covered,
            "an unobserved sibling retains protection and prevents epoch clearance");
        fx.Provider.AddInventory(counterpart, new ScriptedInventory(other) { Unreadable = ["Parent/held.txt"] });
        before = fx.WriterA.Calls + fx.WriterB.Calls;
        var held = items.Single(item => item.Path == "Parent/held.txt");
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), default,
            new RunOptions { Batch = Selected(held) });
        Require(fx.WriterA.Calls + fx.WriterB.Calls == before &&
            gaps.GapObligations(root.RootId, [held.ObjectId]).Count == 1,
            "one-root observation cannot exempt or acknowledge legacy debt");
        fx.Provider.AddInventory(counterpart, other);
        var replacement = 0;
        fx.Provider.AddInventory(pair.B, new ReconciliationBarrierInventory(fx.B, () =>
        {
            if (replacement++ == 0) gaps.BeginGap(root.RootId, new NtfsUsnCheckpoint(2, 0, 300, 0).Cursor, 200, false);
            return Task.CompletedTask;
        }));
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), default,
            new RunOptions { Batch = Selected(held) });
        store.CompleteGapInventory(root.RootId, epoch!.Epoch);
        Require(fx.WriterA.Calls + fx.WriterB.Calls == before &&
            gaps.GapEpoch(root.RootId)!.Epoch != epoch!.Epoch && store.PendingGapInventory(root.RootId) &&
            gaps.GapObligations(root.RootId, [held.ObjectId]).Count == 1,
            "an epoch replaced during the read retains its obligation and cannot inherit the old exemption");
        var currentEpoch = gaps.GapEpoch(root.RootId)!;
        // Discovery may publish its captured item after a delta has already removed the live row.
        var orphan = held with { Path = "Parent/removed.txt", ObjectId = "removed-object" };
        gaps.GapDiscoveredBatch(root.RootId, currentEpoch, [(orphan, true)]);
        Require(store.ByObject(root.RootId, orphan.ObjectId) is null &&
            gaps.GapObligations(root.RootId, [orphan.ObjectId]).Count == 1,
            "discovery retains an orphan obligation without a file-state row");
        var editedOrphan = held with { Path = "Parent/edited.txt", ObjectId = "edited-orphan-object" };
        gaps.GapDiscoveredBatch(root.RootId, currentEpoch, [(editedOrphan, true)]);
        var editedRemote = fx.B.Seed(editedOrphan.Path, "edited remotely");
        var deletesBefore = fx.WriterB.Deletes;
        store.SaveSyncRetry(pair.PairId, new("remote-unreadable", "scan", 1, "remote scope unavailable",
            DateTimeOffset.MaxValue, ReadRoots: [pair.B.RootId]));
        fx.Provider.AddInventory(pair.B, fx.B);
        var report = await run.ReconcileScopeAsync(pair.PairId,
            StateMonitor.FromGap(currentEpoch) with { RootId = root.RootId }, new RecordingProgress(), default);
        Require(!report.CompletionFailed && report.Failed == 0 &&
            gaps.GapObligations(root.RootId, [held.ObjectId, orphan.ObjectId]).Count == 0 &&
            !store.PendingGapInventory(root.RootId) && !gaps.HasGapProtection(root.RootId),
            "fresh reconciliation discharges replacement-epoch and orphan obligations and clears the gap");
        Require(store.SyncRetryAt(pair.PairId, "remote-unreadable") is { Kind: "scan" },
            "Dropbox-side sleeping retry survives while the readable local gap epoch completes");
        Require(fx.WriterB.Deletes == deletesBefore &&
            (await fx.B.ObserveAsync(editedOrphan.Path, default))?.Hash == editedRemote.Hash,
            "an orphan local obligation never deletes content present and edited on the counterpart");
        await MirrorAsync(fx);
    }
}
