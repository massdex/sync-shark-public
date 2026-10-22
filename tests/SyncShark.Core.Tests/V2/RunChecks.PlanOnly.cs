using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task PlanOnlyAsync()
    {
        PlanOnlyPathChecks();
        await PlanOnlyRecoveryAsync();
        await PlanOnlyOutageAsync();
        await PlanOnlyRestoreRetryAsync();
        await IncrementalRestoreOutageAsync();
        await PlanOnlyUnapprovedCountAsync();
        var preview = Build();
        var ordinary = Build();
        foreach (var fx in new[] { preview, ordinary })
        {
            fx.A.Seed("edit.txt", "old");
            fx.A.Seed("delete.txt", "delete");
            fx.A.Seed("held.txt", "hold");
            fx.A.Seed("missing-id.txt", "identity");
            await FirstSyncAsync(fx.Run(), fx);
            foreach (var path in new[] { "delete.txt", "held.txt", "missing-id.txt" })
            {
                var deletedVictim = (await fx.B.ObserveAsync(path, default))!;
                await fx.B.DeleteAsync(new SyncAction("seed-delete", 6, ActionKind.Delete, Side.B,
                    path, null, null, deletedVictim, false, null), default);
            }
            var baseline = fx.Store.Baselines(fx.Pair.PairId).Single(row => row.A.Path == "missing-id.txt");
            var seed = new SyncAction("clear-identity", 0, ActionKind.Adopt, Side.B, baseline.B.Path,
                null, baseline.A, baseline.B with { ObjectId = "" }, false, baseline.ObjectKey);
            fx.Store.CommitResult(fx.Pair.PairId, baseline.Gen, seed,
                new ActionResult(seed.ActionId, Outcome.Done, baseline.A, baseline.B with { ObjectId = "" }, null, null));
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
        var previewResult = await previewRun.PreviewIncrementalAsync(preview.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var plan = previewResult.Plan;
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
        Require(plan.Actions.Any(a => a.Path == "missing-id.txt" && a.Kind == ActionKind.Hold),
            "missing historical identity becomes Hold before initial Apply");
        Require(previewResult.Unapproved.SequenceEqual(["held.txt"]) &&
            ordinary.Store.SyncRetryAt(ordinary.Pair.PairId, "held.txt") is { Held: not null },
            "export-only authority hold corresponds to normal pending retry");
        PlanOnlyCsvChecks(preview, previewResult);
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

    private static async Task PlanOnlyOutageAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        foreach (var full in new[] { false, true })
        {
            var fx = Build();
            fx.A.Seed("retained.txt", "same");
            await FirstSyncAsync(fx.Run(), fx);
            var root = side == Side.A ? fx.Pair.A : fx.Pair.B;
            fx.Provider.AddInventory(root, new ScriptedInventory(side == Side.A ? fx.A : fx.B)
            {
                ChangesError = new HttpRequestException("network unavailable"),
                FullError = new ProviderTransportTimeoutException("response body stalled")
            });
            if (full) await fx.Run().RequestRescanAsync([root.RootId], default);
            var directory = LocalChecks.FreshRoot("plan-outage-" + Guid.NewGuid().ToString("N"));
            var export = Path.Combine(directory, "plan.csv");
            var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), directory);
            var writes = fx.WriterA.Calls + fx.WriterB.Calls;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var preview = await fx.Run().PreviewIncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
                Require(preview.Plan.RunUnavailable is not null, "root outage makes plan-only unavailable on either side");
                try
                {
                    runtime.WritePlanCsv(export, fx.Pair, preview.Plan, [], default);
                    throw new Exception("Incomplete preview was exported");
                }
                catch (SyncRunPendingVerificationException) { }
                Require(!File.Exists(export) && !Directory.EnumerateFiles(directory, "*.partial").Any(),
                    "unavailable preview never creates even a partial export");
            }
            Require(fx.Store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == "sync-retry") == 1 &&
                fx.Store.SyncRetryAt(fx.Pair.PairId, "") is { Kind: "scan" }, "preview outage keeps one deduped read retry");
            Require(fx.WriterA.Calls + fx.WriterB.Calls == writes, "outage preview has no effects");
        }
    }

    private static async Task PlanOnlyRestoreRetryAsync()
    {
        var fx = Build();
        fx.A.DropboxHashes = fx.B.DropboxHashes = true;
        await FirstSyncAsync(fx.Run(), fx);
        var bytes = System.Text.Encoding.UTF8.GetBytes("retained bytes");
        var source = new ItemState("restore.txt", ItemKind.File, "archive", "revision", DropboxContentHash.Compute(bytes),
            bytes.Length, DateTimeOffset.UnixEpoch, ItemFlags.None);
        var record = RetainRecoveryFixture(fx, source, "preview-archive");
        var action = new SyncAction("rrs-preview", 0, ActionKind.CopyNew, Side.A, source.Path,
            null, source, null, false, null) { SuppressReconciledBaseline = true };
        var id = ShortId.Make("rrs", fx.Pair.PairId, record.RecoveryId);
        fx.Store.AddNotices([new Notice(fx.Pair.PairId, 0, NoticeKind.UserDecision, source.Path, "pending",
            SyncRun.RecoveryRestorePending, id,
            ConflictOperation: new ConflictNoticeOperation(record.RecoveryId, source.Path, [action], []))]);
        var archive = new RecoveryFixtureWriter(fx.WriterA, source, bytes);
        fx.Provider.AddWriter(fx.Pair.A, archive);
        var writes = fx.WriterA.Calls + fx.WriterB.Calls;
        var preview = await fx.Run().PreviewIncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(preview.Plan.RunUnavailable is null && archive.Opens == 0 &&
            fx.WriterA.Calls + fx.WriterB.Calls == writes, "pending restore does not fail preview or retry an effect");
        Require(fx.Store.NoticeById(fx.Pair.PairId, id) is { Closed: false } &&
            fx.Store.RecoveryRestoreProtected(fx.Pair.PairId, source.Path), "preview retains restore notice and protection");
        var directory = LocalChecks.FreshRoot("plan-restore-" + Guid.NewGuid().ToString("N"));
        var export = Path.Combine(directory, "plan.csv");
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), directory);
        runtime.WritePlanCsv(export, fx.Pair, preview.Plan, [], default);
        var csv = File.ReadAllLines(export);
        Require(csv.Count(line => line.Contains("recovery-restore,,pending,RESTORE", StringComparison.Ordinal) &&
            line.Contains("restore.txt", StringComparison.Ordinal) && line.Contains("read-only preview", StringComparison.Ordinal)) == 1,
            "pending restore is exported once with its read-only explanation");
    }

    private static async Task IncrementalRestoreOutageAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        {
            var fx = Build();
            fx.A.Seed("restored.txt", "same");
            await FirstSyncAsync(fx.Run(), fx);
            var source = (await fx.A.ObserveAsync("restored.txt", default))!;
            var action = new SyncAction("rrs-ready", 0, ActionKind.CopyNew, Side.A, source.Path,
                null, source, null, false, null) { SuppressReconciledBaseline = true };
            // A recovery-restore notice's durable id is derived from the pair and the frozen choice, not the
            // value passed to the Notice constructor: AddNotices replaces it with ShortId.Make("rrs", pair, choice).
            var id = ShortId.Make("rrs", fx.Pair.PairId, "archive");
            fx.Store.AddNotices([new Notice(fx.Pair.PairId, 0, NoticeKind.Info, source.Path, "verified",
                SyncRun.RecoveryRestoreReady, id,
                ConflictOperation: new ConflictNoticeOperation("archive", source.Path, [action], []))]);
            var inventory = new ScriptedInventory(side == Side.A ? fx.A : fx.B)
            { ChangesError = new HttpRequestException("network unavailable"), FullError = new HttpRequestException("network unavailable") };
            fx.Provider.AddInventory(side == Side.A ? fx.Pair.A : fx.Pair.B, inventory);
            var writes = fx.WriterA.Calls + fx.WriterB.Calls;
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(fx.Store.NoticeById(fx.Pair.PairId, id) is { Closed: false } &&
                fx.WriterA.Calls + fx.WriterB.Calls == writes,
                "incomplete root cannot verify a ready restore from stale equal rows");
            // The failed scan left durable root retry debt with a backoff. Until it is due the next run
            // still treats the root as incomplete (correctly), so make the retry due to model the
            // later run the assertion is about: a complete inventory with no read debt.
            if (fx.Store.SyncRetryAt(fx.Pair.PairId, string.Empty) is { } scanRetry)
                fx.Store.SaveSyncRetry(fx.Pair.PairId, scanRetry with { RetryAfter = DateTimeOffset.MinValue });
            inventory.ChangesError = inventory.FullError = null;
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(fx.Store.NoticeById(fx.Pair.PairId, id) is { Closed: true },
                "complete inventory allows restore verification on a later run");
        }
    }

    private static async Task PlanOnlyUnapprovedCountAsync()
    {
        var fx = Build();
        fx.A.Seed("held.txt", "local");
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        plan = plan with { Actions = [], HeldDeletions = [new HeldDeletion("held.txt", Side.A, "retained-rule")] };
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default);
        Require(report.DeletionEvidencePending.SequenceEqual(["held.txt"]) && report.UnreviewedPending == 0,
            "normal first-sync authority hold is counted once, not also as unreviewed");
    }

    private static void PlanOnlyCsvChecks(Fixture fx, SyncRun.IncrementalPlanPreview preview)
    {
        var directory = Path.Combine(Path.GetTempPath(), "preview-csv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "plan.csv");
            var unsupported = preview.Plan.Actions.First() with { ActionId = "unsupported-export",
                ProviderEffect = new ProviderEffectCheckpoint("whole-folder-group", "{}", DateTimeOffset.UtcNow) };
            fx.Store.RecordIntent(fx.Pair.PairId, unsupported);
            var plan = preview.Plan with { HeldDeletions = [new HeldDeletion("evidence.txt", Side.A)] };
            var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), directory);
            var rows = runtime.WritePlanCsv(path, fx.Pair, plan, [unsupported], default, preview.Unapproved);
            var csv = File.ReadAllLines(path);
            Require(csv.Count(line => line.Contains("unsupported-export", StringComparison.Ordinal)) == 1 &&
                !csv.Any(line => line.Contains("pending-intent", StringComparison.Ordinal)), "unsupported intent exported once");
            Require(csv.Any(line => line.Contains("held-deletion,,held,HOLD,A,,evidence.txt", StringComparison.Ordinal)) &&
                csv.Any(line => line.Contains("unreviewed-held,HOLD", StringComparison.Ordinal) && line.Contains("held.txt", StringComparison.Ordinal)),
                "CSV contains evidence and export-only authority HOLD rows");
            Require(rows == csv.Length - 1, "CSV row count matches actual writer output");
            var result = new SyncCliPlanExportData(fx.Pair.PairId, fx.Pair.Name, path, plan.Gen,
                plan.Actions.Count, 0, 0, plan.HeldDeletions.Count + preview.Unapproved.Count, 0, 0, 1, rows);
            Require(!result.ExactInitialPlan && !(result with { HeldDeletions = 0, UnsupportedReconciles = 1 }).ExactInitialPlan,
                "evidence holds and unsupported reconciliation explicitly make export non-exact");
        }
        finally { Directory.Delete(directory, true); }
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
        var plan = (await preview.Run().PreviewIncrementalAsync(preview.Pair.PairId, new RecordingProgress(), default)).Plan;
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
        var alias = Path.Combine(root, "alias");
        try
        {
            void Refuses(Action check)
            {
                try { check(); } catch (SyncCliUsageException) { return; }
                throw new Exception("Unsafe preview path accepted");
            }
            Refuses(() => V2CliRuntime.ValidateIsolatedDataRoot(live, live));
            Refuses(() => V2CliRuntime.ValidateIsolatedDataRoot(Folder("live/child"), live));
            Refuses(() => V2CliRuntime.ValidateIsolatedDataRoot(root, live));
            Require(V2CliRuntime.ValidateIsolatedDataRoot(isolated, live) == isolated, "independent folder accepted");
            void Link(string args)
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + args)
                    { UseShellExecute = false, CreateNoWindow = true })!;
                process.WaitForExit();
                Require(process.ExitCode == 0, "create test link");
            }
            Link($"mklink /J \"{alias}\" \"{live}\"");
            Refuses(() => V2CliRuntime.ValidateIsolatedDataRoot(alias, live));
            Refuses(() => V2CliRuntime.ValidateIsolatedDataRoot(Path.Combine(alias, "child"), live));
            Folder("live/v2"); Folder("isolated/v2");
            var database = Path.Combine(live, "v2", "state.db");
            File.WriteAllText(database, "test state");
            Link($"mklink /H \"{Path.Combine(isolated, "v2", "state.db")}\" \"{database}\"");
            Refuses(() => V2CliRuntime.ValidateIsolatedDataRoot(isolated, live));
            var pair = Build().Pair;
            var paired = Folder("other-pair");
            var other = pair with { PairId = "other", A = pair.A with { RootPath = paired } };
            foreach (var forbidden in new[] { live, isolated, paired, Folder("Dropbox") })
                Refuses(() => V2CliRuntime.ValidatePlanExportPath(Path.Combine(forbidden, "plan.csv"), [pair, other], isolated, live));
            var existing = Path.Combine(output, "existing.csv"); File.WriteAllText(existing, "keep");
            Refuses(() => V2CliRuntime.ValidatePlanExportPath(existing, [], isolated, live));
            Require(V2CliRuntime.ValidatePlanExportPath(Path.Combine(output, "new.csv"), [], isolated, live).EndsWith("new.csv"), "safe export accepted");
            foreach (var value in new[] { "=1+1", "+cmd", "-1", "@sum", "\tformula" })
                Require(V2CliRuntime.CsvField(value) == "'" + value, "formula neutralized");
            Require(V2CliRuntime.CsvField("\rformula") == "\"'\rformula\"", "leading CR is neutralized and quoted");
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
        finally
        {
            // Unlink the junction itself before recursively removing this fixture's own tree.
            if (Directory.Exists(alias)) Directory.Delete(alias);
            Directory.Delete(root, true);
        }
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
