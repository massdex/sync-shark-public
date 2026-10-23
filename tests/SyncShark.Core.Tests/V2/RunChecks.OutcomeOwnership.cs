using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task OutcomeOwnershipChecksAsync()
    {
        var fx = Build();
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        fx.A.Seed("pending.txt", "retained work");
        fx.WriterB.PutFailure = _ => new IOException("injected effect failure");
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(report.Outcome == RunOutcomes.Partial && report.Failed == 1 && report.Done == 0 && report.Pending > 0,
            "a finished attempt with only failed items is partial, not an uncompleted attempt");
        Require(fx.Store.ReadRunHistory(fx.Pair.PairId, 1).Single().Outcome == report.Outcome,
            "history retains the run owner's final outcome");
        var projected = SyncRunOutcomeProjection.Classify(new SyncRunOutcomeInput
        { Outcome = report.Outcome, Failed = report.Failed, Pending = report.Pending, Deferred = report.Deferred });
        Require(projected.Kind == SyncRunOutcomeKind.Partial && !projected.IsSuccess &&
            !projected.NotificationDetail.Contains("need your input"),
            "GUI partial is neither convergence nor automatic manual review");
        foreach (var (outcome, kind) in new[]
        {
            (RunOutcomes.Complete, SyncRunOutcomeKind.Success),
            (RunOutcomes.Partial, SyncRunOutcomeKind.Partial),
            (RunOutcomes.Paused, SyncRunOutcomeKind.Paused),
            (RunOutcomes.Stopped, SyncRunOutcomeKind.Stopped),
            (RunOutcomes.Failed, SyncRunOutcomeKind.NeedsAttention)
        })
            Require(SyncRunOutcomeProjection.Classify(new SyncRunOutcomeInput
            { Outcome = outcome, Conflicts = 9, Failed = 7 }).Kind == kind,
                "explicit owner disposition wins over presentation counters: " + outcome);
        var finish = typeof(SyncRun).GetMethod("FinalizeOutcome",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var failedAttempt = report with { Outcome = RunOutcomes.Failed, CompletionFailed = true };
        var finalized = (RunReport)finish.Invoke(null, [failedAttempt, CancellationToken.None])!;
        Require(finalized.Outcome == RunOutcomes.Failed,
            "late completion failure cannot downgrade a failed attempt to partial");
        finalized = (RunReport)finish.Invoke(null,
            [report with { PlanBoundExceeded = true }, CancellationToken.None])!;
        Require(finalized.Outcome == RunOutcomes.PlanBoundExceeded,
            "plan admission flag reaches the same final outcome owner");
        var indexing = report with { Outcome = RunOutcomes.Complete, Done = 0, Failed = 0, Held = 0,
            Uncertain = 0, Skipped = 0, Deferred = 0, IndexingRemainingByRoot = new Dictionary<string, long> { ["root-a"] = 42 } };
        finalized = (RunReport)finish.Invoke(null, [indexing, CancellationToken.None])!;
        Require(finalized.Outcome == RunOutcomes.Complete && finalized.Pending == 0,
            "root indexing work is informational and never changes sync disposition or pending debt");
        var indexProjection = SyncRunOutcomeProjection.Classify(new SyncRunOutcomeInput
            { Outcome = RunOutcomes.Complete, IndexingRemainingByRoot = indexing.IndexingRemainingByRoot });
        Require(indexProjection.Kind == SyncRunOutcomeKind.Success &&
            indexProjection.CountLines.Any(line => line.Contains("42 remaining")),
            "GUI exposes root indexing status without turning completed sync into partial");
        await DeferralWriteFailureRetainsCursorAsync();
        await GenerationAndGapCommitAtomicallyAsync();
        Console.WriteLine("PASS OutcomeOwnershipChecks");
    }

    private static async Task DeferralWriteFailureRetainsCursorAsync()
    {
        var folder = FixtureFolder("deferred-write-fault");
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("Held/File", "same");
        fx.B.Seed("Held/File", "same");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        ((ILocalGapStore)store).BeginGap(fx.Pair.A.RootId, "cursor", 1, false, "fixture gap");
        fx.B.Seed("Held/File", "changed during verification");
        var cursor = store.Cursor(fx.Pair.B.RootId);
        using var connection = new SqliteConnection($"Data Source={Path.Combine(folder, "v2", "state.db")};Pooling=False");
        connection.Open();
        StoreSchema.Execute(connection, null, """
            CREATE TRIGGER fail_deferred BEFORE INSERT ON planning_deferred
            BEGIN SELECT RAISE(ABORT,'injected deferred failure'); END;
            CREATE TRIGGER fail_retry BEFORE INSERT ON sync_retry WHEN NEW.path_key<>''
            BEGIN SELECT RAISE(ABORT,'injected retry failure'); END;
            """);
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(report.Outcome == RunOutcomes.Failed && store.Cursor(fx.Pair.B.RootId) == cursor,
            "double deferral write failure reaches run failure owner and cannot advance the cursor");
        Require(store.OpenNotices(fx.Pair.PairId).Any(notice => notice.ReasonKey == "run-unavailable"),
            "double deferral failure is reported by the existing run failure owner");
        StoreSchema.Execute(connection, null, "DROP TRIGGER fail_deferred; DROP TRIGGER fail_retry;");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(store.CountDeferredPlanning(fx.Pair.PairId) > 0,
            "replaying retained cursor durably hands hidden changes to the original deferred owner");
    }

    private static async Task GenerationAndGapCommitAtomicallyAsync()
    {
        var folder = FixtureFolder("generation-gap-atomic");
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("anchor.txt", "same");
        fx.B.Seed("anchor.txt", "same");
        await FirstSyncAsync(fx.Run(), fx);
        // BeginGap marks inventory_pending only for a valid checkpoint. The former
        // literal "cursor" left it zero (measured in the failed fixture DB), so it
        // could not test rollback of an existing inventory obligation.
        var checkpoint = new NtfsUsnCheckpoint(1, 0, 100, 0);
        ((ILocalGapStore)store).BeginGap(fx.Pair.A.RootId, checkpoint.Cursor, 100, false, "fixture gap");
        Require(store.GapInventoryPending(fx.Pair.A.RootId), "atomic fixture starts with pending inventory");
        var cursor = store.Cursor(fx.Pair.A.RootId);
        var gen = store.BeginGeneration(fx.Pair.A.RootId, "full");
        using var connection = new SqliteConnection($"Data Source={Path.Combine(folder, "v2", "state.db")};Pooling=False");
        connection.Open();
        StoreSchema.Execute(connection, null, """
            CREATE TRIGGER fail_gap BEFORE UPDATE OF inventory_pending ON local_gap WHEN NEW.inventory_pending=0
            BEGIN SELECT RAISE(ABORT,'injected gap settlement failure'); END;
            """);
        var failed = false;
        var other = store.BeginGeneration(fx.Pair.B.RootId, "delta");
        var sides = new SyncRun.BothScan(
            new(gen, "new-cursor", true, true, [], [], new Dictionary<string, DateTimeOffset?>(), []),
            new(other, "other-cursor", true, false, [], [], new Dictionary<string, DateTimeOffset?>(), []));
        try { fx.Run().AdvanceCursors(sides); }
        catch (SqliteException) { failed = true; }
        Require(failed && store.Cursor(fx.Pair.A.RootId) == cursor && store.GapInventoryPending(fx.Pair.A.RootId),
            "failed inventory settlement rolls back generation completion and cursor together");
        StoreSchema.Execute(connection, null, "DROP TRIGGER fail_gap;");
        fx.Run().AdvanceCursors(sides);
        Require(store.Cursor(fx.Pair.A.RootId) == "new-cursor" && !store.GapInventoryPending(fx.Pair.A.RootId),
            "retry commits the same still-open generation and inventory settlement together");
    }
}
