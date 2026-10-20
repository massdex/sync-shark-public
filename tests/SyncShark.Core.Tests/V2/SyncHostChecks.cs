using System.Text.Json;

namespace Massdex.SyncShark.V2;

/// <summary>Checks the v2 host's mapped, empty ("E"), not-in-v2 ("X") and pass-through members against fakes.</summary>
internal static class SyncHostChecks
{
    private const string ConnectionA = "connection-fixture-3";
    private const string ConnectionB = "connection-fixture-4";

    public static async Task RunAsync()
    {
        await Pairs_MapToSyncPair();
        await SelectPair_Remembered_UnknownRefused();
        await SetPairEnabled_SavesPair();
        await Run_MapsObservedResults();
        await Run_RefusedPair_IsItemFailure_NextPairRuns();
        await Run_UnrequestedCancellation_IsItemFailure_NotStopped();
        await Run_Stop_SetsStopped_LaterPairsNotStarted_ActiveCleared();
        await Run_Stop_BetweenPairs_NextPairNotStarted();
        await Stop_DelayedPastBatchEnd_DoesNotStopNextBatch();
        await ActiveRun_ShowsProgress();
        await Progress_BlockingWindow_RunContinues_OrderedFinalDelivered();
        await Progress_FailedDelivery_ShownOnLogChannel();
        await Progress_StuckWindow_BatchEndsWithinBound(blockSynchronously: false);
        await Progress_StuckWindow_BatchEndsWithinBound(blockSynchronously: true);
        await Progress_AbandonedReader_TouchesNothingOfTheNextBatch();
        await Progress_AbandonedDelivery_ReaderExitsSilently();
        await Waiter_RegisteredOnPair1_CompletesAtBatchEnd();
        await Notices_Page_FiltersSearchGroupScopePaging();
        await Notices_KeepBoth_ClosesWithAccept();
        await Notices_UseSide_NotInV2_StaysOpen();
        await Notices_UserDecision_CannotBeAccepted();
        await Notices_StaleSelection_ClosesNothing();
        await Notices_ContentFromLiveRows_NoBytes();
        await NoticeOptionsAsync();
        await Recovery_CatalogAndUsage();
        await PendingCount_And_Attention();
        await NotInV2_CommandsThrow_QueriesEmpty();
        await Sharing_PairScoped_NotInV2_AccountForwards();
        await FolderSelectionPage_EmptyButValid();
        await Disconnect_RefusedWhileV2PairUsesAccount();
        await History_MapsRetainedSummaries();
        Console.WriteLine("v2 host checks passed.");
    }

    private static async Task Pairs_MapToSyncPair()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Beta", excluded: ["skip"], ignoreRules: ["FileExtension:.tmp"]));
        store.MarkFirstSyncDone("p1");
        var set = await Host(store).LoadPairsAsync();
        var pair = set.Pairs.Single();
        Require(pair.Id == "p1" && pair.Name == "Beta", "id and name map");
        Require(pair.LocalRoot == @"C:\root\p1" && pair.DropboxRoot == "/root/p1", "roots map");
        Require(pair.LocalVolumeSerialNumber == 0x0a1b2c3d && pair.LocalRootFileId == 0xdeadbeefcafebabe,
            "the local object id maps to volume and file id");
        Require(pair.DropboxFolderId == "id:dropbox:p1", "the dropbox folder id maps");
        Require(pair.Enabled, "enabled maps");
        Require(pair.PropagateDeletions, "deletions propagate");
        Require(pair.ConnectionId == ConnectionA && pair.Provider == SyncProviders.Dropbox,
            "connection id and provider map");
        Require(pair.InitialReviewApproved == true, "first-sync-done maps to InitialReviewApproved");
        Require(pair.RecoveryRetentionDays == 30, "recovery retention days map");
        Require(pair.SelectionRules is [{ FolderPath: "skip", Included: false }], "exclusions map to selection rules");
        Require(pair.IgnoreRules is [{ Kind: SyncIgnoreKind.FileExtension }], "ignore rules map through the shared parser");
        Require(set.SelectedPairId == "p1", "the first pair is selected");
    }

    private static async Task SelectPair_Remembered_UnknownRefused()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        store.SavePair(PairDefOf("p2", "Beta"));
        var host = Host(store);
        var selected = await host.SelectPairAsync("p2");
        Require(selected.SelectedPairId == "p2", "the selected pair is remembered");
        var thrown = await Throws<InvalidDataException>(() => host.SelectPairAsync("nope"));
        Require(thrown.Message.Contains("not retained", StringComparison.Ordinal), "an unknown pair id is refused");
    }

    private static async Task SetPairEnabled_SavesPair()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha", enabled: true));
        var host = Host(store);
        var set = await host.SetPairEnabledAsync("p1", false);
        Require(!set.Pairs.Single().Enabled, "the returned pair is disabled");
        Require(store.Pair("p1")!.Enabled == false, "the store row is disabled");
        store.SavePair(PairDefOf("p2", "Clash", enabled: false) with { A = store.Pair("p1")!.A });
        var refused = await Throws<InvalidOperationException>(() => host.SetPairEnabledAsync("p2", true));
        Require(refused.Message.Contains("exact folder", StringComparison.Ordinal) && !store.Pair("p2")!.Enabled,
            "enabling a pair that shares another pair's root is refused and leaves it off");
    }

    private static async Task Run_MapsObservedResults()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        var plan = new Plan("p1", 1,
        [
            Action("a1", ActionKind.CopyNew, Side.B, "a1.txt"),
            Action("a2", ActionKind.Update, Side.A, "a2.txt"),
            Action("a3", ActionKind.Delete, Side.B, "a3.txt"),
            Action("a4", ActionKind.MkDir, Side.B, "a4"),
            Action("a5", ActionKind.Hold, Side.B, "a5.txt"),
            Action("a6", ActionKind.Update, Side.B, "a6.txt"),
            Action("a7", ActionKind.Update, Side.B, "a7.txt"),
            Action("a8", ActionKind.Update, Side.B, "a8.txt")
        ], []);
        var executor = new ScriptedExecutor
        {
            Results =
            [
                Done("a1"), Done("a2"), Done("a3"), Done("a4"),
                new ActionResult("a6", Outcome.Failed, null, null, null, "boom"),
                new ActionResult("a7", Outcome.Uncertain, null, null, null, "unknown"),
                new ActionResult("a8", Outcome.Skipped, null, null, null, "later change")
            ]
        };
        var observed = new ObservedExecutor(executor);
        var run = new ScriptedRun(observed, plan, Report("p1", RunOutcomes.Complete));
        var host = Host(store, run, observed);

        var batch = await host.RunPairsAsync([PairOf("p1", "Alpha")], "manual");
        var item = batch.Items.Single();
        var decisions = item.Result!.Decisions;
        Require(decisions.Count == 7, "bookkeeping actions (MkDir) produce no row");
        Require(decisions[0].Kind == SyncDecisionKind.Conflict && decisions[0].RelativePath == "a5.txt",
            "a held action is a conflict row");
        Require(decisions[1].Kind == SyncDecisionKind.Uploaded, "a done copy to the cloud is an upload");
        Require(decisions[2].Kind == SyncDecisionKind.Downloaded, "a done update to local is a download");
        Require(decisions[3].Kind == SyncDecisionKind.Archived, "a done delete is archived");
        Require(decisions[4].Kind == SyncDecisionKind.Blocked && decisions[4].Reason == "boom", "a failure is blocked");
        Require(decisions[5].Kind == SyncDecisionKind.ResponseUnknown, "an uncertain effect needs verification");
        Require(decisions[6].Kind == SyncDecisionKind.Skipped, "a skipped action keeps its reason");
        Require(item.Result.Uploaded == 1 && item.Result.Downloaded == 1, "transfer totals count one each way");
        Require(!batch.Stopped && item.FailureType is null, "a complete run is not a failure");
    }

    private static async Task Run_RefusedPair_IsItemFailure_NextPairRuns()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        store.SavePair(PairDefOf("p2", "Beta"));
        var observed = new ObservedExecutor(new ScriptedExecutor());
        var run = new ScriptedRun(observed, new Plan("p1", 1, [], []), Report("p1", RunOutcomes.Complete))
        {
            RefuseFor = id => id == "p1" ? new SyncRunRefusedException("p1 is off") : null
        };
        var host = Host(store, run, observed);

        var batch = await host.RunPairsAsync([PairOf("p1", "Alpha"), PairOf("p2", "Beta")], "manual");
        Require(batch.Items.Count == 2, "the refused pair does not stop the next pair");
        Require(batch.Items[0].FailureType == "refused" && batch.Items[0].FailureMessage == "p1 is off",
            "the refused pair is an item failure");
        Require(batch.Items[1].FailureType is null, "the second pair ran");
    }

    // A cancellation the batch did not ask for (a provider timeout) fails that pair only; a cancelled batch stops.
    private static async Task Run_UnrequestedCancellation_IsItemFailure_NotStopped()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        store.SavePair(PairDefOf("p2", "Beta"));
        var observed = new ObservedExecutor(new ScriptedExecutor());
        var run = new ScriptedRun(observed, new Plan("p1", 1, [], []), Report("p1", RunOutcomes.Complete))
        {
            RefuseFor = id => id == "p1" ? new TaskCanceledException("provider timed out") : null
        };
        var host = Host(store, run, observed);

        var batch = await host.RunPairsAsync([PairOf("p1", "Alpha"), PairOf("p2", "Beta")], "manual");
        Require(!batch.Stopped && batch.Items.Count == 2, "a provider timeout does not stop the batch");
        Require(batch.Items[0].FailureType == "failed" && batch.Items[0].FailureMessage == "provider timed out",
            "a provider timeout is the pair's failure");
        Require(batch.Items[1].FailureType is null, "the next pair runs after a provider timeout");

        using var caller = new CancellationTokenSource();
        run.RefuseFor = null;
        run.AfterPair = (_, token) => { caller.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        var cancelled = await host.RunPairsAsync([PairOf("p1", "Alpha"), PairOf("p2", "Beta")], "manual",
            cancellationToken: caller.Token);
        Require(cancelled.Stopped && cancelled.Items.Count == 0, "the caller's cancellation stops the batch");
    }

    private static async Task Run_Stop_SetsStopped_LaterPairsNotStarted_ActiveCleared()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        store.SavePair(PairDefOf("p2", "Beta"));
        var observed = new ObservedExecutor(new ScriptedExecutor());
        var run = new ScriptedRun(observed, new Plan("p1", 1, [], []), Report("p1", RunOutcomes.Stopped));
        var host = Host(store, run, observed);

        var batch = await host.RunPairsAsync([PairOf("p1", "Alpha"), PairOf("p2", "Beta")], "manual");
        Require(batch.Stopped, "a stopped report sets Stopped");
        Require(batch.Items.Count == 1, "later pairs are not started");
        Require(host.ActiveRun is null && !host.HasActiveRun, "the active run is cleared");
        Require(!await host.StopActiveRunAsync(), "stopping with no run is false");
    }

    private static async Task ActiveRun_ShowsProgress()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        var observed = new ObservedExecutor(new ScriptedExecutor());
        var run = new ScriptedRun(observed, new Plan("p1", 1, [], []), Report("p1", RunOutcomes.Complete),
            [new RunProgress("Applying", "a.txt", 3, 9)]);
        var host = Host(store, run, observed);

        var reported = new List<SyncAuthorityBatchProgress>();
        var activeDuringReport = new List<SyncActiveRunSnapshot?>();
        await host.RunPairsAsync([PairOf("p1", "Alpha")], "manual", progress =>
        {
            reported.Add(progress);
            activeDuringReport.Add(host.ActiveRun);
            return Task.CompletedTask;
        });

        var comparison = reported.Single().Comparison;
        Require(comparison is { Phase: "Applying", RelativePath: "a.txt", CompletedChecks: 3, TotalChecks: 9 },
            "phase, path and counts reach onProgress");
        Require(activeDuringReport.Single()?.Progress?.Comparison?.RelativePath == "a.txt",
            "the active run snapshot carries the progress");
    }

    private static async Task Notices_Page_FiltersSearchGroupScopePaging()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha", connectionId: ConnectionA));
        store.SavePair(PairDefOf("p2", "Beta", connectionId: ConnectionB));
        store.AddNotices(
        [
            new Notice("p1", 7, NoticeKind.KeepBoth, "dir/a.txt", "kept", "test-reason"),
            new Notice("p1", 7, NoticeKind.Info, "dir/b.txt", "info", "test-reason"),
            new Notice("p2", 7, NoticeKind.KeepBoth, "dir/c.txt", "kept", "test-reason")
        ]);
        var host = Host(store);

        var inScope = await host.LoadOpenConflictComparisonsPageAsync(50, 0, null, connectionId: ConnectionA);
        Require(inScope.Count == 2, "the account scope keeps only that connection's notices");
        var searched = await host.LoadOpenConflictComparisonsPageAsync(50, 0, "a.txt");
        Require(searched.Count == 1 && searched[0].RelativePath == "dir/a.txt", "search filters by path");
        var grouped = await host.LoadOpenConflictComparisonsPageAsync(50, 0, null, "Kept both");
        Require(grouped.Count == 2, "the group filter matches the kind's label");
        var allGroups = await host.LoadOpenConflictComparisonsPageAsync(50, 0, null, "All groups");
        Require(allGroups.Count == 3, "All groups is no filter");
        var firstPage = await host.LoadOpenConflictComparisonsPageAsync(1, 0, null);
        var secondPage = await host.LoadOpenConflictComparisonsPageAsync(1, 1, null);
        Require(firstPage.Count == 1 && secondPage.Count == 1 &&
            firstPage[0].ConflictId != secondPage[0].ConflictId, "paging returns distinct rows");
        Require(firstPage[0].Status == "open" && firstPage[0].Generation == 0, "a comparison is open at generation 0");
    }

    private static async Task Notices_KeepBoth_ClosesWithAccept()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        store.AddNotices([new Notice("p1", 7, NoticeKind.KeepBoth, "a.txt", "kept both", "test-reason")]);
        var noticeId = store.OpenNotices("p1").Single().NoticeId!;
        var host = Host(store);

        var selection = await host.CaptureOpenConflictSelectionAsync(null);
        Require(selection.Count == 1, "the selection captures the open notice");
        var batch = await host.ResolveConflictSelectionCommandAsync(
            [new SyncConflictResolutionRequest(selection[0], SyncAuthorityConflictResolutionKind.PreserveBoth)]);
        Require(batch.Items.Single().Result!.Decisions.Single().Kind == SyncDecisionKind.Recovered,
            "the resolved notice is a recovered decision");
        Require(store.ClosedChoice(noticeId) == NoticeChoices.Accept, "the notice is closed with the accept choice");
        Require(store.OpenNotices("p1").Count == 0, "the notice is no longer open");
    }

    private static async Task Notices_UseSide_NotInV2_StaysOpen()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        store.AddNotices([new Notice("p1", 7, NoticeKind.KeepBoth, "a.txt", "kept both", "test-reason")]);
        var host = Host(store);
        var selection = await host.CaptureOpenConflictSelectionAsync(null);

        var thrown = await Throws<NotSupportedException>(() => host.ResolveConflictSelectionCommandAsync(
            [new SyncConflictResolutionRequest(selection[0], SyncAuthorityConflictResolutionKind.KeepLocal)]));
        Require(thrown.Message == "Applying a chosen version is not in core v2 yet. Start Sync Shark without --core v2 " +
            "to use it.", "using a side says it is not in v2 yet");
        Require(store.OpenNotices("p1").Count == 1, "the notice stays open");
    }

    private static async Task Notices_UserDecision_CannotBeAccepted()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        store.AddNotices([new Notice("p1", 5, NoticeKind.UserDecision, "a.txt", "needs a decision", "test-reason")]);
        var host = Host(store);
        var selection = await host.CaptureOpenConflictSelectionAsync(null);

        await Throws<InvalidOperationException>(() => host.ResolveConflictSelectionCommandAsync(
            [new SyncConflictResolutionRequest(selection[0], SyncAuthorityConflictResolutionKind.PreserveBoth)]));
        Require(store.OpenNotices("p1").Count == 1, "a held decision cannot be closed");
    }

    private static async Task Notices_StaleSelection_ClosesNothing()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        store.AddNotices([new Notice("p1", 7, NoticeKind.KeepBoth, "a.txt", "kept", "test-reason")]);
        store.AddNotices([new Notice("p1", 7, NoticeKind.KeepBoth, "b.txt", "kept", "test-reason")]);
        var host = Host(store);
        var selection = await host.CaptureOpenConflictSelectionAsync(null);
        Require(selection.Count == 2, "two notices are open");
        store.CloseNotice(selection[0].ConflictId, NoticeChoices.Accept);

        await Throws<InvalidOperationException>(() => host.ResolveConflictSelectionCommandAsync(
        [
            new SyncConflictResolutionRequest(selection[0], SyncAuthorityConflictResolutionKind.PreserveBoth),
            new SyncConflictResolutionRequest(selection[1], SyncAuthorityConflictResolutionKind.PreserveBoth)
        ]));
        Require(store.OpenNotices("p1").Single().NoticeId == selection[1].ConflictId,
            "a stale selection closes nothing, the other notice stays open");
    }

    public static async Task NoticeOptionsSqliteAsync(string root)
    {
        Directory.CreateDirectory(root);
        string id;
        using (var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System))
        {
            store.SavePair(PairDefOf("p1", "Alpha"));
            store.AddNotices([new Notice("p1", 38, NoticeKind.Info, "", "binding changed",
                "cloud-files-binding-needs-review")]);
            var host = Host(store);
            var selection = (await host.CaptureOpenConflictSelectionAsync(null)).Single();
            id = selection.ConflictId;
            await host.ResolveConflictSelectionCommandAsync([new(selection,
                SyncAuthorityConflictResolutionKind.PreserveBoth, NoticeChoices.ReviewRebind)]);
        }
        using (var reopened = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System))
            Require(reopened.OpenNotices("p1").Count == 0, "recorded choice survives store reopen");
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = SqliteStateStore.DatabasePath(root), Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT choice FROM notice WHERE notice_id=$id AND state='closed'";
        command.Parameters.AddWithValue("$id", id);
        Require((string?)command.ExecuteScalar() == NoticeChoices.ReviewRebind,
            "existing SQLite choice and state columns retain the pending review choice");
        // Inject only a storage failure, offline: pause persists but acknowledgement fails.
        using (var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System))
        using (var write = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=" + SqliteStateStore.DatabasePath(root)))
        {
            store.AddNotices([new Notice("p1", 38, NoticeKind.Info, "", "binding changed",
                "cloud-files-binding-needs-review")]);
            write.Open();
            using var trigger = write.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER fail_notice_close BEFORE UPDATE OF state ON notice " +
                "WHEN NEW.state='closed' BEGIN SELECT RAISE(ABORT, 'test close failure'); END;";
            trigger.ExecuteNonQuery();
            var host = Host(store);
            var selection = (await host.CaptureOpenConflictSelectionAsync(null)).Single();
            var request = new SyncConflictResolutionRequest(selection,
                SyncAuthorityConflictResolutionKind.PreserveBoth, NoticeChoices.PausePair);
            await Throws<Microsoft.Data.Sqlite.SqliteException>(() => host.ResolveConflictSelectionCommandAsync([request]));
            Require(!store.Pair("p1")!.Enabled && store.OpenNotices("p1").Count == 1,
                "acknowledgement failure leaves paused pair and retryable notice");
            trigger.CommandText = "DROP TRIGGER fail_notice_close";
            trigger.ExecuteNonQuery();
            await host.ResolveConflictSelectionCommandAsync([request]);
            Require(store.OpenNotices("p1").Count == 0 && !store.Pair("p1")!.Enabled,
                "retry safely completes acknowledgement without reversing pause");
        }
        Console.WriteLine("PASS durable notice choice recording, reopen and interrupted pause retry");
    }

    public static async Task NoticeOptionsAsync()
    {
        foreach (var choice in new[] { NoticeChoices.ReviewRebind, NoticeChoices.PausePair, NoticeChoices.LeaveAsIs })
        {
            using var store = new InMemoryStateStore();
            store.SavePair(PairDefOf("p1", "Alpha"));
            var other = PairDefOf("p2", "Beta");
            // A different path must also have a different physical folder identity.
            store.SavePair(other with { A = other.A with { RootObjectId = "id:local:0a1b2c3d:deadbeefcafebabf" } });
            var notice = new Notice("p1", 38, NoticeKind.Info, "", "binding changed",
                "cloud-files-binding-needs-review");
            store.AddNotices([notice]);
            var host = Host(store);
            var row = (await host.LoadOpenConflictComparisonsAsync()).Single();
            Require(row.Options?.Select(option => option.Choice).SequenceEqual(
                new[] { NoticeChoices.ReviewRebind, NoticeChoices.PausePair, NoticeChoices.LeaveAsIs }) == true,
                "stored reason projects three explicit options");
            Require(PlanNotices.AwaitsAcknowledgement(notice), "ordinary sync cannot close the binding notice");
            var selection = (await host.CaptureOpenConflictSelectionAsync(null)).Single();
            var request = new SyncConflictResolutionRequest(selection,
                SyncAuthorityConflictResolutionKind.PreserveBoth, choice);
            await Throws<InvalidOperationException>(() => host.ResolveConflictSelectionCommandAsync(
                [request with { NoticeChoice = "invented" }]));
            await Throws<InvalidOperationException>(() => host.ResolveConflictSelectionCommandAsync(
                [request with { NoticeChoice = null }]));
            await Throws<InvalidOperationException>(() => host.ResolveConflictSelectionCommandAsync(
                [request], connectionId: ConnectionB));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Throws<OperationCanceledException>(() => host.ResolveConflictSelectionCommandAsync(
                [request], cancellationToken: cancelled.Token));
            Require(store.Pair("p1")!.Enabled && store.OpenNotices("p1").Count == 1,
                "invalid, wrong-account and cancelled requests change nothing");
            var result = await host.ResolveConflictSelectionCommandAsync([request]);
            Require(store.ClosedChoice(selection.ConflictId) == choice, "existing resolution stores the exact option");
            Require(store.Pair("p1")!.Enabled == (choice != NoticeChoices.PausePair) && store.Pair("p2")!.Enabled,
                "only pause turns off the chosen pair");
            Require(result.Items.Single().Result!.Decisions.Single().Reason == NoticeChoices.Result(choice),
                "operation result explains pending rebind or completed choice");
            await Throws<InvalidOperationException>(() => host.ResolveConflictSelectionCommandAsync([request]));
            store.AddNotices([notice]);
            Require(store.OpenNotices("p1").Count == 1, "a recurring binding failure remains visible on restart");
            await host.ResolveConflictSelectionCommandAsync([request]);
            if (choice == NoticeChoices.PausePair)
            {
                await host.SetPairEnabledAsync("p1", true);
                Require(store.Pair("p1")!.Enabled, "normal pair enable resumes paused pair");
            }
        }
        Console.WriteLine("PASS notice option operations: projection, recording, pause/resume, cancellation, stale retry, pending rebind");
    }

    private static async Task Notices_ContentFromLiveRows_NoBytes()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        Seed(store, "r:local:p1", Side.A, "id:local:0a1b2c3d:deadbeefcafebabe", "a.txt", "hashL", 11);
        Seed(store, "r:dropbox:p1", Side.B, "id:dropbox:a", "a.txt", "hashB", 22);
        store.AddNotices([new Notice("p1", 7, NoticeKind.KeepBoth, "a.txt", "kept both", "test-reason")]);
        var host = Host(store);

        var content = await host.LoadConflictContentAsync("p1", "a.txt");
        Require(content.LocalSize == 11 && content.DropboxSize == 22, "sizes come from the live rows");
        Require(content.LocalHash == "hashL" && content.DropboxHash == "hashB", "hashes come from the live rows");
        Require(content.LocalBytes is null && content.DropboxBytes is null, "no content bytes are read");
        Require(content.Note?.Contains("Content preview is not in core v2 yet.", StringComparison.Ordinal) == true,
            "the note says content preview is not in v2");
    }

    private static async Task Recovery_CatalogAndUsage()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        var host = Host(store);
        Record(store, "p1", "a.txt", 10);
        Record(store, "p1", "b.txt", 20);
        Record(store, "p1", "c.txt", 30);

        var page = await host.LoadRecoveryCatalogPageAsync("p1", pageSize: 2);
        Require(page.Items.Count == 2 && page.HasMoreArchives, "the catalog pages and reports more");
        Require(page.NextArchiveOffset == 2, "the next archive offset advances");
        Require(page.Items[0].RetainedUtc >= page.Items[1].RetainedUtc, "rows are newest first");
        Require(page.Items[0].Health == "retained" && page.Items[0].IdentityJson == "{}", "retained mapping defaults");
        var searched = await host.LoadRecoveryCatalogPageAsync("p1", search: "b.txt");
        Require(searched.Items.Single().RelativePath == "b.txt", "search filters the catalog");
        var choices = await host.LoadRecoveryPairChoicesAsync();
        Require(choices.Single().Id == "p1" && choices.Single().Active, "the pair is a recovery choice");

        var usage = await host.LoadRecoveryUsageAsync("p1");
        Require(usage.ItemCount == 3 && usage.Size == 60, "usage counts items and sums size");
        Require(usage.RetentionDays == 30 && usage.CleanupEligibleCount == 0, "usage reports retention and no expired");
        var future = new FixedTime(DateTimeOffset.UtcNow.AddDays(40));
        var expired = await Host(store, future).LoadRecoveryUsageAsync("p1");
        Require(expired.CleanupEligibleCount == 3, "records past their expiry are eligible");
    }

    private static async Task PendingCount_And_Attention()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha", connectionId: ConnectionA));
        store.SavePair(PairDefOf("p2", "Beta", connectionId: ConnectionB));
        store.RecordIntent("p1", Action("i1", ActionKind.Update, Side.B, "a.txt"));
        store.RecordIntent("p1", Action("i2", ActionKind.Update, Side.B, "b.txt"));
        store.RecordIntent("p2", Action("i3", ActionKind.Update, Side.B, "c.txt"));
        store.AddNotices(
        [
            new Notice("p1", 7, NoticeKind.KeepBoth, "a.txt", "kept", "test-reason"),
            new Notice("p2", 5, NoticeKind.UserDecision, "c.txt", "decide", "test-reason")
        ]);
        var host = Host(store);

        Require(await host.LoadPendingQueueCountAsync() == 3, "the pending count sums unresolved intents");
        Require(await host.LoadPendingQueueCountAsync(connectionId: ConnectionA) == 2,
            "the pending count respects the account scope");
        var attention = await host.LoadPairIdsNeedingAttentionAsync();
        Require(attention.Count == 1 && attention.Contains("p2"), "only a held decision needs attention");
    }

    // the window's Runs surface reads retained run summaries through the v2 host.
    private static async Task History_MapsRetainedSummaries()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        var started = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
        store.RecordRunSummary(new RunSummary(0, "run1", "p1", started, started.AddSeconds(5),
            RunOutcomes.Partial, 3, 2, 1, 0, 0, 0, 1, 0, 0, 0, 0));
        var host = Host(store);
        var row = (await host.LoadRunHistoryAsync(50, 0)).Single();
        Require(row.Id == "run1" && row.Kind == "run_pair" && row.Status == "blocked" &&
            row.StartedUtc == started && row.CompletedUtc == started.AddSeconds(5),
            "a retained summary maps its identity, kind, status and times");
        using (var request = JsonDocument.Parse(row.RequestJson))
            Require(request.RootElement.GetProperty("pairIds")[0].GetString() == "p1",
                "the retained request names its pair");
        using (var result = JsonDocument.Parse(row.ResultJson!))
        {
            var pair = result.RootElement.GetProperty("pairs")[0];
            Require(pair.GetProperty("uploaded").GetInt32() == 2 &&
                pair.GetProperty("downloaded").GetInt32() == 1 &&
                pair.GetProperty("blocked").GetInt32() == 0 && pair.GetProperty("conflicts").GetInt32() == 1 &&
                pair.GetProperty("needsVerification").GetInt32() == 0 &&
                pair.GetProperty("notAttempted").GetInt32() == 0 && pair.GetProperty("failed").GetInt32() == 0,
                "the retained result carries the counts the Runs classifier reads");
        }
        Require((await host.LoadRunByIdAsync("run1"))?.Id == "run1" &&
            (await host.LoadRunByIdAsync("missing")) is null, "one retained run is readable by id");
    }

    private static async Task NotInV2_CommandsThrow_QueriesEmpty()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        var host = Host(store);

        var thrown = ThrowsSync<NotSupportedException>(() => host.RetirePairAsync("p1"));
        Require(thrown.Message == "Removing a pair is not in core v2 yet. Start Sync Shark without --core v2 to use it.",
            "a command not in v2 throws the one message");
        Require(ThrowsSync<NotSupportedException>(() => host.RefreshBootstrapInventoryAsync("p1")).Message
            .StartsWith("First-sync review in the app is not in core v2 yet.", StringComparison.Ordinal),
            "the message names the missing command");
        Require(ThrowsSync<NotSupportedException>(() => host.ChangeManagedRootStorageAsync("p1", "c",
            SyncDirectoryStorage.CloudOnly, 1)).Message.StartsWith("Online-only storage is not in core v2 yet.",
            StringComparison.Ordinal), "another command shares the same wording");
        // Discovery and Explorer status are pair-scoped: a v2 pair id must never reach the v1 pair store.
        Require(ThrowsSync<NotSupportedException>(() => host.CheckFastDiscoveryAsync("p1")).Message
            .StartsWith("The fast discovery check is not in core v2 yet.", StringComparison.Ordinal),
            "the discovery check is refused, not forwarded to v1");
        Require((await host.LoadExplorerStatusPageAsync("p1")).Count == 0,
            "Explorer status is empty, not read from v1");

        Require((await host.LoadQueueProjectionAsync()).Count == 0, "a queue query is empty");
        Require((await host.LoadMonitorProjectionAsync()).Count == 0, "a monitor query is empty");
        Require((await host.LoadRewindSessionsAsync()).Count == 0, "a rewind query is empty");
        Require((await host.LoadBootstrapReviewAsync("p1")).Count == 0, "a first-sync query is empty");
        Require((await host.LoadRunHistoryAsync()).Count == 0, "a run-history query is empty");
        Require(await host.LoadRunByIdAsync("r1") is null, "a run-by-id query is null");
        Require(await host.LoadLastSuccessfulSyncUtcAsync() is null, "the last-sync query is null");
        Require(host.ActivityLogError is null && !host.ActivityLogIncomplete && host.ActivityLogPendingCount == 0,
            "the activity-log queries are neutral");
    }

    private static async Task Disconnect_RefusedWhileV2PairUsesAccount()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha", connectionId: ConnectionA));
        var host = Host(store);

        var thrown = await Throws<InvalidOperationException>(() => host.DisconnectDropboxAsync(connectionId: ConnectionA));
        Require(thrown.Message == "Remove or switch the v2 pairs that use this account first.",
            "a disconnect is refused while a v2 pair uses the account");
        await Throws<InvalidOperationException>(() => host.DisconnectDropboxAsync(connectionId: null));
    }

    private static async Task Run_Stop_BetweenPairs_NextPairNotStarted()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        store.SavePair(PairDefOf("p2", "Beta"));
        var observed = new ObservedExecutor(new ScriptedExecutor());
        V2SyncHost? host = null;
        Task<bool>? stop = null;
        var run = new ScriptedRun(observed, new Plan("p1", 1, [], []), Report("p1", RunOutcomes.Complete))
        {
            // Not awaited here: a stop completes when its batch completes, after this pair returns.
            AfterPair = (id, _) => { if (id == "p1") stop = host!.StopActiveRunAsync(); return Task.CompletedTask; }
        };
        host = Host(store, run, observed);

        var batch = await host.RunPairsAsync([PairOf("p1", "Alpha"), PairOf("p2", "Beta")], "manual");
        Require(batch.Stopped, "a stop between pairs sets Stopped");
        Require(batch.Items.Count == 1, "the next pair never starts after a stop between pairs");
        Require(await stop!, "the stop completes with its batch");
    }

    private static async Task Stop_DelayedPastBatchEnd_DoesNotStopNextBatch()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        store.SavePair(PairDefOf("p2", "Beta"));
        var observed = new ObservedExecutor(new ScriptedExecutor());
        V2SyncHost? host = null;
        Task<bool>? stop = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new ScriptedRun(observed, new Plan("p1", 1, [], []), Report("p1", RunOutcomes.Complete))
        {
            AfterPair = async (id, token) =>
            {
                if (id == "p1")
                {
                    // A slow cancellation callback holds the stop of batch A until batch B is running.
                    token.Register(() => release.Task.Wait(TimeSpan.FromSeconds(3)));
                    stop = host!.StopActiveRunAsync();
                    return;
                }
                release.TrySetResult();
                await stop!;
            }
        };
        host = Host(store, run, observed);

        var first = await host.RunPairsAsync([PairOf("p1", "Alpha"), PairOf("p2", "Beta")], "manual");
        Require(first.Stopped && first.Items.Count == 1, "the delayed stop stopped batch A");
        var second = await host.RunPairsAsync([PairOf("p2", "Beta")], "manual");
        Require(await stop!, "the delayed stop reports that batch A was active");
        Require(!second.Stopped && second.Items.Single().FailureType is null,
            "a stop delayed past batch A's end does not stop batch B");
    }

    private static async Task Progress_BlockingWindow_RunContinues_OrderedFinalDelivered()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        var observed = new ObservedExecutor(new ScriptedExecutor());
        var reports = Enumerable.Range(1, 20).Select(i => new RunProgress("Applying", "f" + i, i, 20)).ToArray();
        using var runPastReports = new ManualResetEventSlim();
        var run = new ScriptedRun(observed, new Plan("p1", 1, [], []), Report("p1", RunOutcomes.Complete), reports)
        {
            AfterPair = (_, _) => { runPastReports.Set(); return Task.CompletedTask; }
        };
        var host = Host(store, run, observed);

        var delivered = new List<long>();
        bool? runContinued = null;
        await host.RunPairsAsync([PairOf("p1", "Alpha")], "manual", progress =>
        {
            // The first delivery blocks until the run has passed all its reports: on the run worker it never would.
            runContinued ??= runPastReports.Wait(TimeSpan.FromSeconds(3));
            delivered.Add(progress.Comparison!.CompletedChecks);
            return Task.CompletedTask;
        });

        Require(runContinued == true, "a blocking progress callback does not block the run worker");
        Require(delivered.Zip(delivered.Skip(1)).All(step => step.First < step.Second),
            "progress reaches the window in order, older pending reports coalesced");
        Require(delivered[^1] == 20, "the final progress is delivered before the batch completes");
    }

    private static async Task Progress_StuckWindow_BatchEndsWithinBound(bool blockSynchronously)
    {
        var bound = TimeSpan.FromSeconds(5);
        var kind = blockSynchronously ? "a blocking" : "a never-completing";
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        var observed = new ObservedExecutor(new ScriptedExecutor());
        var run = new ScriptedRun(observed, new Plan("p1", 1, [], []), Report("p1", RunOutcomes.Complete),
            [new RunProgress("Applying", "a.txt", 1, 2), new RunProgress("Applying", "b.txt", 2, 2)]);
        var host = Host(store, run, observed,
            settings: V2Settings.Default with { ProgressDrainTimeout = TimeSpan.FromMilliseconds(200) });
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var first = host.RunPairsAsync([PairOf("p1", "Alpha")], "manual", _ =>
            {
                entered.TrySetResult();
                if (!blockSynchronously) return new TaskCompletionSource().Task;
                release.Wait();
                return Task.CompletedTask;
            });
            await entered.Task.WaitAsync(bound);
            Require(await host.StopActiveRunAsync().WaitAsync(bound),
                "Stop returns despite " + kind + " progress callback");
            var batch = await first.WaitAsync(bound);
            Require(batch.Items.Count == 1 && !host.HasActiveRun,
                "the batch completes within the drain bound despite " + kind + " progress callback");
            Require(host.ActivityLogIncomplete && host.ActivityLogError ==
                "The window did not take the last sync progress in time; that progress was not shown.",
                "the abandoned delivery is shown on the host's log-error channel");
            var next = await host.RunPairsAsync([PairOf("p1", "Alpha")], "manual").WaitAsync(bound);
            Require(next.Items.Count == 1, "another batch can start after " + kind + " progress callback");
        }
        finally
        {
            release.Set();
        }
    }

    private static async Task Progress_AbandonedReader_TouchesNothingOfTheNextBatch()
    {
        var bound = TimeSpan.FromSeconds(5);
        const string abandoned = "The window did not take the last sync progress in time; that progress was not shown.";
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        var observed = new ObservedExecutor(new ScriptedExecutor());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var run = new ScriptedRun(observed, new Plan("p1", 1, [], []), Report("p1", RunOutcomes.Complete),
            [new RunProgress("Applying", "a.txt", 1, 2), new RunProgress("Applying", "b.txt", 2, 2)])
        {
            // Barrier: the second report is posted only once the window's callback holds the first.
            AfterReport = index => { if (index == 0) entered.Wait(bound); }
        };
        var host = Host(store, run, observed,
            settings: V2Settings.Default with { ProgressDrainTimeout = TimeSpan.FromMilliseconds(100) });
        var oldDeliveries = 0;
        try
        {
            // Batch 1: its callback is admitted and blocks; the second report stays pending and is abandoned.
            var first = await host.RunPairsAsync([PairOf("p1", "Alpha")], "manual", _ =>
            {
                Interlocked.Increment(ref oldDeliveries);
                entered.Set();
                release.Wait();
                return Task.CompletedTask;
            }).WaitAsync(bound);
            Require(first.Items.Count == 1 && host.ActivityLogError == abandoned, "batch 1's stuck delivery is abandoned");

            // Batch 2 delivers while batch 1's callback is still blocked.
            run.AfterReport = null;
            var delivered = new List<long>();
            await host.RunPairsAsync([PairOf("p1", "Alpha")], "manual", progress =>
            {
                delivered.Add(progress.Comparison!.CompletedChecks);
                return Task.CompletedTask;
            }).WaitAsync(bound);
            Require(delivered is [.., 2L] && Volatile.Read(ref oldDeliveries) == 1 && host.ActivityLogError == abandoned,
                "batch 2 delivers its progress while batch 1's callback is blocked");
        }
        finally
        {
            release.Set();
        }
    }

    // The reader-exit boundary is not visible through the host, so it is checked on the delivery itself: once an
    // abandoned callback returns (here by throwing), its reader exits without delivering or reporting more.
    private static async Task Progress_AbandonedDelivery_ReaderExitsSilently()
    {
        var bound = TimeSpan.FromSeconds(5);
        var pair = PairOf("p1", "Alpha");
        SyncAuthorityBatchProgress At(int done) =>
            RunResultMapping.ToProgress(pair, new RunProgress("Applying", "f" + done, done, 9),
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = 0;
        var errors = 0;
        var delivery = new ProgressDelivery(_ =>
        {
            Interlocked.Increment(ref deliveries);
            entered.TrySetResult();
            release.Wait();
            throw new InvalidOperationException("late failure");
        }, _ => Interlocked.Increment(ref errors));
        try
        {
            delivery.Post(At(1));
            await entered.Task.WaitAsync(bound);
            delivery.Post(At(2));
            Require(!await delivery.CompleteAsync(TimeSpan.FromMilliseconds(100)), "the stuck delivery is abandoned");
            release.Set();
            Require(await delivery.CompleteAsync(bound), "the abandoned reader exits once its callback returns");
            Require(Volatile.Read(ref deliveries) == 1 && Volatile.Read(ref errors) == 0,
                "the abandoned reader delivers nothing more and publishes no late error");
        }
        finally
        {
            release.Set();
        }
    }
    private static async Task Progress_FailedDelivery_ShownOnLogChannel()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        var observed = new ObservedExecutor(new ScriptedExecutor());
        var run = new ScriptedRun(observed, new Plan("p1", 1, [], []), Report("p1", RunOutcomes.Complete),
            [new RunProgress("Applying", "a.txt", 1, 1)]);
        var host = Host(store, run, observed);

        var batch = await host.RunPairsAsync([PairOf("p1", "Alpha")], "manual",
            _ => throw new InvalidOperationException("window closed"));
        Require(!batch.Stopped && batch.Items.Single().FailureType is null, "a failed delivery does not fail the run");
        Require(host.ActivityLogIncomplete &&
            host.ActivityLogError == "Sync Shark could not show sync progress: window closed",
            "a failed delivery is shown on the host's log-error channel");
    }

    private static async Task Waiter_RegisteredOnPair1_CompletesAtBatchEnd()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        store.SavePair(PairDefOf("p2", "Beta"));
        var observed = new ObservedExecutor(new ScriptedExecutor());
        V2SyncHost? host = null;
        Task<bool>? waiter = null;
        bool? completeDuringPair2 = null;
        var run = new ScriptedRun(observed, new Plan("p1", 1, [], []), Report("p1", RunOutcomes.Complete))
        {
            AfterPair = (id, _) =>
            {
                if (id == "p1") waiter = host!.WaitForActiveRunCheckpointAsync(TimeSpan.FromSeconds(30));
                else completeDuringPair2 = waiter!.IsCompleted;
                return Task.CompletedTask;
            }
        };
        host = Host(store, run, observed);

        await host.RunPairsAsync([PairOf("p1", "Alpha"), PairOf("p2", "Beta")], "manual");
        Require(completeDuringPair2 == false, "a waiter registered on pair 1 is still waiting during pair 2");
        Require(await waiter!, "a waiter registered on pair 1 completes at the end of the whole batch");
    }

    private static async Task Sharing_PairScoped_NotInV2_AccountForwards()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        var host = Host(store);

        var thrown = ThrowsSync<NotSupportedException>(() => host.GetSharingItemAsync("p1", "a.txt"));
        Require(thrown.Message == "Sharing a folder pair is not in core v2 yet. Start Sync Shark without --core v2 to " +
            "use it.", "pair-scoped sharing is refused, so a v2 pair id never reaches the v1 pair store");
        Require(ThrowsSync<NotSupportedException>(() => host.GetGoogleDriveWebLocationAsync("p1", "account")).Message
            .StartsWith("Sharing a folder pair", StringComparison.Ordinal),
            "another pair-scoped sharing member is refused the same way");

        // Account-only sharing still forwards: it reaches the (null here) v1 host instead of being refused.
        var forwarded = false;
        try { await host.CreateAccountSharingClientAsync("connection-a"); }
        catch (NotSupportedException) { }
        catch (Exception) { forwarded = true; }
        Require(forwarded, "account-only sharing still forwards to the v1 host");
    }

    private static async Task FolderSelectionPage_EmptyButValid()
    {
        var store = new InMemoryStateStore();
        store.SavePair(PairDefOf("p1", "Alpha"));
        var host = Host(store);

        var page = await host.LoadFolderSelectionPageAsync("p1", 1, 0, refreshIndex: false);
        Require(page is not null, "the folder-selection page is a valid, non-null page");
        var returned = page!;
        Require(returned.PairId == "p1" && returned.Folders.Count == 0 && !returned.HasMore,
            "the empty page names the pair and holds no folders");
        // The pair-preview consumer reads these two members straight off the page.
        Require(returned.Rollup.ExcludedItems == 0 && returned.Rollup.OnlineOnlyItems == 0,
            "the page carries a rollup so the consumer works");
    }

    // ---- helpers ----
    private static V2SyncHost Host(IStateStore store, TimeProvider? time = null)
    {
        var observed = new ObservedExecutor(new ScriptedExecutor());
        var run = new ScriptedRun(observed, new Plan("p1", 1, [], []), Report("p1", RunOutcomes.Complete));
        return Host(store, run, observed, time);
    }

    private static V2SyncHost Host(IStateStore store, ISyncRun run, ObservedExecutor observed,
        TimeProvider? time = null, V2Settings? settings = null) => new(store, run, observed, v1: null!,
        settings ?? V2Settings.Default, time ?? TimeProvider.System, @"F:\v2-ui2\syncshark.db");

    private static PairDef PairDefOf(string id, string name, bool enabled = true, string? connectionId = ConnectionA,
        IReadOnlyList<string>? excluded = null, IReadOnlyList<string>? ignoreRules = null) => new(
        id, name,
        new RootDef("r:local:" + id, SyncProviders.Local, null, @"C:\root\" + id, "id:local:0a1b2c3d:deadbeefcafebabe"),
        new RootDef("r:dropbox:" + id, SyncProviders.Dropbox, connectionId, "/root/" + id, "id:dropbox:" + id),
        excluded ?? [], ignoreRules ?? [], enabled);

    private static SyncPair PairOf(string id, string name) =>
        PairMapping.ToSyncPair(PairDefOf(id, name), false, V2Settings.Default);

    private static SyncAction Action(string id, ActionKind kind, Side target, string path) =>
        new(id, 5, kind, target, path, null, null, null, false, "k:" + id);

    private static ActionResult Done(string id) => new(id, Outcome.Done, null, null, null, null);

    private static RunReport Report(string pairId, string outcome) =>
        new(pairId, outcome, 0, 0, 0, 0, 0, 0, 0, 0, []);

    private static void Seed(InMemoryStateStore store, string rootId, Side side, string objectId, string path,
        string hash, long size)
    {
        var gen = store.BeginGeneration(rootId, "full");
        store.WriteSnapshot(new Snapshot(rootId, side, gen, "c1",
            [new ItemState(path, ItemKind.File, objectId, "v1", hash, size, DateTimeOffset.UnixEpoch, ItemFlags.None)],
            []));
        store.CompleteGeneration(gen, "c1", true);
    }

    private static void Record(InMemoryStateStore store, string pairId, string path, long size)
    {
        var action = Action("r:" + path, ActionKind.Update, Side.B, path);
        store.CommitResult(pairId, 1, action,
            new ActionResult(action.ActionId, Outcome.Done, null, null,
                new RecoveryRef(Side.B, "archive/" + path, "v1", "h:" + path, size), null));
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private static async Task<TException> Throws<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException error)
        {
            return error;
        }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }

    private static TException ThrowsSync<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException error)
        {
            return error;
        }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
