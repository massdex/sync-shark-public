using System.Reflection;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    public static async Task RunRootRelistScopedChecksAsync(string root)
    {
        _root = root;
        var folder = Path.Combine(root, "root-relist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        IStateStore listingStore = store;
        IStateStore unsupported = new InMemoryStateStore();
        Require(!unsupported.SupportsListingCheckpoint && !unsupported.CachedPairPaused("p1") &&
            !unsupported.HasFullListing(Root()) && unsupported.ListingPage(RootId, "page") is null,
            "non-SQLite defaults do not certify listing or pause state");
        unsupported.BeginListing(Root(), "full");
        unsupported.SaveListingPage(RootId, "page", "cursor", "{}");
        unsupported.ForgetListing(RootId);
        Require(!unsupported.HasFullListing(Root()) && unsupported.ListingPage(RootId, "page") is null,
            "unsupported listing writes stay inert");
        Require(listingStore.SupportsListingCheckpoint, "SQLite advertises checkpoint support through its interface");
        listingStore.BeginListing(Root(), "full");
        listingStore.SaveListingPage(RootId, "page", "cursor", "{\"cursor\":\"cursor\"}");
        listingStore.BeginListing(Root(), "full");
        Require(listingStore.HasFullListing(Root()) && listingStore.ListingPage(RootId, "page") is not null,
            "same binding and mode replay the durable page");
        listingStore.BeginListing(Root(), "delta:cursor");
        Require(!listingStore.HasFullListing(Root()) && listingStore.ListingPage(RootId, "page") is null,
            "mode change clears replay pages without certifying a full listing");
        listingStore.SaveListingPage(RootId, "page", "cursor", "{}");
        listingStore.BeginListing(Root() with { RootPath = "/Other" }, "full");
        Require(!listingStore.HasFullListing(Root()) && listingStore.ListingPage(RootId, "page") is null,
            "binding change clears replay and cannot certify the old root");
        listingStore.ForgetListing(RootId);
        Require(!listingStore.HasFullListing(Root()), "interface forget clears listing checkpoint");
        store.SetPairPaused("pause-check", true);
        Require(listingStore.CachedPairPaused("pause-check"), "cached pause forwards to SQLite");
        store.SetPairPaused("pause-check", false);
        Require(!listingStore.CachedPairPaused("pause-check"), "cached pause observes local resume immediately");
        using var h = new Harness();
        var local = new FakeEndpoint(Side.A, "ra");
        var pair = new PairDef("p1", "Pair A", new("ra", SyncProviders.Local, null, "/local", "id:ra"), Root(), [], [], true);
        store.SavePair(pair);
        var poison = Stored("poison.txt", "id:poison", new string('a', 64));
        var seed = store.BeginGeneration(RootId, "full");
        store.WriteSnapshot(new(RootId, Side.B, seed, "saved", [poison], []));
        store.CompleteGeneration(seed, "saved", true);
        h.Wire.SeedFile("healthy.txt", "healthy");
        h.Wire.SeedListingRaw(new Dictionary<string, object>
        {
            [".tag"] = "file", ["name"] = "poison.txt",
            ["path_display"] = "/Pilot/poison.txt", ["path_lower"] = "/pilot/poison.txt"
        });
        store.SaveSyncRetry(pair.PairId, new("", "scan", 1, "root read failed", DateTimeOffset.MinValue,
            ReadRoots: [RootId]));
        var inventory = new DropboxInventory(Root(), Side.B, h.Client, store, pair.PairId);
        var provider = new FakeEndpointProvider().Add(pair.A, local).AddInventory(pair.B, inventory)
            .AddWriter(pair.B, h.Writer(Path.Combine(folder, "spool")));
        var settings = V2Settings.Default with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException), provider, settings, TimeProvider.System);
        var retry = typeof(SyncRun).GetMethod("RetryScanScopesAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing targeted scan owner.");
        var gen = store.BeginGeneration(RootId, "delta");
        var result = await (Task<(IReadOnlyList<string> Touched, IReadOnlyList<string> Incomplete,
            IReadOnlyDictionary<string, string> Reasons, bool RootSettles, IReadOnlyList<string> Observed)>)retry.Invoke(run,
            [pair, pair.B, Side.B, inventory, "saved", gen, (Func<string, bool>)(_ => true),
             (Action<string>)(_ => { }), new ProgressThrottle(null, TimeProvider.System, TimeSpan.Zero),
             CancellationToken.None, false])!;
        Require(result.RootSettles && !result.Incomplete.Contains(string.Empty),
            "a root re-list with one poison item settles root coverage");
        Require(result.Incomplete.Contains(poison.Path) && result.Reasons[poison.Path] == DropboxPathIdentity.MalformedReason,
            "the scan owner returns the poison path and exact reason for ordinary retry notices");
        Require(store.ByObject(RootId, poison.ObjectId)?.Hash == poison.Hash,
            "the skipped poison occupant is not inferred absent during hydration installation");
        Require(store.Live(RootId, ["healthy.txt"]).Count == 1,
            "healthy sibling observation publishes despite the poison item");
        var clean = new SyncRun.SideScan(gen, "saved", true, false, [], [],
            new Dictionary<string, DateTimeOffset?>(), []);
        var cloud = clean with { IncompleteSubtrees = result.Incomplete, IncompleteReasons = result.Reasons,
            RootDebtSettles = result.RootSettles };
        var record = typeof(SyncRun).GetMethod("RecordScanRetries", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing scan retry owner.");
        record.Invoke(run, [pair, new SyncRun.BothScan(clean, cloud), null]);
        Require(store.SyncRetryAt(pair.PairId, string.Empty) is null &&
            store.SyncRetryAt(pair.PairId, poison.Path)?.LastError == DropboxPathIdentity.MalformedReason,
            "root retry retires while exact per-item retry remains durable");
        Require(store.OpenNotices(pair.PairId).Any(notice => notice.Path == poison.Path),
            "the existing failure owner exposes the poison item's notice");
        Console.WriteLine("PASS root re-list preserves scoped failures and healthy observations");
    }
}
