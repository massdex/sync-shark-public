using System.Diagnostics;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static class DirectDownloadInterruptionChecks
{
    private static readonly byte[] Old = LocalChecks.Bytes("original download victim");
    private static readonly byte[] New = LocalChecks.Bytes("verified replacement download");

    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Use a fresh crash-test root.");
        Directory.CreateDirectory(root);
        foreach (var phase in new[] { "downloading", "verified", "archive-copy", "preserved", "published", "rollback", "missing-recovery", "moved-root", "absent-recovery", "absent-original", "moved-pair", "changed-stage", "new-downloading", "new-published" })
        {
            var fixture = Path.Combine(root, phase);
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(DirectDownloadInterruptionChecks).Assembly.Location);
            start.ArgumentList.Add("--v2-download-kill-child");
            start.ArgumentList.Add(fixture);
            start.ArgumentList.Add(phase);
            using var child = Process.Start(start) ?? throw new IOException("Crash child did not start.");
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } throw; }
            LocalChecks.Require(child.ExitCode == 73, phase + " reached crash point: " + await stdout + await stderr);
            using var store = SqliteStateStore.Open(Path.Combine(fixture, "state"), StorageChecks.Fast, TimeProvider.System);
            var action = store.UnresolvedIntents(LocalChecks.PairId).Single();
            LocalChecks.Require(action.ProviderEffect?.Kind == SyncDirectDownloadCheckpoint.Kind,
                phase + " production executor persisted checkpoint before crash");
            var stage = JsonSerializer.Deserialize<SyncDownloadStage>(action.ProviderEffect!.Id)!;
            if (phase is "new-published")
                LocalChecks.Require(!stage.Verified, "new-file publication keeps only the first durable checkpoint");
            if (phase == "downloading")
                LocalChecks.Require(!stage.Verified && File.Exists(stage.Path), "partial bytes have durable native ownership before verification");
            if (phase is "missing-recovery" or "absent-recovery")
                File.Move(stage.RecoveryPath, stage.RecoveryPath + ".retained-for-test");
            var absentSettlement = phase is "moved-root" or "absent-recovery" or "absent-original" or "moved-pair" or "changed-stage";
            var localPath = Path.Combine(fixture, "local");
            if (phase == "moved-pair")
            {
                Directory.Move(localPath, Path.Combine(fixture, "relocated-local"));
                localPath = Path.Combine(fixture, "relocated-local");
            }
            if (phase == "changed-stage")
            {
                var (volume, id) = LocalItems.ParseObjectId(stage.ObjectId);
                SyncLocalFileAuthority.RestoreInterruptedWritableObjectAccess(stage.Path,
                    new PilotDirectoryIdentity(volume, id), stage.Path);
                await File.WriteAllBytesAsync(stage.Path, LocalChecks.Bytes("modified partial"));
            }
            if (phase == "absent-original")
                File.Move(Path.Combine(fixture, "local", "file.bin"), Path.Combine(fixture, "original-retained.bin"));
            var writer = await LocalChecks.OpenWriter(localPath,
                Path.Combine(fixture, phase == "moved-root" ? "new-recovery" : "recovery"), CancellationToken.None);
            var executor = new Executor(store, V2Settings.Default, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage);
            var result = (await executor.ReconcileAsync([action], 1, writer, new Source(false), CancellationToken.None)).Single();
            var completes = phase is "preserved" or "published" or "missing-recovery" or "new-published";
            LocalChecks.Require(result.Outcome == (completes ? Outcome.Done : Outcome.Skipped),
                phase + " cold resume: " + result.Outcome + " " + result.Reason);
            LocalChecks.Require(absentSettlement || phase == "new-downloading" ? !File.Exists(Path.Combine(localPath, "file.bin")) :
                File.ReadAllBytes(Path.Combine(fixture, "local", "file.bin")).SequenceEqual(completes ? New : Old),
                phase + " leaves verified new bytes or original bytes");
            LocalChecks.Require(store.UnresolvedIntents(LocalChecks.PairId).Count == 0,
                phase + " SQLite intent settles, so the pair is not blocked");
            if (completes)
            {
                var observed = (await writer.ObserveAsync("file.bin", CancellationToken.None))!;
                var baseline = store.Baselines(LocalChecks.PairId).Single(b => b.A.Path == "file.bin");
                LocalChecks.Require(observed.Mtime == action.Source!.Mtime && baseline.A.Mtime == observed.Mtime &&
                    baseline.A.ObjectId == observed.ObjectId && result.NewA == observed,
                    phase + " cold resume preserves source UTC mtime and persists the observed SQLite baseline");
            }
            LocalChecks.Require(File.Exists(stage.Path) == (phase is "moved-root" or "changed-stage"), phase + " cleans only an owned partial in the configured root");
            if (phase == "new-downloading")
                LocalChecks.Require(result.Reason == EffectResolver.NotApplied && store.OpenNotices(LocalChecks.PairId).Count == 0,
                    "interrupted new download settles silently without a misleading notice");
            if (absentSettlement)
            {
                var baselines = store.Baselines(LocalChecks.PairId);
                LocalChecks.Require(result.ForgetAbsentDownloadBaseline && baselines.All(b => b.ObjectKey != "download-object") &&
                    baselines.Any(b => b.ObjectKey == "unrelated-object"), phase + " forgets only absent target baseline");
                LocalChecks.Require(store.OpenNotices(LocalChecks.PairId).Any(n => n.ReasonKey == "local-download-recovery"),
                    phase + " persists settlement notice");
                var next = Planner.Decide(LocalChecks.PairId, 2,
                    [new PlanRow(baselines.SingleOrDefault(b => b.ObjectKey == "download-object"), null, action.Source,
                        new Evidence(true, false, true, true))],
                    new PlanOptions(TimeSpan.Zero, false, new LabelRule("{name}-{side}{ext}", "yyyyMMdd", TimeSpan.Zero, "local", "cloud"), 10));
                LocalChecks.Require(next.Actions.Any(a => a.Kind == ActionKind.CopyNew && a.Target == Side.A) &&
                    !next.Actions.Any(a => a.Kind == ActionKind.Delete && a.Target == Side.B),
                    phase + " next run downloads, never deletes Dropbox");
            }
            if (phase == "missing-recovery")
                LocalChecks.Require(result.Recovery is null && store.OpenNotices(LocalChecks.PairId).Any(n =>
                    n.ReasonKey == "local-download-recovery"), "missing Recovery settles Done with a durable notice");
            var repeated = await EffectResolver.ResolveAsync(action, writer, writer, CancellationToken.None, isStateDamage: StorageFailureClassifier.IsStateDamage);
            LocalChecks.Require(repeated.Outcome == result.Outcome, phase + " resume is idempotent");
            if (completes && phase is not ("missing-recovery" or "new-published"))
                LocalChecks.Require(result.Recovery is not null && File.ReadAllBytes(result.Recovery.Location).SequenceEqual(Old),
                    phase + " retains the original in Recovery");
            Console.WriteLine("download SQLite executor crash " + phase + " passed");
        }
    }

    internal static async Task CrashAsync(string root, string phase)
    {
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var writer = await LocalChecks.OpenWriter(local, Path.Combine(root, "recovery"), CancellationToken.None);
        if (phase is not ("new-downloading" or "new-published")) await File.WriteAllBytesAsync(Path.Combine(local, "file.bin"), Old);
        var victim = (await writer.ObserveAsync("file.bin", CancellationToken.None))!;
        var source = new ItemState("file.bin", ItemKind.File, "id:dropbox:download", "revision",
            LocalChecks.BlockHash(New), New.Length, DateTimeOffset.UnixEpoch, ItemFlags.None);
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), StorageChecks.Fast, TimeProvider.System);
        var localRoot = LocalChecks.RootOf(local, "root-local");
        store.SavePair(new PairDef(LocalChecks.PairId, "download crash", localRoot,
            localRoot with { RootId = "root-cloud", Provider = SyncProviders.Dropbox }, [], [], true));
        var gen = store.BeginGeneration(localRoot.RootId, "download-crash");
        var action = new SyncAction("crash-download", 1, phase is "new-downloading" or "new-published" ? ActionKind.CopyNew : ActionKind.Update, Side.A, "file.bin", null,
            source, victim, phase is not ("new-downloading" or "new-published"), "download-object");
        if (phase is not ("new-downloading" or "new-published")) SeedBaseline(store, victim, source, "download-object");
        SeedBaseline(store, (victim ?? source) with { Path = "other.bin", ObjectId = "other-local" },
            source with { Path = "other.bin", ObjectId = "other-cloud" }, "unrelated-object");
        if (phase is "verified" or "absent-original") SyncLocalRecovery.BeforeCreateForTest = _ => Environment.Exit(73);
        if (phase == "archive-copy") SyncLocalRecovery.RecoveryCopyIntervalForTest = _ => Environment.Exit(73);
        if (phase is "preserved" or "moved-root" or "absent-recovery" or "moved-pair" or "changed-stage") SyncLocalRecovery.SourceRemovedBeforeRecoveryCloseForTest = _ => Environment.Exit(73);
        if (phase is "published" or "missing-recovery" or "new-published") SyncLocalEndpointStore.PublishedBeforeCloseForTest = () => Environment.Exit(73);
        if (phase == "rollback") SyncLocalRecovery.SourceRemovedBeforeRecoveryCloseForTest = _ => throw new OperationCanceledException();
        var executor = new Executor(store, V2Settings.Default, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage);
        await foreach (var _ in executor.ApplyAsync(new Plan(LocalChecks.PairId, gen, [action], []), writer,
            new Source(phase is "downloading" or "new-downloading"), CancellationToken.None)) { }
        if (phase == "rollback") Environment.Exit(73);
        throw new InvalidOperationException("The requested interruption was not reached.");
    }

    private static void SeedBaseline(IStateStore store, ItemState a, ItemState b, string key)
    {
        b = b with { Hash = a.Hash, Size = a.Size };
        var seed = new SyncAction("seed-" + key, 3, ActionKind.Adopt, Side.B, b.Path, null, a, b, false, key);
        store.RecordIntent(LocalChecks.PairId, seed);
        store.CommitResult(LocalChecks.PairId, 1, seed, new ActionResult(seed.ActionId, Outcome.Done, a, b, null, null));
    }

    private sealed class Source(bool crashDuringWrite) : IEndpointWriter, ISyncDirectDownloadSource
    {
        public async Task<SyncDownloadResult> DownloadToAsync(ItemState source, Stream destination, CancellationToken ct)
        {
            await destination.WriteAsync(New.AsMemory(0, 4), ct);
            await destination.FlushAsync(ct);
            if (crashDuringWrite) Environment.Exit(73);
            await destination.WriteAsync(New.AsMemory(4), ct);
            return new SyncDownloadResult(source.ObjectId, source.VersionId!, source.Size, source.Hash!);
        }
        public Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(SyncAction a, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    }
}
