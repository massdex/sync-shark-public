using System.Collections.Concurrent;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Checks the direct Dropbox-to-local download: one target-volume copy, verify-before-archive, conditional
/// publish, Recovery first, and the narrow crash checkpoint that finishes or rolls back an interrupted swap.
/// </summary>
internal static class DirectDownloadChecks
{
    private const string PairId = LocalChecks.PairId;

    public static async Task RunAsync()
    {
        await DirectDownloadPublishesFromTheTargetDirectoryAsync().ConfigureAwait(false);
        await BlockedPairDownloadsOrdinaryBytesAsync().ConfigureAwait(false);
        await DownloadMtimeAndBaselineAsync().ConfigureAwait(false);
        await FailedVerifyLeavesTheOriginalAsync().ConfigureAwait(false);
        await CancellationLeavesTheOriginalAsync().ConfigureAwait(false);
        await ConcurrentReplacementIsNotOverwrittenAsync().ConfigureAwait(false);
        await CheckpointIsDurableBeforePreserveAsync().ConfigureAwait(false);
        await CrashAfterPreservePublishesTheVerifiedStageAsync().ConfigureAwait(false);
        await AlreadyPublishedDownloadRepairsDateAsync().ConfigureAwait(false);
        await CrashAfterPreserveRestoresTheOriginalAsync().ConfigureAwait(false);
        await ExcludedCheckpointSettlesAsync().ConfigureAwait(false);
        await MovedRecoveryRootSettlesAsync().ConfigureAwait(false);
        await UnownedPartialIsListedAsync().ConfigureAwait(false);
        await CheckpointWithoutResumeStaysUncertainAsync().ConfigureAwait(false);
        Console.WriteLine("v2 direct download checks passed.");
    }

    // The production ActionRunner must use the direct path: one artifact in the destination directory while the
    // bytes arrive, no spool read at all, and the verified stage published create-only.
    private static async Task DirectDownloadPublishesFromTheTargetDirectoryAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("direct-publish").ConfigureAwait(false);
        var content = LocalChecks.Bytes("direct-download-bytes");
        var source = new Source(content);
        source.DuringWrite = _ =>
        {
            var directory = Path.Combine(setup.Root, "d");
            var staged = Directory.GetFiles(directory, ".syncshark-transfer-*.partial");
            LocalChecks.Require(staged.Length == 1,
                "the download writes exactly one temporary file in the destination directory");
            LocalChecks.Require(string.Equals(Path.GetDirectoryName(staged[0]), directory,
                StringComparison.OrdinalIgnoreCase),
                "the download temporary file is in the target directory, not the product spool");
        };
        using var store = new InMemoryStateStore();
        RegisterPair(store, setup.Root);
        var action = new SyncAction("act-direct", 1, ActionKind.CopyNew, Side.A, "d/file.bin", null,
            Planned(content), null, false, null);
        var result = await RunAsync(setup.Writer, source, action, store).ConfigureAwait(false);
        LocalChecks.Require(result is { Outcome: Outcome.Done },
            "the direct download completes: " + result?.Reason);
        LocalChecks.Require(source.OpenReadCalls == 0, "the direct path never materialises a product spool");
        LocalChecks.Require(File.ReadAllBytes(Path.Combine(setup.Root, "d", "file.bin")).SequenceEqual(content),
            "the verified target holds the source bytes");
        LocalChecks.Require(Directory.GetFiles(setup.Root, ".syncshark-transfer-*", SearchOption.AllDirectories)
            .Length == 0, "the published download leaves no partial behind");
        _ = store;
    }

    // N1: when the pair cannot create pointers (coexistence, volume or missing host), a CopyNew under an
    // online-only policy is still an ordinary download. The cause is one pair notice, never a per-item failure.
    private static async Task BlockedPairDownloadsOrdinaryBytesAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("direct-blocked-pair").ConfigureAwait(false);
        var content = LocalChecks.Bytes("blocked-pair-ordinary-bytes");
        using var store = new InMemoryStateStore();
        RegisterPair(store, setup.Root);
        var notices = 0;
        setup.Writer.NewPointers = new NewPointerDownload(_ => true,
            new StoragePairException("binding", "a descendant folder is already a registered sync root"),
            _ => throw new InvalidOperationException("a blocked pair must not acquire a provider"),
            (_, _, _, _) => throw new InvalidOperationException("a blocked pair must not create a pointer"),
            _ => notices++);
        var action = new SyncAction("act-blocked-pair", 1, ActionKind.CopyNew, Side.A, "d/file.bin", null,
            Planned(content), null, false, null);
        var result = await RunAsync(setup.Writer, new Source(content), action, store).ConfigureAwait(false);
        LocalChecks.Require(result is { Outcome: Outcome.Done },
            "a blocked pair downloads ordinary bytes: " + result?.Reason);
        LocalChecks.Require(File.ReadAllBytes(Path.Combine(setup.Root, "d", "file.bin")).SequenceEqual(content),
            "the ordinary fallback publishes the full remote bytes");
        LocalChecks.Require(notices == 1, "the pair-level cause is written once, not once per file");
    }

    // RQ-036/038/140: client_modified, not download time or server_modified, becomes the local
    // time. The committed baseline must describe the actual disk state, so the next run does no upload.
    private static async Task DownloadMtimeAndBaselineAsync()
    {
        foreach (var mode in new[] { "create", "update", "recovery" })
        {
            var setup = await LocalWriterChecks.SetupAsync("download-mtime-" + mode);
            var target = Path.Combine(setup.Root, "file.txt");
            if (mode != "create") LocalChecks.Seed(setup.Root, "file.txt", "old-bytes");
            var victim = await setup.Writer.ObserveAsync("file.txt", CancellationToken.None);
            var content = LocalChecks.Bytes("timestamp-preserving-download");
            var clientModified = new DateTimeOffset(2021, 4, 3, 12, 34, 56, TimeSpan.FromHours(3));
            var planned = DropboxItems.ToItem(new PilotRemoteEntry("file.txt", PilotEntryKind.File,
                "id:dropbox:source", "rev-direct", content.Length, LocalChecks.BlockHash(content),
                DateTimeOffset.UtcNow) { ClientModifiedUtc = clientModified });
            using var store = new InMemoryStateStore();
            RegisterPair(store, setup.Root);
            var action = new SyncAction("act-mtime-" + mode, victim is null ? 1 : 2,
                victim is null ? ActionKind.CopyNew : ActionKind.Update, Side.A, "file.txt", null,
                planned, victim, mode == "recovery", "mtime-object");
            var result = await RunAsync(setup.Writer, new Source(content), action, store);
            LocalChecks.Require(result is { Outcome: Outcome.Done }, "mtime " + mode + " completes: " + result?.Reason);
            var observed = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
            var baseline = store.Baselines(PairId).Single();
            LocalChecks.Require(File.GetLastWriteTimeUtc(target) == clientModified.UtcDateTime &&
                observed.Mtime == clientModified && observed.Mtime.Offset == TimeSpan.Zero,
                mode + " preserves Dropbox client_modified as UTC after closing the published handle");
            LocalChecks.Require(result!.NewA == observed && baseline.A == observed && baseline.B == planned,
                mode + " result and committed baseline match the observed published timestamp and identity");
            var next = Planner.Decide(PairId, 2,
                [new PlanRow(baseline, observed, planned, new Evidence(false, false, true, true))],
                new PlanOptions(TimeSpan.FromSeconds(2), false,
                    new LabelRule("{name}-{side}{ext}", "yyyyMMdd", TimeSpan.Zero, "local", "cloud"), 10));
            LocalChecks.Require(!next.Actions.Any(a => a.Kind is not (ActionKind.Adopt or ActionKind.Forget)),
                mode + " next plan has no reupload, download or conflict loop");
            if (mode == "recovery")
                LocalChecks.Require(result.Recovery is not null && File.ReadAllText(result.Recovery.Location) == "old-bytes",
                    "mtime preservation retains the displaced bytes in Recovery");
        }
    }

    // A bad verification must leave the planned original in place. Baseline archived it before downloading, so
    // the target vanished even when the download failed; the deferred order keeps it.
    private static async Task FailedVerifyLeavesTheOriginalAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("direct-bad-verify").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "keep.txt", "original-target");
        var victim = (await setup.Writer.ObserveAsync("keep.txt", CancellationToken.None))!;
        using var store = new InMemoryStateStore();
        RegisterPair(store, setup.Root);
        var action = new SyncAction("act-bad-verify", 2, ActionKind.Update, Side.A, "keep.txt", null,
            Planned(LocalChecks.Bytes("planned-bytes")), victim, true, null);
        var result = await RunAsync(setup.Writer, new Source(LocalChecks.Bytes("wrong-bytes")), action, store)
            .ConfigureAwait(false);
        LocalChecks.Require(result is { Outcome: Outcome.Failed },
            "a download whose hash disagrees fails: " + result?.Outcome);
        LocalChecks.Require(File.ReadAllText(Path.Combine(setup.Root, "keep.txt")) == "original-target",
            "a failed verification leaves the original target untouched");
        LocalChecks.Require(!File.Exists(Path.Combine(setup.Recovery, ".syncshark-recovery-" + PairId,
            LocalChecks.OperationId("act-bad-verify") + ".recovery")),
            "a failed verification archives nothing because the original never moved");
        LocalChecks.Require(Directory.GetFiles(setup.Root, ".syncshark-transfer-*", SearchOption.AllDirectories)
            .Length == 0, "only the download's own partial was removed");
    }

    // Cancelling after the verified bytes are written deletes only the download's own partial; the deferred
    // archive never ran, so a RecoveryFirst original is still in place.
    private static async Task CancellationLeavesTheOriginalAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("direct-cancel").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "keep.txt", "original-target");
        var victim = (await setup.Writer.ObserveAsync("keep.txt", CancellationToken.None))!;
        using var store = new InMemoryStateStore();
        RegisterPair(store, setup.Root);
        using var cancel = new CancellationTokenSource();
        var source = new Source(LocalChecks.Bytes("planned")) { CancelAfterWrite = cancel };
        var action = new SyncAction("act-cancel", 2, ActionKind.Update, Side.A, "keep.txt", null,
            Planned(LocalChecks.Bytes("planned")), victim, true, null);
        var runner = new ActionRunner(new StoreCommitter(store, new SemaphoreSlim(1, 1), PairId, 1),
            V2Settings.Default, ExecutorChecks.Admission(), PairId, setup.Writer, source, [],
            new ConcurrentDictionary<string, Outcome>());
        var result = await runner.RunAsync(action, cancel.Token).ConfigureAwait(false);
        LocalChecks.Require(result is null, "a cancelled download records no outcome");
        LocalChecks.Require(File.ReadAllText(Path.Combine(setup.Root, "keep.txt")) == "original-target",
            "cancellation leaves the RecoveryFirst original in place because the archive is deferred");
        LocalChecks.Require(Directory.GetFiles(setup.Root, ".syncshark-transfer-*", SearchOption.AllDirectories)
            .Length == 0, "cancellation deletes only the download's own partial");
    }

    // A concurrent replacement during the download must never be overwritten: the conditional publish refuses and
    // the replaced file survives.
    private static async Task ConcurrentReplacementIsNotOverwrittenAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("direct-concurrent").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "file.txt", "original-target");
        var victim = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
        using var store = new InMemoryStateStore();
        RegisterPair(store, setup.Root);
        var source = new Source(LocalChecks.Bytes("downloaded")) { ReplaceDuringWrite = setup.Root };
        var action = new SyncAction("act-concurrent", 2, ActionKind.Update, Side.A, "file.txt", null,
            Planned(LocalChecks.Bytes("downloaded")), victim, true, null);
        var result = await RunAsync(setup.Writer, source, action, store).ConfigureAwait(false);
        LocalChecks.Require(result is { Outcome: Outcome.Uncertain }, "the checkpoint awaits safe reconciliation");
        var executor = new Executor(store, V2Settings.Default, ExecutorChecks.Admission());
        var settled = (await executor.ReconcileAsync(store.UnresolvedIntents(PairId), 1, setup.Writer, source,
            CancellationToken.None)).Single();
        LocalChecks.Require(settled.Outcome == Outcome.Skipped && settled.Reason!.Contains(EffectResolver.Superseded) &&
            store.UnresolvedIntents(PairId).Count == 0 && store.OpenNotices(PairId).Count > 0,
            "a real RecoveryFirst concurrent edit settles with a notice and never blocks the pair");
        LocalChecks.Require(File.ReadAllText(Path.Combine(setup.Root, "file.txt")) == "concurrent-replacement",
            "the concurrent replacement is never overwritten");
        LocalChecks.Require(Directory.GetFiles(setup.Root, ".syncshark-transfer-*", SearchOption.AllDirectories)
            .Length == 0, "the refused download's own partial is removed");
    }

    // The narrow checkpoint must be durable before the original is archived: inside the preserve hook the stored
    // unresolved intent already carries the local-download checkpoint.
    private static async Task CheckpointIsDurableBeforePreserveAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("direct-checkpoint").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "file.txt", "old-bytes");
        var victim = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
        var content = LocalChecks.Bytes("new-download-bytes");
        using var store = new InMemoryStateStore();
        RegisterPair(store, setup.Root);
        var sawCheckpoint = false;
        SyncLocalRecovery.SourcePreservedForTest = _ => sawCheckpoint =
            store.UnresolvedIntents(PairId).Any(intent =>
                intent.ProviderEffect?.Kind == SyncDirectDownloadCheckpoint.Kind);
        try
        {
            var action = new SyncAction("act-checkpoint", 2, ActionKind.Update, Side.A, "file.txt", null,
                Planned(content), victim, true, null);
            var result = await RunAsync(setup.Writer, new Source(content), action, store).ConfigureAwait(false);
            LocalChecks.Require(result is { Outcome: Outcome.Done, Recovery: not null },
                "the recovery-first direct download completes with its Recovery reference");
        }
        finally { SyncLocalRecovery.SourcePreservedForTest = null; }
        LocalChecks.Require(sawCheckpoint,
            "the verified-stage checkpoint is durable before the original is archived");
        LocalChecks.Require(File.ReadAllText(Path.Combine(setup.Root, "file.txt")) == "new-download-bytes" &&
            File.ReadAllText(Path.Combine(setup.Recovery, ".syncshark-recovery-" + PairId,
                LocalChecks.OperationId("act-checkpoint") + ".recovery")) == "old-bytes",
            "the overwritten original is kept in Recovery and the target holds the download");
    }

    // Crash after the archive but before the swap: the intent is unresolved, the original is in Recovery and the
    // verified stage remains. Reconcile publishes that exact stage create-only.
    private static async Task CrashAfterPreservePublishesTheVerifiedStageAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("direct-resume-publish").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "file.txt", "old-bytes");
        var victim = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
        var content = LocalChecks.Bytes("verified-new-bytes");
        await setup.Writer.PreserveAsync(victim, "act-crash-publish", CancellationToken.None).ConfigureAwait(false);
        var (stagePath, stageObjectId) = await WriteStageAsync(setup.Root, content).ConfigureAwait(false);
        var action = CrashAction("act-crash-publish", victim, content, stagePath, stageObjectId, setup.Recovery);

        var result = await ReconcileAsync(setup.Writer, action, setup.Root).ConfigureAwait(false);
        LocalChecks.Require(result is { Outcome: Outcome.Done, Recovery: not null },
            "reconcile publishes the verified stage and keeps the Recovery reference: " + result?.Reason);
        LocalChecks.Require(File.ReadAllBytes(Path.Combine(setup.Root, "file.txt")).SequenceEqual(content),
            "the verified stage becomes the target");
        LocalChecks.Require(!File.Exists(stagePath), "the published stage is no longer a leftover");
        var observed = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
        LocalChecks.Require(observed.Mtime == action.Source!.Mtime && result!.NewA == observed,
            "resumed verified publication stamps the source mtime and returns the actual baseline state");
        LocalChecks.Require(File.ReadAllText(Path.Combine(setup.Recovery, ".syncshark-recovery-" + PairId,
            LocalChecks.OperationId("act-crash-publish") + ".recovery")) == "old-bytes",
            "the archived original is untouched by resume");
    }

    private static async Task AlreadyPublishedDownloadRepairsDateAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("direct-resume-published-date");
        LocalChecks.Seed(setup.Root, "file.txt", "old-bytes");
        var victim = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
        var content = LocalChecks.Bytes("published-new-bytes");
        await setup.Writer.PreserveAsync(victim, "act-published-date", CancellationToken.None);
        var (stagePath, stageObjectId) = await WriteStageAsync(setup.Root, content);
        var action = CrashAction("act-published-date", victim, content, stagePath, stageObjectId, setup.Recovery);
        File.Move(stagePath, Path.Combine(setup.Root, "file.txt")); // an older writer published without its source date
        SyncLocalEndpointStore.BeforeStampPublishedDownloadForTest = () => throw new IOException("sharing violation fixture");
        try
        {
            var deferred = await ReconcileAsync(setup.Writer, action, setup.Root);
            LocalChecks.Require(deferred.Outcome == Outcome.Done && deferred.Reason!.Contains("Date could not be set; will retry"),
                "date failure never leaves a completed download uncertain");
            using var notices = new InMemoryStateStore();
            RegisterPair(notices, setup.Root);
            var notice = SyncDirectDownloadCheckpoint.NoticeFor(PairId, action, deferred)!;
            notices.AddNotices([notice, notice]);
            LocalChecks.Require(notices.OpenNotices(PairId).Count == 1 && notice.Kind == NoticeKind.Info,
                "date failure has one deduplicated informational notice");
        }
        finally { SyncLocalEndpointStore.BeforeStampPublishedDownloadForTest = null; }
        var result = await ReconcileAsync(setup.Writer, action, setup.Root);
        LocalChecks.Require(result is { Outcome: Outcome.Done } &&
            File.GetLastWriteTimeUtc(Path.Combine(setup.Root, "file.txt")) == action.Source!.Mtime.UtcDateTime &&
            result.NewA!.Mtime == action.Source.Mtime,
            "already-published checkpoint reconciles source date as well as content and baseline");
    }

    // Crash after the archive but before publish, with no surviving stage: reconcile restores the original from
    // Recovery and re-plans, never treating the path as a user deletion.
    private static async Task CrashAfterPreserveRestoresTheOriginalAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("direct-resume-restore").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "file.txt", "old-bytes");
        var victim = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
        await setup.Writer.PreserveAsync(victim, "act-crash-restore", CancellationToken.None).ConfigureAwait(false);
        var stagePath = Path.Combine(setup.Root, ".syncshark-transfer-" + Guid.NewGuid().ToString("N") + ".partial");
        var action = CrashAction("act-crash-restore", victim, LocalChecks.Bytes("new"), stagePath,
            "id:local:00000000:0000000000000000", setup.Recovery);

        var result = await ReconcileAsync(setup.Writer, action, setup.Root).ConfigureAwait(false);
        LocalChecks.Require(result is { Outcome: Outcome.Skipped } &&
            result.Reason == EffectResolver.NotApplied,
            "reconcile restores the original and re-plans the action: " + result?.Reason);
        LocalChecks.Require(File.ReadAllText(Path.Combine(setup.Root, "file.txt")) == "old-bytes",
            "the original is restored create-only from its Recovery copy");
        LocalChecks.Require(File.GetLastWriteTimeUtc(Path.Combine(setup.Root, "file.txt")) == victim.Mtime.UtcDateTime,
            "rollback restores the original mtime, not the download's source time or the rollback time");
    }

    // The excluded-intent reconciler resolves without writes: an open download checkpoint stays Uncertain and the
    // original remains recoverable in Recovery.
    private static async Task ExcludedCheckpointSettlesAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("direct-excluded").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "file.txt", "old-bytes");
        var victim = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
        var content = LocalChecks.Bytes("new-bytes");
        await setup.Writer.PreserveAsync(victim, "act-excluded", CancellationToken.None).ConfigureAwait(false);
        var (stagePath, stageObjectId) = await WriteStageAsync(setup.Root, content).ConfigureAwait(false);
        var action = CrashAction("act-excluded", victim, content, stagePath, stageObjectId, setup.Recovery) with
        {
            RecoveryFirst = false
        };

        var result = await ReconcileAsync(setup.Writer, action, setup.Root).ConfigureAwait(false);
        LocalChecks.Require(result is { Outcome: Outcome.Skipped } && result.Reason!.Contains(stagePath) &&
            result.Reason.Contains("Recovery"),
            "an excluded download settles with untouched files and Recovery/deletion-candidate locations");
        LocalChecks.Require(!File.Exists(Path.Combine(setup.Root, "file.txt")) && File.Exists(stagePath),
            "the excluded reconciler writes nothing in the protected area");
    }

    private static async Task UnownedPartialIsListedAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("direct-unowned-partial");
        LocalChecks.Seed(setup.Root, "file.txt", "old-bytes");
        var victim = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
        var bytes = LocalChecks.Bytes("unowned-bytes");
        var (path, _) = await WriteStageAsync(setup.Root, bytes);
        var action = CrashAction("act-unowned", victim, bytes, path,
            "id:local:00000000:0000000000000000", setup.Recovery);
        var result = await ReconcileAsync(setup.Writer, action, setup.Root);
        LocalChecks.Require(result.Outcome == Outcome.Skipped && result.Reason!.Contains("deletion candidate") &&
            result.Reason.Contains(path) && File.ReadAllBytes(path).SequenceEqual(bytes),
            "a partial without matching native ownership is listed, never deleted or silently forgotten");
    }

    private static async Task MovedRecoveryRootSettlesAsync()
    {
        var setup = await LocalWriterChecks.SetupAsync("direct-moved-recovery");
        LocalChecks.Seed(setup.Root, "file.txt", "old-bytes");
        var victim = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
        var bytes = LocalChecks.Bytes("new-bytes");
        await setup.Writer.PreserveAsync(victim, "act-moved", CancellationToken.None);
        var (path, id) = await WriteStageAsync(setup.Root, bytes);
        var action = CrashAction("act-moved", victim, bytes, path, id, setup.Recovery);
        var relocated = await LocalChecks.OpenWriter(setup.Root, setup.Recovery + "-new", CancellationToken.None);
        using var store = new InMemoryStateStore();
        RegisterPair(store, setup.Root);
        store.RecordIntent(PairId, action);
        var result = (await new Executor(store, V2Settings.Default, ExecutorChecks.Admission())
            .ReconcileAsync([action], 1, relocated, new Source(bytes), CancellationToken.None)).Single();
        LocalChecks.Require(result.Outcome == Outcome.Skipped && store.UnresolvedIntents(PairId).Count == 0 &&
            store.OpenNotices(PairId).Any(n => n.Detail.Contains(setup.Recovery)),
            "moved Recovery root settles with a stored notice naming the original location");
        LocalChecks.Require(File.Exists(path) && !File.Exists(Path.Combine(setup.Root, "file.txt")),
            "relocated Recovery handling neither publishes nor deletes anything");
    }

    // A checkpoint without a resume implementation can never fall through to the generic absent-target rule.
    private static async Task CheckpointWithoutResumeStaysUncertainAsync()
    {
        var endpoint = new FakeEndpoint(Side.A, "root-direct");
        var action = new SyncAction("act-no-resume", 2, ActionKind.Update, Side.A, "file.txt", null,
            Planned(LocalChecks.Bytes("new")), Victim(LocalChecks.Bytes("old")), true, null) with
        {
            ProviderEffect = new ProviderEffectCheckpoint(SyncDirectDownloadCheckpoint.Kind, "{}",
                DateTimeOffset.UtcNow)
        };
        var result = await EffectResolver.ResolveAsync(action, endpoint, endpoint, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(result.Outcome == Outcome.Uncertain,
            "a local download checkpoint without resume stays Uncertain instead of being classified absent");
    }

    private static void RegisterPair(IStateStore store, string root)
    {
        var local = LocalChecks.RootOf(root, "root-local");
        store.SavePair(new PairDef(PairId, "direct", local, local with { RootId = "root-cloud" }, [], [], true));
    }

    private static async Task<ActionResult> ReconcileAsync(LocalWriter writer, SyncAction action, string root)
    {
        using var store = new InMemoryStateStore();
        RegisterPair(store, root);
        store.RecordIntent(PairId, action);
        var executor = new Executor(store, V2Settings.Default, ExecutorChecks.Admission());
        var results = await executor.ReconcileAsync([action], 1, writer, new Source(Array.Empty<byte>()),
            CancellationToken.None).ConfigureAwait(false);
        return results.Single();
    }

    private static async Task<ActionResult?> RunAsync(LocalWriter writer, Source source, SyncAction action,
        InMemoryStateStore store)
    {
        var runner = new ActionRunner(new StoreCommitter(store, new SemaphoreSlim(1, 1), PairId, 1),
            V2Settings.Default, ExecutorChecks.Admission(), PairId, writer, source, [],
            new ConcurrentDictionary<string, Outcome>());
        return await runner.RunAsync(action, CancellationToken.None).ConfigureAwait(false);
    }

    private static SyncAction CrashAction(string actionId, ItemState victim, byte[] content, string stagePath,
        string stageObjectId, string recoveryRoot)
    {
        var recoveryPath = Path.Combine(recoveryRoot, ".syncshark-recovery-" + PairId,
            LocalChecks.OperationId(actionId) + ".recovery");
        return new SyncAction(actionId, 2, ActionKind.Update, Side.A, "file.txt", null, Planned(content), victim,
            true, null)
        {
            ProviderEffect = new ProviderEffectCheckpoint(SyncDirectDownloadCheckpoint.Kind,
                JsonSerializer.Serialize(new SyncDownloadStage(stagePath, stageObjectId, recoveryPath)),
                DateTimeOffset.UtcNow)
        };
    }

    private static async Task<(string Path, string ObjectId)> WriteStageAsync(string root, byte[] content)
    {
        var path = Path.Combine(root, ".syncshark-transfer-" + Guid.NewGuid().ToString("N") + ".partial");
        using var stage = SyncLocalFileAuthority.CreateNewMovableFile(path);
        await stage.Stream.WriteAsync(content).ConfigureAwait(false);
        await stage.Stream.FlushAsync().ConfigureAwait(false);
        stage.Stream.Flush(true);
        var identity = stage.ReadIdentity();
        return (path, SyncLocalEndpointStore.ObjectId(identity.VolumeSerialNumber, identity.FileId));
    }

    private static ItemState Planned(byte[] content) =>
        new("source", ItemKind.File, "id:dropbox:source", "rev-direct", LocalChecks.BlockHash(content),
            content.Length, DateTimeOffset.UnixEpoch, ItemFlags.None);

    private static ItemState Victim(byte[] content) =>
        new("file.txt", ItemKind.File, "id:local:00000001:0000000000000002", null, LocalChecks.BlockHash(content),
            content.Length, DateTimeOffset.UnixEpoch, ItemFlags.None);

    private sealed class Source(byte[] bytes) : IEndpointWriter, ISyncDirectDownloadSource
    {
        internal Action<string>? DuringWrite { get; set; }
        internal CancellationTokenSource? CancelAfterWrite { get; set; }
        internal string? ReplaceDuringWrite { get; set; }
        internal int OpenReadCalls { get; private set; }

        public async Task<SyncDownloadResult> DownloadToAsync(ItemState s, Stream destination, CancellationToken ct)
        {
            await destination.WriteAsync(bytes, ct).ConfigureAwait(false);
            await destination.FlushAsync(ct).ConfigureAwait(false);
            if (ReplaceDuringWrite is { } root)
            {
                var path = Path.Combine(root, "file.txt");
                File.Delete(path);
                File.WriteAllText(path, "concurrent-replacement");
            }
            DuringWrite?.Invoke(s.Path);
            if (CancelAfterWrite is { } cancel)
            {
                cancel.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return new SyncDownloadResult("id:dropbox:source", "rev-direct", bytes.Length,
                DropboxContentHash.Compute(bytes));
        }

        public Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct)
        {
            OpenReadCalls++;
            throw new InvalidOperationException("the direct path must not open a spool read");
        }

        public Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(SyncAction a, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    }
}
