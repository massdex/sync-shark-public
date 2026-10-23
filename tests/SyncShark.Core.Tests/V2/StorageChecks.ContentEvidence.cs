namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void CommitUnknownMoveContentKeepsBaseline(string root)
    {
        var folder = Fresh(root, "unknown-move-content");
        var a = F("old.txt", "id:a", "77");
        var b = F("old.txt", "id:b", "77");
        var moved = b with { Path = "new.txt", Hash = null };
        var action = new SyncAction("unknown-move", 28, ActionKind.Move, Side.B, "new.txt", "old.txt",
            a with { Path = "new.txt" }, b, false, "k1");
        using (var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash))
        {
            store.SavePair(Pair("p1"));
            SeedBaseline(store, "p1", "k1", a, b);
            store.RecordIntent("p1", action);
            var good = new SyncAction("good", 1, ActionKind.Adopt, Side.B, "good.txt", null,
                a with { Path = "good.txt", ObjectId = "good-a" }, b with { Path = "good.txt", ObjectId = "good-b" }, false, "good");
            store.CommitResults("p1", 2, [(action, new(action.ActionId, Outcome.Done, null, moved, null, null)),
                (good, new(good.ActionId, Outcome.Done, good.Source, good.ExpectedTarget, null, null))]);
            Require(store.Baselines("p1").Count == 2 && store.SyncRetryAt("p1", action.Path) is not null,
                "invalid Done preserves retry debt while the independent good result commits in the same batch");
            Require(store.OpenNotices("p1").Count(n => n.Path == action.Path && n.ReasonKey == "sync-retry") == 1,
                "missing content evidence raises exactly one actionable notice");
        }
        using var reopened = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
        var baseline = reopened.Baselines("p1").Single(row => row.ObjectKey == "k1");
        Require(baseline.B.Path == "old.txt" && baseline.B.Hash == b.Hash,
            "unknown moved content never synthesizes a new baseline, including after cold reopen");
        Require(reopened.UnresolvedIntents("p1").Single().ActionId == action.ActionId,
            "the failed equality proof leaves the move discoverable for verification");
    }
}
