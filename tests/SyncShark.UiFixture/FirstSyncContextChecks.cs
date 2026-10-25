using Massdex.SyncShark;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace SyncShark.UiFixture;

// Controlled inventories are external inputs. Navigation, generation rejection,
// row choices, confirmation and dispatch remain the actual MainWindow consumers.
internal static class FirstSyncContextChecks
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var dropbox = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-a", "context-dropbox-root", "context-dropbox-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
            DisplayName: "account-a", Email: "account-a@example.test").Normalize();
        Pump(new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(dropbox));
        var google = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey("account-b"), "account-b",
            "account-b@example.test", "account-b", [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
        Pump(new GoogleDriveConnectionStore(instance.StateAuthority).SaveAsync(google));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        var window = new MainWindow(instance.StateAuthority, theme) { Width = 1100, Height = 740 };
        var firstRoot = Path.Combine(root, "first");
        var secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        using var localLease = PilotDirectoryLease.Open(firstRoot);
        using var secondLease = PilotDirectoryLease.Open(secondRoot);
        var first = new SyncPair("first-context-a", "Same name", firstRoot,
            localLease.Identity.VolumeSerialNumber, localLease.Identity.FileId, "/a", "id:a",
            ConnectionId: dropbox.ConnectionId, InitialReviewApproved: false);
        var second = first with { Id = "first-context-b", ConnectionId = google.ConnectionId, DropboxRoot = "/b",
            DropboxFolderId = "folder-b", Provider = SyncProviders.GoogleDrive, LocalRoot = secondRoot,
            LocalVolumeSerialNumber = secondLease.Identity.VolumeSerialNumber, LocalRootFileId = secondLease.Identity.FileId };
        var pairSet = new SyncPairSet([first, second], first.Id);
        // Multi-provider pairs belong in retained authority, not the legacy
        // single-Dropbox migration input (which correctly rejects account mixing).
        Pump(window.AuthorityHost.LoadPairsAsync());
        foreach (var pair in pairSet.Pairs)
        {
            Pump(window.AuthorityHost.Authority.RegisterPairAsync(pair.Id, pair.PolicyRevision));
            Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(pair, pair.Id == first.Id));
        }
        var delayedA = new TaskCompletionSource<SyncBootstrapReviewPage>();
        var delayA = true;
        var generation = 1;
        var loads = 0;
        SyncBootstrapReviewPage Page(string pairId, int gen) => new(
            [Item(pairId)], 0, 1, 1, 1, 0, 0, false, true, "local-" + gen, "cloud-" + gen);
        MainWindow.BootstrapReviewPageLoaderForTest = (id, _, _, _, _, _, _) =>
        {
            loads++;
            return id == first.Id && delayA ? delayedA.Task : Task.FromResult(Page(id, generation));
        };
        MainWindow.BootstrapReviewLoaderForTest = (id, _, _, _, _) =>
            Task.FromResult<IReadOnlyList<SyncBootstrapReviewItem>>([Item(id)]);
        try
        {
            window.Show(); Drain();
            window.LoadPairSetForTest(pairSet);
            Call(window, "OpenFirstSyncReview", first.Id); Drain();
            Require(Control<Button>(window, "RefreshBootstrapButton").IsEnabled &&
                !Control<Button>(window, "ApproveBootstrapButton").IsEnabled &&
                !Control<Button>(window, "RefreshBootstrapButton").ToolTip.ToString()!.Contains("Select a folder pair"),
                "Selected unscanned pair can scan immediately; only confirmation awaits a complete review");
            Require(window.BootstrapRows.Count == 0 && !Control<Button>(window, "ApproveBootstrapButton").IsEnabled,
                "A loading review has no previous rows or enabled Start action");
            Call(window, "OpenFirstSyncReview", second.Id); Drain();
            Require(Control<ComboBox>(window, "BootstrapPairBox").SelectedValue as string == second.Id &&
                window.BootstrapRows.Single().PairId == second.Id,
                "Pair navigation atomically selects and presents exact B despite duplicate names");
            Require(Control<Button>(window, "RefreshBootstrapButton").IsEnabled &&
                !Control<Button>(window, "ApproveBootstrapButton").ToolTip.ToString()!.Contains("Select a folder pair"),
                "Pair navigation refreshes scan availability and the exact confirmation reason");
            delayedA.SetResult(Page(first.Id, 1)); Drain();
            Require(window.BootstrapRows.Single().PairId == second.Id &&
                Control<TextBlock>(window, "BootstrapSummary").Text.Contains("Same name"),
                "Delayed A cannot replace B's rows and summary");
            window.BootstrapRows.Single().SelectedAction = "Skip for now";
            Require(Control<TextBlock>(window, "BootstrapChoiceSummary").Text.Contains("1 skipped") &&
                Control<TextBlock>(window, "BootstrapChoiceSummary").Text.Contains("no files have changed"),
                "Draft choice reports exact count and no file effect");
            var before = loads;
            Call(window, "NavigateTo", "Dashboard", true);
            Call(window, "NavigateTo", "Bootstrap", true); Drain();
            Require(loads > before && window.BootstrapRows.Single().PairId == second.Id &&
                window.BootstrapRows.Single().SelectedAction == "Skip for now",
                "Returning refreshes the same selected pair and retains choices for the same generation");
            generation++;
            Pump(window.RefreshBootstrapReviewForTestAsync());
            Require(window.BootstrapRows.Single().SelectedAction == "Upload",
                "A new inventory generation does not reuse old choices");
            var starts = 0;
            MainWindow.BootstrapActionPlanForTest = (plan, _) =>
            {
                starts++;
                return Task.FromResult(new SyncBootstrapActionResult(second, new SyncRunResult(second.Id, []), 1, 0, false));
            };
            MainWindow.BootstrapApprovalConfirmationForTest = _ =>
            {
                delayA = false;
                Call(window, "OpenFirstSyncReview", first.Id);
                return true;
            };
            Call(window, "ApproveBootstrap_Click", Control<Button>(window, "ApproveBootstrapButton"), new RoutedEventArgs(ButtonBase.ClickEvent));
            Drain();
            Require(starts == 0, "A changed context while confirming cannot dispatch the old plan");
            Call(window, "OpenFirstSyncReview", second.Id); Drain();
            var endpointReasons = Field<Dictionary<string, string>>(window, "_endpointAttentionReasonByPair");
            endpointReasons[second.Id] = "Saved local folder no longer exists.";
            window.PairRows.Single(row => row.Id == second.Id).UpdateHealth(true, endpointReasons[second.Id]);
            Call(window, "RefreshWorkspace");
            Require(!window.TrayAttentionCases.Any(item => item.Pair.Id == second.Id && item.Title == "Review the First Sync") &&
                window.TrayAttentionCases.Any(item => item.Pair.Id == second.Id && item.IsProblem),
                "A broken pair offers its blocker instead of an inapplicable first-sync invitation");
            endpointReasons.Clear();
            Call(window, "OpenFirstSyncReview", second.Id); Drain();
            SyncBootstrapActionPlan? captured = null;
            var completion = new TaskCompletionSource<SyncBootstrapActionResult>();
            string? confirmation = null;
            MainWindow.BootstrapApprovalConfirmationForTest = text => { confirmation = text; return true; };
            MainWindow.BootstrapActionPlanForTest = (plan, _) => { captured = plan; return completion.Task; };
            Call(window, "ApproveBootstrap_Click", Control<Button>(window, "ApproveBootstrapButton"), new RoutedEventArgs(ButtonBase.ClickEvent));
            Drain();
            Require(confirmation is not null && confirmation.Contains("Folder Pair: Same name") &&
                confirmation.Contains("account-b") && !confirmation.Contains("account-a") &&
                confirmation.Contains("Right endpoint: Right — Google Drive") &&
                confirmation.Contains("Copy 1 new item(s) from Left — This computer to Right — Google Drive."),
                "The real confirmation identifies the exact account despite duplicate pair names and names the correct provider");
            Require(captured?.PairId == second.Id && captured.LocalScanGenerationId == "local-2" &&
                captured.DropboxScanGenerationId == "cloud-2" && captured.Choices.Single().RelativePath == second.Id + ".txt",
                "The real Start handler dispatches only the displayed B inventory and exact generations");
            Require(Field<TextBlock>(window, "_homeTitle").Text == "Starting First Sync" &&
                !System.Windows.Automation.AutomationProperties.GetName(Field<Button>(window, "_homePending")).Contains("No Pending"),
                "Home reports the pending first-sync command rather than idle/no work");
            typeof(MainWindow).GetField("_globalPendingQueueCount", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, 775);
            typeof(MainWindow).GetField("_workspaceScopePending", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, 775);
            Call(window, "RefreshWorkspace");
            Require(System.Windows.Automation.AutomationProperties.GetName(Field<Button>(window, "_homePending")) ==
                    "View current sync activity",
                "An active first sync links to its live activity even while retained pending operations exist");
            var liveRow = new ActivityRow(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                "download.txt", "Completed download", "Completed", PairId: second.Id,
                PairName: second.Name, OperationId: "fixture-first-sync-operation", ConnectionId: second.ConnectionId);
            window.ActivityRows.Add(liveRow);
            Call(window, "AddActivity", "download.txt", "Downloaded", "Completed", second);
            Require(window.ActivityRows.Count(row => row.Path == "download.txt") == 1,
                "first-sync summary does not duplicate its already displayed completed operation");
            completion.SetResult(new SyncBootstrapActionResult(second, new SyncRunResult(second.Id, []), 1, 0, false));
            // UI1: the first-sync approval is finished only when its synchronous single-flight guard has been
            // released, which is after its dashboard refresh. Every competing entry now shares that admission, so
            // the fixture waits for the shared busy state rather than only for the run flag.
            PumpUntil(() => !window.RunActiveForTest && !Field<bool>(window, "_firstSyncApprovalInFlight"));
            Require(Control<TextBlock>(window, "BootstrapChoiceSummary").Text.Contains("applied 1"),
                "Completed action feedback remains in the First Sync page: " +
                Control<TextBlock>(window, "BootstrapChoiceSummary").Text);
            Call(window, "RefreshWorkspace");
            Require(Control<TextBlock>(window, "PhaseText").Text.Contains("applied 1") &&
                !Control<TextBlock>(window, "PhaseText").Text.Contains("Checking the reviewed inventory"),
                "A dashboard refresh preserves completed first-sync feedback instead of the starting phase");
            // The real host has no credential for this isolated pair. Its scan must
            // reject without a provider effect and leave a truthful retryable page.
            Call(window, "RefreshBootstrap_Click", new Button(), new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => !window.RunActiveForTest && !Field<bool>(window, "_firstSyncApprovalInFlight"));
            Require(window.BootstrapRows.Count == 0 &&
                    !Control<Button>(window, "ApproveBootstrapButton").IsEnabled &&
                    Control<TextBlock>(window, "BootstrapSummary").Text.Contains("could not be completed") &&
                    !Control<TextBlock>(window, "BootstrapSummary").Text.Contains("Scanning both"),
                "Real rejected scan clears stale review and scanning copy and cannot enable Start");
            // Model an independently completed CLI mutation in the real retained store,
            // then exercise the exact running-app bridge callback, not a label helper.
            var retained = PumpResult(window.AuthorityHost.LoadPairsAsync());
            var retainedSecond = retained.Pairs.Single(pair => pair.Id == second.Id);
            Field<List<ComboBox>>(window, "_workspaceAccountPickers")[0].SelectedValue = second.ConnectionId;
            Drain();
            Call(window, "OpenFirstSyncReview", second.Id); Drain();
            Require(Control<Button>(window, "ApproveBootstrapButton").IsEnabled,
                "The selected review is actionable before the external approval readback");
            var approvedSecond = retainedSecond with { InitialReviewApproved = true,
                PolicyRevision = retainedSecond.PolicyRevision + 1 };
            Pump(window.AuthorityHost.Authority.RegisterPairAsync(approvedSecond.Id,
                approvedSecond.PolicyRevision));
            Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(approvedSecond, true));
            var unexpectedStarts = 0;
            MainWindow.BootstrapActionPlanForTest = (_, _) =>
            { unexpectedStarts++; throw new InvalidOperationException("Readback must not execute a sync plan"); };
            var accountScopeBeforeReadback = Field<string>(window, "_workspaceAccountId");
            Require(accountScopeBeforeReadback == second.ConnectionId,
                "The readback starts from an explicitly selected retained Google account");
            Pump(window.RefreshAfterCliCommandAsync());
            Require(Control<ComboBox>(window, "BootstrapPairBox").SelectedValue as string == second.Id &&
                window.BootstrapRows.Single().PairId == second.Id &&
                !Control<Button>(window, "ApproveBootstrapButton").IsEnabled &&
                System.Windows.Automation.AutomationProperties.GetName(Control<Button>(window, "ApproveBootstrapButton")) == "First Sync Complete",
                "Actual CLI completion callback refreshes the open exact-pair review and disables Start");
            Require(Field<string>(window, "_workspaceAccountId") == accountScopeBeforeReadback &&
                PumpResult(window.AuthorityHost.LoadPairsAsync()).Pairs.Single(pair => pair.Id == first.Id).InitialReviewApproved == false &&
                unexpectedStarts == 0,
                "Approval readback preserves account scope and other pair policy without replaying effects");
            MainWindow.BootstrapReviewPageLoaderForTest = (_, _, _, _, _, _, _) =>
                Task.FromException<SyncBootstrapReviewPage>(new IOException("Fixture retained-read failure"));
            Pump(window.RefreshAfterCliCommandAsync());
            Require(!Control<Button>(window, "ApproveBootstrapButton").IsEnabled &&
                Control<TextBlock>(window, "BootstrapSummary").Text.Contains("could not be loaded") &&
                PumpResult(window.AuthorityHost.LoadPairsAsync()).Pairs.Single(pair => pair.Id == second.Id).InitialReviewApproved == true &&
                unexpectedStarts == 0,
                "Failed post-command review readback fails closed without undoing or replaying approval");
            SyncBootstrapReviewItem[] updateRows =
            [
                new(first.Id, "local-update.txt", SyncBootstrapActions.LocalUpdatedGroup, "File", "TXT",
                    "Upload", "Update on remote", 2, 1, null, null),
                new(first.Id, "cloud-update.txt", SyncBootstrapActions.RemoteUpdatedGroup, "File", "TXT",
                    "Download", "Update on local", 1, 2, null, null)
            ];
            MainWindow.BootstrapReviewPageLoaderForTest = (_, _, _, _, _, _, _) =>
                Task.FromResult(new SyncBootstrapReviewPage(updateRows, 0, 2, 2, 0, 0, 0, false, true,
                    "baseline-local", "baseline-cloud", 1, 1));
            MainWindow.BootstrapReviewLoaderForTest = (_, _, _, _, _) =>
                Task.FromResult<IReadOnlyList<SyncBootstrapReviewItem>>(updateRows);
            Field<List<ComboBox>>(window, "_workspaceAccountPickers")[0].SelectedValue = first.ConnectionId;
            Call(window, "OpenFirstSyncReview", first.Id); Drain();
            Require(window.BootstrapRows.Count == 2 && window.BootstrapRows.All(row =>
                    row.Effect.StartsWith("Update on ", StringComparison.Ordinal) &&
                    row.AvailableActions.Count == 2 && row.AvailableActions.Contains("Skip for now")) &&
                    Control<TextBlock>(window, "BootstrapSummary").Text.Contains("Changed since sync: 2"),
                "Real review renders baseline updates as updates with direction-bound choices and complete counts");
            string? updateConfirmation = null;
            MainWindow.BootstrapApprovalConfirmationForTest = text => { updateConfirmation = text; return false; };
            Call(window, "ApproveBootstrap_Click", Control<Button>(window, "ApproveBootstrapButton"),
                new RoutedEventArgs(ButtonBase.ClickEvent));
            Drain();
            Require(updateConfirmation is not null &&
                    updateConfirmation.Contains("Update 1 unchanged item(s) on Right — Dropbox from Left — This computer.") &&
                    updateConfirmation.Contains("Update 1 unchanged item(s) on Left — This computer from Right — Dropbox.") &&
                    updateConfirmation.Contains("destination to still match the saved synchronized version") &&
                    unexpectedStarts == 0,
                "Actual confirmation distinguishes both update directions and cancellation sends no effect");
            Console.WriteLine("PASS first-sync context consumer checks; provider effects not used");
            return 0;
        }
        finally
        {
            MainWindow.BootstrapReviewPageLoaderForTest = null;
            MainWindow.BootstrapReviewLoaderForTest = null;
            MainWindow.BootstrapApprovalConfirmationForTest = null;
            MainWindow.BootstrapActionPlanForTest = null;
            window.RequestProductExit();
            MainWindow.SuppressLoadedStartupForTest = false;
            app.Shutdown();
        }
    }

    private static SyncBootstrapReviewItem Item(string id) => new(id, id + ".txt", "Local only", "File", "TXT",
        "Upload", "Copy to cloud", 1, null, DateTimeOffset.UtcNow, null);
    private static T Control<T>(MainWindow window, string name) => (T)window.FindName(name);
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Call(MainWindow window, string name, params object[] arguments) => typeof(MainWindow)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
    private static void Require(bool condition, string description)
    { if (!condition) throw new InvalidDataException(description); Console.WriteLine("PASS " + description); }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
    private static void Pump(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static T PumpResult<T>(Task<T> task) { Pump(task); return task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> ready)
    {
        var limit = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < limit) Drain();
        if (!ready()) throw new TimeoutException("First-sync fixture did not settle.");
    }
}
