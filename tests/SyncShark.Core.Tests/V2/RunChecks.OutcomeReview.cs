using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task OutcomeReviewChecksAsync()
    {
        var fx = Build();
        fx.A.Seed("unfinished.txt", "keep pending");
        var empty = new SyncRun(fx.Store, new DropResultsExecutor(), fx.Provider, Settings, TimeProvider.System);
        var plan = await empty.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var report = await empty.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default);
        Require(report.Outcome == RunOutcomes.Partial && report.Deferred > 0 && fx.Store.FirstSyncDone(fx.Pair.PairId),
            "first-sync marker cannot erase unapplied effects from the final disposition");
        fx = Build();
        await FirstSyncAsync(fx.Run(), fx);
        fx.A.Seed("done.txt", "committed before crash");
        var crash = new SyncRun(fx.Store, new FailAfterApply(fx.Executor), fx.Provider, Settings, TimeProvider.System);
        report = await crash.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(report.Outcome == RunOutcomes.Failed && report.Done == 1 && report.Uploaded == 1,
            "executor crash means failed attempt even after a durable success");
        Require(!V2CliRuntime.RehearsalAccepted(report) &&
            !V2CliRuntime.RehearsalAccepted(report with { Outcome = RunOutcomes.Partial }) &&
            V2CliRuntime.RehearsalAccepted(report with { Outcome = RunOutcomes.Complete }),
            "rehearsal gate accepts only the run owner's complete disposition");
        await SupersededKeepsDebtAsync();
        fx = Build();
        await FirstSyncAsync(fx.Run(), fx);
        fx.A.Seed("cancel.txt", "count before cancellation");
        using var cancellation = new CancellationTokenSource();
        var cancelledRun = new SyncRun(fx.Store, new CancelThenFaultExecutor(fx.Executor, cancellation),
            fx.Provider, Settings, TimeProvider.System);
        report = await cancelledRun.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), cancellation.Token);
        Require(report.Outcome == RunOutcomes.Stopped,
            "cancellation retains Stopped when a completed worker subsequently faults");
        await FatalExecutionReportAsync(false);
        await FatalExecutionReportAsync(true);
        foreach (var failure in new Exception[] { new StateDamagedException("fixture damage"),
            new StateNeedsFreshDatabaseException(1, 2), new Microsoft.Data.Sqlite.SqliteException("fixture corrupt state", 11) })
            await FatalStateReportAsync(failure);
    }

    private static async Task SupersededKeepsDebtAsync()
    {
        var folder = FixtureFolder("superseded-debt");
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("file.txt", "same"); fx.B.Seed("file.txt", "same");
        await FirstSyncAsync(fx.Run(), fx);
        var victim = store.Live(fx.Pair.B.RootId, ["file.txt"]).Single();
        var gen = store.BeginGeneration(fx.Pair.B.RootId, "delta");
        var action = new SyncAction("superseded-fixture", 6, ActionKind.Delete, Side.B, "file.txt",
            null, null, victim, false, null);
        // Notice lifecycle is backed by actual uncertain intent debt, not a synthetic
        // orphan notice. An independent superseded action must not retire that debt.
        var uncertain = action with { ActionId = "uncertain-fixture" };
        store.RecordIntent(fx.Pair.PairId, uncertain);
        store.CommitResult(fx.Pair.PairId, gen, uncertain,
            new(uncertain.ActionId, Outcome.Uncertain, null, null, null, "verification pending") { RetryAfterSeconds = 30 });
        store.SaveSyncRetry(fx.Pair.PairId, new("file.txt", "sync", 1, "parked", DateTimeOffset.MaxValue));
        store.AddNotices([new(fx.Pair.PairId, 6, NoticeKind.Info, "file.txt", "verify", "interrupted-delete-unverified"),
            new(fx.Pair.PairId, 40, NoticeKind.Info, "file.txt", "retry", "sync-retry"),
            new(fx.Pair.PairId, 0, NoticeKind.Info, "file.txt", "absence", DeleteSourceGuard.ReasonKey)]);
        var guardId = DeleteSourceGuard.NoticeId(fx.Pair.PairId, "file.txt");
        store.CloseNotice(guardId, "ignored");
        store.CommitResult(fx.Pair.PairId, gen, action,
            new(action.ActionId, Outcome.Skipped, null, null, null, EffectResolver.Superseded));
        var retainedNotices = store.OpenNotices(fx.Pair.PairId).Where(notice => notice.Path == "file.txt" &&
            notice.ReasonKey is "sync-retry" or "interrupted-delete-unverified").ToArray();
        Require(store.SyncRetryAt(fx.Pair.PairId, "file.txt") is { RetryAfter: var due } && due == DateTimeOffset.MaxValue &&
            store.UnresolvedIntents(fx.Pair.PairId).Any(intent => intent.ActionId == uncertain.ActionId) &&
            retainedNotices.Length == 1 && retainedNotices[0].ReasonKey == "interrupted-delete-unverified",
            "superseded result retains parked retry, unresolved intent and the canonical verification notice");
        store.CommitResult(fx.Pair.PairId, gen, action with { Kind = ActionKind.Forget, ObjectKey = "absent-key" },
            new(action.ActionId, Outcome.Done, null, null, null, null));
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder, "v2", "state.db")};Pooling=False");
        connection.Open();
        Require((string?)StoreSchema.Scalar(connection, null, "SELECT choice FROM notice WHERE notice_id=$id;", ("$id", guardId)) == "ignored",
            "later Done never overwrites a closed notice's user choice");
    }

    private sealed class CancelThenFaultExecutor(IExecutor inner, CancellationTokenSource cancellation) : IExecutor
    {
        public async IAsyncEnumerable<ActionResult> ApplyAsync(Plan plan, IEndpointWriter a, IEndpointWriter b,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var result in inner.ApplyAsync(plan, a, b, ct)) yield return result;
            cancellation.Cancel();
            throw new IOException("completed worker fault during cancellation");
        }
        public Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen,
            IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null) =>
            inner.ReconcileAsync(intents, gen, a, b, ct, progress);
    }

    private static async Task FatalExecutionReportAsync(bool cancel)
    {
        var store = DispatchProxy.Create<IStateStore, FatalStoreProbe>();
        var probe = (FatalStoreProbe)(object)store;
        var fx = Build(suppliedStore: store);
        await FirstSyncAsync(fx.Run(), fx);
        fx.A.Seed("execution.txt", "committed before fatal execution fault");
        using var cancellation = new CancellationTokenSource();
        var executor = new FatalExecutionExecutor(fx.Executor, probe, cancel ? cancellation : null);
        var run = new SyncRun(store, executor, fx.Provider, Settings, TimeProvider.System);
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), cancellation.Token);
        Console.WriteLine($"Fatal execution cancel={cancel}: outcome={report.Outcome}, done={report.Done}, after-damage-access={probe.AccessAfterDamage}, cause={report.HaltedBy?.GetType().Name}: {report.HaltedBy?.Message}");
        Require(report.Outcome == RunOutcomes.StateDamaged && report.Done == 1 && probe.AccessAfterDamage == 0,
            "execution damage remains counted and outranks cancellation: " + cancel);
        PairAutoState? published = null;
        var loop = new PairLoop(fx.Pair.PairId, new AutoSyncInputs(store, run, new FakeChangeWatchSource(), Settings,
            _ => false, () => TimeSpan.FromMinutes(1), TimeProvider.System, state => published = state));
        typeof(PairLoop).GetMethod("AfterReport", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(loop, [report]);
        loop.Kick();
        Require(probe.AccessAfterDamage == 0 && published is { Status: AutoStatus.Held } &&
            published.Reason?.Key == RunOutcomes.StateDamaged,
            "tray handles typed damage and kick without reading or writing the damaged store");
        SyncCliException? error = null;
        try { SyncCliRegistry.CheckRunOutcome(report.Outcome, report); }
        catch (SyncCliException caught) { error = caught; }
        Require(error?.ExitCode == SyncCliExitCode.StateDamaged, "execution damage maps to CLI exit 10");
    }

    private sealed class FatalExecutionExecutor(IExecutor inner, FatalStoreProbe probe,
        CancellationTokenSource? cancellation) : IExecutor
    {
        public async IAsyncEnumerable<ActionResult> ApplyAsync(Plan plan, IEndpointWriter a, IEndpointWriter b,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var result in inner.ApplyAsync(plan, a, b, ct)) yield return result;
            probe.Damaged = true;
            cancellation?.Cancel();
            throw new StateDamagedException("fatal execution fixture");
        }
        public Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen,
            IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null) =>
            inner.ReconcileAsync(intents, gen, a, b, ct, progress);
    }

    private static async Task FatalStateReportAsync(Exception failure)
    {
        var store = DispatchProxy.Create<IStateStore, FatalStoreProbe>();
        var probe = (FatalStoreProbe)(object)store;
        var fx = Build(suppliedStore: store);
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        fx.A.Seed("saved.txt", "effect before fatal completion");
        probe.Failure = failure;
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(report.Outcome == RunOutcomes.StateDamaged && report.Done == 1 && report.HaltedBy is { } cause && StorageFailureClassifier.ScopeOf(cause, default) == FailureRetryOwner.FailureScope.State &&
            probe.AccessAfterDamage == 0,
            "fatal state returns counted in-memory report with no later store access: " + failure.GetType().Name);
        var projected = SyncRunOutcomeProjection.Classify(new SyncRunOutcomeInput { Outcome = report.Outcome });
        Require(projected.Kind == SyncRunOutcomeKind.StateDamaged && projected.NotificationDetail.Contains("verified backup"),
            "fatal GUI projection carries recovery information without loading state");
        probe.Damaged = false;
        var runtime = new V2CliRuntime(null, store, run, new EndpointRootProbe(fx.Provider),
            Path.Combine(Path.GetTempPath(), "fatal-cli-" + Guid.NewGuid().ToString("N")));
        var data = await runtime.RunPairAsync(fx.Pair.PairId, _ => { }, default);
        Require(data.Outcome == RunOutcomes.StateDamaged && probe.AccessAfterDamage == 0,
            "CLI returns fatal report without settling or reading damaged state");
        SyncCliException? observed = null;
        try { SyncCliRegistry.CheckRunOutcome(data.Outcome!, data); }
        catch (SyncCliException error) { observed = error; }
        Require(observed is { ExitCode: SyncCliExitCode.StateDamaged, Code: "state_damaged" } &&
            observed.Message.Contains("verified backup") && observed.Message.Contains("rollback"),
            "fatal state has distinct CLI code and verified recovery instructions");
    }

    public class FatalStoreProbe : DispatchProxy
    {
        private readonly InMemoryStateStore _inner = new();
        public Exception? Failure { get; set; }
        public bool Damaged { get; set; }
        public int AccessAfterDamage { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Damaged) { AccessAfterDamage++; throw new StateDamagedException("access after damage"); }
            if (targetMethod!.Name == nameof(IStateStore.Prune) && Failure is { } failure)
            { Damaged = true; throw failure; }
            try { return targetMethod.Invoke(_inner, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }

    private sealed class DropResultsExecutor : IExecutor
    {
        public async IAsyncEnumerable<ActionResult> ApplyAsync(Plan plan, IEndpointWriter a, IEndpointWriter b,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.CompletedTask; yield break; }
        public Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen,
            IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null) =>
            Task.FromResult<IReadOnlyList<ActionResult>>([]);
    }
}
