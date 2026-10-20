namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task HashPauseAsync(string root)
    {
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: sql);
        for (var i = 0; i < 5; i++) { fx.A.Seed($"{i}.txt", "same"); fx.B.Seed($"{i}.txt", "same"); }
        var inventory = new ScriptedInventory(fx.A) { Hashless = true, OnHash = () => sql.SetPairPaused("p1", true) };
        fx.Provider.AddInventory(fx.Pair.A, inventory);
        var run = new SyncRun(sql, fx.Executor, fx.Provider, Settings with { LocalHashCheckpointFiles = 2 }, TimeProvider.System);
        await ExpectAsync<PairPausedException>(() => run.PreviewFirstSyncAsync("p1", new RecordingProgress(), CancellationToken.None));
        Require(inventory.HashedPaths == 2 && sql.Live(fx.Pair.A.RootId).Count(row => row.Hash is not null) == 2,
            "local hash batch persists before pause at next checkpoint");
        sql.SetPairPaused("p1", false);
        inventory.OnHash = null;
        await run.PreviewFirstSyncAsync("p1", new RecordingProgress(), CancellationToken.None);
        Require(inventory.HashedPaths == 5, "resume hashes only unfinished local rows");
    }

    private static void PauseCacheChecks(string root)
    {
        var clock = new OutputClock();
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, clock);
        using var external = SqliteStateStore.OpenPauseControl(root);
        Require(!sql.CachedPairPaused("p1"), "initial cached state is running");
        external.SetPairPaused("p1", true);
        var cached = true;
        for (var i = 0; i < 10_000; i++) cached &= !sql.CachedPairPaused("p1");
        Require(cached, "ten thousand action checks reuse the cached flag");
        clock.Advance(1);
        Require(!sql.CachedPairPaused("p1"), "no cross-process refresh before two seconds");
        clock.Advance(1);
        Require(sql.CachedPairPaused("p1"), "cross-process pause is observed at two seconds");
        sql.SetPairPaused("p1", false);
        Require(!sql.CachedPairPaused("p1"), "same-process resume updates the cache immediately");
        external.SetPairPaused("p1", true);
        clock.Advance(2);
        Require(sql.CachedPairPaused("p1"), "cross-process changes remain observable after local setters");
    }

    private static async Task FinishedApplyPauseAsync(string root)
    {
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: sql);
        fx.A.Seed("only.txt", "data");
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync("p1", new RecordingProgress(), CancellationToken.None);
        fx.WriterB.PutFailure = _ => { sql.SetPairPaused("p1", true); return null; };
        var result = await run.ApplyAsync("p1", plan, new RecordingProgress(), CancellationToken.None);
        Require(result.Outcome == RunOutcomes.Complete && sql.FirstSyncDone("p1") && fx.WriterB.Puts == 1,
            "pause during the last admitted effect does not skip first-sync finalization");
        var next = await run.IncrementalAsync("p1", new RecordingProgress(), CancellationToken.None);
        Require(next.Outcome == RunOutcomes.Paused, "completed apply leaves pause for next run admission");
    }

    private static async Task ScopePauseAsync(string root, string scope)
    {
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: sql);
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync("p1", new RecordingProgress(), CancellationToken.None);
        await run.ApplyAsync("p1", plan, new RecordingProgress(), CancellationToken.None);
        for (var i = 0; i < 12; i++) fx.A.Seed($"{i:D2}.txt", "data");
        var host = new V2SyncHost(sql, run, new ObservedExecutor(fx.Executor), null!, Settings,
            TimeProvider.System, SqliteStateStore.DatabasePath(root));
        var pausedScope = true;
        fx.WriterB.PutFailure = _ =>
        {
            host.SetActiveRunPausedAsync(true).GetAwaiter().GetResult();
            return null;
        };
        var pair = PairMapping.ToSyncPair(fx.Pair, true, Settings);
        string? summary = null;
        var result = await host.RunPairsAsync([pair], "pause-test", admissionProgress: text => summary = text);
        Require(summary?.Contains("(" + RunOutcomes.Paused + ")", StringComparison.Ordinal) == true &&
            result.Items.Count == 1 && !host.HasActiveRun && !sql.PairPaused("p1") && fx.WriterB.Puts < 12,
            scope + " pause exits the active run without persisting a per-pair pause");
        var inputs = new AutoSyncInputs(sql, run, null!, Settings, _ => pausedScope,
            () => TimeSpan.FromMinutes(1), TimeProvider.System, _ => { });
        Require(inputs.Ineligible(fx.Pair)?.Status == AutoStatus.Paused, scope + " blocks its own admissions");
        pausedScope = false;
        Require(await host.SetActiveRunPausedAsync(false) is null && inputs.Ineligible(fx.Pair) is null,
            scope + " resume after run exit admits the pair");
        sql.SetPairPaused("p1", true);
        Require(await host.SetActiveRunPausedAsync(false) is null && sql.PairPaused("p1"),
            scope + " resume never clears an explicit per-pair pause");
    }

    private static async Task DeferredEvidencePauseAsync(string root)
    {
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: sql);
        fx.A.Seed("held.txt", "data");
        var evidence = new DeferredInventory(fx.B);
        fx.Provider.AddInventory(fx.Pair.B, evidence);
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync("p1", new RecordingProgress(), CancellationToken.None);
        Require(plan.HeldDeletions.Count == 1, "fixture enters deferred evidence lane");
        evidence.Pause = true;
        var result = await run.ApplyAsync("p1", plan, new RecordingProgress(), CancellationToken.None, ReviewedHeld(plan));
        Require(result.Outcome == RunOutcomes.Paused && !sql.FirstSyncDone("p1") && fx.WriterB.Puts == 0,
            "deferred evidence pause propagates as paused, not pending or a completed apply");
    }

    internal static async Task PauseChecksAsync(string root)
    {
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: sql);
        for (var i = 0; i < 12; i++) fx.A.Seed($"{i:D2}.txt", "payload");
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync("p1", new RecordingProgress(), CancellationToken.None);
        fx.WriterB.PutFailure = _ => { sql.SetPairPaused("p1", true); return null; };
        var paused = await run.ApplyAsync("p1", plan, new RecordingProgress(), CancellationToken.None);
        Require(paused.Outcome == RunOutcomes.Paused && paused.FailureCount == 0,
            "apply pause is a normal non-failure outcome");
        Require(fx.WriterB.Puts > 0 && fx.WriterB.Puts < 12 && !sql.FirstSyncDone("p1"),
            "pause drains admitted writes but does not finish the unexecuted first sync");
        var before = fx.WriterB.Puts;
        var stillPaused = await run.ApplyAsync("p1", plan, new RecordingProgress(), CancellationToken.None);
        Require(stillPaused.Outcome == RunOutcomes.Paused && fx.WriterB.Puts == before,
            "paused admission starts no effect");
        fx.WriterB.PutFailure = null;
        sql.SetPairPaused("p1", false);
        var next = await run.PreviewFirstSyncAsync("p1", new RecordingProgress(), CancellationToken.None);
        var done = await run.ApplyAsync("p1", next, new RecordingProgress(), CancellationToken.None);
        Require(done.Outcome == RunOutcomes.Complete && sql.FirstSyncDone("p1") && fx.WriterB.Puts == 12,
            "resume replans only unfinished writes, without duplicating completed effects");
        sql.SetPairPaused("p1", true);
        var inputs = new AutoSyncInputs(sql, run, null!, Settings, _ => false,
            () => TimeSpan.FromMinutes(1), TimeProvider.System, _ => { });
        Require(inputs.Ineligible(fx.Pair)?.Status == AutoStatus.Paused, "AutoSync skips durable paused pair");
        sql.SetPairPaused("p1", false);
        Require(inputs.Ineligible(fx.Pair) is null, "AutoSync resumes without changing pair enablement");
        PauseCacheChecks(Path.Combine(root, "cache"));
        await FinishedApplyPauseAsync(Path.Combine(root, "finished"));
        await ScopePauseAsync(Path.Combine(root, "global"), "global");
        await ScopePauseAsync(Path.Combine(root, "account"), "account");
        await DeferredEvidencePauseAsync(Path.Combine(root, "evidence"));
        await HashPauseAsync(Path.Combine(root, "hash-pause"));
        Console.WriteLine("pause: apply safe-point drain, local hash checkpoint, admission, resume and AutoSync checks passed");
    }
}
