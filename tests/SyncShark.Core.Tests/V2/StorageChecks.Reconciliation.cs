namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void ReconcileStagingPages(string root)
    {
        var folder = Path.Combine(root, "reconcile-staging");
        const string session = "reconcile-work";
        using (var store = SqliteStateStore.Open(folder, Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var items = Enumerable.Range(0, 257).Select(i => F($"Parent/item-{i:D3}.txt", "object-" + i, "11")).ToArray();
            store.StageReconcile(session, Side.A, items);
            store.StageReconcile(session, Side.B, [items[0]]);
            var page = store.ReconcilePage(session, Side.A, "", "", 1000);
            Require(page.Count == 256, "reconciliation staging caps each page at 256 rows");
            var tail = store.ReconcilePage(session, Side.A, PathKey.Of(page[^1].Path), page[^1].Path, 256);
            Require(tail.Count == 1 && tail[0] == items[^1], "reconciliation staging resumes by key and spelling");
            Require(store.StagedAmong(session, Side.A, ["PARENT/ITEM-000.TXT", "absent"]).SetEquals([items[0].Path]),
                "staged among uses case-insensitive path keys");
            Require(store.StagedUnder(session, Side.A, "PARENT") && !store.StagedUnder(session, Side.A, "Paren") &&
                !store.StagedUnder(session, Side.A, items[0].Path), "staged under selects strict descendants only");
            store.RetireReconcile(session, Side.A, [items[0] with { Path = items[0].Path.ToUpperInvariant() }]);
            Require(store.ReconcilePage(session, Side.A, "", "", 1).Single() == items[0], "retirement retains a different exact spelling");
            store.RetireReconcile(session, Side.A, page);
            Require(store.ReconcilePage(session, Side.A, "", "", 256).Single() == items[^1] &&
                store.ReconcilePage(session, Side.B, "", "", 256).Single() == items[0], "retirement is exact and side scoped");
            ReconcileMissingPages(store, items);
        }
        using var reopened = SqliteStateStore.Open(folder, Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        Require(reopened.ReconcilePage(session, Side.A, "", "", 256).Count == 0 &&
            reopened.ReconcilePage(session, Side.B, "", "", 256).Count == 0, "reconciliation staging is gone after reopen");
    }

    private static void ReconcileMissingPages(SqliteStateStore store, ItemState[] items)
    {
        var gen = store.BeginGeneration("root", "full");
        store.WriteSnapshot(new("root", Side.A, gen, "cursor", items, []));
        store.StageReconcile("reconcile-seen", Side.A, [items[0] with { Path = items[0].Path.ToUpperInvariant() }]);
        var page = store.MissingPage("reconcile-seen", Side.A, "root", "PARENT", 0, "", "");
        Require(page.Count == 256 && page.Count(row => row.Seen) == 1 && page[0].Seen,
            "missing page is bounded and marks case-insensitive seen membership");
        var last = page[^1].Row;
        var tail = store.MissingPage("reconcile-seen", Side.A, "root", "Parent", last.DirNo, last.NameKey, last.Name);
        Require(tail.Count == 1 && !tail[0].Seen && tail[0].Row.Item == items[^1],
            "missing page keyset advances over both seen and missing rows");
        Require(store.MissingPage("reconcile-seen", Side.A, "root", items[0].Path, 0, "", "").Count == 1 &&
            store.MissingPage("reconcile-seen", Side.A, "root", "ParentOther", 0, "", "").Count == 0,
            "missing page restricts authority to exact scope or descendants");
        store.ClearRemovalObservation("reconcile-seen");
        Require(!store.MissingPage("reconcile-seen", Side.A, "root", items[0].Path, 0, "", "").Single().Seen,
            "existing observation cleanup clears reconciliation sessions");
    }
}
