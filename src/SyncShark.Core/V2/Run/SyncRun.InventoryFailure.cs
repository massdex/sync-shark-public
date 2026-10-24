namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private SideScan RetainFailedInventory(PairDef pair, Side side, ProgressThrottle throttle,
        IReadOnlyDictionary<string, string>? inventoryCursors, Exception error)
    {
        // RQ-032/034: a failed read is durable retry debt, never absence authority.
        // Provider page/local-walk checkpoints belong to the inventory and are left intact.
        // Do not cancel the other side: it can finish and retain its own progress.
        var root = side == Side.A ? pair.A : pair.B;
        var gen = _store.BeginGeneration(root.RootId, "full");
        var cursor = inventoryCursors?.GetValueOrDefault(root.RootId) ?? _store.Cursor(root.RootId) ?? "";
        var reason = "Inventory read incomplete: " + error.Message +
            " Progress is saved; restore connectivity or access and Retry to resume.";
        _store.WriteSnapshot(new Snapshot(root.RootId, side, gen, cursor, [], [""]));
        throttle.Report(RunPhases.Reading(root), reason, 0, 0);
        return new SideScan(gen, cursor, false, true, [], [], NoDeletions, [])
        {
            IncompleteSubtrees = [""], InventoryFailure = error,
            IncompleteReasons = new Dictionary<string, string> { [""] = reason }
        };
    }

    private static DateTimeOffset? ScanRetryFloor(BothScan scan, string path) => new[] { scan.A, scan.B }
        .Where(side => side.IncompleteSubtrees.Contains(path, StringComparer.OrdinalIgnoreCase))
        .Select(side => FailureRetryOwner.ThrottleCause(side.InventoryFailure)?.RetryAt).Max();

    private static SyncRetry WithScanFloor(SyncRetry retry, DateTimeOffset? floor) => retry with
    {
        RetryAfter = FailureRetryOwner.NotBefore(new(retry.Attempts, retry.RetryAfter), floor).RetryAfter,
        FailureKind = floor is not null && retry.Kind == "scan" ? FailureRetryOwner.FailureKind.ProviderThrottle : retry.FailureKind
    };
}
