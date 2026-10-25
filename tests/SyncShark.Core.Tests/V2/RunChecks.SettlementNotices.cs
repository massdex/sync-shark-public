using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // RQ-032/094: display-only summaries cannot turn unrelated verified work into permanent debt.
    private static async Task SettlementNoticeScopeAsync()
    {
        foreach (var flag in new[] { ItemFlags.Link, ItemFlags.Placeholder })
        {
            var folder = FixtureFolder("settlement-summary");
            try
            {
                using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
                var fx = Build(suppliedStore: store);
                var local = fx.A.Seed("Parent/Child.txt", "same");
                fx.B.Seed(local.Path, "same");
                var protectedItem = fx.A.Seed("Unrelated", "held") with { Flags = flag };
                fx.B.Seed("Unrelated", "held");
                await FirstSyncAsync(fx.Run(), fx);
                var cursor = store.Cursor(fx.Pair.A.RootId)!;
                var generation = store.BeginGeneration(fx.Pair.A.RootId, "fixture");
                store.WriteDelta(new(fx.Pair.A.RootId, Side.A, generation, cursor, cursor,
                    [new(ChangeKind.Upsert, protectedItem.ObjectId, protectedItem.Path, protectedItem, null)], false, []));
                store.CompleteGeneration(generation, cursor, true);
                store.DeferPlanning(fx.Pair.PairId, [(fx.Pair.A.RootId, local)], urgent: true);
                var settings = Settings with { ReplaceOnlineOnlyStubs = false };
                var run = new SyncRun(store, fx.Executor, fx.Provider, settings, TimeProvider.System);
                // Keep the committed cursor: no endpoint change overwrites the fixture's flagged metadata.
                var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
                Require(store.OpenNotices(fx.Pair.PairId).Any(n => n.Path.Length == 0 &&
                    n.ReasonKey == (flag == ItemFlags.Link ? "links-excluded" : "stubs-left-alone")),
                    "production summary remains present during settlement");
                Require(report.Failed == 0 && store.CountDeferredPlanning(fx.Pair.PairId) == 0,
                    $"{flag} summary permits admitted equal-content debt to retire through a real run");
                Require(store.Baselines(fx.Pair.PairId).Any(b => b.A.Path == local.Path && b.A.Hash == local.Hash),
                    "summary settlement preserves the verified baseline");
            }
            finally { DeleteFixture(folder); }
        }

        // Exercise the actual completion owner with explicit plans, so each negative case is
        // evaluated (not merely filtered out before planning). No alternate settlement predicate.
        foreach (var scenario in new[] { "summary", "quarantine", "ancestor-failed", "ancestor-skipped", "path", "stale-version" })
        {
            var folder = FixtureFolder("settlement-notice-" + scenario);
            try
            {
                using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
                var fx = Build(suppliedStore: store);
                var local = fx.A.Seed("Parent/Child.txt", "same");
                fx.B.Seed(local.Path, "same");
                var run = fx.Run();
                await FirstSyncAsync(run, fx);
                store.DeferPlanning(fx.Pair.PairId, [(fx.Pair.A.RootId, local)], urgent: true);
                var page = store.AdmitDeferredPlanning(fx.Pair.PairId, DateTime.UtcNow.Ticks);
                Require(page.Count == 1, "fixture admits one exact-version entity");
                Notice[] notices = scenario switch
                {
                    "quarantine" => [new(fx.Pair.PairId, 30, NoticeKind.UserDecision, "", "root quarantine", "fixture-quarantine",
                        Quarantine: new(Side.A, null, null, true))],
                    "path" => [new(fx.Pair.PairId, 38, NoticeKind.Info, local.Path, "path remains unsettled", "fixture-path")],
                    _ => [new(fx.Pair.PairId, 36, NoticeKind.Info, "", "display only", "links-excluded")]
                };
                var ancestor = new SyncAction("ancestor", 9, ActionKind.Move, Side.B, "Parent", "Former",
                    null, null, false, "ancestor-key");
                var plan = new Plan(fx.Pair.PairId, 1,
                    scenario == "ancestor-failed" ? [ancestor] : [], notices)
                { SkippedActions = scenario == "ancestor-skipped" ? [ancestor] : [] };
                if (scenario == "stale-version")
                    store.DeferPlanning(fx.Pair.PairId, [(fx.Pair.A.RootId, local)], urgent: true);
                var side = new SyncRun.SideScan(1, "cursor", true, false, [], [],
                    new Dictionary<string, DateTimeOffset?>(), []);
                var acknowledge = typeof(SyncRun).GetMethod("AcknowledgeVerifiedRehash", BindingFlags.Instance | BindingFlags.NonPublic)!;
                acknowledge.Invoke(run, [fx.Pair, new SyncRun.BothScan(side, side), plan, new SyncRun.Tally(Settings),
                    (Func<string, bool>)(_ => true), (Func<string, bool>)(_ => true), (Func<string, bool>)(_ => true),
                    CancellationToken.None, page, null]);
                Require(store.CountDeferredPlanning(fx.Pair.PairId) == (scenario == "summary" ? 0 : 1),
                    scenario + " retains the intended settlement and exact-version boundary");
            }
            finally { DeleteFixture(folder); }
        }
    }
}
