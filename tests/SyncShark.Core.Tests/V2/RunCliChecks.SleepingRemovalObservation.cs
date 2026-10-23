namespace Massdex.SyncShark.V2;

internal static partial class RunCliChecks
{
    private static async Task SleepingRemovalObservationSurvivesInterruptedRunAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        foreach (var kind in new[] { "first-sync", "choice", "rebuildable", "review" })
        {
            var directory = TempRoot();
            try
            {
                var state = Path.Combine(directory, "state");
                Fixture fx;
                string? cursor;
                var due = DateTimeOffset.UtcNow.AddHours(1);
                var clock = new ReviewedRemovalRetryClock { Now = due.AddMinutes(-1) };
                using (var store = SqliteStateStore.Open(state, Settings, TimeProvider.System))
                {
                    fx = Build(store: store);
                    fx.A.Seed("reviewed", "", ItemKind.Folder);
                    fx.B.Seed("reviewed", "", ItemKind.Folder);
                    fx.A.Seed("reviewed/old.txt", "same");
                    fx.B.Seed("reviewed/old.txt", "same");
                    await FirstSyncAsync(fx);
                    var target = side == Side.A ? fx.A : fx.B;
                    var root = side == Side.A ? fx.Pair.A : fx.Pair.B;
                    var folder = (await target.ObserveAsync("reviewed", default))!;
                    var action = new SyncAction("reviewed-removal", 32, ActionKind.Delete, side,
                        "reviewed", null, null, folder, false, null)
                    {
                        FirstSyncChoice = kind == "first-sync", ChoiceRemove = kind == "choice",
                        RebuildableRemove = kind == "rebuildable", RemovalReviewChoice = kind == "review"
                    };
                    store.SavePair(fx.Pair with { Excluded = ["reviewed", "reviewed/private", "selective"] });
                    store.SaveSyncRetry(fx.Pair.PairId, new("reviewed", "reviewed-removal", 1, "sleeping", due,
                        ReviewedActions: [action]));
                    cursor = store.Cursor(root.RootId);
                    var child = target.Seed("reviewed/new.txt", "new during sleep");
                    target.Seed("reviewed/private/hidden.txt", "selectively excluded");
                    target.Seed("selective/hidden.txt", "unrelated exclusion");
                    var run = new SyncRun(store, new Executor(store, Settings, ExecutorChecks.Admission()), fx.Provider, Settings, clock);
                    var interrupted = false;
                    run.AfterReviewedRemovalObservationsForTest = () =>
                    {
                        Require(store.DeferredPlanningDisplayPage(fx.Pair.PairId).Any(row => row.ObjectId == child.ObjectId),
                            "real run durably marks the sleeping removal child before interruption: " + side + "/" + kind);
                        Require(store.Cursor(root.RootId) == cursor, "execution cursor has not advanced at the durable observation boundary");
                        interrupted = true;
                        throw new IOException("injected interruption after durable planning observations");
                    };
                    await run.IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
                    Require(interrupted && store.Cursor(root.RootId) == cursor,
                        "interrupted real run never advances the execution cursor after its observation handoff");
                    Require(store.Live(root.RootId, [child.Path]).Single().ObjectId == child.ObjectId &&
                        store.Live(root.RootId, ["reviewed/private/hidden.txt", "selective/hidden.txt"]).Count == 0,
                        "all reviewed-removal kinds observe changes during sleep but retain selective exclusions");
                    Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 &&
                        store.SyncRetryAt(fx.Pair.PairId, "reviewed")!.RetryAfter == due,
                        "observation cannot bypass the unchanged removal deadline");
                }
                using (var reopened = SqliteStateStore.Open(state, Settings, TimeProvider.System))
                {
                    var targetRoot = side == Side.A ? fx.Pair.A.RootId : fx.Pair.B.RootId;
                    Require(reopened.Cursor(targetRoot) == cursor && reopened.DeferredPlanningDisplayPage(fx.Pair.PairId)
                        .Any(row => row.Path == "reviewed/new.txt"), "cold reopen retains both the old cursor and dirty child");
                    var resumed = new SyncRun(reopened, new Executor(reopened, Settings, ExecutorChecks.Admission()), fx.Provider, Settings, clock);
                    await resumed.IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
                    Require(reopened.Live(targetRoot, ["reviewed/new.txt"]).Count == 1 &&
                        fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
                        "the next real run reobserves the child safely while removal is still sleeping");
                    Require(reopened.DueSyncRetries(fx.Pair.PairId, due, 256).Any(retry => retry.Path == "reviewed"),
                        "the original reviewed-removal debt remains visible at its deadline without a new delta");
                }
            }
            finally { Cleanup(directory); }
        }
    }
}
