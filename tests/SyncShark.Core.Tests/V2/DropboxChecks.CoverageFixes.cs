using System.Net;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    public static async Task RunCoverageFixChecksAsync(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh test directory.");
        Directory.CreateDirectory(root); _root = root;
        var failures = new List<string>();
        foreach (var (name, test) in new (string, Func<Task>)[] {
            ("after-scan-crash", CoverageCrashReplayAsync),
            ("pending-evidence", CoveragePendingReplayAsync),
            ("hydration-fallback", CoverageHydrationFallbackAsync),
            ("reincluded-delete", CoverageReincludedDeleteAsync) })
        {
            try { await test(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures.Add(name); Console.WriteLine("FAIL " + name + ": " + error); }
        }
        Require(failures.Count == 0, "coverage fix regressions: " + string.Join(", ", failures));
        Console.WriteLine("--v2-coverage-fix-checks passed.");
    }

    private sealed class CrashAfterScanProvider(IEndpointProvider inner, IStateStore store, string remote) : IEndpointProvider
    {
        public bool Armed;
        public bool Fired;
        public IInventorySource Inventory(RootDef root) => inner.Inventory(root);
        public Task PrepareAsync(PairDef pair, CancellationToken ct) => inner.PrepareAsync(pair, ct);
        public IEndpointWriter Writer(RootDef root)
        {
            if (Armed && store.Live(remote, ["nested/after-scan.txt"]).Count != 0)
            {
                Armed = false; Fired = true;
                throw new IOException("Injected crash after persisted scan, before effects.");
            }
            return inner.Writer(root);
        }
    }

    private static async Task CoverageCrashReplayAsync()
    {
        await CoverageCrashReplayAsync(false);
        await CoverageCrashReplayAsync(true);
    }

    private static async Task CoverageCrashReplayAsync(bool hydrate)
    {
        using var h = new Harness();
        var product = Path.Combine(_root, "crash-" + hydrate);
        var localPath = Path.Combine(product, "local"); Directory.CreateDirectory(localPath);
        var settings = StorageChecks.Fast with { MaxWorkers = 1, ProgressInterval = TimeSpan.Zero };
        using var store = SqliteStateStore.Open(Path.Combine(product, "state"), settings, TimeProvider.System);
        var pair = new PairDef("crash", "production inventory crash", LocalChecks.RootOf(localPath, "local"), Root(), hydrate ? ["nested"] : [], [], true);
        store.SavePair(pair); store.MarkFirstSyncDone(pair.PairId);
        var localWriter = await LocalWriter.OpenAsync(pair.A, Side.A, pair.PairId, LocalChecks.InstallationId,
            Path.Combine(product, "recovery"), CancellationToken.None, settings);
        var endpoints = new FakeEndpointProvider().AddWriter(pair.A, localWriter)
            .AddInventory(pair.A, new LocalInventory(pair.A, Side.A, store, new HashSet<string>(), settings, TimeProvider.System))
            .AddInventory(pair.B, new DropboxInventory(pair.B, Side.B, h.Client, store, pair.PairId))
            .AddWriter(pair.B, h.Writer());
        var fault = new CrashAfterScanProvider(endpoints, store, RootId);
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission()), fault, settings, TimeProvider.System);
        h.Wire.LatestCursor = "crash-c0"; h.Wire.SeedChangePage("crash-c0", [], false, "crash-c0");
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        if (hydrate) store.SavePairIncluded(pair, pair with { Excluded = [] });
        h.Wire.SeedFolder("nested");
        h.Wire.SeedFile("nested/after-scan.txt", "must replay");
        h.Wire.SeedChangePage("crash-c0", [h.Wire.ChangeExistingFile("nested/after-scan.txt")], false, "crash-c1");
        h.Wire.SeedChangePage("crash-c1", [], false, "crash-c2");
        fault.Armed = true;
        try { await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None); }
        catch (IOException) { }
        Require(fault.Fired && store.Cursor(RootId) == (hydrate ? null : "crash-c0") && !File.Exists(Path.Combine(localPath, "nested/after-scan.txt")),
            "fault occurs after production inventory ingestion, before effect/cursor commit");
        var feeds = h.Wire.ContinueCursors.Count;
        var result = await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(result.Failed == 0 && h.Wire.ContinueCursors.Skip(feeds).First() == "crash-c0" &&
            await File.ReadAllTextAsync(Path.Combine(localPath, "nested/after-scan.txt")) == "must replay",
            "next production-endpoint run replays unexecuted scan window and downloads remote addition");
    }

    private static async Task CoveragePendingReplayAsync()
    {
        using var h = new Harness();
        var product = Path.Combine(_root, "pending"); var pair = ClassificationPair();
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        local.Seed("gone.txt", "same"); h.Wire.SeedFile("gone.txt", "same");
        h.Wire.LatestCursor = "pending-c0"; h.Wire.SeedChangePage("pending-c0", [], false, "pending-c0");
        using var store = SqliteStateStore.Open(Path.Combine(product, "state"), StorageChecks.Fast, TimeProvider.System);
        store.SavePair(pair);
        var (runtime, run) = PrepareCursorRuntime(h, local, pair, store, product);
        await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
        await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { }, CancellationToken.None);
        h.Wire.MoveOutsideRoot("gone.txt", "/Outside/gone.txt");
        h.Wire.SeedChangePage("pending-c0", [h.Wire.ChangeDeleted("/Pilot/gone.txt")], false, "pending-c1");
        h.Client.ReadTransientBudget = TimeSpan.FromMilliseconds(50);
        h.Wire.OnRevisionAsync = _ => throw new HttpRequestException("transient evidence outage");
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(store.Cursor(RootId) == "pending-c0", "pending evidence pins committed cursor");
        var lists = h.Wire.ListRequests.Count; var feeds = h.Wire.ContinueCursors.Count;
        h.Wire.OnRevisionAsync = null;
        await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(h.Wire.ListRequests.Count == lists && h.Wire.ContinueCursors.Skip(feeds).First() == "pending-c0",
            "classification-ahead/pending evidence retries targeted feed without full-root relisting");
    }

    private static async Task CoverageHydrationFallbackAsync()
    {
        foreach (var invalid in new[] { false, true })
        {
            using var h = new Harness();
            var product = Path.Combine(_root, "fallback-" + invalid);
            var pair = ClassificationPair() with { Excluded = ["nested"] };
            var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
            h.Wire.SeedFolder("nested"); h.Wire.SeedFile("nested/file.txt", "discovered");
            h.Wire.LatestCursor = "fallback-c0"; h.Wire.SeedChangePage("fallback-c0", [], false, "fallback-c0");
            using var store = SqliteStateStore.Open(Path.Combine(product, "state"), StorageChecks.Fast, TimeProvider.System);
            store.SavePair(pair); var (runtime, _) = PrepareCursorRuntime(h, local, pair, store, product);
            await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
            var narrowObservation = store.ReadInventoryObservation(pair.B)!;
            var included = pair with { Excluded = [] }; store.SavePairIncluded(pair, included);
            if (invalid) h.Wire.AfterListingCaptured = () => {
                h.Wire.AfterListingCaptured = null; throw new InvalidDataException("Injected malformed subtree listing."); };
            else h.Wire.Script("/2/files/list_folder", HttpStatusCode.Conflict, 2);
            h.Wire.LatestCursor = "fallback-full"; h.Wire.SeedChangePage("fallback-full", [], false, "fallback-full");
            var lists = h.Wire.ListRequests.Count;
            if (!invalid)
            {
                try
                {
                    await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { }, CancellationToken.None);
                    throw new InvalidOperationException("Incomplete fallback must not authorize apply.");
                }
                catch (SyncCliReviewException) { }
                Require(store.ReadInventoryObservation(pair.B) is null && store.ReadDeletionClassification(pair.B) is null &&
                    store.Cursor(RootId) is null && !store.FirstSyncDone(pair.PairId) &&
                    store.OpenNotices(pair.PairId).Any(row => row.ReasonKey == "inventory-hydration-fallback"),
                    "incomplete fallback invalidates observation, committed cursor and pending-cursor classification authority");
                var notice = store.OpenNotices(pair.PairId).Single(row => row.ReasonKey == "inventory-hydration-fallback");
                Require(notice.Path == pair.B.RootId && notice.Detail.Contains(pair.B.RootPath, StringComparison.Ordinal),
                    "fallback notice identifies its stable root and explains its readable location");
                // Recreate the same narrower coverage, then fail hydration and bootstrap again.
                store.SaveInventoryObservation(narrowObservation.Coverage, narrowObservation.Cursor);
                h.Wire.Script("/2/files/list_folder", HttpStatusCode.Conflict, 2);
                try { await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { }, CancellationToken.None); }
                catch (SyncCliReviewException) { }
                var repeated = store.OpenNotices(pair.PairId).Single(row => row.ReasonKey == notice.ReasonKey);
                Require(repeated.NoticeId == notice.NoticeId, "repeated failed hydration deduplicates one notice per root");
                // Another root identity (same pair and display text) is an independent problem. It is one this run
                // never fully inventories, so neither B's bootstrap nor generic path closure may close it.
                const string otherRoot = "r-other-unscanned";
                store.AddNotices([notice with { Path = otherRoot, NoticeId = null }]);
                Require(store.OpenNotices(pair.PairId).Count(row => row.ReasonKey == notice.ReasonKey) == 2,
                    "distinct root identities do not share a fallback notice");
                // Keep the old pending review: its next attempt must bootstrap, not bypass the guard.
                await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { }, CancellationToken.None);
                Require(store.FirstSyncDone(pair.PairId) && store.NoticeById(pair.PairId, notice.NoticeId!)?.Closed == true &&
                    store.OpenNotices(pair.PairId).Single(row => row.ReasonKey == notice.ReasonKey).Path == otherRoot,
                    "successful bootstrap resolves only its own root notice");
            }
            else
            {
                await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
                Require(!store.OpenNotices(pair.PairId).Any(row => row.ReasonKey == "inventory-hydration-fallback"),
                    "complete bootstrap closes fallback notice immediately, without waiting for apply");
            }
            Require(h.Wire.ListRequests.Skip(lists).Any(row => row.Path == DropboxWire.RootId) &&
                store.ReadInventoryObservation(pair.B)?.Cursor == "fallback-full",
                "unusable subtree yields explanation and safe explicit full bootstrap, not repeated fatal hydration");
        }
    }

    private static async Task CoverageReincludedDeleteAsync()
    {
        using var h = new Harness();
        var product = Path.Combine(_root, "reinclude-delete");
        var pair = ClassificationPair() with { Excluded = ["nested"] };
        var local = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        var item = local.Seed("nested/gone.txt", "copy back", mtime: DateTimeOffset.Parse("2026-10-01T00:00:00Z"));
        h.Wire.SeedFolder("nested"); h.Wire.SeedDeletedEntry("nested/gone.txt");
        h.Wire.RevisionContentHash = item.Hash; h.Wire.RevisionDeletedTime = "2026-10-12T08:00:00Z";
        h.Wire.LatestCursor = "include-c0"; h.Wire.SeedChangePage("include-c0", [], false, "include-c0");
        using var store = SqliteStateStore.Open(Path.Combine(product, "state"), StorageChecks.Fast, TimeProvider.System);
        store.SavePair(pair); var (runtime, run) = PrepareCursorRuntime(h, local, pair, store, product);
        await runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, CancellationToken.None);
        await runtime.ApplyFirstSyncAsync(pair.PairId, null, true, null, _ => { }, CancellationToken.None);
        store.SavePairIncluded(pair, pair with { Excluded = [] });
        h.Wire.SeedChangePage("include-c0", [h.Wire.ChangeDeleted("/Pilot/nested/gone.txt")], false, "include-c1");
        var result = await run.IncrementalAsync(pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(result.Failed == 0 && await local.ObserveAsync("nested/gone.txt", CancellationToken.None) is not null &&
            await h.Client.FindEntryAtPathAsync("nested/gone.txt") is not null,
            "D81 reinclude historical tombstone cannot delete no-baseline local file; copy it back");
    }
}
