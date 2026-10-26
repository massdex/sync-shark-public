using System.Net;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task CursorKeepHistoricalFolderAsync()
    {
        using var h = new Harness();
        var product = Path.Combine(_root, "cursor-historical-folder");
        var state = Path.Combine(product, "state");
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        local.Seed("old-folder", "", ItemKind.Folder);
        h.Wire.SeedDeletedEntry("old-folder");
        var pair = new PairDef("p1", "historical folder", new RootDef("ra", SyncProviders.Local, null,
            "C:\\offline-historical-folder", "id:ra"), Root(), [], [], true);
        h.Wire.LatestCursor = "folder-start";
        h.Wire.SeedChangePage("folder-start", [], false, "folder-start");
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(pair);
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, product);
            await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
        }
        var lists = h.Wire.ListRequests.Count;
        h.Wire.SeedChangePage("folder-start", [], false, "folder-next");
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var (_, run) = PrepareCursorRuntime(h, local, pair, store, product);
            var prepared = FirstSyncChoicesState.Load(product, pair.PairId, store)!;
            var (plan, _) = await run.PreviewFirstSyncApplyResolvedAsync(pair.PairId, null, null, [], null,
                new Progress<RunProgress>(), CancellationToken.None,
                prepared.InventoryCursors.ToDictionary(c => c.RootId, c => c.Cursor));
            Require(h.Wire.ListRequests.Count == lists && plan.HeldDeletions.Any(item => item.Path == "old-folder") &&
                plan.Actions.All(action => action.Path != "old-folder"),
                "a folder deleted before the saved cursor is rediscovered by exact deleted metadata, never relisted or resurrected");
        }
        var missing = await h.Inventory.ChangesSinceAsync("folder-next", 10, _ => true, CancellationToken.None, firstSync: true);
        Require(missing.CursorInvalid && h.Wire.ListRequests.Count == lists,
            "a missing durable classification requires complete bootstrap, never empty classification or folder resurrection");
    }

    private static async Task CursorKeepInterruptedHeldAsync()
    {
        using var h = new Harness();
        var product = Path.Combine(_root, "cursor-interrupted-held");
        var state = Path.Combine(product, "state");
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        local.Seed("held.txt", "unique local content");
        h.Wire.SeedDeletedEntry("held.txt");
        h.Wire.RevisionResponseOverride = new { is_deleted = true, server_deleted = "invalid", entries = Array.Empty<object>() };
        h.Wire.SeedFile("download.txt", "ordinary work");
        var pair = new PairDef("p1", "interrupted held cursor",
            new RootDef("ra", SyncProviders.Local, null, "C:\\offline-cursor-held", "id:ra"), Root(), [], [], true);
        h.Wire.LatestCursor = "held-start";
        h.Wire.SeedChangePage("held-start", [], false, "held-start");
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(pair);
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, product);
            await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
        }
        h.Wire.SeedChangePage("held-start", [], false, "held-failed");
        var lists = h.Wire.ListRequests.Count;
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            local.FailNextWrites = 1;
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, product);
            var failed = await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { }, CancellationToken.None);
            Require(failed.Failed == 1 && store.FirstSyncDone(pair.PairId) &&
                store.Cursor(pair.B.RootId) == "held-failed" && store.SyncRetryAt(pair.PairId, "held.txt") is not null,
                "item failures complete first sync with the refreshed safe cursor and durable held input");
        }
        // RQ-032/RQ-130, docs/design/retry-resume-rewind.md: the persisted exact deadline
        // gates retry effects across reopen; an immediate run must not bypass its backoff.
        DateTimeOffset retryAt;
        h.Wire.SeedChangePage("held-failed", [], false, "held-waiting");
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var retry = store.SyncRetryAt(pair.PairId, "download.txt");
            Require(retry is not null, "failed ordinary download has a durable retry after reopen");
            retryAt = retry!.RetryAfter;
            var calls = h.Wire.Calls.Count;
            var (_, run) = PrepareCursorRuntime(h, local, pair, store, product,
                new LocalChecks.FixedClock(retryAt.AddTicks(-1)));
            var waiting = await run.IncrementalAsync(pair.PairId, new Progress<RunProgress>(), CancellationToken.None);
            Require(waiting.Failed == 0 && waiting.Deferred > 0,
                "ordinary retry remains deferred one tick before its persisted deadline");
            Require(await local.ObserveAsync("download.txt", CancellationToken.None) is null,
                "ordinary retry has no local write effect before its deadline");
            Require(store.SyncRetryAt(pair.PairId, "download.txt")?.RetryAfter == retryAt,
                "waiting run preserves the exact persisted retry deadline");
            Require(await local.ObserveAsync("held.txt", CancellationToken.None) is not null,
                "before retry deadline the D78c survivor remains");
            Require(store.SyncRetryAt(pair.PairId, "held.txt")?.Held is not null,
                "before retry deadline the D78c hold evidence remains durable");
            Require(!h.Wire.Calls.Skip(calls).Any(call => call.Contains("/delete", StringComparison.Ordinal) ||
                call.Contains("/upload", StringComparison.Ordinal)),
                "waiting ordinary retry causes no remote delete or upload");
            Require(store.Baselines(pair.PairId).All(row => row.A.Path != "held.txt"),
                "waiting ordinary retry never records a successful held baseline");
        }
        h.Wire.SeedChangePage("held-waiting", [], false, "held-resumed");
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var (_, run) = PrepareCursorRuntime(h, local, pair, store, product,
                new LocalChecks.FixedClock(retryAt));
            var resumed = await run.IncrementalAsync(pair.PairId, new Progress<RunProgress>(), CancellationToken.None);
            Require(resumed.Failed == 0, "reopened ordinary retry succeeds at its exact deadline");
            Require(resumed.Deferred > 0, "reopened ordinary sync still defers the D78c hold");
            Require(store.FirstSyncDone(pair.PairId), "ordinary resume preserves completed first-sync state");
            Require(h.Wire.ListRequests.Count == lists, "ordinary retry resumes without a full listing");
            Require(await local.ObserveAsync("download.txt", CancellationToken.None) is not null,
                "ordinary retry restores pending download at its exact deadline");
            Require(await local.ObserveAsync("held.txt", CancellationToken.None) is not null,
                "ordinary resume preserves the D78c survivor");
            Require(store.SyncRetryAt(pair.PairId, "held.txt")?.Held is not null,
                "ordinary resume preserves durable D78c hold evidence");
            Require(store.Cursor(pair.B.RootId) == "held-resumed", "ordinary resume commits the refreshed cursor");
            Require(store.Baselines(pair.PairId).All(row => row.A.Path != "held.txt"),
                "unknown held hash never authorizes a successful held baseline");
            Require(!h.Wire.Calls.Any(call => call.Contains("/delete", StringComparison.Ordinal)),
                "unknown held hash never authorizes a remote delete");
            Require(!h.Wire.Calls.Any(call => call.Contains("/upload", StringComparison.Ordinal)),
                "unknown held hash never authorizes a remote upload");
        }
    }

    private static async Task CursorKeepReprepareAsync()
    {
        using var h = new Harness();
        var product = Path.Combine(_root, "cursor-reprepare");
        var state = Path.Combine(product, "state");
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        h.Wire.SeedFile("download.txt", "old");
        var pair = new PairDef("p1", "reprepare", new RootDef("ra", SyncProviders.Local, null,
            "C:\\offline-reprepare", "id:ra"), Root(), [], [], true);
        h.Wire.LatestCursor = "prepare-one";
        h.Wire.SeedChangePage("prepare-one", [], false, "prepare-one");
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(pair);
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, product);
            await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
        }
        var lists = h.Wire.ListRequests.Count;
        h.Wire.SeedFile("new.txt", "new");
        h.Wire.SeedChangePage("prepare-one", [h.Wire.ChangeExistingFile("new.txt")], false, "prepare-two");
        using (var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, product);
            await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
            Require(h.Wire.ListRequests.Count == lists && store.Live(RootId, ["new.txt"]).Count == 1 &&
                FirstSyncChoicesState.Load(product, pair.PairId, store)!.InventoryCursors.Any(c => c.Cursor == "prepare-two"),
                "a new prepare invalidates authorization but reuses compatible saved observation and ingests new delta");
            var generation = store.BeginGeneration(RootId, "checkpoint");
            store.CompleteGeneration(generation, "committed-before-narrow", true);
            await runtime.SaveIgnoresAsync(pair.PairId, "FileName:excluded.tmp", CancellationToken.None);
            Require(store.Cursor(RootId) == "committed-before-narrow" &&
                FirstSyncChoicesState.Load(product, pair.PairId, store)!.InventoryCursors.Any(c => c.Cursor == "prepare-two"),
                "transactional saved-review narrowing retains both committed and pending observation cursors");
        }
    }

    private static async Task CursorKeepManyReleasedAsync()
    {
        using var h = new Harness();
        var product = Path.Combine(_root, "cursor-many-released");
        var state = Path.Combine(product, "state");
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        h.Wire.SeedFile("ghost.txt", "vanishes");
        h.Wire.SeedFile("survivor.txt", "still exists");
        var pair = new PairDef("p1", "released", new RootDef("ra", SyncProviders.Local, null,
            "C:\\offline-released", "id:ra"), Root(), [], [], true);
        h.Wire.LatestCursor = "released-start";
        h.Wire.SeedChangePage("released-start", [], false, "released-start");
        using var store = SqliteStateStore.Open(state, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        store.SavePair(pair);
        var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, product);
        await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
        var writer = h.Writer();
        var ghost = await writer.ObserveAsync("ghost.txt", CancellationToken.None);
        await AuthorizedFixtureDeleteAsync(writer, new SyncAction("remove-ghost", 1, ActionKind.Delete, Side.B,
            "ghost.txt", null, null, ghost, false, null), CancellationToken.None);
        store.StoreDeletionEvidence(pair.PairId, RootId,
            Enumerable.Range(0, 5001).Select(i => new DeletionEvidenceRecord("old-" + i, null,
                Failure: new(3, DateTimeOffset.UtcNow, 1, true)))
            .Concat(new[] { "ghost.txt", "survivor.txt" }.Select(path => new DeletionEvidenceRecord(path, null,
                Failure: new(3, DateTimeOffset.UtcNow, 1, true)))).ToArray());
        var lists = h.Wire.ListRequests.Count;
        h.Wire.SeedChangePage("released-start", [], false, "released-next");
        var applied = await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { }, CancellationToken.None);
        Console.WriteLine($"released-row observations: failed={applied.Failed}; lists-before={lists}; lists-after={h.Wire.ListRequests.Count}; " +
            $"stored-ghost={store.Live(RootId, ["ghost.txt"]).Count}; stored-survivor={store.Live(RootId, ["survivor.txt"]).Count}; " +
            $"local-ghost={await local.ObserveAsync("ghost.txt", CancellationToken.None) is not null}; " +
            $"local-survivor={await local.ObserveAsync("survivor.txt", CancellationToken.None) is not null}; " +
            $"retry-survivor={store.SyncRetryAt(pair.PairId, "survivor.txt")?.Kind}");
        Require(applied.Failed == 0 && h.Wire.ListRequests.Count == lists &&
            store.Live(RootId, ["ghost.txt"]).Count == 0 && store.Live(RootId, ["survivor.txt"]).Count == 1 &&
            await local.ObserveAsync("ghost.txt", CancellationToken.None) is null &&
            await local.ObserveAsync("survivor.txt", CancellationToken.None) is not null,
            "more than 5000 released rows use keyed checks: prune confirmed absent ghost, preserve live survivor, no relist");
    }

    private static void CursorKeepPairScope()
    {
        using var store = SqliteStateStore.Open(Path.Combine(_root, "cursor-scope"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = new PairDef("p1", "scope", new RootDef("ra", SyncProviders.Local, null,
            "C:\\offline-scope", "id:ra"), Root(), ["nested/child"], ["FileName:old.tmp"], true);
        store.SavePair(pair);
        void Checkpoint() { var gen = store.BeginGeneration(RootId, "full"); store.CompleteGeneration(gen, "saved", true); }
        Checkpoint();
        store.SavePair(pair with { Enabled = false });
        store.SavePair(pair);
        Require(store.Cursor(RootId) == "saved", "enable/disable never invalidates observation");
        pair = pair with { Excluded = ["nested"], IgnoreRules = ["FileName:old.tmp", "FileName:new.tmp"] };
        store.SavePair(pair);
        Require(store.Cursor(RootId) == "saved", "semantic exclusion narrowing and added ignores preserve coverage");
        pair = pair with { Excluded = ["nested/child"] };
        store.SavePair(pair);
        Require(store.Cursor(RootId) is null, "scope widening invalidates coverage");
        Checkpoint();
        store.SavePair(pair with { B = pair.B with { RootPath = "/Other" } });
        Require(store.Cursor(RootId) is null, "actual root changes invalidate coverage");
    }
}
