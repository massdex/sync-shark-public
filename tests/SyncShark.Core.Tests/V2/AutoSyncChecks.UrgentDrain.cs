namespace Massdex.SyncShark.V2;

internal static partial class AutoSyncChecks
{
    private static async Task UrgentPagesRunWithoutActivityAsync()
    {
        var directory = LocalChecks.FreshRoot("urgent-loop-drain");
        var store = SqliteStateStore.Open(Path.Combine(directory, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        await using var rig = new Rig(["p1"], store: store);
        var pair = store.Pair("p1")!;
        var endpoint = new FakeEndpoint(Side.A, pair.A.RootId);
        var urgent = Enumerable.Range(0, 2050).Select(index => endpoint.Seed($"urgent-{index}.txt", "value")).ToArray();
        var bulk = Enumerable.Range(0, 5000).Select(index => endpoint.Seed($"bulk-{index}.txt", "value")).ToArray();
        var gen = store.BeginGeneration(pair.A.RootId, "fixture");
        store.WriteSnapshot(await endpoint.FullAsync(gen, _ => true, new Progress<string>(), default));
        store.DeferPlanning("p1", urgent.Select(item => (pair.A.RootId, item)), urgent: true);
        store.DeferPlanning("p1", bulk.Select(item => (pair.A.RootId, item)));
        store.AddNotices([new("p1", 0, NoticeKind.Info, "unrelated.txt", "Awaiting unrelated review", "fixture-review")]);
        rig.Run.ProgressScript = _ =>
        {
            var page = store.AdmitDeferredPlanning("p1", long.MaxValue, rotate: false, userOnly: true);
            store.AcknowledgeDeferredPlanning("p1", page, _ => true);
        };
        foreach (var remaining in new long[] { 1026, 2, 0 })
            rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Complete) with
            { UrgentPending = remaining, PlanningBacklog = 5000 + (int)remaining });
        await rig.StartAsync();
        var now = rig.Clock.GetUtcNow();
        await Until(() => rig.Run.Runs("p1") == 3 && rig.Status("p1") == AutoStatus.Idle &&
            rig.Clock.ActiveTimers == 1, "three urgent pages drain without new activity or advancing the clock");
        Require(store.UrgentPending("p1") == 0 && store.CountDeferredPlanning("p1") == 5000 &&
            store.OpenNotices("p1").Any(notice => notice.Path == "unrelated.txt") && rig.Clock.GetUtcNow() == now,
            "bulk backlog and unrelated notices never block urgent progress requeue");
        Pass("UrgentPagesRunWithoutActivity");
    }

    private static async Task NoticeHeldUrgentDoesNotSpinAsync()
    {
        var directory = LocalChecks.FreshRoot("urgent-loop-held");
        var store = SqliteStateStore.Open(Path.Combine(directory, "store"), StorageChecks.Fast,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        await using var rig = new Rig(["p1"], store: store);
        var pair = store.Pair("p1")!;
        var endpoint = new FakeEndpoint(Side.A, pair.A.RootId);
        var held = endpoint.Seed("held.txt", "value");
        var gen = store.BeginGeneration(pair.A.RootId, "fixture");
        store.WriteSnapshot(await endpoint.FullAsync(gen, _ => true, new Progress<string>(), default));
        store.DeferPlanning("p1", [(pair.A.RootId, held)], urgent: true);
        store.AddNotices([new("p1", 0, NoticeKind.Info, held.Path, "Awaiting review", "fixture-review")]);
        rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Partial, held: 1) with { UrgentPending = 1, PlanningBacklog = 1 });
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Run.Runs("p1") == 1 && rig.State("p1").RetryAt is null && store.UrgentPending("p1") == 1,
            "an unchanged notice-held urgent row keeps its owner without immediate requeue");
        Pass("NoticeHeldUrgentDoesNotSpin");
    }
}
