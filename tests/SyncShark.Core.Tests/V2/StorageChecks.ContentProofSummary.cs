namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void ContentProofAttemptHasOneOwner(string root)
    {
        using var store = SqliteStateStore.Open(Fresh(root, "content-proof-attempt"), Fast, new TestClock());
        store.SavePair(Pair("p1"));
        var first = FailureRetryOwner.SaveFailedRetry(store, "p1", "item.txt", "Hash: side A unavailable", null, T0);
        var second = FailureRetryOwner.SaveFailedRetry(store, "p1", "item.txt", "Hash: side B unavailable",
            store.SyncRetryAt("p1", "item.txt"), first.RetryAfter.AddTicks(-1));
        var parked = FailureRetryOwner.ReportedFailure(second, first.RetryAfter.AddTicks(-1), retainSleeping: true);
        Require(first.Attempts == 1 && second.Attempts == 1 && parked.Attempts == 1 &&
            second.RetryAfter == first.RetryAfter && parked.RetryAfter == first.RetryAfter &&
            second.LastError == "Hash: side B unavailable",
            "both content sides and parking share one attempt and unchanged deadline while refreshing cause");
        var next = FailureRetryOwner.SaveFailedRetry(store, "p1", "item.txt", "Hash: unavailable again",
            store.SyncRetryAt("p1", "item.txt"), first.RetryAfter);
        Require(next.Attempts == 2 && next.RetryAfter == first.RetryAfter.AddSeconds(20),
            "a new content failure at the exact deadline advances once with growing backoff");
        var nextSide = FailureRetryOwner.SaveFailedRetry(store, "p1", "item.txt", "Hash: second side again", next,
            first.RetryAfter.AddTicks(1));
        Require(nextSide.Attempts == 2 && nextSide.RetryAfter == next.RetryAfter &&
            FailureRetryOwner.ReportedFailure(nextSide, first.RetryAfter.AddTicks(1), retainSleeping: true) ==
                new FailureRetryOwner.State(2, next.RetryAfter),
            "second-side report and parking cannot recount the later scheduled attempt");
    }

    private static void ContentProofSummaryIncludesSleepingDebt(string root)
    {
        ContentProofAttemptHasOneOwner(root);
        var directory = Fresh(root, "content-proof-summary");
        var causes = new[] { "Hash: unreadable", "Hash deferred after repeated transport failures: unavailable",
            "Content proof is unavailable, paused, changed, or deferred to indexing; the item remains unsynced." };
        using (var store = SqliteStateStore.Open(directory, Fast, new TestClock()))
        {
            store.SavePair(Pair("p1"));
            for (var i = 0; i < 50; i++)
            {
                var path = $"bad-{i:D2}.txt";
                if (i < 25)
                {
                    var saved = FailureRetryOwner.SaveFailedRetry(store, "p1", path, causes[i % causes.Length], null, T0);
                    Require(saved.FailureKind == FailureRetryOwner.FailureKind.ContentProof,
                        "new content-proof failures persist their typed classification");
                }
                else store.SaveSyncRetry("p1", new(path, "sync", 1, causes[i % causes.Length], T0.AddHours(1)));
            }
            store.SaveSyncRetry("p1", new("other.txt", "sync", 1, "Destination disk full", T0.AddHours(1)));
            store.SaveSyncRetry("p1", new("explicit-other.txt", "sync", 1, causes[0], T0.AddHours(1))
                { FailureKind = FailureRetryOwner.FailureKind.Other });
        }
        using var reopened = SqliteStateStore.Open(directory, Fast, new TestClock());
        var before = reopened.ReadSyncRetries("p1").ToArray();
        var fresh = Enumerable.Range(45, 10).Select(i => $"bad-{i:D2}.txt").Reverse().ToArray();
        var summary = SyncRun.SummarizeContentUnconfirmed(fresh, reopened.ReadSyncRetries("p1"));
        Require(summary.Count == 55 && summary.Examples.SequenceEqual(Enumerable.Range(0, 50).Select(i => $"bad-{i:D2}.txt")),
            "persisted sleeping content debt plus fresh plan reports full union and ordinal top fifty");
        Require(reopened.DueSyncRetries("p1", T0, 256).Count == 0 &&
            before.SequenceEqual(reopened.ReadSyncRetries("p1")),
            "content reporting never changes sleeping debt, deadlines, attempts or typed causes");
        Require(reopened.SyncRetryAt("p1", "bad-00.txt")!.FailureKind == FailureRetryOwner.FailureKind.ContentProof &&
            reopened.SyncRetryAt("p1", "bad-25.txt")!.FailureKind is null,
            "typed classification survives reopen while legacy rows use the owner fallback without migration");
    }
}
