using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static async Task NoticeGateChecks(string root)
    {
        using var store = SqliteStateStore.Open(Fresh(root, "notice-gate"), Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        store.SavePair(Pair("p1"));
        var gate = (FairStoreGate)typeof(SqliteStateStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        var count = Fast.StoreNoticePageRows * 2;
        var notices = Enumerable.Range(0, count).Select(n => new Notice("p1", 40, NoticeKind.Info, "item/" + n,
            "Synthetic notice", "test-notice"));
        Task publishing;
        Task foreground;
        using (gate.Enter())
        {
            publishing = Task.Factory.StartNew(() => store.AddNotices(notices), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Require(SpinWait.SpinUntil(() => gate.Waiting == 1, GapGateWaitLimit), "notice publication queued first");
            foreground = Task.Factory.StartNew(() =>
            {
                using (gate.Enter())
                    Require(store.OpenNotices("p1").Count == Fast.StoreNoticePageRows,
                        "foreground reader admitted between bounded notice transactions");
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Require(SpinWait.SpinUntil(() => gate.Waiting == 2, GapGateWaitLimit), "notice reader queued behind first page");
        }
        try { await foreground.WaitAsync(GapGateWaitLimit); }
        finally { await publishing.WaitAsync(GapGateDrainLimit); }
        Require(store.OpenNotices("p1").Count == count, "all notice pages published exactly once");
    }
}
