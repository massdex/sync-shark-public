using Massdex.SyncShark;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace SyncShark.UiFixture;

// UI1-UI4 focused real-entry checks over the existing production paths only:
//   * the real ApproveBootstrap_Click button handler (single-flight guard, immediate feedback, cancel and
//     failure restoration);
//   * the real RefreshBootstrapReviewAsync page load that recomputes Start's availability;
//   * the real Run_Click ordinary-run entry, which shares the one presentation reset with first sync;
//   * the real HomeStateAction_Click aggregate navigation through NavigateTo and the Needs Attention subtabs;
//   * the real RefreshWorkspace presentation that chooses the Home state token.
//
// Fixture input is asynchronous INPUT only - a controlled review loader, a controlled confirmation answer and a
// controlled action-plan completion - so the decision under test stays the production decision. No provider,
// credential, network, signing, install or user-file effect is involved.
internal static class FirstSyncFeedbackChecks
{
    internal static int Run(string root, bool identityRecoveryOnly = false)
    {
        Directory.CreateDirectory(root);
        var runRoot = Path.Combine(root, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var instance = PilotInstanceLock.Acquire(Path.Combine(runRoot, "state", "instance.lock"));
        var dropbox = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "feedback-account-a", "feedback-root", "feedback-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
            DisplayName: "feedback-account-a", Email: "feedback-account-a@example.test").Normalize();
        Pump(new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(dropbox));
        var google = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey("feedback-account-b"), "feedback-account-b",
            "feedback-account-b@example.test", "feedback-account-b",
            [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
        Pump(new GoogleDriveConnectionStore(instance.StateAuthority).SaveAsync(google));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        var firstRoot = Path.Combine(runRoot, "first");
        var problemRoot = Path.Combine(runRoot, "problem");
        var otherRoot = Path.Combine(runRoot, "other");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(problemRoot);
        Directory.CreateDirectory(otherRoot);
        using var firstLease = PilotDirectoryLease.Open(firstRoot);
        using var problemLease = PilotDirectoryLease.Open(problemRoot);
        using var otherLease = PilotDirectoryLease.Open(otherRoot);
        var first = new SyncPair("feedback-pair-a", "Feedback First", firstRoot,
            firstLease.Identity.VolumeSerialNumber, firstLease.Identity.FileId, "/a", "id:a",
            ConnectionId: dropbox.ConnectionId, InitialReviewApproved: false);
        var problem = new SyncPair("feedback-pair-b", "Feedback Problem", problemRoot,
            problemLease.Identity.VolumeSerialNumber, problemLease.Identity.FileId, "/b", "id:b",
            ConnectionId: dropbox.ConnectionId, InitialReviewApproved: true);
        var other = new SyncPair("feedback-pair-c", "Feedback Other Account", otherRoot,
            otherLease.Identity.VolumeSerialNumber, otherLease.Identity.FileId, "/c", "id:c",
            ConnectionId: google.ConnectionId, Provider: SyncProviders.GoogleDrive,
            InitialReviewApproved: true);
        var pairSet = new SyncPairSet([first, problem, other], first.Id);
        var window = new MainWindow(instance.StateAuthority, theme)
        {
            Width = 1240, Height = 780, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            window.Show();
            Drain();
            Pump(window.AuthorityHost.LoadPairsAsync());
            foreach (var pair in pairSet.Pairs)
            {
                Pump(window.AuthorityHost.Authority.RegisterPairAsync(pair.Id, pair.PolicyRevision));
                Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(pair, pair.Id == first.Id));
            }
            window.LoadPairSetForTest(pairSet);
            var fixture = new Fixture(window, pairSet, first, problem, other, dropbox.ConnectionId);
            if (identityRecoveryOnly)
            {
                fixture.CheckExplicitIdentityRecoveryPresentation();
                Console.WriteLine("PASS focused explicit identity recovery UI action, disabled explanation and exact retained run link; no provider or user-file effect");
                return 0;
            }
            fixture.CheckImmediateFeedbackAndOneRunFromDoubleClick();
            fixture.CheckCancelDispatchesNothingAndRestoresStart();
            fixture.CheckCompetingEntriesAreAdmittedOnlyOnce();
            fixture.CheckConfirmationDoesNotRequireEveryDisplayedRow();
            fixture.CheckFailedRunRestoresControls();
            fixture.CheckNewOperationReplacesStaleTimes();
            fixture.CheckAggregateIssueNavigationIsScoped();
            fixture.CheckActiveRunKeepsRunningToken();
            fixture.CheckAwaitingReviewPairsAreExcludedFromAutomaticSync();
            fixture.CheckLiveOperationOutputAndActionFeedback();
            fixture.CheckExplicitIdentityRecoveryPresentation();
            fixture.CheckFirstSyncNavigationThenFreshAddPairAndCancel();
            Console.WriteLine("PASS --first-sync-feedback-checks; state " + runRoot +
                "; real first-sync approval handler, real review page load, real ordinary-run entry, real " +
                "aggregate issue navigation and real Home state presentation only; no provider, credential, " +
                "network, signing, install or user-file effect");
            return 0;
        }
        finally
        {
            MainWindow.BootstrapReviewPageLoaderForTest = null;
            MainWindow.BootstrapReviewLoaderForTest = null;
            MainWindow.BootstrapApprovalConfirmationForTest = null;
            MainWindow.BootstrapActionPlanForTest = null;
            MainWindow.PairRunDispatcherForTest = null;
            MainWindow.SuppressLoadedStartupForTest = false;
            window.RequestProductExit();
            app.Shutdown();
        }
    }

    private sealed class Fixture
    {
        private const string StaleDetail = "Stale detail from an earlier run.";
        /// <summary>
        /// Fixture-only hierarchy input for one case: it answers from the exact seeded unavailable reasons and
        /// the fixture's own pair set, performs no provider, credential or network operation, rejects an unknown
        /// pair and honours cancellation. The returned scope restores the previous loader.
        /// </summary>
        private IDisposable OverrideHierarchyInput(Dictionary<string, string> reasons)
        {
            var prior = _window.FolderSelectionTreeLoaderForTest;
            var priorHealth = MainWindow.PairHealthLoaderForTest;
            Func<string, CancellationToken, Task<SyncFolderSelectionTree>> input = (pairId, token) =>
            {
                token.ThrowIfCancellationRequested();
                var pair = _pairSet.Pairs.SingleOrDefault(item =>
                    string.Equals(item.Id, pairId, StringComparison.Ordinal))
                    ?? throw new InvalidDataException(
                        "The fixture cannot supply a hierarchy for an unknown pair: " + pairId);
                var localUnavailable = reasons.TryGetValue(pairId, out var localReason) &&
                    string.Equals(localReason, UnavailableRootReason, StringComparison.Ordinal);
                var cloudUnavailable = reasons.TryGetValue(pairId, out var cloudReason) &&
                    string.Equals(cloudReason, UnavailableCloudReason, StringComparison.Ordinal);
                return Task.FromResult(new SyncFolderSelectionTree(pairId, [], 0, 0, 0, 0,
                    LocalHierarchyAvailable: !localUnavailable,
                    LocalHierarchyMessage: localUnavailable ? UnavailableRootReason : null,
                    DropboxHierarchyAvailable: !cloudUnavailable,
                    DropboxHierarchyMessage: cloudUnavailable ? UnavailableCloudReason : null,
                    EndpointContext: SyncEndpointContext.Capture(pair)));
            };
            _window.FolderSelectionTreeLoaderForTest = input;
            MainWindow.PairHealthLoaderForTest = input;
            return new HierarchyInputScope(this, prior, priorHealth);
        }

        private sealed class HierarchyInputScope(Fixture fixture,
            Func<string, CancellationToken, Task<SyncFolderSelectionTree>>? prior,
            Func<string, CancellationToken, Task<SyncFolderSelectionTree>>? priorHealth) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                fixture._window.FolderSelectionTreeLoaderForTest = prior;
                MainWindow.PairHealthLoaderForTest = priorHealth;
            }
        }

        private const string StalePairName = "Stale pair from an earlier run";
        private const string StalePairId = "stale-pair-from-an-earlier-run";
        private const string UnavailableRootReason =
            "Local folder unavailable: the recorded local folder is missing. Choose another folder to repair this pair.";
        private const string UnavailableCloudReason =
            "The saved Dropbox folder no longer exists. Choose the correct cloud folder to repair this pair.";
        private readonly MainWindow _window;
        private readonly SyncPairSet _pairSet;
        private readonly SyncPair _first;
        private readonly SyncPair _problem;
        private readonly SyncPair _other;
        private readonly string _accountId;
        private bool _confirmationAnswer = true;
        private int _confirmations;
        private int _dispatches;

        internal Fixture(MainWindow window, SyncPairSet pairSet, SyncPair first, SyncPair problem, SyncPair other,
            string accountId)
        {
            _window = window;
            _pairSet = pairSet;
            _first = first;
            _problem = problem;
            _other = other;
            _accountId = accountId;
            MainWindow.BootstrapReviewPageLoaderForTest = (pairId, _, offset, _, _, _, _) =>
                Task.FromResult(new SyncBootstrapReviewPage([Item(pairId)], offset, 1, 1, 1, 0, 0, false, true,
                    "feedback-local-1", "feedback-cloud-1"));
            MainWindow.BootstrapReviewLoaderForTest = (pairId, _, _, _, _) =>
                Task.FromResult<IReadOnlyList<SyncBootstrapReviewItem>>([Item(pairId)]);
            MainWindow.BootstrapApprovalConfirmationForTest = _ =>
            {
                _confirmations++;
                return _confirmationAnswer;
            };
        }

        // UI1: one synchronous guard, immediate validating feedback, one authority call from a double click,
        // shared enablement that a later refresh cannot undo, and coherent restoration on completion.
        internal void CheckImmediateFeedbackAndOneRunFromDoubleClick()
        {
            var delayedLoad = new TaskCompletionSource<IReadOnlyList<SyncBootstrapReviewItem>>();
            MainWindow.BootstrapReviewLoaderForTest = (_, _, _, _, _) => delayedLoad.Task;
            _confirmationAnswer = true;
            _confirmations = 0;
            _dispatches = 0;
            var completion = new TaskCompletionSource<SyncBootstrapActionResult>();
            MainWindow.BootstrapActionPlanForTest = (_, _) =>
            {
                _dispatches++;
                return completion.Task;
            };
            OpenReview(_first.Id);
            var start = Control<Button>("ApproveBootstrapButton");
            Call("ApproveBootstrap_Click", start, new RoutedEventArgs(ButtonBase.ClickEvent));
            Call("ApproveBootstrap_Click", start, new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(!start.IsEnabled && Field<bool>("_firstSyncApprovalInFlight") &&
                    Feedback().Contains("Checking the reviewed inventory", StringComparison.Ordinal),
                "UI1 the first click immediately shows validating feedback on First Sync and disables Start before " +
                "the awaited load");
            Require(start.Content is StackPanel busy && busy.Children.OfType<CommandGlyph>().Any() &&
                    busy.Children.OfType<TextBlock>().Any(text => text.Text.EndsWith("…", StringComparison.Ordinal)) &&
                    ToolTipService.GetShowOnDisabled(start) && !string.IsNullOrWhiteSpace(start.ToolTip as string),
                "A03/A04 the real first-sync button shows its busy glyph and disabled reason during the actual awaited operation");
            Require(_confirmations == 0 && _dispatches == 0,
                "UI1 the second click reached neither the confirmation nor a run while the load was in flight");
            delayedLoad.SetResult([Item(_first.Id)]);
            PumpUntil(() => _dispatches == 1, "the approved first sync never started");
            Require(_confirmations == 1 && _dispatches == 1 && _window.RunActiveForTest && !start.IsEnabled,
                "UI1 two clicks before the load completed produced exactly one authority call, and Start stayed " +
                "disabled for the whole load, confirmation and run interval");
            const string preparation = "Checking schema, installation identity and reviewed command preconditions before accepting the sync; no file transfer has started.";
            // Controlled process input, real shared renderer while the actual
            // approval handler is held. Timer/liveness repetition is not progress.
            Call("ShowRunStatus", preparation);
            Refresh();
            Require(Field<string>("_workspacePhaseDetail") == preparation &&
                    Control<TextBlock>("BootstrapChoiceSummary").Text == preparation &&
                    Control<TextBlock>("PhaseText").Text.Contains(preparation, StringComparison.Ordinal) && !start.IsEnabled,
                "First-sync preparation exposes the full exact database process on Home with its existing timing details and on the initiating review, with no transfer claim. " +
                "Home=" + Control<TextBlock>("PhaseText").Text + "; Review=" +
                Control<TextBlock>("BootstrapChoiceSummary").Text + "; Detail=" + Field<string>("_workspacePhaseDetail"));
            var preparationAdvanced = Field<DateTimeOffset?>("_workspaceProgressUpdated");
            Call("ShowRunStatus", preparation);
            Require(Field<DateTimeOffset?>("_workspaceProgressUpdated") == preparationAdvanced,
                "Repeating the same pending preparation process never fabricates a newer real-progress time");
            Pump(_window.RefreshBootstrapReviewForTestAsync());
            Require(!start.IsEnabled,
                "UI1 the real review page refresh cannot re-enable Start while the single-flight guard is held");
            completion.SetResult(Applied());
            PumpUntil(() => !_window.RunActiveForTest && !Field<bool>("_firstSyncApprovalInFlight"),
                "the approved first sync never completed");
            Require(_dispatches == 1 && Feedback().Contains("applied 1", StringComparison.Ordinal),
                "UI1 completion left coherent visible feedback and no second command: " + Feedback());
            Pump(_window.RefreshBootstrapReviewForTestAsync());
            Require(start.IsEnabled,
                "UI1 completion restored Start once the load, confirmation and run interval had ended");
        }

        // UI1: a cancelled confirmation performs no effect and releases the guard and the control state.
        internal void CheckCancelDispatchesNothingAndRestoresStart()
        {
            _confirmationAnswer = false;
            _confirmations = 0;
            _dispatches = 0;
            MainWindow.BootstrapActionPlanForTest = (_, _) =>
            {
                _dispatches++;
                return Task.FromResult(Applied());
            };
            OpenReview(_first.Id);
            _window.BootstrapRows.Single().SelectedAction = "Skip for now";
            Call("ApproveBootstrap_Click", Control<Button>("ApproveBootstrapButton"),
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => _confirmations == 1 && !Field<bool>("_firstSyncApprovalInFlight"),
                "the cancelled first-sync confirmation never returned");
            Require(_dispatches == 0 && !_window.RunActiveForTest &&
                    Control<Button>("ApproveBootstrapButton").IsEnabled,
                "UI1 a cancelled confirmation dispatched zero syncs and restored Start with the guard released");
            Require(Feedback().Contains("First sync cancelled", StringComparison.Ordinal) &&
                    Feedback().Contains("no files were changed", StringComparison.Ordinal) &&
                    !Feedback().Contains("Checking the reviewed inventory", StringComparison.Ordinal) &&
                    _window.BootstrapRows.Single().SelectedAction == "Skip for now",
                "UI1 a cancelled confirmation restores coherent idle feedback and leaves the draft choices " +
                "unchanged: " + Feedback());
        }

        // UI1: a failed run restores the controls with visible feedback and enqueues no further command.
        internal void CheckFailedRunRestoresControls()

        {
            _confirmationAnswer = true;
            _confirmations = 0;
            _dispatches = 0;
            MainWindow.BootstrapActionPlanForTest = (_, _) =>
            {
                _dispatches++;
                throw new InvalidOperationException("Controlled first-sync plan failure");
            };
            OpenReview(_first.Id);
            Call("ApproveBootstrap_Click", Control<Button>("ApproveBootstrapButton"),
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => _dispatches == 1 && !_window.RunActiveForTest &&
                            !Field<bool>("_firstSyncApprovalInFlight"),
                "the failed first sync never restored the controls");
            Require(_dispatches == 1 && !Field<bool>("_firstSyncApprovalInFlight") &&
                    Control<Button>("ApproveBootstrapButton").IsEnabled &&
                    Feedback().Contains("stopped safely", StringComparison.Ordinal),
                "UI1 a failed run restored the controls with visible feedback and enqueued no new command: " +
                Feedback());
        }

        // WF2: the confirmation captures the complete authoritative plan even when only the first displayed page
        // of rows is loaded, and the primary action is labelled for what it does.
        internal void CheckConfirmationDoesNotRequireEveryDisplayedRow()
        {
            MainWindow.BootstrapReviewPageLoaderForTest = (pairId, _, offset, _, _, _, _) =>
                Task.FromResult(new SyncBootstrapReviewPage([Item(pairId)], offset, 5, 5, 2, 3, 0, true, true,
                    "paged-local", "paged-cloud"));
            MainWindow.BootstrapReviewLoaderForTest = (pairId, _, _, _, _) =>
                Task.FromResult<IReadOnlyList<SyncBootstrapReviewItem>>(
                    [Item(pairId), Item(pairId, "authoritative/second.txt")]);
            OpenReview(_first.Id);
            Require(System.Windows.Automation.AutomationProperties.GetName(
                        Control<Button>("ApproveBootstrapButton")) == "Confirm choices and sync",
                "WF2 the primary First Sync action is labelled for what it does: " +
                System.Windows.Automation.AutomationProperties.GetName(Control<Button>("ApproveBootstrapButton")));
            _confirmationAnswer = true;
            _confirmations = 0;
            _dispatches = 0;
            var gate = new TaskCompletionSource<SyncBootstrapActionResult>();
            SyncBootstrapActionPlan? captured = null;
            string? confirmation = null;
            MainWindow.BootstrapApprovalConfirmationForTest = text =>
            {
                _confirmations++;
                confirmation = text;
                return true;
            };
            MainWindow.BootstrapActionPlanForTest = (plan, _) =>
            {
                _dispatches++;
                captured = plan;
                return gate.Task;
            };
            Call("ApproveBootstrap_Click", Control<Button>("ApproveBootstrapButton"),
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => _dispatches == 1, "the paged confirmation never dispatched its plan");
            Require(captured is { } plan && plan.Choices.Count == 2 &&
                    plan.Choices.Any(choice => choice.RelativePath.EndsWith("authoritative/second.txt",
                        StringComparison.OrdinalIgnoreCase)) &&
                    confirmation is not null &&
                    confirmation.Contains("Copy 2 new item(s) from Left", StringComparison.Ordinal) &&
                    !confirmation.Contains("Upload 2 item(s)", StringComparison.Ordinal),
                "WF2 the confirmation captures the complete authoritative plan without every displayed row " +
                "being loaded");
            gate.SetResult(Applied());
            PumpUntil(() => !_window.RunActiveForTest && !Field<bool>("_firstSyncApprovalInFlight"),
                "the paged confirmation run never ended");
            MainWindow.BootstrapReviewPageLoaderForTest = (pairId, _, offset, _, _, _, _) =>
                Task.FromResult(new SyncBootstrapReviewPage([Item(pairId)], offset, 1, 1, 1, 0, 0, false, true,
                    "feedback-local-1", "feedback-cloud-1"));
            MainWindow.BootstrapReviewLoaderForTest = (pairId, _, _, _, _) =>
                Task.FromResult<IReadOnlyList<SyncBootstrapReviewItem>>([Item(pairId)]);
            MainWindow.BootstrapApprovalConfirmationForTest = _ =>
            {
                _confirmations++;
                return _confirmationAnswer;
            };
            MainWindow.BootstrapActionPlanForTest = (_, _) => Task.FromResult(Applied());
        }


        // UI1: while a first-sync approval is pending, the competing normal sync and scan entries are refused
        // through the one shared admission predicate, nothing is queued behind it, and both become available
        // again as soon as it leaves.
        internal void CheckCompetingEntriesAreAdmittedOnlyOnce()
        {
            var delayedLoad = new TaskCompletionSource<IReadOnlyList<SyncBootstrapReviewItem>>();
            MainWindow.BootstrapReviewLoaderForTest = (_, _, _, _, _) => delayedLoad.Task;
            _confirmationAnswer = false;
            _confirmations = 0;
            _dispatches = 0;
            MainWindow.BootstrapActionPlanForTest = (_, _) =>
            {
                _dispatches++;
                return Task.FromResult(Applied());
            };
            var normalRuns = 0;
            MainWindow.PairRunDispatcherForTest = (_, _) =>
            {
                normalRuns++;
                return Task.CompletedTask;
            };
            var scans = 0;
            MainWindow.BootstrapReviewPageLoaderForTest = (pairId, _, offset, _, _, _, _) =>
            {
                scans++;
                return Task.FromResult(new SyncBootstrapReviewPage([Item(pairId)], offset, 1, 1, 1, 0, 0, false,
                    true, "admission-local", "admission-cloud"));
            };
            try
            {
                OpenReview(_first.Id);
                Call("ApproveBootstrap_Click", Control<Button>("ApproveBootstrapButton"),
                    new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(Field<bool>("_firstSyncApprovalInFlight") &&
                        !Control<Button>("RunButton").IsEnabled &&
                        !Control<Button>("SyncAllNowButton").IsEnabled &&
                        !Control<Button>("EditorSyncNowButton").IsEnabled,
                    "UI1 a pending first-sync approval disables the competing sync and scan entry points");
                var scansBefore = scans;
                Call("Run_Click", Control<Button>("RunButton"), new RoutedEventArgs(ButtonBase.ClickEvent));
                Call("RefreshBootstrap_Click", new Button(), new RoutedEventArgs(ButtonBase.ClickEvent));
                Drain();
                Require(normalRuns == 0 && scans == scansBefore && !_window.RunActiveForTest,
                    "UI1 a normal sync and a scan requested while the approval is pending dispatch nothing and " +
                    "queue nothing behind it: runs=" + normalRuns + " scans=" + (scans - scansBefore));
                delayedLoad.SetResult([Item(_first.Id)]);
                PumpUntil(() => _confirmations == 1 && !Field<bool>("_firstSyncApprovalInFlight"),
                    "the pending first-sync approval never left");
                Require(_dispatches == 0 && Control<Button>("RunButton").IsEnabled &&
                        Control<Button>("SyncAllNowButton").IsEnabled,
                    "UI1 the cancelled approval released the shared admission so normal actions are available again");
                Call("NavigateTo", "Dashboard", true);
                Drain();
                Call("Run_Click", Control<Button>("RunButton"), new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpUntil(() => normalRuns == 1 && !_window.RunActiveForTest,
                    "the normal run did not finish its terminal readback after the approval left");
                Require(normalRuns == 1 && !_window.RunActiveForTest,
                    "UI1 the same normal sync entry runs exactly once after the approval leaves");
            }
            finally
            {
                MainWindow.PairRunDispatcherForTest = null;
            }
        }


        // UI2: every operation start replaces the previous run's phase start, progress instant, running pair/id
        // and detail through the one shared presentation reset, for first sync and for the ordinary run entry.
        internal void CheckNewOperationReplacesStaleTimes()
        {
            _dispatches = 0;
            var gate = new TaskCompletionSource<SyncBootstrapActionResult>();
            MainWindow.BootstrapActionPlanForTest = (_, _) =>
            {
                _dispatches++;
                return gate.Task;
            };
            OpenReview(_first.Id);
            var stale = DateTimeOffset.UtcNow.AddHours(-3);
            MakePresentationStale(stale);
            var startedAt = DateTimeOffset.Now;
            Call("ApproveBootstrap_Click", Control<Button>("ApproveBootstrapButton"),
                new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => _dispatches == 1, "the first-sync operation under test never started");
            Require(Field<DateTimeOffset?>("_workspacePhaseStarted") is { } phase && phase >= startedAt &&
                    Field<DateTimeOffset?>("_workspaceProgressUpdated") is { } progress && progress >= startedAt &&
                    Field<string?>("_workspaceRunningPair") == _first.Name &&
                    Field<string?>("_workspaceRunningPairId") == _first.Id &&
                    Field<string>("_workspaceStage") == "Starting First Sync" &&
                    Field<string>("_workspacePhaseDetail") != StaleDetail,
                "UI2 a first sync replaces the previous run's phase start, progress instant, running pair/id and " +
                "detail at its own actual start");
            gate.SetResult(Applied());
            PumpUntil(() => !_window.RunActiveForTest && !Field<bool>("_firstSyncApprovalInFlight"),
                "the first-sync operation under test never ended");

            var runDispatches = 0;
            var normalGate = new TaskCompletionSource();
            MainWindow.PairRunDispatcherForTest = (_, _) =>
            {
                runDispatches++;
                return normalGate.Task;
            };
            MakePresentationStale(stale);
            Call("NavigateTo", "Dashboard", true);
            Drain();
            var runStartedAt = DateTimeOffset.Now;
            Call("Run_Click", Control<Button>("RunButton"), new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => runDispatches == 1,
                "the ordinary run entry never started");
            Require(Field<DateTimeOffset?>("_workspacePhaseStarted") is { } runPhase && runPhase >= runStartedAt &&
                    Field<DateTimeOffset?>("_workspaceProgressUpdated") is { } runProgress &&
                    runProgress >= runStartedAt &&
                    Field<string?>("_workspaceRunningPair") is null &&
                    Field<string>("_workspaceStage") == "Checking folders" &&
                    Field<string>("_workspacePhaseDetail") != StaleDetail,
                "UI2 the ordinary run entry shares that one reset and never inherits the old phase or progress time");
            normalGate.SetResult();
            PumpUntil(() => !_window.RunActiveForTest,
                "the ordinary run entry never completed its terminal readback");
            MainWindow.PairRunDispatcherForTest = null;
        }

        // UI3: the aggregate Review N issues entry opens the All overview for the current account, both the
        // first-sync case and the unavailable-local-root case stay reachable from it, and a single issue still
        // opens that exact issue.
        internal void CheckAggregateIssueNavigationIsScoped()
        {
            var reasons = Field<Dictionary<string, string>>("_endpointAttentionReasonByPair");
            reasons[_problem.Id] = UnavailableRootReason;
            reasons[_other.Id] = UnavailableCloudReason;
            // Both real editor hierarchy and pair-health handlers receive the same controlled input before
            // navigation starts either lookup. Their production health/count/navigation decisions remain real.
            using var hierarchyInput = OverrideHierarchyInput(reasons);
            _window.LoadPairSetForTest(_pairSet);
            SelectAccount(_accountId);
            Row(_problem.Id).UpdateHealth(true, UnavailableRootReason);
            Row(_other.Id).UpdateHealth(true, UnavailableCloudReason);
            Refresh();
            Require(ActionLabel().Contains("Review 2 issues", StringComparison.Ordinal),
                "UI3 Home presents this account's aggregate Review 2 issues entry: " + ActionLabel());
            Field<TabControl>("_attentionTabs").SelectedIndex = 1;
            Drain();
            ClickHomeReview();
            Require(Field<string>("_workspacePage") == "Attention" &&
                    Field<TabControl>("_attentionTabs").SelectedIndex == 0 &&
                    Field<Border>("_attentionPage").Visibility == Visibility.Visible,
                "UI3 Review 2 issues opens the All issues overview for the current account instead of preserving " +
                "the First Sync selection");
            var titles = CaseTitles();
            var cliItems = _window.CliAttentionItems;
            Require(cliItems.Count == 3 && cliItems.Count(item => item.PairId == _first.Id || item.PairId == _problem.Id) == 2 &&
                    cliItems.Any(item => item.Title == "Review the First Sync" && item.PairId == _first.Id) &&
                    cliItems.Any(item => item.Title == "Local folder unavailable" && item.PairId == _problem.Id) &&
                    cliItems.Any(item => item.PairId == _other.Id),
                "A05 the all-account CLI retains all three actual issues, including the same two displayed in the selected account");
            Require(titles.Count == 2 && titles.Contains("Review the First Sync") &&
                    titles.Contains("Local folder unavailable") &&
                    !titles.Contains("Dropbox folder unavailable"),
                "UI3 the aggregate overview lists exactly this account's two issues: " + string.Join(", ", titles));

            var problemRow = Row(_problem.Id);
            ClickCaseRow("Local folder unavailable");
            Require(Field<string>("_workspacePage") == "Pairs" &&
                    problemRow.IsExpanded &&
                    ReferenceEquals(Control<DataGrid>("PairList").SelectedItem, problemRow),
                "UI3 the aggregate overview's unavailable-local-root row opens that exact pair problem (page=" +
                Field<string>("_workspacePage") + "; selected=" +
                (Control<DataGrid>("PairList").SelectedItem as PairRow)?.Id + "; expected=" + _problem.Id +
                "; expanded=" + problemRow.IsExpanded + ")");
            ClickHomeReview();
            Require(Field<TabControl>("_attentionTabs").SelectedIndex == 0,
                "UI3 returning through the aggregate entry lands on All again rather than the last subtab");
            ClickCaseRow("Review the First Sync");
            Require(Field<TabControl>("_attentionTabs").SelectedIndex == 1 &&
                    (Control<ComboBox>("BootstrapPairBox").SelectedValue as string) == _first.Id,
                "UI3 the aggregate overview's first-sync row opens that pair's First Sync review");

            // Pump until the actual background request finishes; one dispatcher frame alone does not
            // wait for Task.Run and could let old input complete after the deliberate healthy transition.
            PumpUntil(() => !Field<bool>("_loadingFolderSelection"),
                "UI3 the controlled editor hierarchy request did not finish before the healthy transition");
            reasons.Clear();
            Row(_problem.Id).UpdateHealth(false, string.Empty);
            Row(_other.Id).UpdateHealth(false, string.Empty);
            Refresh();
            Require(ActionLabel().Contains("Review 1 issue", StringComparison.Ordinal),
                "UI3 Home presents the single first-sync issue entry for this account: " + ActionLabel() +
                " (page=" + Field<string>("_workspacePage") +
                "; attention=" + string.Join(" | ", _window.CliAttentionItems) +
                "; firstSources=" + string.Join(",", _window.PairAttentionSourcesForTest(_first.Id)) +
                "; problemSources=" + string.Join(",", _window.PairAttentionSourcesForTest(_problem.Id)) +
                "; otherSources=" + string.Join(",", _window.PairAttentionSourcesForTest(_other.Id)) + ")");
            ClickHomeReview();
            Require(Field<TabControl>("_attentionTabs").SelectedIndex == 1 &&
                    (Control<ComboBox>("BootstrapPairBox").SelectedValue as string) == _first.Id,
                "UI3 a single first-sync issue still opens that exact issue instead of the aggregate overview");

            // The same entry with a single unavailable-local-root issue opens that exact pair instead.
            _window.LoadPairSetForTest(new SyncPairSet(
                [_first with { InitialReviewApproved = true }, _problem, _other], _problem.Id));
            reasons[_problem.Id] = UnavailableRootReason;
            Row(_problem.Id).UpdateHealth(true, UnavailableRootReason);
            Row(_other.Id).UpdateHealth(false, string.Empty);
            Refresh();
            Require(ActionLabel().Contains("Review 1 issue", StringComparison.Ordinal),
                "UI3 Home presents the single unavailable-local-root issue entry: " + ActionLabel());
            ClickHomeReview();
            Require(Field<string>("_workspacePage") == "Pairs" &&
                    ReferenceEquals(Control<DataGrid>("PairList").SelectedItem, Row(_problem.Id)),
                "UI3 a single unavailable-local-root issue still opens that exact pair problem (page=" +
                Field<string>("_workspacePage") + "; selected=" +
                (Control<DataGrid>("PairList").SelectedItem as PairRow)?.Id + ")");
        }

        // UI4: an actively checking or syncing pair keeps the running token while another pair needs attention,
        // and a pause, the running pair's own current-run failure and an idle scope stay distinct.
        internal void CheckActiveRunKeepsRunningToken()
        {
            _window.LoadPairSetForTest(_pairSet);
            SelectAccount(_accountId);
            var reasons = Field<Dictionary<string, string>>("_endpointAttentionReasonByPair");
            reasons[_problem.Id] = UnavailableRootReason;
            Row(_problem.Id).UpdateHealth(true, UnavailableRootReason);
            _dispatches = 0;
            var gate = new TaskCompletionSource<SyncBootstrapActionResult>();
            MainWindow.BootstrapActionPlanForTest = (_, _) =>
            {
                _dispatches++;
                return gate.Task;
            };
            void Start()
            {
                OpenReview(_first.Id);
                Call("ApproveBootstrap_Click", Control<Button>("ApproveBootstrapButton"),
                    new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpUntil(() => _window.RunActiveForTest, "the reviewed run never started");
                Refresh();
            }
            void Present(SyncAuthorityBatchProgress progress)
            {
                Call("PresentBatchProgressAsync", progress, "First sync", false);
                Drain();
                Refresh();
            }
            Start();
            var glyph = Field<CommandGlyph>("_homeStateGlyph");
            Require(SameBrush(glyph.Foreground, ResourceBrush("FocusBrush")) && glyph.IconName == "sync",
                "UI4 an active run stays blue while another pair needs attention");

            // Feed a failure through the actual presentation entry point, then terminate the real approval
            // handler. No direct edits of the failure collections substitute for that transition.
            Present(new SyncAuthorityBatchProgress(_first, "Blocked", null, "FixtureFailure", UnavailableRootReason));
            gate.SetException(new IOException(UnavailableRootReason));
            PumpUntil(() => !_window.RunActiveForTest && !Field<bool>("_firstSyncApprovalInFlight"),
                "the failed reviewed run never terminated");
            Refresh();
            Require(SameBrush(glyph.Foreground, ResourceBrush("ErrorBrush")) &&
                    _window.TrayAttentionCases.Any(item => item.Pair.Id == _first.Id && item.IsProblem &&
                        item.Description.Contains("Local folder unavailable", StringComparison.Ordinal)),
                "UI4 a terminated failed run is red and exposes its actual retained reason");

            gate = new TaskCompletionSource<SyncBootstrapActionResult>();
            Start();
            Require(_dispatches == 2 && Field<HashSet<string>>("_runFailurePairIds").Contains(_first.Id) &&
                    SameBrush(glyph.Foreground, ResourceBrush("FocusBrush")) && glyph.IconName == "sync",
                "UI4 the real retry is blue even while that pair's previous failure remains retained");
            SelectAllAccounts();
            SetField("_workspacePaused", true);
            Refresh();
            Require(SameBrush(glyph.Foreground, ResourceBrush("WarningBrush")),
                "UI4 a paused active run remains yellow");
            SetField("_workspacePaused", false);
            Present(new SyncAuthorityBatchProgress(_first, "Completed", null, null, null));
            gate.SetResult(Applied());
            PumpUntil(() => !_window.RunActiveForTest && !Field<bool>("_firstSyncApprovalInFlight"),
                "the successful retry never terminated");
            Require(!Field<HashSet<string>>("_runFailurePairIds").Contains(_first.Id),
                "UI4 successful completion clears the prior failure through the production transition");
            reasons.Clear();
            Row(_first.Id).UpdateHealth(false, string.Empty);
            Row(_problem.Id).UpdateHealth(false, string.Empty);
            Refresh();
        }

        // ENG5: an awaiting-first-review pair is excluded from the real automatic poll and the real live-monitor
        // configuration through one shared rule, while its approved sibling still runs and manual discovery and
        // the Start action remain available for the unapproved pair.
        internal void CheckAwaitingReviewPairsAreExcludedFromAutomaticSync()
        {
            Field<Dictionary<string, string>>("_endpointAttentionReasonByPair").Clear();
            Field<Dictionary<string, string>>("_runAttentionReasonByPair").Clear();
            foreach (var pair in _pairSet.Pairs) Row(pair.Id).UpdateHealth(false, string.Empty);
            _window.LoadPairSetForTest(new SyncPairSet(
                [_first with { InitialReviewApproved = false }, _problem, _other], _first.Id));
            Pump(_window.AuthorityHost.SaveAutomationAsync(
                _window.AuthorityHost.LoadAutomation() with { Enabled = true }));
            var polled = new List<string>();
            MainWindow.PairRunDispatcherForTest = (pairs, _) =>
            {
                polled.AddRange(pairs.Select(pair => pair.Id));
                return Task.CompletedTask;
            };
            Call("AutomationTimer_Tick", null, EventArgs.Empty);
            PumpUntil(() => polled.Count > 0, "the real automatic safety poll never dispatched a run");
            Require(!polled.Contains(_first.Id) && polled.Contains(_problem.Id),
                "ENG5 the real safety poll excludes the awaiting-first-review pair and still polls its approved " +
                "sibling: " + string.Join(", ", polled));

            var monitored = new List<string>();
            MainWindow.DropboxConnectionCheckForTest = () => Task.FromResult(true);
            MainWindow.MonitorConfiguratorForTest = (pairs, _, _) =>
            {
                monitored.AddRange(pairs.Select(pair => pair.Id));
                return Task.FromResult<IReadOnlyList<SyncAuthorityMonitorStartResult>>([]);
            };
            Pump(_window.ConfigureLocalMonitorsForTestAsync(_window.AuthorityHost.LoadAutomation()));
            Require(!monitored.Contains(_first.Id) && monitored.Contains(_problem.Id) &&
                    monitored.Contains(_other.Id),
                "ENG5 the real live-monitor configuration never watches an awaiting-first-review pair: " +
                string.Join(", ", monitored));
            Require(_window.PairRows.Single(row => row.Id == _first.Id).MonitorState ==
                    "Waiting for first-sync review",
                "ENG5 an awaiting-first-review pair discloses why it is not monitored: " +
                _window.PairRows.Single(row => row.Id == _first.Id).MonitorState);
            MainWindow.MonitorConfiguratorForTest = null;
            MainWindow.DropboxConnectionCheckForTest = null;

            var manualScans = 0;
            MainWindow.BootstrapReviewPageLoaderForTest = (pairId, _, offset, _, _, _, _) =>
            {
                manualScans++;
                return Task.FromResult(new SyncBootstrapReviewPage([Item(pairId)], offset, 1, 1, 1, 0, 0, false,
                    true, "eng5-local", "eng5-cloud"));
            };
            OpenReview(_first.Id);
            Require(manualScans > 0 && _window.BootstrapRows.Count > 0 &&
                    Control<Button>("ApproveBootstrapButton").IsEnabled,
                "ENG5 manual first-inventory discovery and its Start action remain available for the unapproved pair");

            var approvedFirst = _first with { InitialReviewApproved = true, PolicyRevision = _first.PolicyRevision + 1 };
            Pump(_window.AuthorityHost.Authority.RegisterPairAsync(approvedFirst.Id, approvedFirst.PolicyRevision));
            Pump(_window.AuthorityHost.Authority.RetainPairDefinitionAsync(approvedFirst, true));
            _window.LoadPairSetForTest(new SyncPairSet([approvedFirst, _problem, _other], approvedFirst.Id));
            var afterApproval = new List<string>();
            MainWindow.PairRunDispatcherForTest = (pairs, _) =>
            {
                afterApproval.AddRange(pairs.Select(pair => pair.Id));
                return Task.CompletedTask;
            };
            Call("AutomationTimer_Tick", null, EventArgs.Empty);
            PumpUntil(() => afterApproval.Count > 0, "the automatic poll never ran after the review");
            Require(afterApproval.Contains(_first.Id),
                "ENG5 an approved pair becomes automatically eligible again: " + string.Join(", ", afterApproval));
            MainWindow.PairRunDispatcherForTest = null;
        }

        // AUD16: the first-sync completion/navigation/Add Pair chain must not be replaced by a
        // saved-editor-only check. The existing seams supply inventory, authority completion and
        // folder-browser INPUT; the real handlers still own navigation, dirty-state admission,
        // draft/session creation and cancellation. No field is cleared or session installed by this case.
        internal void CheckFirstSyncNavigationThenFreshAddPairAndCancel()
        {
            var previousBrowser = MainWindow.PairFolderBrowserFactoryForTest;
            var previousLocalRoots = MainWindow.LocalRootNodeFactoryForTest;
            var previousPageLoader = MainWindow.BootstrapReviewPageLoaderForTest;
            var previousInventoryLoader = MainWindow.BootstrapReviewLoaderForTest;
            var previousPlan = MainWindow.BootstrapActionPlanForTest;
            var previousConfirmation = MainWindow.BootstrapApprovalConfirmationForTest;
            var previousMonitors = MainWindow.MonitorConfiguratorForTest;
            var previousStopper = MainWindow.MonitorStopperForTest;
            var previousUnsavedChoice = _window.UnsavedNavigationChoiceForTest;
            // Earlier cases legitimately completed _first's review. Use a distinct, genuinely
            // unapproved retained pair here; never rewind consent merely to enable the button.
            var navigationRoot = Path.Combine(Path.GetDirectoryName(_first.LocalRoot)!, "navigation-first-sync");
            Directory.CreateDirectory(navigationRoot);
            using var navigationLease = PilotDirectoryLease.Open(navigationRoot);
            var navigationPair = new SyncPair("feedback-navigation-first-sync", "Feedback Navigation First Sync",
                navigationRoot, navigationLease.Identity.VolumeSerialNumber, navigationLease.Identity.FileId,
                "/aud16-navigation", "id:aud16-navigation", ConnectionId: _accountId, InitialReviewApproved: false);
            Pump(_window.AuthorityHost.Authority.RegisterPairAsync(navigationPair.Id, navigationPair.PolicyRevision));
            Pump(_window.AuthorityHost.Authority.RetainPairDefinitionAsync(navigationPair, false));
            var load = _window.AuthorityHost.LoadPairsAsync();
            Pump(load);
            var retained = load.Result;
            var saved = retained.Pairs.Single(pair => pair.Id == navigationPair.Id);
            var savedDefinitions = Definitions(retained);
            var browser = new ScriptedFolderBrowserService
            {
                ListHandler = (parent, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(parent) || parent == "/" ||
                        parent == PilotPath.DropboxNamespaceRootIdentity("feedback-root"))
                        return Task.FromResult(new DropboxFolderPage(
                            [new DropboxFolderEntry("a", saved.DropboxRoot, saved.DropboxFolderId)], null));
                    if (parent == saved.DropboxFolderId)
                        return Task.FromResult(new DropboxFolderPage([], null));
                    throw new InvalidDataException("AUD16 unexpected controlled folder-list parent: " + parent);
                }
            };
            var unsavedPrompts = 0;
            var confirmations = 0;
            var dispatches = 0;
            SyncBootstrapActionPlan? submittedPlan = null;
            var completion = new TaskCompletionSource<SyncBootstrapActionResult>();
            try
            {
                MainWindow.PairFolderBrowserFactoryForTest = _ =>
                    Task.FromResult<IDropboxFolderBrowserService>(browser);
                MainWindow.LocalRootNodeFactoryForTest = _ => [LocalFolderNode.CreateForTest(saved.LocalRoot, [])];
                MainWindow.BootstrapReviewPageLoaderForTest = (pairId, _, offset, _, _, _, _) =>
                    Task.FromResult(new SyncBootstrapReviewPage([Item(pairId)], offset, 1, 1, 1, 0, 0,
                        false, true, "aud16-local-1", "aud16-cloud-1"));
                MainWindow.BootstrapReviewLoaderForTest = (pairId, _, _, _, _) =>
                    Task.FromResult<IReadOnlyList<SyncBootstrapReviewItem>>([Item(pairId)]);
                MainWindow.BootstrapApprovalConfirmationForTest = _ => { confirmations++; return true; };
                MainWindow.BootstrapActionPlanForTest = (plan, _) =>
                {
                    submittedPlan = plan;
                    dispatches++;
                    return completion.Task;
                };
                // Completion may reconfigure existing automation. These existing input seams retain
                // that UI decision without opening a native watcher, credential or provider boundary.
                MainWindow.MonitorConfiguratorForTest = (_, _, _) =>
                    Task.FromResult<IReadOnlyList<SyncAuthorityMonitorStartResult>>([]);
                MainWindow.MonitorStopperForTest = () => Task.CompletedTask;
                _window.UnsavedNavigationChoiceForTest = () =>
                {
                    unsavedPrompts++;
                    return AppDialogChoice.Cancel;
                };
                _window.LoadPairSetForTest(retained);
                SelectAllAccounts();
                Navigate("Pairs");
                OpenSavedEditor();
                var editedSession = Field<SyncPairEditorSession>("_endpointSession");
                Require(editedSession.EditingPairId == saved.Id &&
                        editedSession.EditingPolicyRevision == saved.PolicyRevision &&
                        Control<TextBox>("PairNameBox").Text == saved.Name &&
                        Control<TextBox>("LocalRootBox").Text == saved.LocalRoot &&
                        Control<TextBox>("DropboxRootBox").Text == saved.DropboxRoot,
                    "AUD16 saved-edit control opens the actual retained pair and revision, not an invented draft");
                var editedName = saved.Name + " unsaved AUD16 edit";
                Control<TextBox>("PairNameBox").Text = editedName;
                Navigate("Dashboard");
                Require(unsavedPrompts == 1 && Field<string>("_workspacePage") == "Pairs" &&
                        Control<TextBox>("PairNameBox").Text == editedName && editedSession.IsActive &&
                        ReferenceEquals(editedSession, Field<SyncPairEditorSession>("_endpointSession")) &&
                        SavedDefinitions() == savedDefinitions,
                    "AUD16 deliberate saved edit invokes the real unsaved-navigation guard; Stay Here keeps the edit and saved identity unchanged");
                var cancel = Control<Button>("CancelPairButton");
                Require(cancel.IsEnabled && cancel.Visibility == Visibility.Visible,
                    "AUD16 the genuine saved edit exposes its enabled Discard Changes control");
                cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Drain();
                Require(!editedSession.IsActive && Field<SyncPairEditorSession?>("_endpointSession") is null &&
                        SavedDefinitions() == savedDefinitions,
                    "AUD16 actual saved-editor cancel invalidates its session without saving the deliberate edit");

                OpenSavedEditor();
                Call("OpenFirstSync_Click", Control<DataGrid>("PairList"), new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpUntil(() => _window.BootstrapRows.Count == 1 && Control<Button>("ApproveBootstrapButton").IsEnabled,
                    "AUD16 actual first-sync review never became actionable");
                Require(Field<string>("_workspacePage") == "Attention" &&
                        Field<TabControl>("_attentionTabs").SelectedIndex == 1 &&
                        Control<ComboBox>("BootstrapPairBox").SelectedValue as string == saved.Id &&
                        _window.BootstrapRows.Single().PairId == saved.Id,
                    "AUD16 actual Review first sync handler navigates to the retained pair's real First Sync tab");
                _window.BootstrapRows.Single().SelectedAction = "Skip for now";
                var choicesBefore = Choices();
                var start = Control<Button>("ApproveBootstrapButton");
                start.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpUntil(() => dispatches == 1, "AUD16 the real reviewed approval handler never dispatched");
                Require(confirmations == 1 && _window.RunActiveForTest && submittedPlan is { } plan &&
                        plan.PairId == saved.Id && plan.LocalScanGenerationId == "aud16-local-1" &&
                        plan.DropboxScanGenerationId == "aud16-cloud-1" && plan.Choices.Count == 1 &&
                        plan.Choices.Single().Action == "Skip for now",
                    "AUD16 real approval captures the retained review generations and non-default choice before controlled completion");
                // The controlled producer input commits only the approval the real completed plan
                // would retain. Actual UI completion must reload that authority, not a fake pair set.
                var approved = saved with { InitialReviewApproved = true };
                Pump(_window.AuthorityHost.Authority.RetainPairDefinitionAsync(approved, false));
                var approvedDefinitions = Definitions(retained with
                {
                    Pairs = retained.Pairs.Select(pair => pair.Id == saved.Id ? approved : pair).ToArray()
                });
                completion.SetResult(new SyncBootstrapActionResult(approved, new SyncRunResult(saved.Id, []), 0, 1, true));
                PumpUntil(() => !_window.RunActiveForTest && !Field<bool>("_firstSyncApprovalInFlight"),
                    "AUD16 the real first-sync completion/readback never released its controls");
                Require(Feedback().Contains("approval saved", StringComparison.Ordinal) &&
                        Field<SyncPairSet>("_pairSet").Pairs.Single(pair => pair.Id == saved.Id).PolicyRevision == saved.PolicyRevision &&
                        _window.BootstrapRows.Single().SelectedAction == "Skip for now" &&
                        Choices().SequenceEqual(choicesBefore) && SavedDefinitions() == approvedDefinitions,
                    "AUD16 actual completion reloads only the intended approval and preserves endpoint definitions, policy and first-sync choices");
                var reviewedSession = Field<SyncPairEditorSession>("_endpointSession");
                Navigate("Pairs");
                Require(Field<string>("_workspacePage") == "Pairs" && unsavedPrompts == 1,
                    "AUD16 navigation after actual first-sync completion has no spurious unsaved-pair prompt");
                var add = Control<Button>("AddPairButton");
                Require(add.IsEnabled && add.IsVisible, "AUD16 actual Add Pair button is available after leaving First Sync");
                add.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Drain();
                Require(unsavedPrompts == 1,
                    "AUD16 actual Add Pair admission does not treat the clean reviewed pair as an unsaved edit");
                PumpUntil(() => Field<bool>("_editingNewPair") && Field<SyncPairEditorSession?>("_endpointSession") is not null,
                    "AUD16 actual Add Pair never opened a fresh editor");
                var fresh = Field<SyncPairEditorSession>("_endpointSession");
                Require(fresh.IsActive && !ReferenceEquals(fresh, reviewedSession) && !reviewedSession.IsActive &&
                        fresh.Generation > reviewedSession.Generation && fresh.EditingPairId is null &&
                        fresh.EditingPolicyRevision is null && fresh.Draft(SyncPairSide.Left).IsEmpty &&
                        fresh.Draft(SyncPairSide.Right).IsEmpty && Control<DataGrid>("PairList").SelectedItem is null &&
                        Field<string?>("_editingPairRootId") is null &&
                        Control<ContentControl>("NewPairEditorHost").Visibility == Visibility.Visible,
                    "AUD16 Add Pair creates a new unbound generation with no prior pair, expected revision or endpoint identity");
                Require(Control<TextBox>("PairNameBox").Text.Length == 0 &&
                        Control<TextBox>("LocalRootBox").Text.Length == 0 &&
                        Control<TextBox>("DropboxRootBox").Text.Length == 0 &&
                        Field<DropboxFolderEntry?>("_selectedDropboxFolder") is null &&
                        Field<string?>("_draftBrowseLocalRoot") is null &&
                        Field<DropboxFolderEntry?>("_draftBrowseDropboxRoot") is null &&
                        Field<IReadOnlyList<SyncSelectionRule>>("_draftSelectionRules").Count == 0 &&
                        _window.FolderSelectionRows.Count == 0 && !Control<Button>("SavePairButton").IsEnabled &&
                        Choices().SequenceEqual(choicesBefore),
                    "AUD16 new editor is blank, contains no reviewed path/name/actions and cannot save an incomplete pair; review choices stay separately retained");
                // Complete a real editor load AFTER live progress has been presented.
                // Only provider input is controlled; production error routing decides
                // whether this late browser failure can overwrite the active operation.
                var priorList = browser.ListHandler;
                var delayedList = new TaskCompletionSource<DropboxFolderPage>(TaskCreationOptions.RunContinuationsAsynchronously);
                browser.ListHandler = (_, _) => delayedList.Task;
                try
                {
                    var node = DropboxFolderNode.CreateRoot(new DropboxFolderGraphBudget(4, 8));
                    var pendingList = (Task)typeof(MainWindow).GetMethod("LoadDropboxRootNodeAsync",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_window,
                        [node, true, Field<int>("_pairEditorGeneration"), CancellationToken.None])!;
                    Require(node.IsLoading && !pendingList.IsCompleted,
                        "A01 late browser regression reaches the actual asynchronous folder load");
                    SetRunActive(true);
                    const string liveDetail = "Reading file metadata and content identity: Docs/current.txt";
                    Call("ShowRunStatus", liveDetail);
                    delayedList.SetException(new IOException("Controlled late folder-list failure"));
                    Pump(pendingList);
                    Require(Field<TextBlock>("_operationFeedback").Text == liveDetail &&
                            Field<string>("_workspacePhaseDetail") == liveDetail && !node.IsLoading,
                        "A01 a late folder-browser failure cannot replace current file/process output");
                    Require(Control<TextBlock>("DropboxRootBrowserStatus").Text.Contains("Controlled late folder-list failure") &&
                            Control<TextBlock>("PairEditorStatusText").Text.Contains("Controlled late folder-list failure") &&
                            Control<TextBox>("ConsoleText").Text.Contains("Controlled late folder-list failure"),
                        "A01 the late failure remains visible in its browser/editor and diagnostic log");
                }
                finally { SetRunActive(false); browser.ListHandler = priorList; }
                cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Drain();
                Require(!fresh.IsActive && !Field<bool>("_editingNewPair") &&
                        Field<SyncPairEditorSession?>("_endpointSession") is null &&
                        Control<ContentControl>("NewPairEditorHost").Visibility == Visibility.Collapsed &&
                        SavedDefinitions() == approvedDefinitions && Choices().SequenceEqual(choicesBefore) &&
                        confirmations == 1 && dispatches == 1 && browser.CreateCalls == 0,
                    "AUD16 actual new-pair cancel closes the fresh session with no saved-pair mutation, lost first-sync choice or extra dispatch/folder creation");
            }
            finally
            {
                if (Field<SyncPairEditorSession?>("_endpointSession") is { IsActive: true })
                    Control<Button>("CancelPairButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                MainWindow.PairFolderBrowserFactoryForTest = previousBrowser;
                MainWindow.LocalRootNodeFactoryForTest = previousLocalRoots;
                MainWindow.BootstrapReviewPageLoaderForTest = previousPageLoader;
                MainWindow.BootstrapReviewLoaderForTest = previousInventoryLoader;
                MainWindow.BootstrapActionPlanForTest = previousPlan;
                MainWindow.BootstrapApprovalConfirmationForTest = previousConfirmation;
                MainWindow.MonitorConfiguratorForTest = previousMonitors;
                MainWindow.MonitorStopperForTest = previousStopper;
                _window.UnsavedNavigationChoiceForTest = previousUnsavedChoice;
            }

            void Navigate(string page)
            {
                var navigation = Control<ListBox>("Navigation");
                navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => item.Tag?.ToString() == page);
                Drain();
            }

            void OpenSavedEditor()
            {
                Control<DataGrid>("PairList").SelectedItem = Row(saved.Id);
                Call("EditSelectedPair");
                Pump(_window.ExistingPairEditorInitializationForTest);
                Drain();
            }

            string SavedDefinitions()
            {
                var current = _window.AuthorityHost.LoadPairsAsync();
                Pump(current);
                return Definitions(current.Result);
            }

            static string Definitions(SyncPairSet pairs) => System.Text.Json.JsonSerializer.Serialize(
                pairs.Pairs.OrderBy(pair => pair.Id, StringComparer.Ordinal).ToArray());

            string[] Choices() => Field<Dictionary<BootstrapActionKey, BootstrapChoiceState>>("_bootstrapActionOverrides")
                .OrderBy(entry => entry.Key.PairId, StringComparer.Ordinal)
                .ThenBy(entry => entry.Key.RelativePath, StringComparer.Ordinal)
                .Select(entry => $"{entry.Key.PairId}|{entry.Key.RelativePath}|{entry.Value.Group}|{entry.Value.Action}").ToArray();
        }

        private void OpenReview(string pairId)
        {
            Call("OpenFirstSyncReview", pairId);
            PumpUntil(() => _window.BootstrapRows.Count > 0 &&
                            Control<Button>("ApproveBootstrapButton").IsEnabled,
                "the first-sync review for " + pairId + " never became actionable");
        }

        private void SelectAccount(string accountId)
        {
            Field<List<ComboBox>>("_workspaceAccountPickers")[0].SelectedValue = accountId;
            PumpUntil(() => string.Equals(Field<string?>("_workspaceAccountId"), accountId, StringComparison.Ordinal),
                "the workspace account scope never changed");
            Drain();
        }

        private void SelectAllAccounts()
        {
            Field<List<ComboBox>>("_workspaceAccountPickers")[0].SelectedIndex = 0;
            PumpUntil(() => Field<string?>("_workspaceAccountId") is null,
                "the workspace account scope never returned to all accounts");
            Drain();
        }

        internal void CheckLiveOperationOutputAndActionFeedback()
        {
            SelectAllAccounts();
            Call("NavigateTo", "Activity", true);
            Call("SelectAllActivityCategories");
            Control<ComboBox>("ActivityPairBox").SelectedIndex = 0;
            Control<ComboBox>("ActivityFilterBox").SelectedIndex = 0;
            Control<ComboBox>("ActivityTypeFilter").SelectedIndex = 0;
            Control<TextBox>("ActivitySearchBox").Clear();
            Field<TextBox>("_activityRunBox").Clear();
            Pump(_window.LoadActivityPageForTestAsync());
            _window.ActivityRows.Clear();
            var now = DateTimeOffset.UtcNow;
            var sameObservation = typeof(MainWindow).GetMethod("SameLiveObservation", BindingFlags.NonPublic | BindingFlags.Static)!;
            var started = new ActivityRow(now.ToString("O"), "same-file.txt", "Upload", "File transfer",
                PairId: _first.Id, OperationId: "same-operation", Phase: SyncActivityPhase.Running);
            Require((bool)sameObservation.Invoke(null, [started, started])! &&
                    !(bool)sameObservation.Invoke(null, [started, started with { Phase = SyncActivityPhase.Completed }])! &&
                    !(bool)sameObservation.Invoke(null, [started, started with { Outcome = "Failed" }])!,
                "A02 history merging preserves different operation phases and outcomes for the same file");
            var typed = started with { OperationKind = SyncAuthorityOperationKind.CreateRemote };
            Require((bool)sameObservation.Invoke(null, [typed, typed with { Outcome = "Starting upload" }])!,
                "A02 the same typed operation merges despite different live and retained display wording");
            for (var i = 0; i < 20; i++)
            {
                var step = new SyncComparisonProgress("Checking Local Files", "Docs/file-" + i + ".txt", i + 1, 20,
                    now, now.AddMilliseconds(i), "Reading file metadata and content identity");
                PresentBatchProgress(new SyncAuthorityBatchProgress(_first, "Comparing", null, null, null,
                    Comparison: step), "First sync", false);
                Drain();
            }
            Require(_window.ActivityRows.Count(row => row.Path.StartsWith("Docs/file-", StringComparison.Ordinal)) == 20,
                "A01/A02 all distinct fast file observations reach the live Activity rows; actual=" + _window.ActivityRows.Count);
            Pump(_window.LoadActivityPageForTestAsync());
            Require(_window.ActivityRows.Count(row => row.Path.StartsWith("Docs/file-", StringComparison.Ordinal)) == 20,
                "A02 a retained-page refresh cannot erase observations still awaiting their database batch");
            Require(Field<System.Collections.ObjectModel.ObservableCollection<ActivityRow>>("_recentRows")
                    .Count(row => row.Path.StartsWith("Docs/file-", StringComparison.Ordinal)) == 20,
                "A02 the actual visible Activity feed renders every distinct live operation");
            var currentFeedback = Field<TextBlock>("_operationFeedback").Text;
            var currentDetail = Field<string>("_workspacePhaseDetail");
            Require(Control<TextBox>("ConsoleText").Text.Contains("Docs/file-19.txt", StringComparison.Ordinal),
                "A01 the live text retains the actual file Docs/file-19.txt");
            Require(currentFeedback.Contains("Reading file metadata", StringComparison.Ordinal),
                "A01 current-page output identifies the actual operation; feedback=" + currentFeedback +
                "; retained phase detail=" + currentDetail + "; workspaceReady=" + Field<bool>("_workspaceReady"));
            PresentBatchProgress(new SyncAuthorityBatchProgress(_first, "Comparing", null, null, null,
                Comparison: new SyncComparisonProgress("Saving Folder Inventories", _first.Name, 20, 20, now, now,
                    "Committing the local inventory transaction to the sync database")), "First sync", false);
            Drain();
            Require(Field<TextBlock>("_operationFeedback").Text.Contains("Committing the local inventory transaction", StringComparison.Ordinal),
                "A01 a database stage identifies its process instead of inventing a currently copied file");
            var lastProgress = Field<DateTimeOffset?>("_workspaceProgressUpdated");
            PresentBatchProgress(new SyncAuthorityBatchProgress(_first, "Comparing", null, null, null,
                Comparison: new SyncComparisonProgress("Saving Folder Inventories", _first.Name, 20, 20, now, now.AddSeconds(30),
                    "Committing the local inventory transaction to the sync database")), "First sync", false);
            Drain();
            Require(Field<DateTimeOffset?>("_workspaceProgressUpdated") == lastProgress,
                "A05 a repeated database heartbeat does not pretend that file work advanced");
            PresentBatchProgress(new SyncAuthorityBatchProgress(_first, "Comparing", null, null, null,
                Comparison: new SyncComparisonProgress("Saving Folder Inventories", _first.Name, 21, 30, now, now.AddSeconds(31),
                    "Committing the local inventory transaction to the sync database")), "First sync", false);
            Drain();
            Require(Field<DateTimeOffset?>("_workspaceProgressUpdated") > lastProgress,
                "A05 genuinely changed measured progress advances the live progress clock");
            var activeDetail = Field<string>("_workspacePhaseDetail");
            PresentBatchProgress(new SyncAuthorityBatchProgress(_other, "Pending", null, null, null),
                "Recovery", true);
            Drain();
            Require(Field<string>("_workspaceRunningPairId") == _first.Id &&
                    Field<string>("_workspacePhaseDetail") == activeDetail,
                "A05 a queued different pair cannot inherit and relabel the active pair's progress");
            PresentBatchProgress(new SyncAuthorityBatchProgress(_other, "Comparing", null, null, null,
                Comparison: new SyncComparisonProgress("Saving Folder Inventories", _other.Name, 1, 5,
                    now.AddMinutes(1), now.AddMinutes(1), "Committing the other inventory")), "Recovery", true);
            Drain();
            Require(Field<DateTimeOffset?>("_workspacePhaseStarted") == now.AddMinutes(1),
                "A05 the same phase for a different pair has its own phase clock");
            PresentBatchProgress(new SyncAuthorityBatchProgress(_first, "Running", null, null, null,
                Action: new SyncAuthorityLiveAction("fixture-visible-byte-progress", "Docs/current.txt", "Upload",
                    "Reading bytes for upload", "37 of 100 bytes processed; destination has not yet confirmed the upload.", now,
                    "File", "TXT", SyncAuthorityOperationKind.CreateRemote, SyncActivityPhase.Running),
                Comparison: new SyncComparisonProgress("Saving Folder Inventories", _first.Name, 20, 20, now, now,
                    "Stale inventory observation")), "First sync", false);
            Drain();
            Require(Field<string>("_workspacePhaseDetail").Contains("37 of 100") &&
                    Field<string>("_workspacePhaseDetail").Contains("Docs/current.txt") &&
                    Field<string>("_workspaceStage") == "Reading bytes for upload",
                "A01 Home and tray retain the current file, actual transfer phase and measured bytes ahead of a stale comparison observation");
            PresentBatchProgress(new SyncAuthorityBatchProgress(_first, "Failed", null, "IOException",
                "Current test file could not be opened; retry that file after closing its editor.",
                Comparison: new SyncComparisonProgress("Saving Folder Inventories", _first.Name, 20, 20, now, now,
                    "Stale inventory observation")), "First sync", false);
            Drain();
            Require(Field<string>("_workspacePhaseDetail").Contains("Current test file could not be opened", StringComparison.Ordinal) &&
                    !Field<string>("_workspacePhaseDetail").Contains("Stale inventory", StringComparison.Ordinal) &&
                    Field<string>("_workspaceStage") == "Sync needs attention" &&
                    Field<TextBlock>("_operationFeedback").Text.Contains("retry that file", StringComparison.Ordinal),
                "A01/A06 the real terminal callback displays the actual failure and recovery instruction, never stale inventory progress");
            var gate = new TaskCompletionSource();
            var starts = 0;
            var button = WorkspaceVisuals.AsyncButton("Refresh", async (_, _) => { starts++; await gate.Task; });
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(starts == 1 && button.Content is StackPanel panel && panel.Children.OfType<CommandGlyph>().Any(),
                "A03 shared async action shows a progress glyph immediately and refuses a repeated click");
            gate.SetResult(); Drain();
            Require(button.Content is not StackPanel stack || !stack.Children.OfType<TextBlock>().Any(text => text.Text == "Working…"),
                "A03 action caption is restored when the actual asynchronous operation completes");
            ActionAvailability.Bind(button, () => "Select an account before refreshing.");
            button.IsEnabled = false;
            Require(ToolTipService.GetShowOnDisabled(button) && (string?)button.ToolTip == "Select an account before refreshing.",
                "A04 disabled action retains its exact prerequisite explanation");
            var failedButton = WorkspaceVisuals.AsyncButton("Read test folder", (_, _) =>
                Task.FromException(new IOException("Folder is inaccessible; access_token=fixture-secret")));
            failedButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain();
            Require(failedButton.ToolTip?.ToString()?.Contains("Folder is inaccessible") == true &&
                failedButton.ToolTip?.ToString()?.Contains("fixture-secret") == false &&
                System.Windows.Automation.AutomationProperties.GetItemStatus(failedButton).Contains("did not complete"),
                "A03 a failed async action restores its caption and retains the exact redacted cause instead of escaping the UI dispatcher");
        }

        internal void CheckExplicitIdentityRecoveryPresentation()
        {
            SelectAllAccounts();
            SetRunActive(false);
            Call("OpenFirstSyncReview", _first.Id);
            Drain();
            var oldProblems = Field<Dictionary<string, SyncFirstSyncProblem>>("_firstSyncProblemsByPair");
            var problem = new SyncFirstSyncProblem("first_sync_identity_recovery_required", "scan",
                "The retained copied-file mapping needs verification.", "original.txt",
                "Use Repair copied file mappings; both files will be preserved.", "fixture-recovery-run",
                _first.Id, _first.Name, _first.ConnectionId, DateTimeOffset.UtcNow,
                SyncEndpointContext.Capture(_first));
            try
            {
                SetField("_firstSyncProblemsByPair", new Dictionary<string, SyncFirstSyncProblem> { [_first.Id] = problem });
                Call("PresentRetainedFirstSyncProblem");
                var repair = Control<Button>("RepairBootstrapMappingsButton");
                Require(repair.Visibility == Visibility.Visible && repair.IsEnabled &&
                        repair.ToolTip?.ToString()?.Contains("Uncertain operations are refused") == true &&
                        Control<Button>("BootstrapRetainedProblemLink").Tag is SyncFirstSyncProblem { CommandId: "fixture-recovery-run" },
                    "Identity recovery is an explicit explanatory action beside the exact retained failure/run, not another blind Scan Again loop");
                SetRunActive(true);
                Call("PresentRetainedFirstSyncProblem");
                Require(!repair.IsEnabled && ToolTipService.GetShowOnDisabled(repair),
                    "The recovery action is disabled during other work and retains its disabled-button explanation");
                SetRunActive(false);
                SetField("_firstSyncProblemsByPair", new Dictionary<string, SyncFirstSyncProblem> { [_first.Id] = problem with { Code = "first_sync_scan_failed" } });
                Call("PresentRetainedFirstSyncProblem");
                Require(repair.Visibility == Visibility.Collapsed,
                    "Unrelated failures never offer copied-file mapping recovery");
                SetField("_firstSyncProblemsByPair", new Dictionary<string, SyncFirstSyncProblem>());
                Call("PresentRetainedFirstSyncProblem");
                Require(repair.Visibility == Visibility.Collapsed && Control<Button>("BootstrapRetainedProblemLink").Tag is null,
                    "Resolving the retained failure removes its repair action and stale run link");
            }
            finally { SetRunActive(false); SetField("_firstSyncProblemsByPair", oldProblems); }
        }

        private void ClickHomeReview() =>
            Call("HomeStateAction_Click", Field<Button>("_homeStateAction"),
                new RoutedEventArgs(ButtonBase.ClickEvent));

        private string ActionLabel() => Field<Button>("_homeStateAction").Content as string ?? string.Empty;

        private void Refresh()
        {
            Call("RefreshWorkspace");
            Call("RefreshFullApp");
            Drain();
        }

        private List<string> CaseTitles()
        {
            var titles = new List<string>();
            foreach (var row in Field<StackPanel>("_attentionList").Children.OfType<Border>())
                if (CaseTitleButton(row) is { Content: TextBlock text }) titles.Add(text.Text);
            return titles;
        }

        private void ClickCaseRow(string title)
        {
            foreach (var row in Field<StackPanel>("_attentionList").Children.OfType<Border>())
                if (CaseTitleButton(row) is { Content: TextBlock text } button &&
                    string.Equals(text.Text, title, StringComparison.Ordinal))
                {
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Drain();
                    return;
                }
            throw new InvalidDataException("The aggregate overview does not offer the '" + title + "' issue row.");
        }

        private static Button? CaseTitleButton(Border row) =>
            row.Child is DockPanel panel ? panel.Children.OfType<StackPanel>().FirstOrDefault()
                ?.Children.OfType<Button>().FirstOrDefault() : null;

        private void MakePresentationStale(DateTimeOffset stale)
        {
            SetField("_workspaceRunStarted", (DateTimeOffset?)stale);
            SetField("_workspacePhaseStarted", (DateTimeOffset?)stale);
            SetField("_workspaceProgressUpdated", (DateTimeOffset?)stale);
            SetField("_lastReportedWorkspaceStage", StaleDetail);
            SetField("_workspaceStage", StaleDetail);
            SetField("_workspacePhaseDetail", StaleDetail);
            SetField("_workspaceRunningPair", StalePairName);
            SetField("_workspaceRunningPairId", StalePairId);
        }

        private SyncBootstrapActionResult Applied() =>
            new(_first, new SyncRunResult(_first.Id, []), 1, 0, false);

        private PairRow Row(string pairId) =>
            _window.PairRows.Single(row => string.Equals(row.Id, pairId, StringComparison.Ordinal));

        private static SyncBootstrapReviewItem Item(string pairId) => Item(pairId, pairId + ".txt");

        private static SyncBootstrapReviewItem Item(string pairId, string relativePath) => new(pairId, relativePath,
            "Local only", "File", "TXT", "Upload", "Copy to cloud", 1, null, DateTimeOffset.UtcNow, null);

        private string Feedback() => Control<TextBlock>("BootstrapChoiceSummary").Text;

        private static Brush ResourceBrush(string key) => (Brush)Application.Current.Resources[key];

        private static bool SameBrush(Brush? actual, Brush? expected) =>
            actual is SolidColorBrush actualSolid && expected is SolidColorBrush expectedSolid
                ? actualSolid.Color == expectedSolid.Color
                : Equals(actual, expected);

        private T Control<T>(string name) => (T)_window.FindName(name);

        private T Field<T>(string name) => (T)typeof(MainWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_window)!;

        private void SetField(string name, object? value) => typeof(MainWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, value);

        private void SetRunActive(bool value) => typeof(MainWindow)
            .GetProperty("_runActive", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, value);

        private void Call(string name, params object?[] arguments) => typeof(MainWindow)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_window, arguments);

        private void PresentBatchProgress(SyncAuthorityBatchProgress progress, string trigger, bool recoveryOnly) =>
            // Like the real host, await the actual dispatched producer callback before reading its result.
            // This is not a delay or polling until an expected value appears.
            Pump((Task)typeof(MainWindow).GetMethod("PresentBatchProgressAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_window, [progress, trigger, recoveryOnly])!);

        private void PumpUntil(Func<bool> ready, string description)
        {
            var limit = DateTime.UtcNow.AddSeconds(15);
            while (!ready() && DateTime.UtcNow < limit) Drain();
            if (!ready()) throw new TimeoutException(description);
        }

        private static void Require(bool condition, string description)
        {
            if (!condition) throw new InvalidDataException(description);
            Console.WriteLine("PASS " + description);
        }
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(40);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) Drain();
        if (!task.IsCompleted) throw new TimeoutException("The controlled UI fixture did not complete its awaited operation within40 seconds.");
        task.GetAwaiter().GetResult();
    }
}
