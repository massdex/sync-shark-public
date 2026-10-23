namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task FirstSyncCompletionChecksAsync()
    {
        Require(SyncProviders.DisplayName(SyncProviders.Local) == "This computer" &&
            SyncEndpointProviders.Get(SyncEndpointKind.Local).ProviderId is null,
            "local reading label is accurate without changing cloud-provider identity semantics");
        await Reconcile_Uncertain_StopsBeforeInventoryAsync();
        await Completion_PostPreviewDeleteAsync();
        await Completion_RetryAddedAfterPlanningAsync();
        await Completion_UnreviewedEvidenceCursorAsync();
        await Completion_ReleasedEvidenceAsync();
        await Completion_ApplyPreservesDecisionAsync();
        await ReleasedSurvivorChecksAsync();
        await Completion_FailedItemAsync();
        await Completion_ReviewedWinnerRetryAsync();
        await RunCliChecks.Cli_Completion_NoExclusionsWhenRemovalFailsAsync();
        await Completion_StuckNoticeLifecycleAsync();
        await Completion_ThrowingProgressAsync();
        await Completion_EvidenceFailureAsync();
        await Completion_TerminalRetriesAsync();
        await Completion_MovedRetryAsync();
        Completion_RetrySchema();
        Completion_SharedFailurePolicy();
        Completion_DeliveryPreservesPayload();
        await Completion_IgnoreColdReopenAsync();
        await Completion_UnknownDeleteReplansAsync();
        await Completion_IncompleteApplyRefusedAsync();
        await Completion_IncompleteSubtreeAsync();
        await Completion_UnknownHashFailureAsync();
        await Completion_PostPassFailureAsync();
        await Completion_NoticeWriteFailureAsync();
        await Completion_RecoveryUnavailableAsync();
        await Completion_DeliberatelySkippedLinkAsync();
        await Completion_RecoveryDoesNotApproveHeldAsync();
        await Completion_ExcludeNarrowsPrepareAsync();
        await RemovalReviewChecksAsync();
    }

    private static async Task Completion_ExcludeNarrowsPrepareAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "retry-narrow-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("skip.txt", "leave local");
        fx.A.Seed("good.txt", "copy normally");
        var runtime = new V2CliRuntime(null, store, fx.Run(), new EndpointRootProbe(fx.Provider), root);
        await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, default);
        store.SaveSyncRetry(fx.Pair.PairId, new("skip.txt", "sync", 3, "unavailable", DateTimeOffset.MinValue));
        store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.Info, "skip.txt", "retry", "sync-retry")]);
        var notice = store.OpenNotices(fx.Pair.PairId).Single(item => item.ReasonKey == "sync-retry");
        // cloud-files.md/E9: only the separate explicit Stop syncing choice authorizes exclusion.
        Require(NoticeOptions.Options(notice).Any(option => option.Choice == NoticeChoices.Exclude),
            "retry notice offers explicit Stop syncing independently of Ignore");
        await runtime.ResolveConflictAsync(fx.Pair.PairId, notice.Path, NoticeChoices.Exclude, notice.NoticeId, 0, null, default);
        var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, null, true, null, _ => { }, default);
        Require(result.FirstSyncComplete && await fx.B.ObserveAsync("good.txt", default) is not null &&
            await fx.B.ObserveAsync("skip.txt", default) is null && await fx.A.ObserveAsync("skip.txt", default) is not null &&
            fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
            "retry exclusion narrows the retained prepare through normal authority without deleting either side");
        Console.WriteLine("PASS Completion_ExcludeNarrowsPrepare");
    }

    private static async Task Completion_RecoveryDoesNotApproveHeldAsync()
    {
        var fx = Build();
        fx.A.Seed("unapproved.txt", "must survive");
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        plan = plan with { Actions = [], HeldDeletions = [new("unapproved.txt", Side.A)] };
        fx.WriterA.RecoveryUnavailable = "missing USB";
        await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default, new HashSet<string>());
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "unapproved.txt")?.Held is null,
            "Recovery outage never grants held-deletion authority absent from review");
        fx.WriterA.RecoveryUnavailable = null;
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 && await fx.A.ObserveAsync("unapproved.txt", default) is not null,
            "restoring Recovery cannot authorize an unreviewed first-sync deletion");
    }

    private static async Task Completion_DeliberatelySkippedLinkAsync()
    {
        var fx = Build();
        fx.A.Seed("junction", "", ItemKind.Folder);
        fx.B.Seed("junction/cloud.txt", "protected opposite subtree");
        fx.A.Seed("ordinary.txt", "copy");
        fx.Provider.AddInventory(fx.Pair.A, new ScriptedInventory(fx.A) { LinkPath = "junction" });
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default, ReviewedHeld(plan));
        Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && fx.Store.Cursor(fx.Pair.A.RootId) is not null &&
            fx.Store.SyncRetryAt(fx.Pair.PairId, "junction") is null && await fx.B.ObserveAsync("ordinary.txt", default) is not null &&
            await fx.A.ObserveAsync("junction/cloud.txt", default) is null && fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
            "deliberately unfollowed junction protects both sides without retry failure or blocking cursor/first-sync completion");
    }

    private static async Task Completion_RecoveryUnavailableAsync()
    {
        var fx = Build();
        var newer = fx.A.Seed("replace.txt", "new", mtime: DateTimeOffset.UtcNow);
        fx.B.Seed("replace.txt", "old", mtime: DateTimeOffset.UtcNow.AddDays(-1));
        fx.A.Seed("new.txt", "copy while USB unplugged");
        fx.WriterA.RecoveryUnavailable = "F:/Recovery (removable drive disconnected)";
        var recoveryClock = new CompletionClock();
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, Settings, recoveryClock);
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default, ReviewedHeld(plan));
        Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && await fx.B.ObserveAsync("new.txt", default) is not null &&
            Content.SameContent(newer, (await fx.B.ObserveAsync("replace.txt", default))!) == false &&
            fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 && fx.WriterB.Preserves == 0 &&
            fx.Store.SyncRetryAt(fx.Pair.PairId, "replace.txt") is not null,
            "missing Recovery permits new copies but holds replacements durably without destructive effects");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Count(notice => notice.ReasonKey == "recovery-unavailable") == 1,
            "Recovery outage has one pair-level notice");
        var recoveryNotice = fx.Store.OpenNotices(fx.Pair.PairId).Single(notice => notice.ReasonKey == "recovery-unavailable");
        Require(NoticeOptions.Options(recoveryNotice).Any(option => option.Choice == NoticeChoices.Ignore),
            "Recovery summary offers Ignore without dropping protection");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, recoveryNotice.NoticeId!, NoticeChoices.Ignore, default);
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "replace.txt") is not null && fx.Store.Pair(fx.Pair.PairId)!.Excluded.Count == 0 &&
            !fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.ReasonKey == "recovery-unavailable"),
            "ignoring Recovery silences its notice without discarding debt or excluding the root");
        recoveryClock.Now += TimeSpan.FromMinutes(6);
        fx.WriterA.RecoveryUnavailable = null;
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(Content.SameContent(newer, (await fx.B.ObserveAsync("replace.txt", default))!) == true &&
            fx.WriterB.Preserves > 0 && !fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.ReasonKey == "recovery-unavailable"),
            "restored Recovery retries replacement with preservation and auto-closes notice");
    }

    private static async Task Completion_NoticeWriteFailureAsync()
    {
        var store = new RecordingStore(new InMemoryStateStore());
        var fx = Build(suppliedStore: store);
        fx.A.Seed("notice.txt", "held until notice records");
        fx.A.Seed("sibling.txt", "independent");
        var deliveryClock = new CompletionClock();
        var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, deliveryClock);
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        plan = plan with { Notices = [new(fx.Pair.PairId, 40, NoticeKind.Info, "notice.txt", "Injected issue: retry or ignore.", "sync-retry")] };
        store.FailNoticePath = "notice.txt";
        await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default);
        Require(store.FirstSyncDone(fx.Pair.PairId) && store.SyncRetryAt(fx.Pair.PairId, "notice.txt")?.PendingNotice is not null &&
            await fx.B.ObserveAsync("notice.txt", default) is null && await fx.B.ObserveAsync("sibling.txt", default) is not null &&
            fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
            "failed notice write is durable retry input and holds only its scope before execution");
        var pendingDelivery = store.SyncRetryAt(fx.Pair.PairId, "notice.txt")!;
        Require(pendingDelivery.PendingNotice?.DeliveryRetry is { Attempts: 1 },
            "notice delivery owns one nested attempt independent of file retries");
        store.FailNoticePath = null;
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(store.SyncRetryAt(fx.Pair.PairId, "notice.txt")?.PendingNotice?.DeliveryRetry == pendingDelivery.PendingNotice!.DeliveryRetry &&
            await fx.B.ObserveAsync("notice.txt", default) is null,
            "before the nested delivery deadline no notice attempt or protected copy runs");
        deliveryClock.Now = pendingDelivery.PendingNotice.DeliveryRetry!.Value.RetryAfter.AddMinutes(6);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(await fx.B.ObserveAsync("notice.txt", default) is not null &&
            store.SyncRetryAt(fx.Pair.PairId, "notice.txt") is null &&
            !store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "notice.txt"),
            "notice replay succeeds once, scoped copy completes and issue auto-closes");
    }

    private static async Task Completion_PostPassFailureAsync()
    {
        var fx = Build();
        fx.A.Seed("safe.txt", "new independent file");
        fx.A.Seed("replace.txt", "new source", mtime: DateTimeOffset.UtcNow);
        fx.B.Seed("replace.txt", "occupied target", mtime: DateTimeOffset.UtcNow.AddDays(-1));
        var planningClock = new CompletionClock();
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, Settings, planningClock);
        Planner.PostPassFault.Value = () => throw new InvalidOperationException("injected post-pass failure");
        try
        {
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(plan.Actions.All(action => action.Kind == ActionKind.CopyNew && action.ExpectedTarget is null),
                "degraded post-pass allows only independent non-overwriting file copies");
            await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default, ReviewedHeld(plan));
            Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && await fx.B.ObserveAsync("safe.txt", default) is not null &&
                fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 &&
                fx.Store.SyncRetryAt(fx.Pair.PairId, "replace.txt")?.Kind == "planning",
                "degraded planning completes first sync and retains occupied targets without effects");
            Require(fx.Store.OpenNotices(fx.Pair.PairId).Count(notice => notice.ReasonKey == "planning-degraded") == 1,
                "post-pass failure emits exactly one retryable planning notice");
            var notice = fx.Store.OpenNotices(fx.Pair.PairId).Single(item => item.ReasonKey == "planning-degraded");
            Require(NoticeOptions.Options(notice).Any(option => option.Choice == NoticeChoices.Ignore),
                "planning summary offers Ignore without excluding the root");
            await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.Ignore, default);
            Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "replace.txt")?.Kind == "planning" &&
                fx.Store.Pair(fx.Pair.PairId)!.Excluded.Count == 0 &&
                !fx.Store.OpenNotices(fx.Pair.PairId).Any(item => item.ReasonKey == "planning-degraded"),
                "planning Ignore closes only the notice and preserves pending work");
        }
        finally { Planner.PostPassFault.Value = null; }
        planningClock.Now += TimeSpan.FromMinutes(6); // Shared retry policy: automatic work waits until due.
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "replace.txt") is null &&
            !fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.ReasonKey == "planning-degraded"),
            "successful next-run planning retires held scope and its single notice");
    }

    private static async Task Completion_UnknownHashFailureAsync()
    {
        var fx = Build();
        fx.A.Seed("good.txt", "sibling");
        fx.A.Seed("bad.txt", "retry me");
        var inventory = new ScriptedInventory(fx.A) { Hashless = true, ThrowHashPath = "bad.txt" };
        fx.Provider.AddInventory(fx.Pair.A, inventory);
        var clock = new CompletionClock();
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, Settings, clock);
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default, ReviewedHeld(plan));
        Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && await fx.B.ObserveAsync("good.txt", default) is not null &&
            await fx.B.ObserveAsync("bad.txt", default) is null && fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
            "unknown hash exception leaves only its item unfinished and first sync completes");
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "bad.txt") is not null &&
            fx.Store.OpenNotices(fx.Pair.PairId).Count(notice => notice.Path == "bad.txt") == 1,
            "unknown hash failure has one durable retry and exactly one notice");
        inventory.ThrowHashPath = null;
        // docs/design/retry-resume-rewind.md: automatic work waits for the exact persisted deadline.
        var sleeping = fx.Store.SyncRetryAt(fx.Pair.PairId, "bad.txt")!;
        var putsA = fx.WriterA.Puts;
        var putsB = fx.WriterB.Puts;
        clock.Now = sleeping.RetryAfter.AddTicks(-1);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "bad.txt") == sleeping &&
            await fx.B.ObserveAsync("bad.txt", default) is null &&
            fx.WriterA.Puts == putsA && fx.WriterB.Puts == putsB &&
            fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
            "unknown hash debt stays unchanged with no effects before its exact deadline");
        clock.Now = sleeping.RetryAfter;
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(await fx.B.ObserveAsync("bad.txt", default) is not null &&
            fx.Store.SyncRetryAt(fx.Pair.PairId, "bad.txt") is null &&
            !fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "bad.txt"),
            "unknown hash failure retries and auto-closes after success");
    }

    private static async Task Completion_IncompleteSubtreeAsync()
    {
        var fx = Build();
        fx.A.Seed("good.txt", "copy this sibling");
        fx.A.Seed("locked/local.txt", "unreadable local");
        fx.B.Seed("locked/cloud.txt", "do not infer local absence");
        var inventory = new ScriptedInventory(fx.A) { Unreadable = ["locked"] };
        fx.Provider.AddInventory(fx.Pair.A, inventory);
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(!plan.Actions.Any(action => PairPath.IsSelfOrUnder(action.Path, "locked")),
            "unreadable subtree has no executable effects on either endpoint");
        await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default, ReviewedHeld(plan));
        Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && await fx.B.ObserveAsync("good.txt", default) is not null,
            "healthy sibling applies and first sync completes despite scoped scan failure");
        Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 &&
            await fx.A.ObserveAsync("locked/cloud.txt", default) is null &&
            await fx.B.ObserveAsync("locked/local.txt", default) is null,
            "unreadable scope remains unsynced and neither side is deleted");
        Require(fx.Store.Cursor(fx.Pair.A.RootId) is not null && fx.Store.SyncRetryAt(fx.Pair.PairId, "locked")?.Kind == "scan",
            "read debt excludes the subtree while the healthy inventory cursor is promoted");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var notices = fx.Store.OpenNotices(fx.Pair.PairId).Where(notice => notice.Path == "locked" && notice.ReasonKey == "sync-retry").ToArray();
        Require(notices.Length == 1 && NoticeChoices.IsRetryNotice(notices[0]), "repeated scan failure retains exactly one actionable retry notice");
        inventory.Unreadable = [];
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notices[0].NoticeId!, NoticeChoices.RetryNow, default);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "locked") is null &&
            !fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "locked" && notice.ReasonKey == "sync-retry"),
            "successful reread retires scan debt and closes its notice");
        Console.WriteLine("PASS Completion_IncompleteSubtree");
    }

    private static async Task Completion_IncompleteApplyRefusedAsync()
    {
        var fx = Build();
        fx.A.Seed("survivor.txt", "unchanged");
        fx.B.Seed("survivor.txt", "unchanged");
        var run = fx.Run();
        var preview = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        var matches = preview.MatchedBothSides.ToDictionary(item => item.Path,
            item => new PrepareMatch(Content.SideStamp(item.A), Content.SideStamp(item.B)));
        fx.Provider.AddInventory(fx.Pair.A, new ScriptedInventory(fx.A) { Incomplete = true });
        var unavailable = await run.PreviewFirstSyncApplyResolvedAsync(fx.Pair.PairId,
            null, null, [], matches, new RecordingProgress(), default);
        Require(unavailable.Plan.RunUnavailable is not null, "whole-root outage returns a clean unavailable plan");
        Require(!fx.Store.FirstSyncDone(fx.Pair.PairId) && fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 &&
            fx.WriterA.Puts == 0 && fx.WriterB.Puts == 0, "incomplete apply-side scan refuses before any effect");
        Console.WriteLine("PASS Completion_IncompleteApplyRefused");
    }

    private static async Task Completion_ReleasedEvidenceAsync()
    {
        foreach (var choice in new[] { NoticeChoices.DeleteReviewed, NoticeChoices.KeepRestore, NoticeChoices.Ignore })
        {
        var fx = Build();
        var local = fx.A.Seed("released.txt", "survivor", mtime: DateTimeOffset.UtcNow.AddDays(-2));
        fx.Store.MarkFirstSyncDone(fx.Pair.PairId);
        fx.Store.StoreDeletionEvidence(fx.Pair.PairId, fx.Pair.B.RootId,
            [new DeletionEvidenceRecord(local.Path, null, Failure: new(3, DateTimeOffset.UtcNow, 1, true))]);
        fx.Store.SaveSyncRetry(fx.Pair.PairId, new(local.Path, "deletion-evidence", 3, "history unavailable",
            DateTimeOffset.MinValue, Held: new HeldDeletion(local.Path, Side.A)));
        fx.Provider.AddInventory(fx.Pair.B, new RecoveryEvidenceInventory(fx.B)
            { Deleted = DateTimeOffset.UtcNow.AddDays(-1), ContentHash = local.Hash });
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 && await fx.A.ObserveAsync(local.Path, default) == local,
            "released Dropbox evidence never authorizes an unreviewed delete");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == local.Path && notice.ReasonKey == "deletion-review"),
            "released evidence produces a decision notice instead of a delete");
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "released decision holds both directions across ordinary runs");
        var notice = fx.Store.OpenNotices(fx.Pair.PairId).Single(item => item.ReasonKey == "deletion-review");
        await fx.Run().ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notice.NoticeId!, choice, default);
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.WriterA.Deletes == (choice == NoticeChoices.DeleteReviewed ? 1 : 0) &&
            fx.WriterA.Preserves == (choice == NoticeChoices.DeleteReviewed ? 1 : 0) && fx.WriterB.Deletes == 0 &&
            fx.WriterB.Puts == (choice == NoticeChoices.KeepRestore ? 1 : 0) && fx.WriterA.Puts == 0,
            "each released-evidence choice has exactly its own recovery-delete, upload or ignore effects");
        Console.WriteLine("PASS Completion_ReleasedEvidence choice=" + choice);
        }
        var recreated = Build();
        recreated.A.Seed("recreated.txt", "current content");
        recreated.B.Seed("recreated.txt", "current content");
        recreated.Store.MarkFirstSyncDone(recreated.Pair.PairId);
        recreated.Store.SaveSyncRetry(recreated.Pair.PairId, new("recreated.txt", "deletion-evidence", 1, "old history", DateTimeOffset.MinValue,
            Held: new HeldDeletion("recreated.txt", Side.A)));
        recreated.Provider.AddInventory(recreated.Pair.B, new RecoveryEvidenceInventory(recreated.B));
        await recreated.Run().IncrementalAsync(recreated.Pair.PairId, new RecordingProgress(), default);
        await recreated.Run().IncrementalAsync(recreated.Pair.PairId, new RecordingProgress(), default);
        Require(recreated.Store.SyncRetryAt(recreated.Pair.PairId, "recreated.txt") is null && recreated.WriterA.Deletes == 0 && recreated.WriterB.Deletes == 0 &&
            recreated.Store.Baselines(recreated.Pair.PairId).Any(), "recreated remote retires obsolete evidence and converges through ordinary sync without delete");
    }

    private static void Completion_RetrySchema()
    {
        foreach (var version in new[] { 11, 12 })
        {
            var root = Path.Combine(Path.GetTempPath(), "retry-schema-" + Guid.NewGuid().ToString("N"));
            string database;
            using (var store = SqliteStateStore.Open(root, Settings, TimeProvider.System))
            {
                var fx = Build(suppliedStore: store);
                store.MarkFirstSyncDone(fx.Pair.PairId);
                database = Directory.GetFiles(root, "state.db", SearchOption.AllDirectories).Single();
            }
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False"))
            {
                connection.Open();
                StoreSchema.Execute(connection, null, $"DROP TABLE sync_retry; PRAGMA user_version={version};");
            }
            using var reopened = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            Require(reopened.CountSyncRetries("p1") == 0, "v11 migration and v12 presence repair both create retry schema");
        }
        Console.WriteLine("PASS Completion_RetrySchema");
    }

    private static async Task Completion_EvidenceFailureAsync()
    {
        var fx = Build();
        fx.A.Seed("history.txt", "survivor");
        fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B) { Fail = true });
        var run = fx.Run();
        var preview = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        await run.ApplyAsync(fx.Pair.PairId, preview, new RecordingProgress(), default, ReviewedHeld(preview));
        Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && fx.Store.SyncRetryAt(fx.Pair.PairId, "history.txt") is not null,
            "per-path history failure while listing works completes with durable retry input");
        Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 && await fx.A.ObserveAsync("history.txt", default) is not null,
            "unavailable history never deletes the survivor");
        Console.WriteLine("PASS Completion_EvidenceFailure");
    }

    private static async Task Completion_MovedRetryAsync()
    {
        var fx = Build();
        fx.A.Seed("old.txt", "same item");
        fx.B.Seed("old.txt", "same item");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        fx.Store.SaveSyncRetry(fx.Pair.PairId, new("old.txt", "sync", 2, "old failure", DateTimeOffset.MinValue));
        await fx.A.MoveAsync(new SyncAction("rename", 9, ActionKind.Move, Side.A, "new.txt", "old.txt", null, null, false, null), default);
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(await fx.B.ObserveAsync("new.txt", default) is not null && await fx.B.ObserveAsync("old.txt", default) is null,
            "ordinary sync follows the moved item's identity");
        Require(report.Deferred == 0 && !fx.Store.ReadSyncRetries(fx.Pair.PairId).Any() &&
            fx.Store.OpenNotices(fx.Pair.PairId).All(notice => notice.ReasonKey != "sync-retry"),
            "the old-path retry retires after identity-following move without a false stuck notice");
        Console.WriteLine("PASS Completion_MovedRetry");
    }

    private static async Task Completion_TerminalRetriesAsync()
    {
        var fx = Build();
        fx.Store.MarkFirstSyncDone(fx.Pair.PairId);
        fx.Store.SavePair(fx.Pair with { Excluded = ["excluded.txt"], IgnoreRules = ["FileName:ignored.txt"] });
        fx.A.Seed("excluded.txt", "stay local");
        fx.A.Seed("ignored.txt", "stay ignored");
        fx.A.Seed("decision.txt", "held for user");
        fx.Store.AddNotices([new Notice(fx.Pair.PairId, 30, NoticeKind.UserDecision, "decision.txt", "Choose a side", "tie")]);
        foreach (var path in new[] { "absent.txt", "excluded.txt", "ignored.txt", "decision.txt" })
            fx.Store.SaveSyncRetry(fx.Pair.PairId, new(path, "sync", 2, "old failure", DateTimeOffset.MinValue));
        var report = await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(report.Deferred == 0 && !fx.Store.ReadSyncRetries(fx.Pair.PairId).Any(),
            "both-absent and excluded retries retire instead of keeping AutoSync busy");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).All(notice => notice.ReasonKey != "sync-retry") && fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
            "terminal paths create no false retries or deletes");
        Console.WriteLine("PASS Completion_TerminalRetries");
    }

    private sealed class ThrowingCompletionProgress : IProgress<RunProgress>
    {
        public int Calls;
        public void Report(RunProgress value)
        {
            Calls++;
            throw new InvalidOperationException("scripted display failure");
        }
    }

    private static async Task Completion_ThrowingProgressAsync()
    {
        var sink = new ThrowingCompletionProgress();
        var clock = new CompletionClock();
        var throttle = new ProgressThrottle(sink, clock, TimeSpan.Zero);
        throttle.Report("Inventory", null, 0, 2);
        clock.Now += TimeSpan.FromSeconds(12);
        throttle.Report("Inventory", "a.txt", 1, 2);
        throttle.Report("Applying", null, 0, 1);
        throttle.Flush();
        Require(sink.Calls >= 6, "all throttle report branches invoke and isolate the throwing sink");
        var fx = Build();
        fx.A.Seed("good.txt", "survives display failures");
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, sink, CancellationToken.None);
        await run.ApplyAsync(fx.Pair.PairId, plan, sink, CancellationToken.None);
        Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && await fx.B.ObserveAsync("good.txt", CancellationToken.None) is not null,
            "a throwing run display does not change first-sync completion or copy results");
        Console.WriteLine("PASS Completion_ThrowingProgress");
    }

    private sealed class CompletionClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task Completion_StuckNoticeLifecycleAsync()
    {
        var clock = new CompletionClock();
        var root = Path.Combine(Path.GetTempPath(), "fscomplete-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(root, Settings, clock);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        fx.WriterB.OnRead = path => { if (path == "stuck.txt") throw new IOException("unreadable target"); };
        store.RecordIntent(fx.Pair.PairId, new SyncAction("stuck", 2, ActionKind.CopyNew, Side.B, "stuck.txt", null,
            fx.A.Seed("stuck.txt", "retained source"), null, false, "stuck-key"));
        var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, clock);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(store.SyncRetryAt(fx.Pair.PairId, "stuck.txt")?.Attempts == attempt,
                "durable attempt counter counts separate due runs");
            Require(store.OpenNotices(fx.Pair.PairId).Count(notice => notice.ReasonKey == "sync-retry") == 1,
                "every failed run immediately updates one deduplicated notice");
            clock.Now += TimeSpan.FromMinutes(6);
        }
        var stuck = store.OpenNotices(fx.Pair.PairId).Single(notice => notice.ReasonKey == "sync-retry");
        Require(new[] { NoticeChoices.RepairHelp, NoticeChoices.RetryNow, NoticeChoices.Ignore, NoticeChoices.Exclude }
            .All(choice => NoticeOptions.Options(stuck).Any(option => option.Choice == choice)),
            "retry options distinguish Ignore suppression from explicit Stop syncing");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, stuck.NoticeId!, NoticeChoices.RetryNow, CancellationToken.None);
        Require(store.SyncRetryAt(fx.Pair.PairId, "stuck.txt") is { Attempts: 3 } state &&
            state.RetryAfter == DateTimeOffset.MinValue, "retry-now resets backoff without losing attempt count or intent");
        fx.WriterB.OnRead = null;
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(!store.UnresolvedIntents(fx.Pair.PairId).Any(), "later run retries and settles the formerly stuck effect");
        Require(store.OpenNotices(fx.Pair.PairId).All(notice => notice.ReasonKey != "sync-retry"), "settled effect clears its notice");
        Require(await fx.B.ObserveAsync("stuck.txt", CancellationToken.None) is not null, "ordinary planning completes the copy after verification");
        var ignored = new Notice(fx.Pair.PairId, 40, NoticeKind.Info, "ignored.txt", "retry", "sync-retry");
        store.SaveSyncRetry(fx.Pair.PairId, new("ignored.txt", "sync", 3, "still unavailable", clock.Now));
        store.AddNotices([ignored]);
        var ignoredId = store.OpenNotices(fx.Pair.PairId).Single(item => item.Path == "ignored.txt").NoticeId!;
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, ignoredId, NoticeChoices.Ignore, CancellationToken.None);
        store.AddNotices([ignored with { Detail = "failed again" }]);
        Require(store.SyncRetryAt(fx.Pair.PairId, "ignored.txt") is { Attempts: 3 } &&
            !store.Pair(fx.Pair.PairId)!.Excluded.Contains("ignored.txt") &&
            store.NoticeById(fx.Pair.PairId, ignoredId) is { Closed: false } &&
            store.OpenNotices(fx.Pair.PairId).All(item => item.Path != "ignored.txt"),
            "cloud-files E9 Ignore suppresses the cause while preserving actionable retry debt and protection");
        store.AddNotices([ignored with { CauseKey = "new-cause" }]);
        Require(store.OpenNotices(fx.Pair.PairId).Any(item => item.NoticeId == ignoredId),
            "a new cause makes the same entity notice visible again");
        store.ClearSyncRetry(fx.Pair.PairId, "ignored.txt");
        Require(store.NoticeById(fx.Pair.PairId, ignoredId) is { Closed: true },
            "verified success terminally closes the formerly ignored entity notice");
        var excluded = ignored with { Path = "excluded.txt" };
        store.AddNotices([excluded]);
        var excludeId = store.OpenNotices(fx.Pair.PairId).Single(item => item.Path == "excluded.txt").NoticeId!;
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, excludeId, NoticeChoices.Exclude, CancellationToken.None);
        Require(store.Pair(fx.Pair.PairId)!.Excluded.Contains("excluded.txt") && fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
            "exclude changes normal pair policy without deleting either side");
        for (var i = 0; i < 300; i++)
            store.SaveSyncRetry(fx.Pair.PairId, new($"retry/{i:D3}.txt", "sync", 1, "pending", DateTimeOffset.MinValue));
        var batch = store.DueSyncRetries(fx.Pair.PairId, clock.Now, 256);
        var active = batch.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Require(batch.Count == 256 && store.RetryProtected(fx.Pair.PairId, "retry", active, true) &&
            !store.RetryProtected(fx.Pair.PairId, batch[0].Path, active, false),
            "bounded indexed retry batch protects unselected siblings and their containing folder");
        store.SaveSyncRetry(fx.Pair.PairId, new("Ärger/x", "sync", 1, "pending", DateTimeOffset.MinValue));
        store.SaveSyncRetry(fx.Pair.PairId, new("ſ/x", "sync", 1, "pending", DateTimeOffset.MinValue));
        store.SaveSyncRetry(fx.Pair.PairId, new("held/x", "sync", 1, "held", DateTimeOffset.MaxValue));
        var protection = RetryProtection.Create(store, fx.Pair.PairId, active);
        var cached = RetryProtection.CachedFallback(store, fx.Pair.PairId, active);
        foreach (var path in new[] { "", "retry", "RETRY/299.txt", "retry/299.txt/child", "retry/299.txt-suffix", batch[0].Path, "absent",
            "ärger/X", "ÄRGER", "ſ/X", "S/x", "s", "held", "held/X", "held/x/child" })
            foreach (var descendants in new[] { false, true })
            {
                var expected = store.RetryProtected(fx.Pair.PairId, path, active, descendants);
                Require(protection(path, descendants) == expected && cached(path, descendants) == expected,
                    "bounded retry snapshot and folder cache agree with SQLite protection");
            }
        var recorder = new RecordingStore(store);
        var folderCache = RetryProtection.CachedFallback(recorder, fx.Pair.PairId, active);
        for (var n = 0; n < 10_000; n++)
            Require(!folderCache($"absent/deep/folder/{n}", false), "fallback does not invent protection");
        Require(recorder.RetryProtectionCalls == 1, "ten thousand siblings share one ancestor query");
        for (var n = 0; n < 5000; n++) folderCache($"absent/{n}/child", false);
        Require(folderCache("held/x/child", false), "cache rollover preserves protection");
        Console.WriteLine("PASS Completion_StuckNoticeLifecycle");
    }

    private static async Task Completion_RetryAddedAfterPlanningAsync()
    {
        foreach (var injectAt in new[] { 2, 3 })
        {
            var fx = Build(record: true);
            var source = fx.A.Seed("protected.txt", "keep");
            fx.B.Seed("protected.txt", "keep");
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            await fx.A.DeleteAsync(new SyncAction("source-delete", 6, ActionKind.Delete, Side.A,
                source.Path, null, null, source, false, null), CancellationToken.None);
            var reads = 0;
            fx.Recorder!.OnRetryPathsRead = () =>
            {
                if (++reads == injectAt)
                    fx.Store.SaveSyncRetry(fx.Pair.PairId, new(source.Path, "sync", 1, "late protection", DateTimeOffset.MaxValue));
            };
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(reads >= injectAt && fx.WriterB.Deletes == 0 &&
                await fx.B.ObserveAsync(source.Path, CancellationToken.None) is not null,
                "retry saved after planning blocks deletion at each fresh safety snapshot");
        }
        Console.WriteLine("PASS Completion_RetryAddedAfterPlanning");
    }

    private static async Task Completion_PostPreviewDeleteAsync()
    {
        var fx = Build();
        var a = fx.A.Seed("matched.txt", "original");
        fx.B.Seed("matched.txt", "original");
        var run = fx.Run();
        var progress = new RecordingProgress();
        var preview = await run.PreviewFirstSyncAsync(fx.Pair.PairId, progress, CancellationToken.None);
        var matches = preview.MatchedBothSides.ToDictionary(item => item.Path,
            item => new PrepareMatch(Content.SideStamp(item.A), Content.SideStamp(item.B)), StringComparer.OrdinalIgnoreCase);
        await fx.A.DeleteAsync(new SyncAction("owner-delete", 6, ActionKind.Delete, Side.A, a.Path, null, null, a, false, null), CancellationToken.None);
        var (plan, _) = await run.PreviewFirstSyncApplyResolvedAsync(fx.Pair.PairId, null, null, [], matches, progress, CancellationToken.None);
        plan = run.DeferPostPreviewChanges(fx.Pair, plan, matches);
        await run.ApplyAsync(fx.Pair.PairId, plan, progress, CancellationToken.None);
        Require(fx.Store.FirstSyncDone(fx.Pair.PairId), "post-preview deletion does not prevent first-sync completion");
        Require(await fx.B.ObserveAsync(a.Path, CancellationToken.None) is not null && fx.WriterB.Deletes == 0,
            "first-sync apply never executes the post-preview deletion");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).All(notice => notice.ReasonKey != "sync-retry"),
            "post-preview change itself does not create a problem notice");
        await run.IncrementalAsync(fx.Pair.PairId, progress, CancellationToken.None);
        Require(await fx.B.ObserveAsync(a.Path, CancellationToken.None) is null && fx.WriterB.Deletes == 1,
            "next ordinary run propagates the deletion using retained prepare matching evidence");
        Require(!fx.Store.ReadSyncRetries(fx.Pair.PairId).Any(), "successful ordinary propagation retires handoff state");
        Console.WriteLine("PASS Completion_PostPreviewDelete");
    }

    private static async Task Completion_UnreviewedEvidenceCursorAsync()
    {
        var fx = Build();
        fx.A.Seed("later.txt", "keep until ordinary evidence resolves");
        var run = fx.Run();
        var progress = new RecordingProgress();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, progress, CancellationToken.None);
        plan = plan with
        {
            Actions = [],
            UnreviewedHeldDeletions = [new HeldDeletion("later.txt", Side.A)],
            InventoryScope = run.InventoryScope(fx.Pair),
            InventoryCursors = new Dictionary<string, string> { [fx.Pair.A.RootId] = "safe-local", [fx.Pair.B.RootId] = "pinned-cloud" }
        };
        await run.ApplyAsync(fx.Pair.PairId, plan, progress, CancellationToken.None);
        Require(fx.Store.FirstSyncDone(fx.Pair.PairId), "unreviewed evidence does not block completion");
        Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0, "no unreviewed deletion runs in first-sync apply");
        Require(fx.Store.Cursor(fx.Pair.A.RootId) == "safe-local" && fx.Store.Cursor(fx.Pair.B.RootId) == "pinned-cloud",
            "promotion uses only the supplied safe checkpoints, never a later unresolved observation");
        Require(fx.Store.ReadSyncRetries(fx.Pair.PairId).Single().Held?.Path == "later.txt",
            "ordinary evidence input survives the promoted cursor");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Count == 0, "unreviewed status alone is not a notice");
        var held = await fx.A.ObserveAsync("later.txt", default);
        fx.Provider.AddInventory(fx.Pair.B, new RecoveryEvidenceInventory(fx.B)
            { Deleted = DateTimeOffset.UtcNow.AddDays(1), ContentHash = held!.Hash });
        await run.IncrementalAsync(fx.Pair.PairId, progress, default);
        Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 && await fx.A.ObserveAsync("later.txt", default) is not null,
            "an ordinary retry never promotes unreviewed evidence to deletion consent");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "later.txt" && notice.ReasonKey == "deletion-review"),
            "newer deletion evidence asks delete / keep-restore / ignore");
        Require(fx.Store.CountSyncRetries(fx.Pair.PairId) == 0, "decision-held evidence does not keep AutoSync busy");
        var decision = fx.Store.OpenNotices(fx.Pair.PairId).Single(notice => notice.Path == "later.txt" && notice.ReasonKey == "deletion-review");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, decision.NoticeId!, NoticeChoices.DeleteReviewed, default);
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "later.txt")!.ReviewedActions!.All(action =>
            action.Kind != ActionKind.Delete || action.RecoveryFirst), "explicit evidence deletion always requires Recovery");
        await run.IncrementalAsync(fx.Pair.PairId, progress, default);
        Require(fx.WriterA.Deletes == 1 && fx.WriterA.Preserves == 1 &&
            await fx.A.ObserveAsync("later.txt", default) is null,
            "explicit delete preserves the stamped survivor before removing it, even with identical history");
        Console.WriteLine("PASS Completion_UnreviewedEvidenceCursor");
    }

    private static async Task Completion_ReviewedWinnerRetryAsync()
    {
        var fx = Build();
        var chosen = fx.A.Seed("winner.txt", "reviewed local", mtime: DateTimeOffset.UtcNow.AddDays(-2));
        fx.B.Seed("winner.txt", "newer remote", mtime: DateTimeOffset.UtcNow.AddDays(-1));
        fx.WriterB.PutFailure = _ => new IOException("temporary target failure");
        var retryClock = new CompletionClock();
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, Settings, retryClock);
        var sink = new RecordingProgress();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId,
            new Dictionary<string, Side?> { ["winner.txt"] = Side.A }, sink, CancellationToken.None);
        await run.ApplyAsync(fx.Pair.PairId, plan, sink, CancellationToken.None);
        Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && fx.Store.ReadSyncRetries(fx.Pair.PairId)
            .Single(item => item.Path == "winner.txt").ReviewedActions is { Count: > 0 },
            "failed reviewed winner is retained in the same durable retry row");
        fx.WriterB.PutFailure = null;
        await run.IncrementalAsync(fx.Pair.PairId, sink, CancellationToken.None);
        var remote = await fx.B.ObserveAsync("winner.txt", CancellationToken.None);
        var winnerDebt = fx.Store.SyncRetryAt(fx.Pair.PairId, "winner.txt");
        var localWinner = await fx.A.ObserveAsync("winner.txt", CancellationToken.None);
        Console.WriteLine($"WINNER RETRY MEASUREMENT now={DateTimeOffset.UtcNow:O}; due={winnerDebt?.RetryAfter:O}; attempts={winnerDebt?.Attempts}; kind={winnerDebt?.Kind}; reviewed={winnerDebt?.ReviewedActions?.Count}; localMatches={localWinner is not null && Content.SameContent(chosen, localWinner) == true}; remoteMatches={remote is not null && Content.SameContent(chosen, remote) == true}; deletesA={fx.WriterA.Deletes}; deletesB={fx.WriterB.Deletes}");
        // Measured first attempt sleeps for ten seconds, retaining the reviewed winner and both copies.
        Require(winnerDebt is { Attempts: 1, ReviewedActions.Count: > 0 } && winnerDebt.RetryAfter > retryClock.Now &&
            localWinner is not null && Content.SameContent(chosen, localWinner) == true &&
            remote is not null && Content.SameContent(chosen, remote) == false &&
            fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
            "before the durable deadline the reviewed winner remains protected without replay or deletion");
        retryClock.Now = winnerDebt!.RetryAfter.AddTicks(1);
        await run.IncrementalAsync(fx.Pair.PairId, sink, CancellationToken.None);
        remote = await fx.B.ObserveAsync("winner.txt", CancellationToken.None);
        Require(remote is not null && Content.SameContent(chosen, remote) == true,
            "unchanged reviewed winner survives ordinary replanning instead of switching to the newer loser");
        Console.WriteLine("PASS Completion_ReviewedWinnerRetry");
    }

    private static async Task Completion_FailedItemAsync()
    {
        var fx = Build();
        fx.A.Seed("bad.txt", "cannot write");
        fx.A.Seed("good.txt", "can write");
        fx.WriterB.PutFailure = action => action.Path == "bad.txt" ? new IOException("permanent write failure") : null;
        var run = fx.Run();
        var progress = new RecordingProgress();
        var preview = await run.PreviewFirstSyncAsync(fx.Pair.PairId, progress, CancellationToken.None);
        var report = await run.ApplyAsync(fx.Pair.PairId, preview, progress, CancellationToken.None);
        Console.WriteLine($"FAILED ITEM MEASUREMENT firstSyncDone={fx.Store.FirstSyncDone(fx.Pair.PairId)}; outcome={report.Outcome}; deferred={report.Deferred}; done={report.Done}; failed={report.Failed}; retries={string.Join(",", fx.Store.ReadSyncRetries(fx.Pair.PairId).Select(item => item.Path + ":" + item.Kind))}");
        // OUTCOME's recorded ownership: completing the attempt must not hide unfinished work counts.
        Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && report.Outcome == RunOutcomes.Partial &&
            report.Deferred == 1 && report.Done == 1 && report.Failed == 1 &&
            fx.Store.SyncRetryAt(fx.Pair.PairId, "bad.txt") is { Kind: "first-sync" },
            "failed item remains durable and visible while first-sync attempt and independent work complete");
        Require(await fx.B.ObserveAsync("good.txt", CancellationToken.None) is not null, "independent item completes");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "bad.txt" && notice.ReasonKey == "sync-retry"),
            "failed item's notice survives completion");
        Require(fx.Store.ReadSyncRetries(fx.Pair.PairId).Any(item => item.Path == "bad.txt"), "retry input survives cursor promotion");
        Console.WriteLine("PASS Completion_FailedItem");
    }
}
