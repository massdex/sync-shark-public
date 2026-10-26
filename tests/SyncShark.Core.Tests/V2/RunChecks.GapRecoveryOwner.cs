using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // Fixture lifetime includes the asynchronously started reconciliation owner, not just AutoSync.
    private sealed class GapRecoveryRun(SyncRun run) : IAsyncDisposable
    {
        internal SyncRun Run { get; } = run;

        internal async Task DrainAsync(string pairId, bool monitor = false)
        {
            if (monitor) await Run.MonitorAsync(pairId, new RecordingProgress(), default);
            var gate = typeof(SyncRun).GetField("_stopGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Run)!;
            var scopes = (Dictionary<string, ActiveReconciliation>)typeof(SyncRun)
                .GetField("_reconciling", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Run)!;
            Task drain;
            lock (gate) drain = scopes.TryGetValue(pairId, out var active) ? active.Drained.Task : Task.CompletedTask;
            await drain.WaitAsync(TimeSpan.FromSeconds(30));
        }

        public async ValueTask DisposeAsync() => await Run.StopAsync();
    }
}
