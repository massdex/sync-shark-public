namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    // Held is a fourth unresolved classification: it counts, pages, streams and clears on commit exactly like the
    // persisted started/uncertain/failed states, without a separate store table or a schema version bump.
    private static void IntentHolds_ClassificationCountAndPaging(IStateStore store)
    {
        store.SavePair(Pair("p1"));
        var h1 = IntentFor("h1");
        var h2 = IntentFor("h2");
        var h3 = IntentFor("h3");
        var started = IntentFor("s1");
        var failed = IntentFor("f1");
        store.RecordIntents("p1", [h1, h2, h3, started, failed]);
        store.CommitResult("p1", 1, failed, new ActionResult("f1", Outcome.Failed, null, null, null, "boom"));
        store.SetIntentHeld("p1", "h1", true);
        store.SetIntentHeld("p1", "h2", true);
        store.SetIntentHeld("p1", "h3", true);

        Require(store.CountHeldIntents("p1") == 3 && store.CountUnresolvedIntents("p1") == 5,
            "held intents are counted both as held and as unresolved");
        Require(store.IsIntentHeld("p1", "h1") && !store.IsIntentHeld("p1", "s1") &&
            !store.IsIntentHeld("p1", "missing"),
            "the held point lookup distinguishes held from started and unknown ids");
        var unresolved = store.UnresolvedIntents("p1").Select(action => action.ActionId).ToHashSet(StringComparer.Ordinal);
        Require(new[] { "h1", "h2", "h3", "s1", "f1" }.All(unresolved.Contains),
            "the legacy materialized query includes held alongside started, uncertain and failed");
        var streamed = store.StreamUnresolvedIntents("p1").Select(action => action.ActionId).ToArray();
        Require(streamed.SequenceEqual(["h1", "h2", "h3", "s1", "f1"]),
            $"the unresolved stream pages every classification in (updated, rowid) order: {string.Join(",", streamed)}");

        var first = store.HeldIntentsPage("p1", null, 2);
        Require(first.Items.Count == 2 && first.NextCursor is not null,
            "a full held page exposes a next cursor");
        var second = store.HeldIntentsPage("p1", first.NextCursor, 2);
        Require(second.Items.Count == 1 && second.NextCursor is null,
            "the final held page has no next cursor");
        Require(first.Items.Concat(second.Items).Select(action => action.ActionId).SequenceEqual(["h1", "h2", "h3"]),
            "held pages preserve the (updated, rowid) keyset order across cursor boundaries");
        var exact = store.HeldIntentsPage("p1", null, 3);
        Require(exact.Items.Count == 3 && exact.NextCursor is null,
            "an exactly full final page does not invent another page");

        store.SetIntentHeld("p1", "h2", false);
        Require(store.CountHeldIntents("p1") == 2 && store.CountUnresolvedIntents("p1") == 5,
            "releasing a hold returns the intent to unresolved instead of removing it");
        Require(store.UnresolvedIntents("p1").Any(action => action.ActionId == "h2") &&
            !store.IsIntentHeld("p1", "h2") && store.IsIntentHeld("p1", "h3"),
            "a released hold is uncertain, so it remains unresolved without reading as held");
        Require(store.HeldIntentsPage("p1", null, 10).Items.Select(action => action.ActionId).SequenceEqual(["h1", "h3"]),
            "releasing a hold keeps the other held intents' order");
        store.SetIntentHeld("p1", "h2", false);
        store.SetIntentHeld("p1", "missing", true);
        Require(store.CountHeldIntents("p1") == 2 && store.IntentById("p1", "missing") is null,
            "releasing a non-held id and holding a missing id are no-ops, never reinserts");

        store.CommitResult("p1", 1, h1, new ActionResult("h1", Outcome.Done, null, null, null, null));
        Require(store.CountHeldIntents("p1") == 1 && store.CountUnresolvedIntents("p1") == 4 &&
            store.HeldIntentsPage("p1", null, 10).Items.All(action => action.ActionId != "h1"),
            "a commit clears a held intent like any other unresolved intent");

        ExpectThrows<ArgumentOutOfRangeException>(() => store.HeldIntentsPage("p1", null, 0));
        ExpectThrows<ArgumentOutOfRangeException>(() => store.HeldIntentsPage("p1", null, 201));
        ExpectThrows<ArgumentException>(() => store.HeldIntentsPage("p1", "not-a-cursor", 10));
        ExpectThrows<ArgumentException>(() => store.HeldIntentsPage("p1", "1:2:3", 10));
        ExpectThrows<ArgumentException>(() => store.HeldIntentsPage("p1", "-1:0", 10));
    }

    private static void IntentHolds_RecordIntentsUpsert(IStateStore store)
    {
        store.SavePair(Pair("p1"));
        var first = IntentFor("u1", "before.txt");
        store.RecordIntents("p1", [first]);
        store.SetIntentHeld("p1", "u1", true);
        Require(store.CountHeldIntents("p1") == 1, "a held intent can be re-recorded");

        store.RecordIntents("p1", [first with { Path = "after.txt" }]);
        var rows = store.UnresolvedIntents("p1").Where(action => action.ActionId == "u1").ToArray();
        Require(rows.Length == 1 && rows[0].Path == "after.txt",
            "RecordIntents replaces an existing action id instead of appending a second row");
        Require(store.CountHeldIntents("p1") == 0,
            "re-recording an intent clears its temporary hold and starts it again");

        store.RecordIntents("p1", [IntentFor("u2", "one.txt"), IntentFor("u2", "two.txt")]);
        var batchRows = store.UnresolvedIntents("p1").Where(action => action.ActionId == "u2").ToArray();
        Require(batchRows.Length == 1 && batchRows[0].Path == "two.txt",
            "a duplicate id inside one RecordIntents batch keeps the last upsert");
        if (store is SqliteStateStore sqlite)
            Require(sqlite.IntentRows() == 2, "the real store keeps one row per action id across upserts");
    }

    private static void IntentHolds_OwnershipEnforced(IStateStore store)
    {
        store.SavePair(Pair("p1"));
        store.SavePair(Pair("p2"));
        store.RecordIntent("p1", IntentFor("owned"));
        ExpectThrows<InvalidOperationException>(() => store.SetIntentHeld("p2", "owned", true));
        ExpectThrows<InvalidOperationException>(() => store.SetIntentHeld("p2", "owned", false));
        Require(store.CountHeldIntents("p2") == 0 && !store.IsIntentHeld("p2", "owned"),
            "another pair's intent is never classified or read as held");
    }

    // The keyset index is additive and replayed on every compatible open, so an older database regains it without
    // a version bump and held paging keeps working across restarts.
    private static void IntentOrderIndex_RecreatedOnOpen(string root)
    {
        var folder = Fresh(root, "intent-order-index");
        using (var store = SqliteStateStore.Open(folder, Fast, new TestClock()))
        {
            store.SavePair(Pair("p1"));
            store.RecordIntent("p1", IntentFor("held-1"));
            store.SetIntentHeld("p1", "held-1", true);
            store.RecordIntent("p1", IntentFor("held-2"));
            store.SetIntentHeld("p1", "held-2", true);
        }
        var initial = StorageDamage.IntentOrderIndexState(DbPath(folder));
        Require(initial.Version == StoreSchema.Version && initial.Sql.Contains("ON pending_effect(pair_no)", StringComparison.Ordinal),
            "a fresh store creates the rowid-horizon intent-order index");
        StorageDamage.RemoveIntentOrderIndex(DbPath(folder));
        for (var reopen = 0; reopen < 2; reopen++)
        {
            using (var store = SqliteStateStore.Open(folder, Fast, new TestClock()))
            {
                var page = store.HeldIntentsPage("p1", null, 200);
                Require(page.Items.Count == 2 && page.NextCursor is null &&
                    store.StreamUnresolvedIntents("p1").Count() == 2,
                    "held paging and unresolved streaming survive an open that had to recreate the index");
            }
            var repaired = StorageDamage.IntentOrderIndexState(DbPath(folder));
            Require(repaired.Version == initial.Version && repaired.Sql == initial.Sql,
                "the intent-order index is recreated idempotently without a version bump");
        }
    }

    // One call classifies a whole reconciliation batch; the real store reuses one transaction per 256 ids.
    private static void IntentHolds_BatchClassification(IStateStore store)
    {
        store.SavePair(Pair("p1"));
        var actions = Enumerable.Range(0, 300)
            .Select(index => IntentFor($"b{index:000}", $"f{index}.txt")).ToArray();
        store.RecordIntents("p1", actions);
        var ids = actions.Select(action => action.ActionId).ToArray();

        store.SetIntentsHeld("p1", ids, true);
        Require(store.CountHeldIntents("p1") == 300 && store.CountUnresolvedIntents("p1") == 300,
            "a batch hold classifies every intent across the 256-id transaction bound");
        var first = store.HeldIntentsPage("p1", null, 200);
        var second = store.HeldIntentsPage("p1", first.NextCursor, 200);
        Require(first.Items.Count == 200 && second.Items.Count == 100 && second.NextCursor is null,
            "a batch-held population pages exactly like individually held intents");

        store.SetIntentsHeld("p1", ids, false);
        Require(store.CountHeldIntents("p1") == 0 && store.CountUnresolvedIntents("p1") == 300,
            "a batch release returns every hold to uncertain without resolving intents");

        store.SetIntentsHeld("p1", ["b000", "missing", "b000"], true);
        Require(store.CountHeldIntents("p1") == 1 && !store.IsIntentHeld("p1", "missing"),
            "repeated and missing ids inside a batch stay idempotent no-ops");
    }

    // The stream fixes a rowid horizon, so an intent whose updated value moves forward mid-stream (re-record or
    // commit) can never be revisited, and an intent inserted after the horizon is never pulled in.
    private static void IntentStream_RowidHorizonIsMutationStable(string root)
    {
        var clock = new TestClock();
        using var store = SqliteStateStore.Open(Fresh(root, "stream-mutation"), Fast, clock);
        store.SavePair(Pair("p1"));
        var actions = Enumerable.Range(0, 450)
            .Select(index => IntentFor($"s{index:000}", $"f{index}.txt")).ToArray();
        store.RecordIntents("p1", actions);

        var seen = new List<string>();
        using var stream = store.StreamUnresolvedIntents("p1").GetEnumerator();
        while (stream.MoveNext())
        {
            seen.Add(stream.Current.ActionId);
            if (seen.Count != 200) continue;
            // Between the first and second pages: move an already-visited intent's updated forward, reclassify a
            // later one, resolve another already-visited one, and insert a new intent beyond the horizon.
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            store.RecordIntent("p1", IntentFor("s000", "rewritten.txt"));
            store.SetIntentHeld("p1", "s250", true);
            store.CommitResult("p1", 1, IntentFor("s001", "f1.txt"),
                new ActionResult("s001", Outcome.Failed, null, null, null, "still unresolved"));
            store.RecordIntent("p1", IntentFor("late", "late.txt"));
        }

        Require(seen.Count == 450 && seen.Distinct(StringComparer.Ordinal).Count() == 450,
            $"a rowid-horizon stream visits each pre-existing intent exactly once despite mid-stream updates ({seen.Count})");
        Require(!seen.Contains("late"),
            "an intent recorded after the horizon is not pulled into the current stream");
    }

    private static SyncAction IntentFor(string actionId, string path = "x.txt") =>
        new(actionId, 6, ActionKind.Delete, Side.B, path, null, null, null, false, "k-" + actionId);
}
