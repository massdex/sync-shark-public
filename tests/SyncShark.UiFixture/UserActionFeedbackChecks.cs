using Massdex.SyncShark;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace SyncShark.UiFixture;

// UF4: focused regression checks for the two explicit on-page inline-feedback actions.
//
// Both REAL handlers are driven through their REAL controls inside one real MainWindow: the Conflicts
// "Compare…" context-menu item and the Recovery "Load revisions" button. The checks prove that the invoking
// page shows explicit loading, the real result and the honest refusal where the user is looking, that an
// empty prerequisite reaches no provider loader at all, and that the existing selection, in-flight and
// active-run guards still refuse. This isolated process has no connected account, so each loader seam is used
// exactly once where the production host would be called; the empty-prerequisite cases prove no loader is
// reached at all. No provider, credential, signing, installation or file effect is performed.
internal static class UserActionFeedbackChecks
{
    private const string Selector = "--user-action-feedback-checks";
    private const string PairId = "uf-feedback-pair";
    private const string RelativePath = "Docs/report.txt";
    private const string NoConflict = "Select one conflict to compare.";
    private const string ConflictLoading = "Loading both retained conflict versions for read-only comparison…";
    private const string ConflictRefusal = "Conflict comparison could not be opened: ";
    private const string NoPair = "Choose a folder pair above before loading revisions.";
    private const string NoPath = "Enter or select one pair-relative cloud path before loading revisions.";
    private const string RevisionLoading = "Loading exact cloud revisions for " + RelativePath + "…";
    private const string RevisionRefusal = "Cloud revision history could not be loaded: ";
    private const string RevisionKey = "uf-revision";
    private const string RevisionRefused = "The connected account is unavailable in the isolated fixture.";
    private const string LiveSyncCapabilityProfile = "ui397-live-sync-profile";
    // UI397_B: the three outcomes of the Folder Pairs Live Sync switch the page-level status must show.
    private const string LiveSyncWait = "Wait for the current sync to finish before changing Live sync.";
    private const string LiveSyncRefusal = "Live sync was not changed: ";
    private const string PendingWorkRefusal =
        "Finish or recover this pair's pending work before saving changes. Its current settings stay in effect until then; your draft is kept.";

    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var pair = new SyncPair(PairId, "Feedback Pair", Path.Combine(root, "local"), 1, 1, "/Root", "id:root",
            ConnectionId: "uf-connection");
        var conflicts = new List<(string PairId, string Path)>();
        var revisions = new List<(string PairId, string Path)>();
        var pendingConflicts = new List<TaskCompletionSource<SyncConflictContent>>();
        var pendingRevisions = new List<TaskCompletionSource<IReadOnlyList<SyncRecoveryCatalogItem>>>();
        var passed = 0;
        void Check(bool value, string label)
        {
            if (!value) throw new InvalidDataException(label);
            passed++;
            Console.WriteLine("PASS " + label);
        }
        var window = new MainWindow(instance.StateAuthority, theme)
        {
            Width = 1240, Height = 790, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
        };
        try
        {
            const BindingFlags transportFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            var transport = (System.Net.Http.HttpClient)typeof(MainWindow)
                .GetField("_httpClient", transportFlags)!.GetValue(window)!;
            var handler = (System.Net.Http.HttpClientHandler)typeof(System.Net.Http.HttpMessageInvoker)
                .GetField("_handler", transportFlags)!.GetValue(transport)!;
            Check(!handler.AllowAutoRedirect,
                "U01 actual GUI provider transport disables automatic redirects before startup or credential use");
            // The real Recovery page fetch is controlled so page navigation cannot start an uncontrolled load.
            MainWindow.RecoveryCatalogLoaderForTest = (_, _, _, _, _, _, _) =>
                Task.FromResult(new SyncRecoveryCatalogPage([], 0, false, null, false));
            // The real Live Sync change configures local monitors afterwards; that production call stays,
            // and only the monitor start/stop this isolated process cannot own is replaced.
            MainWindow.MonitorConfiguratorForTest = (_, _, _) =>
                Task.FromResult<IReadOnlyList<SyncAuthorityMonitorStartResult>>([]);
            MainWindow.MonitorStopperForTest = () => Task.CompletedTask;
            window.Show();
            Drain();
            window.LoadPairSetForTest(new SyncPairSet([pair], PairId));
            Drain();
            window.UpdateLayout();
            Drain();

            foreach (var (field, purpose) in new[] { ("_storageRefresh", "device free space"),
                         ("_activityRefresh", "recorded activity"), ("_addAccountButton", "authorize an account"),
                         ("_sharingRefresh", "selected accounts"), ("_sharingSearchAll", "remaining pages") })
            {
                var command = Field<Button>(window, field);
                Check(command.ToolTip is string tip && tip.Contains(purpose) &&
                      AutomationProperties.GetHelpText(command) == tip,
                    "Enabled " + field + " explains its actual purpose in hover and accessibility help.");
                var original = command.ToolTip;
                command.IsEnabled = false;
                Check(!Equals(command.ToolTip, original), field + " replaces purpose with its current unavailable reason.");
                command.IsEnabled = true;
                Check(Equals(command.ToolTip, original) && AutomationProperties.GetHelpText(command) == (string)original,
                    field + " restores purpose after its unavailable condition clears.");
            }
            var dynamicCommand = new Button();
            ActionAvailability.SetExplanation(dynamicCommand, "Review who can open the shared link.");
            ActionAvailability.Bind(dynamicCommand, () => "Sharing information is loading.");
            dynamicCommand.IsEnabled = false;
            ActionAvailability.SetExplanation(dynamicCommand, "Let others upload to the selected request folder.");
            Check(dynamicCommand.ToolTip as string == "Sharing information is loading." &&
                  AutomationProperties.GetHelpText(dynamicCommand) == "Sharing information is loading.",
                "A changed command purpose cannot replace its actual disabled reason.");
            dynamicCommand.IsEnabled = true;
            Check(dynamicCommand.ToolTip as string == "Let others upload to the selected request folder." &&
                  AutomationProperties.GetHelpText(dynamicCommand) == dynamicCommand.ToolTip as string,
                "The changed command purpose is restored when its disabled condition clears.");
            CheckTerminalReadbackGuard(window, pair, Check);
            CheckConflicts(window, pair, conflicts, pendingConflicts, Check);
            CheckRecovery(window, pair, revisions, pendingRevisions, Check);
            CheckPairLiveSync(root, window, Check);
            CheckConflictAvailability(window, Check);
            CheckDisabledControlFamilies(window, Check);
            CheckPairRetirementCommit(root, window, Check);
        }
        finally
        {
            MainWindow.ConflictContentLoaderForTest = null;
            MainWindow.ConflictPageCheckpointForTest = null;
            MainWindow.ConflictSelectionCheckpointForTest = null;
            MainWindow.RecommendedRecoveryFolderForTest = null;
            MainWindow.RevisionCatalogLoaderForTest = null;
            MainWindow.RecoveryCatalogLoaderForTest = null;
            MainWindow.MonitorConfiguratorForTest = null;
            MainWindow.MonitorStopperForTest = null;
            MainWindow.PairRetirementConfirmationForTest = null;
            MainWindow.PairRetirementFollowUpForTest = null;
            foreach (var source in pendingConflicts) source.TrySetException(new InvalidOperationException("fixture cleanup"));
            foreach (var source in pendingRevisions) source.TrySetException(new InvalidOperationException("fixture cleanup"));
            window.RequestProductExit();
            MainWindow.SuppressLoadedStartupForTest = false;
            app.Shutdown();
        }
        Console.WriteLine(passed + " " + Selector +
            " checks passed: real action handlers and conflict read/capture lifetimes, with visible feedback, " +
            "no loader on an empty prerequisite and no provider, credential, signing, installation or file effect.");
        return 0;
    }

    // U07: the real menu/handler and host retire a real isolated retained pair.
    // Only confirmation INPUT and a fault after the committed result are injected;
    // no retirement decision or database transition is substituted.
    private static void CheckPairRetirementCommit(string root, MainWindow window, Action<bool, string> check)
    {
        var host = window.AuthorityHost;
        var authority = host.Authority;
        var feedback = Field<TextBlock>(window, "PairPageActionFeedback");
        var confirmations = 0;
        MainWindow.PairRetirementConfirmationForTest = _ => { confirmations++; return true; };
        foreach (var (stage, description) in new[]
                 { ("explorer-unregister", "unregistering the retired pair from Explorer"),
                   ("recovery-choices", "refreshing Recovery choices"),
                   ("local-monitors", "refreshing local monitors"),
                   ("explorer-status", "refreshing Explorer status") })
        {
            using var left = PilotDirectoryLease.Open(FixtureLocalFolder(root, "retire-" + stage + "-left"));
            using var right = PilotDirectoryLease.Open(FixtureLocalFolder(root, "retire-" + stage + "-right"));
            var pair = SyncEndpointPairDraft.Create(new SyncPairEndpoints(
                SyncEndpoint.Local(left.Path, SyncLocalEndpointIdentity.From(left.Identity.VolumeSerialNumber, left.Identity.FileId)),
                SyncEndpoint.Local(right.Path, SyncLocalEndpointIdentity.From(right.Identity.VolumeSerialNumber, right.Identity.FileId)),
                SyncMode.TwoWay), "Retirement " + stage) with { Enabled = false };
            Pump(authority.RegisterPairAsync(pair.Id, pair.PolicyRevision, SyncAuthorityPairConfiguration.FromPair(pair)));
            Pump(authority.RetainPairDefinitionAsync(pair, true));
            Pump(authority.ImportLegacyHistoryAsync(new SyncHistory(
                [new(DateTimeOffset.UtcNow, pair.Id, pair.Name, "local.txt", SyncDecisionKind.Uploaded, "U07 retained history")])));
            var historyBefore = Pump(authority.LoadActivityProjectionPageAsync(pairId: pair.Id));
            check(historyBefore.Count > 0, "U07 " + stage + " starts with real retained Activity history");
            var recovery = Path.Combine(Directory.CreateDirectory(Path.Combine(left.Path, "Recovery")).FullName, "retained.txt");
            File.WriteAllText(recovery, "retained predecessor");
            var statesBefore = Pump(authority.LoadAllPairDefinitionsAsync()).Where(item => item.Id != pair.Id)
                .Select(item => (item.Id, State: Pump(authority.LoadPairStateAsync(item.Id)))).ToArray();
            var reached = new List<string>();
            MainWindow.PairRetirementFollowUpForTest = current =>
            {
                reached.Add(current);
                if (current == stage) throw new IOException("synthetic-retirement-fault-" + stage);
                return Task.CompletedTask;
            };
            window.LoadPairSetForTest(Pump(host.LoadPairsAsync()));
            Field<DataGrid>(window, "PairList").SelectedItem = window.PairRows.Single(item => item.Id == pair.Id);
            var confirmationBefore = confirmations;
            Field<MenuItem>(window, "PairDeleteMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            PumpUntil(() => feedback.Text.Contains("synthetic-retirement-fault-" + stage, StringComparison.Ordinal), "U07 committed " + stage);
            check(Pump(authority.LoadPairStateAsync(pair.Id)) == "retired" &&
                  Pump(host.LoadPairsAsync()).Pairs.All(item => item.Id != pair.Id) && window.PairRows.All(item => item.Id != pair.Id),
                "U07 " + stage + " reports the actual committed state and removes its stale row");
            check(feedback.Text.Contains("was deleted as a pair", StringComparison.Ordinal) &&
                  feedback.Text.Contains(description, StringComparison.Ordinal) &&
                  !feedback.Text.Contains("was not deleted", StringComparison.Ordinal) &&
                  feedback.Visibility == Visibility.Visible && Field<DataGrid>(window, "PairList").IsEnabled,
                "U07 " + stage + " visibly names committed retirement and its precise pending follow-up");
            check(confirmations == confirmationBefore + 1 && reached.Count(item => item == stage) == 1,
                "U07 " + stage + " confirms once and never replays its committed retirement");
            check(File.ReadAllText(Path.Combine(left.Path, "local.txt")) == "UI397 fixture folder" &&
                  File.ReadAllText(Path.Combine(right.Path, "local.txt")) == "UI397 fixture folder" &&
                  File.ReadAllText(recovery) == "retained predecessor",
                "U07 " + stage + " retains exact endpoint and Recovery bytes");
            // Categories are deserialized arrays: record equality compares their
            // references, not their values. Compare every serialized projection field,
            // including those array contents, rather than dropping any history evidence.
            check(JsonSerializer.Serialize(historyBefore) == JsonSerializer.Serialize(
                    Pump(authority.LoadActivityProjectionPageAsync(pairId: pair.Id))),
                "U07 " + stage + " retains every exact Activity history field and category");
            check(statesBefore.All(item => Pump(authority.LoadPairStateAsync(item.Id)) == item.State),
                "U07 " + stage + " leaves every other retained pair unchanged");
        }
        MainWindow.PairRetirementFollowUpForTest = null;
        using var refusedLeft = PilotDirectoryLease.Open(FixtureLocalFolder(root, "retire-refused-left"));
        using var refusedRight = PilotDirectoryLease.Open(FixtureLocalFolder(root, "retire-refused-right"));
        var refused = SyncEndpointPairDraft.Create(new SyncPairEndpoints(
            SyncEndpoint.Local(refusedLeft.Path, SyncLocalEndpointIdentity.From(refusedLeft.Identity.VolumeSerialNumber, refusedLeft.Identity.FileId)),
            SyncEndpoint.Local(refusedRight.Path, SyncLocalEndpointIdentity.From(refusedRight.Identity.VolumeSerialNumber, refusedRight.Identity.FileId)),
            SyncMode.TwoWay), "Retirement refused") with { Enabled = false };
        Pump(authority.RegisterPairAsync(refused.Id, refused.PolicyRevision, SyncAuthorityPairConfiguration.FromPair(refused)));
        Pump(authority.RetainPairDefinitionAsync(refused, true));
        Pump(SeedPendingPairWorkAsync(authority, refused));
        var followUps = 0;
        MainWindow.PairRetirementFollowUpForTest = _ => { followUps++; return Task.CompletedTask; };
        window.LoadPairSetForTest(Pump(host.LoadPairsAsync()));
        Field<DataGrid>(window, "PairList").SelectedItem = window.PairRows.Single(item => item.Id == refused.Id);
        Field<MenuItem>(window, "PairDeleteMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        PumpUntil(() => feedback.Text.StartsWith("The folder pair was not deleted:", StringComparison.Ordinal), "U07 pre-commit refusal");
        check(Pump(authority.LoadPairStateAsync(refused.Id)) != "retired" &&
              window.PairRows.Any(item => item.Id == refused.Id) && followUps == 0,
            "U07 genuine pending-work refusal leaves the pair intact and performs no post-commit follow-up");
        MainWindow.PairRetirementConfirmationForTest = null;
        MainWindow.PairRetirementFollowUpForTest = null;
    }

    private static void CheckDisabledControlFamilies(MainWindow window, Action<bool, string> check)
    {
        Call(window, "NavigateTo", "Pairs", true);
        Call(window, "ExpandPairRow", window.PairRows.First());
        window.UpdateLayout();
        Drain();
        var names = new[] { "PairEnabledBox", "PairDeletionBox", "PairPriorityChoice",
            "RecoveryRetentionBox", "WholeRootSelectionBox", "PairStorageBox" };
        Set(window, "_uiRunActive", true);
        Call(window, "SetRunControls");
        foreach (var name in names)
        {
            var control = Field<Control>(window, name);
            check(!control.IsEnabled && ToolTipService.GetShowOnDisabled(control) &&
                  AutomationProperties.GetHelpText(control).Contains("scan or sync is running"),
                "AUD14 actual " + name + " explains the run lock through disabled hover and accessibility");
        }
        Set(window, "_uiRunActive", false);
        Call(window, "SetRunControls");
        var storage = Field<ComboBox>(window, "PairStorageBox");
        check(!storage.IsEnabled && AutomationProperties.GetHelpText(storage).Contains("Both folders are local"),
            "AUD14 local/local storage explains the endpoint limitation after the run lock clears");
        var gate = new TaskCompletionSource<DropboxFolderEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        MainWindow.RecommendedRecoveryFolderForTest = _ => { requests++; return gate.Task; };
        var prepare = window.UseRecommendedRecoveryForTestAsync();
        check(requests == 1 && !prepare.IsCompleted && !Field<UIElement>(window, "PairEditorPanel").IsEnabled,
            "AUD14 real Recovery preparation enters one pending request and locks the pair editor");
        foreach (var name in names)
        {
            // WPF only presents the selected TabItem's content in its visual tree. Exercise
            // the real inherited lock with each control's own tab actually selected.
            Field<TabControl>(window, "PairEditorTabs").SelectedIndex = name == "RecoveryRetentionBox" ? 4
                : name == "WholeRootSelectionBox" ? 0 : 2;
            window.UpdateLayout();
            Drain();
            var control = Field<Control>(window, name);
            ActionAvailability.Refresh(control);
            check(!control.IsEnabled && AutomationProperties.GetHelpText(control).Contains("Recovery folder"),
                "AUD14 inherited pair-panel lock takes precedence over " + name + "'s own prerequisite" +
                " (enabled=" + control.IsEnabled + ", help=" + AutomationProperties.GetHelpText(control) + ")");
        }
        gate.SetException(new IOException("fixture recovery preparation refused"));
        Pump(prepare);
        MainWindow.RecommendedRecoveryFolderForTest = null;
        check(Field<UIElement>(window, "PairEditorPanel").IsEnabled,
            "AUD14 real Recovery preparation failure releases the inherited editor lock");
    }

    private static void CheckConflictAvailability(MainWindow window, Action<bool, string> check)
    {
        Set(window, "_uiRunActive", false);
        Call(window, "SetRunControls");
        window.UnsavedNavigationChoiceForTest = () =>
            throw new InvalidDataException("AUD16 unexpected unsaved prompt after a saved row action");
        Call(window, "NavigateTo", "Conflicts", true);
        window.UnsavedNavigationChoiceForTest = null;
        Drain();
        var more = Field<Button>(window, "LoadMoreConflictsButton");
        var select = Field<Button>(window, "SelectAllMatchingConflictsButton");
        var checkpoints = new List<TaskCompletionSource>();
        MainWindow.ConflictPageCheckpointForTest = () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            checkpoints.Add(gate);
            return gate.Task;
        };
        Task Load() => (Task)Call(window, "LoadConflictPageAsync", true, null)!;
        var older = Load();
        check(!more.IsEnabled && AutomationProperties.GetHelpText(more).Contains("still loading"),
            "AUD14 conflict pagination explains the actual pending query, not a completed page");
        var current = Load();
        checkpoints[1].SetResult();
        Pump(current);
        check(!Field<bool>(window, "_loadingConflicts") &&
              !AutomationProperties.GetHelpText(more).Contains("still loading"),
            "AUD14 completing the current query clears its busy reason even while a superseded query remains");
        checkpoints[0].SetException(new IOException("superseded query failure"));
        Pump(older);
        check(Field<string?>(window, "_conflictLoadFailure") is null,
            "AUD14 a late superseded query cannot replace the current result with an error");
        var failed = Load();
        checkpoints[2].SetException(new IOException("fixture read unavailable"));
        Pump(failed);
        check(!Field<bool>(window, "_loadingConflicts") && !more.IsEnabled &&
              AutomationProperties.GetHelpText(more).Contains("fixture read unavailable") &&
              Field<TextBlock>(window, "ConflictActionFeedback").Text.Contains("fixture read unavailable"),
            "AUD14 conflict query failure releases busy state and shows its exact retryable cause on the page and disabled control");
        MainWindow.ConflictPageCheckpointForTest = null;
        Pump(Load());
        check(Field<string?>(window, "_conflictLoadFailure") is null &&
              !Field<TextBlock>(window, "ConflictActionFeedback").Text.Contains("fixture read unavailable"),
            "AUD14 a real subsequent host query replaces the old query-failure state");

        var captures = new List<TaskCompletionSource>();
        MainWindow.ConflictSelectionCheckpointForTest = () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            captures.Add(gate);
            return gate.Task;
        };
        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(!select.IsEnabled && AutomationProperties.GetHelpText(select).Contains("still being selected"),
            "AUD14 select-all explains a pending conflict snapshot rather than claiming a sync is running");
        Call(window, "SetRunControls");
        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(!select.IsEnabled && captures.Count == 1,
            "AUD14 a control refresh and repeated activation cannot reenable or duplicate an in-flight selection");
        Set(window, "_uiRunActive", true);
        Call(window, "SetRunControls");
        captures[0].SetResult();
        PumpUntil(() => !Field<bool>(window, "_capturingConflictSelection"), "capture completing during sync");
        check(!select.IsEnabled && AutomationProperties.GetHelpText(select).Contains("scan or sync is running"),
            "AUD14 finishing a capture cannot enable the action during sync, including after terminal feedback disposal");
        Set(window, "_uiRunActive", false);
        Call(window, "SetRunControls");
        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Field<TextBox>(window, "ConflictSearchBox").Text = "new-view";
        captures[1].SetResult();
        PumpUntil(() => !Field<bool>(window, "_capturingConflictSelection"), "superseded capture");
        check(ConsoleText(window).Contains("No old selection was applied") &&
              Field<IReadOnlyList<SyncConflictSelection>>(window, "_matchingConflictSelection").Count == 0,
            "AUD14 a changed filter cannot receive the old all-matching snapshot");
        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        captures[2].SetResult();
        PumpUntil(() => !Field<bool>(window, "_capturingConflictSelection"), "current capture");
        check(select.IsEnabled && ConsoleText(window).Contains("No open conflicts match the current filters"),
            "AUD14 the current real host capture reports its empty result and restores the action");
        MainWindow.ConflictSelectionCheckpointForTest = null;
    }

    private static void CheckConflicts(MainWindow window, SyncPair pair,
        List<(string PairId, string Path)> conflicts, List<TaskCompletionSource<SyncConflictContent>> pending,
        Action<bool, string> check)
    {
        Call(window, "NavigateTo", "Conflicts", true);
        Drain();
        window.UpdateLayout();
        Drain();
        var feedback = Field<TextBlock>(window, "ConflictActionFeedback");
        var page = Field<Border>(window, "ConflictsPage");
        var compare = CompareItem(window);
        check(page.Visibility == Visibility.Visible && compare is not null,
            "UF4 conflicts: the real Conflicts page is visible and its real Compare context-menu item is reachable (" +
            "page=" + page.Visibility + ", pageVisible=" + page.IsVisible + ", compare=" + (compare is null ? "missing" : "found") +
            ", items=[" + string.Join(" | ", CompareItems(window).Select(Header)) + "])");

        // (1) No conflict selected. The existing refusal must be readable on the invoking page and must not
        // reach any conflict-content loader.
        Field<DataGrid>(window, "ConflictGrid").SelectedItem = null;
        Field<DataGrid>(window, "ResultGrid").SelectedItem = null;
        var consoleBefore = ConsoleText(window).Length;
        compare!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Drain();
        check(feedback.Text == NoConflict && feedback.Visibility == Visibility.Visible && feedback.IsVisible &&
              AutomationProperties.GetName(feedback) == "Action status",
            "UF4 conflicts: an empty selection shows the existing refusal on the visible invoking page (" + Describe(feedback) + ")");
        check(conflicts.Count == 0, "UF4 conflicts: an empty selection reaches no conflict-content loader at all");
        check(ConsoleText(window).Length > consoleBefore && ConsoleText(window).Contains(NoConflict, StringComparison.Ordinal),
            "UF4 conflicts: the existing console recording still happens behind the on-page feedback");

        // (2) A selected row that is not an open conflict keeps the same refusal and loads nothing.
        var resolved = new DecisionRow(PairId, pair.Name, "notes.txt", "Completed", "No conflict remains.");
        window.ConflictRows.Add(resolved);
        Field<DataGrid>(window, "ConflictGrid").SelectedItem = resolved;
        Drain();
        compare.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Drain();
        check(feedback.Text == NoConflict && feedback.Visibility == Visibility.Visible && feedback.IsVisible &&
              conflicts.Count == 0,
            "UF4 conflicts: a selected non-conflict row is refused visibly and still loads nothing");
        window.ConflictRows.Remove(resolved);
        Drain();

        // (3) A selected conflict: explicit loading while the real handler waits, then the honest refusal.
        var conflict = new DecisionRow(PairId, pair.Name, RelativePath, SyncDecisionKind.Conflict.ToString(),
            "Both versions changed.");
        window.ConflictRows.Add(conflict);
        Field<DataGrid>(window, "ConflictGrid").SelectedItem = conflict;
        Drain();
        MainWindow.ConflictContentLoaderForTest = (pairId, path) =>
        {
            conflicts.Add((pairId, path));
            var source = new TaskCompletionSource<SyncConflictContent>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(source);
            return source.Task;
        };
        compare.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        check(conflicts.Count == 1 && conflicts[0] == (PairId, RelativePath),
            "UF4 conflicts: a selected conflict asks the real handler for the exact pair and path exactly once");
        check(feedback.Text == ConflictLoading && feedback.Visibility == Visibility.Visible && feedback.IsVisible,
            "UF4 conflicts: the real handler shows explicit loading on the invoking page while it waits (" + Describe(feedback) + ")");
        check(ConsoleText(window).Contains(ConflictLoading, StringComparison.Ordinal),
            "UF4 conflicts: the loading state keeps the existing console recording");
        pending[^1].SetException(new InvalidOperationException("The selected conflict is no longer open."));
        PumpUntil(() => feedback.Text != ConflictLoading, "the conflict refusal to be shown");
        check(feedback.Text == ConflictRefusal + "The selected conflict is no longer open." &&
              feedback.Visibility == Visibility.Visible && feedback.IsVisible &&
              AutomationProperties.GetName(feedback) == "Action failed",
            "UF4 conflicts: an honest refused load is a visible, error-marked failure on the same page (" + Describe(feedback) + ")");

        // (4) With no substitution at all, the REAL production host refusal is what reaches the page, and no
        // comparison dialog is opened for a refusal.
        MainWindow.ConflictContentLoaderForTest = null;
        var dialogs = Application.Current.Windows.OfType<ConflictComparisonWindow>().Count();
        compare.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        PumpUntil(() => feedback.Text.StartsWith(ConflictRefusal, StringComparison.Ordinal) &&
                        !feedback.Text.Contains("no longer open", StringComparison.Ordinal),
            "the real host refusal to be shown");
        check(feedback.Visibility == Visibility.Visible && feedback.IsVisible &&
              AutomationProperties.GetName(feedback) == "Action failed" &&
              Application.Current.Windows.OfType<ConflictComparisonWindow>().Count() == dialogs,
            "UF4 conflicts: the real Host refusal is surfaced visibly with no comparison dialog (" + Describe(feedback) + ")");
    }

    private static void CheckRecovery(MainWindow window, SyncPair pair,
        List<(string PairId, string Path)> revisions,
        List<TaskCompletionSource<IReadOnlyList<SyncRecoveryCatalogItem>>> pending, Action<bool, string> check)
    {
        Call(window, "NavigateTo", "Recovery", true);
        Drain();
        PumpUntil(() => !Field<bool>(window, "_loadingRecovery"), "the initial recovery page to settle");
        window.UpdateLayout();
        Drain();
        var summary = Field<TextBlock>(window, "RecoverySummary");
        var pathBox = Field<TextBox>(window, "RecoveryPathBox");
        var recoveryPage = Field<Border>(window, "RecoveryPage");
        var load = LoadRevisionsButton(window);
        check(recoveryPage.Visibility == Visibility.Visible && load is not null,
            "UF4 recovery: the real Recovery page is visible and its real Load Revisions button is reachable (" +
            "page=" + recoveryPage.Visibility + ", pageVisible=" + recoveryPage.IsVisible + ", button=" +
            (load is null ? "missing" : "found") + ", pageButtons=[" +
            string.Join(" | ", Descendants(recoveryPage).OfType<Button>().Select(ButtonText)) + "])");

        // (1) A selected pair with no path: a visible local actionable explanation and no provider loader.
        pathBox.Text = string.Empty;
        SettleRecovery(window);
        load!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(summary.Text == NoPath && summary.Visibility == Visibility.Visible && summary.IsVisible,
            "UF4 recovery: an empty path shows the local actionable explanation on the invoking page (" + summary.Text + ")");
        check(revisions.Count == 0, "UF4 recovery: an empty path reaches no revision loader at all");
        check(ConsoleText(window).Contains(NoPath, StringComparison.Ordinal),
            "UF4 recovery: the empty-path explanation keeps the existing console recording");

        // (2) A valid request: explicit loading, then the real returned result, and the in-flight guard holds.
        pathBox.Text = RelativePath;
        SettleRecovery(window);
        check(RecoveryQuiet(window) && (string?)Call(window, "CurrentRecoveryPairId") == PairId,
            "UF4 recovery: the page is quiet and bound to the seeded pair before the valid request (" +
            "loading=" + Field<bool>(window, "_loadingRecovery") + ", queued=" + Field<bool>(window, "_recoveryReloadPending") +
            ", pair=" + ((string?)Call(window, "CurrentRecoveryPairId") ?? "<none>") + ")");
        MainWindow.RevisionCatalogLoaderForTest = (pairId, path) =>
        {
            revisions.Add((pairId, path));
            var source = new TaskCompletionSource<IReadOnlyList<SyncRecoveryCatalogItem>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(source);
            return source.Task;
        };
        var versionAtClick = Field<int>(window, "_recoveryQueryVersion");
        var pairAtClick = (string?)Call(window, "CurrentRecoveryPairId");
        var boxAtClick = DescribeBox(window);
        Click(window, load!);
        check(revisions.Count == 1 && revisions[0] == (PairId, RelativePath),
            "UF4 recovery: a valid request asks the real provider path for the exact pair and normalized path once");
        check(summary.Text == RevisionLoading && summary.Visibility == Visibility.Visible && summary.IsVisible,
            "UF4 recovery: the valid request shows explicit loading locally while it waits (" + Describe(summary) + ")");
        // The existing in-flight guard is exercised without running the dispatcher, so nothing else can change
        // the pending request's pair or query generation before the controlled result is supplied.
        load!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(revisions.Count == 1 && summary.Text == RevisionLoading,
            "UF4 recovery: the existing in-flight guard refuses a second request with no extra loader call and no replaced feedback");
        pending[^1].SetResult([RevisionItem(pair)]);
        var presented = ObserveRecoveryLoad(window);
        check(presented.Any(item => item.Summary ==
                    "Loaded 1 exact revision(s) for " + RelativePath + ". Select one to preview a restore." &&
                item.Rows == 1 && item.Keys.Contains(RevisionKey)),
            "UF4 recovery: the real returned result is reported locally with the returned revision applied to " +
            "the real bound rows (" + Describe(presented) + "; versionAtClick=" + versionAtClick +
            ", versionNow=" + Field<int>(window, "_recoveryQueryVersion") + ", pairAtClick=" +
            (pairAtClick ?? "<none>") + ", pairNow=" + ((string?)Call(window, "CurrentRecoveryPairId") ?? "<none>") +
            ", boxAtClick=" + boxAtClick + ", boxNow=" + DescribeBox(window) + ")");

        // (3) A refused provider load is stated locally.
        pathBox.Text = RelativePath;
        SettleRecovery(window);
        Click(window, load!);
        check(revisions.Count == 2 && summary.Text == RevisionLoading,
            "UF4 recovery: the second valid request again shows loading and asks once");
        pending[^1].SetException(new InvalidOperationException(RevisionRefused));
        var refused = ObserveRecoveryLoad(window);
        check(refused.Any(item => item.Summary == RevisionRefusal + RevisionRefused) &&
              summary.Visibility == Visibility.Visible,
            "UF4 recovery: a refused provider load is stated locally and visibly (" + Describe(refused) + ")");

        // (4) The existing active-run guard refuses silently, without another loader call or a replaced result.
        Set(window, "_uiRunActive", true);
        Drain();
        var retained = summary.Text;
        pathBox.Text = RelativePath;
        load!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(revisions.Count == 2 && summary.Text == retained,
            "UF4 recovery: the existing active-run guard refuses without another loader call or a replaced result");
        Set(window, "_uiRunActive", false);
        Drain();

        // (5) No pair at all: the pair prerequisite is stated locally and still loads nothing. This case
        // mutates the displayed pair set, so it runs last.
        Field<DataGrid>(window, "RecoveryGrid").SelectedItem = null;
        window.RecoveryRows.Clear();
        window.LoadPairSetForTest(new SyncPairSet([], null));
        pathBox.Text = string.Empty;
        var loadsBefore = revisions.Count;
        SettleRecovery(window);
        load!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(summary.Text == NoPair && summary.Visibility == Visibility.Visible && summary.IsVisible &&
              revisions.Count == loadsBefore,
            "UF4 recovery: a missing pair keeps its own actionable explanation and loads nothing (" + summary.Text + ")");
    }

    // UI397_B: the Folder Pairs page's own Live Sync switch, driven through the real Click handler on the real
    // pair row inside the real pair list. The page-level status target beside that list is read directly, so
    // every prescribed outcome is asserted where the user is looking while the row's own details and the pair
    // editor are both collapsed. The two refusal cases additionally prove the pair's own Enabled state is
    // unchanged, and the third case proves the previous outcome is replaced rather than left beside the new
    // one. Both pairs are retained through the window's own real authority; nothing here performs a provider,
    // credential, signing, installation or file effect.
    private static void CheckPairLiveSync(string root, MainWindow window, Action<bool, string> check)
    {
        var authority = window.AuthorityHost.Authority;
        using var pausedLeft = PilotDirectoryLease.Open(FixtureLocalFolder(root, "live-sync-left"));
        using var pausedRight = PilotDirectoryLease.Open(FixtureLocalFolder(root, "live-sync-right"));
        using var pendingLeft = PilotDirectoryLease.Open(FixtureLocalFolder(root, "live-sync-pending-left"));
        using var pendingRight = PilotDirectoryLease.Open(FixtureLocalFolder(root, "live-sync-pending-right"));
        var pausedPair = SyncEndpointPairDraft.Create(new SyncPairEndpoints(
            SyncEndpoint.Local(pausedLeft.Path, SyncLocalEndpointIdentity.From(
                pausedLeft.Identity.VolumeSerialNumber, pausedLeft.Identity.FileId)),
            SyncEndpoint.Local(pausedRight.Path, SyncLocalEndpointIdentity.From(
                pausedRight.Identity.VolumeSerialNumber, pausedRight.Identity.FileId)),
            SyncMode.TwoWay), "UI397 Paused Pair") with { Enabled = false };
        var pendingPair = SyncEndpointPairDraft.Create(new SyncPairEndpoints(
            SyncEndpoint.Local(pendingLeft.Path, SyncLocalEndpointIdentity.From(
                pendingLeft.Identity.VolumeSerialNumber, pendingLeft.Identity.FileId)),
            SyncEndpoint.Local(pendingRight.Path, SyncLocalEndpointIdentity.From(
                pendingRight.Identity.VolumeSerialNumber, pendingRight.Identity.FileId)),
            SyncMode.TwoWay), "UI397 Pending Pair");
        // Keep the host's installation identity and retain the exact capability profile referenced
        // by the pending operation. An already initialized host still needs this fixture profile.
        var installationId = Pump(authority.LoadInstallationIdAsync(CancellationToken.None)) ??
            "ui397-live-sync-installation";
        Pump(authority.InitializeAsync(installationId, LiveSyncCapabilityProfile, "fixture", "{}"));
        Pump(authority.RegisterPairAsync(pausedPair.Id, pausedPair.PolicyRevision));
        Pump(authority.RetainPairDefinitionAsync(pausedPair, true));
        Pump(authority.RegisterPairAsync(pendingPair.Id, pendingPair.PolicyRevision));
        Pump(authority.RetainPairDefinitionAsync(pendingPair, false));
        Pump(SeedPendingPairWorkAsync(authority, pendingPair));
        window.LoadPairSetForTest(new SyncPairSet([pausedPair, pendingPair], pausedPair.Id));
        Drain();
        window.UpdateLayout();
        Drain();
        Call(window, "NavigateTo", "Pairs", true);
        Drain();
        window.UpdateLayout();
        Drain();
        var page = Field<Border>(window, "PairsPage");
        var feedback = Field<TextBlock>(window, "PairPageActionFeedback");
        var editorHost = Field<ContentControl>(window, "NewPairEditorHost");
        check(page.Visibility == Visibility.Visible && feedback.Visibility == Visibility.Collapsed,
            "UI397_B: the Folder Pairs page carries its own page-level status target, collapsed before any " +
            "action (page=" + page.Visibility + ", target=" + Describe(feedback) + ")");

        var toggle = LiveSyncSwitch(window, pausedPair.Id);
        check(PairRowContainer(window, pausedPair.Id).DetailsVisibility == Visibility.Collapsed &&
              editorHost.Visibility == Visibility.Collapsed &&
              AutomationProperties.GetName(toggle) == "Toggle live sync for this pair",
            "UI397_B: the real pair row exposes its Live Sync switch while the pair's details and the pair " +
            "editor are both collapsed (details=" +
            PairRowContainer(window, pausedPair.Id).DetailsVisibility + ", editor=" + editorHost.Visibility + ")");

        // (1) The existing active-run guard: refused visibly, and the pair's own state is unchanged.
        Set(window, "_uiRunActive", true);
        Drain();
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Drain();
        var afterActiveRun = window.PairRows.Single(item => item.Id == pausedPair.Id);
        check(feedback.Text == LiveSyncWait && feedback.Visibility == Visibility.Visible && feedback.IsVisible &&
              AutomationProperties.GetName(feedback) == "Action failed" && !afterActiveRun.Enabled &&
              toggle.IsChecked == false,
            "UI397_B: an active run refuses the change visibly on the Folder Pairs page and leaves the pair " +
            "unchanged (" + Describe(feedback) + ", pairEnabled=" + afterActiveRun.Enabled + ")");
        Set(window, "_uiRunActive", false);
        Drain();

        // (2) A mutable pair: the real retained change reports the pair and its resulting state on the page.
        toggle = LiveSyncSwitch(window, pausedPair.Id);
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(() => window.PairRows.Any(item => item.Id == pausedPair.Id && item.Enabled),
            "the real Live Sync change to be retained");
        // Persistence is an intermediate boundary: monitor configuration is still awaited by
        // the real handler. Assert its terminal feedback only after the shared action completes.
        PumpUntil(() => AutomationProperties.GetItemStatus(toggle) != "In progress",
            "the real Live Sync action including monitor configuration to finish");
        var enabledRow = window.PairRows.Single(item => item.Id == pausedPair.Id);
        var success = "Live sync is on for " + enabledRow.Name + ".";
        check(feedback.Text == success && feedback.Visibility == Visibility.Visible && feedback.IsVisible &&
              AutomationProperties.GetName(feedback) == "Action status" && enabledRow.Enabled &&
              PairRowContainer(window, pausedPair.Id).DetailsVisibility == Visibility.Collapsed &&
              editorHost.Visibility == Visibility.Collapsed,
            "UI397_B: a mutable pair's retained change names the pair and its resulting state on the visible " +
            "page while the details stay collapsed (" + Describe(feedback) + ")");
        check(ConsoleText(window).Contains(success, StringComparison.Ordinal),
            "UI397_B: the existing status recording still happens behind the page-level feedback");

        // (3) The real pending-work guard: refused visibly, the pair's own state unchanged, and the previous
        // outcome replaced rather than left beside the new one.
        toggle = LiveSyncSwitch(window, pendingPair.Id);
        var pendingEnabled = window.PairRows.Single(item => item.Id == pendingPair.Id).Enabled;
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, !pendingEnabled);
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpUntil(() => feedback.Text.StartsWith(LiveSyncRefusal, StringComparison.Ordinal),
            "the real pending-work refusal to reach the page");
        var refusedRow = window.PairRows.Single(item => item.Id == pendingPair.Id);
        check(feedback.Text == LiveSyncRefusal + PendingWorkRefusal &&
              feedback.Visibility == Visibility.Visible && feedback.IsVisible &&
              AutomationProperties.GetName(feedback) == "Action failed" &&
              refusedRow.Enabled == pendingEnabled && toggle.IsChecked == pendingEnabled,
            "UI397_B: a pair with pending work refuses the change visibly, keeps its Enabled state and reverts " +
            "the switch (" + Describe(feedback) + ", pairEnabled=" + refusedRow.Enabled + ")");
        check(ConsoleText(window).Contains(LiveSyncRefusal + PendingWorkRefusal, StringComparison.Ordinal),
            "UI397_B: the refusal keeps the existing status recording");

        check(!(bool)Call(window, "CurrentPairEditorHasUnsavedChanges")!,
            "AUD16 a saved Live Sync row change does not create a phantom hidden-editor draft");
        toggle = LiveSyncSwitch(window, pausedPair.Id);
        check(BindingOperations.IsDataBound(toggle, ToggleButton.IsCheckedProperty) &&
              BindingOperations.IsDataBound(toggle, UIElement.IsEnabledProperty),
            "AUD14 Live Sync preserves the shared checked/enabled bindings after real success and refusal");
        var nameBox = Field<TextBox>(window, "PairNameBox");
        var originalName = nameBox.Text;
        nameBox.Text = originalName + " deliberate draft";
        var monitorGate = new TaskCompletionSource<IReadOnlyList<SyncAuthorityMonitorStartResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        MainWindow.MonitorConfiguratorForTest = (_, _, _) => monitorGate.Task;
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        check(AutomationProperties.GetItemStatus(toggle) == "In progress" && !toggle.IsEnabled &&
              feedback.Text == "Pausing live sync for " + pausedPair.Name + "…" &&
              feedback.IsVisible && AutomationProperties.GetName(feedback) == "Action status",
            "AUD13 a new Live Sync action immediately replaces the previous refusal with its exact in-flight action");
        PumpUntil(() => !window.PairRows.Single(item => item.Id == pausedPair.Id).Enabled,
            "row pause retained before monitor refresh");
        Set(window, "_uiRunActive", true);
        Call(window, "SetRunControls");
        monitorGate.SetResult([]);
        PumpUntil(() => AutomationProperties.GetItemStatus(toggle) != "In progress", "row action completion");
        check(!toggle.IsEnabled && BindingOperations.IsDataBound(toggle, UIElement.IsEnabledProperty) &&
              AutomationProperties.GetHelpText(toggle).Contains("scan or sync is running"),
            "AUD14 completing a row action cannot reenable it during a newly active sync");
        check(nameBox.Text == originalName + " deliberate draft" &&
              (bool)Call(window, "CurrentPairEditorHasUnsavedChanges")!,
            "AUD16 rebasing the saved Live Sync field preserves genuine unsaved name edits");
        nameBox.Text = originalName;
        Set(window, "_uiRunActive", false);
        Call(window, "SetRunControls");
        check(toggle.IsEnabled && !(bool)Call(window, "CurrentPairEditorHasUnsavedChanges")!,
            "AUD16 returning the real edit to its saved value leaves no phantom draft and restores the bound switch");
        MainWindow.MonitorConfiguratorForTest = (_, _, _) =>
            Task.FromResult<IReadOnlyList<SyncAuthorityMonitorStartResult>>([]);
    }

    /// <summary>The real Live Sync switch of one real pair row, found through that row's own bound identity.</summary>
    private static CheckBox LiveSyncSwitch(MainWindow window, string pairId)
    {
        var list = Field<DataGrid>(window, "PairList");
        var row = window.PairRows.Single(item => item.Id == pairId);
        list.ScrollIntoView(row);
        list.UpdateLayout();
        window.UpdateLayout();
        Drain();
        var container = PairRowContainer(window, pairId);
        return Descendants(container).OfType<CheckBox>()
                   .FirstOrDefault(box => ReferenceEquals(box.Tag, row)) ??
               throw new InvalidDataException("UI397_B: the pair row " + pairId + " lost its Live Sync switch.");
    }

    private static DataGridRow PairRowContainer(MainWindow window, string pairId)
    {
        var list = Field<DataGrid>(window, "PairList");
        var row = window.PairRows.Single(item => item.Id == pairId);
        return list.ItemContainerGenerator.ContainerFromItem(row) as DataGridRow ??
               throw new InvalidDataException("UI397_B: the pair row " + pairId + " was not rendered.");
    }

    private static string FixtureLocalFolder(string root, string name)
    {
        var local = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        File.WriteAllText(Path.Combine(local, "local.txt"), "UI397 fixture folder");
        return local;
    }

    /// <summary>
    /// One genuinely non-terminal operation for the pair, so the product's own pending-work guard is the thing
    /// that refuses the next change and not a substituted decision.
    /// </summary>
    private static async Task SeedPendingPairWorkAsync(SyncAuthorityDatabase authority, SyncPair pair)
    {
        var id = pair.Id + "-ui397-pending";
        var hash = new string('b', 64);
        // UI397_E_FIX: the membership below names this pair's local root binding, and the schema requires that
        // parent row (membership.root_binding_id REFERENCES root_binding). The product helper is the established
        // fixture registration: this pair's own captured lease identity in the same hex forms the workspace
        // fixture already uses, with the pair's real local root and no volume or installation prerequisite.
        await authority.RegisterRootBindingAsync(pair.Id + "-local", pair.Id, "local", 1,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                volumeSerial = pair.LocalVolumeSerialNumber.ToString("x8"),
                fileId = pair.LocalRootFileId.ToString("x16")
            }), pair.LocalRoot);
        await authority.RegisterObjectAsync(id, "file");
        await authority.RegisterMembershipAsync(id + "-membership", id, pair.Id + "-local", "pending.txt", 1);
        await authority.RegisterObjectVersionAsync(id + "-version", id, "local", "{}", hash, 1024);
        var fixtureAdmission = await authority.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id, null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await authority.CreateOperationAsync(new SyncAuthorityOperationPlan(id, id + "-key",
            SyncAuthorityOperationKind.CreateLocal, pair.Id, id, pair.PolicyRevision, LiveSyncCapabilityProfile, "{}",
            pair.Priority,
            [
                new(id + "-source", pair.Id, id, "source", pair.Id + "-local", id + "-membership",
                    id + "-version", 1, "pending.txt", "{\"kind\":\"file\"}")
            ], [], []), fixtureAdmission);
    }

    private static SyncRecoveryCatalogItem RevisionItem(SyncPair pair) => new(RevisionKey, pair.Id, pair.Name,
        SyncRecoverySource.DropboxRevision, RelativePath, "Fixture Account", "Dropbox revision rev-uf-1", 2048,
        new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero), "Subject to the connected Dropbox account's revision history",
        "Available from Dropbox", "sha256-uf-revision", "id:uf-file", "rev-uf-1", "{}", true, "file", false);

    // The real Compare item is the conflicts grid's own context-menu entry. Its header is matched by its
    // displayed text, so a command-icon decoration can never make it unreachable.
    private static MenuItem? CompareItem(MainWindow window) => CompareItems(window)
        .FirstOrDefault(item => Header(item).StartsWith("Compare", StringComparison.Ordinal));

    private static IEnumerable<MenuItem> CompareItems(MainWindow window) =>
        Field<DataGrid>(window, "ConflictGrid").ContextMenu?.Items.OfType<MenuItem>() ?? [];

    private static string Header(MenuItem item) => item.Header switch
    {
        string text => text,
        Panel panel => string.Concat(panel.Children.OfType<TextBlock>().Select(child => child.Text)),
        _ => string.Empty
    };

    // The real action button is matched inside the real page by the text it actually displays. The product's
    // own command-label pass captions it "Load Revisions", so the match is on the real displayed caption.
    private static Button? LoadRevisionsButton(MainWindow window) =>
        Descendants(Field<Border>(window, "RecoveryPage")).OfType<Button>()
            .FirstOrDefault(button => string.Equals(ButtonText(button), "Load revisions", StringComparison.OrdinalIgnoreCase));

    private static string ButtonText(Button button) => button.Content switch
    {
        string text => text,
        Panel panel => string.Concat(panel.Children.OfType<TextBlock>().Select(child => child.Text)),
        _ => string.Empty
    };

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static string Describe(TextBlock target) =>
        "visibility=" + target.Visibility + "; visible=" + target.IsVisible + "; text='" + target.Text + "'";

    // A real click on the real control, completed by the layout pass and dispatcher idle that make the
    // page's own visibility state observable for the assertions that follow.
    private static void Click(MainWindow window, Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        Drain();
    }

    // The recovery page records a queued follow-up page reload when a refresh arrives while a load is in
    // flight, and honours it in the load's own finally. Navigation in this fixture can leave that queue
    // armed, so it is normalised here: the assertions below are about the explicit action's own loading,
    // result and refusal feedback, not about the separate page reload that legitimately follows it.
    private static void ClearQueuedRecoveryReload(MainWindow window) =>
        Set(window, "_recoveryReloadPending", false);

    // Brings the page to a settled state before an explicit action: no page load in flight, the dispatcher
    // idle, and no queued follow-up page reload armed. The action's own feedback is then the only thing the
    // next assertion can observe.
    private static void SettleRecovery(MainWindow window)
    {
        // The page's own pair-choice refresh, page load and deferred selector resolution are asynchronous, so
        // the settle runs layout passes and waits until the whole recovery state has been unchanged for a
        // bounded window rather than for a single dispatcher turn.
        // The revision action belongs to the page's own revision tab, so that tab is selected up front. The
        // handler's own selection is then a no-op instead of a re-host of the page, which is what keeps this
        // check about the action's feedback rather than about the page's separate follow-up reload.
        if (Field<TabControl?>(window, "_recoveryTabs") is { } tabs) tabs.SelectedIndex = 1;
        var state = string.Empty;
        var stableSince = -1L;
        var deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline)
        {
            Drain();
            window.UpdateLayout();
            Drain();
            var box = Field<ComboBox>(window, "RecoveryPairBox");
            var next = string.Join("|", Field<bool>(window, "_loadingRecovery"),
                Field<bool>(window, "_recoveryReloadPending"), Field<int>(window, "_recoveryQueryVersion"),
                box.Items.Count, (box.SelectedItem as SyncRecoveryPairChoice)?.Id ?? "<none>",
                box.SelectedValue?.ToString() ?? "<none>");
            if (string.Equals(next, state, StringComparison.Ordinal))
            {
                if (stableSince < 0) stableSince = Environment.TickCount64;
                else if (Environment.TickCount64 - stableSince > 500) break;
            }
            else
            {
                state = next;
                stableSince = -1;
            }
            Thread.Sleep(5);
        }
        ClearQueuedRecoveryReload(window);
    }

    private static bool RecoveryQuiet(MainWindow window) =>
        !Field<bool>(window, "_loadingRecovery") && !Field<bool>(window, "_recoveryReloadPending");

    private static string DescribeBox(MainWindow window)
    {
        var box = Field<ComboBox>(window, "RecoveryPairBox");
        return "items=" + box.Items.Count + " sel=" + ((box.SelectedItem as SyncRecoveryPairChoice)?.Id ?? "<none>") +
               " value=" + (box.SelectedValue?.ToString() ?? "<none>") + " selecting=" +
               Field<bool>(window, "_selectingRecoveryPair");
    }

    // Runs the dispatcher until the recovery load has settled and stayed settled, recording every distinct
    // summary the page actually presented together with the rows presented at that moment. A queued follow-up
    // page reload may legitimately replace the action's own result afterwards, so the presented sequence,
    // not only the final text, is the evidence this check asserts against.
    private static List<(string Summary, int Rows, string[] Keys, int Version, bool Queued, bool Loading)> ObserveRecoveryLoad(MainWindow window)
    {
        var summary = Field<TextBlock>(window, "RecoverySummary");
        var seen = new List<(string Summary, int Rows, string[] Keys, int Version, bool Queued, bool Loading)>();
        void Record()
        {
            var snapshot = (Summary: summary.Text, Rows: window.RecoveryRows.Count,
                Keys: window.RecoveryRows.Select(row => row.Item.Key).ToArray(),
                Version: Field<int>(window, "_recoveryQueryVersion"),
                Queued: Field<bool>(window, "_recoveryReloadPending"),
                Loading: Field<bool>(window, "_loadingRecovery"));
            if (seen.Count == 0 || !string.Equals(seen[^1].Summary, snapshot.Summary, StringComparison.Ordinal) ||
                seen[^1].Version != snapshot.Version || seen[^1].Queued != snapshot.Queued)
                seen.Add(snapshot);
        }
        var idleSince = -1L;
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            Record();
            Drain();
            if (Field<bool>(window, "_loadingRecovery")) idleSince = -1;
            else if (idleSince < 0) idleSince = Environment.TickCount64;
            else if (Environment.TickCount64 - idleSince > 300) break;
            Thread.Sleep(5);
        }
        Record();
        return seen;
    }

    private static string Describe(List<(string Summary, int Rows, string[] Keys, int Version, bool Queued, bool Loading)> presented) =>
        string.Join(" -> ", presented.Select(item =>
            "'" + item.Summary.Replace(Environment.NewLine, " / ") + "' rows=" + item.Rows +
            " keys=[" + string.Join(",", item.Keys) + "] v=" + item.Version + " queued=" + item.Queued +
            " loading=" + item.Loading));

    private static string ConsoleText(MainWindow window) => Field<TextBox>(window, "ConsoleText").Text;

    private static void CheckTerminalReadbackGuard(MainWindow window, SyncPair pair, Action<bool, string> check)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatches = 0;
        const string completedSummary = "Fixture: applied 3 reviewed actions; 1 skipped item remains unchanged.";
        string? projectedSummary = null;
        var previousDispatcher = MainWindow.PairRunDispatcherForTest;
        var previousReadbackFailure = Field<string?>(window, "_workspaceReadbackFailure");
        MainWindow.PairRunDispatcherForTest = (_, _) => { dispatches++; return Task.CompletedTask; };
        try
        {
            Set(window, "_uiRunActive", true);
            var finishing = (Task)Call(window, "FinishRunPresentationAsync", "Sync",
                (Func<Task>)(() => gate.Task),
                (Action<string>)(value => projectedSummary = value), completedSummary)!;
            Drain();
            check(!finishing.IsCompleted && Field<bool>(window, "_uiRunActive") &&
                  Field<bool>(window, "_readingRunOutcomes") &&
                  !Field<Button>(window, "RunButton").IsEnabled,
                "The real completion handler keeps sync busy while recorded outcomes are still loading.");
            check(!Field<Button>(window, "PauseButton").IsEnabled && !Field<Button>(window, "StopButton").IsEnabled &&
                  Field<Button>(window, "StopButton").ToolTip is string reason && reason.Contains("recorded outcomes"),
                "Terminal readback does not pretend a file transfer can be paused or stopped, and explains why.");
            Pump((Task)Call(window, "RunPairsAsync", new[] { pair }, "Reentry during recorded-outcome loading")!);
            check(dispatches == 0 && Field<bool>(window, "_uiRunActive"),
                "The actual shared run entry point refuses a second start during terminal readback.");
            gate.SetResult();
            Pump(finishing);
            check(!Field<bool>(window, "_uiRunActive") && !Field<bool>(window, "_readingRunOutcomes") &&
                  Field<Button>(window, "RunButton").IsEnabled,
                "Successful readback releases its controls only after the awaited operation settles.");
            check(projectedSummary == completedSummary && ConsoleText(window).Contains(completedSummary, StringComparison.Ordinal),
                "Successful readback restores the caller's exact completed/skipped result instead of a generic ended message.");
            const string cause = "Controlled recorded-outcome read failure";
            Set(window, "_uiRunActive", true);
            Pump((Task)Call(window, "FinishRunPresentationAsync", "Sync",
                (Func<Task>)(() => Task.FromException(new IOException(cause))), null, completedSummary)!);
            check(!Field<bool>(window, "_uiRunActive") && Field<Button>(window, "RunButton").IsEnabled &&
                  ConsoleText(window).Contains(cause, StringComparison.Ordinal) &&
                  ConsoleText(window).Contains("IOException", StringComparison.Ordinal) &&
                  ConsoleText(window).Contains("display error does not change any recorded file outcome", StringComparison.Ordinal) &&
                  window.TrayDetail.Contains(cause, StringComparison.Ordinal) &&
                  Field<TextBlock>(window, "PhaseText").Text.Contains(cause, StringComparison.Ordinal),
                "A readback failure keeps its exact cause in Home/tray, preserves outcome meaning and releases controls without escaping.");
            Set(window, "_uiRunActive", true);
            Pump((Task)Call(window, "FinishRunPresentationAsync", "Sync",
                (Func<Task>)(() => Task.CompletedTask), null, null)!);
            check(Field<string?>(window, "_workspaceReadbackFailure") is null &&
                  !window.TrayDetail.Contains(cause, StringComparison.Ordinal),
                "A real successful terminal readback clears its earlier display error without changing file outcomes.");
            check(dispatches == 0, "Neither terminal-readback path dispatches or replays a file effect.");
        }
        finally
        {
            gate.TrySetResult();
            MainWindow.PairRunDispatcherForTest = previousDispatcher;
            Set(window, "_readingRunOutcomes", false);
            Set(window, "_uiRunActive", false);
            Set(window, "_workspaceReadbackFailure", previousReadbackFailure);
        }
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    // The fixture's own authority work must complete while the dispatcher keeps running: this process owns a
    // DispatcherSynchronizationContext, so blocking on a task without draining would deadlock its continuation.
    private static void Pump(Task task)
    {
        var deadline = Environment.TickCount64 + 30000;
        while (!task.IsCompleted && Environment.TickCount64 < deadline)
        {
            Drain();
            Thread.Sleep(2);
        }
        if (!task.IsCompleted)
            throw new InvalidOperationException("A UI397 fixture authority operation did not settle.");
        task.GetAwaiter().GetResult();
        Drain();
    }

    private static T Pump<T>(Task<T> task)
    {
        Pump((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> condition, string label)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            Drain();
            Thread.Sleep(5);
            if (Environment.TickCount64 - started > 20000)
                throw new InvalidOperationException("Timed out waiting for " + label);
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;

    private static void Set(object target, string name, object? value) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static object? Call(object target, string name, params object?[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
