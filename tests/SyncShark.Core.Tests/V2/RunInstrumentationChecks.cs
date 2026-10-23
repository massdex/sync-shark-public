namespace Massdex.SyncShark.V2;

/// <summary>Fast deterministic checks of per-run collection, snapshots and disabled collection.</summary>
internal static class RunInstrumentationChecks
{
    public static void Run()
    {
        Require(RunInstrumentation.Current is null, "no collector outside a run");
        using (var disabled = RunInstrumentation.Begin(V2Settings.Default with { EnableRunInstrumentation = false },
                   TimeProvider.System))
            Require(disabled is null && RunInstrumentation.Current is null, "disabled collection creates no collector");
        using (var collector = RunInstrumentation.Begin(V2Settings.Default, TimeProvider.System)!)
        {
            Require(ReferenceEquals(RunInstrumentation.Current, collector), "run scope exposes its collector");
            using (collector.Measure(RunTimingStage.AdmissionWait)) { }
            var first = collector.Admitted();
            var second = collector.Admitted();
            var active = collector.Snapshot();
            Require(active.AdmittedConcurrency == 2 && active.PeakAdmittedConcurrency == 2 &&
                active.AdmissionCount == 2, "actual admissions counted independently of worker caps");
            first.Dispose();
            first.Dispose();
            second.Dispose();
            collector.RecordBatch(3);
            collector.RecordBatch(3);
            collector.RecordBatch(1);
            collector.RecordThrottle();
            collector.RecordRetry();
            collector.RecordServerError();
            var snapshot = collector.Snapshot();
            Require(snapshot.AdmittedConcurrency == 0, "early release and finally release are idempotent");
            Require(snapshot.Timings[RunTimingStage.AdmissionWait].Count == 1 &&
                snapshot.Timings[RunTimingStage.AdmissionWait].Elapsed >= TimeSpan.Zero, "completed stage timing recorded");
            Require(snapshot.BatchHistogram[3] == 2 && snapshot.BatchHistogram[1] == 1,
                "histogram records actual batch sizes");
            Require(snapshot.ThrottleCount == 1 && snapshot.RetryCount == 1 && snapshot.ServerErrorCount == 1,
                "throttles, retries and server errors are separate counts");
            collector.RecordFailure("boom");
            collector.RecordFailure("boom");
            collector.RecordFailure("bang");
            collector.RecordFailure(null);
            var failures = collector.Snapshot();
            Require(failures.FailureReasons["boom"] == 2 && failures.FailureReasons["bang"] == 1 &&
                failures.FailureReasons.Values.Sum() == 4, "failure reasons are counted, an absent reason is kept");
            collector.RecordSkip("deferred");
            collector.RecordSkip("deferred");
            collector.RecordSkip("changed");
            collector.RecordSkip(null);
            var skips = collector.Snapshot();
            Require(skips.SkipReasons["deferred"] == 2 && skips.SkipReasons["changed"] == 1 &&
                skips.SkipReasons["Unspecified skip"] == 1 && skips.SkipReasons.Values.Sum() == 4 &&
                skips.FailureReasons.Values.Sum() == 4,
                "skip reasons are counted in their own histogram, apart from failure reasons");
            for (var i = 0; i < 12; i++) collector.RecordFailure("reason-" + i);
            var bounded = collector.Snapshot();
            Require(bounded.FailureReasons.Count == 10 && bounded.FailureReasons["boom"] == 2,
                "the reported reasons are bounded to the top N, most frequent first");
            Require(snapshot.FailureReasons.Count == 0, "snapshots remain detached");
            collector.RecordBatch(3);
            Require(snapshot.BatchHistogram[3] == 2 && active.BatchHistogram.Count == 0, "snapshots remain detached");
            using (var nested = RunInstrumentation.Begin(V2Settings.Default, TimeProvider.System))
                Require(!ReferenceEquals(RunInstrumentation.Current, collector), "nested run has its own collector");
            Require(ReferenceEquals(RunInstrumentation.Current, collector), "nested disposal restores enclosing run");
        }
        Require(RunInstrumentation.Current is null, "finished run leaves no ambient collector");
        Console.WriteLine("v2 instrumentation checks passed.");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
