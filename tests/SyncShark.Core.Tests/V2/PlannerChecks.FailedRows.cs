namespace Massdex.SyncShark.V2;

internal static partial class PlannerChecks
{
    private static void FailedMappedRowIsAtomic()
    {
        var local = F("folder/a_b.txt", "local", "old");
        var remote = F("folder/a:b.txt", "remote", "old");
        var row = Row(Base("linked", local, remote), local with { Hash = "left" }, remote with { Hash = "right" });
        var safeRow = Row(null, F("safe", "safe", "hash"), null);
        var forget = new SyncAction("forget", 27, ActionKind.Forget, Side.A, local.Path, null, null, null, false, "linked");
        var left = new SyncAction("left", 27, ActionKind.KeepBoth, Side.A, "folder/left.txt", null,
            row.B, null, false, "left") { ConflictTransitionKey = "linked" };
        var right = new SyncAction("right", 27, ActionKind.KeepBoth, Side.B, "folder/right.txt", null,
            row.A, null, false, "right") { ConflictTransitionKey = "linked" };
        var safe = new SyncAction("safe", 18, ActionKind.CopyNew, Side.B, "safe", null, safeRow.A, null, false, "safe");
        foreach (var failed in new[] { local.Path, remote.Path, left.Path, right.Path })
        {
            var filtered = FailedPlanRows.Filter([(row, forget), (row, left), (row, right), (safeRow, safe)], [failed]);
            Require(filtered.Count == 1 && filtered.Single().Action == safe,
                "failure of either mapped spelling drops Forget and both successors together, retaining independent work");
        }
    }
}
