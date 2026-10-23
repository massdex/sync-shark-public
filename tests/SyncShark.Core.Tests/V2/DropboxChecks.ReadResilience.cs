using System.Net;
using System.Net.Sockets;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    // The 2026-10-21 incident was DNS/transport, not an HTTP refusal: HttpRequestException carrying a
    // SocketException is the shape that must take the checkpointed retry path.
    private static HttpRequestException DnsTransportFailure() =>
        new("Name or service not known", new SocketException((int)SocketError.HostNotFound));

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
        // F9 pin: the unreadable membership becomes targeted item debt and the delta cursor may advance;
        // a replay of the deletion event is not required and would re-read the same outage.
        Require(!delta.CursorInvalid && delta.CursorAfter == "membership-next",
            "failed membership is per-item debt, so the delta cursor still advances");
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
            sql.SavePair(new PairDef("p1", "Listing fixture", new RootDef("ra", "Local", null, state, "local"),
                Root(), [], [], true));
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
            sql.SavePair(new PairDef("p1", "Listing fixture", new RootDef("ra", "Local", null, state, "local"),
                Root(), [], [], true));
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

    // F3 + the explicit readBudget seam. The inventory passes the long listing budget only for a full or
    // delta page read; item-scoped reads (evidence, hydration) keep the short budget even when a long
    // listing budget is configured.
    private static async Task ReadBudgetScopeAsync()
    {
        using var h = new Harness();
        h.Wire.SeedFolder("Docs");
        h.Wire.SeedChangePage("budget-start", [h.Wire.ChangeFile("budget.txt", "bytes")], false, "budget-next");
        var clock = new ResilienceChecks.Clock();
        var waited = TimeSpan.Zero;
        h.Client.ReadRetryTime = clock;
        h.Client.ReadTransientBudget = TimeSpan.FromMinutes(5);
        h.Client.ListingTransientBudget = TimeSpan.FromMinutes(45);
        h.Client.ReadRetryDelay = (delay, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            clock.Advance(delay);
            waited += delay;
            return Task.CompletedTask;
        };

        h.Wire.RequestException = path => path == "/2/files/list_folder/continue" ? DnsTransportFailure() : null;
        try
        {
            await h.Client.ReadChangePageAsync("budget-start", default, readBudget: TimeSpan.FromSeconds(3));
            throw new InvalidOperationException("Expected the explicit page budget to be exhausted");
        }
        catch (HttpRequestException) { }
        Require(waited > TimeSpan.Zero && waited < TimeSpan.FromMinutes(1),
            "an explicit readBudget bounds one page read");

        waited = TimeSpan.Zero;
        try
        {
            await h.Inventory.ChangesSinceAsync("budget-start", 1, _ => true, default);
            throw new InvalidOperationException("Expected the inventory delta budget to be exhausted");
        }
        catch (HttpRequestException) { }
        Require(waited >= TimeSpan.FromMinutes(40),
            "inventory delta uses the long listing budget, not the five-minute item budget");

        // Hydration is item-scoped: its recursive listing keeps the short budget.
        waited = TimeSpan.Zero;
        h.Wire.RequestException = path => path == "/2/files/list_folder" ? DnsTransportFailure() : null;
        try
        {
            await h.Client.ListEntriesWithDeletedPathsAsync(null, default, readBudget: TimeSpan.FromSeconds(3));
            throw new InvalidOperationException("Expected the explicit full-listing budget to be exhausted");
        }
        catch (HttpRequestException) { }
        Require(waited > TimeSpan.Zero && waited < TimeSpan.FromMinutes(1),
            "an explicit readBudget bounds the full listing page read");

        waited = TimeSpan.Zero;
        try
        {
            await h.Client.HydratePrefixAsync("Docs", _ => Task.CompletedTask, default);
            throw new InvalidOperationException("Expected the hydration budget to be exhausted");
        }
        catch (HttpRequestException) { }
        Require(waited >= TimeSpan.FromMinutes(4) && waited < TimeSpan.FromMinutes(10),
            "hydration keeps the short item budget even when a long listing budget is configured");
        h.Wire.RequestException = null;
        Console.WriteLine("PASS read budget is explicit: listing pages long, hydration short");
    }

    // DNS/transport recovery, cancellation and pause while the retry wait is running.
    private static async Task RetryWaitPauseAndCancelAsync()
    {
        using (var h = new Harness())
        {
            h.Wire.SeedChangePage("dns-start", [h.Wire.ChangeFile("dns.txt", "bytes")], false, "dns-end");
            var clock = new ResilienceChecks.Clock();
            var slices = 0;
            h.Client.ReadRetryTime = clock;
            h.Client.ReadRetryDelay = (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                slices++;
                clock.Advance(delay);
                return Task.CompletedTask;
            };
            var remaining = 3;
            h.Wire.RequestException = path =>
                path == "/2/files/list_folder/continue" && remaining-- > 0 ? DnsTransportFailure() : null;
            var page = await h.Client.ReadChangePageAsync("dns-start", default);
            Require(page.Cursor == "dns-end" && page.Changes.Count == 1 && slices >= 3,
                "DNS/transport outage retries and resumes from the checkpoint");
            h.Wire.RequestException = null;
        }

        using (var h = new Harness())
        {
            h.Wire.SeedChangePage("cancel-start", [], false, "cancel-end");
            using var cancel = new CancellationTokenSource();
            var slices = 0;
            h.Client.ReadRetryTime = new ResilienceChecks.Clock();
            h.Client.ReadRetryDelay = (delay, ct) =>
            {
                slices++;
                cancel.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            };
            h.Wire.RequestException = path => path == "/2/files/list_folder/continue" ? DnsTransportFailure() : null;
            try
            {
                await h.Client.ReadChangePageAsync("cancel-start", cancel.Token);
                throw new InvalidOperationException("Expected caller cancellation during the retry wait");
            }
            catch (OperationCanceledException error) when (error is not PairPausedException) { }
            Require(slices == 1, "cancel during the retry delay stops before the next one-second slice");
            h.Wire.RequestException = null;
        }

        using (var h = new Harness())
        {
            h.Wire.SeedChangePage("pause-start", [], false, "pause-end");
            h.Client.ListingTransientBudget = TimeSpan.FromMinutes(45);
            h.Client.ReadRetryTime = new ResilienceChecks.Clock();
            var paused = false;
            var slices = 0;
            h.Client.ReadRetryDelay = (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                slices++;
                paused = true;
                return Task.CompletedTask;
            };
            h.Wire.RequestException = path => path == "/2/files/list_folder/continue" ? DnsTransportFailure() : null;
            using var pause = PairPause.Begin(h.Known, "p1", () => paused);
            try
            {
                await h.Client.ReadChangePageAsync("pause-start", default, readBudget: TimeSpan.FromMinutes(45));
                throw new InvalidOperationException("Expected the pause during the retry wait");
            }
            catch (PairPausedException) { }
            Require(slices == 1, "pause during the retry wait stops before the next one-second slice");
            h.Wire.RequestException = null;
        }

        Console.WriteLine("PASS retry wait observes pause and cancellation, and DNS outages resume");
    }

    // Production SyncRun + DropboxInventory + SqliteStateStore: a delta outage writes durable whole-root
    // debt; after a database reopen the next run replays the delta from the unadvanced cursor and retires
    // that debt WITHOUT re-listing or hydrating the root, even while the scheduled retry is still sleeping.
    private static async Task DeltaOutageRunRetiresDebtAsync(string state)
    {
        using var h = new Harness();
        var (pair, local) = ReadResiliencePair(state);
        local.Seed("retained.txt", "same");
        h.Wire.SeedFile("retained.txt", "same");
        var spool = state + "-spool";
        var writerA = new CountingWriter(local);

        string cursor;
        using (var sql = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            sql.SavePair(pair);
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, sql, "p1");
            var run = ReadResilienceRun(sql, pair, local, writerA, h.Writer(spool), inventory);
            Require((await FirstSyncAsync(run, pair.PairId)).Failed == 0, "fixture completes first sync");
            cursor = sql.Cursor(RootId) ?? "";
            Require(cursor.Length > 0 && !sql.HasFullListing(Root()), "fixture commits a delta cursor");
            // A real change made during the outage must still replay from the saved cursor on the next run.
            h.Wire.SeedFile("after-outage.txt", "bytes");
            h.Wire.SeedChangePage(cursor, [h.Wire.ChangeExistingFile("after-outage.txt")], false,
                cursor + "-replayed");

            var clock = new ResilienceChecks.Clock();
            h.Client.ListingTransientBudget = TimeSpan.FromMinutes(45);
            h.Client.ReadRetryTime = clock;
            h.Client.ReadRetryDelay = (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                clock.Advance(delay);
                return Task.CompletedTask;
            };
            h.Wire.RequestException = path => path == "/2/files/list_folder/continue" ? DnsTransportFailure() : null;
            var outage = await run.IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            h.Wire.RequestException = null;
            Require(outage.Failed == 0, "a delta transport outage completes the run as durable read debt");
            Require(sql.Cursor(RootId) == cursor, "a delta outage cannot advance the committed cursor");
            Require(sql.SyncRetryAt(pair.PairId, "") is { Kind: "scan" },
                "a delta outage leaves durable whole-root scan debt");
        }

        using var reopened = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System);
        var sleeping = reopened.SyncRetryAt(pair.PairId, "");
        Require(sleeping is not null, "whole-root read debt survives the database reopen");
        // Pin the harder case: the scheduled retry is not due, so it must not be the trigger for the clear.
        reopened.SaveSyncRetry(pair.PairId, sleeping! with { RetryAfter = DateTimeOffset.MaxValue });
        // A named child scope that is still incomplete must not keep the whole-root debt alive either.
        reopened.SaveSyncRetry(pair.PairId,
            new SyncRetry("held-child.txt", "scan", 0, "child scope still incomplete", DateTimeOffset.MaxValue)
                { ReadRoots = [Root().RootId] });
        var listsBefore = h.Wire.ListRequests.Count;
        var callsBefore = h.Wire.Calls.Count;
        var resumedInventory = new DropboxInventory(Root(), Side.B, h.Client, reopened, "p1");
        var resumed = ReadResilienceRun(reopened, pair, local, writerA, h.Writer(spool), resumedInventory);
        var second = await resumed.IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(second.Failed == 0, "the next run replays the delta cleanly");
        Require(h.Wire.ListRequests.Count == listsBefore &&
            !h.Wire.Calls.Skip(callsBefore).Contains("/2/files/list_folder"),
            "debt retirement never re-lists or hydrates the Dropbox root");
        Require(reopened.SyncRetryAt(pair.PairId, "") is null,
            "whole-root debt clears after the cursor replay even while the scheduled retry sleeps");
        Require(reopened.SyncRetryAt(pair.PairId, "held-child.txt") is { Kind: "scan" },
            "a named incomplete child scope is untouched while the root debt clears");
        Require(reopened.Cursor(RootId) != cursor, "the replay advances the delta cursor");
        Console.WriteLine("PASS delta outage run retires root debt without a re-list after reopen");
    }

    // Follow-up 2d: an edit made on the healthy local side while the remote root is unreadable must
    // still be synced after recovery. The recovery run's own local delta is empty (the outage run
    // ingested and advanced it), so the edit returns to planning only because the settling whole-root
    // retry expands the touched scope to the stored rows.
    private static async Task HealthyEditDuringOutageSyncsAfterRecoveryAsync(string state)
    {
        using var h = new Harness();
        var (pair, local) = ReadResiliencePair(state);
        local.Seed("retained.txt", "same");
        h.Wire.SeedFile("retained.txt", "same");
        var spool = state + "-spool";
        var writerA = new CountingWriter(local);

        string cursor;
        using (var sql = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            sql.SavePair(pair);
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, sql, "p1");
            var run = ReadResilienceRun(sql, pair, local, writerA, h.Writer(spool), inventory);
            Require((await FirstSyncAsync(run, pair.PairId)).Failed == 0, "healthy-edit fixture completes first sync");
            cursor = sql.Cursor(RootId) ?? "";
            Require(cursor.Length > 0, "healthy-edit fixture commits a remote cursor");
            // The healthy side gains a file while the remote root is unreadable.
            local.Seed("healthy-edit.txt", "bytes");
            var clock = new ResilienceChecks.Clock();
            h.Client.ReadRetryTime = clock;
            h.Client.ReadRetryDelay = (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                clock.Advance(delay);
                return Task.CompletedTask;
            };
            h.Wire.RequestException = path => path == "/2/files/list_folder/continue" ? DnsTransportFailure() : null;
            var outage = await run.IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            h.Wire.RequestException = null;
            Require(outage.Failed == 0 && sql.SyncRetryAt(pair.PairId, "") is { Kind: "scan" },
                "the remote outage finishes as durable whole-root read debt");
            Require(sql.Live(pair.A.RootId, ["healthy-edit.txt"]).Count == 1,
                "the outage run stored the healthy side's edit");
            Require(await h.Client.FindEntryAtPathAsync("healthy-edit.txt", default) is null,
                "the healthy side's edit is not uploaded while the remote root is unreadable");
            // The outage shortened the remote feed; the next real feed replays from the saved cursor.
            h.Wire.SeedChangePage(cursor, [], false, cursor + "-recovered");
        }
        using var reopened = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System);
        var resumedInventory = new DropboxInventory(Root(), Side.B, h.Client, reopened, "p1");
        var resumed = ReadResilienceRun(reopened, pair, local, writerA, h.Writer(spool), resumedInventory);
        var recovery = await resumed.IncrementalAsync(pair.PairId, new RecordingProgress(), default);
        Require(recovery.Failed == 0, "the recovery run completes");
        Require(reopened.SyncRetryAt(pair.PairId, "") is null, "the recovery run settles the whole-root debt");
        Require(await h.Client.FindEntryAtPathAsync("healthy-edit.txt", default) is not null,
            "the healthy side's earlier edit is uploaded after recovery");
        Console.WriteLine("PASS healthy-side edit during a remote outage syncs after recovery");
    }

    // Adversarial deletion safety: the local side deletes X while the Dropbox delta is unavailable. An
    // outage-shortened remote read is not absence authority, so nothing is deleted remotely.
    private static async Task AdversarialDeleteDuringDeltaOutageAsync(string state)
    {
        using var h = new Harness();
        var (pair, local) = ReadResiliencePair(state);
        local.Seed("x.txt", "same");
        h.Wire.SeedFile("x.txt", "same");
        var spool = state + "-spool";
        var writerA = new CountingWriter(local);

        string cursor;
        using (var sql = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            sql.SavePair(pair);
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, sql, "p1");
            var run = ReadResilienceRun(sql, pair, local, writerA, h.Writer(spool), inventory);
            Require((await FirstSyncAsync(run, pair.PairId)).Failed == 0, "adversarial fixture completes first sync");
            cursor = sql.Cursor(RootId) ?? "";

            // The user deletes X locally, outside this run's apply path.
            var original = (await local.ObserveAsync("x.txt", default))!;
            await local.DeleteAsync(new SyncAction("local-delete-x", 6, ActionKind.Delete, Side.A, "x.txt",
                null, null, original, false, null), default);
            Require(await local.ObserveAsync("x.txt", default) is null, "local X is deleted before the run");

            var clock = new ResilienceChecks.Clock();
            h.Client.ReadRetryTime = clock;
            h.Client.ReadRetryDelay = (delay, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                clock.Advance(delay);
                return Task.CompletedTask;
            };
            var deletesBefore = h.Wire.Calls.Count(call => call is "/2/files/delete_v2" or "/2/files/delete");
            h.Wire.RequestException = path => path == "/2/files/list_folder/continue" ? DnsTransportFailure() : null;
            var outage = await run.IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            h.Wire.RequestException = null;
            Require(outage.Failed == 0 && outage.Deleted == 0,
                "a local delete never executes while the Dropbox delta is unavailable");
            Require(h.Wire.Calls.Count(call => call is "/2/files/delete_v2" or "/2/files/delete") == deletesBefore,
                "no remote delete request is issued during the outage");
            Require(sql.Cursor(RootId) == cursor, "the outage leaves the remote cursor unchanged");
            Require(sql.SyncRetryAt(pair.PairId, "") is { Kind: "scan" },
                "the outage leaves durable whole-root debt protecting remote absence");
        }
        Require(await h.Client.FindEntryAtPathAsync("x.txt", default) is not null,
            "the remote X still exists after an outage-shortened listing");
        Console.WriteLine("PASS adversarial local delete during a delta outage has no remote effect");
    }

    // F9 pin at run level: a failed membership read on a deleted path advances the cursor, preserves the
    // item's durable retry debt, and never issues a delete.
    private static async Task MembershipFailureKeepsItemRetryAsync(string state)
    {
        using var h = new Harness();
        var (pair, local) = ReadResiliencePair(state);
        // SeedFile does not create ancestors. The real inventory discovers nested files
        // through its top-level folder listing, so both endpoints need the parent too.
        local.Seed("Docs", "", ItemKind.Folder);
        local.Seed("Docs/A.txt", "same");
        h.Wire.SeedFolder("Docs");
        h.Wire.SeedFile("Docs/A.txt", "same");
        var spool = state + "-spool";
        var writerA = new CountingWriter(local);

        using (var sql = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System))
        {
            sql.SavePair(pair);
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, sql, "p1");
            var run = ReadResilienceRun(sql, pair, local, writerA, h.Writer(spool), inventory);
            Require((await FirstSyncAsync(run, pair.PairId)).Failed == 0, "membership fixture completes first sync");
            var cursor = sql.Cursor(RootId) ?? "";
            var item = (await h.Client.FindEntryAtPathAsync("Docs/A.txt", default))!;
            var stored = sql.Live(RootId, ["Docs/A.txt"]);
            Require(stored.Count == 1 && stored[0].ObjectId == item.Id && stored[0].Path == "Docs/A.txt",
                "membership fixture stores the remote identity and display spelling before the failed read");
            var metadataReadsBefore = h.Wire.MetadataPaths.Count(path => path == item.Id);

            h.Wire.SeedChangePage(cursor, [h.Wire.ChangeDeletedLower("/pilot/docs/a.txt")], false,
                "membership-run-next");
            UseFastHistoryRetryClock(h);
            h.Wire.MetadataFailurePath = item.Id;
            var deletesBefore = h.Wire.Calls.Count(call => call is "/2/files/delete_v2" or "/2/files/delete");
            var report = await run.IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            h.Wire.MetadataFailurePath = null;

            Require(report.Failed == 0 && report.Deleted == 0,
                "a failed membership read is item debt, not a run failure or a delete");
            Require(sql.Cursor(RootId) == "membership-run-next",
                "failed membership advances the delta cursor (deletion event is not replayed)");
            Require(sql.ReadSyncRetries(pair.PairId).Any(row => PairPath.Same(row.Path, "Docs/A.txt")),
                "the affected item keeps its durable targeted retry");
            Require(h.Wire.MetadataPaths.Count(path => path == item.Id) > metadataReadsBefore,
                "the delta exercises the failed object-membership read");
            Require(sql.Live(RootId, ["Docs/A.txt"]).Count == 1,
                "the stored item row is preserved, never treated as absence");
            Require(sql.Live(RootId, ["Docs/A.txt"]).Single() == stored[0],
                "the failed membership read preserves the exact stored item state");
            Require(h.Wire.Calls.Count(call => call is "/2/files/delete_v2" or "/2/files/delete") == deletesBefore,
                "a failed membership read issues no delete request");
        }
        Require(await h.Client.FindEntryAtPathAsync("Docs/A.txt", default) is not null,
            "the remote item still exists after a failed membership read");
        Console.WriteLine("PASS failed membership advances the cursor and preserves item retry without deleting");
    }

    private static (PairDef Pair, FakeEndpoint Local) ReadResiliencePair(string localPath)
    {
        var pair = new PairDef("p1", "read resilience",
            new RootDef("ra", SyncProviders.Local, null, localPath, "id:ra"),
            Root(), [], [], true);
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        return (pair, local);
    }

    private static SyncRun ReadResilienceRun(IStateStore store, PairDef pair, FakeEndpoint local,
        CountingWriter writerA, IEndpointWriter writerB, IInventorySource inventoryB)
    {
        var settings = V2Settings.Default with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };
        var provider = new FakeEndpointProvider().Add(pair.A, local)
            .AddWriter(pair.A, writerA).AddWriter(pair.B, writerB)
            .AddInventory(pair.B, inventoryB);
        return new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage), provider,
            settings, TimeProvider.System);
    }

    private static async Task<RunReport> FirstSyncAsync(SyncRun run, string pairId)
    {
        var plan = await run.PreviewFirstSyncAsync(pairId, new RecordingProgress(), default);
        return await run.ApplyAsync(pairId, plan, new RecordingProgress(), default);
    }
}
