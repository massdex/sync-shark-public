namespace Massdex.SyncShark.V2;

/// <summary>The `pairs rescan` verb: clearing a pair's stored cursors, and the refusals around it.</summary>
internal static partial class RunCliChecks
{
    // The first sync's preview commits no cursor, so each check first runs once to leave both roots a stored
    // cursor; only then does a rescan change what the next run would read.
    private static async Task SettleAsync(Fixture fx) =>
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);

    private static async Task Cli_Rescan_ClearsBothCursorsAsync()
    {
        var fx = Build();
        fx.A.Seed("x.txt", "same");
        fx.B.Seed("x.txt", "same");
        await FirstSyncAsync(fx);
        await SettleAsync(fx);
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        Require(fx.Store.Cursor("ra") is not null && fx.Store.Cursor("rb") is not null,
            "the settled pair holds a cursor for each root");
        var result = await DispatchAsync(runtime, ["--cli", "pairs", "rescan", "--pair", fx.Pair.PairId, "--yes"]);
        Require(result is SyncCliRescanData { Roots.Count: 2 } data &&
            data.Roots.Any(root => root.Side == "a" && root.RootPath == "C:\\a") &&
            data.Roots.Any(root => root.Side == "b" && root.RootPath == "/b"),
            "the default rescan reports both roots");
        Require(fx.Store.Cursor("ra") is null && fx.Store.Cursor("rb") is null,
            "the default rescan clears both cursors so the next run scans in full");
        Console.WriteLine("PASS Cli_Rescan_ClearsBothCursors");
    }

    private static async Task Cli_Rescan_SideSelectsOneRootAsync()
    {
        var fx = Build();
        fx.A.Seed("x.txt", "same");
        fx.B.Seed("x.txt", "same");
        await FirstSyncAsync(fx);
        await SettleAsync(fx);
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        var result = await DispatchAsync(runtime,
            ["--cli", "pairs", "rescan", "--pair", fx.Pair.PairId, "--side", "a", "--yes"]);
        Require(result is SyncCliRescanData { Roots.Count: 1 } single && single.Roots[0].Side == "a",
            "--side a reports only the local root");
        Require(fx.Store.Cursor("ra") is null && fx.Store.Cursor("rb") is not null,
            "--side a clears only that side's cursor");
        Console.WriteLine("PASS Cli_Rescan_SideSelectsOneRoot");
    }

    // A corrupt retained prepare blocks neither the reset nor the report: the record is discarded with its
    // reason, because a rescan is already the full re-read that a prepare would ask for.
    private static async Task Cli_Rescan_CorruptPrepareIsDiscardedAsync()
    {
        var fx = Build();
        fx.A.Seed("x.txt", "same");
        fx.B.Seed("x.txt", "same");
        var root = TempRoot();
        var record = Path.Combine(root, "v2", "first-sync-" + fx.Pair.PairId + ".choices.json");
        try
        {
            var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None);
            Require(File.Exists(record), "prepare retains a record to corrupt");
            await File.WriteAllTextAsync(record, "{\"digest\":");
            var result = await DispatchAsync(runtime,
                ["--cli", "pairs", "rescan", "--pair", fx.Pair.PairId, "--yes"]);
            Require(result is SyncCliRescanData { Roots.Count: 2 } data && data.Warnings.Count == 1 &&
                data.Warnings[0].Contains("Discarded unreadable first-sync prepare", StringComparison.Ordinal) &&
                data.Warnings[0].Contains("unparsable prepare record", StringComparison.Ordinal),
                "rescan discards a corrupt prepare and reports why");
            Require(!File.Exists(record), "rescan removes the unreadable record");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Rescan_CorruptPrepareIsDiscarded");
    }

    private static async Task Cli_Rescan_UnknownPairRefusedAsync()
    {
        var fx = Build();
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        var refusal = await RefusalAsync(async () =>
            await DispatchAsync(runtime, ["--cli", "pairs", "rescan", "--pair", "no-such-pair", "--yes"]));
        Require(refusal is SyncCliTargetException, "an unknown pair is refused as a missing target");
        Console.WriteLine("PASS Cli_Rescan_UnknownPairRefused");
    }

    private static async Task Cli_Rescan_RefusedWhileRunActiveAsync()
    {
        var fx = Build();
        fx.A.Seed("x.txt", "same");
        fx.B.Seed("x.txt", "same");
        await FirstSyncAsync(fx);
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = new GatedInventory(fx.A) { Block = block };
        fx.Provider.AddInventory(fx.Pair.A, gated);
        var run = runtime.RunPairAsync(fx.Pair.PairId, _ => { }, CancellationToken.None);
        await gated.Entered;
        var refusal = await RefusalAsync(async () =>
            await DispatchAsync(runtime, ["--cli", "pairs", "rescan", "--pair", fx.Pair.PairId, "--yes"]));
        Require(refusal is SyncCliBusyException, "a rescan is refused while a run holds the gate");
        block.SetResult();
        await run;
        Console.WriteLine("PASS Cli_Rescan_RefusedWhileRunActive");
    }
}
