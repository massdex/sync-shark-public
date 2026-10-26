namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task PlanningObservationHandoffAsync(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        await FirstSyncAsync(fx.Run(), fx);
        fx.A.Seed("held.txt", "observed");
        var generation = store.BeginGeneration(fx.Pair.A.RootId, "full");
        var snapshot = await fx.A.FullAsync(generation, _ => true, SilentProgress, default);
        store.WriteSnapshot(snapshot);
        store.CompleteGeneration(generation, snapshot.CursorAfter, true);
        store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.UserDecision, "held.txt", "Choose a side", "fixture-decision")]);
        Require(store.DeferredPlanningDisplayPage(fx.Pair.PairId).Any(row => row.Path == "held.txt"),
            "inventory publication retains observation debt before the completed cursor");
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(store.DeferredPlanningDisplayPage(fx.Pair.PairId).Any(row => row.Path == "held.txt") &&
            await fx.B.ObserveAsync("held.txt", default) is null,
            "temporary decision protection does not retire the observation obligation or perform an effect");
        var decision = store.OpenNotices(fx.Pair.PairId).Single(notice => notice.ReasonKey == "fixture-decision");
        store.CloseNotice(decision.NoticeId!, "resolved");
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require((await fx.B.ObserveAsync("held.txt", default))?.Hash == Hash("observed"),
            "lifting protection plans the retained observation without another provider change");
        var ancestor = new ItemState("placeholder", ItemKind.Folder, "ancestor", null, null, 0,
            DateTimeOffset.MinValue, ItemFlags.Placeholder);
        var child = new ItemState("placeholder/child.txt", ItemKind.File, "descendant", null, null, 4,
            DateTimeOffset.MinValue, ItemFlags.None);
        var stub = child with { Path = "placeholder/stub.txt", ObjectId = "stub", Flags = ItemFlags.Placeholder };
        generation = store.BeginGeneration(fx.Pair.A.RootId, "full");
        store.WriteSnapshot(new Snapshot(fx.Pair.A.RootId, Side.A, generation, "", [ancestor], [])
            { IncludesPath = path => path == ancestor.Path });
        store.WriteSnapshot(new Snapshot(fx.Pair.A.RootId, Side.A, generation, "", [child, stub], [])
            { IncludesPath = path => path == child.Path || path == stub.Path });
        Require(store.DeferredPlanningDisplayPage(fx.Pair.PairId).Any(row => row.Path == child.Path) &&
            !store.DeferredPlanningDisplayPage(fx.Pair.PairId).Any(row => row.Path == stub.Path),
            "snapshot handoff admits the child of a placeholder folder but rejects the fresh left-alone stub");
        var observed = child with { Path = "placeholder/observed.txt", ObjectId = "observed" };
        store.RetainPlanningObservations(fx.Pair.A.RootId, [observed], default);
        Require(store.DeferredPlanningDisplayPage(fx.Pair.PairId).Any(row => row.Path == observed.Path),
            "run observation admits the child through the same fresh-observation owner");
        var observedStub = stub with { Path = "placeholder/observed-stub.txt", ObjectId = "observed-stub" };
        store.RetainPlanningObservations(fx.Pair.A.RootId, [observedStub], default);
        Require(!store.DeferredPlanningDisplayPage(fx.Pair.PairId).Any(row => row.Path == observedStub.Path),
            "run observation rejects its own fresh left-alone stub flags");
        using var replacing = SqliteStateStore.Open(Path.Combine(folder, "replacing"),
            StorageChecks.Fast with { ReplaceOnlineOnlyStubs = true }, TimeProvider.System, LocalItems.CaptureOptionalHash);
        replacing.SavePair(fx.Pair);
        replacing.MarkFirstSyncDone(fx.Pair.PairId);
        generation = replacing.BeginGeneration(fx.Pair.A.RootId, "full");
        replacing.WriteSnapshot(new Snapshot(fx.Pair.A.RootId, Side.A, generation, "", [stub], []));
        replacing.RetainPlanningObservations(fx.Pair.A.RootId, [observedStub], default);
        Require(replacing.DeferredPlanningDisplayPage(fx.Pair.PairId).Count == 2,
            "both handoffs admit fresh stubs when replacement is enabled");
        Console.WriteLine("PASS PlanningObservationHandoff");
    }
}
