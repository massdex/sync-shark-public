using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>Run orchestration checks over the real Planner and Executor: gating, first sync, cursors, safety, recovery.</summary>
internal static partial class RunChecks
{
    public static async Task RunAsync()
    {
        await PairSummaryDoesNotHoldSiblingsAsync();
        await HeldRetriesSurviveAsync();
        await ReleasedSurvivor_FlaggedAsync();
        await HeldIntentsDoNotStarveAsync();
        await HeldIntentBoundariesAsync();
        await HeldIntentSummaryAsync();
        await HeldIntentRetryParksAsync();
        await HeldIntentUnparkAfterOutsideCommitAsync();
        await HeldIntentLinkAndStubRetireAsync();
        await HeldIntentDestructiveLinkAndStubStayPendingAsync();
        await HeldIntentPendingStaysUnsyncedAsync();
        await HeldIntentPendingNoticeCountsAsync();
        await HeldIntentQuarantineChoiceReconcilesAsync();
        await HeldIntentApplySummaryUsesPostClassificationAsync();
        await HeldRetryRotationAndReleaseAsync();
        await HeldRetryExclusionAndContainersAsync();
        await ItemIgnoreWaitsForSettingsSaveAsync();
        await HistoryFailureDoesNotFailCompletedRunAsync();
        PairMissing_Refused();
        DisabledPair_Refused();
        FirstSyncNotDone_Refused();
        await FirstSync_PreviewIsEffectFreeAsync();
        await FirstSync_SkippedReasonReportedAsync();
        await FirstSync_ApplyThenIncrementalIsEmptyAsync();
        await FirstSync_MoveDuringReview_IsMoveAsync();
        await FirstSync_PreviewHashesBeforeDecidingAsync();
        await FirstSync_IncompleteScanRefusedAsync();
        await FirstSync_DeletionWinsRepeatablyAsync();
        await MissingDeleteIdentityChoicesAsync();
        await FirstSync_DeletionLookupIgnoresCaseAsync();
        await Incremental_UnconfirmedContentReplannedAsync();
        await Incremental_DeleteSourceNoticeSettlesWithoutActionAsync();
        await Incremental_FreeSpacePendingRetriedAsync();
        await Incremental_ConvergesAfterApplyAsync();
        await NestedPair_OuterCannotMoveContainingFolderAsync();
        await Apply_SkipsWhenExclusionsChangedAsync();
        await NarrowedSafetyChecksAsync();
        await NoticeRestoreChecksAsync();
        await Notice_ClosedWhenFileSyncsCleanlyAsync();
        await Notice_ClosedWhenPathDiffersOnlyInCaseAsync();
        await Notice_HoldSurvivesEmptyRunAsync();
        await Notice_UntouchedFileKeepsNoticeAsync();
        await Notice_KeepBothSurvivesEchoAndCleanSyncAsync();
        await Notice_EditBeatsDeleteSurvivesEchoAsync();
        await Notice_ClosePolicyByReasonAsync();
        await AccessLost_ScanPropagatesAsync();
        await AccessLost_EffectKeepsIntentAsync();
        await AccessLost_UncertainStaysUncertainAsync();
        await CliRuntime_AccountsArePassedThroughAsync();
        await CliRuntime_CreatePairAwaitsRootObservationAsync();
        await Incremental_OneSidedEditAsync();
        await Incremental_DeletedIdenticalSkipsRecoveryAsync();
        await Incremental_LocalLocalDeleteCreatesRecoveryAsync();
        await Incremental_NoChanges_WritesNothingAsync();
        await Incremental_FullRescanAfterForgetCursorAsync();
        await RequestRescan_WaitsForRunningScanAsync();
        await RequestRescan_ReconcilesBeforeInventoryAsync();
        await Reconcile_ExcludedIntentSettlesAsync();
        await Reconcile_ExcludedUncertainHonorsGraceAsync();
        await Reconcile_ExcludedMoveCompletesFirstSyncAsync();
        await Reconcile_ExcludedFolderDeleteNeverExpandsAsync();
        await Reconcile_ExcludedAppliedIntentIsDoneAsync();
        await Incremental_FailedEffect_RetriedNextRunAsync();
        await Incremental_FolderDeleteChildFailureCountedAsync();
        await Reconcile_Uncertain_StopsBeforeInventoryAsync();
        await Reconcile_UncertainItemsConcurrentlyAsync();
        await Reconcile_AllPagesAsync();
        await StoredClash_RefusedBeforeReconcileAsync();
        await LegacyEmptyIdPairs_DoNotBlockDisjointPairAsync();
        await KillMidApply_NoDuplicatesNoPartialsAsync();
        await KillBeforeEffect_SkippedAndReplannedAsync();
        await Stop_KeepsFinishedWorkAsync();
        await Progress_ThrottledAsync();
        await Progress_CountsPlannedActionsOnlyAsync();
        await ReviewedPropagationStillRequiresSourceAbsenceAsync();
        Tally_DuplicateTerminalResultCountsOnce();
        await Activity_ProcessMessagesReachSinkAsync();
        await Activity_HeartbeatWhileQuietAsync();
        Operations_SweepWarns();
        await Operations_QuietWhenFastAsync();
        Operations_RegistrationCheap();
        RunLog_WritesAndRotatesAsync();
        await Output_RegressionChecksAsync();
        Operations_DisposedNoOp();
        Progress_UnknownTotalAsync();
        Mapping_StablePhaseWithDetail();
        await Activity_DuplicateSuppressionAsync();
        Include_NestedPairExcludedAndIgnoreRules();
        await SafetyFilter_DropsOutsidePairAreaAsync();
        await ProductOwned_RemoteOnlyChildNotPlannedAsync();
        await ProductOwned_AncestorMoveAndDeleteDroppedAsync();
        await CopyHash_OneSidedCopiesCarryHashesAsync();
        await CopyHash_UnhashableSourceHeldAsync();
        await CopyHash_IncrementalCopyHashedAsync();
        await CopyHash_KeepBothHeldTogetherAsync();
        await CopyHash_SourceUnderMovedFolderAsync();
        await CopyHash_FolderRemoveHashesVictimsAsync();
        await LinkExclusionChecks.RunAsync();
        await DecisionHoldChecks.RunAsync();
        await ConcurrentScanChecks.RunAsync();
        await AccountVerificationDedupeAsync();
        await RebuildableChecks.RunAsync();
        await DeferredEvidenceChecksAsync();
        await MemoryReductionChecksAsync();
        Console.WriteLine("v2 run checks passed.");
    }

    private static async Task PairSummaryDoesNotHoldSiblingsAsync()
    {
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "summary-scope-" + Guid.NewGuid().ToString("N")),
            Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("held.txt", "original");
        await FirstSyncAsync(fx.Run(), fx);
        fx.A.Seed("held.txt", "awaiting decision");
        fx.A.Seed("sibling.txt", "independent progress");
        store.AddNotices([
            new("p1", 0, NoticeKind.Info, "", "pair-wide information", "fixture-info"),
            new("p1", 0, NoticeKind.Info, "", "pair-wide evidence summary", "deletion-history-unavailable"),
            new("p1", 0, NoticeKind.UserDecision, "", "pair-wide choices", "fixture-summary"),
            new("p1", 10, NoticeKind.UserDecision, "held.txt", "item decision", "fixture-item")]);
        await fx.Run().IncrementalAsync("p1", new RecordingProgress(), default);
        Require((await fx.B.ObserveAsync("sibling.txt", default))?.Hash == (await fx.A.ObserveAsync("sibling.txt", default))?.Hash &&
            await fx.B.ObserveAsync("sibling.txt", default) is not null,
            "pair-wide info and decision summaries never hold an independent sibling");
        Require((await fx.B.ObserveAsync("held.txt", default))?.Hash != (await fx.A.ObserveAsync("held.txt", default))?.Hash,
            "real item decision remains held alongside pair summaries");
        store.PrepareDateRepair("p1");
        var baselines = store.Baselines("p1");
        Require(!store.DateRepairHeld(fx.Pair, baselines.Single(b => b.A.Path == "sibling.txt")) &&
            store.DateRepairHeld(fx.Pair, baselines.Single(b => b.A.Path == "held.txt")),
            "date repair ignores empty-path summaries while keeping the exact item hold");
        foreach (var kind in new[] { "decision", "deletion-evidence" })
        {
            store.SaveSyncRetry("p1", new SyncRetry("", kind, 0, "whole-root evidence", DateTimeOffset.MaxValue));
            Require(LinkExclusions.Of(store, fx.Pair, false).Covers("sibling.txt"),
                "sleeping whole-root " + kind + " remains protective without a notice");
            store.PrepareDateRepair("p1");
            Require(store.DateRepairHeld(fx.Pair, baselines.Single(b => b.A.Path == "sibling.txt")),
                "date repair retains durable whole-root " + kind);
            store.ClearSyncRetry("p1", "");
        }
    }

    // One worker keeps the executor's effect order deterministic for the crash and stop checks.
    private static V2Settings Settings => V2Settings.Default with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };

    // A gate wait fails a check rather than hanging it if a run never reaches the effect the check waits on.
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    private static readonly IProgress<string> SilentProgress = new SilentTextProgress();

    private sealed class SilentTextProgress : IProgress<string>
    {
        public void Report(string value) { }
    }

    // Counts reconcile completions; they arrive concurrently, so the counter is atomic.
    private sealed class CompletionProgress : IProgress<int>
    {
        private int _count;
        private int _last;

        public int Count => Volatile.Read(ref _count);
        public int Last => Volatile.Read(ref _last);

        public void Report(int value)
        {
            Interlocked.Increment(ref _count);
            Volatile.Write(ref _last, value);
        }
    }

    private static void PairMissing_Refused()
    {
        using var store = new InMemoryStateStore();
        var run = new SyncRun(store, NewExecutor(store), new FakeEndpointProvider(), Settings, TimeProvider.System);
        ExpectRefused(run.IncrementalAsync("missing", new RecordingProgress(), CancellationToken.None));
        Console.WriteLine("PASS PairMissing_Refused");
    }

    private static void DisabledPair_Refused()
    {
        var fx = Build(enabled: false);
        var run = fx.Run();
        ExpectRefused(run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None));
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "a refused run makes no writer calls");
        Console.WriteLine("PASS DisabledPair_Refused");
    }

    private static void FirstSyncNotDone_Refused()
    {
        var fx = Build();
        var run = fx.Run();
        ExpectRefused(run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None));
        Console.WriteLine("PASS FirstSyncNotDone_Refused");
    }

    private static async Task FirstSync_PreviewIsEffectFreeAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        fx.A.Seed("b.txt", "2");
        fx.B.Seed("b.txt", "2");
        fx.B.Seed("c.txt", "3");
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "the preview has no writer calls");
        Require(plan.Actions.Any(a => a.Kind == ActionKind.CopyNew && a.Target == Side.B && a.Path == "a.txt"),
            "the preview plans the local-only copy");
        Require(plan.Actions.Any(a => a.Kind == ActionKind.CopyNew && a.Target == Side.A && a.Path == "c.txt"),
            "the preview plans the cloud-only copy");
        Require(!fx.Store.FirstSyncDone(fx.Pair.PairId), "the preview does not apply the first sync");
        Require(fx.Store.Cursor(fx.Pair.A.RootId) is null && fx.Store.Cursor(fx.Pair.B.RootId) is null,
            "the preview commits no cursor, so the next preview scans in full again");
        Require(fx.WriterA.Calls == 0, "the preview still writes nothing");
        Console.WriteLine("PASS FirstSync_PreviewIsEffectFree");
    }

    internal static async Task PairNoticeIgnoreDoesNotWaitForRunAsync()
    {
        var fx = Build();
        fx.Store.MarkFirstSyncDone(fx.Pair.PairId);
        fx.A.Seed("one.txt", "one"); fx.A.Seed("two.txt", "two");
        fx.WriterB.ParkAfterFirstEffect = true;
        var run = fx.Run();
        var pending = run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        try
        {
            await fx.WriterB.FirstEffectCommitted.Task.WaitAsync(GateTimeout);
            fx.Store.AddNotices([new Notice(fx.Pair.PairId, 0, NoticeKind.Info, "", "Reconnect and Retry", "autosync-failed-account-access")]);
            var notice = fx.Store.OpenNotices(fx.Pair.PairId).Single(n => NoticeChoices.IsPairRetryNotice(n));
            var runtime = new V2CliRuntime(null, fx.Store, run, new EndpointRootProbe(fx.Provider), Path.GetTempPath());
            await runtime.ResolveConflictAsync(fx.Pair.PairId, "", NoticeChoices.Ignore, notice.NoticeId, 0, null, default).WaitAsync(TimeSpan.FromSeconds(2));
            Require(fx.Store.NoticeById(fx.Pair.PairId, notice.NoticeId!) is { Closed: true }, "pair Ignore closes the notice");
            Require(!pending.IsCompleted && fx.Store.Pair(fx.Pair.PairId)!.Excluded.Count == 0,
                "Ignore responds while the run remains active and never excludes the root");
        }
        finally { await run.StopAsync(); await pending; }
        Console.WriteLine("PASS pair notice Ignore bypasses the active run gate");
    }

    internal static async Task ItemIgnoreWaitsForSettingsSaveAsync()
    {
        var fx = Build();
        var run = fx.Run();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        // This uses the production run gate and deliberately saves the stale run-start pair
        // exactly as PersistRebuildables does. No timing assumptions or real providers.
        var pending = Task.Run(() => run.UpdatePairSettingsAsync(() =>
        {
            var snapshot = fx.Store.Pair(fx.Pair.PairId)!;
            started.SetResult();
            if (!release.Wait(GateTimeout)) throw new TimeoutException("test run was not released");
            fx.Store.SavePair(snapshot with { Excluded = ["generated"] });
        }, default));
        await started.Task.WaitAsync(GateTimeout);
        fx.Store.AddNotices([new Notice(fx.Pair.PairId, 0, NoticeKind.Info, "failed.txt", "Retry this item", "sync-retry")]);
        var notice = fx.Store.OpenNotices(fx.Pair.PairId).Single();
        var runtime = new V2CliRuntime(null, fx.Store, run, new EndpointRootProbe(fx.Provider), Path.GetTempPath());
        var ignored = runtime.ResolveConflictAsync(fx.Pair.PairId, "failed.txt", NoticeChoices.Ignore, notice.NoticeId, 0, null, default);
        try
        {
            Require(!ignored.IsCompleted, "item Ignore waits behind the active run gate");
            // A run may also resolve the original notice while the user's exclusion is queued.
            fx.Store.CloseNotice(notice.NoticeId!, "resolved");
        }
        finally { release.Set(); }
        await pending.WaitAsync(GateTimeout);
        await ignored.WaitAsync(GateTimeout);
        var exclusions = fx.Store.Pair(fx.Pair.PairId)!.Excluded;
        Require(exclusions.Contains("generated") && exclusions.Contains("failed.txt"), "both run settings and queued exclusion survive");
        Console.WriteLine("PASS ItemIgnoreWaitsForSettingsSave");
    }

    internal static async Task HistoryFailureDoesNotFailCompletedRunAsync()
    {
        using var store = new InMemoryStateStore { BeforeRecordSummary = _ => throw new IOException("history write unavailable") };
        var fx = Build(suppliedStore: store);
        fx.A.Seed("file.txt", "content");
        var report = await FirstSyncAsync(fx.Run(), fx);
        Require(report.Outcome == RunOutcomes.Complete && report.Done == 1, "history failure leaves the successful run outcome unchanged");
        var notice = store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == "run-history-unavailable");
        Require(NoticeChoices.Options(notice).Any(o => o.Choice == NoticeChoices.RetryNow), "history failure offers a retry notice");
        await MirrorAsync(fx);
        var empty = await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(empty.Done == 0 && store.OpenNotices(fx.Pair.PairId).Any(n => n.ReasonKey == "run-history-unavailable"),
            "an empty poll does not clear a history failure without a successful history write");
        store.BeforeRecordSummary = null;
        fx.A.Seed("another.txt", "more content");
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(store.OpenNotices(fx.Pair.PairId).All(n => n.ReasonKey != "run-history-unavailable"), "a later summary write clears the history notice");
        Console.WriteLine("PASS HistoryFailureDoesNotFailCompletedRun");
    }

    private static async Task FirstSync_ApplyThenIncrementalIsEmptyAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        fx.B.Seed("c.txt", "3");
        var run = fx.Run();
        var applied = await FirstSyncAsync(run, fx);
        Require(applied.Done == 2 && applied.Outcome == RunOutcomes.Complete, "the approved plan applies both copies");
        Require(fx.Store.FirstSyncDone(fx.Pair.PairId), "the first sync is marked done");
        await MirrorAsync(fx);
        var again = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(again.Done == 0 && again.Outcome == RunOutcomes.Complete, "the next run finds nothing to do");
        // each finished run leaves exactly one bounded summary, newest first.
        var history = fx.Store.ReadRunHistory(null, 10);
        Require(history.Count == 1 && history.All(row => row.PairId == fx.Pair.PairId &&
                row.Outcome == RunOutcomes.Complete && row.CompletedUtc >= row.StartedUtc),
            "meaningful runs retain a summary; empty successful safety polls do not flood history");
        Require(history[0].Seq > 0, "retained summary has a unique sequence");
        Console.WriteLine("PASS FirstSync_ApplyThenIncrementalIsEmpty");
    }

    private static async Task FirstSync_MoveDuringReview_IsMoveAsync()
    {
        var fx = Build();
        fx.A.Seed("x.txt", "same");
        fx.B.Seed("x.txt", "same");
        var run = fx.Run();
        // The move happens between the preview and the apply, while the reviewed plan is still for x.
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        await fx.A.MoveAsync(new SyncAction("seed-move", 9, ActionKind.Move, Side.A, "y.txt", "x.txt", null, null,
            false, null), CancellationToken.None);
        await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(fx.WriterB.Moves == 1, "the file moved during review is applied as a move");
        Require(fx.WriterB.Puts == 0, "the move is never turned into a copy");
        var b = await MapAsync(fx.B);
        Require(b.ContainsKey("y.txt") && !b.ContainsKey("x.txt"), "the cloud side has y and no x");
        Console.WriteLine("PASS FirstSync_MoveDuringReview_IsMove");
    }

    // Audit A2: the run asks the Planner which paths need a hash and reads them before deciding.
    private static async Task FirstSync_PreviewHashesBeforeDecidingAsync()
    {
        var fx = Build();
        fx.A.Seed("x.txt", "same");
        fx.B.Seed("x.txt", "same");
        var listingA = new ScriptedInventory(fx.A) { Hashless = true };
        var listingB = new ScriptedInventory(fx.B) { Hashless = true };
        fx.Provider.AddInventory(fx.Pair.A, listingA).AddInventory(fx.Pair.B, listingB);
        var plan = await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(listingA.HashedPaths == 1 && listingB.HashedPaths == 1, "both unhashed copies are hashed before deciding");
        Require(plan.Actions.Count == 1 && plan.Actions[0].Kind == ActionKind.Adopt,
            "equal content found by hashing is adopted, never copied");
        Console.WriteLine("PASS FirstSync_PreviewHashesBeforeDeciding");
    }

    // Audit A5: an incomplete scan never yields an approvable first-sync plan.
    private static async Task FirstSync_IncompleteScanRefusedAsync()
    {
        var fx = Build();
        fx.B.Seed("c.txt", "3");
        fx.Provider.AddInventory(fx.Pair.A, new ScriptedInventory(fx.A) { Incomplete = true });
        var plan = await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(plan.RunUnavailable is not null && plan.Actions.Count == 0,
            "whole-root read outage returns an unavailable plan, never deletion authority");
        Require(fx.Store.Cursor(fx.Pair.A.RootId) is null, "a refused preview commits no cursor");
        Require(!fx.Store.FirstSyncDone(fx.Pair.PairId), "the pair stays in first-sync mode");
        Console.WriteLine("PASS FirstSync_IncompleteScanRefused");
    }

    // Audit A4 and review 1: the deletion time reaches the Planner on every preview, so a repeated preview and the
    // apply of the approved plan give the same rule-19 outcome (a preview is repeatable).
    private static async Task FirstSync_DeletionWinsRepeatablyAsync()
    {
        foreach (var repeatPreview in new[] { false, true })
        {
            var fx = Build();
            fx.A.Seed("x.txt", "same");
            var cloud = fx.B.Seed("x.txt", "same");
            await fx.B.DeleteAsync(new SyncAction("seed-delete", 6, ActionKind.Delete, Side.B, "x.txt", null, null, cloud,
                false, null), CancellationToken.None);
            fx.Provider.AddInventory(fx.Pair.B, new RecoveryEvidenceInventory(fx.B)
                { Cached = true, Deleted = DateTimeOffset.UtcNow.AddHours(1), ContentHash = cloud.Hash });
            var run = fx.Run();
            var first = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            var second = repeatPreview
                ? await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None) : first;
            Require(first.Actions.Count == 0 && second.Actions.Count == 0 &&
                first.HeldDeletions.SequenceEqual([new HeldDeletion("x.txt", Side.A)]) &&
                second.HeldDeletions.SequenceEqual(first.HeldDeletions),
                "both previews hold the same cached rule-19 victim for hash-aware resolution");
            // This no-baseline victim has explicit per-item review, not a trusted historical source id.
            // Its approval must survive into the action even when an absence probe cannot grant authority.
            fx.WriterB.AbsenceUnknown = true;
            var applied = await run.ApplyAsync(fx.Pair.PairId, second, new RecordingProgress(), CancellationToken.None,
                ReviewedHeld(second));
            Require(applied.Deleted == 1 && applied.Outcome == RunOutcomes.Complete && fx.WriterA.Preserves == 0, "the apply carries out the same delete");
            Require(await fx.A.ObserveAsync("x.txt", CancellationToken.None) is null &&
                await fx.B.ObserveAsync("x.txt", CancellationToken.None) is null, "the file is gone on both sides");
        }
        Console.WriteLine("PASS FirstSync_DeletionWinsRepeatably");
    }

    // Review 2: the deletion lookup ignores case (decision 34): Foo.txt deleted, foo.txt kept on the other side.
    private static async Task FirstSync_DeletionLookupIgnoresCaseAsync()
    {
        var fx = Build();
        var local = fx.B.Seed("Foo.txt", "same");
        await fx.B.DeleteAsync(new SyncAction("seed-delete", 6, ActionKind.Delete, Side.B, "Foo.txt", null, null, local,
            false, null), CancellationToken.None);
        fx.A.Seed("foo.txt", "same");
        fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B)
        {
            Cached = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase)
                { ["Foo.txt"] = DateTimeOffset.UtcNow.AddHours(1) },
            HistoryHashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Foo.txt"] = local.Hash },
            ResolvedPath = _ => "FOO.txt"
        });
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(plan.Actions.Count == 0 && plan.HeldDeletions.SequenceEqual([new HeldDeletion("foo.txt", Side.A)]),
            "the deletion evidence for Foo.txt holds foo.txt for hash-aware resolution");
        var reviewed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FolderDelete.VictimKey(Side.A, "fOo.TxT") };
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None, reviewed);
        Require(report.Deleted == 1 && report.Failed == 0 && fx.WriterA.Preserves == 0 &&
            await fx.A.ObserveAsync("foo.txt", CancellationToken.None) is null,
            "case-insensitive matching revision evidence deletes foo.txt without Recovery");
        Console.WriteLine("PASS FirstSync_DeletionLookupIgnoresCase");
    }

    private static string Decisions(Plan plan) =>
        string.Join("; ", plan.Actions.Select(action => $"{action.Kind} {action.Target} {action.Path} {action.Rule}"));

    private static async Task NestedPair_OuterCannotMoveContainingFolderAsync()
    {
        var fx = Build();
        var outer = fx.Pair with
        {
            A = fx.Pair.A with { RootPath = "C:\\C" },
            B = fx.Pair.B with { RootPath = "/C" }
        };
        fx.Store.SavePair(outer);
        fx.A.Seed("sub", "", ItemKind.Folder);
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        // A nested pair appears inside the synced folder, then the outer side renames that folder.
        fx.Store.SavePair(new PairDef("inner", "inner",
            new RootDef("ri-a", SyncProviders.Local, null, "C:\\C\\sub\\inner", "id:ria"),
            new RootDef("ri-b", SyncProviders.Dropbox, "acc", "/C/sub/inner", "id:rib"), [], [], true));
        await fx.A.MoveAsync(new SyncAction("seed-move", 25, ActionKind.Move, Side.A, "sub2", "sub", null, null, false,
            null), CancellationToken.None);
        var callsBefore = fx.WriterB.Calls;
        await run.IncrementalAsync(outer.PairId, new RecordingProgress(), CancellationToken.None);
        Require(fx.WriterB.Calls == callsBefore, "a move of a folder containing a nested pair is dropped");
        Require(fx.Store.OpenNotices(outer.PairId).Any(notice => notice.Rule == 33),
            "the drop is reported as a notice");
        Console.WriteLine("PASS NestedPair_OuterCannotMoveContainingFolder");
    }

    private static async Task Apply_SkipsWhenExclusionsChangedAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(plan.Actions.Any(action => action.Path == "a.txt" && action.Target == Side.B),
            "the preview plans the copy");
        fx.Store.SavePair(fx.Pair with { Excluded = ["a.txt"] });
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
        Require(report.Skipped == 1 && report.Done == 0, "newly excluded effect is reported as skipped");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.ReasonKey == PlanNotices.OutsidePairArea.Key),
            "newly excluded effect has an explanatory notice");
        Require(fx.WriterB.Calls == 0, "an excluded approval applies nothing");
        Console.WriteLine("PASS Apply_SkipsWhenExclusionsChanged");
    }

    private static async Task Notice_ClosedWhenFileSyncsCleanlyAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        fx.Store.AddNotices([new Notice(fx.Pair.PairId, 2, NoticeKind.AutoConflict, "a.txt", "old conflict", "old-conflict")]);
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Count == 1, "the notice is open before the run");
        fx.A.Seed("a.txt", "2");
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Done == 1, "the edited file syncs");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Count == 0,
            "a notice whose file is re-evaluated and syncs cleanly is closed");
        Console.WriteLine("PASS Notice_ClosedWhenFileSyncsCleanly");
    }

    private static async Task Notice_ClosedWhenPathDiffersOnlyInCaseAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        fx.Store.AddNotices([new Notice(fx.Pair.PairId, 2, NoticeKind.AutoConflict, "A.txt", "old conflict", "old-conflict")]);
        fx.A.Seed("a.txt", "2");
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Done == 1, "the edited file syncs");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Count == 0,
            "a notice on a path differing only in case closes when the file syncs cleanly");
        Console.WriteLine("PASS Notice_ClosedWhenPathDiffersOnlyInCase");
    }

    private static async Task Notice_HoldSurvivesEmptyRunAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        // The first run after the apply re-reads the apply's own writes; the next one has an empty delta.
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        fx.Store.AddNotices([new Notice(fx.Pair.PairId, 5, NoticeKind.UserDecision, "a.txt", "held for you", "held")]);
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Done == 0, "the run has an empty delta");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Count == 1,
            "an unresolved hold notice survives an empty-delta run");
        Console.WriteLine("PASS Notice_HoldSurvivesEmptyRun");
    }

    private static async Task Notice_UntouchedFileKeepsNoticeAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        fx.A.Seed("b.txt", "1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        fx.Store.AddNotices([new Notice(fx.Pair.PairId, 2, NoticeKind.AutoConflict, "a.txt", "old conflict", "old-conflict")]);
        fx.A.Seed("b.txt", "2");
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Done == 1, "only the other file syncs");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "a.txt"),
            "a notice on a file the run did not touch stays open");
        Console.WriteLine("PASS Notice_UntouchedFileKeepsNotice");
    }

    // Requirements (clashing conflicts): a keep-both stays flagged until the user resolves it, so neither the echo
    // run that reads its own writes nor a later clean sync of the same file may close its notice.
    private static async Task Notice_KeepBothSurvivesEchoAndCleanSyncAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        fx.A.Seed("a.txt", "edited on A");
        fx.B.Seed("a.txt", "edited on B");
        var clash = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(clash.KeptBoth == 2, "the clash keeps both versions");
        bool KeepBothOpen() => fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Kind == NoticeKind.KeepBoth);
        Require(KeepBothOpen(), "the keep-both notice is open after the clash");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(KeepBothOpen(), "the echo run does not close the keep-both notice");
        fx.A.Seed("a.txt", "edited again on A");
        var clean = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(clean.Done == 1 && clean.Failed == 0, "the later one-sided edit syncs cleanly");
        Require(KeepBothOpen(), "a clean sync of the same file does not close the keep-both notice");
        Console.WriteLine("PASS Notice_KeepBothSurvivesEchoAndCleanSync");
    }

    // Notices design §1: an edit kept over a delete is a record of an automatic decision, so the echo run that reads
    // the restore back from the change feed must not close it.
    private static async Task Notice_EditBeatsDeleteSurvivesEchoAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        fx.A.Seed("a.txt", "edited on A");
        var cloud = await fx.B.ObserveAsync("a.txt", CancellationToken.None);
        await fx.B.DeleteAsync(new SyncAction("seed-delete", 6, ActionKind.Delete, Side.B, "a.txt", null, null, cloud,
            false, null), CancellationToken.None);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        bool Open() => fx.Store.OpenNotices(fx.Pair.PairId)
            .Any(notice => notice.ReasonKey == PlanNotices.EditBeatsDelete.Key);
        Require(Open(), "the edit-beats-delete notice is open after the restore");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(Open(), "the echo run does not close the edit-beats-delete notice");
        Console.WriteLine("PASS Notice_EditBeatsDeleteSurvivesEcho");
    }

    // One policy (PlanNotices.AwaitsAcknowledgement): decision records survive a clean sync of their path; problem and
    // transient reasons still close (decision 26).
    private static async Task Notice_ClosePolicyByReasonAsync()
    {
        NoticeReason[] awaiting =
            [PlanNotices.KeepBoth, PlanNotices.EditBeatsDelete, PlanNotices.MovedVersionKept, PlanNotices.AutoConflict];
        NoticeReason[] transient =
            [PlanNotices.CannotRead, PlanNotices.ContentUnconfirmed, PlanNotices.OutsidePairArea, PlanNotices.NeedDecision];
        foreach (var reason in awaiting.Concat(transient))
        {
            var fx = Build();
            fx.A.Seed("a.txt", "1");
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            fx.Store.AddNotices([new Notice(fx.Pair.PairId, 1, NoticeKind.Info, "a.txt", reason.Detail, reason.Key)]);
            fx.A.Seed("a.txt", "2");
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(report.Done == 1, "the edited file syncs");
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            var open = fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.ReasonKey == reason.Key);
            Require(open == awaiting.Contains(reason),
                reason.Key + (open ? " stayed open" : " closed") + " after a clean sync and its echo run");
        }
        Console.WriteLine("PASS Notice_ClosePolicyByReason");
    }

    private static async Task AccessLost_ScanPropagatesAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        // The first run after the apply scans in full and stores the cursors; the next one reads changes.
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        fx.Provider.AddInventory(fx.Pair.B, new ScriptedInventory(fx.B) { ChangesError = new EndpointAccessException("revoked") });
        await ExpectAsync<EndpointAccessException>(() =>
            run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None));
        Require(fx.Store.ReadRunHistory(fx.Pair.PairId, 1).Single().Outcome == RunOutcomes.Failed,
            "a failed run leaves a retained summary");
        Console.WriteLine("PASS AccessLost_ScanPropagates");
    }

    private static async Task AccessLost_EffectKeepsIntentAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        fx.A.Seed("a.txt", "2");
        fx.WriterB.PutError = new EndpointAccessException("revoked");
        await ExpectAsync<EndpointAccessException>(() =>
            run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None));
        Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 1,
            "an effect stopped by lost access commits nothing and leaves its intent unresolved");
        fx.WriterB.PutError = null;
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Failed == 0 && fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 0,
            "after reconnect the intent is reconciled and the edit syncs");
        await MirrorAsync(fx);
        Console.WriteLine("PASS AccessLost_EffectKeepsIntent");
    }

    private static async Task AccessLost_UncertainStaysUncertainAsync()
    {
        var fx = Build(record: true);
        fx.A.Seed("a.txt", "1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        fx.A.Seed("a.txt", "2");
        fx.WriterB.PutError = new UncertainEffectException("connection dropped mid-upload");
        fx.WriterB.ObserveError = new EndpointAccessException("revoked");
        await ExpectAsync<EndpointAccessException>(() =>
            run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None));
        var intent = fx.Store.UnresolvedIntents(fx.Pair.PairId).Single();
        Require(fx.Recorder!.Outcomes[intent.ActionId] == Outcome.Uncertain,
            "an uncertain write whose observation is denied stays uncertain");
        await ExpectAsync<EndpointAccessException>(() =>
            run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None));
        Require(fx.Recorder.Outcomes[intent.ActionId] == Outcome.Uncertain,
            "reconcile under lost access propagates and leaves the intent uncertain");
        Console.WriteLine("PASS AccessLost_UncertainStaysUncertain");
    }

    private static async Task CliRuntime_CreatePairAwaitsRootObservationAsync()
    {
        var fx = Build();
        var provider = new GatedObserveProvider();
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(provider), "C:\\root");
        var create = runtime.CreatePairAsync("acc", "/cloud", "C:\\local", null, null, true, CancellationToken.None);
        Require(!create.IsCompleted, "pair creation yields while the root observation is pending");
        provider.Release();
        var pair = await create;
        var saved = fx.Store.Pair(pair.Id);
        Require(saved is not null && saved.A.RootObjectId == GatedObserveProvider.RootObjectId &&
            saved.B.RootObjectId == GatedObserveProvider.RootObjectId,
            "the pair is saved with the observed root identities");
        Console.WriteLine("PASS CliRuntime_CreatePairAwaitsRootObservation");
    }

    private static async Task CliRuntime_AccountsArePassedThroughAsync()
    {
        var fx = Build();
        var runtime = new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(fx.Provider), "C:\\root");
        runtime.ConnectionRecords = () =>
        [
            new ProviderConnectionRecord("conn1", SyncProviders.Dropbox, "acct1", "ns1", "ns1", "Me", "me@example.com",
                "Pro", "cred", true,
                new ProviderCapabilityFlags(true, true, true, true, false, false, true, true, true, true, true, false))
        ];
        var accounts = await runtime.LoadAccountsAsync(CancellationToken.None);
        Require(accounts.Count == 1 && accounts.Accounts[0].ConnectionId == "conn1",
            "accounts are read from the connection stores");
        var status = await runtime.LoadStatusAsync(CancellationToken.None);
        Require(status.AccountCount == 1 && status.Accounts.Count == 1, "status reports the pass-through accounts");
        await runtime.DisposeAsync();
        Console.WriteLine("PASS CliRuntime_AccountsArePassedThrough");
    }

    private static async Task Incremental_OneSidedEditAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        var before = fx.Store.Cursor(fx.Pair.A.RootId);
        fx.A.Seed("a.txt", "2");
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Done == 1 && report.Uploaded == 1, "one local edit is uploaded");
        var b = await MapAsync(fx.B);
        Require(b["a.txt"] == Hash("2"), "the cloud copy holds the new content");
        Require(fx.Store.Cursor(fx.Pair.A.RootId) != before, "the local cursor advanced");
        Console.WriteLine("PASS Incremental_OneSidedEdit");
    }

    private static async Task FirstSync_SkippedReasonReportedAsync()
    {
        var fx = Build();
        fx.A.Seed("changed.txt", "reviewed bytes");
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        fx.A.Seed("changed.txt", "different bytes");
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
        Require(report.Skipped == 1 && report.Deferred == 0 && report.Done == 0 && report.Failed == 0,
            "a changed source is counted as skipped, but no longer blocks first-sync completion");
        Require(report.Outcome == RunOutcomes.Partial && fx.Store.FirstSyncDone(fx.Pair.PairId) &&
            fx.Store.OpenNotices(fx.Pair.PairId).All(notice => notice.ReasonKey != "sync-retry"),
            "decision 85: changed preconditions hand off to ordinary sync without a problem notice");
        Require(report.Diagnostics is { } diagnostics && diagnostics.FailureReasons.Count == 0 &&
            diagnostics.SkipReasons.Values.Sum() == 1 &&
            diagnostics.SkipReasons.Keys.Single().Contains("changed", StringComparison.OrdinalIgnoreCase),
            "skipped effects retain their precondition reason in the separate skip histogram, not as failures");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require((await MapAsync(fx.B))["changed.txt"] == Hash("different bytes"),
            "ordinary sync transfers current content after the skipped reviewed source");
        Console.WriteLine("PASS FirstSync_SkippedReasonReported");
    }

    private static async Task Incremental_DeletedIdenticalSkipsRecoveryAsync()
    {
        var fx = Build();
        fx.A.Seed("same.txt", "content");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        var cloud = await fx.B.ObserveAsync("same.txt", CancellationToken.None);
        await fx.B.DeleteAsync(new SyncAction("seed-delete", 6, ActionKind.Delete, Side.B, "same.txt", null, null, cloud,
            false, null), CancellationToken.None);
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Deleted == 1 && fx.WriterA.Deletes == 1 && fx.WriterA.Preserves == 0 &&
            await fx.A.ObserveAsync("same.txt", CancellationToken.None) is null,
            "a Dropbox-deleted local file identical to the deleted revision skips the Recovery copy");
        Console.WriteLine("PASS Incremental_DeletedIdenticalSkipsRecovery");
    }

    private static async Task Incremental_NoChanges_WritesNothingAsync()
    {
        var fx = Build(record: true);
        fx.A.Seed("a.txt", "1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        fx.Recorder!.ResetCounters();
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Done == 0 && report.Outcome == RunOutcomes.Complete, "a steady-state run has nothing to do");
        Require(fx.Recorder.ChangeRows == 0, "a steady-state run writes no sync-state rows (its run summary is history)");
        Console.WriteLine("PASS Incremental_NoChanges_WritesNothing");
    }

    private static async Task Incremental_FailedEffect_RetriedNextRunAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        var clock = new CompletionClock();
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, Settings, clock);
        await FirstSyncAsync(run, fx);
        fx.A.Seed("a.txt", "2");
        fx.B.FailNextWrites = 1;
        var first = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(first.Failed == 1, "the failed effect is reported");
        clock.Now += TimeSpan.FromMinutes(6);
        var second = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(second.Done == 1, "the next run retries and finishes the copy");
        await MirrorAsync(fx);
        Console.WriteLine("PASS Incremental_FailedEffect_RetriedNextRun");
    }

    // Audit A3: the Executor expands a folder delete into child deletes that are not in the plan; their
    // failures still count, so the run is never reported complete.
    private static async Task Incremental_FolderDeleteChildFailureCountedAsync()
    {
        var fx = Build();
        var folder = fx.A.Seed("f", "", ItemKind.Folder);
        var child = fx.A.Seed("f/x.txt", "1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        await fx.A.DeleteAsync(new SyncAction("seed-child", 6, ActionKind.Delete, Side.A, "f/x.txt", null, null, child,
            false, null), CancellationToken.None);
        await fx.A.DeleteAsync(new SyncAction("seed-folder", 6, ActionKind.Delete, Side.A, "f", null, null, folder,
            false, null), CancellationToken.None);
        fx.B.FailNextWrites = 1;
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Failed == 1 && report.Failures.Count == 1, "the expanded child delete's failure is counted");
        Require(report.Outcome != RunOutcomes.Complete, "a run whose child delete failed is not complete");
        Require(report.Diagnostics is { } diagnostics && report.Skipped == 1 &&
            diagnostics.FailureReasons.Values.Sum() == 1 &&
            diagnostics.FailureReasons.GetValueOrDefault(report.Failures[0].Reason!) == 1 &&
            diagnostics.SkipReasons.Values.Sum() == 1 &&
            diagnostics.SkipReasons.Keys.Any(reason => reason.StartsWith("Folder not emptied:", StringComparison.Ordinal)),
            "diagnostics keep the failed child as a failure and the safely skipped parent as a skip reason");
        Console.WriteLine("PASS Incremental_FolderDeleteChildFailureCounted");
    }

    // Decision 85: unconfirmed items survive completion as durable retries, never overwrite authority.
    // Ordinary retries re-hash them and close the notice once the row resolves.
    private static async Task Incremental_UnconfirmedContentReplannedAsync()
    {
        var fx = Build();
        fx.A.Seed("x.txt", "same");
        fx.B.Seed("x.txt", "same");
        var listingA = new ScriptedInventory(fx.A) { Hashless = true, FailHashes = true };
        var listingB = new ScriptedInventory(fx.B) { Hashless = true, FailHashes = true };
        fx.Provider.AddInventory(fx.Pair.A, listingA).AddInventory(fx.Pair.B, listingB);
        var run = fx.Run();
        var applied = await FirstSyncAsync(run, fx);
        Require(applied.Done == 0 && fx.Store.FirstSyncDone(fx.Pair.PairId) && OpenUnconfirmed(fx) == 1 &&
            fx.Store.SyncRetryAt(fx.Pair.PairId, "x.txt") is { Attempts: 1 },
            "decision 85: unconfirmed content completes first sync with a durable retry and visible notice");
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "x.txt") is { Attempts: 2 },
            "unconfirmed item is retried on the next ordinary run");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 &&
            (await MapAsync(fx.A))["x.txt"] == Hash("same") && (await MapAsync(fx.B))["x.txt"] == Hash("same"),
            "unconfirmed content is never overwritten or deleted on either side");
        Require(OpenUnconfirmed(fx) == 1, "failed retries keep one open notice for the same problem");
        listingA.FailHashes = listingB.FailHashes = false;
        var retry = fx.Store.SyncRetryAt(fx.Pair.PairId, "x.txt")!;
        fx.Store.SaveSyncRetry(fx.Pair.PairId, retry with { RetryAfter = DateTimeOffset.MinValue });
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(report.Done == 1 && fx.Store.Baselines(fx.Pair.PairId).Count == 1,
            "a run with no changes re-hashes the unconfirmed row and adopts it");
        Require(OpenUnconfirmed(fx) == 0, "the content-not-confirmed notice closes once the row resolves");
        Console.WriteLine("PASS Incremental_UnconfirmedContentReplanned");
    }

    private static async Task Incremental_DeleteSourceNoticeSettlesWithoutActionAsync()
    {
        var fx = Build();
        fx.A.Seed("x.txt", "same");
        fx.B.Seed("x.txt", "same");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        var noticeId = DeleteSourceGuard.NoticeId(fx.Pair.PairId, "x.txt");
        fx.Store.SaveSyncRetry(fx.Pair.PairId, new SyncRetry("x.txt", "sync", 0,
            "Source still exists", DateTimeOffset.MinValue));
        fx.Store.AddNotices([new Notice(fx.Pair.PairId, 0, NoticeKind.Info, "x.txt",
            "Source still exists; nothing deleted", DeleteSourceGuard.ReasonKey, noticeId)]);
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(report.Done == 0 && fx.Store.SyncRetryAt(fx.Pair.PairId, "x.txt") is null,
            "equal baseline replan settles source-guard debt without an effect");
        Require(fx.Store.NoticeById(fx.Pair.PairId, noticeId) is { Closed: true },
            "equal no-action replan closes the indexed source guard notice");
    }

    private static int OpenUnconfirmed(Fixture fx) =>
        fx.Store.OpenNotices(fx.Pair.PairId).Count(notice => notice.ReasonKey == PlanNotices.ContentUnconfirmed.Key);

    // Review 5: convergence through the real engine. A run applies a move, a delete, a file-to-folder replacement
    // and a keep-both; the next run, which re-reads those writes, has nothing left to do and raises nothing.
    private static async Task Incremental_ConvergesAfterApplyAsync()
    {
        var fx = Build();
        fx.A.Seed("m.txt", "move me");
        var doomed = fx.A.Seed("d.txt", "delete me");
        var replaced = fx.A.Seed("r", "a file, then a folder");
        fx.A.Seed("k.txt", "both edit me");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        await fx.A.MoveAsync(new SyncAction("seed-move", 9, ActionKind.Move, Side.A, "m2.txt", "m.txt", null, null,
            false, null), CancellationToken.None);
        await fx.A.DeleteAsync(new SyncAction("seed-delete", 6, ActionKind.Delete, Side.A, "d.txt", null, null, doomed,
            false, null), CancellationToken.None);
        await fx.A.DeleteAsync(new SyncAction("seed-replace", 26, ActionKind.Delete, Side.A, "r", null, null, replaced,
            false, null), CancellationToken.None);
        fx.A.Seed("r", "", ItemKind.Folder);
        fx.A.Seed("k.txt", "edited on A");
        fx.B.Seed("k.txt", "edited on B");

        var applied = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(applied.Failed == 0 && applied.Moved == 1 && applied.Deleted == 2 && applied.KeptBoth == 2,
            $"the run applies the move, both deletes and the keep-both pair (moved {applied.Moved}, deleted " +
            $"{applied.Deleted}, kept both {applied.KeptBoth}, failed {applied.Failed})");
        var b = await MapAsync(fx.B);
        Require(b.ContainsKey("m2.txt") && !b.ContainsKey("m.txt") && !b.ContainsKey("d.txt") &&
            (await fx.B.ObserveAsync("r", CancellationToken.None))?.Kind == ItemKind.Folder,
            "the cloud side holds the moved file, lacks the deleted one and has r as a folder");
        Require(b.Keys.Count(path => path.StartsWith("k", StringComparison.Ordinal)) == 2 &&
            (await MapAsync(fx.A)).Keys.Count(path => path.StartsWith("k", StringComparison.Ordinal)) == 2,
            "each side keeps its own k.txt and a copy of the other side's version");
        var noticesBefore = fx.Store.OpenNotices(fx.Pair.PairId).Select(notice => notice.NoticeId).ToHashSet();

        var next = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(next.Done == 0 && next.Failed == 0 && next.Held == 0, "the next run has nothing left to do");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).All(notice => noticesBefore.Contains(notice.NoticeId)),
            "the next run raises no new notice");
        Console.WriteLine("PASS Incremental_ConvergesAfterApply");
    }

    private static async Task Reconcile_ExcludedIntentSettlesAsync()
    {
        var fx = Build();
        var item = fx.A.Seed("visible.txt", "same");
        fx.B.Seed("visible.txt", "same");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        fx.Store.SavePair(fx.Pair with { Excluded = ["private"] });
        fx.Store.RecordIntent(fx.Pair.PairId, new SyncAction("excluded-pending", 1, ActionKind.CopyNew,
            Side.B, "private/ghost.txt", null, item, null, false, "excluded-key"));
        fx.Store.RecordIntent(fx.Pair.PairId, new SyncAction("visible-pending", 1, ActionKind.CopyNew,
            Side.B, "ghost.txt", null, item, null, false, "visible-key"));
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 0,
            "reconcile settles included and excluded intents without applying excluded work");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == SyncRun.ExcludedIntentNoticeKey) == 1,
            "excluded work raises one durable pair notice");
        fx.Store.SavePair(fx.Pair);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 0,
            "re-including the path resumes its outstanding reconciliation");
    }

    private static async Task Reconcile_Uncertain_StopsBeforeInventoryAsync()
    {
        // Decision 85 supersedes the pair-wide stop: protect the uncertain item's footprint only.
        var fx = Build();
        fx.Store.MarkFirstSyncDone(fx.Pair.PairId);
        fx.WriterB.OnRead = path => { if (path == "x.txt") throw new IOException("permanent item failure"); };
        fx.Store.RecordIntent(fx.Pair.PairId, new SyncAction("pend", 2, ActionKind.Update, Side.B, "x.txt", null, null,
            null, false, "k"));
        for (var index = 0; index < 1000; index++) fx.A.Seed($"sibling-{index}.txt", "safe content");
        var run = fx.Run();
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Outcome == RunOutcomes.Partial && report.Uncertain == 1,
            "one unresolved effect remains explicit without aborting the run");
        Require(report.Done == 1000 && fx.WriterA.Calls == 0, "all 1000 independent copies complete");
        Require(fx.Store.Cursor(fx.Pair.A.RootId) is not null && fx.Store.Cursor(fx.Pair.B.RootId) is not null,
            "safe cursor checkpoints advance despite the protected uncertain item");
        Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Single().ActionId == "pend",
            "the uncertain effect is retained, never converted to deletion permission");
        var outage = Build();
        outage.Store.MarkFirstSyncDone(outage.Pair.PairId);
        var original = outage.A.Seed("protected/x.txt", "never delete");
        outage.B.Seed("protected/x.txt", "never delete");
        outage.WriterB.ObserveFails = true;
        outage.Store.RecordIntent(outage.Pair.PairId, new SyncAction("outage", 2, ActionKind.Update, Side.B,
            original.Path, null, original, original, false, "outage-key"));
        var unavailable = await outage.Run().IncrementalAsync(outage.Pair.PairId, new RecordingProgress(), default);
        Require(unavailable.Uncertain > 0 && outage.Store.UnresolvedIntents(outage.Pair.PairId).Count == 1,
            "endpoint-wide observation outage retains uncertain submission authority");
        Require(outage.WriterA.Deletes == 0 && outage.WriterB.Deletes == 0 && outage.WriterA.Puts == 0 && outage.WriterB.Puts == 0,
            "endpoint-wide uncertainty never overwrites or deletes its protected footprint");
        Require(await outage.A.ObserveAsync(original.Path, default) is not null &&
            await outage.B.ObserveAsync(original.Path, default) is not null, "both survivors remain during observation outage");
        Console.WriteLine("PASS Reconcile_Uncertain_IndependentProgress");
    }

    // Every uncertain intent is an independent observation. They must overlap (bounded), still all resolve, persist
    // each resolution as it completes, and report completions — never a zero total — as they happen.
    private static async Task Reconcile_UncertainItemsConcurrentlyAsync()
    {
        var fx = Build();
        fx.Store.MarkFirstSyncDone(fx.Pair.PairId);
        const int count = 32;
        var intents = new List<SyncAction>(count);
        for (var index = 0; index < count; index++)
        {
            var path = $"pending-{index:D2}.txt";
            var source = fx.A.Seed(path, "content-" + index, versionId: "v1");
            var action = new SyncAction("pending-" + index, 2, ActionKind.CopyNew, Side.B, path, null, source,
                null, false, "pending-" + index);
            fx.Store.RecordIntent(fx.Pair.PairId, action);
            intents.Add(action);
        }
        fx.WriterB.ObserveLatency = TimeSpan.FromMilliseconds(25);
        var progress = new CompletionProgress();

        var results = await fx.Executor.ReconcileAsync(intents, 7, fx.WriterA, fx.WriterB,
            CancellationToken.None, progress);

        Require(results.Count == count && results.Select(r => r.ActionId).SequenceEqual(intents.Select(i => i.ActionId)),
            "every resolution is returned, in the caller's order");
        Require(results.All(r => r.Outcome == Outcome.Skipped), "an absent copy resolves Skipped, never Uncertain");
        Require(fx.WriterB.MaxConcurrentObservations > 1, "uncertain items are observed concurrently");
        Require(fx.WriterB.MaxConcurrentObservations <= Executor.ReconcileObservationParallelism,
            $"observation concurrency stays within its bound ({fx.WriterB.MaxConcurrentObservations} > " +
            $"{Executor.ReconcileObservationParallelism})");
        Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 0,
            "every resolution is persisted as it completes");
        Require(progress.Count == count, "progress counts each actual completion");
        Require(progress.Last == count, "the last reported count is the real total");
        Console.WriteLine("PASS Reconcile_UncertainItemsConcurrently");
    }

    // A saved layout that clashes with another pair is refused by preview, apply and run before reconcile, so a
    // pending effect is not finished and nothing is read or written.
    private static async Task StoredClash_RefusedBeforeReconcileAsync()
    {
        var fx = Build();
        fx.Store.SavePair(Pair("p2", "C:\\A\\", "/elsewhere"));
        fx.Store.RecordIntent(fx.Pair.PairId, new SyncAction("pend", 2, ActionKind.Update, Side.B, "x.txt", null, null,
            null, false, "k"));
        var run = fx.Run();
        Require(await RefusalReasonAsync(() => run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(),
            CancellationToken.None)) == PairAdmissionReasons.SameRoot, "preview refuses a saved clashing layout");
        Require(await RefusalReasonAsync(() => run.ApplyAsync(fx.Pair.PairId, new Plan(fx.Pair.PairId, 0, [], []),
            new RecordingProgress(), CancellationToken.None)) == PairAdmissionReasons.SameRoot,
            "apply refuses a saved clashing layout");
        fx.Store.MarkFirstSyncDone(fx.Pair.PairId);
        Require(await RefusalReasonAsync(() => run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(),
            CancellationToken.None)) == PairAdmissionReasons.SameRoot, "run refuses a saved clashing layout");
        Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 1 && fx.WriterA.Calls == 0 &&
            fx.WriterB.Calls == 0, "the refusal comes before reconcile: the pending intent is untouched");
        Console.WriteLine("PASS StoredClash_RefusedBeforeReconcile");
    }

    // Legacy records without root ids (enabled and disabled) next to a disjoint pair never block that pair.
    private static async Task LegacyEmptyIdPairs_DoNotBlockDisjointPairAsync()
    {
        var fx = Build();
        fx.Store.SavePair(LegacyPair("p2", "C:\\legacy-on", "/legacy-on", enabled: true));
        fx.Store.SavePair(LegacyPair("p3", "C:\\legacy-off", "/legacy-off", enabled: false));
        fx.A.Seed("a.txt", "1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && report.Outcome == RunOutcomes.Complete,
            "a valid pair previews, applies and runs next to legacy pairs without root ids");
        Console.WriteLine("PASS LegacyEmptyIdPairs_DoNotBlockDisjointPair");
    }

    private static PairDef LegacyPair(string id, string localRoot, string remoteRoot, bool enabled) => new(id, id,
        new RootDef("ra-" + id, SyncProviders.Local, null, localRoot, string.Empty),
        new RootDef("rb-" + id, SyncProviders.Dropbox, "acc", remoteRoot, string.Empty), [], [], enabled);

    private static async Task<NoticeReason?> RefusalReasonAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (SyncRunRefusedException error)
        {
            return error.Reason;
        }
        throw new InvalidOperationException("Expected a run refusal.");
    }

    private static async Task KillMidApply_NoDuplicatesNoPartialsAsync()
    {
        var fx = Build(record: true);
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        for (var index = 0; index < 20; index++) fx.A.Seed($"f{index:D2}.txt", "content" + index);
        fx.Recorder!.CrashOnCommit = 6;
        var interrupted = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(interrupted.Uncertain > 0 || interrupted.Failed > 0, "injected persistence interruption leaves unfinished work, not an escaped run");
        foreach (var retry in fx.Store.ReadSyncRetries(fx.Pair.PairId))
            fx.Store.SaveSyncRetry(fx.Pair.PairId, retry with { RetryAfter = DateTimeOffset.MinValue });
        fx.Recorder.CrashOnCommit = -1;
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var b = await MapAsync(fx.B);
        Require(b.Count == 20, "the cloud side holds all twenty files");
        Require(b.Keys.All(path => !path.Contains(" version ", StringComparison.Ordinal)),
            "no keep-both labels were created");
        Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 0, "no intent is left unresolved");
        Require(fx.WriterB.Puts == 20, "nothing was written twice");
        await MirrorAsync(fx);
        Console.WriteLine("PASS KillMidApply_NoDuplicatesNoPartials");
    }

    private static async Task KillBeforeEffect_SkippedAndReplannedAsync()
    {
        var fx = Build(record: true);
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        for (var index = 0; index < 20; index++) fx.A.Seed($"g{index:D2}.txt", "content" + index);
        fx.Recorder!.CrashAfterIntent = 6;
        var interrupted = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(interrupted.Uncertain > 0 || interrupted.Failed > 0, "injected persistence interruption leaves unfinished work, not an escaped run");
        foreach (var retry in fx.Store.ReadSyncRetries(fx.Pair.PairId))
            fx.Store.SaveSyncRetry(fx.Pair.PairId, retry with { RetryAfter = DateTimeOffset.MinValue });
        fx.Recorder.CrashAfterIntent = -1;
        await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var b = await MapAsync(fx.B);
        Require(b.Count == 20, "all twenty files arrive after the re-plan");
        Require(fx.WriterB.Puts == 20, "nothing was written twice");
        await MirrorAsync(fx);
        Console.WriteLine("PASS KillBeforeEffect_SkippedAndReplanned");
    }

    private static async Task Stop_KeepsFinishedWorkAsync()
    {
        var fx = Build();
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        for (var index = 0; index < 100; index++) fx.A.Seed($"h{index:D3}.txt", "content" + index);
        fx.B.Latency = TimeSpan.FromMilliseconds(20);
        // Park every effect after the first and stop once a second one begins: with one worker that signal means the
        // first effect is already committed, so the stop lands mid-run with finished work kept, never before a put.
        fx.WriterB.ParkAfterFirstEffect = true;
        var before = fx.Store.Cursor(fx.Pair.A.RootId);
        var task = run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        await fx.WriterB.FirstEffectCommitted.Task.WaitAsync(GateTimeout);
        await run.StopAsync();
        var report = await task;
        Require(report.Outcome == RunOutcomes.Stopped, "a stopped run reports stopped");
        Require(fx.Store.Cursor(fx.Pair.A.RootId) == before, "a stopped run does not advance the cursor");
        Require(fx.Store.Baselines(fx.Pair.PairId).Count > 0, "finished work is kept");
        Require(fx.Store.ReadRunHistory(fx.Pair.PairId, 1).Single().Outcome == RunOutcomes.Stopped,
            "a stopped run leaves a retained summary");
        Console.WriteLine("PASS Stop_KeepsFinishedWork");
    }

    private static async Task Progress_ThrottledAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        var settings = V2Settings.Default with { ProgressInterval = TimeSpan.FromHours(1) };
        var progress = new RecordingProgress();
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, settings, TimeProvider.System);
        await run.PreviewFirstSyncAsync(fx.Pair.PairId, progress, CancellationToken.None);
        // RQ-090: every phase prints a start line (a phase report) and an end line ("<phase> done: ...").
        var phases = progress.Reports.Where(report => report.Message is null).Select(report => report.Phase)
            .Distinct(StringComparer.Ordinal).ToArray();
        Require(phases.Length > 0, "a run reports its phases");
        foreach (var phase in phases)
            Require(progress.Reports.Any(report => report.Message is { } message &&
                    message.StartsWith(phase + " done", StringComparison.Ordinal)),
                $"phase '{phase}' prints an end line with its counts and elapsed time");
        Console.WriteLine("PASS Progress_Throttled");
    }

    // The apply line counts finished planned actions only. A folder delete the Executor expands over live children
    // yields child results that are not in the plan; those are reported as a separate suffix, never added to done.
    private static async Task Progress_CountsPlannedActionsOnlyAsync()
    {
        var fx = Build();
        fx.A.Seed("f", "", ItemKind.Folder);
        fx.A.Seed("f/a.txt", "a");
        fx.A.Seed("f/b.txt", "b");
        var run = fx.Run();
        await SeedPartialFirstSyncBaselinesAsync(run, fx);
        // Real baselines retain each source identity; remove the actual source children then their folder.
        foreach (var path in new[] { "f/a.txt", "f/b.txt", "f" })
        {
            var source = await fx.A.ObserveAsync(path, default);
            await fx.A.DeleteAsync(new SyncAction("remove-source-" + path, 6, ActionKind.Delete,
                Side.A, path, null, null, source, false, null), default);
        }
        var newFile = fx.A.Seed("new.txt", "1");
        // Fill the store's inventory, so the Executor expands the folder delete over its two live children.
        var preview = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var folder = await fx.B.ObserveAsync("f", CancellationToken.None);
        var plan = new Plan(fx.Pair.PairId, preview.Gen,
        [
            new SyncAction("copy-new", 1, ActionKind.CopyNew, Side.B, "new.txt", null, newFile, null, false, null),
            new SyncAction("delete-f", 6, ActionKind.Delete, Side.B, "f", null, null, folder, true, null)
            {
                FrozenVictimSignature = FolderDelete.Signature(FolderDelete.LiveDescendants(fx.Store, fx.Pair.B.RootId, "f"))
            }
        ], []);
        // Reviewed membership does not confer explicit authority. These ordinary deletes must use their
        // actual baseline identities and verify the absent source, including each expanded child.
        var reviewed = new[] { "f", "f/a.txt", "f/b.txt" }
            .Select(path => FolderDelete.VictimKey(Side.B, path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sink = new RecordingProgress();
        await run.ApplyAsync(fx.Pair.PairId, plan, sink, CancellationToken.None, reviewed);
        Require(fx.WriterB.Deletes == 3, "the reviewed progress fixture actually removes the folder and both children");

        var applying = sink.Reports.Where(report => report.Phase == RunPhases.Applying).ToArray();
        Require(applying.Length > 0, "the apply reports progress");
        Require(applying.All(report => report.Done <= report.Total),
            "applying progress never reports more done than its total");
        Require(applying[^1].Done == 2 && applying[^1].Total == 2,
            "applying ends at the two planned actions, never the expanded children");
        Require(applying.Any(report => report.Extra == 2),
            "the expanded folder's child results are reported beside the planned total");
        Console.WriteLine("PASS Progress_CountsPlannedActionsOnly");
    }

    // Model a first sync interrupted after committed effects but before run-level completion. Execute real
    // conditional writes and baseline commits without calling the run's first-sync completion transition.
    private static async Task SeedPartialFirstSyncBaselinesAsync(SyncRun run, Fixture fx)
    {
        var prepared = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        await foreach (var result in fx.Executor.ApplyAsync(prepared, fx.WriterA, fx.WriterB, default))
            Require(result.Outcome == Outcome.Done, "partial first-sync setup commits its real effects");
        Require(!fx.Store.FirstSyncDone(fx.Pair.PairId) && fx.Store.Baselines(fx.Pair.PairId).Count > 0,
            "partial first-sync setup retains real baselines without marking first sync complete");
    }

    // First-sync reviewed membership is not explicit authority for ordinary rule-6 propagation.
    private static async Task ReviewedPropagationStillRequiresSourceAbsenceAsync()
    {
        foreach (var hasBaseline in new[] { false, true })
        {
            var fx = Build();
            var run = fx.Run();
            if (hasBaseline)
            {
                fx.A.Seed("x.txt", "same");
                await SeedPartialFirstSyncBaselinesAsync(run, fx);
            }
            else fx.B.Seed("x.txt", "same");
            var preview = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
            var victim = await fx.B.ObserveAsync("x.txt", default);
            var baselinesBefore = fx.Store.Baselines(fx.Pair.PairId).ToArray();
            var plan = new Plan(fx.Pair.PairId, preview.Gen,
                [new SyncAction("reviewed-rule6", 6, ActionKind.Delete, Side.B, "x.txt", null, null,
                    victim, true, null)], []);
            var reviewed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { FolderDelete.VictimKey(Side.B, "x.txt") };
            var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), default, reviewed);
            Require(fx.WriterB.Deletes == 0 && await fx.B.ObserveAsync("x.txt", default) is not null,
                "reviewed rule-6 membership never bypasses source-present or missing-identity protection");
            Require(fx.Store.Baselines(fx.Pair.PairId).SequenceEqual(baselinesBefore),
                "a blocked reviewed propagation retains its baseline");
            if (hasBaseline)
            {
                Require(await fx.A.ObserveAsync("x.txt", default) is not null && report.Skipped > 0,
                    "the present historical source causes reviewed first-sync propagation to skip");
                Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice =>
                    notice.Path == "x.txt" && notice.ReasonKey == DeleteSourceGuard.ReasonKey),
                    "a reviewed propagation blocked by a present source has a blocked-delete notice");
            }
            else
            {
                Require(fx.Store.SyncRetryAt(fx.Pair.PairId, "x.txt") is { MissingDeleteIdentityReview: true },
                    "a reviewed rule-6 victim without source identity still requires per-item review");
            }
        }
        Console.WriteLine("PASS ReviewedPropagationStillRequiresSourceAbsence");
    }

    // A terminal result reported twice for the same action still advances the planned count once.
    private static void Tally_DuplicateTerminalResultCountsOnce()
    {
        var fx = Build();
        var tally = new SyncRun.Tally();
        var action = new SyncAction("dup", 1, ActionKind.CopyNew, Side.B, "x.txt", null, null, null, false, null);
        tally.Plan([action]);
        var result = new ActionResult("dup", Outcome.Done, null, null, null, null);
        tally.Add(fx.Pair, action, result);
        tally.Add(fx.Pair, action, result);
        Require(tally.PlannedDone == 1 && tally.Total == 1 && tally.ExtraDone == 0,
            "a duplicate terminal result is counted once");
        Console.WriteLine("PASS Tally_DuplicateTerminalResultCountsOnce");
    }

    // RQ-090/RQ-130: a provider process message (a read retry) published through the shared byte-progress seam
    // reaches the run's own progress sink, so a v2 run is not silent while it waits on the provider.
    private static async Task Activity_ProcessMessagesReachSinkAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        fx.Provider.AddInventory(fx.Pair.A, new ProcessMessageInventory(fx.A));
        var progress = new RecordingProgress();
        await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, progress, CancellationToken.None);
        Require(progress.Reports.Any(report => report.Message is { } message &&
                message.Contains("retry Dropbox read", StringComparison.Ordinal)),
            "a provider process message reaches the run's progress sink");
        Console.WriteLine("PASS Activity_ProcessMessagesReachSink");
    }

    // RQ-090: a run that writes nothing for the heartbeat interval still says it is working.
    private static async Task Activity_HeartbeatWhileQuietAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        var gate = new GatedScanInventory(fx.A);
        fx.Provider.AddInventory(fx.Pair.A, gate);
        var settings = V2Settings.Default with
        {
            ProgressInterval = TimeSpan.Zero, MaxWorkers = 1,
            HeartbeatInterval = TimeSpan.FromMilliseconds(40), HeartbeatCheckInterval = TimeSpan.FromMilliseconds(20)
        };
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, settings, TimeProvider.System);
        var progress = new RecordingProgress();
        var pending = run.PreviewFirstSyncAsync(fx.Pair.PairId, progress, CancellationToken.None);
        await gate.Entered;
        var heartbeat = SpinUntil(() => progress.Reports.Any(report =>
            report.Message is { } message && message.StartsWith("Still working:", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
        gate.Release();
        await pending;
        Require(heartbeat, "a quiet run emits a heartbeat line while it is still working");
        Console.WriteLine("PASS Activity_HeartbeatWhileQuiet");
    }

    // RQ-090: the run's one sweeper warns once a tracked operation passes its threshold, naming it and its age.
    private static void Operations_SweepWarns()
    {
        var settings = V2Settings.Default with
        {
            OperationSweepInterval = TimeSpan.FromMilliseconds(20),
            OperationWarningRepeat = TimeSpan.FromMilliseconds(20),
            SlowStoreThreshold = TimeSpan.FromMilliseconds(30)
        };
        var progress = new RecordingProgress();
        using var activity = RunActivity.Begin(progress, settings, TimeProvider.System);
        using (RunOperations.Begin(RunOperationKind.Store, "state store"))
        {
            var warned = SpinUntil(() => progress.Reports.Any(report => report.Message is { } message &&
                    message.StartsWith("Still waiting: Store state store", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3));
            Require(warned, "the sweeper warns about a slow operation and names it");
        }
        Console.WriteLine("PASS Operations_SweepWarns");
    }

    // RQ-090: an operation that finishes before its threshold is never warned about.
    private static async Task Operations_QuietWhenFastAsync()
    {
        var settings = V2Settings.Default with
        {
            OperationSweepInterval = TimeSpan.FromMilliseconds(20), SlowStoreThreshold = TimeSpan.FromSeconds(10)
        };
        var progress = new RecordingProgress();
        using var activity = RunActivity.Begin(progress, settings, TimeProvider.System);
        using (RunOperations.Begin(RunOperationKind.Store, "state store")) { /* completes at once */ }
        await Task.Delay(150);
        Require(!progress.Reports.Any(report => report.Message is { } message &&
                message.StartsWith("Still waiting:", StringComparison.Ordinal)),
            "a fast operation is never warned about");
        Console.WriteLine("PASS Operations_QuietWhenFast");
    }

    // RQ-090: tracking sits on hot paths, so registering and unregistering must be cheap.
    private static void Operations_RegistrationCheap()
    {
        var settings = V2Settings.Default with { OperationSweepInterval = TimeSpan.FromHours(1) };
        using var activity = RunActivity.Begin(new RecordingProgress(), settings, TimeProvider.System);
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < 100_000; index++)
            using (RunOperations.Begin(RunOperationKind.Http, "/2/files/list_folder")) { }
        watch.Stop();
        Require(watch.ElapsedMilliseconds < 200,
            $"100k register/unregister operations take under 200 ms (took {watch.ElapsedMilliseconds} ms)");
        Console.WriteLine($"PASS Operations_RegistrationCheap: 100k in {watch.ElapsedMilliseconds} ms");
    }

    // RQ-130: every line is recorded with an ISO timestamp to a .log and a JSON-lines twin, names never collide,
    // rotation keeps whole pairs within a combined budget, and a single run's log is capped.
    private static void RunLog_WritesAndRotatesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "runlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            string textPath, jsonPath;
            using (var log = RunLog.Open(root, "pair one", "run", verbose: false))
            {
                Require(SpinUntil(() => log.FileAvailable, TimeSpan.FromSeconds(2)) && log.Path is not null &&
                        log.Path.Contains("pair_one", StringComparison.Ordinal),
                    "a run log opens, naming the pair and command safely");
                log.Summary("Uploading: 1/2 files (0/0)");
                log.Detailed("sync dropbox: read 5 pages");
                textPath = log.Path!;
                jsonPath = log.JsonPath!;
            }
            var text = File.ReadAllText(textPath);
            var lines = File.ReadAllLines(jsonPath);
            Require(text.Contains("Uploading: 1/2 files", StringComparison.Ordinal),
                "the summary line is recorded in the .log");
            Require(text.Contains("sync dropbox: read 5 pages", StringComparison.Ordinal),
                "the detailed trace is recorded in the .log");
            Require(lines.Length >= 2 && lines.All(line => line.Contains("\"ts\"", StringComparison.Ordinal)),
                "the .jsonl twin holds one ISO-stamped record per line");
            // Collision-proof: two runs opened in the same instant never share a name.
            using (var second = RunLog.Open(root, "pair one", "run", verbose: false))
                Require(second.Path is not null && !string.Equals(second.Path, textPath, StringComparison.Ordinal),
                    "two run logs never collide on one name");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        // Rotation counts a .log and its .jsonl twin together against the budget, and deletes them together.
        var rotateRoot = Path.Combine(Path.GetTempPath(), "runlog-rotate-" + Guid.NewGuid().ToString("N"), "v2", "logs");
        try
        {
            Directory.CreateDirectory(rotateRoot);
            for (var index = 0; index < 6; index++)
            {
                File.WriteAllText(Path.Combine(rotateRoot, $"2026010{index + 1}-120000-000-aaaaaaaa-p-run.log"), new string('x', 16));
                File.WriteAllText(Path.Combine(rotateRoot, $"2026010{index + 1}-120000-000-aaaaaaaa-p-run.jsonl"), new string('y', 16));
            }
            RunLog.Rotate(rotateRoot, maximumFiles: 100, maximumBytes: 48);
            Require(Directory.GetFiles(rotateRoot, "*.log").Length == 1,
                "rotation counts each run's combined bytes against the budget");
            Require(Directory.GetFiles(rotateRoot, "*.jsonl").Length == 1,
                "rotation deletes a run's .jsonl twin with its .log");
        }
        finally
        {
            try { Directory.Delete(rotateRoot, recursive: true); } catch (IOException) { }
        }

        // A single run's log is capped: one "log truncated" line, then file output stops.
        var capRoot = Path.Combine(Path.GetTempPath(), "runlog-cap-" + Guid.NewGuid().ToString("N"));
        try
        {
            string capPath;
            using (var log = RunLog.Open(capRoot, "p", "run", verbose: false))
            {
                log.ByteLimit = 200;
                for (var index = 0; index < 200; index++) log.Summary("line " + index + " " + new string('z', 40));
                capPath = log.Path!;
            }
            var capped = File.ReadAllText(capPath);
            Require(File.ReadAllText(Path.ChangeExtension(capPath, ".jsonl")).Contains("run log truncated"),
                "truncation is recorded in both formats");
            Require(capped.Contains("run log truncated", StringComparison.Ordinal),
                "a run's log is capped with one truncation line");
            Require(!capped.Contains("line 199", StringComparison.Ordinal),
                "file output stops after the truncation line");
        }
        finally
        {
            try { Directory.Delete(capRoot, recursive: true); } catch (IOException) { }
        }

        // File logging is best-effort: a folder that cannot be created leaves the run continuing with no file.
        var blocker = Path.Combine(Path.GetTempPath(), "runlog-block-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a folder");
        try
        {
            using var log = RunLog.Open(blocker, "p", "run", verbose: false);
            Require(SpinUntil(() => log.Failed, TimeSpan.FromSeconds(2)) && !log.FileAvailable,
                "a run log that cannot open its file continues without one");
            log.Summary("a summary line still goes to the sink");
            log.Detailed("a trace line still respects verbosity");
        }
        finally
        {
            try { File.Delete(blocker); } catch (IOException) { }
        }
        Console.WriteLine("PASS RunLog_WritesAndRotates");
    }

    // RQ-090: a report after disposal is a no-op, and disposal never throws.
    private static void Operations_DisposedNoOp()
    {
        var progress = new RecordingProgress();
        var activity = RunActivity.Begin(progress, V2Settings.Default, TimeProvider.System);
        activity.Dispose();
        activity.Dispose();
        IProgress<RunProgress> sink = activity;
        sink.Report(new RunProgress("Late", null, 1, 2) { Message = "after dispose" });
        Require(progress.Reports.Count == 0, "a report after disposal is a no-op");
        RunOperations.Begin(RunOperationKind.Http, "/x").Dispose();
        Console.WriteLine("PASS Operations_DisposedNoOp");
    }

    // RQ-090: a phase with an unknown total shows "N so far" and its end line carries the count, never "done: 0".
    private static void Progress_UnknownTotalAsync()
    {
        var progress = new RecordingProgress();
        var throttle = new ProgressThrottle(progress, TimeProvider.System, TimeSpan.Zero);
        throttle.Report("Reading Dropbox", null, 0, 0);
        throttle.Report("Reading Dropbox", null, 42, 0);
        throttle.Flush();
        Require(progress.Reports.Any(report => report.Message is { } message && message.Contains("42 so far", StringComparison.Ordinal)),
            "an unknown total shows 'N so far'");
        var end = progress.Reports[^1];
        Require(end.Message is { } line && line.Contains("done: 42", StringComparison.Ordinal) &&
                !line.Contains("done: 0", StringComparison.Ordinal),
            "the end line carries the real count, never zero");
        Console.WriteLine("PASS Progress_UnknownTotal");
    }

    // RQ-090: duplicate suppression applies to ordinary lines only; a retry is never suppressed.
    private static async Task Activity_DuplicateSuppressionAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        fx.Provider.AddInventory(fx.Pair.A, new ScriptedMessageInventory(fx.A,
            ["Checking remote item", "Checking remote item",
             "Waiting for network; retry Dropbox read", "Waiting for network; retry Dropbox read"]));
        var progress = new RecordingProgress();
        await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, progress, CancellationToken.None);
        Require(progress.Reports.Count(report => report.Message == "Checking remote item") == 1,
            "a duplicate ordinary line is suppressed");
        Require(progress.Reports.Count(report => report.Message == "Waiting for network; retry Dropbox read") == 2,
            "a retry line is never suppressed");
        Console.WriteLine("PASS Activity_DuplicateSuppression");
    }

    // RQ-090: the app mapping keeps the phase identity stable and carries the display text separately, so stage timing
    // is never reset by a notice or heartbeat.
    private static void Mapping_StablePhaseWithDetail()
    {
        var pair = new SyncPair("pair-progress", "Progress fixture", Path.GetTempPath(), 1, 1, "/fixture", "id:fixture");
        var progress = new RunProgress("Reading Dropbox", "a.txt", 5, 0) { Message = "Reading Dropbox: 5 so far" };
        var mapped = RunResultMapping.ToProgress(pair, progress, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Require(mapped.State == "Reading Dropbox" &&
                mapped.Comparison is { Phase: "Reading Dropbox", Detail: "Reading Dropbox: 5 so far" },
            "the phase identity is stable and the display text rides in Detail");
        Console.WriteLine("PASS Mapping_StablePhaseWithDetail");
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    private static void Include_NestedPairExcludedAndIgnoreRules()
    {
        var outer = Pair("outer", "C:\\C", "/C");
        var inner = Pair("inner", "C:\\C\\B", "/C/B");
        var all = new[] { outer, inner };
        Require(!PairInclude.For(outer, Side.A, all, [])("B/x.txt"),
            "the outer pair never sees the nested pair's local area");
        Require(!PairInclude.For(outer, Side.B, all, [])("B/x.txt"),
            "the outer pair never sees the nested pair's cloud area");
        Require(PairInclude.For(outer, Side.A, all, [])("other.txt"), "an ordinary local path is included");
        var excluded = outer with { Excluded = ["ex"] };
        Require(!PairInclude.For(excluded, Side.A, all, [])("ex/y.txt"), "an excluded folder is hidden");
        var rules = PairInclude.IgnoreRules(outer with { IgnoreRules = ["file-extension:tmp; FolderName:cache"] });
        Require(rules.Count == 2, "stored ignore rules parse through the ignore policy");
        Require(SyncIgnorePolicy.IsIgnored("x.tmp", false, rules), "an ignore rule hides a matching file");
        Console.WriteLine("PASS Include_NestedPairExcludedAndIgnoreRules");
    }

    private static async Task SafetyFilter_DropsOutsidePairAreaAsync()
    {
        var fx = Build();
        var folder = fx.A.Seed("ex", "", ItemKind.Folder);
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        // A subfolder is excluded after the folder synced; deleting the folder must not reach the excluded part.
        fx.Store.SavePair(fx.Pair with { Excluded = ["ex/keep"] });
        await fx.A.DeleteAsync(new SyncAction("seed-delete", 6, ActionKind.Delete, Side.A, "ex", null, null, folder,
            false, null), CancellationToken.None);
        var callsBefore = fx.WriterB.Calls;
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(fx.WriterB.Calls == callsBefore, "a delete of a folder holding an excluded subtree is dropped");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Rule == 33),
            "the drop is reported as a notice");
        Console.WriteLine("PASS SafetyFilter_DropsOutsidePairArea");
    }

    // Product-owned folders inside a local root (data, spool, Recovery) are protected like an exclusion on both sides.
    private static async Task ProductOwned_RemoteOnlyChildNotPlannedAsync()
    {
        var fx = Build();
        fx.B.Seed("Recovery", "", ItemKind.Folder);
        fx.B.Seed("Recovery/x.txt", "1");
        fx.B.Seed(".syncshark-recovery-p1", "", ItemKind.Folder);
        fx.B.Seed(".syncshark-recovery-p1/y.txt", "1");
        fx.B.Seed("a.txt", "1");
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, Settings, TimeProvider.System, _ => ["Recovery"]);
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(plan.Actions.Any(action => action.Path == "a.txt"), "an ordinary remote-only file is planned");
        Require(!plan.Actions.Any(action => PairPath.IsSelfOrUnder(action.Path, "Recovery")),
            "a remote-only file under a product-owned folder is never planned: " + Decisions(plan));
        Require(!plan.Actions.Any(action => action.Path.StartsWith(".syncshark-", StringComparison.Ordinal)),
            "a remote-only file under a Sync Shark artifact name is never planned: " + Decisions(plan));
        Console.WriteLine("PASS ProductOwned_RemoteOnlyChildNotPlanned");
    }

    private static async Task ProductOwned_AncestorMoveAndDeleteDroppedAsync()
    {
        foreach (var move in new[] { false, true })
        {
            var fx = Build();
            var folder = fx.A.Seed("data", "", ItemKind.Folder);
            var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, Settings, TimeProvider.System,
                _ => ["data/Recovery"]);
            await FirstSyncAsync(run, fx);
            if (move)
                await fx.A.MoveAsync(new SyncAction("seed-move", 25, ActionKind.Move, Side.A, "data2", "data", null,
                    null, false, null), CancellationToken.None);
            else
                await fx.A.DeleteAsync(new SyncAction("seed-delete", 6, ActionKind.Delete, Side.A, "data", null, null,
                    folder, false, null), CancellationToken.None);
            var callsBefore = fx.WriterB.Calls;
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            var kind = move ? "move" : "delete";
            Require(fx.WriterB.Calls == callsBefore, "a " + kind + " of a folder holding a product-owned folder is dropped" +
                $" (moves {fx.WriterB.Moves}, deletes {fx.WriterB.Deletes}, mkdirs {fx.WriterB.MkDirs}, puts {fx.WriterB.Puts})");
            Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Rule == 33),
                "the dropped " + kind + " is reported as a notice");
        }
        Console.WriteLine("PASS ProductOwned_AncestorMoveAndDeleteDropped");
    }

    // A `pair rescan` clears a root's stored cursor: that root's next run scans in full, and a full scan carries
    // no journal delete, so a deletion made before the rescan is planned as a delete only from the baseline. An
    // unchanged file alongside the deleted one must still produce no other effect.
    private static async Task Incremental_FullRescanAfterForgetCursorAsync()
    {
        var fx = Build();
        var item = fx.A.Seed("x.txt", "same");
        fx.B.Seed("x.txt", "same");
        fx.A.Seed("keep.txt", "keep");
        fx.B.Seed("keep.txt", "keep");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        // The first sync's preview commits no cursor, so one run settles both roots onto a stored cursor.
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(fx.Store.Cursor("ra") is not null && fx.Store.Cursor("rb") is not null,
            "a settled pair holds a cursor for each root");
        await fx.A.DeleteAsync(new SyncAction("seed-delete", 6, ActionKind.Delete, Side.A, "x.txt", null, null, item,
            false, null), CancellationToken.None);
        var fullBefore = fx.A.FullCalls;
        var deltaBefore = fx.A.DeltaCalls;
        await run.RequestRescanAsync(["ra"], CancellationToken.None);
        Require(fx.Store.Cursor("ra") is null, "RequestRescanAsync clears that root's cursor");
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(fx.A.FullCalls > fullBefore && fx.A.DeltaCalls == deltaBefore,
            "the run re-reads the forgotten root in full instead of from its journal delta");
        Require(report.Deleted == 1 && report.Done == 1,
            "the deletion made before the rescan is planned as exactly one delete from the baseline");
        Require(report.Uploaded == 0 && report.Downloaded == 0 && report.Moved == 0 && report.KeptBoth == 0 &&
            report.Held == 0 && report.Failed == 0, "the unchanged file causes no other effect");
        Require(!(await MapAsync(fx.B)).ContainsKey("x.txt"), "the delete removed the file on the other side");
        Require((await MapAsync(fx.B)).ContainsKey("keep.txt"), "the unchanged file stays on the other side");
        Console.WriteLine("PASS Incremental_FullRescanAfterForgetCursor");
    }

    // A run started outside this runtime (continuous sync calls IncrementalAsync directly) holds the run's own
    // gate; a rescan requested meanwhile waits for it and is applied after it, so the run's cursor write-back
    // cannot lose the reset.
    private static async Task RequestRescan_WaitsForRunningScanAsync()
    {
        var fx = Build();
        fx.A.Seed("x.txt", "same");
        fx.B.Seed("x.txt", "same");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(fx.Store.Cursor("ra") is not null, "the settled pair holds a cursor for the local root");
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = new GatedInventory(fx.A) { Block = block };
        fx.Provider.AddInventory(fx.Pair.A, gated);
        var running = run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        await gated.Entered;
        var rescan = run.RequestRescanAsync(["ra"], CancellationToken.None);
        Require(!rescan.IsCompleted, "a rescan requested during a run waits for that run's gate");
        block.SetResult();
        await running;
        await rescan;
        Require(fx.Store.Cursor("ra") is null, "the waited rescan clears the cursor after the run finished");
        var fullBefore = fx.A.FullCalls;
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(fx.A.FullCalls > fullBefore, "the next run scans that root in full, so the rescan was not lost");
        Console.WriteLine("PASS RequestRescan_WaitsForRunningScan");
    }

    // A rescan never shortcuts reconcile: the run resolves its pending intents before it reads any inventory, so a
    // forced full scan still starts from reconciled state.
    private static async Task RequestRescan_ReconcilesBeforeInventoryAsync()
    {
        var fx = Build();
        var item = fx.A.Seed("x.txt", "same");
        fx.B.Seed("x.txt", "same");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        // A copy that never reached its target: reconcile resolves it as not applied.
        fx.Store.RecordIntent(fx.Pair.PairId, new SyncAction("pending", 1, ActionKind.CopyNew, Side.B, "ghost.txt",
            null, item, null, false, "k1"));
        Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 1, "the fixture holds one unresolved intent");
        var probe = new GatedInventory(fx.A) { Probe = () => fx.Store.UnresolvedIntents(fx.Pair.PairId).Count };
        fx.Provider.AddInventory(fx.Pair.A, probe);
        await run.RequestRescanAsync(["ra"], CancellationToken.None);
        var fullBefore = fx.A.FullCalls;
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(probe.ProbeAtFirstScan == 0,
            "the inventory read saw no unresolved intent, so reconcile ran before the inventory");
        Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 0, "the run resolves the pending intent");
        Require(fx.A.FullCalls > fullBefore, "the run still took the forced full-scan path");
        Console.WriteLine("PASS RequestRescan_ReconcilesBeforeInventory");
    }

    // B-165: inventories report files without hashes (as the real local scan does) and writers refuse an unhashed
    // source (as the real local and Dropbox writers do); every planned copy must carry its source hash.
    private static Fixture HashlessFixture()
    {
        var fx = Build();
        foreach (var endpoint in new[] { fx.A, fx.B })
        {
            endpoint.InventoryWithoutHashes = true;
            endpoint.RequireHashes = true;
        }
        return fx;
    }

    private static string PlanText(Plan plan) => string.Join("; ", plan.Actions.Select(action =>
        $"{action.Kind} {action.Target} {action.Path} {action.Source?.Hash}")) + " | " +
        string.Join("; ", plan.Notices.Select(notice => $"{notice.Rule} {notice.Path} {notice.ReasonKey}"));

    private static async Task CopyHash_OneSidedCopiesCarryHashesAsync()
    {
        var fx = HashlessFixture();
        fx.A.Seed("up.txt", "local only");
        fx.B.Seed("down.txt", "cloud only");
        fx.Store.SavePair(fx.Pair with { Excluded = ["skip"] });
        fx.A.Seed("skip/x.txt", "excluded");
        var run = fx.Run();
        var preview = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var again = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var copies = preview.Actions.Where(action => action.Kind == ActionKind.CopyNew).ToArray();
        Require(copies.Length == 2 && copies.All(action => action.Source?.Hash is not null),
            "both one-sided copies carry their source hash in the preview: " + PlanText(preview));
        Require(PlanText(preview) == PlanText(again), "a repeated preview is the same plan: " + PlanText(again));
        Require(!fx.A.HashedPaths.Contains("skip/x.txt"), "a file that will not be copied is never hashed");
        var report = await run.ApplyAsync(fx.Pair.PairId, preview, new RecordingProgress(), CancellationToken.None);
        Require(report.Failed == 0 && report.Uploaded + report.Downloaded == 2,
            $"the reviewed plan applies both copies (failed {report.Failed}, up {report.Uploaded}, down {report.Downloaded})");
        Require(await fx.B.ObserveAsync("up.txt", CancellationToken.None) is not null &&
                await fx.A.ObserveAsync("down.txt", CancellationToken.None) is not null,
            "the local-only file reached the cloud side and the cloud-only file the local side");
        Console.WriteLine("PASS CopyHash_OneSidedCopiesCarryHashes");
    }

    private static async Task CopyHash_UnhashableSourceHeldAsync()
    {
        var fx = HashlessFixture();
        fx.A.Seed("ok.txt", "fine");
        fx.A.Seed("bad.txt", "unreadable");
        fx.A.Unhashable.Add("bad.txt");
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(!plan.Actions.Any(action => action.Path == "bad.txt"),
            "a copy whose source cannot be hashed is not planned: " + PlanText(plan));
        Require(plan.Notices.Any(notice => notice.Path == "bad.txt" && notice.Rule == 30 &&
                                           notice.ReasonKey == PlanNotices.ContentUnconfirmed.Key),
            "it is held with the content-unconfirmed notice: " + PlanText(plan));
        Require(plan.Actions.Any(action => action.Path == "ok.txt" && action.Source?.Hash is not null),
            "the other copy is planned with its hash");
        Console.WriteLine("PASS CopyHash_UnhashableSourceHeld");
    }

    private static async Task CopyHash_IncrementalCopyHashedAsync()
    {
        var fx = HashlessFixture();
        fx.A.Seed("a.txt", "1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        fx.A.Seed("new.txt", "made later");
        fx.A.Seed("a.txt", "2", mtime: DateTimeOffset.UnixEpoch.AddSeconds(1));
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(report.Failed == 0 && report.Uploaded == 2 && fx.Store.OpenNotices(fx.Pair.PairId).Count == 0,
            $"an incremental run uploads its new and edited files (failed {report.Failed}, up {report.Uploaded}, " +
            $"done {report.Done}, notices {fx.Store.OpenNotices(fx.Pair.PairId).Count})");
        Require(await TextAsync(fx.B, "a.txt") == "2" && await TextAsync(fx.B, "new.txt") == "made later",
            "the cloud side ends with both files' new contents");
        Console.WriteLine("PASS CopyHash_IncrementalCopyHashed");
    }

    // Both sides edited with different sizes: two labelled keep-both copies. If one source cannot be hashed, neither
    // half of the row runs (no lone keep-both copy, no lone Forget).
    private static async Task CopyHash_KeepBothHeldTogetherAsync()
    {
        var fx = HashlessFixture();
        fx.A.Seed("f.txt", "same");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        fx.A.Seed("f.txt", "local edit");
        fx.B.Seed("f.txt", "a much longer cloud edit");
        fx.A.Unhashable.Add("f.txt");
        var callsA = fx.WriterA.Calls;
        var callsB = fx.WriterB.Calls;
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(fx.WriterA.Calls == callsA && fx.WriterB.Calls == callsB && report.Failed == 0,
            $"no half of the keep-both row runs (A calls {fx.WriterA.Calls - callsA}, B calls {fx.WriterB.Calls - callsB})");
        Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Rule == 30 &&
                notice.ReasonKey == PlanNotices.ContentUnconfirmed.Key),
            "the row is held with the content-unconfirmed notice");
        Require(await TextAsync(fx.A, "f.txt") == "local edit" && await TextAsync(fx.B, "f.txt") == "a much longer cloud edit",
            "both edits are untouched");
        fx.A.Unhashable.Clear();
        // Decision 85: retry timing is durable; explicitly make the recovered item due.
        var retry = fx.Store.SyncRetryAt(fx.Pair.PairId, "f.txt")!;
        fx.Store.SaveSyncRetry(fx.Pair.PairId, retry with { RetryAfter = DateTimeOffset.MinValue });
        var retried = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(retried.Failed == 0 && retried.KeptBoth == 2,
            $"once hashable, the next run keeps both versions (kept both {retried.KeptBoth}, failed {retried.Failed}, " +
            $"done {retried.Done}, held {retried.Held}, up {retried.Uploaded}, down {retried.Downloaded}, A {fx.WriterA.Calls - callsA}, B {fx.WriterB.Calls - callsB}, notices " +
            string.Join("; ", fx.Store.OpenNotices(fx.Pair.PairId).Select(notice => notice.Rule + " " + notice.Path + " " + notice.ReasonKey)) + ")");
        Console.WriteLine("PASS CopyHash_KeepBothHeldTogether");
    }

    // A renames old/ to new/ while B edits old/file.txt: the planner moves B's folder and rebases the update's
    // source to new/file.txt, which does not exist on B yet; the hash is read at old/file.txt (its current place).
    private static async Task CopyHash_SourceUnderMovedFolderAsync()
    {
        var fx = HashlessFixture();
        fx.A.Seed("old", "", ItemKind.Folder);
        fx.A.Seed("old/file.txt", "v1");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        await fx.A.MoveAsync(new SyncAction("seed-move-dir", 25, ActionKind.Move, Side.A, "new", "old", null, null,
            false, null), CancellationToken.None);
        fx.B.Seed("old/file.txt", "v2 from the cloud");
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        // Move + edit keeps both versions (requirements): the cloud edit's copy is read at its current place.
        Require(report.Failed == 0 && report.Moved == 1,
            $"the edit under the moved folder is hashed at its current place and copied (failed {report.Failed}, " +
            $"moved {report.Moved}, failures " + string.Join("; ", report.Failures.Select(f => f.Outcome + " " + f.Reason)) + ")");
        foreach (var endpoint in new[] { fx.A, fx.B })
        {
            var files = (await endpoint.FullAsync(0, _ => true, null!, CancellationToken.None)).Items
                .Where(item => item.Kind == ItemKind.File).Select(item => item.Path).ToArray();
            var texts = new List<string?>();
            foreach (var path in files) texts.Add(await TextAsync(endpoint, path));
            Require(files.Length == 2 && files.All(path => path.StartsWith("new/", StringComparison.Ordinal)) &&
                    texts.Contains("v1") && texts.Contains("v2 from the cloud"),
                "each side ends with both versions under the moved folder: A " + await ListingAsync(fx.A) +
                " | B " + await ListingAsync(fx.B));
        }        Console.WriteLine("PASS CopyHash_SourceUnderMovedFolder");
    }

    private static async Task<string> ListingAsync(FakeEndpoint endpoint)
    {
        var items = (await endpoint.FullAsync(0, _ => true, null!, CancellationToken.None)).Items;
        var parts = new List<string>();
        foreach (var item in items)
            parts.Add(item.Kind == ItemKind.File ? item.Path + "=" + await TextAsync(endpoint, item.Path) : item.Path + "/");
        return string.Join(", ", parts);
    }

    // A first-sync remove choice for a local-only folder collapses to one folder delete (rule 23) that the executor
    // expands into RecoveryFirst deletes of the live files under it. Those victims were never hashed (one-sided,
    // not copied), and the real writers refuse to preserve an unhashed file: they are hashed while planning.
    private static async Task CopyHash_FolderRemoveHashesVictimsAsync()
    {
        foreach (var unhashable in new[] { false, true })
        {
            var fx = HashlessFixture();
            fx.A.Seed("dir", "", ItemKind.Folder);
            fx.A.Seed("dir/a.txt", "one");
            fx.A.Seed("dir/sub", "", ItemKind.Folder);
            fx.A.Seed("dir/sub/b.txt", "two");
            if (unhashable) fx.A.Unhashable.Add("dir/sub/b.txt");
            var run = fx.Run();
            var choices = new[] { new OneSidedChoice("dir", Side.A, true) };
            var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, null, choices, new RecordingProgress(),
                CancellationToken.None);
            var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
            if (!unhashable)
            {
                Require(report.Failed == 0 && fx.WriterA.Preserves == 2 &&
                        await fx.A.ObserveAsync("dir", CancellationToken.None) is null,
                    $"the folder removal hashes its file victims, preserves both and deletes the folder (failed " +
                    $"{report.Failed}, preserves {fx.WriterA.Preserves}, " + string.Join("; ",
                        report.Failures.Select(f => f.Outcome + " " + f.Reason)) + ") | " + PlanText(plan));
            }
            else
            {
                Require(report.Failed == 0 && fx.WriterA.Calls == 0 &&
                        await fx.A.ObserveAsync("dir/a.txt", CancellationToken.None) is not null &&
                        plan.Notices.Any(notice => notice.Path == "dir/sub/b.txt" && notice.Rule == 30),
                    "a folder removal with an unhashable victim is held, not expanded, and the victim is noticed: " +
                    PlanText(plan));
            }
        }
        Console.WriteLine("PASS CopyHash_FolderRemoveHashesVictims");
    }
    private static async Task<string?> TextAsync(FakeEndpoint endpoint, string path)
    {
        if (await endpoint.ObserveAsync(path, CancellationToken.None) is not { } state) return null;
        await using var stream = await endpoint.OpenReadAsync(state, CancellationToken.None);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private sealed class Fixture
    {
        public required IStateStore Store { get; init; }
        public required RecordingStore? Recorder { get; init; }
        public required FakeEndpoint A { get; init; }
        public required FakeEndpoint B { get; init; }
        public required CountingWriter WriterA { get; init; }
        public required CountingWriter WriterB { get; init; }
        public required FakeEndpointProvider Provider { get; init; }
        public required Executor Executor { get; init; }
        public required PairDef Pair { get; init; }

        public SyncRun Run() => new(Store, Executor, Provider, Settings, TimeProvider.System);
    }

    internal static async Task HashCheckpointsAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "resilience-hash-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        for (var i = 0; i < 5; i++) { fx.A.Seed($"{i}.txt", "same"); fx.B.Seed($"{i}.txt", "same"); }
        var inventory = new ScriptedInventory(fx.A) { Hashless = true, ThrowHashPath = "4.txt" };
        fx.Provider.AddInventory(fx.Pair.A, inventory);
        var settings = Settings with { LocalHashCheckpointFiles = 2 };
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, settings, TimeProvider.System);
        var preview = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(preview.RunUnavailable is null && fx.Store.SyncRetryAt(fx.Pair.PairId, "4.txt") is not null,
            "failed hash checkpoint remains retryable while the preview completes");
        Require(inventory.HashedPaths == 4, "two completed hash chunks before interruption");
        Require(fx.Store.Live(fx.Pair.A.RootId, Enumerable.Range(0, 5).Select(i => $"{i}.txt"))
            .Count(x => x.Hash is not null) == 4, "completed chunks are committed before the next hash call");
        inventory.ThrowHashPath = null;
        store.Dispose();
        using var reopened = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
        var restarted = new SyncRun(reopened, NewExecutor(reopened), fx.Provider, settings, TimeProvider.System);
        await restarted.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(inventory.HashedPaths == 5, "restart hashes only the remainder, not the two committed chunks");
    }

    // Each hash checkpoint must be one atomic store write: a chunk may never exceed the store's
    // batch row size, or a single checkpoint could split across StoreBatch commits and an
    // interruption could leave it half-applied.
    internal static async Task HashCheckpointBatchAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "resilience-hashbatch-" + Guid.NewGuid().ToString("N"));
        var settings = Settings with { LocalHashCheckpointFiles = 2000, StoreBatchRows = 1000 };
        using var store = SqliteStateStore.Open(folder, settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        for (var i = 0; i < 1001; i++) { fx.A.Seed($"{i:D4}.txt", "same"); fx.B.Seed($"{i:D4}.txt", "same"); }
        var inventory = new ScriptedInventory(fx.A) { Hashless = true };
        fx.Provider.AddInventory(fx.Pair.A, inventory);
        var run = new SyncRun(fx.Store, fx.Executor, fx.Provider, settings, TimeProvider.System);
        await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(inventory.MaxHashChunk > 0, "the checkpointed hashing ran");
        Require(inventory.MaxHashChunk <= settings.StoreBatchRows,
            $"a hash checkpoint never exceeds StoreBatchRows ({inventory.MaxHashChunk} > {settings.StoreBatchRows})");
        Console.WriteLine("PASS hash checkpoints stay atomic: no chunk exceeds StoreBatchRows");
    }

    private static Executor NewExecutor(IStateStore store) => new(store, Settings, ExecutorChecks.Admission());

    private static Fixture Build(bool enabled = true, bool record = false, string pairId = "p1", IStateStore? suppliedStore = null, bool localLocal = false, string accountId = "acc", bool displayRoot = false)
    {
        var inner = suppliedStore ?? new InMemoryStateStore();
        var recorder = record ? new RecordingStore(inner) : null;
        IStateStore store = recorder is not null ? recorder : inner;
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        var writerA = new CountingWriter(a);
        var writerB = new CountingWriter(b);
        var pair = new PairDef(pairId, pairId,
            new RootDef("ra", SyncProviders.Local, null, "C:\\a", displayRoot ? "id:local:00000001:0000000000000001" : "id:ra"),
            new RootDef("rb", localLocal ? SyncProviders.Local : SyncProviders.Dropbox,
                localLocal ? null : accountId, localLocal ? @"C:\b" : "/b", "id:rb"),
            [], [], enabled);
        store.SavePair(pair);
        var provider = new FakeEndpointProvider()
            .Add(pair.A, a).Add(pair.B, b)
            .AddWriter(pair.A, writerA).AddWriter(pair.B, writerB);
        return new Fixture
        {
            Store = store,
            Recorder = recorder,
            A = a,
            B = b,
            WriterA = writerA,
            WriterB = writerB,
            Provider = provider,
            Executor = NewExecutor(store),
            Pair = pair
        };
    }

    private static PairDef Pair(string id, string localRoot, string remoteRoot) => new(id, id,
        new RootDef("ra-" + id, SyncProviders.Local, null, localRoot, "id:" + id),
        new RootDef("rb-" + id, SyncProviders.Dropbox, "acc", remoteRoot, "id2:" + id),
        [], [], true);

    private static async Task<RunReport> FirstSyncAsync(SyncRun run, Fixture fx)
    {
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        return await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
    }

    private static async Task<Dictionary<string, string>> MapAsync(FakeEndpoint endpoint)
    {
        var snapshot = await endpoint.FullAsync(0, _ => true, SilentProgress, CancellationToken.None);
        return snapshot.Items.ToDictionary(item => item.Path, item => item.Hash ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
    }

    private static async Task MirrorAsync(Fixture fx)
    {
        var a = await MapAsync(fx.A);
        var b = await MapAsync(fx.B);
        Require(a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var hash) && hash == pair.Value),
            "both sides hold the same files");
    }

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static void ExpectRefused(Task task)
    {
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (SyncRunRefusedException)
        {
            return;
        }
        throw new InvalidOperationException("Expected SyncRunRefusedException.");
    }

    private static async Task ExpectAsync<TException>(Func<Task> operation) where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
