namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static void ReconciliationBatchBoundValidated()
    {
        var fx = Build();
        Require(Settings.ReconciliationBatch == new ReconciliationBatchBound(128, 256L * 1024 * 1024),
            "reconciliation batch defaults bound paths and bytes");
        foreach (var bound in new ReconciliationBatchBound[] { new(0, 1), new(-1, 1), new(1, 0), new(1, -1) })
        {
            try
            {
                _ = new SyncRun(fx.Store, fx.Executor, fx.Provider,
                    Settings with { ReconciliationBatch = bound }, TimeProvider.System);
                throw new InvalidOperationException("invalid reconciliation bound was accepted");
            }
            catch (ArgumentOutOfRangeException error)
            {
                Require(error.ParamName == nameof(V2Settings.ReconciliationBatch),
                    "invalid reconciliation bound identifies its setting");
            }
        }
        _ = new SyncRun(fx.Store, fx.Executor, fx.Provider,
            Settings with { ReconciliationBatch = new(1, 1) }, TimeProvider.System);
    }
}
