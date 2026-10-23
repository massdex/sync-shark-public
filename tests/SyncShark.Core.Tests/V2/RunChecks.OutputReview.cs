namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task Output_ReviewChecksAsync()
    {
        await Output_DiagnosticPortsAsync();
        var byteSink = new RecordingProgress();
        using (var activity = RunActivity.Begin(byteSink, V2Settings.Default, TimeProvider.System))
        {
            for (var n = 0; n < 100; n++)
            {
                using var bytes = new SyncByteProgressStream(new MemoryStream(new byte[8]), "Hashing file contents");
                await bytes.StartedAsync();
                await bytes.ReadAsync(new byte[8].AsMemory());
            }
            Require(byteSink.Reports.Count == 0, "byte progress never becomes run output");
            await SyncByteProgressStream.ReportCurrentProcessAsync("Listing page", "done");
            Require(byteSink.Reports.Count == 1, "ordinary process messages still reach the sink");
        }
        var clock = new OutputClock();
        var sink = new RecordingProgress();
        using (var instrumentation = RunInstrumentation.Begin(V2Settings.Default, clock))
        {
            var throttle = new ProgressThrottle(sink, clock, TimeSpan.FromMilliseconds(250));
            throttle.Report("Reading Dropbox", "first", 0, long.MaxValue);
            clock.Advance(11);
            throttle.Report("Reading Dropbox", "second", 1, long.MaxValue);
            clock.Advance(1);
            throttle.Report("Reading Dropbox", "third", 2, long.MaxValue);
            Require(sink.Reports.Any(x => x.Path == "third" && x.Diagnostics is not null),
                "long phases retain frequent path and diagnostic snapshots");
            Require(sink.Reports.Count(x => x.Message?.Contains("ETA") == true) == 1,
                "rich lines are independently paced and extreme ETA cannot overflow");
            throttle.Flush(failed: true);
            Require(sink.Reports.Last().Message!.StartsWith("Reading Dropbox failed after"), "failed phase does not claim done");
        }
        foreach (var kind in new[] { RunOperationKind.Http, RunOperationKind.LocalWrite, RunOperationKind.LocalIo })
        {
            var warnings = new List<string>();
            using var ops = RunOperations.Start(clock, V2Settings.Default, warnings.Add);
            using var operation = RunOperations.Begin(kind, "moving chunks");
            using var stream = RunOperationStream.Touching(new MemoryStream(new byte[100]), operation.Touch);
            for (var n = 0; n < 5; n++)
            {
                await stream.ReadAsync(new byte[1].AsMemory());
                clock.Advance(10);
            }
            Require(warnings.Count == 0, kind + " healthy streaming resets inactivity");
            clock.Advance(16);
            Require(warnings.Any(x => x.Contains("moving chunks")), kind + " stalls warn after idle threshold");
        }
        var notices = new RecordingProgress();
        using (var activity = RunActivity.Begin(notices, V2Settings.Default, clock))
        {
            activity.Progress.Report(new RunProgress("Uploading", "file", 2, 3));
            for (var n = 0; n < 10; n++) await SyncByteProgressStream.ReportThrottleAsync("/2/upload", TimeSpan.FromSeconds(2));
            Require(notices.Reports.Count(x => x.Message?.Contains("provider throttle") == true) == 1,
                "short cooldowns announce without a per-worker flood");
            clock.Advance(5);
            Require(notices.Reports.Any(x => x.Message == "9 provider throttle responses on /2/upload; waiting 2s"),
                "route cooldowns aggregate remaining requests in five-second window");
            await SyncByteProgressStream.ReportThrottleAsync("/2/short", TimeSpan.FromMilliseconds(400));
            Require(notices.Reports.Any(x => x.Message == "1 provider throttle response on /2/short"),
                "a sub-second wait is not claimed in the throttle line");
            await SyncByteProgressStream.ReportThrottleAsync("/2/round", TimeSpan.FromMilliseconds(1200));
            Require(notices.Reports.Any(x => x.Message == "1 provider throttle response on /2/round; waiting 2s"),
                "the real wait is rounded up to whole seconds");
            activity.Progress.Report(new RunProgress("Scanning", "latest.txt", 3, 4));
            clock.Advance(30);
            Require(notices.Reports.Last().Message!.Contains("last activity: Scanning: latest.txt"),
                "heartbeat names phase activity rather than stale dedupe text");
            activity.Dispose();
            var count = notices.Reports.Count;
            clock.Advance(60);
            activity.Progress.Report(new RunProgress("late", null, 0, 0));
            Require(notices.Reports.Count == count, "no timer or phase output after disposal");
        }
        // Pacer waits (the shared provider rate budget) are queueing, never throttles, and report at most every 30 s.
        var pacerSink = new RecordingProgress();
        using (var activity = RunActivity.Begin(pacerSink, V2Settings.Default, clock))
        {
            for (var n = 0; n < 3; n++) await SyncByteProgressStream.ReportPacerAsync("/2/files/list_folder");
            Require(pacerSink.Reports.Count(x => x.Message?.Contains("requests queued for") == true) == 1,
                "pacer waits announce as queueing, once");
            Require(pacerSink.Reports.Any(x =>
                    x.Message == "1 requests queued for /2/files/list_folder (shared Dropbox rate budget)"),
                "pacer queueing names the shared rate budget");
            Require(pacerSink.Reports.All(x => x.Message?.Contains("provider throttle") != true),
                "pacer queueing is never reported as a provider throttle");
            clock.Advance(31);
            Require(pacerSink.Reports.Any(x =>
                    x.Message == "2 requests queued for /2/files/list_folder (shared Dropbox rate budget)"),
                "pacer queueing reports the batch accumulated since the last line");
            await SyncByteProgressStream.ReportPacerAsync("/2/files/list_folder");
            Require(pacerSink.Reports.Count(x => x.Message?.Contains("requests queued for") == true) == 2,
                "pacer queueing stays within its thirty-second cadence");
        }
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        var scanGate = new GatedScanInventory(fx.A);
        fx.Provider.AddInventory(fx.Pair.A, scanGate);
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, V2Settings.Default, clock);
        var first = run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        await scanGate.Entered;
        var queuedSink = new RecordingProgress();
        var second = run.PreviewFirstSyncAsync(fx.Pair.PairId, queuedSink, CancellationToken.None);
        clock.Advance(40);
        scanGate.Release();
        await first;
        await second;
        Require(queuedSink.Reports.Any(x => x.Message == "Run queued for 40.0s"), "queued time is reported separately");
        Require(queuedSink.Reports.Where(x => x.Diagnostics is not null).All(x => x.Diagnostics!.Elapsed < TimeSpan.FromSeconds(40)),
            "instrumentation excludes run gate queue time");
        var quiet = new RecordingProgress();
        using (var activity = RunActivity.Begin(quiet, V2Settings.Default, clock))
        {
            for (var n = 0; n < 6; n++)
            {
                using (RunOperations.Begin(RunOperationKind.TransferSlot, "routine")) clock.Advance(5);
            }
            Require(quiet.Reports.Any(x => x.Message?.StartsWith("Still working:") == true) &&
                    !quiet.Reports.Any(x => x.Message?.Contains("transfer slots;") == true),
                "routine slot waits do not suppress heartbeat");
        }
        var directory = Path.Combine(PhysicalFixtureRoot, "orphan-" + Guid.NewGuid().ToString("N"), "v2", "logs");
        Directory.CreateDirectory(directory);
        try
        {
            var orphan = Path.Combine(directory, "20260101-120000-000-aaaaaaaa-p-run.jsonl");
            var foreign = Path.Combine(directory, "foreign.jsonl");
            File.WriteAllText(orphan, "{}"); File.WriteAllText(foreign, "{}");
            RunLog.Rotate(directory, 50, RunLog.MaximumBytes);
            Require(!File.Exists(orphan) && File.Exists(foreign), "rotation cleans only matching orphan twins");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS Output_ReviewChecks");
    }

    private static async Task Output_DiagnosticPortsAsync()
    {
        var previousTrace = RunTrace.Slot.Value;
        var previousPreparation = RunPreparation.Slot.Value;
        var root = Path.Combine(PhysicalFixtureRoot, "diagnostic-ports-" + Guid.NewGuid().ToString("N"));
        var error = Console.Error;
        try
        {
            RunTrace.Slot.Value = null;
            RunPreparation.Slot.Value = null;
            using var fallback = new StringWriter();
            Console.SetError(fallback);
            RunTrace.Emit("lower fallback");
            RunLog.Trace("shim fallback");
            Require(fallback.ToString() == "Sync Shark trace: lower fallback" + Environment.NewLine +
                "Sync Shark trace: shim fallback" + Environment.NewLine, "exact lower and shim no-scope fallback text");
            Console.SetError(new ThrowingOutput());
            RunTrace.Emit("failed sink");
            RunPreparation.Report("no scope");
            Console.SetError(error);

            using (var outer = RunLog.Open(root, "outer", "run", false))
            {
                Require(ReferenceEquals(RunTrace.Slot.Value, outer) && ReferenceEquals(RunLog.Current, outer),
                    "logger port and shim share actual instance identity");
                using (var inner = RunLog.Open(root, "inner", "run", false))
                {
                    RunTrace.Emit("inner-only");
                    Require(ReferenceEquals(RunTrace.Slot.Value, inner), "nested logger installs its single port slot");
                }
                Require(ReferenceEquals(RunLog.Current, outer), "nested logger restores outer");
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var child = Task.Run(async () =>
                {
                    using var log = RunLog.Open(root, "child", "run", false);
                    ready.SetResult();
                    await release.Task;
                    Require(ReferenceEquals(RunTrace.Slot.Value, log), "child logger survives parent flow activity");
                    RunTrace.Emit("child-only");
                });
                await ready.Task;
                try { Require(ReferenceEquals(RunLog.Current, outer), "concurrent logger cannot replace parent slot"); }
                finally { release.SetResult(); }
                await child;
                RunTrace.Emit("outer-only");
            }
            Require(RunTrace.Slot.Value is null, "logger scope restores empty slot after writer completion");

            var clock = new OutputClock();
            var sink = new RecordingProgress();
            using (var outer = RunActivity.Begin(sink, V2Settings.Default, clock))
            {
                RunPreparation.Report("outer preparation");
                using (var inner = RunActivity.Begin(null, V2Settings.Default, clock))
                {
                    RunActivity.Preparation("inner preparation");
                    Require(ReferenceEquals(RunPreparation.Slot.Value, inner), "preparation shim and port share nested instance");
                }
                Require(ReferenceEquals(RunPreparation.Slot.Value, outer), "nested activity restores outer port");
                await Task.Run(async () =>
                {
                    using var child = RunActivity.Begin(null, V2Settings.Default, clock);
                    await Task.Yield();
                    RunPreparation.Report("child preparation");
                    Require(ReferenceEquals(RunPreparation.Slot.Value, child), "activity slot flows across child awaits");
                });
                Require(ReferenceEquals(RunPreparation.Slot.Value, outer), "child activity leaves parent scope intact");
                clock.Advance(30);
                Require(sink.Reports.Any(x => x.Message?.Contains("outer preparation") == true) &&
                    sink.Reports.All(x => x.Message?.Contains("child preparation") != true &&
                        x.Message?.Contains("inner preparation") != true), "preparation text remains isolated per activity");
            }
            Require(RunPreparation.Slot.Value is null, "activity restores empty port slot");

            using (var prior = RunLog.Open(root, "prior", "run", false))
            using (var log = RunLog.Open(root, "conditional", "run", false))
            {
                RunTrace.Slot.Value = null;
                log.Dispose();
                Require(RunTrace.Slot.Value is null, "logger disposal does not replace a changed slot");
            }
            using (var prior = RunActivity.Begin(null, V2Settings.Default, clock))
            using (var activity = RunActivity.Begin(null, V2Settings.Default, clock))
            {
                RunPreparation.Slot.Value = null;
                activity.Dispose();
                Require(RunPreparation.Slot.Value is null, "activity disposal does not replace a changed slot");
            }
        }
        finally
        {
            Console.SetError(error);
            RunTrace.Slot.Value = previousTrace;
            RunPreparation.Slot.Value = previousPreparation;
        }
    }
}
