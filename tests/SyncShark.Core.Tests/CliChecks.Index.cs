using Massdex.SyncShark;
using Massdex.SyncShark.V2;

// Focused CLI checks for the index surface: the real parser, the real dispatcher, the real core v2
// runtime over an isolated SQLite store. No provider is contacted; the fixture's HTTP handler refuses
// any request, so a status read that probed an endpoint would fail rather than silently pass.
internal static partial class CliChecks
{
    internal static async Task IndexCommandChecksAsync(string root)
    {
        Directory.CreateDirectory(root);
        var local = Path.Combine(root, "indexed root");
        Directory.CreateDirectory(local);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var http = new HttpClient(new NoNetworkHandler());
        using var composition = EngineComposition.Open(
            new EngineHostInputs(root, instance.StateAuthority, http), V2Settings.Default, TimeProvider.System);
        var localRootId = "r00000000000000aa";
        var cloudRootId = "r00000000000000bb";
        var pair = new PairDef("index-pair", "Index fixture",
            new RootDef(localRootId, SyncProviders.Local, null, local, string.Empty),
            new RootDef(cloudRootId, SyncProviders.Dropbox, "index-account", "/Index fixture", "id:index"),
            [], [], true);
        composition.Store.SavePair(pair);
        var runtime = V2CliRuntime.Borrow(instance, composition, root);

        // Status is a durable read: one local root, no gap protection and no pending rehash in a fresh store.
        var status = await RunCliAsync(["--cli", "index", "status", "--json"], () => runtime);
        Expect("index status exits 0", status, 0);
        var statusData = JsonData(status, "index status", "index status");
        var roots = statusData["roots"]!.AsArray();
        Require(statusData["count"]!.GetValue<int>() == 1 && roots.Count == 1,
            "index status reports exactly the retained local roots");
        Require(roots[0]!["rootId"]!.GetValue<string>() == localRootId &&
                roots[0]!["gapProtected"]!.GetValue<bool>() == false &&
                roots[0]!["rehashPending"]!.GetValue<long>() == 0,
            "index status carries the local root identity and its durable debt");

        // One independent pass completes immediately on a debt-free root and never needs a sync run.
        var ran = await RunCliAsync(["--cli", "index", "run", "--root", localRootId, "--yes", "--json"],
            () => runtime);
        Expect("index run exits 0 for a retained local root", ran, 0);
        var runData = JsonData(ran, "index run", "index run");
        Require(runData["rootId"]!.GetValue<string>() == localRootId &&
                runData["outcome"]!.GetValue<string>() == RunOutcomes.Complete,
            "index run reports the exact root and a completed independent pass");

        // The same pause authority an ordinary run obeys: a paused pair pauses the pass and stays paused.
        SqliteStateStore sql = composition.Store as SqliteStateStore ??
            throw new InvalidOperationException("The fixture store is the shared SQLite store");
        sql.SetPairPaused(pair.PairId, true);
        var pausedByPause = await RunCliAsync(["--cli", "index", "run", "--root", localRootId, "--yes", "--json"],
            () => runtime);
        Expect("a paused pair pauses index run", pausedByPause, 0);
        var pausedData = JsonData(pausedByPause, "index run", "index run");
        Require(pausedData["outcome"]!.GetValue<string>() == RunOutcomes.Paused && sql.PairPaused(pair.PairId),
            "index run reports paused and never resumes the paused pair");
        sql.SetPairPaused(pair.PairId, false);

        // A disabled pair is equally off; paused is informational and changes no enablement.
        composition.Store.SavePair(pair with { Enabled = false });
        var pausedByDisable = await RunCliAsync(["--cli", "index", "run", "--root", localRootId, "--yes", "--json"],
            () => runtime);
        Expect("a disabled pair pauses index run", pausedByDisable, 0);
        Require(JsonData(pausedByDisable, "index run", "index run")["outcome"]!.GetValue<string>() == RunOutcomes.Paused &&
                composition.Store.Pair(pair.PairId)!.Enabled == false,
            "index run reports paused and never enables the disabled pair");
        composition.Store.SavePair(pair);

        // Exact target: an unknown identity and a non-local root are refused before any work.
        var unknown = await RunCliAsync(["--cli", "index", "run", "--root", "rffffffffffffffff", "--yes", "--json"],
            () => runtime);
        Expect("index run refuses an unknown root", unknown, 4);
        Require(JsonErrorCode(unknown, "missing root", "index run") == "missing_target",
            "index run names the missing exact target");
        var cloud = await RunCliAsync(["--cli", "index", "run", "--root", cloudRootId, "--yes", "--json"],
            () => runtime);
        Expect("index run refuses a non-local root", cloud, 4);

        // Argument validation is state-free and precedes any runtime.
        var missingConfirmation = await RunCliAsync(["--cli", "index", "run", "--root", localRootId, "--json"],
            NeverOpensState);
        Expect("index run requires --yes before opening state", missingConfirmation, 2);
        var missingRoot = await RunCliAsync(["--cli", "index", "run", "--yes", "--json"], NeverOpensState);
        Expect("index run requires its exact --root before opening state", missingRoot, 2);
        var ambiguous = await RunCliAsync(["--cli", "index", "run", "--root", "two roots", "--yes", "--json"],
            NeverOpensState);
        Expect("index run refuses a non-exact root selector", ambiguous, 2);

        Console.WriteLine("CLI index status/run, pause hold and exact-target checks passed");
    }
}
