using System.Net;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    // RQ-030/RQ-140: a durable prepare is a change-tracking checkpoint, not permission to repeat a full walk.
    // Exercise the public CLI boundary so the pending cursor must survive both inventory and SQLite reopen.
    public static async Task RunCursorChecksAsync(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh bounded test directory.");
        Directory.CreateDirectory(root);
        _root = root;
        await PrepareCursorChecksAsync();
        Console.WriteLine("--v2-cursor-checks passed.");
    }

    private static async Task PrepareCursorChecksAsync()
    {
        await CursorClassificationChecksAsync();
        CursorKeepPairScope();
        await CursorKeepHistoricalFolderAsync();
        await CursorKeepReprepareAsync();
        await CursorKeepManyReleasedAsync();
        await CursorKeepInterruptedHeldAsync();
        await PrepareCursorResumeAsync("unchanged");
        await PrepareCursorResumeAsync("remote-add");
        await PrepareCursorResumeAsync("local-delete");
        await PrepareCursorResumeAsync("reset");
        await PrepareCursorResumeAsync("failed-apply");
        await PrepareCursorWidenedScopeAsync("excluded");
        await PrepareCursorWidenedScopeAsync("ignore");
        await PrepareCursorWidenedScopeAsync("nested-root");
        await PrepareCursorLegacyOrRescanAsync(false);
        await PrepareCursorLegacyOrRescanAsync(true);
        await PrepareCursorDeletionFallbackAsync(false);
        await PrepareCursorDeletionFallbackAsync(true);
        await PermanentEvidenceCallbackChecksAsync();
        await PrepareCursorDeferredAsync();
        await PrepareCursorRefreshRaceAsync();
    }

    private static async Task PrepareCursorResumeAsync(string scenario)
    {
        using var h = new Harness();
        var productRoot = Path.Combine(_root, "prepare-cursor-" + scenario);
        var stateRoot = Path.Combine(productRoot, "state");
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        var original = local.Seed("matched.txt", "same");
        h.Wire.SeedFile("matched.txt", "same");
        var pair = new PairDef("p1", "prepare cursor " + scenario,
            new RootDef("ra", SyncProviders.Local, null, "C:\\offline-prepare-cursor-" + scenario, "id:ra"),
            Root(), [], [], true);
        const string preparedCursor = "prepare-before-list";
        const string appliedCursor = "apply-after-delta";
        const string nextCursor = "next-incremental";
        h.Wire.LatestCursor = preparedCursor;
        h.Wire.SeedChangePage(preparedCursor, [], false, preparedCursor);

        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
        {
            store.SavePair(pair);
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
            Require(!store.FirstSyncDone(pair.PairId) && string.IsNullOrEmpty(store.Cursor(RootId)),
                scenario + ": prepare must not promote a pending cursor to the successful root cursor");
            Require(h.Wire.Calls.IndexOf("/2/files/list_folder/get_latest_cursor") >= 0 &&
                h.Wire.Calls.IndexOf("/2/files/list_folder/get_latest_cursor") <
                h.Wire.Calls.IndexOf("/2/files/list_folder"),
                scenario + ": prepare captures its cursor before listing, never after");
        }

        if (scenario is "remote-add" or "reset" or "failed-apply")
        {
            h.Wire.SeedFile("after-prepare.txt", "new after prepare");
            h.Wire.SeedChangePage(preparedCursor, [h.Wire.ChangeExistingFile("after-prepare.txt")],
                false, appliedCursor);
        }
        else
        {
            h.Wire.SeedChangePage(preparedCursor, [], false, appliedCursor);
        }
        if (scenario == "local-delete")
        {
            // D71: this was matched at prepare; deletion afterwards is ordinary sync, not an unreviewed
            // first-sync remove. Retaining the cursor must not drop the prepare's matched-path baseline.
            await local.DeleteAsync(new SyncAction("post-prepare-delete", 6, ActionKind.Delete, Side.A,
                "matched.txt", null, null, original, false, null), CancellationToken.None);
        }
        if (scenario == "reset")
        {
            h.Wire.Script("/2/files/list_folder/continue", HttpStatusCode.Conflict, 1);
            h.Wire.LatestCursor = "fallback-before-list";
            h.Wire.SeedChangePage("fallback-before-list", [], false, "fallback-before-list");
        }
        if (scenario == "failed-apply")
        {
            var feedsBeforeFailure = h.Wire.ContinueCursors.Count;
            var listingsBeforeFailure = h.Wire.ListRequests.Count;
            using var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System);
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            var reviewedBefore = FirstSyncChoicesState.Load(productRoot, pair.PairId, store)
                ?? throw new InvalidOperationException("Missing prepared review before failed apply.");
            var headerBefore = store.ReadPrepare(pair.PairId);
            var matchedRowsBefore = store.CountPrepareRows(headerBefore!.Generation, PrepareDatabase.Matches);
            local.FailNextWrites = 1;
            var failed = await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { },
                CancellationToken.None);
            Require(failed.Failed == 1 && local.FailNextWrites == 0 && !store.FirstSyncDone(pair.PairId) &&
                string.IsNullOrEmpty(store.Cursor(RootId)) &&
                await local.ObserveAsync("after-prepare.txt", CancellationToken.None) is null,
                "a failed executor effect leaves first sync unfinished and does not commit the refreshed cursor");
            Require(h.Wire.ContinueCursors.Skip(feedsBeforeFailure).FirstOrDefault() == preparedCursor &&
                h.Wire.ListRequests.Count == listingsBeforeFailure &&
                store.Live(RootId, ["after-prepare.txt"]).Count == 1,
                "the interrupted apply had already consumed the delta and persisted its inventory before failure");
            var reviewedAfter = FirstSyncChoicesState.Load(productRoot, pair.PairId, store)
                ?? throw new InvalidOperationException("Failed apply discarded the prepared review.");
            Require(reviewedBefore.Database is not null && reviewedAfter.Database is not null &&
                headerBefore == store.ReadPrepare(pair.PairId) &&
                matchedRowsBefore == store.CountPrepareRows(headerBefore.Generation, PrepareDatabase.Matches) &&
                store.PrepareCursorForPair(pair.PairId, RootId) is not null &&
                !File.Exists(Path.Combine(productRoot, "v2", "first-sync-p1.choices.json")),
                "pending refresh updates v6 cursor rows only: same generation, metadata and lazy matches; no JSON rewrite");
            Require(System.Text.Json.JsonSerializer.Serialize(reviewedBefore with
                { InventoryCursors = [], InventoryScope = null }) ==
                System.Text.Json.JsonSerializer.Serialize(reviewedAfter with
                { InventoryCursors = [], InventoryScope = null }),
                "refreshing pending cursors preserves the entire frozen review, including digest, matches and deletes");
            Require(reviewedAfter.InventoryCursors.Any(entry => entry.RootId == RootId && entry.Cursor == appliedCursor),
                "a failed effect retains the successfully refreshed inventory cursor inside the pending prepare");
            h.Wire.SeedChangePage(appliedCursor, [], false, appliedCursor);
            // The next block reopens SQLite and rebuilds runtime/inventory. Its first feed resumes the
            // refreshed PENDING cursor, while the successful root cursor must remain uncommitted.
        }
        var listingsBeforeApply = h.Wire.ListRequests.Count;
        var callsBeforeApply = h.Wire.Calls.Count;
        var feedsBeforeApply = h.Wire.ContinueCursors.Count;
        string committedCursor;
        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
        {
            // No prepare inventory, run, runtime or open store survives this boundary.
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            if (scenario == "unchanged")
            {
                var refused = false;
                try
                {
                    await runtime.ApplyFirstSyncAsync(pair.PairId, """{"matched.txt":"no-change"}""",
                        false, null, _ => { }, CancellationToken.None);
                }
                catch (SyncCliReviewException) { refused = true; }
                Require(refused && h.Wire.Calls.Count == callsBeforeApply &&
                    string.IsNullOrEmpty(store.Cursor(RootId)) && !store.FirstSyncDone(pair.PairId),
                    "retaining a prepare cursor never bypasses choice binding or promotes a refused apply");
            }
            var applied = await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { },
                CancellationToken.None);
            Require(applied.Failed == 0 && store.FirstSyncDone(pair.PairId),
                scenario + ": resumed CLI apply completes successfully");
            Require(h.Wire.ContinueCursors.Skip(feedsBeforeApply).FirstOrDefault() ==
                (scenario == "failed-apply" ? appliedCursor : preparedCursor),
                scenario + ": resumed apply consumes the latest durable pending prepare cursor first");
            if (scenario == "reset")
            {
                var calls = h.Wire.Calls.Skip(callsBeforeApply).ToList();
                Require(h.Wire.ListRequests.Count > listingsBeforeApply &&
                    calls.IndexOf("/2/files/list_folder/continue") <
                    calls.IndexOf("/2/files/list_folder/get_latest_cursor") &&
                    calls.IndexOf("/2/files/list_folder/get_latest_cursor") <
                    calls.IndexOf("/2/files/list_folder"),
                    "expired/reset prepare cursor falls back to a fresh cursor-then-list snapshot");
            }
            else
            {
                Require(h.Wire.ListRequests.Count == listingsBeforeApply &&
                    !h.Wire.Calls.Skip(callsBeforeApply).Contains("/2/files/list_folder/get_latest_cursor"),
                    scenario + ": resumed apply uses delta, with no fresh full listing or latest-cursor capture");
            }
            if (scenario is "remote-add" or "reset" or "failed-apply")
            {
                Require(await local.ObserveAsync("after-prepare.txt", CancellationToken.None) is not null &&
                    store.Live(RootId, ["after-prepare.txt"]).Count == 1,
                    scenario + ": the postprepare Dropbox addition reaches local and the retained inventory");
            }
            if (scenario == "local-delete")
            {
                Require(await h.Writer().ObserveAsync("matched.txt", CancellationToken.None) is null &&
                    await local.ObserveAsync("matched.txt", CancellationToken.None) is null,
                    "D71 postprepare deletion is ordinary sync, not resurrected or refused by review guards");
            }
            committedCursor = store.Cursor(RootId) ?? "";
            Require(committedCursor == (scenario == "reset" ? "fallback-before-list" : appliedCursor),
                scenario + ": successful apply commits its refreshed root cursor");
        }

        h.Wire.SeedChangePage(committedCursor, [], false, nextCursor);
        var listingsBeforeNext = h.Wire.ListRequests.Count;
        var feedsBeforeNext = h.Wire.ContinueCursors.Count;
        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
        {
            Require(store.Cursor(RootId) == committedCursor,
                scenario + ": successful root cursor survives a second SQLite reopen");
            var (_, run) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            var next = await run.IncrementalAsync(pair.PairId, new Progress<RunProgress>(), CancellationToken.None);
            Require(next.Failed == 0 && h.Wire.ListRequests.Count == listingsBeforeNext &&
                h.Wire.ContinueCursors.Skip(feedsBeforeNext).FirstOrDefault() == committedCursor &&
                store.Cursor(RootId) == nextCursor,
                scenario + ": the very next incremental consumes and advances the committed cursor without listing");
        }
    }

    private static async Task PrepareCursorRefreshRaceAsync()
    {
        using var h = new Harness();
        var productRoot = Path.Combine(_root, "prepare-cursor-refresh-race");
        var stateRoot = Path.Combine(productRoot, "state");
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        h.Wire.SeedFile("queued.txt", "download will fail once");
        var pair = new PairDef("p1", "prepare cursor refresh race",
            new RootDef("ra", SyncProviders.Local, null, "C:\\offline-prepare-refresh-race", "id:ra"),
            Root(), [], [], true);
        h.Wire.LatestCursor = "race-prepare";
        h.Wire.SeedChangePage("race-prepare", [], false, "race-prepare");
        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
        {
            store.SavePair(pair);
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
        }

        h.Wire.Script("/2/files/list_folder/continue", HttpStatusCode.Conflict, 1);
        h.Wire.LatestCursor = "race-before-list";
        h.Wire.SeedChangePage("race-before-list", [], false, "race-list-drained");
        var injected = false;
        h.Wire.AfterContinueResponse = cursor =>
        {
            if (cursor != "race-before-list") return;
            h.Wire.AfterContinueResponse = null;
            // Full fallback has already listed queued.txt and captured its final replay response.
            // Add content after both reads but BEFORE the pending prepare can be refreshed.
            injected = true;
            h.Wire.SeedFile("between-list-and-refresh.txt", "must not be skipped");
            h.Wire.LatestCursor = "race-too-late";
            h.Wire.SeedChangePage("race-too-late", [], false, "race-too-late");
            h.Wire.SeedChangePage("race-list-drained",
                [h.Wire.ChangeExistingFile("between-list-and-refresh.txt")], false, "race-retried");
        };
        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
        {
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            local.FailNextWrites = 1;
            var failed = await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { },
                CancellationToken.None);
            var pending = FirstSyncChoicesState.Load(productRoot, pair.PairId, store);
            Require(injected && failed.Failed == 1 && !store.FirstSyncDone(pair.PairId) &&
                string.IsNullOrEmpty(store.Cursor(RootId)) &&
                pending is not null && pending.InventoryCursors.Any(entry =>
                    entry.RootId == RootId && entry.Cursor == "race-list-drained"),
                "failed fallback apply checkpoints its last fully drained cursor, never a late latest cursor");
            Require(store.Live(RootId, ["between-list-and-refresh.txt"]).Count == 0 &&
                await local.ObserveAsync("between-list-and-refresh.txt", CancellationToken.None) is null,
                "race fixture inserted the remote change after snapshot capture, not into the failed plan");
        }
        var feedsBefore = h.Wire.ContinueCursors.Count;
        var listsBefore = h.Wire.ListRequests.Count;
        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
        {
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            var retried = await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { },
                CancellationToken.None);
            Require(retried.Failed == 0 && store.FirstSyncDone(pair.PairId) &&
                h.Wire.ContinueCursors.Skip(feedsBefore).FirstOrDefault() == "race-list-drained" &&
                h.Wire.ListRequests.Count == listsBefore && store.Cursor(RootId) == "race-retried" &&
                await local.ObserveAsync("queued.txt", CancellationToken.None) is not null &&
                await local.ObserveAsync("between-list-and-refresh.txt", CancellationToken.None) is not null,
                "reopened retry catches the listing-to-checkpoint race through delta and completes both downloads");
        }
    }

    private static async Task PrepareCursorWidenedScopeAsync(string scope)
    {
        using var h = new Harness();
        var productRoot = Path.Combine(_root, "prepare-cursor-widened-" + scope);
        var stateRoot = Path.Combine(productRoot, "state");
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        local.Seed("matched.txt", "same");
        h.Wire.SeedFile("matched.txt", "same");
        // This file predates prepare and therefore will NOT occur in its later change feed.
        const string excludedPath = "nested/excluded.txt";
        h.Wire.SeedFolder("nested");
        h.Wire.SeedFile(excludedPath, "existed before prepare");
        var pair = new PairDef("p1", "prepare cursor widened scope",
            new RootDef("ra", SyncProviders.Local, null, "C:\\offline-prepare-widened", "id:ra"),
            Root(), scope == "excluded" ? ["nested"] : [],
            scope == "ignore" ? ["FolderName:nested"] : [], true);
        var inner = new PairDef("inner", "nested ownership",
            pair.A with { RootId = "inner-a", RootPath = pair.A.RootPath + "\\nested", RootObjectId = "id:inner-a" },
            pair.B with { RootId = "inner-b", RootPath = "/Pilot/nested", RootObjectId = "id:inner-b" },
            [], [], true);
        h.Wire.LatestCursor = "narrow-scope";
        h.Wire.SeedChangePage("narrow-scope", [], false, "narrow-scope");
        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
        {
            store.SavePair(pair);
            if (scope == "nested-root") store.SavePair(inner);
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            var prepared = await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
            if (scope == "ignore")
                Require(prepared.RecommendedDownloadBytes == 0 && store.Live(RootId, [excludedPath]).Count == 1,
                    "ignore rules filter the prepared download plan, not the retained inventory");
            else
                Require(store.Live(RootId, [excludedPath]).Count == 0,
                    scope + ": narrow prepare excludes the pre-existing file from retained inventory");
            pair = pair with { Excluded = [], IgnoreRules = [] };
            store.SavePair(pair);
            if (scope == "nested-root")
                store.SavePair(inner with
                {
                    A = inner.A with { RootPath = "C:\\elsewhere" },
                    B = inner.B with { RootPath = "/Elsewhere" }
                });
        }
        if (scope != "ignore") h.Wire.AfterListingCaptured = () =>
        {
            h.Wire.SeedFile("nested/raced.txt", "arrived after hydration capture");
            h.Wire.SeedChangePage("narrow-scope", [h.Wire.ChangeExistingFile("nested/raced.txt")], false, "wide-race-end");
        };
        h.Wire.LatestCursor = "wide-scope-before-list";
        h.Wire.SeedChangePage("wide-scope-before-list", [], false, "wide-scope-before-list");
        var listingsBefore = h.Wire.ListRequests.Count;
        var callsBefore = h.Wire.Calls.Count;
        var feedsBefore = h.Wire.ContinueCursors.Count;
        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
        {
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            var applied = await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { },
                CancellationToken.None);
            var calls = h.Wire.Calls.Skip(callsBefore).ToList();
            Require(applied.Failed == 0 && store.FirstSyncDone(pair.PairId) &&
                h.Wire.ListRequests.Count > listingsBefore &&
                (scope == "ignore"
                    ? !h.Wire.ContinueCursors.Skip(feedsBefore).Contains("narrow-scope") &&
                      calls.IndexOf("/2/files/list_folder/get_latest_cursor") >= 0 &&
                      calls.IndexOf("/2/files/list_folder/get_latest_cursor") < calls.IndexOf("/2/files/list_folder")
                    : h.Wire.ContinueCursors.Skip(feedsBefore).Contains("narrow-scope") &&
                      !calls.Contains("/2/files/list_folder/get_latest_cursor") &&
                      h.Wire.ListRequests.Skip(listingsBefore).All(request => request.Path != DropboxWire.RootId)),
                "prefix widening hydrates only its subtree and replays original root cursor; ignore removal bootstraps explicitly");
            Require(await local.ObserveAsync(excludedPath, CancellationToken.None) is not null &&
                store.Live(RootId, [excludedPath]).Count == 1 &&
                store.Cursor(RootId) == (scope == "ignore" ? "wide-scope-before-list" : "wide-race-end") &&
                (scope == "ignore" || await local.ObserveAsync("nested/raced.txt", CancellationToken.None) is not null),
                "scope fallback discovers unchanged formerly excluded content and commits the complete cursor");
        }
        h.Wire.AfterListingCaptured = null;
        var committed = scope == "ignore" ? "wide-scope-before-list" : "wide-race-end";
        h.Wire.SeedFile("nested/later.txt", "later delta");
        h.Wire.SeedChangePage(committed, [h.Wire.ChangeExistingFile("nested/later.txt")], false, "wide-later");
        var listsAfterWidening = h.Wire.ListRequests.Count;
        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
        {
            var (_, run) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            var next = await run.IncrementalAsync(pair.PairId, new Progress<RunProgress>(), CancellationToken.None);
            Require(next.Failed == 0 && h.Wire.ListRequests.Count == listsAfterWidening &&
                store.Cursor(RootId) == "wide-later" && await local.ObserveAsync("nested/later.txt", CancellationToken.None) is not null,
                "later delta consumes widened root coverage without any listing");
        }
    }

    private static (V2CliRuntime Runtime, SyncRun Run) PrepareCursorRuntime(Harness h, FakeEndpoint local,
        PairDef pair, IStateStore store, string productRoot)
    {
        var settings = V2Settings.Default with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };
        var provider = new FakeEndpointProvider().Add(pair.A, local)
            .AddInventory(pair.B, new DropboxInventory(pair.B, Side.B, h.Client, store, pair.PairId))
            .AddWriter(pair.B, h.Writer(Path.Combine(productRoot, "spool")));
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission()),
            provider, settings, TimeProvider.System);
        return (new V2CliRuntime(null, store, run, new EndpointRootProbe(provider), productRoot), run);
    }
}
