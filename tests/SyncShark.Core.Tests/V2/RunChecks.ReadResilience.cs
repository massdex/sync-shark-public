namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ReadOutageCompletesSafelyAsync()
    {
        foreach (var full in new[] { false, true })
        {
            var fx = Build();
            fx.A.Seed("retained.txt", "same");
            fx.B.Seed("retained.txt", "same");
            await FirstSyncAsync(fx.Run(), fx);
            var cursor = fx.Store.Cursor(fx.Pair.B.RootId);
            var inventory = new ScriptedInventory(fx.B)
            {
                ChangesError = new HttpRequestException("network unavailable"),
                FullError = new ProviderTransportTimeoutException("response body stalled")
            };
            fx.Provider.AddInventory(fx.Pair.B, inventory);
            if (full) await fx.Run().RequestRescanAsync([fx.Pair.B.RootId], default);
            var writes = fx.WriterA.Calls + fx.WriterB.Calls;
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "") is { Kind: "scan" },
                "exhausted read ends cleanly with durable root read debt");
            Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "sync-retry"),
                "exhausted read has an actionable notice");
            Require(fx.WriterA.Calls + fx.WriterB.Calls == writes,
                "incomplete root read cannot authorize an absence delete or other effect");
            if (!full) Require(fx.Store.Cursor(fx.Pair.B.RootId) == cursor, "partial delta cannot advance cursor");
            inventory.ChangesError = inventory.FullError = null;
            var retry = fx.Store.SyncRetryAt(fx.Pair.PairId, "")!;
            fx.Store.SaveSyncRetry(fx.Pair.PairId, retry with { RetryAfter = DateTimeOffset.MinValue });
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "") is null, "next complete read retires debt");
        }
        Console.WriteLine("PASS read outage completes safely and resumes next run");
    }

    private static async Task FirstSyncOutageRetainsReadDebtAsync()
    {
        var fx = Build();
        fx.A.Seed("retained.txt", "bytes");
        fx.Provider.AddInventory(fx.Pair.B, new ScriptedInventory(fx.B)
            { FullError = new HttpRequestException("network unavailable") });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var plan = await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(plan.RunUnavailable is not null && plan.Actions.Count == 0,
                "incomplete first sync returns a pending non-applicable plan");
            Require(!fx.Store.FirstSyncDone(fx.Pair.PairId), "incomplete first sync never completes the pair");
            Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "") is { Kind: "scan" }, "first-sync read debt survives");
        }
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == "sync-retry") == 1,
            "repeated first-sync outage has one deduped retry notice");
        Require(fx.WriterA.Calls + fx.WriterB.Calls == 0, "pending first sync authorizes no effects");
        Console.WriteLine("PASS first-sync outage retains read debt without apply authority");
    }

    private static async Task ItemReadTimeoutBecomesRetryAsync()
    {
        var fx = Build();
        fx.A.Seed("unreadable.txt", "bytes");
        fx.A.Seed("healthy.txt", "other bytes");
        fx.Provider.AddInventory(fx.Pair.A, new ScriptedInventory(fx.A)
        {
            Hashless = true, ThrowHashPath = "unreadable.txt",
            HashError = new ProviderTransportTimeoutException("metadata read timed out")
        });
        var plan = await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "unreadable.txt") is not null,
            "single metadata timeout is durable per-item retry, not run failure");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.Path == "unreadable.txt"),
            "single metadata timeout has its item notice");
        Require(plan.Actions.Any(action => action.Path == "healthy.txt"), "healthy item still plans");
        Require(!plan.Actions.Any(action => action.Path == "unreadable.txt" && action.Kind != ActionKind.Hold),
            "unreadable item stays unsynced");
        Console.WriteLine("PASS item read timeout becomes retry without failing healthy work");
    }
}
