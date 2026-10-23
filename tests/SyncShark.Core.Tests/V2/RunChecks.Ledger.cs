namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task LiveLedgerAndAdmissionAsync()
    {
        var root = LocalChecks.FreshRoot("live-ledger");
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        fx.A.Seed("one.txt", "one");
        fx.A.Seed("two.txt", "two");
        var before = store.Cursor(fx.Pair.A.RootId);
        var blockedCsv = Path.Combine(root, "blocked.csv");
        var blocked = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
            new RunOptions { EffectsExportPath = blockedCsv, MaxPlannedActions = 1 });
        Require(blocked.Outcome == "plan_bound_exceeded", "bound has the single outcome owner's distinct outcome");
        Require(fx.WriterA.Calls + fx.WriterB.Calls == 0 && File.ReadAllLines(blockedCsv).Length == 2 &&
            File.ReadAllLines(blockedCsv)[1].StartsWith("1,complete,", StringComparison.Ordinal),
            "bound exceeded produces zero endpoint effects with a completed empty ledger");
        Require(store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == PlanNotices.PlanBoundExceededKey) == 1,
            "bound refusal raises one notice");
        Require(store.Cursor(fx.Pair.A.RootId) == before && store.Baselines(fx.Pair.PairId).Count == 0 &&
            store.CountUnresolvedIntents(fx.Pair.PairId) == 0, "bound refusal commits no planned work or cursor");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
            new RunOptions { MaxPlannedActions = 1 });
        Require(store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == PlanNotices.PlanBoundExceededKey) == 1,
            "repeated bound refusal deduplicates the notice");
        var liveCsv = Path.Combine(root, "live.csv");
        var admitted = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
            new RunOptions { EffectsExportPath = liveCsv });
        var lines = File.ReadAllLines(liveCsv);
        Require(admitted.Failed == 0 && lines.Length == 4 && lines[^1].StartsWith("1,complete,", StringComparison.Ordinal) &&
            lines.Skip(1).All(line => line.EndsWith(",false", StringComparison.Ordinal)),
            "next unbounded run replans both effects and streams exactly one live row per committed effect");
        Require(!store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == PlanNotices.PlanBoundExceededKey),
            "admission resolves the previous bound notice");
        fx.A.Seed("three.txt", "three");
        var equal = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
            new RunOptions { MaxPlannedActions = 1 });
        Require(equal.Failed == 0 && await fx.B.ObserveAsync("three.txt", default) is not null,
            "an action count equal to the bound runs normally");

        // Same CSV owner, same columns, explicit simulation marker, and CSV escaping.
        var simulated = Path.Combine(root, "simulated.csv");
        using (var ledger = new EffectsLedger(simulated, simulated: true))
            ledger.Simulated(new RehearsalEffect("CopyNew", "B", "local", "a,b.txt", 1, "hash"));
        var simulatedLines = File.ReadAllLines(simulated);
        Require(!simulatedLines.Any(line => line.Contains(",complete,", StringComparison.Ordinal)),
            "disposing an unfinished ledger never writes a completeness trailer (abrupt-stop contract)");
        Require(simulatedLines[0] == lines[0] && simulatedLines[1].Contains("\"a,b.txt\"", StringComparison.Ordinal) &&
            simulatedLines[1].EndsWith(",true", StringComparison.Ordinal), "rehearsal shares live CSV format and escaping");
        await InterruptedLiveLedgerAsync();
        await PlanBoundNoticeChoicesAsync();
        await LedgerFailureAndObserverAsync();
        Console.WriteLine("PASS live ledger commits, interruption, bounded reconciliation and plan admission");
    }

    private sealed class FailedLedgerWriter : StringWriter
    {
        public bool Fail { get; set; }
        public override void Flush()
        {
            if (Fail) throw new IOException("fixture ledger disk full");
            base.Flush();
        }
    }

    private static async Task LedgerFailureAndObserverAsync()
    {
        using var store = SqliteStateStore.Open(LocalChecks.FreshRoot("ledger-failure"), StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        fx.A.Seed("committed.txt", "content");
        var observed = 0;
        void Observe(PairDef pair, IReadOnlyList<CommittedAction> results)
        {
            // A different thread can acquire the store while the observer runs. This would deadlock
            // until timeout if CSV observation still ran inside the shared SQLite gate.
            var read = Task.Run(() => store.Pair(pair.PairId));
            Require(read.Wait(GateTimeout), "committed observer runs outside the shared store gate");
            observed += results.Count(row => row.Outcome == Outcome.Done && row.Action.Kind == ActionKind.CopyNew);
        }
        store.ResultsCommitted += Observe;
        RunReport report;
        try
        {
            report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
                new RunOptions { EffectsExportPath = Path.Combine(LocalChecks.FreshRoot("ledger-export"), "missing-parent", "effects.csv") });
        }
        finally { store.ResultsCommitted -= Observe; }
        Require(report.Done == 1 && report.CompletionFailed && observed == 1 &&
            await fx.B.ObserveAsync("committed.txt", default) is not null,
            "ledger IO failure preserves committed effects, completed counts and diagnostic-failure flag");
        var notice = store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == EffectsLedger.IncompleteReason);
        Require(NoticeChoices.Options(notice).Any(o => o.Choice == NoticeChoices.RetryNow) &&
            NoticeChoices.Options(notice).Any(o => o.Choice == NoticeChoices.Ignore),
            "ledger diagnostic offers Retry and Ignore without losing the report");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
            new RunOptions { EffectsExportPath = Path.Combine(LocalChecks.FreshRoot("ledger-repaired"), "effects.csv") });
        Require(!store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == EffectsLedger.IncompleteReason),
            "the next clean completed ledger closes the diagnostic notice family");

        void ThrowingObserver(PairDef _, IReadOnlyList<CommittedAction> __) => throw new InvalidOperationException("fixture subscriber failed");
        store.ResultsCommitted += ThrowingObserver;
        fx.A.Seed("observer.txt", "observer");
        try { report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default); }
        finally { store.ResultsCommitted -= ThrowingObserver; }
        Require(report.Done == 1 && report.Failed == 0 && await fx.B.ObserveAsync("observer.txt", default) is not null,
            "a throwing subscriber never reclassifies durable effects as failed");
        Require(store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == EffectsLedger.IncompleteReason) == 1,
            "a throwing observer raises one ledger diagnostic notice");
        fx.A.Seed("retry-request.txt", "retry");
        var retryNotice = store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == EffectsLedger.IncompleteReason);
        var runtime = new V2CliRuntime(null, store, run, new EndpointRootProbe(fx.Provider), LocalChecks.FreshRoot("ledger-runtime"));
        await runtime.ResolveConflictAsync(fx.Pair.PairId, string.Empty, NoticeChoices.RetryNow,
            retryNotice.NoticeId!, 0, null, default);
        Require(await fx.B.ObserveAsync("retry-request.txt", default) is not null,
            "ledger notice Retry requests a live sync rather than merely returning a label");
        var writer = new FailedLedgerWriter();
        using var simulated = new EffectsLedger(writer, simulated: true);
        writer.Fail = true;
        simulated.Simulated(new RehearsalEffect("CopyNew", "B", "local", "x.txt", 1, "hash"));
        simulated.Complete();
        Require(simulated.Failure is IOException && !writer.ToString().Contains(",complete,", StringComparison.Ordinal),
            "simulated IO failure is diagnostic, never an endpoint exception or complete ledger");
    }

    private static async Task PlanBoundNoticeChoicesAsync()
    {
        using var store = SqliteStateStore.Open(LocalChecks.FreshRoot("bound-choices"), StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        fx.A.Seed("a.txt", "a"); fx.A.Seed("b.txt", "b");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
            new RunOptions { MaxPlannedActions = 0 });
        Notice BoundNotice() => store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == PlanNotices.PlanBoundExceededKey);
        var notice = BoundNotice();
        Require(store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice.PlanBound == new NoticePlanBound(0, 2),
            "bound/count metadata round-trips through real SQLite notice codec");
        Require(!run.TryApplyImmediateNoticeChoice(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.RetryNow, default),
            "bound Retry cannot take the generic unbounded scheduler route");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.RetryNow, default);
        Require(fx.WriterB.Calls == 0 && BoundNotice().PlanBound == new NoticePlanBound(0, 2),
            "Retry preserves the original explicit cap");
        fx.A.Seed("c.txt", "c");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.ApprovePlanSize, default);
        Require(fx.WriterB.Calls == 0 && BoundNotice().PlanBound == new NoticePlanBound(2, 3),
            "approve uses displayed count once and refuses a plan that grew beyond it");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.ApprovePlanSize, default);
        Require(fx.WriterB.Puts == 3 && !store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == PlanNotices.PlanBoundExceededKey),
            "approving the new displayed count admits exactly that bounded run");
        fx.A.Seed("d.txt", "d");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default, new RunOptions { MaxPlannedActions = 0 });
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, BoundNotice().NoticeId!, NoticeChoices.Ignore, default);
        Require(fx.WriterB.Puts == 3 && store.Pair(fx.Pair.PairId)!.Excluded.Count == 0,
            "Ignore neither admits rejected work nor excludes the pair root");
    }

    private static async Task InterruptedLiveLedgerAsync()
    {
        var root = LocalChecks.FreshRoot("interrupted-ledger");
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        for (var i = 0; i < 4; i++) fx.A.Seed($"item-{i}.txt", "content" + i);
        fx.WriterB.ParkAfterFirstEffect = true;
        var csv = Path.Combine(root, "interrupted.csv");
        var pending = run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
            new RunOptions { EffectsExportPath = csv });
        try { await fx.WriterB.FirstEffectCommitted.Task.WaitAsync(GateTimeout); }
        finally { await run.StopAsync(); }
        await pending;
        var committed = store.Baselines(fx.Pair.PairId).Count;
        Require(committed == 1 && File.ReadAllLines(csv).Length == committed + 1 &&
            !File.ReadAllLines(csv).Any(line => line.Contains(",complete,", StringComparison.Ordinal)),
            "interrupted live ledger exactly matches durable completed effects but never claims completion");
        fx.WriterB.ParkAfterFirstEffect = false;

        // Model a crash after the provider effect but before its SQLite result commit. The bounded
        // rerun must reconcile this already-admitted intent, log its commit, then hold NEW work.
        fx.A.Seed("reconcile.txt", "retained");
        fx.B.Seed("reconcile.txt", "retained");
        var source = (await fx.A.ObserveAsync("reconcile.txt", default))!;
        var action = new SyncAction("interrupted-copy", 1, ActionKind.CopyNew, Side.B, "reconcile.txt", null,
            source, null, false, "interrupted-copy-key");
        store.RecordIntent(fx.Pair.PairId, action);
        var resumedCsv = Path.Combine(root, "bounded-resume.csv");
        var resumed = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default,
            new RunOptions { EffectsExportPath = resumedCsv, MaxPlannedActions = 0 });
        Require(resumed.Outcome == "plan_bound_exceeded", "bounded interrupted rerun still refuses new work");
        Require(store.IntentById(fx.Pair.PairId, action.ActionId) is null &&
            File.ReadAllLines(resumedCsv).Skip(1).Any(line => line.Contains(",reconcile.txt,", StringComparison.Ordinal)),
            "bound does not strand prior admitted work and logs its reconciled commit");
        Require(await fx.B.ObserveAsync("item-3.txt", default) is null,
            "bounded resume does not admit the remaining new copies");
    }
}
