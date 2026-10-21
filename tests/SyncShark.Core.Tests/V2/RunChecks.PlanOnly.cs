using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task PlanOnlyAsync()
    {
        PlanOnlyPathChecks();
        await PlanOnlyRecoveryAsync();
        var preview = Build();
        var ordinary = Build();
        foreach (var fx in new[] { preview, ordinary })
        {
            fx.A.Seed("edit.txt", "old");
            fx.A.Seed("delete.txt", "delete");
            fx.A.Seed("held.txt", "hold");
            await FirstSyncAsync(fx.Run(), fx);
            foreach (var path in new[] { "delete.txt", "held.txt" })
            {
                var deletedVictim = (await fx.B.ObserveAsync(path, default))!;
                await fx.B.DeleteAsync(new SyncAction("seed-delete", 6, ActionKind.Delete, Side.B,
                    path, null, null, deletedVictim, false, null), default);
            }
            fx.Store.SaveSyncRetry(fx.Pair.PairId, new SyncRetry("held.txt", "deletion-evidence", 1,
                "history pending", DateTimeOffset.MinValue, Held: new HeldDeletion("held.txt", Side.A, "retained-rule")));
            fx.Store.SaveSyncRetry(fx.Pair.PairId, new SyncRetry("upload.txt", "copy", 1,
                "retry", DateTimeOffset.MinValue));
            fx.A.Seed("edit.txt", "new");
            fx.A.Seed("upload.txt", "upload");
            fx.B.Seed("download.txt", "download");
        }
        var before = preview.WriterA.Calls + preview.WriterB.Calls;
        var capture = new PlanCaptureExecutor(ordinary.Executor);
        var run = new SyncRun(ordinary.Store, capture, ordinary.Provider, Settings, TimeProvider.System);
        var previewCapture = new PlanCaptureExecutor(preview.Executor);
        var previewRun = new SyncRun(preview.Store, previewCapture, preview.Provider, Settings, TimeProvider.System);
        var plan = await previewRun.PreviewIncrementalAsync(preview.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(previewCapture.First is null, "plan-only never enters executor Apply, including empty and metadata-only plans");
        await run.IncrementalAsync(ordinary.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(plan.RunUnavailable is null && plan.Actions.Count >= 3, "ordinary plan-only includes edits and both copy directions");
        Require(preview.WriterA.Calls + preview.WriterB.Calls == before, "plan-only makes zero endpoint mutation calls");
        static string Key(SyncAction a) => $"{a.Kind}|{a.Target}|{a.Path}|{a.Rule}|{a.RecoveryFirst}|{a.Source?.Hash}|{a.ExpectedTarget?.Hash}";
        Require(plan.Actions.Select(Key).Order().SequenceEqual(capture.Actions.Select(Key).Order()),
            "plan-only exports exactly the ordinary run's initial execution plan");
        Require(capture.First is not null &&
            plan.DeferredPlanningActions.Select(Key).SequenceEqual(capture.First.DeferredPlanningActions.Select(Key)) &&
            plan.SkippedActions.Select(Key).SequenceEqual(capture.First.SkippedActions.Select(Key)) &&
            plan.HeldDeletions.SequenceEqual(capture.First.HeldDeletions) &&
            plan.UnreviewedHeldDeletions.SequenceEqual(capture.First.UnreviewedHeldDeletions) &&
            plan.RecoveryPending.SequenceEqual(capture.First.RecoveryPending) &&
            plan.Notices.Select(n => (n.Path, n.Rule, n.ReasonKey)).SequenceEqual(
                capture.First.Notices.Select(n => (n.Path, n.Rule, n.ReasonKey))),
            "initial plan parity includes deferred, skipped, held, recovery and notices");
        Require(plan.Actions.Any(a => a.Kind == ActionKind.Delete && a.Path == "delete.txt") &&
            !plan.Actions.Any(a => a.Path == "held.txt" && a.Kind == ActionKind.Delete),
            "deletes propagate while retained held deletions stay protected");
        var denied = new PlanOnlyEndpoints(preview.Provider).Writer(preview.Pair.B);
        try { await denied.MkDirAsync("must-not-exist", CancellationToken.None); throw new Exception("write allowed"); }
        catch (InvalidOperationException) { }
        Require(preview.WriterA.Calls + preview.WriterB.Calls == before, "write-denying facade never delegates an effect");
        // A null route client proves this reconciliation-only reference needs no provider call.
        var cloud = new DropboxWriter(preview.Pair.B, Side.B, null!, string.Empty, Settings);
        var readCloud = new PlanOnlyEndpoints(new FakeEndpointProvider().AddWriter(preview.Pair.B, cloud));
        var victim = (await preview.B.ObserveAsync("download.txt", CancellationToken.None))!;
        var recovery = await readCloud.Writer(preview.Pair.B).PreserveAsync(victim, "pending", CancellationToken.None);
        Require(recovery.VersionId == victim.VersionId && recovery.Hash == victim.Hash && !readCloud.MutationRequested,
            "cloud reconciliation constructs revision recovery without a provider call");
        var pending = plan.Actions.First();
        foreach (var kind in new[] { "whole-folder-group", "whole-folder-member", SyncDirectDownloadCheckpoint.Kind })
            Require(PlanOnlyEndpoints.UnsupportedReconcile(pending with
                { ProviderEffect = new ProviderEffectCheckpoint(kind, "{}", DateTimeOffset.UtcNow) }),
                "effectful reconciliation is explicitly marked unsupported: " + kind);
        Require(!PlanOnlyEndpoints.UnsupportedReconcile(pending with
            { ProviderEffect = new ProviderEffectCheckpoint("delete-job", "{}", DateTimeOffset.UtcNow) }),
            "read-only delete-job verification remains supported");
        Console.WriteLine("PASS ordinary plan-only parity and zero endpoint writes");
    }

    private static async Task PlanOnlyRecoveryAsync()
    {
        var preview = Build(); var ordinary = Build();
        foreach (var fx in new[] { preview, ordinary })
        {
            fx.A.Seed("replace.txt", "old");
            await FirstSyncAsync(fx.Run(), fx);
            fx.B.Seed("replace.txt", "new");
            fx.B.Seed("safe-new.txt", "safe");
            fx.WriterA.RecoveryUnavailable = "Recovery volume unavailable";
        }
        var capture = new PlanCaptureExecutor(ordinary.Executor);
        var plan = await preview.Run().PreviewIncrementalAsync(preview.Pair.PairId, new RecordingProgress(), default);
        await new SyncRun(ordinary.Store, capture, ordinary.Provider, Settings, TimeProvider.System)
            .IncrementalAsync(ordinary.Pair.PairId, new RecordingProgress(), default);
        Require(plan.RecoveryPending.Contains("replace.txt") &&
            plan.Actions.All(a => a.Path != "replace.txt") &&
            plan.Actions.Select(a => (a.Kind, a.Path)).SequenceEqual(capture.Actions.Select(a => (a.Kind, a.Path))) &&
            plan.RecoveryPending.SequenceEqual(capture.First!.RecoveryPending), "unavailable Recovery initial plan parity");
        if (!OperatingSystem.IsWindows()) return;
        var root = LocalChecks.FreshRoot("plan-read-only-open");
        var recovery = Path.Combine(Path.GetDirectoryName(root)!, "preview-recovery");
        var definition = LocalChecks.RootOf(root, "preview-root");
        try
        {
            await LocalWriter.OpenAsync(definition, Side.A, LocalChecks.PairId, LocalChecks.InstallationId,
                recovery, default, readOnlyOpen: true);
            throw new Exception("Missing namespace accepted");
        }
        catch (SyncRunRefusedException) { }
        Require(!Directory.Exists(recovery), "read-only open never creates Recovery");
        await LocalWriter.OpenAsync(definition, Side.A, LocalChecks.PairId, LocalChecks.InstallationId,
            recovery, default, recoveryUnavailable: "fixture skips recovery allocation");
        await LocalWriter.OpenAsync(definition, Side.A, LocalChecks.PairId, LocalChecks.InstallationId,
            recovery, default, readOnlyOpen: true);
        Require(!Directory.Exists(recovery), "existing namespace read-only open does not allocate Recovery");
    }

    private static void PlanOnlyPathChecks()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "plan-safety-" + Guid.NewGuid().ToString("N"));
        string Folder(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        var live = Folder("live");
        var isolated = Folder("isolated");
        var output = Folder("output");
        using var paths = SyncSharkRuntimePaths.OverrideProductRootForTest(live);
        void Refuses(Action check)
        {
            try { check(); } catch (SyncCliUsageException) { return; }
            throw new Exception("Unsafe preview path accepted");
        }
        Refuses(() => V2CliRuntime.ValidateIsolatedDataRoot(live));
        Refuses(() => V2CliRuntime.ValidateIsolatedDataRoot(Folder("live/child")));
        Refuses(() => V2CliRuntime.ValidateIsolatedDataRoot(root));
        Require(V2CliRuntime.ValidateIsolatedDataRoot(isolated) == isolated, "independent folder accepted");
        void Link(string args)
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + args)
                { UseShellExecute = false, CreateNoWindow = true })!;
            process.WaitForExit();
            Require(process.ExitCode == 0, "create test link");
        }
        var alias = Path.Combine(root, "alias");
        Link($"mklink /J \"{alias}\" \"{live}\"");
        Refuses(() => V2CliRuntime.ValidateIsolatedDataRoot(alias));
        Refuses(() => V2CliRuntime.ValidateIsolatedDataRoot(Path.Combine(alias, "child")));
        Folder("live/v2"); Folder("isolated/v2");
        var database = Path.Combine(live, "v2", "state.db");
        File.WriteAllText(database, "test state");
        Link($"mklink /H \"{Path.Combine(isolated, "v2", "state.db")}\" \"{database}\"");
        Refuses(() => V2CliRuntime.ValidateIsolatedDataRoot(isolated));
        var pair = Build().Pair;
        var paired = Folder("other-pair");
        var other = pair with { PairId = "other", A = pair.A with { RootPath = paired } };
        foreach (var forbidden in new[] { live, isolated, paired, Folder("Dropbox") })
            Refuses(() => V2CliRuntime.ValidatePlanExportPath(Path.Combine(forbidden, "plan.csv"), [pair, other], isolated));
        var existing = Path.Combine(output, "existing.csv"); File.WriteAllText(existing, "keep");
        Refuses(() => V2CliRuntime.ValidatePlanExportPath(existing, [], isolated));
        Require(V2CliRuntime.ValidatePlanExportPath(Path.Combine(output, "new.csv"), [], isolated).EndsWith("new.csv"), "safe export accepted");
        foreach (var value in new[] { "=1+1", "+cmd", "-1", "@sum" })
            Require(V2CliRuntime.CsvField(value) == "'" + value, "formula neutralized");
        var recovery = Path.Combine(output, "missing-recovery");
        Require(LocalWriter.ProbeRecoveryReadOnly(recovery) is null && !Directory.Exists(recovery), "recovery probe creates nothing");
        Require(LocalWriter.ProbeRecoveryReadOnly(existing) is not null, "occupied Recovery unavailable");
        var invocation = SyncCliRegistry.Parse(["--cli", "run", "--pair", "p1", "--plan-only", "--data-root", isolated,
            "--export", Path.Combine(output, "new.csv")]);
        Require(!SyncCliRegistry.MutatesProductState(invocation), "preview is non-mutating without yes");
        Refuses(() => SyncCliRegistry.Parse(["--cli", "run", "--pair", "p1"]));
        Require(SyncCliRegistry.MutatesProductState(SyncCliRegistry.Parse(["--cli", "run", "--pair", "p1", "--yes"])),
            "normal run still requires consent and is mutating");
    }

    private sealed class PlanCaptureExecutor(IExecutor inner) : IExecutor
    {
        private Plan? _first;
        internal Plan? First => Volatile.Read(ref _first);
        internal IReadOnlyList<SyncAction> Actions => First?.Actions ?? [];
        public async IAsyncEnumerable<ActionResult> ApplyAsync(Plan plan, IEndpointWriter a, IEndpointWriter b,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.CompareExchange(ref _first, plan, null);
            await foreach (var result in inner.ApplyAsync(plan, a, b, ct)) yield return result;
        }
        public Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> actions, long gen,
            IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null) =>
            inner.ReconcileAsync(actions, gen, a, b, ct, progress);
    }
}
