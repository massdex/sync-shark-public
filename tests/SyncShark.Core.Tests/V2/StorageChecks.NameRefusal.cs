using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void WrittenNameRefusalChecks(string root)
    {
        foreach (var side in new[] { Side.A, Side.B })
        foreach (var scenario in new[] { "replacement", "pending-destination", "moved-away" })
        {
            var folder = Fresh(root, "written-refusal-" + side + "-" + scenario);
            string repairedId, otherId;
            using (var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash))
            {
                store.SavePair(Pair("p1"));
                var victim = F("item", "id:victim", "11", flags: ItemFlags.ProviderNameRefused);
                var written = scenario == "moved-away"
                    ? victim with { Path = "elsewhere", Flags = ItemFlags.None }
                    : F("item", "id:written", "22");
                var source = scenario == "moved-away" ? written : written with { ObjectId = "id:source" };
                var rootId = side == Side.A ? "ra" : "rb";
                var generation = store.BeginGeneration(rootId, "snapshot");
                store.WriteSnapshot(new Snapshot(rootId, side, generation, "cursor", [victim], []));
                var repair = new NoticeRepair(side, victim)
                    { Destination = scenario == "pending-destination" ? "another" : null };
                var notice = new Notice("p1", 31, NoticeKind.Info, victim.Path,
                    ProviderNameRefusedException.Detail, ProviderNameRefusedException.NoticeKey, Repair: repair);
                var other = notice with { Repair = new NoticeRepair(side, victim with { ObjectId = "id:other" }) };
                store.AddNotices([notice, other]);
                repairedId = NoticeTable.Id(notice);
                otherId = NoticeTable.Id(other);
                var action = new SyncAction("replace", 32, scenario == "moved-away" ? ActionKind.Move : ActionKind.Update,
                    side, written.Path, scenario == "moved-away" ? victim.Path : null, source, victim, false, "key");
                store.RecordIntent("p1", action);
                store.CommitResult("p1", generation, action, new(action.ActionId, Outcome.Failed, null, null, null, "not applied"));
                Require(store.NoticeById("p1", repairedId) is { Closed: false }, "failed replacement keeps refusal open");
                var done = new ActionResult(action.ActionId, Outcome.Done,
                    side == Side.A ? written : null, side == Side.B ? written : null, null, null);
                store.CommitResult("p1", generation, action, done);
                store.CommitResult("p1", generation, action, done); // replay cannot duplicate or reopen it
                Require(store.UnresolvedIntents("p1").Count == 0 && store.Baselines("p1").Count == 1,
                    "fixture commits a verified successful effect before checking refusal closure");
                Require(store.NoticeById("p1", repairedId)?.Closed == (scenario == "replacement"),
                    "only a committed replacement at the notice destination closes its refusal");
                Require(store.NoticeById("p1", otherId) is { Closed: false },
                    "a different entity's refusal at the same path stays open");
                Require(store.OpenNotices("p1").Count(n => n.ReasonKey == ProviderNameRefusedException.NoticeKey) ==
                    (scenario == "replacement" ? 1 : 2), "successful write does not duplicate refusal notices");
            }
            using var reopened = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash);
            Require(reopened.NoticeById("p1", repairedId)?.Closed == (scenario == "replacement") &&
                reopened.NoticeById("p1", otherId) is { Closed: false }, "entity-scoped closure survives cold reopen");
            using var connection = new SqliteConnection("Data Source=" + DbPath(folder));
            connection.Open();
            var plan = StoreSchema.QueryPlan(connection, "notice-by-id", NoticeTable.ByIdSql, "$p", "$id");
            Require(plan.Any(line => line.Contains("SEARCH n USING PRIMARY KEY (notice_id=?)")) &&
                !plan.Any(line => line.Contains("SCAN n") || line.Contains("TEMP B-TREE")),
                "production notice lookup uses its primary-key index without scanning or sorting");
        }
    }
}
