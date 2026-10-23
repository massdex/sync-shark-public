namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void MissingPrepareMatchReadIsFilteredAndPaged(string root)
    {
        using var store = SqliteStateStore.Open(Fresh(root, "missing-prepare-match-pages"), Fast, new TestClock());
        store.SavePair(Pair("p1"));
        var match = new PrepareMatch("a|m:1:1", "b|v:1");
        foreach (var kind in new[] { "sync", "first-sync", "post-preview", "decision", "reviewed-removal" })
            for (var i = 0; i < 514; i++)
                store.SaveSyncRetry("p1", new(kind + "/" + i.ToString("D4"), kind, 2, "saved", T0,
                    Match: i % 2 == 0 ? match : null));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var retry in store.ReadRetriesMissingPrepareMatch("p1"))
        {
            if (retry.Match is not null || retry.Kind is not ("sync" or "first-sync" or "post-preview") || !seen.Add(retry.Path))
                throw new InvalidOperationException("Evidence handoff admitted ineligible or duplicate retry work.");
            store.SaveSyncRetry("p1", retry with { Match = match });
        }
        Require(seen.Count == 771 && !store.ReadRetriesMissingPrepareMatch("p1").Any(),
            "filtered evidence paging remains complete when each yielded row leaves the query");
        Require(store.SyncRetryAt("p1", "decision/0001")!.Match is null,
            "prepared evidence never changes independent decision authority");
    }

    private static void SleepingRemovalObservationSurvivesReopen(string root)
    {
        var folder = Fresh(root, "sleeping-removal-observation");
        var deadline = DateTimeOffset.UtcNow.AddHours(1);
        var child = F("cache/new.txt", "id:new-child", "77");
        using (var store = SqliteStateStore.Open(folder, Fast, new TestClock()))
        {
            store.SavePair(Pair("p1") with { Excluded = ["cache"] });
            store.SaveSyncRetry("p1", new("cache", "reviewed-removal", 1, "retry", deadline));
            var gen = store.BeginGeneration("rb", "full");
            store.WriteSnapshot(new Snapshot("rb", Side.B, gen, "observed", [child], []));
            store.RetainPlanningObservations("rb", [child], default);
            store.RetainPlanningObservations("rb", [child], default);
            store.CompleteGeneration(gen, "observed", true);
            Require(store.DueSyncRetries("p1", deadline.AddSeconds(-1), 256).Count == 0,
                "retaining sleeping removal observations grants no early effect authority");
        }
        using var reopened = SqliteStateStore.Open(folder, Fast, new TestClock());
        var dirty = reopened.DeferredPlanningDisplayPage("p1");
        Require(dirty.Count == 1 && dirty[0].ObjectId == child.ObjectId && dirty[0].Path == child.Path,
            "the observed child has one durable entity-owned planning handoff after cursor commit and reopen");
        Require(reopened.Cursor("rb") == "observed" && reopened.Live("rb", [child.Path]).Single().Hash == child.Hash,
            "sleeping removal observation and advanced cursor retain the child's current content");
        Require(reopened.DueSyncRetries("p1", deadline, 256).Single().Path == "cache",
            "reviewed removal becomes due at its original deadline without another delta");
    }
}
