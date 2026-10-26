namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReadScopesRereadsExactPaths()
    {
        var fx = Build();
        fx.A.Seed("Parent/item.txt", "original");
        fx.A.Seed("Parent/sibling.txt", "original sibling");
        await FirstSyncAsync(fx.Run(), fx);
        var before = fx.Store.Live(fx.Pair.A.RootId, ["Parent/sibling.txt"]).Single();
        fx.A.Seed("Parent/item.txt", "changed");
        fx.A.Seed("Parent/sibling.txt", "changed sibling");
        // Durable scan debt is allowed to outlive its cursor. Only its exact path is reread.
        var gen = fx.Store.BeginGeneration(fx.Pair.A.RootId, "delta");
        var delta = await fx.A.ChangesSinceAsync(fx.Store.Cursor(fx.Pair.A.RootId)!, gen, _ => true, default);
        fx.Store.CompleteGeneration(gen, delta.CursorAfter, true);
        fx.Store.SaveSyncRetry(fx.Pair.PairId, new("Parent/item.txt", "scan", 0,
            "read failed", DateTimeOffset.MinValue, ReadRoots: [fx.Pair.A.RootId]));
        var fullReads = fx.A.FullCalls;
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.A.FullCalls == fullReads + 1, "due scope is reread even when its delta cursor is current");
        Require((await fx.B.ObserveAsync("Parent/item.txt", default))?.Hash ==
            (await fx.A.ObserveAsync("Parent/item.txt", default))?.Hash,
            "targeted reader supplies the current selected content to the ordinary planner");
        Require(fx.Store.Live(fx.Pair.A.RootId, ["Parent/sibling.txt"]).Single() == before &&
            (await fx.B.ObserveAsync("Parent/sibling.txt", default))?.Hash !=
            (await fx.A.ObserveAsync("Parent/sibling.txt", default))?.Hash,
            "targeted reader does not refresh a sibling outside its admitted path");
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "Parent/item.txt") is null,
            "successful reread retires the selected scan debt");
    }
}
