namespace Massdex.SyncShark.V2;

/// <summary>Run CLI follow-up checks: per-direction run counts, the store-backed catalogs and first-sync choices.</summary>
internal static partial class RunCliChecks
{
    public static async Task RunAsync()
    {
        await Cli_RunReport_KeptBothIsNotUploadedAsync();
        await Cli_RunReport_DeleteIsOwnFieldAsync();
        await Cli_RunReport_MoveIsOwnFieldAsync();
        await Cli_Retire_DisablesOnlyAsync();
        await Cli_RecoveryList_ReadsStoreRecoveriesAsync();
        await Cli_ConflictsList_ReadsOpenNoticesAsync();
        await Cli_Dispatch_RecoveryAndConflictsAsync();
        await Cli_RetainedNoticeChoicesAsync();
        await Cli_PlanAfterFirstSync_IsNotInV2Async();
        await Cli_CreatePair_RefusesEqualRootAsync();
        await Cli_CreatePair_RefusesSameObservedRootAsync();
        await Cli_EnablePair_RefusesEqualRootAsync();
        await Cli_Rescan_ClearsBothCursorsAsync();
        await Cli_Rescan_SideSelectsOneRootAsync();
        await Cli_Rescan_UnknownPairRefusedAsync();
        await Cli_Rescan_RefusedWhileRunActiveAsync();
        await RunFirstSyncChoiceChecksAsync();
        await RunRebuildableChecksAsync();
        await RunCreateExcludeChecksAsync();
        await RunExclusionUpdateChecksAsync();
        await RunIncludePreviewChecksAsync();
        await IncludePreviewChecks.RunAsync();
        await DropboxChecks.IncludePreviewScopeAsync();
        await RunIgnoreChecksAsync();
        await RunDecisionChecksAsync();
        await RunSqlitePrepareApplyChecksAsync();
        Console.WriteLine("v2 run CLI checks passed.");
    }

    private static V2Settings Settings => V2Settings.Default with { ProgressInterval = TimeSpan.Zero };

    // Both sides edit the same file after the first sync: the real Planner keeps both (rule 27), and each kept-both
    // copy is its own count, so the CLI maps them to conflicts and never to uploaded or downloaded.
    private static async Task Cli_RunReport_KeptBothIsNotUploadedAsync()
    {
        var fx = Build();
        fx.A.Seed("x.txt", "v1");
        fx.B.Seed("x.txt", "v1");
        await FirstSyncAsync(fx);
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        fx.A.Seed("x.txt", "edited on A");
        fx.B.Seed("x.txt", "edited on B");
        var progress = new List<string>();
        var data = await runtime.RunPairAsync(fx.Pair.PairId, progress.Add, CancellationToken.None);
        Require(data.Diagnostics is { AdmissionCount: > 0, PeakAdmittedConcurrency: > 0 },
            "the CLI result carries actual per-run admission measurements");
        Require(progress.Any(line => line.Contains(nameof(RunDiagnostics.PeakAdmittedConcurrency), StringComparison.Ordinal)),
            "CLI progress exposes the throughput snapshot");
        Require(data.Conflicts == 2 && data.Uploaded == 0 && data.Downloaded == 0 && data.Blocked == 0,
            "kept-both copies are reported as conflicts, not as transfers");
        Console.WriteLine("PASS Cli_RunReport_KeptBothIsNotUploaded");
    }

    // A delete is its own field: the run output must never fold it into the uploaded total.
    private static async Task Cli_RunReport_DeleteIsOwnFieldAsync()
    {
        var fx = Build();
        var local = fx.A.Seed("x.txt", "same");
        fx.B.Seed("x.txt", "same");
        await FirstSyncAsync(fx);
        await fx.A.DeleteAsync(new SyncAction("seed-delete", 6, ActionKind.Delete, Side.A, "x.txt", null, null, local,
            false, null), CancellationToken.None);
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        var data = await runtime.RunPairAsync(fx.Pair.PairId, _ => { }, CancellationToken.None);
        Require(data.Deleted == 1 && data.Uploaded == 0 && data.Downloaded == 0,
            "a finished delete is reported as deleted, not as an upload");
        Console.WriteLine("PASS Cli_RunReport_DeleteIsOwnField");
    }

    private static async Task Cli_RunReport_MoveIsOwnFieldAsync()
    {
        var fx = Build();
        fx.A.Seed("x.txt", "same");
        fx.B.Seed("x.txt", "same");
        await FirstSyncAsync(fx);
        await fx.A.MoveAsync(new SyncAction("seed-move", 9, ActionKind.Move, Side.A, "y.txt", "x.txt", null, null,
            false, null), CancellationToken.None);
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        var data = await runtime.RunPairAsync(fx.Pair.PairId, _ => { }, CancellationToken.None);
        Require(data.Moved == 1 && data.Uploaded == 0, "a finished move is reported as moved, not as an upload");
        Console.WriteLine("PASS Cli_RunReport_MoveIsOwnField");
    }
    private static async Task Cli_Retire_DisablesOnlyAsync()
    {
        var fx = Build();
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        var retired = await runtime.RetirePairAsync(fx.Pair.PairId, CancellationToken.None);
        Require(!retired.Enabled && !retired.IsRetired, "retire turns the pair off without claiming it is retired");
        Require(fx.Store.Pair(fx.Pair.PairId) is { Enabled: false }, "the store keeps the retired pair disabled");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "retire makes no file effects");
        Console.WriteLine("PASS Cli_Retire_DisablesOnly");
    }

    private static async Task Cli_RecoveryList_ReadsStoreRecoveriesAsync()
    {
        var fx = Build();
        SeedRecovery(fx);
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        var list = await runtime.LoadRecoveryAsync(fx.Pair.PairId, null, 50, CancellationToken.None);
        Require(list.Items.Count == 1 && list.ItemCount == 1 && list.TotalSize == 12,
            "the recovery list counts the store's recovery row");
        Require(list.Items[0].Source == "DropboxArchive" && list.Items[0].RelativePath == "r.txt" &&
            list.Items[0].Status == "retained" && list.Items[0].ContentHash == "abc",
            "the recovery row maps to the CLI record");
        Require(list.RetentionDays == (int)V2Settings.Default.RecoveryRetention.TotalDays,
            "the recovery retention comes from the run settings");
        Console.WriteLine("PASS Cli_RecoveryList_ReadsStoreRecoveries");
    }

    private static async Task Cli_ConflictsList_ReadsOpenNoticesAsync()
    {
        var fx = Build();
        SeedNotice(fx);
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        var scoped = await runtime.LoadConflictsAsync(fx.Pair.PairId, null, null, 50, 0, CancellationToken.None);
        Require(scoped.Items.Count == 1 && scoped.Items[0].RelativePath == "x.txt" &&
            scoped.Items[0].Group == "KeepBoth" && scoped.Items[0].Status == "open" &&
            scoped.Items[0].Recommendation == "both versions kept",
            "an open notice maps to one open conflict of its kind");
        var all = await runtime.LoadConflictsAsync(null, null, null, 50, 0, CancellationToken.None);
        Require(all.TotalOpen == 1, "the unfiltered list counts every open notice");
        Console.WriteLine("PASS Cli_ConflictsList_ReadsOpenNotices");
    }

    // The registry routes these commands through the shared surface entry point, so the check goes through
    // the same dispatch path the CLI uses, not straight to the typed methods.
    private static async Task Cli_Dispatch_RecoveryAndConflictsAsync()
    {
        var fx = Build();
        SeedRecovery(fx);
        SeedNotice(fx);
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        var recovery = await DispatchAsync(runtime, ["--cli", "recovery", "list", "--pair", fx.Pair.PairId]);
        Require(recovery is SyncCliRecoveryListData { ItemCount: 1, Returned: 1 },
            "recovery list is reachable through the registry dispatch");
        var conflicts = await DispatchAsync(runtime, ["--cli", "conflicts", "list", "--pair", fx.Pair.PairId]);
        Require(conflicts is SyncCliConflictListData { TotalOpen: 1, Returned: 1 },
            "conflicts list is reachable through the registry dispatch");
        Console.WriteLine("PASS Cli_Dispatch_RecoveryAndConflicts");
    }

    private static async Task<object> DispatchAsync(V2CliRuntime runtime, string[] args) =>
        await SyncCliRegistry.DispatchAsync(runtime, SyncCliParser.Parse(args), TextWriter.Null, CancellationToken.None);

    // The incremental plan preview is deferred , so a plan after the first sync is
    // unsupported rather than a stale or refused first-sync plan.
    private static async Task Cli_PlanAfterFirstSync_IsNotInV2Async()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        await FirstSyncAsync(fx);
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        var message = "no refusal";
        try
        {
            await DispatchAsync(runtime, ["--cli", "plan", "--pair", fx.Pair.PairId]);
        }
        catch (Exception error)
        {
            message = error.Message;
        }
        Require(message.Contains("not in core v2 yet", StringComparison.Ordinal),
            "plan after the first sync says it is not in core v2 yet");
        Console.WriteLine("PASS Cli_PlanAfterFirstSync_IsNotInV2");
    }

    // Creating a pair on a folder another pair already uses is refused before any root is observed or saved.
    private static async Task Cli_CreatePair_RefusesEqualRootAsync()
    {
        var fx = Build();
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        var refusal = await RefusalAsync(() => runtime.CreatePairAsync("acc", "/elsewhere", "c:\\A", null, null, true,
            CancellationToken.None));
        Require(refusal is SyncCliUsageException && refusal.Message.Contains("exact folder", StringComparison.Ordinal),
            "creating a pair on another pair's local root is a usage error with the catalog wording");
        refusal = await RefusalAsync(() => runtime.CreatePairAsync("acc", "/", "C:\\a\\b", null, null, true,
            CancellationToken.None));
        Require(refusal is SyncCliUsageException, "creating a pair that nests in opposite directions is refused");
        refusal = await RefusalAsync(() => runtime.CreatePairAsync("acc", "/elsewhere", "C:\\other\\..\\a", null,
            null, true, CancellationToken.None));
        Require(refusal is SyncCliUsageException && refusal.Message == PairAdmissionReasons.SameRoot.Detail,
            "a '..' alias of another pair's local root is refused as the same root");
        refusal = await RefusalAsync(() => runtime.CreatePairAsync("acc", "/x/../b", "C:\\fresh", null, null, true,
            CancellationToken.None));
        Require(refusal is SyncCliUsageException && refusal.Message == PairAdmissionReasons.UnsafePath.Detail,
            "a Dropbox root with a dot segment is refused before anything is compared");
        Require(fx.Store.Pairs().Count == 1 && fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
            "a refused create saves nothing and observes nothing");
        Console.WriteLine("PASS Cli_CreatePair_RefusesEqualRoot");
    }

    // A folder reached by another path (or a shared folder through another account) is the same observed root:
    // the create is refused after observation and nothing is saved.
    private static async Task Cli_CreatePair_RefusesSameObservedRootAsync()
    {
        var fx = Build();
        fx.Store.SavePair(new PairDef("p2", "p2",
            new RootDef("rc", SyncProviders.Local, null, "C:\\alias-target", GatedObserveProvider.RootObjectId),
            new RootDef("rd", SyncProviders.Dropbox, "other-account", "/shared", "id:rd"), [], [], true));
        var provider = new GatedObserveProvider();
        provider.Release();
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(provider), "C:\\root");
        var refusal = await RefusalAsync(() => runtime.CreatePairAsync("acc", "/fresh", "C:\\alias", null, null,
            true, CancellationToken.None));
        Require(refusal is SyncCliUsageException && refusal.Message == PairAdmissionReasons.SameRoot.Detail &&
            fx.Store.Pairs().Count == 2, "a create whose observed root is another pair's root is refused unsaved");
        Console.WriteLine("PASS Cli_CreatePair_RefusesSameObservedRoot");
    }

    // A pair saved off before admission existed cannot be turned on while it shares a root; turning it off is fine.
    private static async Task Cli_EnablePair_RefusesEqualRootAsync()
    {
        var fx = Build();
        var self = await new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root")
            .SetPairEnabledAsync(fx.Pair.PairId, true, CancellationToken.None);
        Require(self.Enabled, "re-enabling an admitted pair is not refused by its own saved record");
        var clash = new PairDef("p2", "p2", new RootDef("rc", SyncProviders.Local, null, "C:\\other", "id:rc"),
            new RootDef("rd", SyncProviders.Dropbox, "acc", "/B", "id:rd"), [], [], false);
        fx.Store.SavePair(clash);
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        var refusal = await RefusalAsync(() => runtime.SetPairEnabledAsync("p2", true, CancellationToken.None));
        Require(refusal is SyncCliUsageException && fx.Store.Pair("p2") is { Enabled: false },
            "enabling a pair that shares a root is refused and leaves it off");
        var disabled = await runtime.SetPairEnabledAsync("p2", false, CancellationToken.None);
        Require(!disabled.Enabled, "disabling a pair that shares a root is allowed");
        Console.WriteLine("PASS Cli_EnablePair_RefusesEqualRoot");
    }

    private static async Task<Exception?> RefusalAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static void SeedRecovery(Fixture fx) => fx.Store.CommitResult(fx.Pair.PairId, 0,
        new SyncAction("rec", 1, ActionKind.Update, Side.B, "r.txt", null, null, null, false, "k"),
        new ActionResult("rec", Outcome.Done, null, null,
            new RecoveryRef(Side.B, "archive/r.txt", "v1", "abc", 12), null));

    private static void SeedNotice(Fixture fx) => fx.Store.AddNotices(
        [new Notice(fx.Pair.PairId, 7, NoticeKind.KeepBoth, "x.txt", "both versions kept", "kept-both")]);

    private sealed class Fixture
    {
        public required IStateStore Store { get; init; }
        public required FakeEndpoint A { get; init; }
        public required FakeEndpoint B { get; init; }
        public required CountingWriter WriterA { get; init; }
        public required CountingWriter WriterB { get; init; }
        public required FakeEndpointProvider Provider { get; init; }
        public required PairDef Pair { get; init; }

        public SyncRun Run() =>
            new(Store, new Executor(Store, Settings, ExecutorChecks.Admission()), Provider, Settings, TimeProvider.System);
    }

    private static Fixture Build(string pairId = "p1", IStateStore? store = null)
    {
        store ??= new InMemoryStateStore();
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        var writerA = new CountingWriter(a);
        var writerB = new CountingWriter(b);
        var pair = new PairDef(pairId, pairId,
            new RootDef("ra", SyncProviders.Local, null, "C:\\a", "id:ra"),
            new RootDef("rb", SyncProviders.Dropbox, "acc", "/b", "id:rb"),
            [], [], true);
        store.SavePair(pair);
        var provider = new FakeEndpointProvider()
            .Add(pair.A, a).Add(pair.B, b)
            .AddWriter(pair.A, writerA).AddWriter(pair.B, writerB);
        return new Fixture
        {
            Store = store,
            A = a,
            B = b,
            WriterA = writerA,
            WriterB = writerB,
            Provider = provider,
            Pair = pair
        };
    }

    private static async Task<RunReport> FirstSyncAsync(Fixture fx)
    {
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        return await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
