namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void RetryDeadlineUsesExactPersistedInstant(string root)
    {
        var directory = Fresh(root, "retry-deadline-ticks");
        // Deliberately non-millisecond aligned, with a non-UTC offset in persisted JSON.
        var deadline = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)).AddTicks(4321);
        using (var memory = new InMemoryStateStore()) Check(memory, seed: true, "memory");
        using (var sql = SqliteStateStore.Open(directory, Fast, new TestClock(), LocalItems.CaptureOptionalHash)) Check(sql, seed: true, "sqlite");
        using (var reopened = SqliteStateStore.Open(directory, Fast, new TestClock(), LocalItems.CaptureOptionalHash)) Check(reopened, seed: false, "reopened");
        using (var control = SqliteStateStore.OpenPauseControl(directory)) Check(control, seed: false, "pause-control");

        void Check(IStateStore store, bool seed, string adapter)
        {
            if (seed)
            {
                store.SavePair(Pair("p1"));
                store.SaveSyncRetry("p1", new("boundary", "sync", 1, "pending", deadline));
                store.SavePair(Pair("p2"));
                // A full candidate page sorts before the truly due row in the millisecond index.
                // Filtering after LIMIT would starve that row, despite its deadline having arrived.
                for (var i = 0; i < 256; i++)
                    store.SaveSyncRetry("p2", new($"a/{i:D4}", "sync", 1, "future", deadline.AddTicks(1)));
                store.SaveSyncRetry("p2", new("z/due", "sync", 1, "ready", deadline));
            }
            Require(store.DueSyncRetries("p1", deadline.AddTicks(-1), 256).Count == 0,
                adapter + ": retry is not due one tick before its persisted deadline");
            var due = store.DueSyncRetries("p1", deadline.ToUniversalTime(), 256);
            Require(due.Count == 1 && due[0].Path == "boundary" && due[0].RetryAfter == deadline,
                adapter + ": retry is due at its exact persisted instant regardless of offset");
            var ordered = store.DueSyncRetries("p2", deadline, 1);
            Require(ordered.Count == 1 && ordered[0].Path == "z/due",
                adapter + ": exact deadline filters same-millisecond future candidates before limit");
        }
    }
}
