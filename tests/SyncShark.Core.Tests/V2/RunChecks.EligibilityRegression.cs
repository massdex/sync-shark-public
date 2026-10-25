namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task EligibilityRegressionAsync()
    {
        using var store = SqliteStateStore.Open(FixtureFolder("eligibility-regression"), Settings,
            TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var item = fx.A.Seed("Parent", "", ItemKind.Folder) with { Flags = ItemFlags.Placeholder };
        var gen = store.BeginGeneration(fx.Pair.A.RootId, "full");
        var snapshot = await fx.A.FullAsync(gen, _ => true, SilentProgress, default);
        store.WriteSnapshot(snapshot with { Items = [item] });
        store.SaveSyncRetry(fx.Pair.PairId, new(item.Path, "scan", 1, "enumeration unavailable", DateTimeOffset.MaxValue));
        store.DeferPlanning(fx.Pair.PairId, [(fx.Pair.A.RootId, item)]);
        var page = fx.Run().AdmitPlanningPage(fx.Pair.PairId, fx.Pair);
        Require(page.Count == 1 && store.CountDeferredPlanning(fx.Pair.PairId) == 1 &&
            store.SyncRetryAt(fx.Pair.PairId, item.Path) is not null,
            "cached placeholder classification cannot retire scan or planning obligations at admission");
        var eligibility = new ItemEligibility(store.Pairs(), []);
        foreach (var flag in new[] { ItemFlags.Link, ItemFlags.Placeholder })
            Require(eligibility.Evaluate(fx.Pair, Side.A, item with { Flags = flag }, ItemEligibility.Purpose.Plan).Eligible,
                "selection-only planning preserves observation for cached classifications");
        foreach (var reason in PlanNotices.ObservationReasons)
        {
            var observation = new Notice(fx.Pair.PairId, 30, NoticeKind.Info, "waiting.txt", reason.Detail, reason.Key);
            store.AddNotices([observation, FailureRetryOwner.RetryNotice(fx.Pair.PairId, observation.Path, "observation pending")]);
            Require(store.OpenNotices(fx.Pair.PairId).Count(notice => notice.Path == observation.Path) == 1 &&
                NoticeChoices.IsRetryNotice(observation), "observation cause owns exactly one retry notice and its actions");
            foreach (var notice in store.OpenNotices(fx.Pair.PairId).Where(notice => notice.Path == observation.Path))
                store.CloseNotice(notice.NoticeId!, "resolved");
        }
        await IndexFailureCapAsync(store, fx);
    }

    private static async Task IndexFailureCapAsync(SqliteStateStore store, Fixture fx)
    {
        var gen = store.BeginGeneration(fx.Pair.A.RootId, "full");
        var items = Enumerable.Range(0, StorageNotices.NoticeAggregateLimit + 3)
            .Select(index => fx.A.Seed($"item-{index}.txt", "bytes")).ToArray();
        store.WriteSnapshot(await fx.A.FullAsync(gen, _ => true, SilentProgress, default));
        foreach (var item in items)
        {
            store.RequireRehash(fx.Pair.A.RootId, item, "proof pending");
            store.FailIndexingRehash(fx.Pair.A.RootId, item, store.RehashRevision(fx.Pair.A.RootId, item.ObjectId),
                new ContentProofFailure(ContentProofFailure.StageKind.ContentOpen, "access denied", 5).Encode());
        }
        var notices = store.OpenNotices(fx.Pair.PairId).Where(NoticeChoices.IsIndexFailure).ToArray();
        Require(notices.Count(notice => notice.Path.Length > 0) == StorageNotices.NoticeAggregateLimit,
            "per-file proof notices are capped at the existing aggregate limit");
        var summary = notices.Single(notice => notice.Path.Length == 0);
        var overflow = store.IndexFailureDetailPage(summary, null, 200, 0);
        Require(overflow.Count == 3 && overflow.Items.Count == 3 && overflow.Items.All(notice => notice.Detail.Contains("ContentOpen")),
            "overflow remains readable from entity debt with its exact cause");
        var retired = items[0];
        store.RetireIndexingRehash(fx.Pair.A.RootId, retired, store.RehashRevision(fx.Pair.A.RootId, retired.ObjectId));
        Require(!store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == retired.Path && NoticeChoices.IsIndexFailure(notice)),
            "retiring item work closes its per-file notice");
        Console.WriteLine("PASS EligibilityRegression");
    }
}
