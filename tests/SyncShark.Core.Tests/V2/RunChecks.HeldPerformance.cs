namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // Bounded offline entrypoint; no scale suite or provider calls.
    public static async Task RunHeldPerformanceChecksAsync()
    {
        await HeldFiles_ReuseExclusionsAsync();
        await HeldFolders_RefreshExclusionsAndScopeBaselinesAsync();
        await HeldFiles_FreshFlagsPreventEffectsAsync();
    }

    private static async Task HeldFiles_ReuseExclusionsAsync()
    {
        var fx = Build(record: true);
        using var deadline = new CancellationTokenSource(GateTimeout);
        for (var i = 0; i < 40; i++) fx.A.Seed($"held-{i:D2}.txt", "content");
        var evidence = new DeferredInventory(fx.B) { Block = true };
        fx.Provider.AddInventory(fx.Pair.B, evidence);
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
        var applying = run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), deadline.Token, ReviewedHeld(plan));
        await evidence.Started.Task.WaitAsync(deadline.Token);
        var before = fx.Recorder!.FlaggedReads;
        evidence.Release.TrySetResult();
        var report = await applying;
        Require(report.Deferred == 0 && report.Uploaded == 40, "all held files execute through the real lane");
        Require(fx.Recorder.FlaggedReads == before,
            "resolved held files reuse the lane snapshot rather than scanning flags per path/action");
        Console.WriteLine("PASS HeldFiles_ReuseExclusions");
    }

    private static async Task HeldFolders_RefreshExclusionsAndScopeBaselinesAsync()
    {
        foreach (var flags in new[] { ItemFlags.None, ItemFlags.Link, ItemFlags.Placeholder })
        {
            var fx = Build(record: true);
            using var deadline = new CancellationTokenSource(GateTimeout);
            fx.A.Seed("folder", "", ItemKind.Folder);
            fx.A.Seed("folder/held.txt", "content");
            fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B));
            var refreshing = false;
            var readsBeforeRefresh = -1;
            var baselinesBeforeRefresh = -1;
            var writer = new SignalingWriter(fx.WriterA)
            {
                BeforeObserve = (path, _) =>
                {
                    if (path == "folder" && !refreshing)
                    {
                        refreshing = true;
                        readsBeforeRefresh = fx.Recorder!.FlaggedReads;
                        baselinesBeforeRefresh = fx.Recorder.AllBaselineReads;
                    }
                    return Task.CompletedTask;
                },
                AfterObserve = item => refreshing && item is { Path: "folder/held.txt" }
                    ? item with { Flags = flags } : item
            };
            // Install after preview so the fresh link/stub exists only during folder refresh.
            var run = fx.Run();
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
            fx.Provider.AddWriter(fx.Pair.A, writer);
            var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), deadline.Token, ReviewedHeld(plan));
            Require(refreshing && fx.Recorder!.FlaggedReads > readsBeforeRefresh,
                "a folder refresh invalidates the lane guard before the folder's action check");
            Require(fx.Recorder!.AllBaselineReads == baselinesBeforeRefresh,
                "folder classification does not load the entire pair's baselines");
            foreach (var side in new[] { Side.A, Side.B })
                Require(fx.Recorder.BaselinePrefixReads.Any(read => read.Side == side &&
                    read.Prefixes.SequenceEqual(["folder"])), "folder baselines queried by prefix on both sides");
            Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 &&
                await fx.A.ObserveAsync("folder/held.txt", deadline.Token) is not null,
                "surviving children, including newly flagged links/stubs, are never deleted");
            if (flags == ItemFlags.None) Require(report.Deferred == 0, "ordinary refreshed folder finishes");
            Require(fx.Store.Live(fx.Pair.A.RootId, ["folder/held.txt"]).Single().Flags == flags,
                "the refresh published the current flags before checking folder effects");
        }
        Console.WriteLine("PASS HeldFolders_RefreshExclusionsAndScopeBaselines");
    }

    private static async Task HeldFiles_FreshFlagsPreventEffectsAsync()
    {
        foreach (var flags in new[] { ItemFlags.Link, ItemFlags.Placeholder })
        {
            var fx = Build();
            using var deadline = new CancellationTokenSource(GateTimeout);
            fx.A.Seed("held.txt", "content");
            var evidence = new DeferredInventory(fx.B) { Block = true };
            fx.Provider.AddInventory(fx.Pair.B, evidence);
            var run = fx.Run();
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
            var applying = run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), deadline.Token, ReviewedHeld(plan));
            await evidence.Started.Task.WaitAsync(deadline.Token);
            fx.Provider.AddWriter(fx.Pair.A, new SignalingWriter(fx.WriterA)
            {
                AfterObserve = item => item is null ? null : item with { Flags = flags }
            });
            evidence.Release.TrySetResult();
            var report = await applying;
            Require(report.Deferred == 0 && fx.Store.FirstSyncDone(fx.Pair.PairId) &&
                fx.Store.SyncRetryAt(fx.Pair.PairId, "held.txt")?.Held is not null &&
                fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
                "fresh held-file link/stub flags prevent all effects despite the cached lane snapshot");
        }
        Console.WriteLine("PASS HeldFiles_FreshFlagsPreventEffects");
    }
}
