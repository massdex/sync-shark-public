using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task PrepareCursorLegacyOrRescanAsync(bool rescan)
    {
        using var h = new Harness();
        var scenario = rescan ? "rescan" : "legacy-json";
        var productRoot = Path.Combine(_root, "prepare-cursor-" + scenario);
        var stateRoot = Path.Combine(productRoot, "state");
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        local.Seed("matched.txt", "same");
        h.Wire.SeedFile("matched.txt", "same");
        var pair = new PairDef("p1", scenario,
            new RootDef("ra", SyncProviders.Local, null, "C:\\offline-" + scenario, "id:ra"),
            Root(), [], [], true);
        h.Wire.LatestCursor = "old-pending";
        h.Wire.SeedChangePage("old-pending", [], false, "old-pending");
        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
        {
            store.SavePair(pair);
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
            var before = FirstSyncChoicesState.Load(productRoot, pair.PairId, store)!;
            Require(before.InventoryCursors.Any(entry => entry.RootId == RootId),
                scenario + ": fixture starts with a persisted Dropbox checkpoint");
            if (rescan)
            {
                // Public rescan owns pending prepare checkpoints; SyncRun.RequestRescanAsync owns root state.
                await runtime.RescanPairAsync(pair.PairId, "b", CancellationToken.None);
                var after = FirstSyncChoicesState.Load(productRoot, pair.PairId, store)!;
                Require(after.InventoryCursors.All(entry => entry.RootId != RootId) &&
                    JsonSerializer.Serialize(before with { InventoryCursors = [] }) ==
                    JsonSerializer.Serialize(after with { InventoryCursors = [] }),
                    "rescan removes pending checkpoints without changing any frozen review field or scope");
            }
            else
            {
                // Exercise actual backwards-compatible deserialization, not a current object with empty fields.
                var file = Path.Combine(productRoot, "v2", "first-sync-p1.choices.json");
                var json = JsonSerializer.SerializeToNode(before,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!.AsObject();
                Require(json.Remove("inventoryCursors") && json.Remove("inventoryScope"),
                    "legacy fixture removes both new JSON properties entirely");
                var originalMatches = before.Matched.ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, json.ToJsonString());
                store.ClearPrepare(pair.PairId); // Force the production streaming legacy-import path.
                // A pre-v10 installation has no independent durable observation checkpoint either.
                store.ForgetInventoryObservation(RootId);
                var legacy = FirstSyncChoicesState.Load(productRoot, pair.PairId, store)!;
                Require(legacy.InventoryCursors.Count == 0 && legacy.InventoryScope is null &&
                    legacy.Database is not null && legacy.Digest == before.Digest && legacy.Matched.SequenceEqual(originalMatches),
                    "legacy JSON defaults cursor metadata while retaining the original review");
            }
        }
        h.Wire.SeedFile("not-in-feed.txt", "found only by a full listing");
        h.Wire.LatestCursor = "fresh-full";
        h.Wire.SeedChangePage("fresh-full", [], false, "fresh-full");
        var lists = h.Wire.ListRequests.Count;
        var feeds = h.Wire.ContinueCursors.Count;
        var calls = h.Wire.Calls.Count;
        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
        {
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            var applied = await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { },
                CancellationToken.None);
            var resumedCalls = h.Wire.Calls.Skip(calls).ToList();
            Require(applied.Failed == 0 && store.FirstSyncDone(pair.PairId) &&
                h.Wire.ListRequests.Count > lists &&
                !h.Wire.ContinueCursors.Skip(feeds).Contains("old-pending") &&
                resumedCalls.IndexOf("/2/files/list_folder/get_latest_cursor") >= 0 &&
                resumedCalls.IndexOf("/2/files/list_folder/get_latest_cursor") <
                resumedCalls.IndexOf("/2/files/list_folder") &&
                store.Cursor(RootId) == "fresh-full" &&
                await local.ObserveAsync("not-in-feed.txt", CancellationToken.None) is not null,
                scenario + ": reopened apply uses cursor-then-full listing rather than the old pending cursor");
        }
    }

    private static async Task PrepareCursorDeletionFallbackAsync(bool released)
    {
        using var h = new Harness();
        var scenario = released ? "released-delete" : "pending-delete";
        var productRoot = Path.Combine(_root, "prepare-cursor-" + scenario);
        var stateRoot = Path.Combine(productRoot, "state");
        // Local files have no provider revision; prepare must retain their content hash, not a fake v-stamp.
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true, OmitVersionIds = true };
        local.Seed("matched.txt", "same");
        h.Wire.SeedFile("matched.txt", "same");
        h.Wire.SeedFile("remote-only.txt", "chosen download disappears");
        var pair = new PairDef("p1", scenario,
            new RootDef("ra", SyncProviders.Local, null, "C:\\offline-" + scenario, "id:ra"),
            Root(), [], [], true);
        h.Wire.LatestCursor = "delete-prepare";
        h.Wire.SeedChangePage("delete-prepare", [], false, "delete-prepare");
        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
        {
            store.SavePair(pair);
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
        }
        foreach (var path in new[] { "matched.txt", "remote-only.txt" })
        {
            var writer = h.Writer();
            var current = await writer.ObserveAsync(path, CancellationToken.None);
            await writer.DeleteAsync(new SyncAction("fixture-delete-" + path, 1, ActionKind.Delete,
                Side.B, path, null, null, current, false, null), CancellationToken.None);
            h.Wire.SeedDeletedEntry(path);
        }
        h.Wire.SeedFile("ordinary.txt", "download can fail independently of released history");
        h.Wire.SeedChangePage("delete-prepare",
            [h.Wire.ChangeDeleted("/Pilot/matched.txt"), h.Wire.ChangeDeleted("/Pilot/remote-only.txt"),
                h.Wire.ChangeExistingFile("ordinary.txt")],
            false, "delete-delta");
        h.Wire.LatestCursor = "delete-full";
        h.Wire.SeedChangePage("delete-full", [], false, "delete-full");
        if (released) h.Wire.Script("/2/files/list_revisions", HttpStatusCode.BadRequest, 20);
        // A non-transport lookup failure is pending evidence without entering the client's five-minute read retry.
        else h.Wire.OnRevisionAsync = _ => Task.FromException(new InvalidOperationException("offline revision read failed"));
        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
        {
            var (runtime, run) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            var prepared = FirstSyncChoicesState.Load(productRoot, pair.PairId, store)!;
            var matches = prepared.Matched.ToDictionary(entry => entry.Path,
                entry => new PrepareMatch(entry.A, entry.B), StringComparer.OrdinalIgnoreCase);
            var survivor = (await local.ObserveAsync("matched.txt", CancellationToken.None))!;
            Require(survivor.VersionId is null && survivor.Hash is not null &&
                matches["matched.txt"].A == survivor.ObjectId + "|h:" + survivor.Hash,
                scenario + ": prepared local survivor carries real identity and hash evidence");
            var lists = h.Wire.ListRequests.Count;
            var calls = h.Wire.Calls.Count;
            var delta = await run.PreviewFirstSyncApplyResolvedAsync(pair.PairId, null, null, [], matches,
                new Progress<RunProgress>(), CancellationToken.None,
                prepared.InventoryCursors.ToDictionary(entry => entry.RootId, entry => entry.Cursor));
            var resumedCalls = h.Wire.Calls.Skip(calls).ToList();
            Require(h.Wire.ListRequests.Count == lists &&
                resumedCalls.Contains("/2/files/list_revisions") &&
                !resumedCalls.Contains("/2/files/list_folder/get_latest_cursor") &&
                delta.Plan.InventoryCursors[RootId] == "delete-delta" &&
                store.Live(RootId, ["matched.txt", "remote-only.txt"]).Count == 0,
                scenario + ": unresolved delta deletes prune phantom rows without losing the saved cursor or relisting");
            Require(store.DeletionEvidenceRows(pair.PairId, RootId).Any(row =>
                row.Path == "remote-only.txt" && row.Failure is { Released: true }),
                scenario + ": permanent and transient history failures both release to one durable evidence hold");
            var full = await run.PreviewFirstSyncApplyResolvedAsync(pair.PairId, null, null, [], matches,
                new Progress<RunProgress>(), CancellationToken.None);
            // Generation/action IDs are intentionally fresh; compare the semantic plan instead.
            static string Effects(Plan plan) => JsonSerializer.Serialize(new
            {
                Actions = plan.Actions.Select(action => new
                {
                    action.Kind, action.Target, action.Path, action.FromPath,
                    action.FirstSyncChoice, action.NeedsReview
                }),
                plan.HeldDeletions,
                plan.UnreviewedHeldDeletions
            });
            Require(Effects(delta.Plan) == Effects(full.Plan) &&
                delta.Plan.Actions.Any(action => action.Kind == ActionKind.Delete && action.Path == "matched.txt") &&
                delta.Plan.Actions.All(action => action.Path != "remote-only.txt"),
                scenario + ": resumed plan matches full listing, propagates D71 deletion and drops the vanished download");
            if (released)
            {
                local.FailNextWrites = 1;
                var failed = await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { },
                    CancellationToken.None);
                var checkpoint = store.Cursor(RootId)!;
                Require(failed.Failed == 1 && store.FirstSyncDone(pair.PairId) &&
                    !string.IsNullOrEmpty(checkpoint) &&
                    store.DeletionEvidenceRows(pair.PairId, RootId).Any(row => row.Failure is { Released: true }),
                    "released-evidence fixture retains a refreshed checkpoint after an unrelated failed download");
                h.Wire.SeedChangePage(checkpoint, [], false, "released-next-delta");
                // The deletion guard may list a parent nonrecursively for fresh positive identity evidence.
                // Only recursive requests represent the full inventory relisting this regression forbids.
                var listsBeforeRetry = h.Wire.ListRequests.Count(request => request.Recursive);
                var feedsBeforeRetry = h.Wire.ContinueCursors.Count;
                // Rebuild inventory/runtime: the historical released rows survive, but their phantom live rows do not.
                var (_, retryRun) = PrepareCursorRuntime(h, local, pair, store, productRoot);
                var retried = await retryRun.IncrementalAsync(pair.PairId, new Progress<RunProgress>(), CancellationToken.None);
                Require(h.Wire.ListRequests.Count(request => request.Recursive) == listsBeforeRetry &&
                    h.Wire.ContinueCursors.Skip(feedsBeforeRetry).FirstOrDefault() == checkpoint &&
                    store.Cursor(RootId) == "released-next-delta",
                    "a historical released row never forces another full inventory listing on a clean resumed delta");
                Require(retried.Failed == 0 && store.FirstSyncDone(pair.PairId) &&
                    await local.ObserveAsync("ordinary.txt", CancellationToken.None) is not null &&
                    await local.ObserveAsync("matched.txt", CancellationToken.None) is null &&
                    await local.ObserveAsync("remote-only.txt", CancellationToken.None) is null,
                    "the resumed delta retries the unrelated download and propagates only the proven source deletion");
                return;
            }
            var applied = await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { },
                CancellationToken.None);
            Require(applied.Failed == 0 && store.FirstSyncDone(pair.PairId) &&
                await local.ObserveAsync("matched.txt", CancellationToken.None) is not null,
                "post-preview matched deletion waits for ordinary sync after completion");
            var safeCursor = store.Cursor(RootId)!;
            h.Wire.SeedChangePage(safeCursor, [], false, safeCursor);
            await run.IncrementalAsync(pair.PairId, new Progress<RunProgress>(), default);
            Require(await local.ObserveAsync("matched.txt", CancellationToken.None) is null &&
                await local.ObserveAsync("remote-only.txt", CancellationToken.None) is null,
                scenario + ": resumed public apply completes without retrying a phantom download");
        }
    }

    private static async Task PrepareCursorDeferredAsync()
    {
        using var h = new Harness();
        var productRoot = Path.Combine(_root, "prepare-cursor-deferred");
        var stateRoot = Path.Combine(productRoot, "state");
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        local.Seed("held.txt", "deletion history unavailable");
        h.Wire.SeedDeletedEntry("held.txt");
        h.Wire.RevisionResponseOverride = new { is_deleted = true, server_deleted = "invalid", entries = Array.Empty<object>() };
        h.Wire.SeedFile("ordinary.txt", "unrelated download succeeds");
        var pair = new PairDef("p1", "nonfailed deferred apply",
            new RootDef("ra", SyncProviders.Local, null, "C:\\offline-deferred", "id:ra"),
            Root(), [], [], true);
        h.Wire.LatestCursor = "deferred-prepare";
        h.Wire.SeedChangePage("deferred-prepare", [], false, "deferred-prepare");
        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
        {
            store.SavePair(pair);
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
        }
        h.Wire.SeedChangePage("deferred-prepare", [], false, "deferred-refreshed");
        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
        {
            var (runtime, run) = PrepareCursorRuntime(h, local, pair, store, productRoot);
            var applied = await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { },
                CancellationToken.None);
            var pending = store.SyncRetryAt(pair.PairId, "held.txt");
            Require(applied.Failed == 0 && applied.Deferred == 0 && store.FirstSyncDone(pair.PairId) &&
                store.Cursor(RootId) == "deferred-refreshed" && pending is not null &&
                await local.ObserveAsync("ordinary.txt", CancellationToken.None) is not null &&
                await local.ObserveAsync("held.txt", CancellationToken.None) is not null,
                "nonfailed partial apply completes unrelated work and promotes the refreshed safe cursor despite the evidence hold");
            Require(h.Wire.Calls.Count(call => call == "/2/files/list_revisions") == 1 &&
                !h.Wire.Calls.Any(call => call.Contains("/delete", StringComparison.Ordinal) ||
                    call.Contains("/restore", StringComparison.Ordinal) || call.Contains("/upload", StringComparison.Ordinal) ||
                    call.Contains("/move", StringComparison.Ordinal) || call.Contains("/copy", StringComparison.Ordinal)) &&
                store.Baselines(pair.PairId).All(row => row.A.Path != "held.txt" && row.B.Path != "held.txt"),
                "permanent Unknown is held after one probe: no retry, delete, restore, upload or held-path baseline");
            Require(pending!.Held?.Path == "held.txt" &&
                store.DeletionEvidenceRows(pair.PairId, RootId).All(item => item.Path != "held.txt"),
                "unanswered authority remains in the durable held row and is not cached as resolved deletion evidence");
            Require(pending.ReviewedActions is { Count: 0 } && pending.ProposedActions is null &&
                store.UnresolvedIntents(pair.PairId).All(action => action.Path != "held.txt" && action.FromPath != "held.txt"),
                "terminal Unknown retains no executable held-path action, only evidence recheck input");
        }
        using (var store = SqliteStateStore.Open(stateRoot, StorageChecks.Fast, TimeProvider.System))
            Require(store.FirstSyncDone(pair.PairId) && store.Cursor(RootId) == "deferred-refreshed" &&
                store.SyncRetryAt(pair.PairId, "held.txt")?.Held?.Path == "held.txt",
                "completion, safe cursor and held evidence input survive SQLite reopen");
    }
}
