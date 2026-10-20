using System.Diagnostics;

namespace Massdex.SyncShark.V2;

/// <summary>Contract checks for the Executor, driven by FakeEndpoint fault injection (executor.md section 10).</summary>
internal static partial class ExecutorChecks
{
    // Only a guard against a hang if the held effect is never reached; the test never waits for it to elapse.
    private static readonly TimeSpan HeldEffectTimeout = TimeSpan.FromSeconds(5);

    public static async Task RunAsync()
    {
        await Order_PhasesAndLevels();
        await Order_InterleavesTargets();
        await Vacate_DeleteBeforeCreateSamePath();
        await Vacate_FolderToFile_DescendantsBeforeFolder();
        await Mismatch_Skipped();
        await NewestWins_ChangedLocalTargetSkippedBeforeOverwrite();
        await HeldDelete_EditAfterPlan_Skipped();
        await OccupiedCreate_Skipped();
        await OccupiedMoveDestination_Skipped();
        await CaseOnlyRename_Done();
        await CaseOnlyFolder_AdoptsAndChildrenProceed();
        await CaseOnlyFolder_BaselinedChildren_AdoptEditOneNotice();
        await SourceChanged_Skipped();
        await Uncertain_ObservedDone_NotReplayed();
        await Uncertain_Unverifiable_StaysUncertain();
        await Resolve_Cancellation_PropagatesOnlyWhenRequested();
        await RecoveryFirst_PreserveBeforeDelete();
        await RecoveryFirst_PreserveFails_NoEffect();
        await RecoveryFirst_PreserveMismatch_Skipped();
        await OneFailureDoesNotStopOthers();
        await FreeSpace_RefusalIsPendingAndOthersContinue();
        await FreeSpace_ReservationIsReleasedOnEveryExit();
        await ParentFailed_ChildSkipped();
        await Admission_ProviderBackoff_Failed();
        await Hold_NeverExecuted();
        await CommitOnly_AdoptForget();
        await IntentBeforeEffect_Ordered();
        await GroupCommit_SharesTransactions();
        await GroupCommit_FailedBatch_LeavesIntentsForReconcile();
        await Throughput_200TinyFiles();
        await WorkerCap_Respected();
        await Batching_SmallCreates_GroupAndReleaseAdmission();
        await Cancel_StopsLaunching_KeepsIntents();
        await Apply_EarlyBreak_StopsEffects();
        await Reconcile_ThreeOutcomes();
        await Reconcile_RecoveryFirstDone_SameRef();
        await Reconcile_Move_Done_WhenSameObject();
        await Reconcile_Move_Done_WhenSourceContentDiffers();
        await Reconcile_Move_UnrelatedDestination_Superseded();
        await Reconcile_Move_SourcePresent_NotApplied();
        await Reconcile_RecoveryLookupFails_Continues();
        await FolderDelete_ExpandsPerFile();
        await FolderDelete_FrozenVictimsChanged_Skipped();
        await FolderDelete_DescendantFails_FolderKept();
        await RunPlannedDescendantChecksAsync();
        await ProviderCheckpoint_ReconcileKeepsPending();
        Admission_ActualSizeReservations();
        await UploadPipeline_2000TinyFiles();
        await UploadPipeline_IsolatedFailureAndLostAck();
        await UploadPipeline_DefaultBatchCapAsync();
        UploadPipeline_StagingBufferDefault();
        await UploadPipeline_StagesAheadWhileCommitInFlightAsync();
        await UploadPipeline_AgeFlushReArmsAsync();
        await GroupCommit_ProviderCheckpointsAreOneTransactionAsync();
        await GroupCommit_TransactionsPerWriteUnderLoadAsync();
        await UploadPipeline_CheckpointOneTransactionBeforeSendAsync();
        await UploadPipeline_CheckpointWriteFails_NothingSentAsync();
        await UploadPipeline_ConcurrentQueueAndTailsAsync();
        await UploadPipeline_NamespaceGateAsync();
        await UploadPipeline_CrossClientConcurrencyAsync();
        await UploadSessionStart_GateAsync();
        await UploadSessionStart_ConcurrencyCapAsync();
        await UploadSessionStart_BoundedRetryAsync();
        UploadPipeline_BatchTimeoutPolicy();
        await UploadPipeline_SmallBatchFloorAsync();
        await UploadPipeline_BatchTimeoutIsUncertainAsync();
        await UploadPipeline_Safety();
        Console.WriteLine("v2 executor checks passed.");
    }

    private static async Task Order_PhasesAndLevels()
    {
        var (_, a, b, executor) = Rig();
        var moved = a.Seed("d/x", "x-content", versionId: "v1");
        var copy = a.Seed("d/e/f", "f-content", versionId: "v1");
        var xFolder = b.Seed("x", "x-content", versionId: "v1");
        var old = b.Seed("old", "old-content", versionId: "v1");
        var record = new RecordingWriter(b);
        var plan = Plan(
            Act(ActionKind.MkDir, Side.B, "d"),
            Act(ActionKind.MkDir, Side.B, "d/e"),
            Act(ActionKind.Move, Side.B, "d/x", from: "x", source: moved, expected: xFolder),
            Act(ActionKind.CopyNew, Side.B, "d/e/f", source: copy),
            Act(ActionKind.Delete, Side.B, "old", expected: old));

        var results = await Apply(executor, plan, a, record);
        var calls = record.Calls;
        Require(results.All(r => r.Outcome == Outcome.Done), "every ordered action is Done");
        Require(Index(calls, "MkDir:d") < Index(calls, "MkDir:d/e"), "a folder precedes its child folder");
        Require(Index(calls, "MkDir:d") < Index(calls, "Move:d/x"), "a move into a new folder follows its MkDir");
        Require(Math.Max(Index(calls, "MkDir:d/e"), Index(calls, "Move:d/x")) < Index(calls, "Put:d/e/f"),
            "content runs after the structure phase");
        Require(Index(calls, "Put:d/e/f") < Index(calls, "Delete:old"), "deletes run last");
    }

    private static async Task Order_InterleavesTargets()
    {
        var (store, a, b, _) = Rig();
        var journal = new List<string>();
        var recorded = new JournalStore(store, journal);
        var executor = new Executor(recorded, Settings(1), Admission());
        var actions = new List<SyncAction>();
        foreach (var side in new[] { Side.A, Side.B })
        {
            var path = side == Side.A ? "Child" : "Projects";
            actions.Add(Act(ActionKind.MkDir, side, path));
            actions.Add(Act(ActionKind.MkDir, side, path + "/nested"));
            for (var i = 0; i < 3; i++)
                actions.Add(Act(ActionKind.CopyNew, side, path + "/nested/" + i,
                    source: (side == Side.A ? b : a).Seed(path + "/nested/" + i, "content")));
            var target = side == Side.A ? a : b;
            var folder = target.Seed("old", "", ItemKind.Folder);
            var child = target.Seed("old/child", "old");
            actions.Add(Act(ActionKind.Delete, side, "old", expected: folder) with { DescendantsPlanned = true });
            actions.Add(Act(ActionKind.Delete, side, "old/child", expected: child));
        }
        var results = await Apply(executor, Plan(actions.ToArray()), a, b);
        Require(results.All(r => r.Outcome == Outcome.Done), "mixed directions retain folder readiness and descendant-delete barriers");
        var admitted = journal.Where(line => line.StartsWith("intent:", StringComparison.Ordinal))
            .Select(line => actions.Single(action => "intent:" + action.ActionId == line)).ToArray();
        Require(admitted.Select(action => action.Target).SequenceEqual(
            Enumerable.Range(0, actions.Count).Select(i => i % 2 == 0 ? Side.A : Side.B)),
            "path-grouped mixed plan alternates target sides at admission");
        foreach (var side in new[] { Side.A, Side.B })
        {
            var sideActions = admitted.Where(action => action.Target == side).ToArray();
            Require(sideActions.Take(2).All(action => action.Kind == ActionKind.MkDir) &&
                sideActions.Skip(2).Take(3).All(action => action.Kind == ActionKind.CopyNew) &&
                sideActions[^2].Path == "old/child" && sideActions[^1].Path == "old",
                "interleaving retains parent-before-content and deepest-first deletes on " + side);
        }
    }

    private static async Task Vacate_DeleteBeforeCreateSamePath()
    {
        var (_, a, b, executor) = Rig();
        var file = b.Seed("p", "file-content", versionId: "v1");
        var record = new RecordingWriter(b);
        var plan = Plan(
            Act(ActionKind.Delete, Side.B, "p", expected: file, key: "k1"),
            Act(ActionKind.MkDir, Side.B, "p"));

        var results = await Apply(executor, plan, a, record);
        Require(results.All(r => r.Outcome == Outcome.Done), "the vacating delete and the create both succeed");
        Require(Index(record.Calls, "Delete:p") < Index(record.Calls, "MkDir:p"), "the delete vacates before the MkDir");
    }

    // Replacing folder d with a file: the folder's own delete must follow every descendant it removes, and all
    // of them must run before the create at d (else the non-empty folder delete fails).
    private static async Task Vacate_FolderToFile_DescendantsBeforeFolder()
    {
        var (store, a, b, executor) = Rig();
        var folder = b.Seed("d", "", ItemKind.Folder, versionId: "v1");
        var child = b.Seed("d/x", "child-content", versionId: "v1");
        var snapshot = store.BeginGeneration("rb", "snapshot");
        store.WriteSnapshot(new Snapshot("rb", Side.B, snapshot, "1", new[] { folder, child }, Array.Empty<string>()));
        store.CompleteGeneration(snapshot, "1", true);
        var replacement = a.Seed("d", "file-content", versionId: "v1");
        var record = new RecordingWriter(b);
        var plan = Plan(
            Act(ActionKind.Delete, Side.B, "d", expected: folder, rf: true, key: "kd"),
            Act(ActionKind.CopyNew, Side.B, "d", source: replacement));

        var results = await Apply(executor, plan, a, record);
        var calls = record.Calls;
        Require(results.All(r => r.Outcome == Outcome.Done), "the folder replacement completes");
        Require(Index(calls, "Delete:d/x") < Index(calls, "Delete:d"), "the folder's own delete follows its file");
        Require(Index(calls, "Delete:d") < Index(calls, "Put:d"), "the whole folder goes before the file replaces it");
        Require(record.Preserves == 1, "one recovery copy for the folder's file");
        Require((await b.ObserveAsync("d", CancellationToken.None))!.Kind == ItemKind.File, "d is now a file");
    }

    private static async Task Mismatch_Skipped()
    {
        var (store, a, b, executor) = Rig();
        var source = a.Seed("x", "new-content", versionId: "v1");
        var stale = b.Seed("x", "old-content", versionId: "v1");
        var reseeded = b.Seed("x", "reseeded-content", versionId: "v2");
        var action = Act(ActionKind.Update, Side.B, "x", source: source, expected: stale);

        var results = await Apply(executor, Plan(action), a, new RecordingWriter(b));
        Require(results.Single().Outcome == Outcome.Skipped, "a stale ExpectedTarget is Skipped");
        Require((await b.ObserveAsync("x", CancellationToken.None))!.Hash == reseeded.Hash, "B keeps the reseeded content");
        Require(store.UnresolvedIntents("p1").Count == 0, "a Skipped action closes its intent");
    }

    private static async Task HeldDelete_EditAfterPlan_Skipped()
    {
        var (store, a, b, executor) = Rig();
        var stale = a.Seed("x", "before", versionId: "v1");
        var action = new SyncAction("held-delete", 19, ActionKind.Delete, Side.A, "x", null, null,
            stale, false, null) { NeedsReview = true };
        var edited = a.Seed("x", "edited", versionId: "v2");
        var results = await Apply(executor, Plan(action), a, b);
        Require(results.Single().Outcome == Outcome.Skipped, "an edited held-delete target is skipped");
        Require((await a.ObserveAsync("x", default))?.Hash == edited.Hash, "the local edit survives");
        Require(store.UnresolvedIntents("p1").Count == 0, "the superseded held-delete intent closes");
    }

    // Writer contract: a create onto an occupied path is a mismatch, so it is Skipped and the occupant survives.
    private static async Task OccupiedCreate_Skipped()
    {
        var (_, a, b, executor) = Rig();
        var source = a.Seed("x", "new-content", versionId: "v1");
        var occupant = b.Seed("x", "occupant", versionId: "v1");
        var results = await Apply(executor, Plan(Act(ActionKind.CopyNew, Side.B, "x", source: source)), a, b);
        Require(results.Single().Outcome == Outcome.Skipped, "a create onto an occupied path is Skipped");
        Require((await b.ObserveAsync("x", CancellationToken.None))!.Hash == occupant.Hash, "the occupant is untouched");
    }

    // Writer contract: a move onto an occupied destination is a mismatch; neither item changes.
    private static async Task OccupiedMoveDestination_Skipped()
    {
        var (_, a, b, executor) = Rig();
        var moved = b.Seed("p", "moved", versionId: "v1");
        var occupant = b.Seed("q", "occupant", versionId: "v1");
        var results = await Apply(executor,
            Plan(Act(ActionKind.Move, Side.B, "q", from: "p", source: moved, expected: moved)), a, b);
        Require(results.Single().Outcome == Outcome.Skipped, "a move onto an occupied destination is Skipped");
        Require((await b.ObserveAsync("p", CancellationToken.None))!.Hash == moved.Hash &&
            (await b.ObserveAsync("q", CancellationToken.None))!.Hash == occupant.Hash, "both items are untouched");
    }

    // A case-only rename moves an item onto its own path and is never refused as occupied.
    private static async Task CaseOnlyRename_Done()
    {
        var (_, a, b, executor) = Rig();
        var moved = b.Seed("name.txt", "content", versionId: "v1");
        var results = await Apply(executor,
            Plan(Act(ActionKind.Move, Side.B, "Name.txt", from: "name.txt", source: moved, expected: moved)), a, b);
        Require(results.Single().Outcome == Outcome.Done, "a case-only rename is Done");
        var snapshot = await b.FullAsync(0, _ => true, new Progress<string>(), CancellationToken.None);
        Require(snapshot.Items.Single().Path == "Name.txt", "the item carries the new spelling");
    }

    private static async Task SourceChanged_Skipped()
    {
        var (_, a, b, executor) = Rig();
        var source = a.Seed("x", "content", versionId: "v1");
        a.Seed("x", "changed-content", versionId: "v2");
        var action = Act(ActionKind.CopyNew, Side.B, "x", source: source);

        var results = await Apply(executor, Plan(action), a, new RecordingWriter(b));
        Require(results.Single().Outcome == Outcome.Skipped, "a source changed since planning is Skipped");
        Require(await b.ObserveAsync("x", CancellationToken.None) is null, "nothing was written to B");
    }

    private static async Task Uncertain_ObservedDone_NotReplayed()
    {
        var (store, a, b, executor) = Rig();
        var source = a.Seed("x", "hello", versionId: "v1");
        b.NextWriteUncertain = true;
        var record = new RecordingWriter(b);
        var action = Act(ActionKind.CopyNew, Side.B, "x", source: source, key: "k1");

        var results = await Apply(executor, Plan(action), a, record);
        Require(results.Single().Outcome == Outcome.Done, "an uncertain put that landed resolves to Done");
        Require(record.Puts == 1, "the effect is never replayed");
        Require(store.Baselines("p1").Any(baseline => baseline.ObjectKey == "k1"), "the resolved baseline is written");
    }

    private static async Task Uncertain_Unverifiable_StaysUncertain()
    {
        var (store, a, b, executor) = Rig();
        var source = a.Seed("x", "hello", versionId: "v1");
        b.NextWriteUncertain = true;
        var record = new RecordingWriter(b) { ObserveFails = true };
        var action = Act(ActionKind.CopyNew, Side.B, "x", source: source, key: "k1");

        var results = await Apply(executor, Plan(action), a, record);
        Require(results.Single().Outcome == Outcome.Uncertain, "an unobservable uncertain effect stays Uncertain");
        Require(store.UnresolvedIntents("p1").Any(intent => intent.ActionId == action.ActionId),
            "the uncertain intent is still unresolved");
        Require(results.Single().Reason is { } reason && reason.Contains("IOException") &&
            reason.Contains("observe fails by test request"),
            "the uncertain outcome carries the actual verification failure: " + results.Single().Reason);
    }

    // Standards audit A6: a cancellation the caller requested propagates out of verification; one it did not
    // request (an endpoint timeout) leaves the effect Uncertain with that failure as its reason.
    private static async Task Resolve_Cancellation_PropagatesOnlyWhenRequested()
    {
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        var action = Act(ActionKind.CopyNew, Side.B, "x", source: a.Seed("x", "hello", versionId: "v1"), key: "k1");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() =>
            EffectResolver.ResolveAsync(action, new RecordingWriter(b), a, cancelled.Token));
        var timedOut = await EffectResolver.ResolveAsync(action, new RecordingWriter(b) { ObserveTimesOut = true }, a,
            CancellationToken.None);
        Require(timedOut.Outcome == Outcome.Uncertain && timedOut.Reason!.Contains("observe timed out"),
            "an unrequested cancellation is an unverified (Uncertain) effect with its diagnostic: " + timedOut.Reason);
    }

    private static async Task RecoveryFirst_PreserveBeforeDelete()
    {
        var (store, a, b, executor) = Rig();
        var victim = b.Seed("x", "content", versionId: "v1");
        SeedBaseline(store, "k1", a.Seed("x", "content", versionId: "v1"), victim);
        var record = new RecordingWriter(b);
        var action = Act(ActionKind.Delete, Side.B, "x", expected: victim, rf: true, key: "k1");

        var results = await Apply(executor, Plan(action), a, record);
        var result = results.Single();
        Require(result.Outcome == Outcome.Done && result.Recovery is not null, "the delete is Done with a recovery copy");
        Require(Index(record.Calls, "Preserve:x") < Index(record.Calls, "Delete:x"), "the copy precedes the delete");
        Require(store.Baselines("p1").All(baseline => baseline.ObjectKey != "k1"), "the done delete removed its baseline");
    }

    private static async Task RecoveryFirst_PreserveFails_NoEffect()
    {
        var (_, a, b, executor) = Rig();
        var victim = b.Seed("x", "content", versionId: "v1");
        b.FailNextWrites = 1;
        var record = new RecordingWriter(b);
        var action = Act(ActionKind.Delete, Side.B, "x", expected: victim, rf: true, key: "k1");

        var results = await Apply(executor, Plan(action), a, record);
        var result = results.Single();
        Require(result.Outcome == Outcome.Failed && result.Reason!.StartsWith("Recovery copy failed; nothing changed:"),
            "a failed recovery fails the action without an effect: " + result.Reason);
        Require(record.Deletes == 0 && await b.ObserveAsync("x", CancellationToken.None) is not null,
            "the victim is untouched and no delete was attempted");
    }

    // A victim that changed since planning is a mismatch, not a failure: nothing changed, so re-plan.
    private static async Task RecoveryFirst_PreserveMismatch_Skipped()
    {
        var (store, a, b, executor) = Rig();
        var victim = b.Seed("x", "content", versionId: "v1");
        var record = new RecordingWriter(b) { PreserveFailsMismatch = true };
        var action = Act(ActionKind.Delete, Side.B, "x", expected: victim, rf: true, key: "k1");

        var results = await Apply(executor, Plan(action), a, record);
        var result = results.Single();
        Require(result.Outcome == Outcome.Skipped && result.Reason!.StartsWith("RecordingWriter"),
            "a stale victim during preservation is Skipped: " + result.Reason);
        Require(record.Deletes == 0 && store.UnresolvedIntents("p1").Count == 0, "no effect and the intent is closed");
    }

    private static async Task OneFailureDoesNotStopOthers()
    {
        var (store, a, b, executor) = Rig();
        b.FailNextWrites = 1;
        var actions = Enumerable.Range(0, 10)
            .Select(i => Act(ActionKind.CopyNew, Side.B, "f" + i, source: a.Seed("f" + i, "v" + i), key: "k" + i))
            .ToArray();

        var results = await Apply(executor, Plan(actions), a, b);
        Require(results.Count(r => r.Outcome == Outcome.Failed) == 1, "exactly one action fails");
        Require(results.Count(r => r.Outcome == Outcome.Done) == 9, "one failure never stops the others");
        Require(store.UnresolvedIntents("p1").Count == 1, "the failed intent stays unresolved");
    }

    private static async Task ParentFailed_ChildSkipped()
    {
        var (_, a, b, executor) = Rig();
        var source = a.Seed("d/f", "content", versionId: "v1");
        b.FailNextWrites = 1;
        var record = new RecordingWriter(b);
        var folder = Act(ActionKind.MkDir, Side.B, "d");
        var child = Act(ActionKind.CopyNew, Side.B, "d/f", source: source);

        var results = await Apply(executor, Plan(folder, child), a, record);
        Require(results.Single(r => r.ActionId == folder.ActionId).Outcome == Outcome.Failed, "the parent MkDir fails");
        var skipped = results.Single(r => r.ActionId == child.ActionId);
        Require(skipped.Outcome == Outcome.Skipped && skipped.Reason == "Parent folder not ready: d",
            "the child is Skipped for its missing parent: " + skipped.Reason);
        Require(record.Puts == 0, "no content was written into the missing folder");
    }

    // A provider backoff longer than the retry window fails the action instead of waiting.
    private static async Task Admission_ProviderBackoff_Failed()
    {
        var store = new InMemoryStateStore();
        store.SavePair(Pair());
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        var record = new RecordingWriter(b);
        var admission = Admission();
        var executor = new Executor(store, Settings(64), admission);
        admission.ProviderThrottled(new(record.TransferResourceKey, SyncProviderOperation.Write), TimeSpan.FromMinutes(6));
        var action = Act(ActionKind.CopyNew, Side.B, "f0", source: a.Seed("f0", "v0"), key: "k0");

        var results = await Apply(executor, Plan(action), a, record);
        var result = results.Single();
        Require(result.Outcome == Outcome.Failed && result.Reason!.StartsWith("HttpRequestException"),
            "a long provider backoff fails the action: " + result.Reason);
        Require(record.Puts == 0, "no effect was attempted during the backoff");
        var download = Act(ActionKind.CopyNew, Side.A, "download", source: b.Seed("download", "v1"), key: "k1");
        var downloaded = await Apply(executor, Plan(download), a, record);
        Require(downloaded.Single().Outcome == Outcome.Done,
            "the same provider's write cooldown does not block executor download admission");
    }

    private static async Task Hold_NeverExecuted()
    {
        var (store, a, b, executor) = Rig();
        var hold = new SyncAction("hold1", 5, ActionKind.Hold, Side.B, "x", null, null, null, false, null);
        var recordA = new RecordingWriter(a);
        var recordB = new RecordingWriter(b);

        var results = await Apply(executor, Plan(hold), recordA, recordB);
        Require(results.Count == 0, "a Hold produces no result");
        Require(recordA.Calls.Count == 0 && recordB.Calls.Count == 0, "a Hold never touches a writer");
        Require(store.UnresolvedIntents("p1").Count == 0, "a Hold records no intent");
    }

    private static async Task CommitOnly_AdoptForget()
    {
        var (store, a, b, executor) = Rig();
        var aState = a.Seed("x", "content", versionId: "v1");
        var bState = b.Seed("x", "content", versionId: "v1");
        SeedBaseline(store, "k2", aState, bState);
        var adopt = Act(ActionKind.Adopt, Side.B, "x", source: aState, expected: bState, key: "k1");
        var forget = Act(ActionKind.Forget, Side.B, "", key: "k2", rule: 15);
        var recordA = new RecordingWriter(a);
        var recordB = new RecordingWriter(b);

        var results = await Apply(executor, Plan(adopt, forget), recordA, recordB);
        Require(results.All(r => r.Outcome == Outcome.Done), "Adopt and Forget are Done");
        Require(recordA.Calls.Count == 0 && recordB.Calls.Count == 0, "commit-only actions never touch a writer");
        var k1 = store.Baselines("p1").Single(baseline => baseline.ObjectKey == "k1");
        Require(k1.A == aState && k1.B == bState, "Adopt writes the baseline from Source and ExpectedTarget");
        Require(store.Baselines("p1").All(baseline => baseline.ObjectKey != "k2"), "Forget removes its baseline");
        Require(store.UnresolvedIntents("p1").Count == 0, "commit-only actions leave no unresolved intent");
    }

    // Each effect must be preceded by its durable intent, and followed by its commit.
    private static async Task IntentBeforeEffect_Ordered()
    {
        var journal = new List<string>();
        var store = new JournalStore(new InMemoryStateStore(), journal);
        store.SavePair(Pair());
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        var record = new RecordingWriter(b, journal);
        var executor = new Executor(store, Settings(64), Admission());
        var actions = Enumerable.Range(0, 5)
            .Select(i => Act(ActionKind.CopyNew, Side.B, "f" + i, source: a.Seed("f" + i, "v" + i), key: "k" + i))
            .ToArray();

        var results = await Apply(executor, Plan(actions), a, record);
        Require(results.All(r => r.Outcome == Outcome.Done), "all five copies commit");
        foreach (var action in actions)
        {
            var intent = JournalIndex(journal, "intent:" + action.ActionId);
            var effect = JournalIndex(journal, "effect:" + action.ActionId);
            var commit = JournalIndex(journal, "commit:" + action.ActionId + "|" + Outcome.Done);
            Require(intent >= 0 && intent < effect && effect < commit, "intent < effect < commit for " + action.ActionId);
        }
    }

    // B-124: parallel actions share store transactions (group commit), still intent < effect < commit per action.
    private static async Task GroupCommit_SharesTransactions()
    {
        var journal = new List<string>();
        var store = new JournalStore(new InMemoryStateStore(), journal) { WriteCost = TimeSpan.FromMilliseconds(2) };
        store.SavePair(Pair());
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb") { Latency = TimeSpan.FromMilliseconds(10) };
        var record = new RecordingWriter(b, journal);
        var executor = new Executor(store, Settings(64), Admission());
        var actions = Enumerable.Range(0, 64)
            .Select(i => Act(ActionKind.CopyNew, Side.B, "f" + i, source: a.Seed("f" + i, "v" + i), key: "k" + i))
            .ToArray();

        var results = await Apply(executor, Plan(actions), a, record);
        Require(results.Count == 64 && results.All(r => r.Outcome == Outcome.Done), "all 64 copies commit");
        Require(store.IntentBatches.Sum() == 64 && store.ResultBatches.Sum() == 64,
            "every intent and every result is written exactly once");
        Require(store.IntentBatches.Count < 64 && store.ResultBatches.Count < 64,
            $"concurrent writes share transactions ({store.IntentBatches.Count} intent and " +
            $"{store.ResultBatches.Count} result batches for 64 actions)");
        foreach (var action in actions)
        {
            var effect = JournalIndex(journal, "effect:" + action.ActionId);
            Require(JournalIndex(journal, "intent:" + action.ActionId) < effect &&
                effect < JournalIndex(journal, "commit:" + action.ActionId + "|" + Outcome.Done),
                "intent < effect < commit for " + action.ActionId);
        }
    }

    // B-124: a failed result batch loses no effect: every action whose result did not commit keeps its intent, and
    // the next run's reconcile resolves each by observation.
    private static async Task GroupCommit_FailedBatch_LeavesIntentsForReconcile()
    {
        var journal = new List<string>();
        var inner = new InMemoryStateStore();
        var store = new JournalStore(inner, journal) { WriteCost = TimeSpan.FromMilliseconds(2), FailNextCommits = 1 };
        store.SavePair(Pair());
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb") { Latency = TimeSpan.FromMilliseconds(10) };
        var record = new RecordingWriter(b, journal);
        var executor = new Executor(store, Settings(64), Admission());
        var actions = Enumerable.Range(0, 16)
            .Select(i => Act(ActionKind.CopyNew, Side.B, "f" + i, source: a.Seed("f" + i, "v" + i), key: "k" + i))
            .ToArray();

        var results = new List<ActionResult>();
        await foreach (var result in executor.ApplyAsync(Plan(actions), a, record, CancellationToken.None)) results.Add(result);
        Require(results.Any(result => result.Outcome == Outcome.Uncertain),
            "a failed result commit is retained as uncertain without aborting sibling effects");
        var effects = IdsWithPrefix(journal, "effect:");
        var committed = IdsWithPrefix(journal, "commit:");
        var unresolved = inner.UnresolvedIntents("p1");
        Require(effects.Except(committed).All(id => unresolved.Any(intent => intent.ActionId == id)),
            "every effect whose result was lost with the batch keeps its intent");
        var reconciled = await executor.ReconcileAsync(unresolved, 1, a, b, CancellationToken.None);
        Require(reconciled.Where(r => effects.Contains(r.ActionId)).All(r => r.Outcome == Outcome.Done),
            "reconcile resolves every applied effect Done");
        Require(inner.Baselines("p1").Count == effects.Count, "each applied copy ends with its baseline");
    }

    private static async Task Throughput_200TinyFiles()
    {
        var (_, a, b, executor) = Rig(Settings(maxWorkers: 8));
        b.Latency = TimeSpan.FromMilliseconds(20);
        var record = new RecordingWriter(b);
        var actions = Enumerable.Range(0, 200)
            .Select(i => Act(ActionKind.CopyNew, Side.B, "f" + i, source: a.Seed("f" + i, "0123456789abcdef"), key: "k" + i))
            .ToArray();

        var watch = Stopwatch.StartNew();
        var results = await Apply(executor, Plan(actions), a, record);
        watch.Stop();
        Require(results.Count == 200 && results.All(r => r.Outcome == Outcome.Done), "all 200 tiny files land");
        Require(record.Peak == 8, $"the one content group runs the small files at the worker cap ({record.Peak})");
        Require(watch.Elapsed < TimeSpan.FromSeconds(4),
            $"throughput {200 / watch.Elapsed.TotalSeconds:F0} actions/s stays above 50");
    }

    private static async Task WorkerCap_Respected()
    {
        var (_, a, b, executor) = Rig(Settings(maxWorkers: 4));
        b.Latency = TimeSpan.FromMilliseconds(10);
        var record = new RecordingWriter(b);
        var actions = Enumerable.Range(0, 50)
            .Select(i => Act(ActionKind.CopyNew, Side.B, "f" + i, source: a.Seed("f" + i, "v" + i), key: "k" + i))
            .ToArray();

        var results = await Apply(executor, Plan(actions), a, record);
        Require(results.All(r => r.Outcome == Outcome.Done), "all 50 files land under the worker cap");
        Require(record.Peak == 4, $"peak concurrency {record.Peak} never exceeds MaxWorkers");
    }

    // Sections 4.3 and 8: small creates form one content group, so they reach the batched path together, a worker
    // cap at a time; each frees its admission slot once its bytes are sent, and the Executor's own release is a no-op.
    private static async Task Batching_SmallCreates_GroupAndReleaseAdmission()
    {
        const int Workers = 8, Files = 20, Bytes = 16;
        var store = new InMemoryStateStore();
        store.SavePair(Pair());
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        var admission = Admission();
        var executor = new Executor(store, Settings(Workers), admission);
        var batcher = new BatchingWriter(b, admission, capacity: Workers, expected: Files);
        var actions = Enumerable.Range(0, Files)
            .Select(i => Act(ActionKind.CopyNew, Side.B, "f" + i, source: a.Seed("f" + i, new string('x', Bytes)), key: "k" + i))
            .ToArray();

        var results = await Apply(executor, Plan(actions), a, batcher);
        var batches = batcher.Batches;
        Require(results.Count == Files && results.All(r => r.Outcome == Outcome.Done), "every batched create lands");
        Require(batches.Select(batch => batch.Count).SequenceEqual(new[] { 8, 8, 4 }),
            "small creates form full batches at the worker cap: " + string.Join(",", batches.Select(x => x.Count)));
        for (var i = 0; i < batches.Count; i++)
            Require(batches[i].ToHashSet().SetEquals(actions.Skip(i * Workers).Take(Workers).Select(x => x.Path)),
                $"batch {i} holds the next creates in plan order");
        Require(batcher.Leases.Count == Files && batcher.Leases.All(lease => lease is { File: true, Bytes: Bytes }),
            "each create runs under its own file lease sized by its source");
        Require(batcher.ActiveAtCommit.All(active => active == 0),
            "every slot in a batch was released once its bytes were sent, before the batch commit");
        Require(admission.Snapshot().ActiveFiles == 0, "the Executor's final release is a no-op (no double release)");
    }

    // Every started effect leaves its intent unresolved, and every committed effect is delivered.
    private static async Task Cancel_StopsLaunching_KeepsIntents()
    {
        var journal = new List<string>();
        var store = new JournalStore(new InMemoryStateStore(), journal);
        store.SavePair(Pair());
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb") { Latency = TimeSpan.FromMilliseconds(50) };
        var record = new RecordingWriter(b, journal);
        var executor = new Executor(store, Settings(64), Admission());
        var actions = Enumerable.Range(0, 100)
            .Select(i => Act(ActionKind.CopyNew, Side.B, "f" + i, source: a.Seed("f" + i, "v" + i), key: "k" + i))
            .ToArray();
        using var cts = new CancellationTokenSource();
        var results = new List<ActionResult>();

        await foreach (var result in executor.ApplyAsync(Plan(actions), a, record, cts.Token))
        {
            results.Add(result);
            if (results.Count == 1) cts.Cancel();
        }

        var intents = IdsWithPrefix(journal, "intent:");
        var committed = IdsWithPrefix(journal, "commit:");
        var effects = IdsWithPrefix(journal, "effect:");
        var unresolved = store.UnresolvedIntents("p1").Select(intent => intent.ActionId).ToHashSet(StringComparer.Ordinal);
        Require(results.Count < 100, "cancellation stops launching new actions");
        Require(effects.IsSubsetOf(intents), "no effect runs before its intent is recorded");
        Require(unresolved.SetEquals(intents.Except(committed).ToHashSet(StringComparer.Ordinal)),
            "every started but uncommitted effect keeps its intent");
        Require(results.Count == committed.Count, "every committed effect is delivered to the caller");
    }

    // If the caller stops reading early, leaving the loop drains the in-flight effect and no further effect starts.
    // The second put is held in flight until cancelled, so the state at the break is fixed, not timing-dependent.
    private static async Task Apply_EarlyBreak_StopsEffects()
    {
        var (store, a, b, executor) = Rig(Settings(maxWorkers: 1));
        var record = new RecordingWriter(b) { HoldPutsExcept = "f0" };
        var actions = Enumerable.Range(0, 100)
            .Select(i => Act(ActionKind.CopyNew, Side.B, "f" + i, source: a.Seed("f" + i, "v" + i), key: "k" + i))
            .ToArray();
        var results = new List<ActionResult>();

        await foreach (var result in executor.ApplyAsync(Plan(actions), a, record, CancellationToken.None))
        {
            results.Add(result);
            await record.PutHeld.WaitAsync(HeldEffectTimeout);
            break;
        }

        Require(results.Count == 1 && results[0].ActionId == actions[0].ActionId, "the caller leaves after one result");
        Require(record.CancelledPuts == 1 && record.Active == 0,
            "leaving waits for the producer: the held effect was cancelled and nothing is still in flight");
        Require(record.Puts == 2, $"no effect started after the held one ({record.Puts} puts)");
        Require(await b.ObserveAsync("f1", CancellationToken.None) is null, "the cancelled effect applied nothing");
        Require(store.UnresolvedIntents("p1").Select(intent => intent.ActionId).SequenceEqual(new[] { actions[1].ActionId }),
            "only the cancelled effect keeps its intent for the next run's reconcile");
    }

    private static async Task Reconcile_ThreeOutcomes()
    {
        var (store, a, b, executor) = Rig();
        var presentSource = a.Seed("present", "present-content", versionId: "v1");
        b.Seed("present", "present-content", versionId: "v1");
        var editedExpected = b.Seed("edited", "old-content", versionId: "v1");
        b.Seed("edited", "third-content", versionId: "v2");
        var editedSource = a.Seed("edited", "source-content", versionId: "v1");
        var absentSource = a.Seed("absent", "absent-content", versionId: "v1");
        var present = Act(ActionKind.CopyNew, Side.B, "present", source: presentSource, key: "k1");
        var absent = Act(ActionKind.CopyNew, Side.B, "absent", source: absentSource, key: "k2");
        var edited = Act(ActionKind.Update, Side.B, "edited", source: editedSource, expected: editedExpected, key: "k3");
        foreach (var action in new[] { present, absent, edited }) store.RecordIntent("p1", action);
        var recordA = new RecordingWriter(a);
        var recordB = new RecordingWriter(b);

        const long ReconcileGen = 7;
        var results = await executor.ReconcileAsync(new[] { present, absent, edited }, ReconcileGen, recordA, recordB,
            CancellationToken.None);
        Require(results[0].Outcome == Outcome.Done, "a copy already present resolves Done");
        Require(store.Baselines("p1").Single(baseline => baseline.ObjectKey == "k1").Gen == ReconcileGen,
            "the reconciled baseline carries the reconcile generation");
        Require(results[1].Outcome == Outcome.Skipped && results[1].Reason!.StartsWith("Not applied"), "an absent copy is not applied");
        Require(results[2].Outcome == Outcome.Skipped && results[2].Reason!.StartsWith("Superseded"), "an edited target is superseded");
        Require(recordA.Puts + recordA.Moves + recordA.Deletes + recordA.MkDirs == 0 &&
            recordB.Puts + recordB.Moves + recordB.Deletes + recordB.MkDirs == 0, "reconcile never writes");
        Require(store.UnresolvedIntents("p1").Count == 0, "every reconciled intent is closed");
    }

    private static async Task Reconcile_RecoveryFirstDone_SameRef()
    {
        var inner = new InMemoryStateStore();
        inner.SavePair(Pair());
        var store = new JournalStore(inner, new List<string>()) { FailNextCommits = 1 };
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        var victim = b.Seed("x", "content", versionId: "v1");
        SeedBaseline(inner, "k1", a.Seed("x", "content", versionId: "v1"), victim);
        var executor = new Executor(store, Settings(64), Admission());
        var action = Act(ActionKind.Delete, Side.B, "x", expected: victim, rf: true, key: "k1");

        var interrupted = new List<ActionResult>();
        await foreach (var outcome in executor.ApplyAsync(Plan(action), a, b, CancellationToken.None)) interrupted.Add(outcome);
        Require(interrupted.Any(outcome => outcome.Outcome == Outcome.Uncertain),
            "lost commit is reported uncertain, never as a completed effect");
        var intents = inner.UnresolvedIntents("p1");
        Require(intents.Any(intent => intent.ActionId == action.ActionId), "the interrupted intent is still unresolved");

        var results = await executor.ReconcileAsync(intents, 1, a, b, CancellationToken.None);
        var result = results.Single(r => r.ActionId == action.ActionId);
        Require(result.Outcome == Outcome.Done, "the observed delete resolves to Done");
        Require(result.Recovery?.Location == "memory://recovery/" + action.ActionId,
            "the recovery ref is the original one: " + result.Recovery?.Location);
    }

    private static async Task Reconcile_Move_Done_WhenSameObject()
    {
        var (store, a, b, executor) = Rig();
        var source = a.Seed("y", "content", versionId: "v1");
        b.Seed("x", "content", versionId: "v1");
        var move = await ApplyMoveAsync(b, "x", "y", source, "k1");
        store.RecordIntent("p1", move);

        var results = await executor.ReconcileAsync(new[] { move }, 1, a, new RecordingWriter(b), CancellationToken.None);
        Require(results.Single().Outcome == Outcome.Done, "a move whose destination holds the same object is Done");
    }

    // A move that precedes an update (rule 28): the moved item keeps the victim's content, which differs from Source.
    private static async Task Reconcile_Move_Done_WhenSourceContentDiffers()
    {
        var (store, a, b, executor) = Rig();
        var source = a.Seed("y", "new-content", versionId: "v1");
        b.Seed("x", "old-content", versionId: "v1");
        var move = await ApplyMoveAsync(b, "x", "y", source, "k1");
        store.RecordIntent("p1", move);

        var results = await executor.ReconcileAsync(new[] { move }, 1, a, b, CancellationToken.None);
        var result = results.Single();
        Require(result.Outcome == Outcome.Done && result.NewB?.Hash == move.ExpectedTarget!.Hash,
            "a landed move is matched against the moved item, not the opposite side: " + result.Reason);
    }

    // An unrelated (or unhashed) destination must never be accepted as a landed move.
    private static async Task Reconcile_Move_UnrelatedDestination_Superseded()
    {
        var (store, a, b, executor) = Rig();
        var expected = b.Seed("x", "content", versionId: "v1");
        var source = a.Seed("y", "content", versionId: "v1") with { Hash = null };
        b.Seed("y", "unrelated-content", versionId: "v1");
        await b.DeleteAsync(Act(ActionKind.Delete, Side.B, "x", expected: expected), CancellationToken.None);
        var move = Act(ActionKind.Move, Side.B, "y", from: "x", source: source, expected: expected, key: "k1");
        store.RecordIntent("p1", move);

        var results = await executor.ReconcileAsync(new[] { move }, 1, a, b, CancellationToken.None);
        var result = results.Single();
        Require(result.Outcome == Outcome.Skipped && result.Reason!.StartsWith("Superseded"),
            "an unrelated destination is not a landed move: " + result.Reason);
    }

    private static async Task Reconcile_Move_SourcePresent_NotApplied()
    {
        var (store, a, b, executor) = Rig();
        var source = a.Seed("y", "content", versionId: "v1");
        b.Seed("x", "content", versionId: "v1");
        var move = await ApplyMoveAsync(b, "x", "y", source, "k1");
        b.Seed("x", "content", versionId: "v2");
        store.RecordIntent("p1", move);

        var results = await executor.ReconcileAsync(new[] { move }, 1, a, b, CancellationToken.None);
        var result = results.Single();
        Require(result.Outcome == Outcome.Skipped && result.Reason!.StartsWith("Not applied"),
            "a move whose source is still present is not applied: " + result.Reason);
    }

    // A failing recovery lookup must not abort the intents after it.
    private static async Task Reconcile_RecoveryLookupFails_Continues()
    {
        var (store, a, b, executor) = Rig();
        var victim = b.Seed("x", "content", versionId: "v1");
        await b.DeleteAsync(Act(ActionKind.Delete, Side.B, "x", expected: victim), CancellationToken.None);
        var presentSource = a.Seed("present", "present-content", versionId: "v1");
        b.Seed("present", "present-content", versionId: "v1");
        var remove = Act(ActionKind.Delete, Side.B, "x", expected: victim, rf: true, key: "k1");
        var copy = Act(ActionKind.CopyNew, Side.B, "present", source: presentSource, key: "k2");
        foreach (var action in new[] { remove, copy }) store.RecordIntent("p1", action);
        var record = new RecordingWriter(b) { PreserveFailsMismatch = true };

        var results = await executor.ReconcileAsync(new[] { remove, copy }, 1, a, record, CancellationToken.None);
        Require(results[0].Outcome == Outcome.Uncertain, "a failed recovery lookup leaves the intent uncertain");
        Require(results[0].Reason!.Contains("recovery copy lookup failed") &&
            results[0].Reason!.Contains(nameof(ExpectedTargetMismatchException)),
            "the uncertain outcome names the failed recovery lookup: " + results[0].Reason);
        Require(results[1].Outcome == Outcome.Done, "reconciliation continues with the next intent");
        Require(store.UnresolvedIntents("p1").Count == 1, "only the uncertain intent stays unresolved");
    }

    private static async Task FolderDelete_ExpandsPerFile()
    {
        var (store, a, b, executor) = Rig();
        var fileOne = b.Seed("d/x", "one", versionId: "v1");
        var fileTwo = b.Seed("d/e/y", "two", versionId: "v1");
        var folderE = b.Seed("d/e", "", ItemKind.Folder, versionId: "v1");
        var folderState = b.Seed("d", "", ItemKind.Folder, versionId: "v1");
        var gen = store.BeginGeneration("rb", "snapshot");
        store.WriteSnapshot(new Snapshot("rb", Side.B, gen, "1", new[] { folderState, folderE, fileOne, fileTwo },
            Array.Empty<string>()));
        store.CompleteGeneration(gen, "1", true);
        SeedBaseline(store, "kx", a.Seed("d/x", "one", versionId: "v1"), fileOne);
        SeedBaseline(store, "ky", a.Seed("d/e/y", "two", versionId: "v1"), fileTwo);
        var record = new RecordingWriter(b);
        var folder = Act(ActionKind.Delete, Side.B, "d", expected: folderState, rf: true, key: "kd")
            with { FrozenVictimSignature = FolderDelete.Signature(FolderDelete.LiveDescendants(store, "rb", "d")) };

        var results = await Apply(executor, Plan(folder), a, record);
        Require(results.All(r => r.Outcome == Outcome.Done), "the folder and its files all delete");
        Require(record.Calls.Count(c => c == "Delete:d/x") == 1 && record.Calls.Count(c => c == "Delete:d/e/y") == 1,
            "each file under the folder gets its own delete");
        Require(record.Calls.Count(c => c.StartsWith("Preserve:")) == 2, "one recovery copy per file (owner decision 16)");
        Require(Index(record.Calls, "Delete:d/e/y") < Index(record.Calls, "Delete:d/e") &&
            Index(record.Calls, "Delete:d/e") < Index(record.Calls, "Delete:d"), "folders are removed bottom-up");
        Require(await b.ObserveAsync("d", CancellationToken.None) is null, "the folder is gone");
    }

    // finding 4: a folder Delete freezes its descendants at plan time. A child that appeared (or
    // changed) after the freeze makes the Executor skip the whole folder as Superseded, never a partial subtree
    // delete; the next run replans.
    private static async Task FolderDelete_FrozenVictimsChanged_Skipped()
    {
        var (store, a, b, executor) = Rig();
        var fileX = b.Seed("d/x", "one", versionId: "v1");
        var folderState = b.Seed("d", "", ItemKind.Folder, versionId: "v1");
        var gen = store.BeginGeneration("rb", "snapshot");
        store.WriteSnapshot(new Snapshot("rb", Side.B, gen, "1", new[] { folderState, fileX }, Array.Empty<string>()));
        store.CompleteGeneration(gen, "1", true);
        SeedBaseline(store, "kx", a.Seed("d/x", "one", versionId: "v1"), fileX);
        var frozen = FolderDelete.Signature(FolderDelete.LiveDescendants(store, "rb", "d"));
        // A child that was not part of the plan arrives after the freeze and reaches the store.
        var late = b.Seed("d/new", "new", versionId: "v1");
        var next = store.BeginGeneration("rb", "snapshot");
        store.WriteSnapshot(new Snapshot("rb", Side.B, next, "2", new[] { folderState, fileX, late },
            Array.Empty<string>()));
        store.CompleteGeneration(next, "2", true);
        var record = new RecordingWriter(b);
        var folder = Act(ActionKind.Delete, Side.B, "d", expected: folderState, rf: true, key: "kd")
            with { FrozenVictimSignature = frozen };

        var results = await Apply(executor, Plan(folder), a, record);
        Require(results.Count == 1 && results[0].Outcome == Outcome.Skipped &&
            results[0].Reason == EffectResolver.Superseded,
            "a folder whose frozen descendants changed is skipped as Superseded");
        Require(record.Calls.Count == 0 && await b.ObserveAsync("d", CancellationToken.None) is not null &&
            await b.ObserveAsync("d/new", CancellationToken.None) is not null,
            "no partial subtree delete runs for the changed folder");
        Console.WriteLine("PASS FolderDelete_FrozenVictimsChanged_Skipped");
    }

    // A descendant whose delete fails keeps its folder: the folder's own delete is Skipped, never run over content.
    private static async Task FolderDelete_DescendantFails_FolderKept()
    {
        var (store, a, b, executor) = Rig();
        var fileX = b.Seed("d/x", "one", versionId: "v1");
        var fileY = b.Seed("d/y", "two", versionId: "v1");
        var folderState = b.Seed("d", "", ItemKind.Folder, versionId: "v1");
        var gen = store.BeginGeneration("rb", "snapshot");
        store.WriteSnapshot(new Snapshot("rb", Side.B, gen, "1", new[] { folderState, fileX, fileY }, Array.Empty<string>()));
        store.CompleteGeneration(gen, "1", true);
        SeedBaseline(store, "kx", a.Seed("d/x", "one", versionId: "v1"), fileX);
        SeedBaseline(store, "ky", a.Seed("d/y", "two", versionId: "v1"), fileY);
        var record = new RecordingWriter(b) { FailDeleteAt = "d/x" };
        var folder = Act(ActionKind.Delete, Side.B, "d", expected: folderState, rf: true, key: "kd");

        var results = await Apply(executor, Plan(folder), a, record);
        var byPath = results.ToDictionary(r => r.ActionId == folder.ActionId ? "d" : r.ActionId[(folder.ActionId.Length + 1)..]);
        Require(byPath["d/x"].Outcome == Outcome.Failed, "the failing descendant delete is Failed");
        Require(byPath["d/y"].Outcome == Outcome.Done, "the other descendant still deletes");
        Require(byPath["d"].Outcome == Outcome.Skipped && byPath["d"].Reason == "Folder not emptied: d/x",
            "the folder delete is Skipped for its undeleted child: " + byPath["d"].Reason);
        Require(!record.Calls.Contains("Delete:d"), "the folder delete never reaches the writer");
        Require(await b.ObserveAsync("d", CancellationToken.None) is not null &&
            await b.ObserveAsync("d/x", CancellationToken.None) is not null, "the folder and its failed file remain");
        Require(store.UnresolvedIntents("p1").Select(intent => intent.ActionId)
                .SequenceEqual(new[] { folder.ActionId + ":d/x" }), "only the failed descendant keeps its intent");
        var keys = store.Baselines("p1").Select(baseline => baseline.ObjectKey).ToHashSet();
        Require(keys.Contains("kx") && !keys.Contains("ky"), "the kept file keeps its baseline; the deleted one loses it");
    }

    private static async Task<SyncAction> ApplyMoveAsync(FakeEndpoint target, string from, string to, ItemState source,
        string key)
    {
        var expected = await target.ObserveAsync(from, CancellationToken.None)
            ?? throw new InvalidOperationException("no item at " + from);
        var action = Act(ActionKind.Move, Side.B, to, from: from, source: source, expected: expected, key: key);
        await target.MoveAsync(action, CancellationToken.None);
        return action;
    }

    private static V2Settings Settings(int maxWorkers) => V2Settings.Default with { MaxWorkers = maxWorkers };

    /// <summary>An admission controller under calm, fixed pressure; the run checks reuse it for the real Executor.</summary>
    internal static SyncTransferResourceController Admission() =>
        new(new SyncTransferSettings(ResourceMode: "Performance"), TimeProvider.System,
            () => new SyncResourcePressure(0, 0.1, 8L << 30));

    private static (InMemoryStateStore Store, FakeEndpoint A, FakeEndpoint B, Executor Executor) Rig(
        V2Settings? settings = null)
    {
        var store = new InMemoryStateStore();
        store.SavePair(Pair());
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        return (store, a, b, new Executor(store, settings ?? Settings(64), Admission()));
    }

    private static SyncAction Act(ActionKind kind, Side target, string path, string? from = null,
        ItemState? source = null, ItemState? expected = null, bool rf = false, string? key = null, int rule = 2) =>
        new("a-" + kind + "-" + target + "-" + path + "-" + (key ?? "none"), rule, kind, target, path, from, source,
            expected, rf, key);

    private static Plan Plan(params SyncAction[] actions) => new("p1", 1, actions, Array.Empty<Notice>());

    private static PairDef Pair() => new("p1", "p1",
        new RootDef("ra", "Local", null, "C:\\a", "id:local:1"),
        new RootDef("rb", "Dropbox", "acc", "/b", "id:dbx:1"),
        Array.Empty<string>(), Array.Empty<string>(), true);

    private static void SeedBaseline(InMemoryStateStore store, string key, ItemState a, ItemState b)
    {
        var action = new SyncAction("seed-" + key, 1, ActionKind.Adopt, Side.B, b.Path, null, a, b, false, key);
        store.RecordIntent("p1", action);
        store.CommitResult("p1", 1, action, new ActionResult(action.ActionId, Outcome.Done, a, b, null, null));
    }

    private static async Task<List<ActionResult>> Apply(Executor executor, Plan plan, IEndpointWriter a,
        IEndpointWriter b, CancellationToken ct = default)
    {
        var results = new List<ActionResult>();
        await foreach (var result in executor.ApplyAsync(plan, a, b, ct)) results.Add(result);
        return results;
    }

    private static int Index(IReadOnlyList<string> calls, string call)
    {
        for (var i = 0; i < calls.Count; i++)
            if (calls[i] == call) return i;
        throw new InvalidOperationException("missing recorded call: " + call);
    }

    private static int JournalIndex(IReadOnlyList<string> journal, string entry)
    {
        for (var i = 0; i < journal.Count; i++)
            if (journal[i] == entry) return i;
        return -1;
    }

    private static HashSet<string> IdsWithPrefix(IReadOnlyList<string> journal, string prefix)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in journal)
        {
            if (!entry.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = entry[prefix.Length..];
            var cut = rest.IndexOf('|');
            ids.Add(cut < 0 ? rest : rest[..cut]);
        }
        return ids;
    }

    private static async Task ExpectAsync<TException>(Func<Task> operation) where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException)
        {
            Console.WriteLine("PASS rejects via " + typeof(TException).Name);
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    /// <summary>Test-only IStateStore that journals intents/commits and can throw from CommitResult (a crash).</summary>
    private sealed class JournalStore : IStateStore
    {
        private readonly InMemoryStateStore _inner;
        private readonly List<string> _journal;

        internal JournalStore(InMemoryStateStore inner, List<string> journal)
        {
            _inner = inner;
            _journal = journal;
        }

        public IEnumerable<SyncRetry> ReadSyncRetries(string pairId) => _inner.ReadSyncRetries(pairId);
        public SyncRetry? SyncRetryAt(string pairId, string path) => _inner.SyncRetryAt(pairId, path);
        public IReadOnlyList<SyncRetry> DueSyncRetries(string pairId, DateTimeOffset now, int limit) => _inner.DueSyncRetries(pairId, now, limit);
        public int CountSyncRetries(string pairId) => _inner.CountSyncRetries(pairId);
        public bool RetryProtected(string pairId, string path, IReadOnlySet<string> active, bool descendants, bool restoredOnly = false) => _inner.RetryProtected(pairId, path, active, descendants, restoredOnly);
        public void SaveSyncRetry(string pairId, SyncRetry retry) => _inner.SaveSyncRetry(pairId, retry);
        public void ClearSyncRetry(string pairId, string path) => _inner.ClearSyncRetry(pairId, path);
    public void SuspendHeldRetry(string pairId, SyncRetry retry) => _inner.SuspendHeldRetry(pairId, retry);
        public void RequireRehash(string rootId, ItemState item, string reason) => _inner.RequireRehash(rootId, item, reason);
        public bool CompleteRehash(string rootId, ItemState item, string hash) => _inner.CompleteRehash(rootId, item, hash);
        public IReadOnlyList<ItemState> RehashCandidates(string rootId, int limit) => _inner.RehashCandidates(rootId, limit);
        public long RehashWatermark(string rootId) => _inner.RehashWatermark(rootId);
        public IReadOnlyList<ItemState> RehashCandidates(string rootId, int limit, long through) => _inner.RehashCandidates(rootId, limit, through);
        public IReadOnlyList<string> ObjectPaths(string rootId, string objectId, int offset, int limit) => _inner.ObjectPaths(rootId, objectId, offset, limit);
        public void AcknowledgeRehash(string rootId, long through, Func<string, bool> settled, CancellationToken ct = default) => _inner.AcknowledgeRehash(rootId, through, settled, ct);

        internal int FailNextCommits { get; set; }

        /// <summary>Fails a whole intent transaction when its actions match, standing in for a store write failure.</summary>
        internal Func<IReadOnlyList<SyncAction>, bool>? FailIntentsWhen { get; set; }

        /// <summary>A simulated cost per store transaction, so concurrent writes queue behind a running one.</summary>
        internal TimeSpan WriteCost { get; set; }

        /// <summary>The size of each intent and result batch the executor wrote (one store transaction each).</summary>
        internal List<int> IntentBatches { get; } = new();
        internal List<int> ResultBatches { get; } = new();

        /// <summary>The actual actions of each intent transaction, so a test can prove which writes shared one.</summary>
        internal List<IReadOnlyList<SyncAction>> IntentActionBatches { get; } = new();

        public void RecordIntents(string pairId, IReadOnlyList<SyncAction> actions)
        {
            if (FailIntentsWhen is { } fail && fail(actions))
                throw new IOException("simulated intent transaction failure");
            lock (_journal)
            {
                IntentBatches.Add(actions.Count);
                IntentActionBatches.Add(actions);
            }
            if (WriteCost > TimeSpan.Zero) Thread.Sleep(WriteCost);
            foreach (var action in actions) RecordIntent(pairId, action);
        }

        public void CommitResults(string pairId, long gen, IReadOnlyList<(SyncAction Action, ActionResult Result)> results)
        {
            lock (_journal) ResultBatches.Add(results.Count);
            if (WriteCost > TimeSpan.Zero) Thread.Sleep(WriteCost);
            foreach (var (action, result) in results) CommitResult(pairId, gen, action, result);
        }

        public void RecordIntent(string pairId, SyncAction a)
        {
            lock (_journal) _journal.Add("intent:" + a.ActionId);
            _inner.RecordIntent(pairId, a);
        }

        public void CommitResult(string pairId, long gen, SyncAction a, ActionResult r)
        {
            if (FailNextCommits > 0)
            {
                FailNextCommits--;
                throw new IOException("simulated crash between effect and commit");
            }
            lock (_journal) _journal.Add("commit:" + a.ActionId + "|" + r.Outcome);
            _inner.CommitResult(pairId, gen, a, r);
        }

        public long BeginGeneration(string rootId, string kind) => _inner.BeginGeneration(rootId, kind);
        public void WriteSnapshot(Snapshot s) => _inner.WriteSnapshot(s);
        public void WriteDelta(Delta d) => _inner.WriteDelta(d);
        public void CompleteGeneration(long gen, string cursor, bool complete) => _inner.CompleteGeneration(gen, cursor, complete);
        public string? Cursor(string rootId) => _inner.Cursor(rootId);
        public void ForgetCursor(string rootId) => _inner.ForgetCursor(rootId);
        public bool FirstSyncDone(string pairId) => _inner.FirstSyncDone(pairId);
        public void MarkFirstSyncDone(string pairId) => _inner.MarkFirstSyncDone(pairId);
        public IReadOnlyList<ItemState> Live(string rootId, IEnumerable<string>? paths = null) => _inner.Live(rootId, paths);
        public IReadOnlyList<ItemState> LiveUnder(string rootId, IEnumerable<string> pathPrefixes) => _inner.LiveUnder(rootId, pathPrefixes);
        public IReadOnlyList<ItemState> LiveChildren(string rootId, string directory) => _inner.LiveChildren(rootId, directory);
    public IReadOnlyList<string> DirectorySpellings(string rootId, string path) => _inner.DirectorySpellings(rootId, path);

    public ItemState? ByObject(string rootId, string objectId) => _inner.ByObject(rootId, objectId);
        public IReadOnlyList<PairDef> Pairs() => _inner.Pairs();
        public PairDef? Pair(string pairId) => _inner.Pair(pairId);
        public void SavePair(PairDef p) => _inner.SavePair(p);
        public IReadOnlyList<Baseline> Baselines(string pairId, IEnumerable<string>? objectKeys = null) => _inner.Baselines(pairId, objectKeys);
        public IReadOnlyList<Baseline> BaselinesAt(string pairId, Side side, IEnumerable<string> paths) =>
            _inner.BaselinesAt(pairId, side, paths);

        public IReadOnlyList<Baseline> BaselinesTouching(string pairId, Side side, IEnumerable<string> objectIds,
            IEnumerable<string> pathPrefixes) => _inner.BaselinesTouching(pairId, side, objectIds, pathPrefixes);
        public IReadOnlyList<SyncAction> UnresolvedIntents(string pairId) => _inner.UnresolvedIntents(pairId);
        public void AddNotices(IEnumerable<Notice> n) => _inner.AddNotices(n);
        public IReadOnlyList<Notice> OpenNotices(string? pairId) => _inner.OpenNotices(pairId);
        public IReadOnlyList<RecoveryRecord> Recoveries(string pairId) => _inner.Recoveries(pairId);
        public void CloseNotice(string noticeId, string? choice) => _inner.CloseNotice(noticeId, choice);
        public void Prune() => _inner.Prune();
        public void Dispose() => _inner.Dispose();
    }
}
