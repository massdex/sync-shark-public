namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{    // d1: the reconciliation run owns a dedicated log that names its trigger and scope, selection source and counts,
    // each batch's paths/bytes/admission wait/plan summary, its acknowledgement counts, the intake retirement count
    // per batch and in total, and the selecting/repairing phases.
    private static async Task ReconciliationLogsEveryStage()
    {
        var folder = FixtureFolder("reconcile-log");
        try
        {
            using var store = SqliteStateStore.Open(Path.Combine(folder, "state"), Settings,
                TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            var item = fx.A.Seed("Excluded/file.txt", "retained");
            SelectionSnapshot(store, fx.Pair.A, Side.A, [item]);
            store.SavePair(fx.Pair with { Excluded = ["Excluded"] });
            store.DeferPlanning(fx.Pair.PairId, [(fx.Pair.A.RootId, item)]);
            RunLog? captured = null;
            var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System,
                beginRunLog: (pairId, command) =>
                {
                    captured = RunLog.Open(folder, pairId, command, false);
                    return captured;
                });
            await run.ReconcileScopeAsync(fx.Pair.PairId, StateMonitor.PlanningBacklog(), new RecordingProgress(), default);
            await captured!.Completion;
            var text = File.ReadAllText(captured.Path!);
            Require(text.Contains("reconciliation trigger=PlanningBacklog scope=root", StringComparison.Ordinal),
                "the reconciliation log names the trigger and scope");
            Require(text.Contains("reconciliation phase=" + RunPhases.Selecting, StringComparison.Ordinal) &&
                text.Contains("reconciliation phase=" + RunPhases.Repairing, StringComparison.Ordinal),
                "the reconciliation log names the selecting and repairing phases");
            var catalog = MessageCatalog.Default;
            var source = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                catalog.Template("reconciliation-source-trace"), catalog.Template("source-planning-backlog"));
            var selection = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                catalog.Template("reconcile-backlog-selection"), 1, 1);
            Require(text.Contains(source, StringComparison.Ordinal) &&
                text.Contains(selection, StringComparison.Ordinal),
                "the reconciliation log names the selection source and its counts");
            Require(text.Contains("reconciliation batch: paths=1 bytes=", StringComparison.Ordinal) &&
                text.Contains("admissionWaitMs=", StringComparison.Ordinal),
                "the reconciliation log names each batch's paths, bytes and admission wait");
            var dispositionsPrefix = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                catalog.Template("reconcile-dispositions-trace").Split("{1}")[0], 1);
            Require(text.Contains(dispositionsPrefix, StringComparison.Ordinal),
                "the reconciliation log names the acknowledgement dispositions");
            Require(text.Contains("reconciliation intake retired batch=1 total=1", StringComparison.Ordinal) &&
                text.Contains("reconciliation intake retired total=1", StringComparison.Ordinal),
                "the reconciliation log names the intake retirement count per batch and in total");
        }
        finally { DeleteFixture(folder); }
    }
}
