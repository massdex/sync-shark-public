using System.Text;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // Registered by the coordinating test runner; no provider credentials or user paths.
    public static async Task RecoveryRestoreChecksAsync()
    {
        foreach (var mode in new[] { "dry", "identical", "conflict", "conflict-apply", "opposite-dry", "opposite-apply", "apply", "copy", "failure", "no-version" })
        {
            var root = LocalChecks.FreshRoot("recovery-page-" + mode + "-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            var bytes = Encoding.UTF8.GetBytes("retained bytes");
            var source = new ItemState("file.txt", ItemKind.File, "archived-entity", "revision-1",
                DropboxContentHash.Compute(bytes), bytes.Length, DateTimeOffset.UtcNow.AddDays(-1), ItemFlags.None);
            var removed = new SyncAction("retained-action", 0, ActionKind.Delete, Side.A, source.Path,
                null, null, source, true, "old-key");
            var gen = store.BeginGeneration(fx.Pair.A.RootId, "fixture");
            store.CommitResult(fx.Pair.PairId, gen, removed, new ActionResult(removed.ActionId, Outcome.Done,
                null, null, new RecoveryRef(Side.A, "fixture-archive", source.VersionId, source.Hash!, source.Size), null));
            var recovery = store.RecoveryPage(fx.Pair.PairId, new RecoveryQuery()).Single();
            if (mode == "apply")
            {
                var opposite = fx.B.Seed(source.Path, "retained bytes");
                var stale = new SyncAction("stale-baseline", 0, ActionKind.Adopt, Side.A, source.Path,
                    null, opposite, source, false, "stale-key");
                store.CommitResult(fx.Pair.PairId, gen, stale,
                    new ActionResult(stale.ActionId, Outcome.Done, source, opposite, null, null));
            }
            if (mode is "opposite-dry" or "opposite-apply") fx.B.Seed(source.Path, "opposite current bytes");
            if (mode is "identical" or "conflict" or "conflict-apply" or "copy")
                fx.A.Seed(source.Path, mode == "identical" ? "retained bytes" : "current bytes");
            if (mode == "failure")
            {
                var second = removed with { ActionId = "second-retained-action", Path = "other.txt",
                    ExpectedTarget = source with { Path = "other.txt" } };
                store.CommitResult(fx.Pair.PairId, gen, second, new ActionResult(second.ActionId, Outcome.Done,
                    null, null, new RecoveryRef(Side.A, "second-archive", source.VersionId, source.Hash!, source.Size), null));
            }
            var archive = new RecoveryFixtureWriter(fx.WriterA, source, bytes)
                { FailOpen = mode == "failure", Missing = mode == "no-version" };
            fx.Provider.AddWriter(fx.Pair.A, archive);
            var provider = new RecoveryFixtureProvider(fx.Provider);
            var run = new SyncRun(store, fx.Executor, provider, Settings, TimeProvider.System);
            var dry = mode is "dry" or "identical" or "conflict" or "opposite-dry";
            var noticesBefore = store.OpenNotices(fx.Pair.PairId).Count;
            var result = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId, new RecoveryQuery(Id: recovery.RecoveryId),
                mode == "copy", dry, CancellationToken.None)).Single();
            var expected = mode switch
            {
                "dry" => "RESTORE", "identical" => "SKIP-PRESENT", "conflict" or "conflict-apply" or "opposite-dry" or "opposite-apply" => "CONFLICT",
                "failure" => "FAILED", "no-version" => "NO-VERSION", _ => "RESTORED"
            };
            Require(result.Classification == expected, "Recovery page classification: " + mode);
            if (dry)
            {
                Require(provider.WritePreparations == 0 && provider.ReadPreparations == 1 && archive.Opens == 0 &&
                    fx.WriterA.Calls == 0 && store.OpenNotices(fx.Pair.PairId).Count == noticesBefore &&
                    store.CountUnresolvedIntents(fx.Pair.PairId) == 0, "dry restore has no mutation or historical-byte download");
                continue;
            }
            Require(store.RecoveryRestoreProtected(fx.Pair.PairId, result.Destination ?? source.Path) ==
                (mode is not ("failure" or "no-version" or "conflict-apply" or "opposite-apply")),
                "only an effect intent retains durable next-run protection; prewrite failures are informational");
            if (mode is "conflict-apply" or "opposite-apply")
            {
                var notice = store.RecoveryRestoreNoticesPage(fx.Pair.PairId).Single();
                Require(notice.Kind == NoticeKind.Info && notice.ConflictOperation?.Actions.Count == 0 &&
                    fx.WriterA.Puts == 0 && fx.WriterA.Preserves == 0, "real conflict writes no file and freezes no intent");
                await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.RetryNow, default);
                Require(fx.WriterA.Puts == 0, "Retry rechecks conflict without replacing current bytes");
                await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, "restore-as-copy", default);
                Require(fx.WriterA.Puts == 1 && (mode == "conflict-apply"
                    ? await fx.A.ObserveAsync(source.Path, default)
                    : await fx.B.ObserveAsync(source.Path, default)) is { Hash: var preserved } && preserved != source.Hash,
                    "copy choice keeps current original and creates one separate restored file");
                continue;
            }
            if (mode == "no-version")
            {
                var notice = store.RecoveryRestoreNoticesPage(fx.Pair.PairId).Single();
                await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.Ignore, default);
                Require(store.NoticeById(fx.Pair.PairId, notice.NoticeId!) is { Closed: true }, "actionless Ignore closes the notice");
                await run.RestoreRecoveryPageAsync(fx.Pair.PairId, new RecoveryQuery(Id: recovery.RecoveryId), false, false, default);
                await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, "dismiss", default);
                Require(store.NoticeById(fx.Pair.PairId, notice.NoticeId!) is { Closed: true }, "Dismiss closes a reopened actionless notice");
                fx.A.Seed(source.Path, "retained bytes");
                store.MarkFirstSyncDone(fx.Pair.PairId);
                await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
                await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
                Require(!store.RecoveryRestoreProtected(fx.Pair.PairId, source.Path) &&
                    await fx.B.ObserveAsync(source.Path, CancellationToken.None) is { Hash: var restoredHash } && restoredHash == source.Hash,
                    "actionless unavailable-version notice releases safely when identical bytes arrive");
                continue;
            }
            if (mode == "failure")
            {
                var batch = await run.RestoreRecoveryPageAsync(fx.Pair.PairId, new RecoveryQuery(), false, false, CancellationToken.None);
                Require(batch.Count == 2 && batch.Count(item => item.Classification == "FAILED") == 1 &&
                    batch.Count(item => item.Classification == "RESTORED") == 1, "one failed archive cannot stop its sibling restore");
                Require(store.RecoveryRestoreNoticesPage(fx.Pair.PairId).Count == 2, "repeated failure deduplicates its notice");
                archive.FailOpen = false;
                store.MarkFirstSyncDone(fx.Pair.PairId);
                await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
                await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
                Require(await fx.A.ObserveAsync(source.Path, CancellationToken.None) is not null &&
                    await fx.B.ObserveAsync(source.Path, CancellationToken.None) is not null,
                    "automatic archive retry creates and propagates without a manual Retry");
                continue;
            }
            Require(fx.WriterA.Preserves == 0, "restore is always create-only, never an overwrite");
            var again = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId, new RecoveryQuery(Id: recovery.RecoveryId),
                mode == "copy", false, CancellationToken.None)).Single();
            Require(again.Classification == "SKIP-PRESENT" && again.Destination == result.Destination && fx.WriterA.Puts == 1,
                "same recovery identity reuses one destination, including copy retries");
            store.MarkFirstSyncDone(fx.Pair.PairId);
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(await fx.A.ObserveAsync(result.Destination!, CancellationToken.None) is { Hash: var hashA } &&
                await fx.B.ObserveAsync(result.Destination!, CancellationToken.None) is { Hash: var hashB } &&
                hashA == source.Hash && hashB == source.Hash && store.BaselinesAt(fx.Pair.PairId, Side.A, [result.Destination!]).Count == 1,
                "the following run adopts or propagates restored bytes, never deletes either side");
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            var oldDelete = new SyncAction("old-approved-delete", 0, ActionKind.Delete, Side.B, result.Destination!,
                null, null, await fx.B.ObserveAsync(result.Destination!, CancellationToken.None), true, "old-key");
            var adopt = oldDelete with { ActionId = "fresh-adopt", Kind = ActionKind.Adopt };
            var replay = run.GuardReviewedRecoveryRestores(fx.Pair, new Plan(fx.Pair.PairId, gen, [oldDelete, adopt], []));
            Require(replay.Actions.Count == 1 && replay.Actions[0].Kind == ActionKind.Adopt &&
                replay.SkippedActions.Single().ActionId == oldDelete.ActionId &&
                replay.Notices.Single().Detail.Contains("fresh incremental", StringComparison.Ordinal),
                "old reviewed deletion stays blocked after restore notice closes; fresh adoption remains available");
            var noticeId = ShortId.Make("rrs", fx.Pair.PairId, recovery.RecoveryId);
            var completed = store.NoticeById(fx.Pair.PairId, noticeId)!.Value.Notice;
            var oldActionId = completed.ConflictOperation!.Actions.Single().ActionId;
            await fx.A.DeleteAsync(oldDelete with { Target = Side.A,
                ExpectedTarget = await fx.A.ObserveAsync(result.Destination!, default) }, default);
            var repeat = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId, new RecoveryQuery(Id: recovery.RecoveryId),
                false, false, default)).Single();
            Require(repeat.Classification == "RESTORED" && fx.WriterA.Puts == 2 &&
                store.NoticeById(fx.Pair.PairId, noticeId)!.Value.Notice.ConflictOperation!.Actions.Single().ActionId != oldActionId,
                "a missing completed destination starts a fresh durable executor attempt under the same receipt notice");
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            fx.A.Seed(repeat.Destination!, "changed since completion");
            var changed = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId, new RecoveryQuery(Id: recovery.RecoveryId), false, false, default)).Single();
            Require(changed.Classification == "CONFLICT", "completed changed destination is never replayed over current bytes");
            var freshCopy = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId, new RecoveryQuery(Id: recovery.RecoveryId), true, false, default)).Single();
            Require(freshCopy.Classification == "RESTORED" && freshCopy.Destination != repeat.Destination && fx.WriterA.Puts == 3,
                "explicit as-copy after completion creates a fresh attempt without replacing changed bytes");
        }
        await RecoveryBulkSelectionCheckAsync();
        await RecoveryCrashPreviewGateCheckAsync();
        await RecoveryLateOppositeChangeCheckAsync();
        await RecoveryAbsentEffectCheckAsync();
        await RecoveryHashCasingCheckAsync();
        RecoveryPlanningSnapshotCheck();
        RecoveryDeletionEvidenceAttributionCheck();
        Require(SyncRun.RecoveryCopyPath("Folder/report.txt", new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), 2) ==
            "Folder/report (restored 2026-01-02) 2.txt", "bounded copy names preserve directory and extension");
    }

    private static RecoveryRecord RetainRecoveryFixture(Fixture fx, ItemState source, string actionId)
    {
        var action = new SyncAction(actionId, 0, ActionKind.Delete, Side.A, source.Path, null, null, source, true, null);
        fx.Store.CommitResult(fx.Pair.PairId, fx.Store.BeginGeneration(fx.Pair.A.RootId, "fixture"), action,
            new ActionResult(actionId, Outcome.Done, null, null,
                new RecoveryRef(Side.A, "fixture-archive-" + actionId, source.VersionId, source.Hash!, source.Size), null));
        return fx.Store.RecoveryPage(fx.Pair.PairId, new RecoveryQuery(ActionId: actionId)).Single();
    }

    private static async Task RecoveryBulkSelectionCheckAsync()
    {
        var root = LocalChecks.FreshRoot("recovery-bulk-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.DropboxHashes = fx.B.DropboxHashes = true;
        var bytes = Encoding.UTF8.GetBytes("retained bytes");
        var source = new ItemState("Folder/file.txt", ItemKind.File, "archive", "revision",
            DropboxContentHash.Compute(bytes), bytes.Length, DateTimeOffset.UtcNow.AddDays(-1), ItemFlags.None);
        RetainRecoveryFixture(fx, source, "archive-first");
        RetainRecoveryFixture(fx, source with { Path = "folder/FILE.txt" }, "archive-second");
        var rows = store.RecoveryPage(fx.Pair.PairId, new RecoveryQuery()).ToArray();
        var newest = rows.OrderByDescending(row => row.Created).ThenByDescending(row => row.RecoveryId, StringComparer.Ordinal).First();
        var archive = new RecoveryFixtureWriter(fx.WriterA, source, bytes);
        fx.Provider.AddWriter(fx.Pair.A, archive);
        var run = fx.Run();
        var results = new List<RecoveryRestoreItem>();
        string? after = null;
        for (var page = 0; page < 2; page++)
        {
            var result = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId,
                new RecoveryQuery(PathPrefix: "folder", AfterId: after, Limit: 1), false, false, default)).Single();
            results.Add(result);
            after = result.Id;
        }
        Require(results.Single(item => item.Classification == "RESTORED").Id == newest.RecoveryId &&
            results.Count(item => item.Classification == "SUPERSEDED") == 1 && fx.WriterA.Puts == 1 &&
            store.RecoveryRestoreNoticesPage(fx.Pair.PairId).Count == 1,
            "newest path winner spans page boundaries; superseded rows create neither notice nor write");
        var older = rows.Single(row => row.RecoveryId != newest.RecoveryId);
        var filtered = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId,
            new RecoveryQuery(ActionId: older.ActionId), false, true, default)).Single();
        Require(filtered.Classification != "SUPERSEDED", "winner selection is within the filter, not globally newest");
        var explicitCopy = await run.RestoreRecoveryPageAsync(fx.Pair.PairId, new RecoveryQuery(), true, false, default);
        Require(explicitCopy.All(item => item.Classification is "RESTORED" or "SKIP-PRESENT") &&
            explicitCopy.Select(item => item.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2,
            "as-copy permits older rows while same-path rows execute serially");
    }

    private static async Task RecoveryCrashPreviewGateCheckAsync()
    {
        foreach (var effectWritten in new[] { false, true })
        {
            var root = LocalChecks.FreshRoot("recovery-crash-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            var bytes = Encoding.UTF8.GetBytes("retained bytes");
            var source = new ItemState("file.txt", ItemKind.File, "archive", "revision", DropboxContentHash.Compute(bytes),
                bytes.Length, DateTimeOffset.UtcNow.AddDays(-1), ItemFlags.None);
            var record = RetainRecoveryFixture(fx, source, "crash-archive");
            var action = new SyncAction("rrs-crashed-attempt", 0, ActionKind.CopyNew, Side.A, source.Path,
                null, source, null, false, null) { SuppressReconciledBaseline = true };
            var id = ShortId.Make("rrs", fx.Pair.PairId, record.RecoveryId);
            var notice = new Notice(fx.Pair.PairId, 0, NoticeKind.UserDecision, source.Path, "pending", SyncRun.RecoveryRestorePending,
                id, ConflictOperation: new ConflictNoticeOperation(record.RecoveryId, source.Path, [action], []));
            store.AddNotices([notice]);
            store.RecordIntent(fx.Pair.PairId, action);
            if (effectWritten) fx.A.Seed(source.Path, "retained bytes");
            var archive = new RecoveryFixtureWriter(fx.WriterA, source, bytes);
            fx.Provider.AddWriter(fx.Pair.A, archive);
            var run = fx.Run();
            var before = store.NoticeById(fx.Pair.PairId, id)!.Value;
            await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
            var afterPreview = store.NoticeById(fx.Pair.PairId, id)!.Value;
            Require(fx.WriterA.Puts == 0 && archive.Opens == 0 && afterPreview.Closed == before.Closed &&
                afterPreview.Notice.ReasonKey == before.Notice.ReasonKey && afterPreview.Notice.Detail == before.Notice.Detail &&
                afterPreview.Notice.ConflictOperation!.Actions.Single().ActionId == action.ActionId &&
                store.RecoveryRestoreProtected(fx.Pair.PairId, source.Path),
                "first-sync preview leaves restore state and frozen protections unchanged, with no retry writes");
            // A queued restore waits for an existing run, rather than refusing or preparing outside its gate.
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var occupying = Task.Run(() => run.UpdatePairSettingsAsync(() => { entered.SetResult(); release.Wait(); }, default));
            await entered.Task;
            var queued = run.RestoreRecoveryPageAsync(fx.Pair.PairId, new RecoveryQuery(Id: record.RecoveryId), false, false, default);
            try { Require(!queued.IsCompleted, "restore queues behind the engine gate"); }
            finally { release.Set(); }
            await occupying;
            var result = (await queued).Single();
            Require(result.Classification == "RESTORED" && fx.WriterA.Puts == (effectWritten ? 0 : 1) &&
                store.CountUnresolvedIntents(fx.Pair.PairId) == 0,
                "crash reconciliation verifies an existing effect or retries the exact frozen absent destination once");
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, id, NoticeChoices.Ignore, default);
            Require(store.RecoveryRestoreProtected(fx.Pair.PairId, source.Path), "Ignore on a frozen intent retains protection");
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, id, NoticeChoices.RetryNow, default);
            Require(fx.WriterA.Puts == (effectWritten ? 0 : 1), "Retry after frozen Ignore never duplicates a verified write");
            // After Retry the notice still freezes the current executor attempt. An absent effect was retried
            // under a fresh identity; a written effect kept its reconciled one. Re-record that current action,
            // never the superseded original, or the fixture resurrects an orphan intent no run can reconcile.
            var frozenAction = store.NoticeById(fx.Pair.PairId, id)!.Value.Notice.ConflictOperation!
                .Actions.Single(SyncRun.IsRecoveryRestoreIntent);
            Require(effectWritten ? frozenAction.ActionId == action.ActionId : frozenAction.ActionId != action.ActionId,
                "an absent effect rotates its executor identity; a written effect keeps the reconciled one");
            fx.A.Seed(source.Path, "unrelated current bytes");
            store.RecordIntent(fx.Pair.PairId, frozenAction);
            var superseded = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId, new RecoveryQuery(Id: record.RecoveryId), false, false, default)).Single();
            Require(superseded.Classification == "CONFLICT" && !store.RecoveryRestoreProtected(fx.Pair.PairId, source.Path) &&
                store.CountUnresolvedIntents(fx.Pair.PairId) == 0 &&
                store.NoticeById(fx.Pair.PairId, id)!.Value.Notice.ConflictOperation!.Actions.Count == 0,
                "definitively superseded create intent releases the unrelated live entity and exposes copy/dismiss");
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, id, "dismiss", default);
            Require(store.NoticeById(fx.Pair.PairId, id) is { Closed: true }, "superseded frozen intent can be dismissed after reconciliation");
        }
    }

    private static async Task RecoveryLateOppositeChangeCheckAsync()
    {
        foreach (var readyBeforeCrash in new[] { false, true })
        {
        var root = LocalChecks.FreshRoot("recovery-late-opposite-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.DropboxHashes = fx.B.DropboxHashes = true;
        var bytes = Encoding.UTF8.GetBytes("retained bytes");
        var source = new ItemState("file.txt", ItemKind.File, "archive", "revision", DropboxContentHash.Compute(bytes),
            bytes.Length, DateTimeOffset.UtcNow.AddDays(-1), ItemFlags.None);
        var record = RetainRecoveryFixture(fx, source, "late-archive");
        fx.Provider.AddWriter(fx.Pair.A, new RecoveryFixtureWriter(fx.WriterA, source, bytes));
        var run = fx.Run();
        await run.RestoreRecoveryPageAsync(fx.Pair.PairId, new RecoveryQuery(Id: record.RecoveryId), false, false, default);
        if (readyBeforeCrash)
        {
            var pending = store.RecoveryRestoreNoticesPage(fx.Pair.PairId).Single();
            var verified = (await fx.A.ObserveAsync(source.Path, default))!;
            store.AddNotices([pending with { ReasonKey = SyncRun.RecoveryRestoreReady, Kind = NoticeKind.Info,
                ConflictOperation = pending.ConflictOperation! with
                { Actions = [pending.ConflictOperation!.Actions.Single() with { Source = verified }] } }]);
        }
        var other = fx.B.Seed(source.Path, "current opposite bytes");
        store.MarkFirstSyncDone(fx.Pair.PairId);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var notice = store.RecoveryRestoreNoticesPage(fx.Pair.PairId).Single();
        Require(store.RecoveryRestoreProtected(fx.Pair.PairId, source.Path) &&
            notice.Detail.Contains("Dropbox", StringComparison.Ordinal) &&
            notice.Detail.Contains("This computer", StringComparison.Ordinal) &&
            !notice.Detail.Contains("side A", StringComparison.Ordinal) &&
            !notice.Detail.Contains("side B", StringComparison.Ordinal) &&
            !notice.Detail.Contains("..", StringComparison.Ordinal) &&
            notice.Detail.Contains("Still held: rename the opposite file first.", StringComparison.Ordinal) &&
            notice.Detail.Contains("rename or move", StringComparison.Ordinal) &&
            (await fx.A.ObserveAsync(source.Path, default))!.Hash == source.Hash &&
            (await fx.B.ObserveAsync(source.Path, default))!.Hash == other.Hash && fx.WriterB.Puts == 0,
            "late opposite change preserves both versions and names the real providers in the manual resolution");
        // Retry before the user renames must report the remaining hold, not SKIP-PRESENT, which reads as success.
        var retryHeld = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.RetryNow, default);
        Require(!retryHeld.Contains("SKIP-PRESENT", StringComparison.Ordinal) &&
            retryHeld.Contains("different bytes", StringComparison.Ordinal) &&
            retryHeld.Contains("Still held: rename the opposite file first.", StringComparison.Ordinal) &&
            retryHeld.Contains("Dropbox", StringComparison.Ordinal) &&
            store.RecoveryRestoreProtected(fx.Pair.PairId, source.Path) &&
            (await fx.A.ObserveAsync(source.Path, default))!.Hash == source.Hash &&
            (await fx.B.ObserveAsync(source.Path, default))!.Hash == other.Hash,
            "Retry while the opposite file still differs reports the hold and changes no bytes");
        await fx.B.MoveAsync(new SyncAction("manual-rename", 0, ActionKind.Move, Side.B, "other-current.txt",
            source.Path, null, other, false, null), default);
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.RetryNow, default);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(!store.RecoveryRestoreProtected(fx.Pair.PairId, source.Path) &&
            (await fx.A.ObserveAsync(source.Path, default))!.Hash == source.Hash &&
            (await fx.B.ObserveAsync(source.Path, default))!.Hash == source.Hash &&
            (await fx.A.ObserveAsync("other-current.txt", default))!.Hash == other.Hash &&
            (await fx.B.ObserveAsync("other-current.txt", default))!.Hash == other.Hash,
            "renaming only the opposite version then Retry and convergence releases the hold with both byte versions preserved");
        }
    }

    // RQ-060/032: a frozen restore whose write never landed must not keep holding a user's live path. Reconcile
    // proves the effect absent; the receipt stays informational, keeps its selected destination, and a copy works.
    private static async Task RecoveryAbsentEffectCheckAsync()
    {
        {
            var root = LocalChecks.FreshRoot("recovery-absent-conflict-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            var bytes = Encoding.UTF8.GetBytes("retained bytes");
            var source = new ItemState("file.txt", ItemKind.File, "archive", "revision", DropboxContentHash.Compute(bytes),
                bytes.Length, DateTimeOffset.UtcNow.AddDays(-1), ItemFlags.None);
            var record = RetainRecoveryFixture(fx, source, "absent-archive");
            fx.Provider.AddWriter(fx.Pair.A, new RecoveryFixtureWriter(fx.WriterA, source, bytes));
            var run = fx.Run();
            fx.A.FailNextWrites = 1;
            var failed = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId,
                new RecoveryQuery(Id: record.RecoveryId), false, false, default)).Single();
            var attempts = fx.WriterA.Puts;
            Require(failed.Classification == "FAILED" && attempts >= 1 &&
                store.RecoveryRestoreProtected(fx.Pair.PairId, source.Path) &&
                store.CountUnresolvedIntents(fx.Pair.PairId) == 1,
                "a failed restore write leaves one frozen, unresolved attempt");
            fx.A.FailNextWrites = 0;
            fx.B.Seed(source.Path, "opposite current bytes");
            var second = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId,
                new RecoveryQuery(Id: record.RecoveryId), false, false, default)).Single();
            var notice = store.RecoveryRestoreNoticesPage(fx.Pair.PairId).Single();
            Require(second.Classification == "CONFLICT" &&
                !store.RecoveryRestoreProtected(fx.Pair.PairId, source.Path) &&
                store.CountUnresolvedIntents(fx.Pair.PairId) == 0 && fx.WriterA.Puts == attempts &&
                notice.Kind == NoticeKind.Info && notice.ConflictOperation?.Actions.Count == 0 &&
                notice.ConflictOperation?.Destination == source.Path &&
                NoticeChoices.Options(notice).Any(option => option.Choice == NoticeChoices.RestoreAsCopy),
                "an absent effect releases the hold, keeps the selected destination and offers Restore as a copy");
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.RestoreAsCopy, default);
            var restored = (await fx.A.FullAsync(0, _ => true, new Progress<string>(), default)).Items
                .Where(item => item.Kind == ItemKind.File && !PairPath.Same(item.Path, source.Path)).ToArray();
            Require(fx.WriterA.Puts == attempts + 1 && restored.Length == 1 && restored[0].Hash == source.Hash &&
                (await fx.B.ObserveAsync(source.Path, default))!.Hash != source.Hash,
                "Restore as a copy from the released notice creates the retained bytes without touching the opposite file");
        }
        {
            var clock = new RecoveryClock(DateTimeOffset.UtcNow);
            var root = LocalChecks.FreshRoot("recovery-absent-expired-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, clock);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            var bytes = Encoding.UTF8.GetBytes("retained bytes");
            var source = new ItemState("file.txt", ItemKind.File, "archive", "revision", DropboxContentHash.Compute(bytes),
                bytes.Length, DateTimeOffset.UtcNow.AddDays(-1), ItemFlags.None);
            var record = RetainRecoveryFixture(fx, source, "expired-archive");
            fx.Provider.AddWriter(fx.Pair.A, new RecoveryFixtureWriter(fx.WriterA, source, bytes));
            var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, clock);
            fx.A.FailNextWrites = 1;
            var failed = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId,
                new RecoveryQuery(Id: record.RecoveryId), false, false, default)).Single();
            var attempts = fx.WriterA.Puts;
            Require(failed.Classification == "FAILED" && attempts >= 1 &&
                store.RecoveryRestoreProtected(fx.Pair.PairId, source.Path) &&
                store.CountUnresolvedIntents(fx.Pair.PairId) == 1,
                "the expired fixture starts from one frozen, unresolved absent attempt");
            clock.Now += Settings.RecoveryRetention + TimeSpan.FromMinutes(1);
            var expired = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId,
                new RecoveryQuery(Id: record.RecoveryId), false, false, default)).Single();
            var notice = store.RecoveryRestoreNoticesPage(fx.Pair.PairId).Single();
            Require(expired.Classification == "NO-VERSION" &&
                !store.RecoveryRestoreProtected(fx.Pair.PairId, source.Path) &&
                store.CountUnresolvedIntents(fx.Pair.PairId) == 0 &&
                notice.Kind == NoticeKind.Info && notice.ConflictOperation?.Actions.Count == 0 &&
                notice.ConflictOperation?.Destination == source.Path && fx.WriterA.Puts == attempts,
                "an absent effect whose record expired releases the hold instead of protecting forever");
        }
        {
            // A failed copy attempt keeps its chosen copy destination through the downgrade. The clock advances a
            // day, so a fresh copy name would differ: Retry must reuse the notice's destination, not derive one.
            var clock = new RecoveryClock(DateTimeOffset.UtcNow);
            var root = LocalChecks.FreshRoot("recovery-absent-copy-retry-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, clock);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            var bytes = Encoding.UTF8.GetBytes("retained bytes");
            var source = new ItemState("file.txt", ItemKind.File, "archive", "revision", DropboxContentHash.Compute(bytes),
                bytes.Length, DateTimeOffset.UtcNow.AddDays(-1), ItemFlags.None);
            var record = RetainRecoveryFixture(fx, source, "copy-retry-archive");
            fx.Provider.AddWriter(fx.Pair.A, new RecoveryFixtureWriter(fx.WriterA, source, bytes));
            var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, clock);
            fx.A.FailNextWrites = 1;
            var copyFailed = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId,
                new RecoveryQuery(Id: record.RecoveryId), true, false, default)).Single();
            var frozenNotice = store.RecoveryRestoreNoticesPage(fx.Pair.PairId).Single();
            var copyPath = frozenNotice.ConflictOperation!.Destination!;
            var attempts = fx.WriterA.Puts;
            Require(copyFailed.Classification == "FAILED" && attempts >= 1 &&
                !PairPath.Same(copyPath, source.Path),
                "a failed copy attempt freezes its selected copy destination");
            fx.A.FailNextWrites = 0;
            fx.B.Seed(source.Path, "opposite current bytes");
            clock.Now += TimeSpan.FromDays(1);
            var retried = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId,
                frozenNotice.NoticeId!, NoticeChoices.RetryNow, default);
            Require(retried.StartsWith("RESTORED", StringComparison.Ordinal) &&
                fx.WriterA.Puts == attempts + 1 &&
                await fx.A.ObserveAsync(copyPath, default) is { Hash: var copied } && copied == source.Hash &&
                await fx.A.ObserveAsync(source.Path, default) is null &&
                (await fx.B.ObserveAsync(source.Path, default))!.Hash != source.Hash,
                "Retry after an absent copy effect restores to the destination the notice already selected");
        }
        {
            // The automatic refresh must keep the selected copy destination too: the clock advances a day, so a
            // freshly derived copy name would differ from the one the failed attempt froze.
            var clock = new RecoveryClock(DateTimeOffset.UtcNow);
            var root = LocalChecks.FreshRoot("recovery-absent-auto-copy-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, clock);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            var bytes = Encoding.UTF8.GetBytes("retained bytes");
            var source = new ItemState("file.txt", ItemKind.File, "archive", "revision", DropboxContentHash.Compute(bytes),
                bytes.Length, DateTimeOffset.UtcNow.AddDays(-1), ItemFlags.None);
            var record = RetainRecoveryFixture(fx, source, "auto-copy-retry-archive");
            fx.Provider.AddWriter(fx.Pair.A, new RecoveryFixtureWriter(fx.WriterA, source, bytes));
            var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, clock);
            fx.A.FailNextWrites = 1;
            var copyFailed = (await run.RestoreRecoveryPageAsync(fx.Pair.PairId,
                new RecoveryQuery(Id: record.RecoveryId), true, false, default)).Single();
            var copyPath = store.RecoveryRestoreNoticesPage(fx.Pair.PairId).Single().ConflictOperation!.Destination!;
            Require(copyFailed.Classification == "FAILED" && !PairPath.Same(copyPath, source.Path),
                "the automatic-retry fixture starts from a frozen failed copy at the selected destination");
            fx.A.FailNextWrites = 0;
            fx.B.Seed(source.Path, "opposite current bytes");
            clock.Now += TimeSpan.FromDays(1);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            var restored = (await fx.A.FullAsync(0, _ => true, new Progress<string>(), default)).Items
                .Where(item => item.Kind == ItemKind.File && item.Hash == source.Hash).ToArray();
            Require(restored.Length == 1 && PairPath.Same(restored[0].Path, copyPath) &&
                (await fx.B.ObserveAsync(source.Path, default))!.Hash != source.Hash,
                "automatic retry restores to the notice's selected copy destination, not a freshly named copy");
        }
    }

    // A provider may echo one content hash in a different letter case. The post-write opposite check must
    // compare case-insensitively; otherwise identical bytes raise a false hold with a rename instruction.
    private static async Task RecoveryHashCasingCheckAsync()
    {
        var root = LocalChecks.FreshRoot("recovery-hash-case-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.DropboxHashes = fx.B.DropboxHashes = true;
        var bytes = Encoding.UTF8.GetBytes("retained bytes");
        var source = new ItemState("file.txt", ItemKind.File, "archive", "revision", DropboxContentHash.Compute(bytes),
            bytes.Length, DateTimeOffset.UtcNow.AddDays(-1), ItemFlags.None);
        var record = RetainRecoveryFixture(fx, source, "case-archive");
        fx.Provider.AddWriter(fx.Pair.A, new RecoveryFixtureWriter(fx.WriterA, source, bytes));
        fx.Provider.AddWriter(fx.Pair.B, new HashCaseWriter(fx.WriterB, source.Path));
        var run = fx.Run();
        await run.RestoreRecoveryPageAsync(fx.Pair.PairId, new RecoveryQuery(Id: record.RecoveryId), false, false, default);
        fx.B.Seed(source.Path, "retained bytes");
        store.MarkFirstSyncDone(fx.Pair.PairId);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var notice = store.RecoveryRestoreNoticesPage(fx.Pair.PairId).Single();
        Require(!notice.Detail.Contains("different bytes", StringComparison.Ordinal) && fx.WriterB.Puts == 0,
            "a hash that differs only by letter case is the same bytes, not a late opposite change");
    }

    private static void RecoveryDeletionEvidenceAttributionCheck()
    {
        foreach (var restoredSide in new[] { Side.A, Side.B })
        {
            var root = LocalChecks.FreshRoot("recovery-tombstone-" + Guid.NewGuid().ToString("N"));
            using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            var source = new ItemState("restored.txt", ItemKind.File, "verified-restored-object", "restored-revision",
                DropboxContentHash.Compute(Encoding.UTF8.GetBytes("original")), 8, DateTimeOffset.UnixEpoch, ItemFlags.None);
            var action = new SyncAction(ShortId.Make("rrs", fx.Pair.PairId, "old-recovery"), 0, ActionKind.CopyNew,
                restoredSide, source.Path, null, source, null, false, null) { SuppressReconciledBaseline = true };
            store.AddNotices([new Notice(fx.Pair.PairId, 0, NoticeKind.Info, source.Path, "verified", SyncRun.RecoveryRestoreReady,
                ConflictOperation: new ConflictNoticeOperation("old-recovery", source.Path, [action], []))]);
            // Both sides were deleted after the archived mtime, with exactly the same bytes. Only one was restored.
            var row = new PlanRow(null, restoredSide == Side.A ? source : null, restoredSide == Side.B ? source : null,
                new Evidence(true, true, true, true, source.Mtime.AddDays(1), source.Mtime.AddDays(1), source.Hash, source.Hash));
            foreach (var firstSync in new[] { false, true })
            {
                var protectedRows = InterruptedDeleteRepair.Protect(new[] { row }, store, fx.Pair.PairId);
                var plan = Planner.Decide(fx.Pair.PairId, 1, protectedRows,
                    new(TimeSpan.Zero, firstSync, new("{name} [{side}]{ext}", "s", TimeSpan.Zero, "A", "B"), 10));
                Require(plan.Actions.All(item => item.Kind is not (ActionKind.Delete or ActionKind.Hold)) &&
                    plan.Actions.Any(item => item.Kind == ActionKind.CopyNew && item.Target != restoredSide),
                    "explicit old-mtime recovery overrides old identical tombstone only for its verified restored entity");
            }
            var unrelated = source with { ObjectId = "replacement-object" };
            var changedRow = row with { A = restoredSide == Side.A ? unrelated : null, B = restoredSide == Side.B ? unrelated : null };
            var unchangedEvidence = InterruptedDeleteRepair.Protect(new[] { changedRow }, store, fx.Pair.PairId)[0];
            Require(unchangedEvidence.Ev == changedRow.Ev, "restore attribution cannot bless an unrelated object at the same path");
            var scope = new RunScope.Scope(restoredSide == Side.A ? new[] { source } : [],
                restoredSide == Side.B ? new[] { source } : [], []);
            Require(InterruptedDeleteRepair.SourcePaths(scope, store, fx.Pair.PairId).Contains(source.Path),
                "first-sync deletion-history probes skip identity-bound explicit Recovery repairs");
        }
    }

    private static void RecoveryPlanningSnapshotCheck()
    {
        var root = LocalChecks.FreshRoot("recovery-snapshot-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var pending = new Notice(fx.Pair.PairId, 0, NoticeKind.UserDecision, "Folder/file.txt", "pending",
            SyncRun.RecoveryRestorePending, ConflictOperation: new ConflictNoticeOperation("snapshot-recovery", null,
                [new SyncAction("rrs-snapshot", 0, ActionKind.CopyNew, Side.A, "Folder/file.txt", null, null, null, false, null)
                    { SuppressReconciledBaseline = true }], []));
        store.AddNotices([pending]);
        using (var snapshot = store.OpenPlanningProtection(fx.Pair))
        {
            Require(snapshot.RecoveryRestoreProtected(fx.Pair.PairId, "folder/file.txt", false) &&
                snapshot.RecoveryRestoreProtected(fx.Pair.PairId, "Folder", true) &&
                !snapshot.RecoveryRestoreProtected(fx.Pair.PairId, "Folder-other", true),
                "snapshot restore guard uses indexed self/ancestor/descendant paths with separator boundaries");
            store.AddNotices([pending with { ReasonKey = SyncRun.RecoveryRestoreReady, Kind = NoticeKind.Info }]);
            Require(snapshot.RecoveryRestoreProtected(fx.Pair.PairId, "Folder/file.txt", false) &&
                !store.RecoveryRestoreProtected(fx.Pair.PairId, "Folder/file.txt"),
                "both sides of one planning join keep the same protection despite a live ready transition");
        }
        using var fresh = store.OpenPlanningProtection(fx.Pair);
        Require(!fresh.RecoveryRestoreProtected(fx.Pair.PairId, "Folder/file.txt", false),
            "a later planning snapshot releases verified ready restores");
    }

    // Mutable clock: the fixture creates its Recovery record at T0, then lets the record expire before the retry.
    private sealed class RecoveryClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // A provider may echo the same content hash in different letter case. Every observation of the path
    // uppercases it, so the post-write opposite check is the comparison under test.
    private sealed class HashCaseWriter(IEndpointWriter inner, string path) : IEndpointWriter
    {
        public async Task<ItemState?> ObserveAsync(string observed, CancellationToken ct)
        {
            var state = await inner.ObserveAsync(observed, ct);
            if (state is null || state.Hash is not { } hash || !PairPath.Same(observed, path)) return state;
            return state with { Hash = hash.ToUpperInvariant() };
        }

        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct) =>
            inner.PutAsync(action, content, ct);
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => inner.MoveAsync(action, ct);
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => inner.DeleteAsync(action, ct);
        public Task<ItemState> MkDirAsync(string target, CancellationToken ct) => inner.MkDirAsync(target, ct);
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) =>
            inner.PreserveAsync(victim, actionId, ct);
        public Task<Stream> OpenReadAsync(ItemState state, CancellationToken ct) => inner.OpenReadAsync(state, ct);
    }

    private sealed class RecoveryFixtureProvider(IEndpointProvider inner) : IEndpointProvider
    {
        public int WritePreparations { get; private set; }
        public int ReadPreparations { get; private set; }
        public IInventorySource Inventory(RootDef root) => inner.Inventory(root);
        public IEndpointWriter Writer(RootDef root) => inner.Writer(root);
        public Task PrepareAsync(PairDef pair, CancellationToken ct) { WritePreparations++; return Task.CompletedTask; }
        public Task PrepareRecoveryReadAsync(PairDef pair, CancellationToken ct) { ReadPreparations++; return Task.CompletedTask; }
    }

    private sealed class RecoveryFixtureWriter(IEndpointWriter inner, ItemState source, byte[] bytes) : IEndpointWriter, IRecoveryRecordReader
    {
        public bool FailOpen { get; set; }
        public bool Missing { get; init; }
        public int Opens { get; private set; }
        public Task<ItemState> DescribeRecoveryAsync(RecoveryRecord record, CancellationToken ct) => Missing
            ? throw new FileNotFoundException("fixture unavailable") : Task.FromResult(source with { Path = record.Path });
        public Task<Stream> OpenRecoveryRecordAsync(RecoveryRecord record, CancellationToken ct)
        {
            Opens++;
            if (FailOpen && record.Path == "file.txt") throw new IOException("fixture read failure");
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => inner.ObserveAsync(path, ct);
        public Task<Stream> OpenReadAsync(ItemState state, CancellationToken ct) => inner.OpenReadAsync(state, ct);
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct)
        {
            if (SyncRun.IsRecoveryRestoreIntent(action))
                Require(action.Kind == ActionKind.CopyNew && action.ExpectedTarget is null && action.SuppressReconciledBaseline,
                    "restore effect is create-only and cannot claim an archived opposite-side baseline");
            return inner.PutAsync(action, content, ct);
        }
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => inner.MoveAsync(action, ct);
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => inner.DeleteAsync(action, ct);
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => inner.MkDirAsync(path, ct);
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) => inner.PreserveAsync(victim, actionId, ct);
    }
}
