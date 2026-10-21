using System.Net;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task MembershipTimeoutIsPerItemAsync()
    {
        using var h = new Harness();
        h.Wire.SeedFile("Docs/A.txt", "same");
        var item = (await h.Client.FindEntryAtPathAsync("Docs/A.txt", default))!;
        SeedStored(h, Stored("Docs/A.txt", item.Id!, "hash"));
        h.Wire.SeedChangePage("membership", [h.Wire.ChangeDeletedLower("/pilot/docs/a.txt"),
            h.Wire.ChangeFile("healthy.txt", "bytes")], false, "membership-next");
        UseFastHistoryRetryClock(h);
        h.Wire.MetadataFailurePath = item.Id;
        var delta = await h.Inventory.ChangesSinceAsync("membership", 2, _ => true, default);
        Require(delta.IncompleteSubtrees.Contains("docs/a.txt", StringComparer.OrdinalIgnoreCase) &&
            delta.IncompleteReasons.Count == 1, "metadata timeout protects only its path with the exact reason");
        Require(delta.Changes.All(change => change.Kind != ChangeKind.Delete) &&
            delta.Changes.Any(change => change.Path == "healthy.txt"), "failed metadata is not absence; healthy changes survive");
        Console.WriteLine("PASS membership metadata timeout is per-item incomplete read");
    }

    private static async Task DeltaNetworkOutageAsync(string state)
    {
        using var h = new Harness();
        h.Wire.SeedChangePage("outage-start", [h.Wire.ChangeFile("first.txt", "one")], true, "outage-middle");
        h.Wire.SeedChangePage("outage-middle", [h.Wire.ChangeFile("second.txt", "two")], false, "outage-end");
        UseFastHistoryRetryClock(h);
        h.Wire.AfterContinueResponse = cursor =>
        {
            if (cursor == "outage-start") h.Wire.Script("/2/files/list_folder/continue", HttpStatusCode.ServiceUnavailable, 3);
        };
        using (var sql = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, sql, "p1");
            try
            {
                await inventory.ChangesSinceAsync("outage-start", 1, _ => true, default);
                throw new InvalidOperationException("Expected delta outage exhaustion");
            }
            catch (DropboxApiException error) when (error.StatusCode == HttpStatusCode.ServiceUnavailable) { }
            Require(sql.Live(RootId).Count == 0, "exhausted delta cannot publish partial inventory");
        }
        h.Wire.AfterContinueResponse = null;
        h.Client.ListingTransientBudget = TimeSpan.FromMinutes(45);
        using var reopened = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System);
        var resumed = new DropboxInventory(Root(), Side.B, h.Client, reopened, "p1");
        var delta = await resumed.ChangesSinceAsync("outage-start", 2, _ => true, default);
        Require(!delta.CursorInvalid && delta.CursorAfter == "outage-end" && delta.Changes.Count == 2,
            "exhausted delta resumes after database reopen without losing changes");
        Require(h.Wire.ContinueCursors.Count(cursor => cursor == "outage-start") == 1,
            "saved delta page is never refetched after the outage");
        Console.WriteLine("PASS delta outage checkpoint survives reopen");
    }

    private static async Task ListingNetworkOutageAsync(string state, bool exhaust)
    {
        using var h = new Harness();
        h.Wire.DeletedPageSize = 2;
        for (var i = 0; i < 7; i++) h.Wire.SeedFile($"outage-{i}.txt", "bytes");
        var clock = new ResilienceChecks.Clock();
        var waited = TimeSpan.Zero;
        h.Client.ReadRetryTime = clock;
        h.Client.ListingTransientBudget = exhaust ? TimeSpan.FromSeconds(3) : TimeSpan.FromMinutes(45);
        h.Client.ReadRetryDelay = (delay, token) =>
        {
            token.ThrowIfCancellationRequested();
            clock.Advance(delay);
            waited += delay;
            return Task.CompletedTask;
        };
        // Twenty continued-page refusals span over five simulated minutes at the 30s backoff ceiling.
        h.Wire.Script("/2/files/list_folder/continue", HttpStatusCode.ServiceUnavailable, exhaust ? 3 : 20);
        using (var sql = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, sql, "p1");
            if (exhaust)
            {
                try
                {
                    await inventory.FullAsync(1, _ => true, Sink, default);
                    throw new InvalidOperationException("Expected exhausted listing");
                }
                catch (DropboxApiException error) when (error.StatusCode == HttpStatusCode.ServiceUnavailable) { }
                Require(sql.HasFullListing(Root()) && sql.Live(RootId).Count == 0,
                    "exhausted listing retains durable pages without installing partial absence authority");
            }
            else
            {
                var snapshot = await inventory.FullAsync(1, _ => true, Sink, default);
                Require(snapshot.Items.Count == 7 && snapshot.IncompleteSubtrees.Count == 0,
                    "five-minute outage resumes all pages exactly once");
                Require(waited >= TimeSpan.FromMinutes(5), "outage simulation spans at least five minutes");
            }
        }
        if (exhaust)
        {
            h.Client.ListingTransientBudget = TimeSpan.FromMinutes(45);
            using var reopened = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System);
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, reopened, "p1");
            var snapshot = await inventory.FullAsync(2, _ => true, Sink, default);
            Require(snapshot.Items.Count == 7 && snapshot.IncompleteSubtrees.Count == 0,
                "reopened checkpoint resumes after exhausted budget");
        }
        Require(h.Wire.ListRequests.Count == 1, "network outage never refetches the initial listing page");
        Console.WriteLine(exhaust ? "PASS listing outage checkpoint survives reopen" : "PASS five-minute listing outage resumes");
    }
}
