namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task Reconcile_Move_UnknownContentStaysUncertain()
    {
        var (store, a, b, executor) = Rig();
        var source = a.Seed("y", "content", versionId: "v1");
        b.Seed("x", "content", versionId: "v1");
        var move = await ApplyMoveAsync(b, "x", "y", source, "k1");
        move = move with { ExpectedTarget = move.ExpectedTarget! with { Hash = null } };
        store.RecordIntent("p1", move);

        var results = await executor.ReconcileAsync(new[] { move }, 1, a, b, CancellationToken.None);
        Require(results.Single().Outcome == Outcome.Uncertain,
            "missing moved-content evidence remains uncertain, not a successful landing");
        Require(store.UnresolvedIntents("p1").Count == 1,
            "unknown landing retains its intent for verification instead of advancing the baseline");
    }
}
