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

    // A global pause saved between two pairs of one batch stops the batch: pair 2 never starts. The pause and the
    // resume both go through the real CLI operations over the same preferences store the host reads.
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
        var host = new V2SyncHost(sql, run, new ObservedExecutor(fx.Executor), null!, Settings,
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
        var summary = string.Empty;
        var result = await host.RunPairsAsync(
            [PauseView(fx.Pair), PauseView(second.Def)],
            "global-pause-batch", admissionProgress: text =>
            {
                summary = text;
                if (!pausing) return;
                pausing = false;
                // The same operation the app's pause menu and the CLI verb use: save the durable scope.
                control = cli.SetPauseAsync(PauseScopes.Global, null, true, null, true, CancellationToken.None)
                    .GetAwaiter().GetResult();
            });
        var now = TimeProvider.System.GetUtcNow();
        Require(control is { Scope: PauseScopes.Global, Paused: true },
            "the CLI global pause saved the durable scope");
        Require(result.Items.Count == 1 && summary.Contains("(" + RunOutcomes.Complete + ")", StringComparison.Ordinal),
            "the global pause arrives after pair 1 and before pair 2");
        Require(second.Writer.Puts == before && !sql.PairPaused("p1") && !sql.PairPaused("p2"),
            "a global pause between pairs stops the batch before pair 2 runs, without a per-pair pause");
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

    // The account scope alone stops the batch: the global preference is untouched, so this check cannot pass
    // through the global rule that the previous check exercises.
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
        var host = new V2SyncHost(sql, run, new ObservedExecutor(fx.Executor), null!, Settings,
            TimeProvider.System, SqliteStateStore.DatabasePath(root))
        {
            PauseSource = () => saved
        };
        fx.A.Seed("first-run.txt", "one");
        second.A.Seed("second-run.txt", "two");
        var before = second.Writer.Puts;
        var pausing = true;
        var result = await host.RunPairsAsync(
            [PauseView(fx.Pair), PauseView(second.Def)],
            "account-pause-batch", admissionProgress: _ =>
            {
                if (!pausing) return;
                pausing = false;
                saved = saved with
                {
                    AccountPauses = [new SyncAccountPause(PauseAccount, true, null, TimeZoneInfo.Local.Id)]
                };
            });
        var now = TimeProvider.System.GetUtcNow();
        Require(!PauseStateProjection.GlobalPaused(saved, now) &&
            PauseStateProjection.AccountPaused(saved, PauseAccount, now),
            "the account pause leaves the global scope untouched");
        Require(result.Items.Count == 1, "the account pause arrives after pair 1 and before pair 2");
        Require(second.Writer.Puts == before && !sql.PairPaused("p1") && !sql.PairPaused("p2"),
            "an account pause between pairs stops the batch before pair 2 runs, without a per-pair pause");
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
        var host = new V2SyncHost(sql, run, new ObservedExecutor(fx.Executor), null!, Settings,
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
        var summaries = new List<string>();
        var result = await host.RunPairsAsync(pairs, "scope-mid-pair", admissionProgress: summaries.Add);
        Require(saved && (account is null
                ? PauseStateProjection.GlobalPaused(preferences.Load(), clock.GetUtcNow())
                : PauseStateProjection.AccountPaused(preferences.Load(), account, clock.GetUtcNow()) &&
                    !PauseStateProjection.GlobalPaused(preferences.Load(), clock.GetUtcNow())),
            "mid-pair pause is durable in the requested scope");
        if (account is null or PauseAccount)
        {
            Require(result.Items.Count == 1 && summaries.Single().Contains("(" + RunOutcomes.Paused + ")",
                StringComparison.Ordinal) && fx.WriterB.Puts > 0 && fx.WriterB.Puts < 12,
                "durable scope pause stops pair 1 at a safe point after the two-second refresh");
            Require(second.Writer.Puts == 0, "pair 2 on the paused account never runs");
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
                var held = await host.RunPairsAsync([pairs[0]], "partial-scope-resume", admissionProgress: summaries.Add);
                Require(held.Items.Count == 1 && summaries.Single().Contains("(" + RunOutcomes.Paused + ")",
                    StringComparison.Ordinal) && fx.WriterB.Puts == putsBefore && !sql.PairPaused("p1"),
                    "resuming one scope leaves a single-pair run paused at its first safe point by the other");
            }
        }
        await preferences.SaveAsync(preferences.Load() with { PauseUntilResumed = false, AccountPauses = [] });
        second.A.Seed("after-resume.txt", "three");
        summaries.Clear();
        var resumed = await host.RunPairsAsync(pairs, "account-mid-pair-resume", admissionProgress: summaries.Add);
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
        await HashPauseAsync(Path.Combine(root, "hash-pause"));
        Console.WriteLine("pause: apply safe-point drain, local hash checkpoint, admission, resume, AutoSync and " +
            "batch scope-pause checks passed");
    }
}
