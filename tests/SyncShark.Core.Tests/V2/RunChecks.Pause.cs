namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task HashPauseAsync(string root)
    {
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: sql, accountId: PauseAccount);
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
        var fx = Build(suppliedStore: sql, accountId: PauseAccount);
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

    private const string PauseAccount = "connection-000000000000000000000000";
    private const string OtherPauseAccount = "gdrive-connection-000000000000000000000000";

    private static SyncPair PauseView(PairDef pair) => PairMapping.ToSyncPair(pair with
        { A = pair.A with { RootObjectId = "id:local:00000001:0000000000000001" } }, true, Settings);

    // The temporary active-run pause: it is never written as a durable pair pause, and a resume after the run
    // exits leaves an explicit pair pause untouched. The durable global and account scopes have their own checks.
    private static async Task ScopePauseAsync(string root, string scope)
    {
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: sql, accountId: PauseAccount);
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
        var pair = PauseView(fx.Pair);
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

    // Independent pairs may run concurrently, so the old "pair 2 never starts" premise is not the rule. A durable
    // global pause saved from pair 1's completion must stop pair 2 at its first safe point: the run-start gate holds
    // pair 2 out of the real SyncRun until the pause is saved, then the run reports Paused with zero new effects.
    // The pause and resume both go through the real CLI operations over the same preferences store the host reads.
    private static async Task GlobalPauseBetweenPairsAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: sql, accountId: PauseAccount);
        var run = fx.Run();
        Require((await FirstSyncAsync(run, fx)).Outcome == RunOutcomes.Complete,
            "pair 1 fixture completes first sync before between-pairs pause");
        var second = await AddSyncedPairAsync(fx, run, "p2");
        var preferences = new UiPreferencesStore(instance.StateAuthority);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedRun = new PairStartGate(run, gatedPairId: "p2", observedPairId: "p1", gate.Task);
        var host = new V2SyncHost(sql, gatedRun, new ObservedExecutor(fx.Executor), null!, Settings,
            TimeProvider.System, SqliteStateStore.DatabasePath(root))
        {
            PauseSource = preferences.Load
        };
        fx.A.Seed("first-run.txt", "one");
        second.A.Seed("second-run.txt", "two");
        var before = second.Writer.Puts;
        var cli = new PauseCliRuntime(instance.StateAuthority);
        SyncCliPauseData? control = null;
        var pausing = true;
        var summaries = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var putsAtPause = -1;
        SyncAuthorityBatchResult? batch = null;
        try
        {
            batch = await host.RunPairsAsync(
                [PauseView(fx.Pair), PauseView(second.Def)],
                "global-pause-batch", admissionProgress: text =>
                {
                    summaries.Enqueue(text);
                    if (!pausing) return;
                    pausing = false;
                    // Pair 2 is still held at its run start, so this first completion is pair 1's.
                    putsAtPause = second.Writer.Puts;
                    // The same operation the app's pause menu and the CLI verb use: save the durable scope first.
                    control = cli.SetPauseAsync(PauseScopes.Global, null, true, null, true, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    // Only now may pair 2 reach the real SyncRun's first safe point.
                    gate.TrySetResult();
                });
        }
        finally
        {
            gate.TrySetResult();
        }
        var result = batch!;
        var now = TimeProvider.System.GetUtcNow();
        Require(control is { Scope: PauseScopes.Global, Paused: true },
            "the CLI global pause saved the durable scope");
        Require(gatedRun.EnteredWhileOtherRunning,
            "pair 2 reaches its run start while pair 1 is still running (independent pairs)");
        var order = summaries.ToArray();
        Require(result.Items.Count == 2 && order.Length == 2 &&
            order[0].Contains("(" + RunOutcomes.Complete + ")", StringComparison.Ordinal) &&
            order[1].Contains("(" + RunOutcomes.Paused + ")", StringComparison.Ordinal),
            "the durable global pause stops the second pair at its first safe point");
        var completed = result.Items.Single(item => item.Pair.Id == "p1");
        Require(completed.FailureType is null && completed.Result is { Uploaded: > 0 },
            "pair 1 completes its own work before the pause is saved");
        var held = result.Items.Single(item => item.Pair.Id == "p2");
        Require(held.FailureType is null && held.Result is
            { Outcome: RunOutcomes.Paused, Uploaded: 0, Downloaded: 0, AppliedItems: 0, Decisions: { Count: 0 } },
            "the held pair's Paused item carries no counted work");
        Require(second.Writer.Puts == putsAtPause && putsAtPause == before &&
            !sql.PairPaused("p1") && !sql.PairPaused("p2"),
            "zero new puts after the global pause was saved between independent pairs, without a per-pair pause");
        Require(PauseStateProjection.GlobalPaused(preferences.Load(), now),
            "the global preference remains saved after the run exits");
        var inputs = new AutoSyncInputs(sql, run, null!, Settings,
            _ => PauseStateProjection.GlobalPaused(preferences.Load(), TimeProvider.System.GetUtcNow()),
            () => TimeSpan.FromMinutes(1), TimeProvider.System, _ => { });
        Require(inputs.Ineligible(fx.Pair)?.Status == AutoStatus.Paused,
            "the saved global pause blocks admission after the run exits");
        var resumed = cli.SetPauseAsync(PauseScopes.Global, null, false, null, false, CancellationToken.None)
            .GetAwaiter().GetResult();
        Require(resumed is { Scope: PauseScopes.Global, Paused: false } && inputs.Ineligible(fx.Pair) is null,
            "the CLI global resume clears the scope and admits the pair");
        var resumedBatch = await host.RunPairsAsync(
            [PauseView(fx.Pair), PauseView(second.Def)],
            "global-resume-batch");
        Require(resumedBatch.Items.Count == 2 && second.Writer.Puts > before,
            "after the CLI resume the next batch runs the pair the pause held back");
    }

    // The account scope alone stops the second pair: the global preference is untouched, and the run-start gate
    // makes the durable account pause visible to the real SyncRun's first safe point before pair 2 can act.
    private static async Task AccountPauseBetweenPairsAsync(string root)
    {
        Directory.CreateDirectory(root);
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: sql, accountId: PauseAccount);
        var run = fx.Run();
        Require((await FirstSyncAsync(run, fx)).Outcome == RunOutcomes.Complete,
            "pair 1 fixture completes first sync before between-pairs pause");
        var second = await AddSyncedPairAsync(fx, run, "p2");
        var saved = new UiPreferences(UiPreferencesStore.SystemTheme);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedRun = new PairStartGate(run, gatedPairId: "p2", observedPairId: "p1", gate.Task);
        var host = new V2SyncHost(sql, gatedRun, new ObservedExecutor(fx.Executor), null!, Settings,
            TimeProvider.System, SqliteStateStore.DatabasePath(root))
        {
            PauseSource = () => saved
        };
        fx.A.Seed("first-run.txt", "one");
        second.A.Seed("second-run.txt", "two");
        var before = second.Writer.Puts;
        var pausing = true;
        var summaries = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var putsAtPause = -1;
        SyncAuthorityBatchResult? batch = null;
        try
        {
            batch = await host.RunPairsAsync(
                [PauseView(fx.Pair), PauseView(second.Def)],
                "account-pause-batch", admissionProgress: text =>
                {
                    summaries.Enqueue(text);
                    if (!pausing) return;
                    pausing = false;
                    putsAtPause = second.Writer.Puts;
                    saved = saved with
                    {
                        AccountPauses = [new SyncAccountPause(PauseAccount, true, null, TimeZoneInfo.Local.Id)]
                    };
                    gate.TrySetResult();
                });
        }
        finally
        {
            gate.TrySetResult();
        }
        var result = batch!;
        var now = TimeProvider.System.GetUtcNow();
        Require(!PauseStateProjection.GlobalPaused(saved, now) &&
            PauseStateProjection.AccountPaused(saved, PauseAccount, now),
            "the account pause leaves the global scope untouched");
        Require(gatedRun.EnteredWhileOtherRunning,
            "pair 2 reaches its run start while pair 1 is still running (independent pairs)");
        var order = summaries.ToArray();
        Require(result.Items.Count == 2 && order.Length == 2 &&
            order[0].Contains("(" + RunOutcomes.Complete + ")", StringComparison.Ordinal) &&
            order[1].Contains("(" + RunOutcomes.Paused + ")", StringComparison.Ordinal),
            "the durable account pause stops the second pair at its first safe point");
        var held = result.Items.Single(item => item.Pair.Id == "p2");
        Require(held.FailureType is null && held.Result is
            { Outcome: RunOutcomes.Paused, Uploaded: 0, Downloaded: 0, AppliedItems: 0, Decisions: { Count: 0 } },
            "the held pair's Paused item carries no counted work");
        Require(second.Writer.Puts == putsAtPause && putsAtPause == before &&
            !sql.PairPaused("p1") && !sql.PairPaused("p2"),
            "zero new puts after the account pause was saved between independent pairs, without a per-pair pause");
        var inputs = new AutoSyncInputs(sql, run, null!, Settings,
            def => PauseStateProjection.AccountPaused(saved, def.A.AccountId, now) ||
                PauseStateProjection.AccountPaused(saved, def.B.AccountId, now),
            () => TimeSpan.FromMinutes(1), TimeProvider.System, _ => { });
        Require(inputs.Ineligible(fx.Pair)?.Status == AutoStatus.Paused,
            "the saved account pause blocks admission after the run exits");
        saved = saved with { AccountPauses = [] };
        var resumedBatch = await host.RunPairsAsync(
            [PauseView(fx.Pair), PauseView(second.Def)],
            "account-resume-batch");
        Require(resumedBatch.Items.Count == 2 && second.Writer.Puts > before,
            "clearing the account scope lets the next batch run pair 2");
    }

    // Carries the real run through a run-start gate on one pair. The host admits both pairs independently, so the
    // gated pair reaches IncrementalAsync but can plan or apply nothing until the check releases it. The durable
    // scope pause saved from the other pair's completion is therefore already visible to the real SyncRun's first
    // safe point, which must report Paused without an effect of its own. The gate is bounded by the run token so a
    // cancelled batch never parks the pair.
    private sealed class PairStartGate(ISyncRun inner, string gatedPairId, string observedPairId, Task gate)
        : ISyncRun
    {
        private static readonly TimeSpan EnterBound = TimeSpan.FromSeconds(5);

        private readonly TaskCompletionSource _gatedEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _enteredWhileOtherRunning;
        private volatile bool _observedRunning;

        /// <summary>True when the gated pair had entered its run start before the observed pair's run finished.</summary>
        internal bool EnteredWhileOtherRunning => _enteredWhileOtherRunning;

        public async Task<RunReport> IncrementalAsync(string pairId, IProgress<RunProgress> p, CancellationToken ct)
        {
            if (string.Equals(pairId, observedPairId, StringComparison.Ordinal))
            {
                _observedRunning = true;
                try
                {
                    // Both host calls must be admitted concurrently before either can finish.
                    // A serial host fails this bound; it cannot manufacture the concurrency proof
                    // by waiting for p2 only after p1's real run has already completed.
                    await _gatedEntered.Task.WaitAsync(EnterBound, ct).ConfigureAwait(false);
                    return await inner.IncrementalAsync(pairId, p, ct).ConfigureAwait(false);
                }
                finally { _observedRunning = false; }
            }
            if (string.Equals(pairId, gatedPairId, StringComparison.Ordinal))
            {
                _enteredWhileOtherRunning = _observedRunning;
                _gatedEntered.TrySetResult();
                await gate.WaitAsync(EnterBound, ct).ConfigureAwait(false);
            }
            return await inner.IncrementalAsync(pairId, p, ct).ConfigureAwait(false);
        }

        public Task<Plan> PreviewFirstSyncAsync(string pairId, IProgress<RunProgress> p, CancellationToken ct) =>
            inner.PreviewFirstSyncAsync(pairId, p, ct);

        public Task<RunReport> ApplyAsync(string pairId, Plan approved, IProgress<RunProgress> p,
            CancellationToken ct) => inner.ApplyAsync(pairId, approved, p, ct);

        public Task<IReadOnlyList<RecoveryRestoreItem>> RestoreRecoveryPageAsync(string pairId, RecoveryQuery query,
            bool asCopy, bool dryRun, CancellationToken ct) =>
            inner.RestoreRecoveryPageAsync(pairId, query, asCopy, dryRun, ct);

        public Task StopAsync() => inner.StopAsync();
    }

    // Save through the real preferences store during pair 1, without setting the temporary active-run flag.
    private static async Task ScopePauseMidPairAsync(string root, string? account, Exception? readError = null)
    {
        Directory.CreateDirectory(root);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var clock = new OutputClock();
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, clock);
        var fx = Build(suppliedStore: sql, accountId: PauseAccount);
        var run = fx.Run();
        Require((await FirstSyncAsync(run, fx)).Outcome == RunOutcomes.Complete,
            "pair 1 fixture completes its first sync");
        var second = await AddSyncedPairAsync(fx, run, "p2");
        var preferences = new UiPreferencesStore(instance.StateAuthority);
        var reads = 0;
        var failNextRead = 0;
        // Decision 72 / RQ-141: both pairs enter independently. Hold only the fixture's second
        // run at its start until pair 1 has exercised the mid-effect refresh and stopped safely.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedRun = new PairStartGate(run, gatedPairId: "p2", observedPairId: "p1", gate.Task);
        var host = new V2SyncHost(sql, gatedRun, new ObservedExecutor(fx.Executor), null!, Settings,
            clock, SqliteStateStore.DatabasePath(root))
        {
            PauseSource = () =>
            {
                Interlocked.Increment(ref reads);
                if (Interlocked.Exchange(ref failNextRead, 0) == 1) throw readError!;
                return preferences.Load();
            }
        };
        for (var i = 0; i < 12; i++) fx.A.Seed($"{i:D2}.txt", "data");
        second.A.Seed("second-run.txt", "two");
        var saved = false;
        var secondPutsAtPause = -1;
        fx.WriterB.PutFailure = _ =>
        {
            if (saved) return null;
            saved = true;
            if (readError is not null)
            {
                var beforeFailure = Volatile.Read(ref reads);
                Interlocked.Exchange(ref failNextRead, 1);
                clock.Advance(2);
                PairPause.Check();
                Require(Volatile.Read(ref reads) == beforeFailure + 1,
                    "mid-pair preference read failure keeps the last running value without failing the pair");
                PairPause.Check();
                Require(Volatile.Read(ref reads) == beforeFailure + 2,
                    "the very next safe point retries without waiting for another refresh interval");
            }
            preferences.SaveAsync(preferences.Load() with
            {
                PauseUntilResumed = account is null,
                AccountPauses = account is null ? [] : [new SyncAccountPause(account, true, null, TimeZoneInfo.Local.Id)]
            }).GetAwaiter().GetResult();
            secondPutsAtPause = second.Writer.Puts;
            var before = Volatile.Read(ref reads);
            for (var i = 0; i < 10_000; i++) PairPause.Check();
            clock.Advance(1);
            PairPause.Check();
            Require(Volatile.Read(ref reads) == before, "safe points reuse the scope cache before two seconds");
            clock.Advance(1);
            var checkPaused = false;
            try { PairPause.Check(); }
            catch (PairPausedException) { checkPaused = true; }
            Require(checkPaused == (account is null or PauseAccount) && Volatile.Read(ref reads) == before + 1,
                "the callback safe point reaches the scope cache and refreshes its pause at two seconds");
            return null;
        };
        var pairs = new[] { PauseView(fx.Pair),
            PauseView(second.Def) };
        var summaries = new System.Collections.Concurrent.ConcurrentQueue<string>();
        SyncAuthorityBatchResult result;
        try
        {
            result = await host.RunPairsAsync(pairs, "scope-mid-pair", admissionProgress: text =>
            {
                summaries.Enqueue(text);
                gate.TrySetResult();
            });
        }
        finally { gate.TrySetResult(); }
        Require(gatedRun.EnteredWhileOtherRunning,
            "mid-pair pause fixture admits both independent pairs before pair 1 finishes");
        Require(saved && (account is null
                ? PauseStateProjection.GlobalPaused(preferences.Load(), clock.GetUtcNow())
                : PauseStateProjection.AccountPaused(preferences.Load(), account, clock.GetUtcNow()) &&
                    !PauseStateProjection.GlobalPaused(preferences.Load(), clock.GetUtcNow())),
            "mid-pair pause is durable in the requested scope");
        if (account is null or PauseAccount)
        {
            Require(result.Items.Count == 2 && summaries.Count == 2 &&
                result.Items.All(item => item.FailureType is null && item.Result?.Outcome == RunOutcomes.Paused) &&
                summaries.All(text => text.Contains("(" + RunOutcomes.Paused + ")", StringComparison.Ordinal)) &&
                fx.WriterB.Puts == 1,
                "durable scope pause stops pair 1 after its admitted effect and both pairs report Paused");
            Require(secondPutsAtPause == 0 && second.Writer.Puts == secondPutsAtPause &&
                result.Items.Single(item => item.Pair.Id == "p2").Result is
                    { Uploaded: 0, Downloaded: 0, AppliedItems: 0, Decisions: { Count: 0 } },
                "pair 2 reaches its safe point after the saved pause without starting any effect");
        }
        else
        {
            Require(result.Items.Count == 2 && fx.WriterB.Puts == 12 && second.Writer.Puts == 1 &&
                summaries.All(text => text.Contains("(" + RunOutcomes.Complete + ")", StringComparison.Ordinal)),
                "a different account's durable pause does not stop either pair");
        }
        Require(!sql.PairPaused("p1") && !sql.PairPaused("p2"), "scope pause never writes a pair pause");
        fx.WriterB.PutFailure = null;
        if (account is null)
        {
            // Resume each scope independently while the other still pauses this same pair.
            foreach (var resumeGlobal in new[] { false, true })
            {
                await preferences.SaveAsync(preferences.Load() with
                {
                    PauseUntilResumed = true,
                    AccountPauses = [new SyncAccountPause(PauseAccount, true, null, TimeZoneInfo.Local.Id)]
                });
                var both = preferences.Load();
                await preferences.SaveAsync(resumeGlobal
                    ? both with { PauseUntilResumed = false }
                    : both with { AccountPauses = [] });
                var putsBefore = fx.WriterB.Puts;
                summaries.Clear();
                var held = await host.RunPairsAsync([pairs[0]], "partial-scope-resume", admissionProgress: summaries.Enqueue);
                Require(held.Items.Count == 1 && summaries.Single().Contains("(" + RunOutcomes.Paused + ")",
                    StringComparison.Ordinal) && fx.WriterB.Puts == putsBefore && !sql.PairPaused("p1"),
                    "resuming one scope leaves a single-pair run paused at its first safe point by the other");
            }
        }
        await preferences.SaveAsync(preferences.Load() with { PauseUntilResumed = false, AccountPauses = [] });
        second.A.Seed("after-resume.txt", "three");
        summaries.Clear();
        var resumed = await host.RunPairsAsync(pairs, "account-mid-pair-resume", admissionProgress: summaries.Enqueue);
        Require(resumed.Items.Count == 2 && fx.WriterB.Puts == 12 && second.Writer.Puts == 2 &&
            summaries.All(text => text.Contains("(" + RunOutcomes.Complete + ")", StringComparison.Ordinal)),
            "durable resume lets a fresh batch finish both pairs without a stale scope cache");
    }

    // Adds a second pair that has already completed a first sync, so a pair skipped by a batch is evidence of
    // the pause rather than of a pair that could never have run.
    private static async Task<(PairDef Def, FakeEndpoint A, CountingWriter Writer)> AddSyncedPairAsync(
        Fixture fx, SyncRun run, string id)
    {
        var a = new FakeEndpoint(Side.A, "ra-" + id);
        var b = new FakeEndpoint(Side.B, "rb-" + id);
        var writer = new CountingWriter(b);
        var def = new PairDef(id, id,
            new RootDef("ra-" + id, SyncProviders.Local, null, "C:\\" + id, "id:" + id),
            new RootDef("rb-" + id, SyncProviders.Dropbox, fx.Pair.B.AccountId, "/" + id, "id2:" + id), [], [], true);
        fx.Store.SavePair(def);
        fx.Provider.Add(def.A, a).Add(def.B, b).AddWriter(def.B, writer);
        var plan = await run.PreviewFirstSyncAsync(id, new RecordingProgress(), CancellationToken.None);
        var applied = await run.ApplyAsync(id, plan, new RecordingProgress(), CancellationToken.None);
        Require(applied.Outcome == RunOutcomes.Complete, id + " fixture pair completes its first sync");
        return (def, a, writer);
    }

    // The real CLI pause operations over a state authority; these checks never perform a provider call.
    private sealed class PauseCliRuntime : SyncCliOperationsBase
    {
        private readonly SyncAuthorityRuntimeHost _host;
        private readonly PilotStateAuthority _state;
        internal PauseCliRuntime(PilotStateAuthority state)
        {
            _state = state;
            _host = new SyncAuthorityRuntimeHost(state, new HttpClient(new NoNetworkHandler(), disposeHandler: false));
        }
        protected override SyncAuthorityRuntimeHost Host => _host;
        protected override PilotStateAuthority StateAuthority => _state;
        public override string Mode => "run-pause-checks";

        private sealed class NoNetworkHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("This pause check performs no provider call.");
        }
    }

    private static async Task DeferredEvidencePauseAsync(string root)
    {
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: sql, accountId: PauseAccount);
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

    // F4 regression: a pause raised by the delete's source-absence check must surface as Paused, delete
    // nothing, commit no blocked-delete result or notice, and leave the delete for the next run to replan.
    private static async Task DeleteSourceAbsencePauseAsync(string root)
    {
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: sql, accountId: PauseAccount);
        fx.A.Seed("gone.txt", "same");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        var source = await fx.A.ObserveAsync("gone.txt", CancellationToken.None);
        await fx.A.DeleteAsync(new SyncAction("remove-source", 6, ActionKind.Delete, Side.A, "gone.txt", null, null,
            source, false, null), CancellationToken.None);
        // The delete targets side B, so its absence probe runs against side A's writer.
        var probe = new SignalingWriter(fx.WriterA) { PauseAbsence = true };
        fx.Provider.AddWriter(fx.Pair.A, probe);
        var paused = await run.IncrementalAsync("p1", new RecordingProgress(), CancellationToken.None);
        Require(paused.Outcome == RunOutcomes.Paused,
            "a pause during the delete absence check reports Paused");
        Require(fx.WriterB.Deletes == 0 && await fx.B.ObserveAsync("gone.txt", CancellationToken.None) is not null,
            "a paused delete absence check deletes nothing");
        Require(paused.Skipped == 0 && sql.SyncRetryAt("p1", "gone.txt") is null &&
            !sql.OpenNotices("p1").Any(notice => notice.Path == "gone.txt" &&
                notice.ReasonKey is DeleteSourceGuard.ReasonKey or SqliteStateStore.BlockedDeleteSummary),
            "a paused delete absence check commits no Skipped delete and no blocked-delete notice");
        probe.PauseAbsence = false;
        var resumed = await run.IncrementalAsync("p1", new RecordingProgress(), CancellationToken.None);
        Require(resumed.Outcome == RunOutcomes.Complete && resumed.Deleted == 1 && fx.WriterB.Deletes == 1 &&
            await fx.B.ObserveAsync("gone.txt", CancellationToken.None) is null,
            "resume replans and completes the paused delete");
        Console.WriteLine("PASS DeleteSourceAbsencePause");
    }

    // A pause raised by a Dropbox source read during a copy effect must surface as Paused, never as a Failed or
    // Uncertain result or a false failure notice. The intent is already durable, so the next run reconciles it and
    // replans the copy instead of leaving the item stuck.
    private static async Task SourceReadPauseAsync(string root)
    {
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: sql, accountId: PauseAccount);
        fx.A.Seed("anchor.txt", "anchor");
        var run = fx.Run();
        Require((await FirstSyncAsync(run, fx)).Outcome == RunOutcomes.Complete,
            "source-read pause fixture completes its first sync");
        fx.B.Seed("remote.txt", "payload");
        // The copy targets side A, so side B is the source: its OpenReadAsync models the Dropbox read that pauses.
        var probe = new SignalingWriter(fx.WriterB) { PauseRead = true };
        fx.Provider.AddWriter(fx.Pair.B, probe);
        var paused = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(paused.Outcome == RunOutcomes.Paused,
            "a pause raised by a Dropbox source read reports Paused, not Failed");
        Require(paused.FailureCount == 0 && paused.Skipped == 0,
            "a paused source read commits no Failed, Uncertain or Skipped result");
        Require(!sql.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "remote.txt"),
            "a paused source read raises no failure notice for the item");
        Require(await fx.A.ObserveAsync("remote.txt", CancellationToken.None) is null,
            "a paused source read leaves the target unchanged");
        probe.PauseRead = false;
        var resumed = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var copied = (await MapAsync(fx.A)).TryGetValue("remote.txt", out var hash) && hash == Hash("payload");
        Require(resumed.Outcome == RunOutcomes.Complete && fx.WriterA.Puts == 1 && copied,
            "the next run reconciles the paused intent and completes the copy");
        Console.WriteLine("PASS SourceReadPause");
    }

    // A pause raised while a rule-4 move-completion delete admits its paired Move must surface as Paused, never as a
    // committed Skipped delete ("Retry replans the move"). The pause is raised by the source observation the paired
    // Move admission performs, before the delete records any intent or result.
    private static async Task PairedDeleteAdmissionPauseAsync(string root)
    {
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: sql, accountId: PauseAccount);
        var incoming = fx.A.Seed("new", "incoming");
        var occupant = fx.B.Seed("new", "loser");
        var old = fx.B.Seed("old", "incoming");
        // A Done effect needs its object key, exactly as the planner supplies it for the Move and its vacating delete.
        var delete = new SyncAction("paired-delete", 4, ActionKind.Delete, Side.B, "new", null, incoming, occupant,
            true, "k-new");
        var move = new SyncAction("paired-move", 4, ActionKind.Move, Side.B, "new", "old", incoming, old, false,
            "k-new");
        var plan = new Plan(fx.Pair.PairId, 1, [delete, move], Array.Empty<Notice>());
        // The vacating delete admits the paired Move by observing the move source (side A) and old path (side B).
        var probe = new SignalingWriter(fx.WriterA) { PauseMetadata = true };
        fx.Provider.AddWriter(fx.Pair.A, probe);
        var run = fx.Run();
        var paused = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
        Require(paused.Outcome == RunOutcomes.Paused,
            "a pause in the paired-move admission reports Paused, not a skipped move-completion delete");
        Require(paused.Skipped == 0 && paused.FailureCount == 0 && fx.WriterB.Deletes == 0 &&
            fx.WriterB.Preserves == 0 && await fx.B.ObserveAsync("new", CancellationToken.None) is not null,
            "a paused paired-move admission commits no Skipped delete and removes nothing");
        Require(!sql.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "new" &&
                notice.Detail.Contains("Retry replans the move", StringComparison.Ordinal)),
            "a paused paired-move admission records no replan-the-move deletion result");
        probe.PauseMetadata = false;
        var resumed = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
        var moved = (await MapAsync(fx.B)).TryGetValue("new", out var hash) && hash == Hash("incoming");
        Require(resumed.Outcome == RunOutcomes.Complete && fx.WriterB.Deletes == 1 && fx.WriterB.Preserves == 1 &&
            moved, "resume admits the paired move, preserves the occupant and completes the move " +
            $"(outcome={resumed.Outcome}, done={resumed.Done}, failed={resumed.Failed}, uncertain={resumed.Uncertain}, " +
            $"skipped={resumed.Skipped}, deletes={fx.WriterB.Deletes}, preserves={fx.WriterB.Preserves}, " +
            $"moves={fx.WriterB.Moves}, moved={moved})");
        Console.WriteLine("PASS PairedDeleteAdmissionPause");
    }

    internal static async Task PauseChecksAsync(string root)
    {
        using var sql = SqliteStateStore.Open(root, StorageChecks.Fast, TimeProvider.System);
        var fx = Build(suppliedStore: sql, accountId: PauseAccount);
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
        await ScopePauseAsync(Path.Combine(root, "scope"), "active-run");
        await GlobalPauseBetweenPairsAsync(Path.Combine(root, "global"));
        await AccountPauseBetweenPairsAsync(Path.Combine(root, "account"));
        await ScopePauseMidPairAsync(Path.Combine(root, "account-mid-pair"), PauseAccount);
        await ScopePauseMidPairAsync(Path.Combine(root, "other-account-mid-pair"), OtherPauseAccount);
        await ScopePauseMidPairAsync(Path.Combine(root, "global-mid-pair"), null);
        await ScopePauseMidPairAsync(Path.Combine(root, "pause-invalid-data"), PauseAccount,
            new InvalidDataException("Invalid saved pause data."));
        await ScopePauseMidPairAsync(Path.Combine(root, "pause-io-error"), PauseAccount,
            new IOException("Preferences temporarily unreadable during save."));
        await ScopePauseMidPairAsync(Path.Combine(root, "pause-state-damaged"), PauseAccount,
            new StateDamagedException("Pause state is damaged."));
        await DeferredEvidencePauseAsync(Path.Combine(root, "evidence"));
        await DeleteSourceAbsencePauseAsync(Path.Combine(root, "absence"));
        await SourceReadPauseAsync(Path.Combine(root, "source-read"));
        await PairedDeleteAdmissionPauseAsync(Path.Combine(root, "paired-admission"));
        await HashPauseAsync(Path.Combine(root, "hash-pause"));
        Console.WriteLine("pause: apply safe-point drain, local hash checkpoint, admission, resume, AutoSync and " +
            "batch scope-pause checks passed");
    }
}
