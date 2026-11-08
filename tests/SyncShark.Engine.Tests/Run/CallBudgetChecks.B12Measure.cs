using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Massdex.SyncShark.Engine.Tests.Executor;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>B12-MEAS: test-only allocation probe for the ScanPass async lifetime (suspended and cancelled).
/// Not part of the product budget; registered only as the <c>b12-meas</c> measurement group.</summary>
public static partial class CallBudgetChecks
{
    public static void RunB12Scan()
    {
        ScanPath("suspended-await", suspend: true, cancel: false);
        ScanPath("cancelled-before-rows", suspend: false, cancel: true);
    }

    // Measures one steady-state repair pass with a fresh log so only the ScanPass lifetime under test is timed.
    private static void ScanPath(string label, bool suspend, bool cancel)
    {
        try
        {
            var shape = SmallShape;
            var folder = Path.Combine(Path.GetTempPath(), "syncshark-b12-scan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var local = new FakeSide(Side.Local);
                var dropbox = new FakeSide(Side.Dropbox);
                Seed(local, dropbox, shape, changedTimes: 0);
                using var rig = NewRig(Path.Combine(folder, label), local, dropbox);
                var seed = RepairAsync(rig, "seed").GetAwaiter().GetResult();
                Check.Require(seed.Summary.Completed, "the B12 scan seed repair completes: " + label);
                Func<ExecutorContext, CancellationToken, Task>? before = null;
                if (suspend) before = async (_, _) => await Task.Yield();
                using var cts = new CancellationTokenSource();
                if (cancel) cts.Cancel();
                using var log = RunLog.Open(rig.Folder, rig.Pair.PairId, "b12-" + label, _ => { },
                    Settings.LogBufferCharacters);
                var scanRig = new ScanPassChecks.Rig(rig.Pair, rig.Store, rig.Local, rig.Dropbox,
                    rig.Recovery, rig.Admission, rig.Scope);
                var threw = false;
                var completed = false;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var beforeBytes = GC.GetTotalAllocatedBytes(precise: true);
                try
                {
                    var summary = ScanPassChecks.RunRepairAsync(scanRig, log, Settings,
                        ct: cts.Token, before: before).GetAwaiter().GetResult();
                    completed = summary.Completed;
                }
                catch (OperationCanceledException) { threw = true; }
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - beforeBytes;
                Console.WriteLine("B12 scan path=" + label + " items=" + shape.Items + " cancelled=" + threw +
                    " completed=" + completed + " allocated=" + allocated + " perItem=" +
                    (allocated / (double)shape.Items).ToString("F3"));
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error)
        {
            Console.WriteLine("B12 scan path=" + label + " probe-failed " + error.GetType().Name + ": " + error.Message);
        }
    }
}
