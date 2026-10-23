namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    public static async Task HeldRecoveryAsync()
    {
        await HeldRecovery_EvidenceOutageRerunAsync();
        await HeldRecovery_PendingPreviewAsync();
        await HeldRecovery_RefreshClosedGenerationsAsync();
        await HeldRecovery_EmptyFolderRerunAsync(true);
        await HeldRecovery_EmptyFolderRerunAsync(false);
        await HeldRecovery_NonemptyTombstoneKeptAsync();
        await DeferredEvidence_NestedFolderAndCachedChildAsync();
        await HeldRecovery_RecreatedAncestorKeepsChildAsync();
        await HeldRecovery_RemoveBudgetAsync();
        await HeldRecovery_NewHeldRemoveBudgetAsync();
        await HeldRecovery_ReviewedPendingCountAsync();
        await HeldRecovery_UnconfirmedBlocksCompletionAsync();
        await HeldRecovery_UnconfirmedExamplesAsync();
        await HeldRecovery_SkipIdenticalRevisionAsync();
        await DropboxChecks.HeldFolderTombstonesAsync();
    }

    private static async Task HeldRecovery_EvidenceOutageRerunAsync()
    {
        var fx = Build();
        fx.A.Seed("held.txt", "local");
        fx.B.Seed("ordinary.txt", "cloud");
        fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B) { ProviderOutage = true });
        var clock = new CompletionClock();
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, Settings, clock);
        var root = Path.Combine(Path.GetTempPath(), "evidence-retry-" + Guid.NewGuid().ToString("N"));
        var runtime = new V2CliRuntime(null, fx.Store, run, new EndpointRootProbe(fx.Provider), root);
        var preparation = await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, default);
        Require(preparation.HeldDeletionExamples!.Single().Rule == HeldDeletion.NewerDeletionDisplayRule &&
            HeldDeletion.NewerDeletionRule == "dropbox-deletion-newer-than-local:recovery-first-delete;otherwise-upload",
            "CLI displays decision 78 while the persisted reviewed rule identifier remains compatible");
        var partial = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, null, true, null, _ => { }, default);
        Require(partial is { DeletionEvidencePendingCount: 1, ReviewedDeletionEvidencePendingCount: 1, UnreviewedPending: 0 },
            "CLI result distinguishes reviewed pending evidence from new unreviewed paths");
        Require(!fx.Store.FirstSyncDone(fx.Pair.PairId) && fx.WriterA.Puts == 1 && fx.WriterB.Puts == 0,
            "evidence outage keeps reviewed pending while unrelated download completes");
        fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B));
        // docs/design/retry-resume-rewind.md: failure deadlines gate automatic work. Re-applying
        // the same prepare is not an explicit Retry and must not bypass the persisted deadline.
        var sleeping = fx.Store.SyncRetryAt(fx.Pair.PairId, "held.txt");
        Require(sleeping is { ReprobeEvidence: true, HeldReviewed: true, Held: not null } &&
            sleeping.RetryAfter > clock.Now,
            "the outage retains reviewed evidence debt with a future retry deadline");
        clock.Now = sleeping!.RetryAfter.AddTicks(-1);
        // D85: an early apply may complete first sync with durable debt; preview keeps this prepare available.
        var beforeDue = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(beforeDue.HeldDeletions.All(item => item.Path != "held.txt") &&
            beforeDue.Actions.All(action => action.Path != "held.txt") &&
            fx.Store.SyncRetryAt(fx.Pair.PairId, "held.txt") == sleeping &&
            fx.WriterA.Puts == 1 && fx.WriterB.Puts == 0 && fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
            "before its deadline held evidence stays dormant with unchanged debt and zero new effects");
        clock.Now = sleeping.RetryAfter;
        await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, null, true, null, _ => { }, default);
        Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && fx.WriterA.Puts == 1 && fx.WriterB.Puts == 1,
            "same prepare resumes evidence without duplicate ordinary download");
        Console.WriteLine("PASS HeldRecovery_EvidenceOutageRerun");
    }

    private static async Task HeldRecovery_PendingPreviewAsync()
    {
        var fx = Build();
        var source = fx.A.Seed("pending.txt", "local");
        var action = new SyncAction("pending-upload", 2, ActionKind.CopyNew, Side.B, source.Path,
            null, source, null, false, null)
        { ProviderEffect = new ProviderEffectCheckpoint("upload-commit", "s1", DateTimeOffset.UtcNow) };
        fx.Store.RecordIntent(fx.Pair.PairId, action);
        var plan = await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(plan.Actions.All(item => item.Path != source.Path), "pending upload footprint stays protected while preview continues");
        Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Single().ProviderEffect == action.ProviderEffect,
            "preview retains uncertain submission authority without replay");
        Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 && fx.WriterB.Puts == 0,
            "preview never deletes or resubmits the pending upload");
        Console.WriteLine("PASS HeldRecovery_PendingPreview");
    }

    private static async Task HeldRecovery_RefreshClosedGenerationsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "held-refresh-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        fx.A.Seed("folder", "", ItemKind.Folder, mtime: at);
        var child = fx.A.Seed("folder/file.txt", "content", mtime: at);
        fx.Provider.AddInventory(fx.Pair.B, new RecoveryEvidenceInventory(fx.B)
            { Tombstone = true, Deleted = at.AddDays(1), ContentHash = child.Hash });
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None,
            ReviewedHeld(plan));
        Require(report.Deferred == 0 && store.FirstSyncDone(fx.Pair.PairId),
            "held descendant refresh succeeds after both SQLite preview generations close");
        Require(await fx.A.ObserveAsync("folder", CancellationToken.None) is null,
            "explicitly reviewed empty exact-tombstoned folder is deleted after its child");
        // The child equals the retained deleted revision, so the existing identical-history
        // exemption supplies recovery. Empty folders have no bytes; LocalWriter rejects Preserve(folder).
        Require(fx.WriterA.Preserves == 0,
            "identical-history child needs no duplicate archive and the emptied folder has no bytes to preserve");
        Console.WriteLine("PASS HeldRecovery_RefreshClosedGenerations");
    }

    private static async Task HeldRecovery_EmptyFolderRerunAsync(bool tombstone)
    {
        var root = Path.Combine(Path.GetTempPath(), "held-rerun-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        fx.A.Seed("folder", "", ItemKind.Folder, mtime: at);
        var child = fx.A.Seed("folder/file.txt", "content", mtime: at);
        var source = new RecoveryEvidenceInventory(fx.B)
            { Tombstone = tombstone, Deleted = at.AddDays(1), ContentHash = child.Hash };
        fx.Provider.AddInventory(fx.Pair.B, source);
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        // Model interruption after the child's durable Done but before the folder is attempted.
        // Leaving the folder unreviewed prevents the first attempt from completing it.
        var folder = plan.HeldDeletions.Single(item => item.Path == "folder");
        plan = plan with { HeldDeletions = plan.HeldDeletions.Where(item => item.Path != "folder").ToArray(),
            UnreviewedHeldDeletions = [folder] };
        var first = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None,
            ReviewedHeld(plan));
        Require(first.UnreviewedPending == 1, "unreviewed held folder remains explicitly counted");
        Require(first.Deferred == 0 && store.FirstSyncDone(fx.Pair.PairId) && await fx.A.ObserveAsync("folder/file.txt", CancellationToken.None) is null,
            "child finishes and first sync completes while the unreviewed empty parent remains held");
        store.Dispose();
        using var reopened = SqliteStateStore.Open(root, Settings, TimeProvider.System);
        var restarted = new SyncRun(reopened, NewExecutor(reopened), fx.Provider, Settings, TimeProvider.System);
        var result = await restarted.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        if (tombstone)
        {
            Require(await fx.A.ObserveAsync("folder", default) is not null,
                "unreviewed folder remains until an explicit notice choice after restart");
            var decision = reopened.OpenNotices(fx.Pair.PairId).Single(notice => notice.Path == "folder" && notice.ReasonKey == "deletion-review");
            Require(reopened.SyncRetryAt(fx.Pair.PairId, "folder") is { MissingDeleteIdentityReview: true, Kind: "decision" },
                "no-baseline tombstone without a source identity becomes review, not indefinite retry");
            await restarted.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, decision.NoticeId!, NoticeChoices.DeleteReviewed, default);
            result = await restarted.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        }
        Require(result.Deferred == 0 && reopened.FirstSyncDone(fx.Pair.PairId), "rerun finishes without prior in-memory delete proof");
        Require((await fx.A.ObserveAsync("folder", CancellationToken.None) is null) == tombstone,
            "only an exact current tombstone removes the empty folder on rerun");
        if (!tombstone)
            Require(await fx.B.ObserveAsync("folder", CancellationToken.None) is not null, "untombstoned folder is synced normally");
        Console.WriteLine("PASS HeldRecovery_EmptyFolderRerun tombstone=" + tombstone);
    }

    private static async Task HeldRecovery_NonemptyTombstoneKeptAsync()
    {
        var fx = Build();
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        fx.A.Seed("folder", "", ItemKind.Folder, mtime: at);
        fx.A.Seed("folder/file.txt", "newer", mtime: at.AddDays(2));
        fx.Provider.AddInventory(fx.Pair.B, new RecoveryEvidenceInventory(fx.B)
            { Tombstone = true, Deleted = at.AddDays(1) });
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None, ReviewedHeld(plan));
        Require(report.Deferred == 0 && fx.WriterA.Deletes == 0 &&
            await fx.A.ObserveAsync("folder/file.txt", CancellationToken.None) is not null &&
            await fx.B.ObserveAsync("folder/file.txt", CancellationToken.None) is not null,
            "a nonempty folder with a surviving newer child is kept and synced despite its exact tombstone");
        Console.WriteLine("PASS HeldRecovery_NonemptyTombstoneKept");
    }

    private static async Task HeldRecovery_RecreatedAncestorKeepsChildAsync()
    {
        var fx = Build();
        fx.A.Seed("folder", "", ItemKind.Folder);
        fx.B.Seed("folder", "", ItemKind.Folder);
        fx.A.Seed("folder/nested", "", ItemKind.Folder);
        // The old ancestor tombstone has been superseded by a live folder. The real
        // Dropbox classification counterpart is checked in HeldFolderTombstonesAsync.
        fx.Provider.AddInventory(fx.Pair.B, new RecoveryEvidenceInventory(fx.B));
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        plan = plan with
        {
            Actions = plan.Actions.Where(action => action.Path != "folder/nested").ToArray(),
            HeldDeletions = [new HeldDeletion("folder/nested", Side.A)]
        };
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None, ReviewedHeld(plan));
        Require(report.Deferred == 0 && fx.WriterA.Deletes == 0 &&
            await fx.A.ObserveAsync("folder/nested", CancellationToken.None) is not null &&
            await fx.B.ObserveAsync("folder/nested", CancellationToken.None) is not null,
            "held empty child under a recreated ancestor is kept and synced");
        Console.WriteLine("PASS HeldRecovery_RecreatedAncestorKeepsChild");
    }

    private static async Task HeldRecovery_RemoveBudgetAsync()
    {
        var fx = Build();
        fx.A.Seed("held.txt", "content");
        var evidence = new RecoveryEvidenceInventory(fx.B);
        fx.Provider.AddInventory(fx.Pair.B, evidence);
        var root = Path.Combine(Path.GetTempPath(), "held-budget-" + Guid.NewGuid().ToString("N"));
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), root);
        const string choices = "{\"oneSided\":[{\"prefix\":\"\",\"side\":\"Local\",\"action\":\"remove\"}]}";
        var prepared = await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
        Require(prepared.ChoiceEffects!.Single() is { Files: 1, Bytes: 7 }, "prepare freezes held remove potential instead of zero");
        evidence.Cached = true;
        await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { }, CancellationToken.None);
        Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && fx.WriterA.Deletes == 1,
            "resolved held explicit remove does not falsely grow beyond its prepared budget");
        Console.WriteLine("PASS HeldRecovery_RemoveBudget");
    }

    private static async Task HeldRecovery_NewHeldRemoveBudgetAsync()
    {
        var fx = Build();
        fx.A.Seed("reviewed.txt", "content");
        fx.B.Seed("ordinary.txt", "cloud");
        fx.Provider.AddInventory(fx.Pair.B, new RecoveryEvidenceInventory(fx.B));
        var root = Path.Combine(Path.GetTempPath(), "new-held-budget-" + Guid.NewGuid().ToString("N"));
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), root);
        const string choices = "{\"oneSided\":[{\"prefix\":\"\",\"side\":\"Local\",\"action\":\"remove\"}]}";
        await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, default, choices);
        fx.A.Seed("new.txt", "not reviewed");
        var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { }, default);
        Require(result is { UnreviewedPending: 1, ReviewedDeletionEvidencePendingCount: 0,
                DeletionEvidencePendingCount: 0, FirstSyncComplete: true } &&
            result.DeletionEvidencePendingExamples.Count == 0,
            "newly held remove stays unreviewed instead of refusing the whole apply for budget growth");
        Require(result.ChoiceEffects!.Single() is { Files: 1, Bytes: 7 } && fx.WriterA.Deletes == 1 &&
            fx.WriterA.Puts == 1 && await fx.A.ObserveAsync("new.txt", default) is not null &&
            await fx.B.ObserveAsync("new.txt", default) is null,
            "only frozen held victim is charged and removed; ordinary work finishes without touching new held path");
        Console.WriteLine("PASS HeldRecovery_NewHeldRemoveBudget");
    }

    private static async Task HeldRecovery_ReviewedPendingCountAsync()
    {
        var fx = Build();
        fx.A.Seed("reviewed.txt", "content");
        fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B) { Fail = true });
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        // Pending paths are deduplicated, unlike held records. Reviewed counts must come from
        // the reviewed partition, not subtraction of record counts from distinct path counts.
        plan = plan with { UnreviewedHeldDeletions = [new HeldDeletion("new.txt", Side.A), new HeldDeletion("NEW.txt", Side.A)] };
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default, ReviewedHeld(plan));
        Require(report.DeletionEvidencePending.Count == 1 && report.UnreviewedPending == 2 &&
            report.ReviewedDeletionEvidencePendingCount == 1,
            "reviewed pending count is measured directly even when unreviewed records share a pending path");
        Console.WriteLine("PASS HeldRecovery_ReviewedPendingCount");
    }

    private static async Task HeldRecovery_UnconfirmedExamplesAsync()
    {
        var fx = HashlessFixture();
        var paths = Enumerable.Range(0, 55).Select(index => $"bad-{index:D2}.txt").ToArray();
        foreach (var path in paths)
        {
            fx.A.Seed(path, "unreadable");
            fx.A.Unhashable.Add(path);
        }
        var root = Path.Combine(Path.GetTempPath(), "unconfirmed-examples-" + Guid.NewGuid().ToString("N"));
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), root);
        await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, default);
        var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, null, true, null, _ => { }, default);
        Require(result is { ContentUnconfirmedCount: 55, Deferred: 0, FirstSyncComplete: true,
                DeletionEvidencePendingCount: 0 } && result.ContentUnconfirmedExamples.SequenceEqual(paths.Take(50)),
            "CLI exposes full content-unconfirmed count and only fifty deterministic example paths");
        var human = SyncCli.RenderHuman(result);
        Require(human.Contains("content unconfirmed=55", StringComparison.Ordinal) &&
            human.Contains("showing 50 of 55 content-unconfirmed paths", StringComparison.Ordinal) &&
            paths.Take(50).All(path => human.Contains("content unconfirmed: " + path, StringComparison.Ordinal)) &&
            !human.Contains("bad-50.txt", StringComparison.Ordinal),
            "human incomplete report names bounded content-unconfirmed examples and the total");
        Console.WriteLine("PASS HeldRecovery_UnconfirmedExamples");
    }

    private static async Task HeldRecovery_UnconfirmedBlocksCompletionAsync()
    {
        var fx = HashlessFixture();
        fx.A.Seed("bad.txt", "unreadable");
        fx.A.Unhashable.Add("bad.txt");
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(plan.Actions.All(action => action.Path != "bad.txt"), "unhashable executable row was dropped");
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
        Require(report.Deferred == 0 && report.DeletionEvidencePending.Count == 0 &&
            report.ContentUnconfirmedCount == 1 && report.ContentUnconfirmedExamples.SequenceEqual(["bad.txt"]) &&
            fx.Store.FirstSyncDone(fx.Pair.PairId) && fx.Store.SyncRetryAt(fx.Pair.PairId, "bad.txt") is not null,
            "content-unconfirmed completes with durable retry input without being mislabeled deletion evidence");
        Console.WriteLine("PASS HeldRecovery_UnconfirmedBlocksCompletion");
    }

    private static async Task HeldRecovery_SkipIdenticalRevisionAsync()
    {
        var at = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var cases = new (Func<ItemState, string?> HistoryHash, int Deletes, int Deferred, bool Restored, string Message)[]
        {
            (local => local.Hash, 1, 0, false, "an identical Dropbox history revision deletes without a Recovery copy"),
            (_ => new string('a', 64), 0, 0, true, "a different history hash restores the local edit"),
            (_ => null, 0, 1, false, "a missing history hash stays pending")
        };
        foreach (var cached in new[] { false, true })
        foreach (var (historyHash, deletes, deferred, restored, message) in cases)
        {
            var fx = Build();
            var local = fx.A.Seed("held.txt", "content", mtime: at.AddHours(-1));
            fx.Provider.AddInventory(fx.Pair.B, new RecoveryEvidenceInventory(fx.B)
                { Deleted = at, ContentHash = historyHash(local), Cached = cached });
            var run = fx.Run();
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None,
                ReviewedHeld(plan));
            Require(report.Deferred == 0 && fx.Store.FirstSyncDone(fx.Pair.PairId) &&
                report.DeletionEvidencePending.Count == deferred && fx.WriterA.Deletes == deletes && fx.WriterA.Preserves == 0, message);
            Require(fx.WriterB.Puts == (restored ? 1 : 0) &&
                fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.Kind == NoticeKind.EditBeatsDelete) == restored,
                "only a differing known hash restores with an EditBeatsDelete notice");
            Require((await fx.A.ObserveAsync("held.txt", default) is not null) == (deletes == 0),
                "edits and unknown content remain local");
            if (restored)
                Require((await fx.B.ObserveAsync("held.txt", default))?.Hash == local.Hash,
                    "the uploaded restoration carries the local edit's hash");
        }
        // The execution-time observation wins: an edit between prepare and apply differs from history.
        {
            var fx = Build();
            var local = fx.A.Seed("held.txt", "content", mtime: at.AddHours(-1));
            fx.Provider.AddInventory(fx.Pair.B, new RecoveryEvidenceInventory(fx.B)
                { Deleted = at, ContentHash = local.Hash });
            var run = fx.Run();
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            fx.A.Seed("held.txt", "changed locally", mtime: at.AddHours(-2));
            var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None,
                ReviewedHeld(plan));
            Require(report.Deferred == 0 && fx.WriterA.Deletes == 0 && fx.WriterA.Preserves == 0 &&
                fx.WriterB.Puts == 1 &&
                (await fx.A.ObserveAsync("held.txt", default))?.Hash == (await fx.B.ObserveAsync("held.txt", default))?.Hash &&
                fx.Store.OpenNotices(fx.Pair.PairId).Any(n => n.Kind == NoticeKind.EditBeatsDelete),
                "a local edit after prepare is re-observed and restored, never deleted");
        }
        Console.WriteLine("PASS HeldRecovery_SkipIdenticalRevision");
    }

    private sealed class RecoveryEvidenceInventory(FakeEndpoint inner) : IInventorySource, IDeferredDeletionEvidence
    {
        public bool Tombstone { get; init; }
        public bool Cached { get; set; }
        public DateTimeOffset? Deleted { get; init; }
        public string? ContentHash { get; init; }
        public bool IsAtOrUnderExactTombstone(string path) => Tombstone && PairPath.Same(path, "folder");
        public IReadOnlyList<string> FolderDeletionCandidates(IReadOnlyList<string> paths) =>
            paths.Where(IsAtOrUnderExactTombstone).ToArray();
        public Task<DeletionEvidencePreparation> PrepareDeletionEvidenceAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            Task.FromResult(Cached
                ? new DeletionEvidencePreparation(paths.ToDictionary(path => path, _ => Evidence()), [])
                : new DeletionEvidencePreparation(new Dictionary<string, SyncRemoteDeletionEvidence>(), paths));
        public Task ResolveDeletionEvidenceAsync(IReadOnlyList<string> paths,
            Action<string, SyncRemoteDeletionEvidence> resolved, CancellationToken ct)
        {
            foreach (var path in paths) resolved(path, Evidence());
            return Task.CompletedTask;
        }
        private SyncRemoteDeletionEvidence Evidence() => new(Deleted,
            "Dropbox revision history confirms the deletion", ContentHash: ContentHash);
        public Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> progress, CancellationToken ct) =>
            inner.FullAsync(gen, include, progress, ct);
        public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) =>
            inner.ChangesSinceAsync(cursor, gen, include, ct);
        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) => inner.HashAsync(paths, ct);
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            inner.DeletionTimesAsync(paths, ct);
    }
}
