using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

// SS-D01/SS-D02/SS-D03 real-consumer checks on the actual MainWindow and the actual application state.
//
// SS-D02 is driven through the real run-presentation entry (ShowResult), the real Home labelled summary,
// the real retained-run projection and the exact title/message/flag the tray passes to its balloon. SS-D03
// drives the real Save pair path over an authoritative binding input and proves that each committed-save
// follow-up fails and is retried separately, with one Bind and one pair. SS-D01 drives the real first-sync
// approval handler and the real Stop control and proves that the token the queued work received is the one
// the stop cancelled, and that one invocation's finalization cannot touch a later run's token.
//
// The isolated state root is the fixture's own; no provider, credential, network, signing, install or
// user-folder effect is performed and no original product state is read or written.
internal static class RunOutcomeAndPairSaveChecks
{
    private const string PairId = "outcome-pair";
    private const string PairName = "Outcome Pair";
    private const string AccountId = "outcome-account";
    private const string RowStep = "the pair list refresh";
    private const string MonitorStep = "the local monitor refresh";
    private const string StorageStep = "the storage settings";
    private const string ExplorerStep = "the Explorer integration refresh";

    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var state = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(state.StateAuthority));
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var passed = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidDataException(label);
            passed++;
            Console.WriteLine("PASS " + label);
        }
        Action<bool, string> check = Check;
        var window = new MainWindow(state.StateAuthority, theme)
        {
            Width = 1240, Height = 790, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            window.Show();
            Drain();
            CheckLatestRunConsumers(window, check);
            CheckRetainedOutcomeShapes(window, check);
            CheckPairSaveFollowUps(window, check);
            CheckQueuedFirstSyncStop(window, state.StateAuthority, root, check);
            CheckQueuedHostInvocationStop(window, state.StateAuthority, root, check);
            Console.WriteLine("PASS " + passed + " real-consumer run-outcome, retained-summary, pair-save and " +
                "queued-first-sync-stop assertions on isolated state; no provider, credential, network, signing, " +
                "install or user-folder effect");
            return 0;
        }
        finally
        {
            MainWindow.PairBindingForTest = null;
            MainWindow.PairSaveFollowUpFailureStepForTest = null;
            MainWindow.BootstrapActionPlanForTest = null;
            MainWindow.BootstrapApprovalConfirmationForTest = null;
            MainWindow.BootstrapReviewLoaderForTest = null;
            MainWindow.BootstrapReviewPageLoaderForTest = null;
            MainWindow.MonitorStopperForTest = null;
            MainWindow.QueuedInvocationHoldForTest = null;
            MainWindow.SuppressLoadedStartupForTest = false;
            window.RequestProductExit();
            app.Shutdown();
        }
    }

    // ---- SS-D02: the real Home consumer and the exact tray decision -------------------------------

    private static void CheckLatestRunConsumers(MainWindow window, Action<bool, string> check)
    {
        var pair = SeedOutcomePair(window);
        void Present(SyncRunResult result)
        {
            Call(window, "ShowResult", pair, result);
            Call(window, "RefreshWorkspace");
            Drain();
        }

        Present(new SyncRunResult(pair.Id, [
            new SyncDecision("upload.txt", SyncDecisionKind.Uploaded, "Uploaded the reviewed change."),
            new SyncDecision("same.txt", SyncDecisionKind.Identical, "Already identical.")]));
        var clean = window.LatestRunForTray;
        check(clean is not null && clean.Outcome.IsSuccess && clean.Outcome.Headline == "Sync Completed" &&
              !clean.Outcome.Attention &&
              clean.Outcome.NotificationDetail == "The latest run finished and is available in Activity." &&
              HomeStatus(window) == "Finished",
            "the actual success is the only case the tray states as Sync Completed: " + HomeStatus(window));

        Present(new SyncRunResult(pair.Id, [
            new SyncDecision("uploaded.txt", SyncDecisionKind.Uploaded, "Uploaded before the stop."),
            new SyncDecision("lost.txt", SyncDecisionKind.ResponseUnknown, "The response was lost."),
            new SyncDecision("<run>", SyncDecisionKind.Stopped, "Stopped at the user's request.")]));
        var stopped = window.LatestRunForTray;
        check(stopped is not null && stopped.Outcome.Kind == SyncRunOutcomeKind.Stopped &&
              stopped.Outcome.Headline == "Sync Stopped" && stopped.Outcome.Attention &&
              stopped.Outcome.NotificationDetail.Contains("was stopped", StringComparison.Ordinal),
            "a stopped run with unverified work never renders the completion notification");
        check(HomeStatus(window) == "Stopped" &&
              HomeCounts(window).GetValueOrDefault("Needs verification:") == "1" &&
              HomeCounts(window).GetValueOrDefault("Uploaded:") == "1",
            "Home states the stop and keeps its unverified and completed counts visible: " + HomeStatus(window));

        Present(new SyncRunResult(pair.Id, [
            new SyncDecision("lost.txt", SyncDecisionKind.ResponseUnknown, "The response was lost.")]));
        var verifying = window.LatestRunForTray;
        check(verifying is not null && verifying.Outcome.Kind == SyncRunOutcomeKind.NeedsAttention &&
              verifying.Outcome.Headline == "Sync Needs Attention" && verifying.Outcome.Attention &&
              verifying.Outcome.AttentionItems == 0 && verifying.Outcome.NeedsVerification == 1,
            "an unverified run with nothing failed is not a success and never claims a completion");
        check(HomeStatus(window) == "Needs Attention",
            "Home no longer states the unfinished outcome as Finished: " + HomeStatus(window));

        Present(new SyncRunResult(pair.Id, [
            new SyncDecision("conflict.txt", SyncDecisionKind.Conflict, "Both folders changed."),
            new SyncDecision("deferred.txt", SyncDecisionKind.Blocked, "Deferred while the other pair runs.")
                { NotAttempted = true }]));
        check(window.LatestRunForTray is { Outcome.Kind: SyncRunOutcomeKind.NeedsAttention } &&
              HomeStatus(window) == "Needs Attention" &&
              HomeCounts(window).GetValueOrDefault("Not attempted:") == "1" &&
              HomeCounts(window).GetValueOrDefault("Needs attention:") == "2",
            "a conflict plus not-attempted work is attention, with the not-attempted item stated separately");

        Present(new SyncRunResult(pair.Id, [
            new SyncDecision("later.txt", SyncDecisionKind.Skipped, "Skipped for now at the reviewer's request.")]));
        check(window.LatestRunForTray is { Outcome.IsSuccess: true } skipped &&
              skipped.Outcome.Headline == "Sync Completed" &&
              HomeCounts(window).GetValueOrDefault("Skipped:") == "1" &&
              HomeCounts(window).GetValueOrDefault("Needs attention:") == "0",
            "a deliberate skip keeps its own line without turning the run into attention");
    }

    // ---- SS-D02: absent, malformed and complete retained records ----------------------------------

    private static void CheckRetainedOutcomeShapes(MainWindow window, Action<bool, string> check)
    {
        var retained = Field<List<SyncAuthorityRunHistory>>(window, "_retainedRuns");
        var workspaceRuns = (IList)Field<object>(window, "_workspaceRuns");
        workspaceRuns.Clear();
        retained.Clear();
        var completed = DateTimeOffset.UtcNow;
        var request = "{\"pairIds\":[\"" + PairId + "\"]}";
        void Show(SyncAuthorityRunHistory run)
        {
            retained.Clear();
            retained.Add(run);
            Call(window, "RefreshWorkspace");
            Drain();
        }

        Show(new SyncAuthorityRunHistory("run-malformed", "run_all", completed.AddMinutes(-3), completed,
            "completed", request, JsonSerializer.Serialize(new
            {
                pairs = new[] { new { pairId = PairId, uploaded = (int?)null } }
            })));
        check(HomeStatus(window) == "Outcome not recorded" &&
              HomeCounts(window).GetValueOrDefault("Counts:") == "Not recorded" &&
              window.LatestRunForTray is { Outcome.Kind: SyncRunOutcomeKind.NotRecorded } malformed &&
              malformed.Outcome.Headline != "Sync Completed" && malformed.Outcome.Attention,
            "absent retained counts state an unrecorded outcome and never reach the tray as a completion");

        Show(new SyncAuthorityRunHistory("run-unreadable", "run_all", completed.AddMinutes(-4), completed,
            "completed", request, "{oops"));
        check(HomeCounts(window).GetValueOrDefault("Counts:") == "Not recorded (unreadable retained result)" &&
              window.LatestRunForTray is { Outcome.Kind: SyncRunOutcomeKind.NotRecorded } unreadable &&
              unreadable.Outcome.Headline != "Sync Completed",
            "a malformed retained result is stated as unreadable and never as a completion");

        Show(new SyncAuthorityRunHistory("run-clean", "run_all", completed.AddMinutes(-5), completed,
            "completed", request, JsonSerializer.Serialize(new
            {
                pairCount = 1, completedPairs = 1, failedPairs = 0, Stopped = false,
                pairs = new[]
                {
                    new { pairId = PairId, pairName = PairName, uploaded = 3, downloaded = 1, unchanged = 4,
                        conflicts = 0, blocked = 0, failed = 0, needsVerification = 0, notAttempted = 0,
                        skipped = 0, stopped = false, failureType = (string?)null, failureMessage = "" }
                }
            })));
        check(HomeStatus(window) == "Finished" &&
              HomeCounts(window).GetValueOrDefault("Uploaded:") == "3" &&
              HomeCounts(window).GetValueOrDefault("Unchanged:") == "4" &&
              window.LatestRunForTray is { Outcome.IsSuccess: true } clean &&
              clean.Outcome.Headline == "Sync Completed",
            "a complete clean retained record is the one that reaches the completion headline");

        Show(new SyncAuthorityRunHistory("run-stopped", "run_all", completed.AddMinutes(-6), completed,
            "cancelled", request, JsonSerializer.Serialize(new
            {
                Stopped = true,
                pairs = new[]
                {
                    new { pairId = PairId, pairName = PairName, uploaded = 1, downloaded = 0, unchanged = 2,
                        conflicts = 0, blocked = 0, failed = 0, needsVerification = 1, notAttempted = 0,
                        skipped = 0, stopped = true, failureType = (string?)null, failureMessage = "" }
                }
            })));
        check(HomeStatus(window) == "Stopped" &&
              HomeCounts(window).GetValueOrDefault("Needs verification:") == "1" &&
              window.LatestRunForTray is { Outcome.Kind: SyncRunOutcomeKind.Stopped } retainedStopped &&
              retainedStopped.Outcome.NeedsVerification == 1,
            "a stopped retained run states the stop and keeps its unresolved count visible");

        retained.Clear();
        workspaceRuns.Clear();
    }

    // ---- SS-D03: the committed save versus its follow-ups -----------------------------------------

    private static void CheckPairSaveFollowUps(MainWindow window, Action<bool, string> check)
    {
        Pump(window.AuthorityHost.SaveAutomationAsync(window.AuthorityHost.LoadAutomation() with { Enabled = false }));
        var saves = 0;
        var monitorStops = 0;
        MainWindow.MonitorStopperForTest = () => { monitorStops++; return Task.CompletedTask; };
        var saved = new SyncPair(PairId, PairName, Path.Combine(Path.GetTempPath(), "sync-shark-outcome"),
            1, 1, "/Outcome", "id:outcome", ConnectionId: AccountId, FilesOnDemandEnabled: true);
        SyncPairBindingResult Bound() => new(new SyncPairSet([saved], saved.Id), saved,
            new PilotSettings(saved.LocalRoot, saved.LocalVolumeSerialNumber, saved.LocalRootFileId,
                saved.DropboxRoot, saved.DropboxFolderId), IdentityChanged: false);

        // A pre-commit refusal keeps its existing not-saved wording and its draft.
        MainWindow.PairBindingForTest = _ => throw new IOException("The authoritative binding refused this draft.");
        PrepareNewPairDraft(window, saved, "Refused pair");
        var refused = PumpResult(window.SavePairForTestAsync());
        check(!refused && EditorStatus(window).StartsWith("Pair was not saved:", StringComparison.Ordinal) &&
              Field<SyncPairSet>(window, "_pairSet").Pairs.Count == 0 &&
              Field<TextBox>(window, "PairNameBox").Text == "Refused pair" &&
              window.PendingPairSaveFollowUpLabelsForTest.Count == 0,
            "a pre-commit refusal reports not saved and preserves the draft: " + EditorStatus(window));

        // An unremarkable committed save still reports only its own saved status.
        MainWindow.PairBindingForTest = _ => { saves++; return Task.FromResult(Bound()); };
        PrepareNewPairDraft(window, saved, "Clean pair");
        var cleanSave = PumpResult(window.SavePairForTestAsync());
        check(cleanSave && saves == 1 && Field<SyncPairSet>(window, "_pairSet").Pairs.Count == 1 &&
              window.LastCommittedPairIdForTest == saved.Id &&
              window.PendingPairSaveFollowUpLabelsForTest.Count == 0 &&
              !EditorStatus(window).Contains("Follow-up needs attention", StringComparison.Ordinal),
            "an ordinary committed save reports no outstanding follow-up: " + EditorStatus(window));

        foreach (var step in new[] { RowStep, MonitorStep, StorageStep, ExplorerStep })
        {
            var binds = 0;
            monitorStops = 0;
            MainWindow.PairBindingForTest = _ => { binds++; return Task.FromResult(Bound()); };
            PrepareNewPairDraft(window, saved, "Isolated " + step);
            MainWindow.PairSaveFollowUpFailureStepForTest = step;
            var committed = PumpResult(window.SavePairForTestAsync());
            Console.WriteLine(JsonSerializer.Serialize(new { step, committed, binds,
                pairCount = Field<SyncPairSet>(window, "_pairSet").Pairs.Count,
                committedId = window.LastCommittedPairIdForTest,
                editingId = Field<string?>(window, "_editingPairRootId"),
                editingNew = Field<bool>(window, "_editingNewPair"),
                endpointDraft = window.EndpointDraft() is not null,
                status = EditorStatus(window) }));
            check(committed && binds == 1 &&
                  Field<SyncPairSet>(window, "_pairSet").Pairs.Count == 1 &&
                  Field<SyncPairSet>(window, "_pairSet").Pairs[0].Id == saved.Id &&
                  window.LastCommittedPairIdForTest == saved.Id &&
                  Field<string?>(window, "_editingPairRootId") is null &&
                  !Field<bool>(window, "_editingNewPair") && window.EndpointDraft() is null,
                "the committed save retains the pair identity and closes its draft while " + step + " fails");
            check(EditorStatus(window).StartsWith("Pair saved", StringComparison.Ordinal) &&
                  EditorStatus(window).Contains("Follow-up needs attention: " + step + ".", StringComparison.Ordinal),
                "the save reports saved-with-follow-up-attention naming " + step + ": " + EditorStatus(window));
            check(window.PendingPairSaveFollowUpLabelsForTest.Count == 1 &&
                  window.PendingPairSaveFollowUpLabelsForTest[0] == step &&
                  window.PairSaveFollowUpDetailForTest is not null,
                "only the failed follow-up stays outstanding after " + step);
            check(monitorStops == (step == MonitorStep ? 0 : 1),
                "the other committed-save follow-ups still ran while " + step + " failed: monitor stops " +
                monitorStops);

            MainWindow.PairSaveFollowUpFailureStepForTest = null;
            var retried = PumpResult(window.SavePairForTestAsync());
            check(retried && binds == 1 &&
                  Field<SyncPairSet>(window, "_pairSet").Pairs.Count == 1 &&
                  Field<SyncPairSet>(window, "_pairSet").Pairs[0].Id == saved.Id &&
                  window.PendingPairSaveFollowUpLabelsForTest.Count == 0 &&
                  window.LastCommittedPairIdForTest is null &&
                  EditorStatus(window).Contains("follow-up completed", StringComparison.OrdinalIgnoreCase),
                "a retry re-ran only the outstanding follow-up for " + step +
                ": no second Bind and no duplicate pair");
        }

        MainWindow.PairBindingForTest = null;
        MainWindow.PairSaveFollowUpFailureStepForTest = null;
        MainWindow.MonitorStopperForTest = null;
        check(Field<SyncPairSet>(window, "_pairSet").Pairs.Count == 1,
            "exactly one pair exists after every committed save and retry in this sequence");
    }

    // ---- SS-D01: the queued first-sync invocation owns its own cancellation -----------------------

    private static void CheckQueuedFirstSyncStop(MainWindow window, PilotStateAuthority state, string root,
        Action<bool, string> check)
    {
        // The ownership rule itself, through the real production members: a superseded invocation is
        // cancelled, and an earlier invocation's finalization can neither clear nor cancel a later one.
        var firstCancellation = (CancellationTokenSource)Call(window, "BeginPendingRunCancellation")!;
        var secondCancellation = (CancellationTokenSource)Call(window, "BeginPendingRunCancellation")!;
        check(firstCancellation.IsCancellationRequested && !secondCancellation.IsCancellationRequested,
            "a superseded UI invocation is cancelled and is never silently reused");
        Call(window, "EndPendingRunCancellation", firstCancellation);
        check(ReferenceEquals(Field<CancellationTokenSource>(window, "_pendingRunCancellation"),
                  secondCancellation) && !secondCancellation.IsCancellationRequested,
            "an earlier invocation's finalization neither clears nor cancels a later run's token");
        check((bool)Call(window, "CancelPendingRun")! && secondCancellation.IsCancellationRequested,
            "the surviving token is the one the real stop path cancels");
        Call(window, "EndPendingRunCancellation", secondCancellation);
        check(Field<CancellationTokenSource?>(window, "_pendingRunCancellation") is null,
            "the owning invocation clears only its own token");

        // A real registered first-sync pair with a real review page, as the existing first-sync feedback
        // checks establish it.
        var localRoot = Directory.CreateDirectory(Path.Combine(root, "first-sync-local")).FullName;
        using var lease = PilotDirectoryLease.Open(localRoot);
        var account = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            AccountId, "outcome-root", "outcome-home", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes, DisplayName: AccountId,
            Email: "outcome-account@example.test").Normalize();
        Pump(new DropboxConnectionStore(state).SaveNewAsync(account));
        var pair = new SyncPair(PairId, PairName, localRoot, lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, "/Outcome", "id:outcome", ConnectionId: account.ConnectionId,
            InitialReviewApproved: false);
        Pump(window.AuthorityHost.Authority.RegisterPairAsync(pair.Id, pair.PolicyRevision));
        Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(pair, true));
        window.LoadPairSetForTest(new SyncPairSet([pair], pair.Id));
        Set(window, "_accountLabels", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [account.ConnectionId] = "Dropbox: outcome@example.invalid"
        });
        Call(window, "RefreshAccountPickers");
        MainWindow.BootstrapReviewPageLoaderForTest = (pairId, _, offset, _, _, _, _) =>
            Task.FromResult(new SyncBootstrapReviewPage([ReviewItem(pairId)], offset, 1, 1, 1, 0, 0, false, true,
                "outcome-local-1", "outcome-cloud-1"));
        MainWindow.BootstrapReviewLoaderForTest = (pairId, _, _, _, _) =>
            Task.FromResult<IReadOnlyList<SyncBootstrapReviewItem>>([ReviewItem(pairId)]);
        MainWindow.BootstrapApprovalConfirmationForTest = _ => true;
        Call(window, "OpenFirstSyncReview", pair.Id);
        PumpUntil(() => window.BootstrapRows.Count > 0 &&
            Field<Button>(window, "ApproveBootstrapButton").IsEnabled, "the first-sync review to load");

        var runsBefore = Pump(window.AuthorityHost.Authority.LoadRunHistoryAsync()).Count;
        CancellationToken queuedToken = default;
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        MainWindow.BootstrapActionPlanForTest = async (_, token) =>
        {
            queuedToken = token;
            entered.TrySetResult();
            // Keep the injected pre-Host boundary held until its cancellation presentation is observed.
            // Then let the real handler consume this invocation's cancellation and finalize.
            await release.Task;
            token.ThrowIfCancellationRequested();
            return new SyncBootstrapActionResult(pair, new SyncRunResult(pair.Id, []), 1, 0, false);
        };
        Call(window, "ApproveBootstrap_Click", Field<Button>(window, "ApproveBootstrapButton"),
            new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(() => window.RunActiveForTest && entered.Task.IsCompleted,
            "the reviewed first sync to be queued and held");
        check(!queuedToken.IsCancellationRequested && !window.AuthorityHost.HasActiveRun,
            "the held first-sync work passed its own live token before any host run existed");

        Call(window, "Stop_Click", Field<Button>(window, "StopButton"),
            new RoutedEventArgs(ButtonBase.ClickEvent));
        Drain();
        check(queuedToken.IsCancellationRequested,
            "the actual Stop handler cancelled the token the queued first-sync work received");
        check(Field<string>(window, "_workspacePhaseDetail").Contains("queued first-sync work is cancelled",
                  StringComparison.Ordinal) && !window.AuthorityHost.HasActiveRun,
            "the stop reports the queued first-sync work as cancelled with no host run: " +
            Field<string>(window, "_workspacePhaseDetail"));

        release.TrySetResult();
        PumpUntil(() => !window.RunActiveForTest && !Field<bool>(window, "_firstSyncApprovalInFlight"),
            "the stopped first sync to finalize");
        check(Pump(window.AuthorityHost.Authority.LoadRunHistoryAsync()).Count == runsBefore &&
              Field<CancellationTokenSource?>(window, "_pendingRunCancellation") is null,
            "the cancelled first sync left no accepted run and the invocation cleared its own token");

        // Run B is queued after run A finalized: A's finalization must not have touched B's token.
        var runBEntered = new TaskCompletionSource();
        var runBRelease = new TaskCompletionSource();
        CancellationToken runBToken = default;
        Call(window, "OpenFirstSyncReview", pair.Id);
        PumpUntil(() => window.BootstrapRows.Count > 0 &&
            Field<Button>(window, "ApproveBootstrapButton").IsEnabled,
            "the first-sync review to be ready for run B");
        MainWindow.BootstrapActionPlanForTest = async (_, token) =>
        {
            runBToken = token;
            runBEntered.TrySetResult();
            await runBRelease.Task;
            return new SyncBootstrapActionResult(pair, new SyncRunResult(pair.Id, []), 1, 0, false);
        };
        Call(window, "ApproveBootstrap_Click", Field<Button>(window, "ApproveBootstrapButton"),
            new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(() => window.RunActiveForTest && runBEntered.Task.IsCompleted, "run B to be queued and held");
        var runBCancellation = Field<CancellationTokenSource>(window, "_pendingRunCancellation");
        check(!runBToken.IsCancellationRequested && runBCancellation.Token.Equals(runBToken),
            "run A's finalization left run B's own token live and bound to run B's invocation");
        runBRelease.TrySetResult();
        PumpUntil(() => !window.RunActiveForTest && !Field<bool>(window, "_firstSyncApprovalInFlight"),
            "run B to finalize normally");
        check(!runBToken.IsCancellationRequested &&
              Field<CancellationTokenSource?>(window, "_pendingRunCancellation") is null,
            "run B completed without run A's finalization cancelling or clearing it");
        check(Pump(window.AuthorityHost.Authority.LoadRunHistoryAsync()).Count == runsBefore,
            "neither the stopped run nor run B created a provider effect in this isolated state");
    }

    private static SyncBootstrapReviewItem ReviewItem(string pairId) => new(pairId, pairId + ".txt",
        "Local only", "File", "TXT", "Upload", "Copy to cloud", 1, null, DateTimeOffset.UtcNow, null);

    // ---- SS-D01: every queued host invocation owns its own cancellation token ---------------------

    // The ordinary run, the startup recovery and the copied-file mapping repair each queue work before
    // any host call. Each is held here at that exact pre-host boundary, the REAL Stop control is invoked,
    // and the held work is then released so the real host method receives the invocation's own cancelled
    // token. No provider request is created, the token the host received is the cancelled one, and a later
    // invocation keeps its own live token when an earlier one finalizes.
    private static void CheckQueuedHostInvocationStop(MainWindow window, PilotStateAuthority state, string root,
        Action<bool, string> check)
    {
        var localRoot = Directory.CreateDirectory(Path.Combine(root, "queued-invocation-local")).FullName;
        using var lease = PilotDirectoryLease.Open(localRoot);
        var account = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "queued-invocation-account", "queued-invocation-root", "queued-invocation-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
            DisplayName: "Queued Invocation", Email: "queued-invocation@example.test").Normalize();
        var fixtureConnections = new DropboxConnectionStore(state);
        Pump(fixtureConnections.DeleteAsync());
        Pump(fixtureConnections.SaveNewAsync(account));
        var pair = new SyncPair("queued-invocation-pair", "Queued Invocation Pair", localRoot,
            lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/Queued Invocation", "id:queued-invocation",
            ConnectionId: account.ConnectionId, InitialReviewApproved: true);
        Pump(window.AuthorityHost.Authority.RegisterPairAsync(pair.Id, pair.PolicyRevision));
        Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(pair, true));
        window.LoadPairSetForTest(new SyncPairSet([pair], pair.Id));
        Call(window, "RefreshAccountPickers");
        Call(window, "RefreshAttentionPairChoices");
        MainWindow.PairRunDispatcherForTest = null;
        var stop = Field<Button>(window, "StopButton");
        var runsBefore = Pump(window.AuthorityHost.Authority.LoadRunHistoryAsync()).Count;
        MainWindow.QueuedInvocationHoldForTest = (_, token) =>
        {
            var held = _held ?? throw new InvalidDataException("A queued invocation was held with no hold installed.");
            held.Token = token;
            held.Owner = Field<CancellationTokenSource>(window, "_pendingRunCancellation");
            held.Entered.TrySetResult();
            return held.Release.Task;
        };
        try
        {
            // The ordinary shared sync entry.
            var sync = BeginHeld();
            _ = (Task)Call(window, "RunPairsAsync", new[] { pair }, "Manual")!;
            PumpUntil(() => sync.Entered.Task.IsCompleted, "the ordinary sync to reach its held pre-host queue");
            check(window.RunActiveForTest && !window.AuthorityHost.HasActiveRun && !sync.Token.IsCancellationRequested &&
                  ReferenceEquals(Field<CancellationTokenSource?>(window, "_pendingRunCancellation"), sync.Owner),
                "SS-D01 the queued ordinary sync owns one live token before the host is invoked");
            StopHeld(window, stop, sync, "the ordinary queued sync", check);
            check(Pump(window.AuthorityHost.Authority.LoadRunHistoryAsync()).Count == runsBefore &&
                  !window.AuthorityHost.HasActiveRun,
                "SS-D01 the stopped queued ordinary sync left no accepted run and no provider effect");

            // The queued startup recovery.
            window.LoadPairSetForTest(new SyncPairSet([pair], pair.Id));
            var recovery = BeginHeld();
            var recoveryTask = (Task)Call(window, "RecoverOnStartupAsync")!;
            PumpUntil(() => recovery.Entered.Task.IsCompleted, "the recovery to reach its held pre-host queue");
            check(window.RunActiveForTest && !window.AuthorityHost.HasActiveRun &&
                  !recovery.Token.IsCancellationRequested &&
                  ReferenceEquals(Field<CancellationTokenSource?>(window, "_pendingRunCancellation"), recovery.Owner),
                "SS-D01 the queued startup recovery owns one live token before the host is invoked");
            StopHeld(window, stop, recovery, "the queued startup recovery", check);
            PumpUntil(() => recoveryTask.IsCompleted, "the cancelled startup recovery task to complete");
            check(recoveryTask.IsCanceled || recoveryTask.Exception?.GetBaseException() is OperationCanceledException,
                "SS-D01 the released recovery ended as the cancellation its own token received");
            check(Pump(window.AuthorityHost.Authority.LoadRunHistoryAsync()).Count == runsBefore &&
                  !window.AuthorityHost.HasActiveRun,
                "SS-D01 the stopped queued recovery replayed no interrupted effect");

            // The queued copied-file mapping repair, through its real button handler.
            window.LoadPairSetForTest(new SyncPairSet([pair], pair.Id));
            Call(window, "RefreshAttentionPairChoices");
            var repair = BeginHeld();
            Call(window, "RepairBootstrapMappings_Click", Field<Button>(window, "RepairBootstrapMappingsButton"),
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => repair.Entered.Task.IsCompleted, "the mapping repair to reach its held pre-host queue");
            check(window.RunActiveForTest && !window.AuthorityHost.HasActiveRun &&
                  !repair.Token.IsCancellationRequested &&
                  ReferenceEquals(Field<CancellationTokenSource?>(window, "_pendingRunCancellation"), repair.Owner),
                "SS-D01 the queued mapping repair owns one live token before the host is invoked");
            StopHeld(window, stop, repair, "the queued mapping repair", check,
                () => Field<TextBlock>(window, "BootstrapChoiceSummary").Text);
            check(Pump(window.AuthorityHost.Authority.LoadRunHistoryAsync()).Count == runsBefore &&
                  !window.AuthorityHost.HasActiveRun,
                "SS-D01 the stopped queued mapping repair created no accepted run and no provider effect");

            // An earlier invocation's finalization can neither cancel nor clear a later invocation.
            var earlier = BeginHeld();
            _ = (Task)Call(window, "RunPairsAsync", new[] { pair }, "Manual")!;
            PumpUntil(() => earlier.Entered.Task.IsCompleted, "the earlier invocation to be held");
            Call(window, "Stop_Click", stop, new RoutedEventArgs(ButtonBase.ClickEvent));
            earlier.Release.TrySetResult();
            PumpUntil(() => !window.RunActiveForTest && Field<CancellationTokenSource?>(window, "_pendingRunCancellation") is null,
                "the earlier invocation to finalize");
            var later = BeginHeld();
            _ = (Task)Call(window, "RunPairsAsync", new[] { pair }, "Manual")!;
            PumpUntil(() => later.Entered.Task.IsCompleted, "the later invocation to be held");
            Call(window, "EndPendingRunCancellation", earlier.Owner!);
            check(!later.Token.IsCancellationRequested &&
                  ReferenceEquals(Field<CancellationTokenSource?>(window, "_pendingRunCancellation"), later.Owner),
                "SS-D01 an earlier invocation's finalization cannot cancel or clear a later invocation's token");
            StopHeld(window, stop, later, "the later queued sync", check);
            check(later.Token.IsCancellationRequested &&
                  Field<CancellationTokenSource?>(window, "_pendingRunCancellation") is null,
                "SS-D01 the later invocation was cancelled only by its own stop and cleared only its own token");
            check(Pump(window.AuthorityHost.Authority.LoadRunHistoryAsync()).Count == runsBefore,
                "SS-D01 none of these stopped or completed invocations created a provider effect");
        }
        finally { MainWindow.QueuedInvocationHoldForTest = null; _held = null; }
    }

    private sealed class HeldInvocation
    {
        public CancellationTokenSource? Owner;
        public CancellationToken Token;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static HeldInvocation? _held;

    private static HeldInvocation BeginHeld()
    {
        var held = new HeldInvocation();
        _held = held;
        return held;
    }

    // The real UI Stop control while the invocation is still held, then release, then its own
    // finalization. The shared stop wording is asserted before any release, and an entry that reports the
    // stop in its own words is polled for that exact wording before the readback can replace it.
    private static void StopHeld(MainWindow window, Button stop, HeldInvocation held, string label,
        Action<bool, string> check, Func<string>? ownStopText = null)
    {
        Call(window, "Stop_Click", stop, new RoutedEventArgs(ButtonBase.ClickEvent));
        Drain();
        check(held.Token.IsCancellationRequested,
            "SS-D01 the real Stop control cancelled " + label + "'s own queued token");
        check(!window.AuthorityHost.HasActiveRun &&
              Field<string>(window, "_workspacePhaseDetail").Contains("cancelled", StringComparison.Ordinal),
            "SS-D01 the real Stop control cancelled " + label + " before it reached the host and reported it " +
            "as a cancelled invocation: " + Field<string>(window, "_workspacePhaseDetail"));
        held.Release.TrySetResult();
        if (ownStopText is not null)
            PumpUntil(() => ownStopText().Contains("stopped before any change", StringComparison.Ordinal),
                label + " to report its own stop instead of a completed mapping change");
        PumpUntil(() => !window.RunActiveForTest &&
                        Field<CancellationTokenSource?>(window, "_pendingRunCancellation") is null,
            label + " to finalize after its hold was released");
    }

    // ---- shared fixture helpers -------------------------------------------------------------------

    private static SyncPair SeedOutcomePair(MainWindow window)
    {
        var pair = new SyncPair(PairId, PairName, Path.Combine(Path.GetTempPath(), "sync-shark-outcome"),
            1, 1, "/Outcome", "id:outcome", ConnectionId: AccountId);
        window.LoadPairSetForTest(new SyncPairSet([pair], pair.Id));
        Set(window, "_accountLabels", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AccountId] = "Dropbox: outcome@example.invalid"
        });
        Call(window, "RefreshWorkspaceAccountPickers");
        Drain();
        return pair;
    }

    private static void PrepareNewPairDraft(MainWindow window, SyncPair saved, string name)
    {
        window.LoadPairSetForTest(new SyncPairSet([], null));
        Set(window, "_editingNewPair", false);
        Set(window, "_editingPairRootId", null);
        Set(window, "_duplicateSourcePairId", null);
        Set(window, "_recoveryPreparationFailed", false);
        Set(window, "_selectedRecoveryFolder", new DropboxFolderEntry("Recovery",
            SyncDropboxArtifactPaths.RecoveryContainerPath, "id:recovery-outcome"));
        Set(window, "_selectedDropboxFolder", new DropboxFolderEntry("Outcome", saved.DropboxRoot,
            saved.DropboxFolderId));
        Field<TextBox>(window, "LocalRootBox").Text = saved.LocalRoot;
        Field<TextBox>(window, "PairNameBox").Text = name;
        Field<TextBox>(window, "PairPriorityBox").Text = "0";
        Field<CheckBox>(window, "PairEnabledBox").IsChecked = true;
        Field<CheckBox>(window, "PairDeletionBox").IsChecked = false;
        Field<CheckBox>(window, "PairFilesOnDemandBox").IsChecked = false;
        // This fixture supplies the legacy binding seam to isolate post-save follow-ups. Discard the
        // earlier outcome pair's explicit endpoint session, which is not this new two-tree draft.
        Call(window, "EndPairEndpointStrip");
        Drain();
    }

    private static string EditorStatus(MainWindow window) => Field<TextBlock>(window, "PairEditorStatusText").Text;

    private static string HomeStatus(MainWindow window) => HomeSummary(window).GetValueOrDefault("Status:") ?? "";

    private static Dictionary<string, string> HomeCounts(MainWindow window) => HomeSummary(window);

    private static Dictionary<string, string> HomeSummary(MainWindow window)
    {
        var grid = Field<ContentPresenter>(window, "_homeLastSync").Content as Grid
            ?? throw new InvalidDataException("The real Home labelled summary is unavailable.");
        var labels = grid.Children.OfType<TextBlock>().Where(text => Grid.GetColumn(text) == 0).ToArray();
        return labels.ToDictionary(label => label.Text, label => grid.Children.OfType<TextBlock>()
            .FirstOrDefault(value => Grid.GetRow(value) == Grid.GetRow(label) && Grid.GetColumn(value) == 1)?.Text ?? "",
            StringComparer.Ordinal);
    }

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static object? Call(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) is { } instance
            ? instance.Invoke(window, arguments)
            : typeof(MainWindow).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic) is { } statics
                ? statics.Invoke(null, arguments)
                : throw new MissingMethodException(nameof(MainWindow), name);

    private static void Drain() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Drain(); Thread.Sleep(1); }
        task.GetAwaiter().GetResult();
    }

    private static T Pump<T>(Task<T> task)
    {
        Pump((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static bool PumpResult(Task<bool> task) => Pump(task);

    private static void PumpUntil(Func<bool> condition, string label)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline) { Drain(); Thread.Sleep(1); }
        if (!condition()) throw new InvalidDataException("Timed out waiting for " + label);
    }
}

