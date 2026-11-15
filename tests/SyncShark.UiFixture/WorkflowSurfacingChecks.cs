using Massdex.SyncShark;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace SyncShark.UiFixture;

// WF1/ENG4/WF2 focused contract over the real surfaces: the real folder browser window and its provider-neutral
// paging API, the real MainWindow attention/First sync/Runs presentation, and the real draft-choice action.
internal static class WorkflowSurfacingChecks
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var runRoot = Path.Combine(root, "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runRoot);
        // One application and one isolated authority for every surface in this contract: the folder browser and
        // the main window both need the real app resources, and a second Application cannot be created.
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var instance = PilotInstanceLock.Acquire(Path.Combine(runRoot, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        try
        {
            CheckBrowserAutoPaging();
            CheckBrowserErrorAndCancelTruthfulness();
            CheckRetainedFirstSyncProblemSurfacing(runRoot, instance.StateAuthority, theme);
            HomeProducerProjectionChecks.Run(Path.Combine(runRoot, "home-producer"));
        }
        finally
        {
            MainWindow.SuppressLoadedStartupForTest = false;
            app.Shutdown();
        }
        Console.WriteLine("PASS workflow surfacing checks: browser auto-paging, retained first-sync problem surfacing " +
            "and the recommended-choices draft action; no provider, credential, network or user-file effect");
        return 0;
    }

    // WF1: expanding a level continues the existing provider-neutral paging API, so the last page's item is
    // present without any manual Load more, the accumulated children are globally ordered, and only a level that
    // actually completed claims completion.
    private static void CheckBrowserAutoPaging()
    {
        var service = new ScriptedFolderBrowserService();
        // The first page of this level is stable, so a later refresh of the same level re-reads it and the
        // automatic continuation then completes the level again.
        service.ListHandler = (_, _) => Task.FromResult(Page("c1", "Alpha", "Bravo"));
        service.ContinueListHandler = (cursor, _) => Task.FromResult(cursor switch
        {
            "c1" => Page("c2", "Delta", "Echo"),
            "c2" => Page(null, "Charlie"),
            _ => Page(null)
        });
        var window = new DropboxFolderBrowserWindow(service, "Fixture Account")
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var node = window.Roots[0];
            WaitUntil(() => node.Children.Any(child => child.Name == "Charlie") && node.ContinueCursor is null,
                "the browser never reached the last provider page for this level (names=" +
                string.Join(",", node.Children.Select(child => child.Name)) + "; cursor=" +
                (node.ContinueCursor ?? "<none>") + "; status=" + Status(window) + "; lists=" +
                service.ListCalls + "; continues=" + service.ContinueListCalls + ")");
            var names = node.Children.Where(child => child.Entry is not null).Select(child => child.Name).ToArray();
            Require(names.SequenceEqual(["Alpha", "Bravo", "Charlie", "Delta", "Echo"]),
                "the browser did not accumulate every provider page in one global order: " + string.Join(", ", names));
            Require(Status(window).Contains("Loaded all 5 child folder", StringComparison.Ordinal),
                "a completed level did not claim its real completion: " + Status(window));
        }
        finally
        {
            window.Close();
        }
    }

    // WF1: a failed continuation is disclosed with the count actually loaded, never claims completeness, and the
    // list stays continuable; a closed dialog stops cleanly.
    private static void CheckBrowserErrorAndCancelTruthfulness()
    {
        var service = new ScriptedFolderBrowserService();
        var failContinuation = true;
        service.ListHandler = (_, _) => Task.FromResult(Page("c1", "Alpha"));
        service.ContinueListHandler = (_, _) => failContinuation
            ? Task.FromException<DropboxFolderPage>(new IOException("simulated provider failure"))
            : Task.FromResult(Page(null, "Zulu"));
        var window = new DropboxFolderBrowserWindow(service, "Fixture Account")
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false };
        var node = window.Roots[0];
        try
        {
            window.Show();
            WaitUntil(() => Status(window).Contains("could not be loaded after", StringComparison.Ordinal),
                "a failed continuation was not disclosed with the loaded count");
            Require(!Status(window).Contains("Loaded all", StringComparison.Ordinal) &&
                    node.LoadedChildCount == 1 && node.ContinueCursor is not null &&
                    Status(window).Contains("list is incomplete", StringComparison.Ordinal),
                "a failed continuation claimed completeness or lost its continuation: " + Status(window));
            failContinuation = false;
            InvokeOn(window, "LoadMore_Click", window, new RoutedEventArgs());
            WaitUntil(() => node.LoadedChildCount == 2, "the manual continuation did not finish after the failure");
            Require(node.Children.Any(child => child.Name == "Zulu"),
                "the recovered continuation did not merge the remaining provider page");
        }
        finally
        {
            window.Close();
        }

        var blocked = new TaskCompletionSource<DropboxFolderPage>();
        var slow = new ScriptedFolderBrowserService();
        slow.ListHandler = (_, _) => Task.FromResult(Page("c1", "Alpha"));
        slow.ContinueListHandler = (_, _) => blocked.Task;
        var cancelWindow = new DropboxFolderBrowserWindow(slow, "Fixture Account")
        { WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false };
        try
        {
            cancelWindow.Show();
            WaitUntil(() => slow.ContinueListCalls > 0, "the cancel scenario never requested its continuation");
            cancelWindow.Close();
            blocked.SetResult(Page(null, "Late"));
            Drain();
            Require(true, "closing the browser during an automatic continuation stopped cleanly");
        }
        finally
        {
            if (cancelWindow.IsLoaded) cancelWindow.Close();
        }
    }

    // ENG4/WF2: the retained typed first-sync problem reaches Needs Attention (All and Problems), the First sync
    // page and the Runs reason, and its exact command is linked from both places; the recommended-choices action
    // is a draft action that changes no file.
    private static void CheckRetainedFirstSyncProblemSurfacing(string runRoot, PilotStateAuthority stateAuthority,
        ThemeManager theme)
    {
        var localRoot = Path.Combine(runRoot, "local");
        Directory.CreateDirectory(localRoot);
        using var lease = PilotDirectoryLease.Open(localRoot);
        var dropbox = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "surfacing-account", "surfacing-root", "surfacing-home",
            DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
            DisplayName: "surfacing-account", Email: "surfacing-account@example.test").Normalize();
        Pump(new DropboxConnectionStore(stateAuthority).SaveNewAsync(dropbox));
        var pair = new SyncPair("surfacing-pair", "Surfacing Pair", localRoot,
            lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/a", "id:a",
            ConnectionId: dropbox.ConnectionId, InitialReviewApproved: true);
        var pairSet = new SyncPairSet([pair], pair.Id);
        var window = new MainWindow(stateAuthority, theme)
        {
            Width = 1240, Height = 780, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false
        };
        try
        {
            window.Show();
            Drain();
            Pump(window.AuthorityHost.LoadPairsAsync());
            Pump(window.AuthorityHost.Authority.RegisterPairAsync(pair.Id, pair.PolicyRevision));
            Pump(window.AuthorityHost.Authority.RetainPairDefinitionAsync(pair, true));
            window.LoadPairSetForTest(pairSet);
            var problem = new SyncFirstSyncProblem("first_sync_incomplete", "Reviewed first sync",
                "First sync is not complete: this pair still has items that must be reviewed before they are copied.",
                "waiting.txt",
                "Open First sync review, review the remaining items, then confirm choices and sync.",
                "command-surfacing-run", pair.Id, pair.Name, dropbox.ConnectionId, DateTimeOffset.UtcNow, null);
            const string failureEventId = "surfacing-retained-failure";
            // The retained Activity projection prefixes a persisted presentation identity with "history:"
            // (production SyncAuthorityDatabase SQL), so every row comparison below uses the projected
            // identity while the append above keeps the raw one.
            const string expectedProjectedEventId = "history:" + failureEventId;
            Pump(LegacyHistoryFixture.SeedAsync(window.AuthorityHost.Authority, new SyncActivityEvent(failureEventId,
                DateTimeOffset.UtcNow.AddDays(-2), [SyncActivityCategories.Sync], "Error", "Folder pair",
                "First sync failed", problem.Message, dropbox.ConnectionId, pair.Id, problem.CommandId,
                RelativePath: problem.RelativePath ?? string.Empty)));
            Field<Dictionary<string, SyncFirstSyncProblem>>(window, "_firstSyncProblemsByPair")[pair.Id] = problem;
            Invoke(window, "RefreshFullApp");
            Invoke(window, "RefreshWorkspace");
            Drain();
            var caseRow = window.TrayAttentionCases.SingleOrDefault(item => item.Pair.Id == pair.Id &&
                item.Title == "First sync needs attention");
            Require(caseRow is not null && caseRow.IsProblem &&
                    caseRow.Description.Contains(problem.Message, StringComparison.Ordinal) &&
                    caseRow.Description.Contains(problem.CommandId, StringComparison.Ordinal),
                "Needs Attention did not present the retained typed first-sync problem with its exact run");
            var allList = Field<StackPanel>(window, "_attentionList");
            var problemsList = Field<StackPanel>(window, "_attentionProblems");
            Require(CaseTitles(allList).Any(title => title == "First sync needs attention") &&
                    CaseTitles(problemsList).Any(title => title == "First sync needs attention"),
                "the retained first-sync problem was missing from the All or Problems list");
            var actionLabel = (Field<Button>(window, "_homeStateAction").Content as string) ?? string.Empty;
            Require(actionLabel.Contains("Review 1 issue", StringComparison.Ordinal),
                "the retained first-sync problem did not drive the Home review entry: " + actionLabel);
            // UI504: native scanning populates BOTH a retained command problem and the
            // transient run-health projection. They are one issue, not two Problems rows.
            var runReasons = Field<Dictionary<string, string>>(window, "_runAttentionReasonByPair");
            var pairRow = window.PairRows.Single(row => row.Id == pair.Id);
            runReasons[pair.Id] = problem.Message;
            pairRow.UpdateHealth(true, problem.Message);
            Invoke(window, "RefreshFullApp");
            Invoke(window, "RefreshWorkspace");
            Require(window.TrayAttentionCases.Count(item => item.Pair.Id == pair.Id && item.IsProblem) == 1 &&
                    window.CliAttentionItems.Count(item => item.PairId == pair.Id && item.IsProblem) == 1 &&
                    CaseTitles(problemsList).Count == 1,
                "the same retained first-sync failure is counted once across Problems, tray and CLI");
            const string independentFailure = "A different pending operation failed after the first-sync scan.";
            runReasons[pair.Id] = independentFailure;
            pairRow.UpdateHealth(true, independentFailure);
            Invoke(window, "RefreshFullApp");
            Require(window.TrayAttentionCases.Count(item => item.Pair.Id == pair.Id && item.IsProblem) == 2 &&
                    CaseTitles(problemsList).Count == 2,
                "deduplicating the scan failure preserves a different run problem for the same pair");
            runReasons.Remove(pair.Id);
            pairRow.UpdateHealth(false, string.Empty);
            Invoke(window, "RefreshFullApp");
            Invoke(window, "RefreshWorkspace");
            // S476-2: use the real persisted event/query/list, not merely the destination's run text box.
            // The old filters must first demonstrably hide it. Neither link is followed by a test reload.
            void HideFailureUsingOldFilters()
            {
                Invoke(window, "OpenWorkspaceActivity", 0, null);
                Invoke(window, "SelectAllActivityCategories");
                Pump((Task)InvokeResult(window, "LoadActivityPageAsync", true, null)!);
                // The editable type selector queues its committed-text query on the dispatcher.
                // Await the query actually applied to the list, not an older request it superseded.
                WaitUntil(() => Field<int>(window, "_activityLastAppliedQueryVersion") ==
                                Field<int>(window, "_activityQueryVersion"),
                    "the unfiltered Activity query never settled");
                Require(window.ActivityRows.Any(row => row.EventId == expectedProjectedEventId),
                    "the retained failure exists in the real unfiltered Activity query before the drilldown");
                Field<Dictionary<string, ToggleButton>>(window, "_activityCategoryChipByValue")
                    [SyncActivityCategories.Files].IsChecked = true;
                window.ActivitySearchBox.Text = "unrelated old search";
                window.ActivityFilterBox.SelectedIndex = 1;
                window.ActivityTypeFilter.SelectedIndex = 1;
                window.ActivityDateBox.SelectedIndex = 1;
                Field<ComboBox>(window, "_activitySeverityBox").SelectedIndex = 2;
                Field<ComboBox>(window, "_activityEntityBox").SelectedIndex = 1;
                Field<TextBox>(window, "_activityRunBox").Text = "unrelated-old-run";
                Pump((Task)InvokeResult(window, "LoadActivityPageAsync", true, null)!);
                WaitUntil(() => Field<int>(window, "_activityLastAppliedQueryVersion") ==
                                Field<int>(window, "_activityQueryVersion"),
                    "the conflicting-filter Activity query never settled");
                Require(!window.ActivityRows.Any(row => row.EventId == expectedProjectedEventId),
                    "conflicting category/search/action/type/date/severity/entity/run filters hide the retained failure");
            }
            void RequireRevealedFailure(string entry)
            {
                WaitUntil(() => window.ActivityRows.Any(row => row.EventId == expectedProjectedEventId),
                    entry + " did not reveal the persisted failure through its own refresh");
                Require(Field<string>(window, "_workspacePage") == "Activity" &&
                        Field<TextBox>(window, "_activityRunBox").Text == problem.CommandId &&
                        window.ActivityRows.All(row => row.RunId == problem.CommandId) &&
                        Field<IEnumerable<ActivityRow>>(window, "_recentRows").Any(row =>
                            row.EventId == expectedProjectedEventId &&
                            row.Reason.Contains(problem.Message, StringComparison.Ordinal)),
                    entry + " reveals the exact retained failure and reason in the visible Activity list");
                Require(window.ActivitySearchBox.Text.Length == 0 && window.ActivityFilterBox.SelectedIndex == 0 &&
                        window.ActivityTypeFilter.SelectedIndex == 0 && window.ActivityDateBox.SelectedIndex == 0 &&
                        Field<ComboBox>(window, "_activitySeverityBox").SelectedIndex == 0 &&
                        Field<ComboBox>(window, "_activityEntityBox").SelectedIndex == 0,
                    entry + " clears incompatible filters, not history");
                var retainedRow = window.ActivityRows.Single(row => row.EventId == expectedProjectedEventId);
                Invoke(window, "InspectActivity", retainedRow);
                window.UpdateLayout();
                Drain();
                var visibleFields = Field<StackPanel>(window, "_detailFields").Children.OfType<StackPanel>()
                    .SelectMany(field => field.Children.OfType<TextBlock>()).ToArray();
                Require(visibleFields.Any(text => text.IsVisible && text.Text == retainedRow.Reason &&
                            text.Text.Contains(problem.Message, StringComparison.Ordinal) &&
                            text.TextWrapping == TextWrapping.Wrap) &&
                        visibleFields.Any(text => text.IsVisible && text.Text == problem.CommandId),
                    entry + " exposes the exact retained reason and run in the shared visible inspector: " +
                    string.Join(" | ", visibleFields.Select(text => $"{text.IsVisible}/{text.TextWrapping}:{text.Text}")));
                Invoke(window, "CloseWorkspaceDetails");
            }
            HideFailureUsingOldFilters();
            Invoke(window, "NavigateTo", "Home", true);
            Invoke(window, "HomeStateAction_Click", Field<Button>(window, "_homeStateAction"), new RoutedEventArgs());
            RequireRevealedFailure("Home review");
            window.BootstrapPairBox.SelectedValue = pair.Id;
            Invoke(window, "PresentRetainedFirstSyncProblem");
            var retainedText = Control<TextBlock>(window, "BootstrapRetainedProblem");
            var retainedLink = Control<Button>(window, "BootstrapRetainedProblemLink");
            Require(retainedText.Visibility == Visibility.Visible &&
                    retainedText.Text.Contains(problem.Message, StringComparison.Ordinal) &&
                    retainedText.Text.Contains(problem.CommandId, StringComparison.Ordinal) &&
                    retainedLink.Visibility == Visibility.Visible && ReferenceEquals(retainedLink.Tag, problem),
                "the First sync page did not state the retained problem with its exact run");
            HideFailureUsingOldFilters();
            Invoke(window, "OpenRetainedFirstSyncProblem_Click", retainedLink, new RoutedEventArgs());
            RequireRevealedFailure("First sync problem link");
            // The Runs surface names the same product-owned reason and run.
            var resultJson = "{\"reviewed\":1,\"firstSyncComplete\":false,\"firstSyncProblem\":" +
                System.Text.Json.JsonSerializer.Serialize(problem) + "}";
            var history = new SyncAuthorityRunHistory("command-surfacing-run", "run_pair",
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, "completed",
                "{\"pairIds\":[\"" + pair.Id + "\"]}", resultJson);
            var projected = InvokeResult(window, "ProjectRun", history);
            var statusLine = (string?)projected?.GetType().GetProperty("StatusLine")?.GetValue(projected);
            Require(statusLine is not null && statusLine.Contains(problem.Message, StringComparison.Ordinal) &&
                    statusLine.Contains(problem.CommandId, StringComparison.Ordinal),
                "the Runs surface did not state the retained typed reason and run: [" + (statusLine ?? "<null>") +
                "] rowType=" + (projected?.GetType().Name ?? "<null>"));
            // WF2: the recommended-choices action is a draft action over the real First sync rows.
            var dispatches = 0;
            MainWindow.BootstrapReviewPageLoaderForTest = (pairId, _, offset, _, _, _, _) =>
                Task.FromResult(new SyncBootstrapReviewPage(
                    [new SyncBootstrapReviewItem(pairId, "recommended.txt", "Local only", "File", "TXT", "Upload",
                        "Copy to cloud", 1, null, DateTimeOffset.UtcNow, null)], offset, 1, 1, 1, 0, 0, false, true,
                    "surfacing-local", "surfacing-cloud"));
            MainWindow.BootstrapActionPlanForTest = (_, _) =>
            {
                dispatches++;
                return Task.FromResult(new SyncBootstrapActionResult(pair, new SyncRunResult(pair.Id, []), 0, 0, false));
            };
            Invoke(window, "OpenFirstSyncReview", pair.Id);
            WaitUntil(() => window.BootstrapRows.Count > 0, "the first-sync review never loaded");
            var row = window.BootstrapRows[0];
            row.SelectedAction = "Skip for now";
            Invoke(window, "UseRecommendedBootstrapChoices_Click",
                Control<Button>(window, "UseRecommendedBootstrapButton"), new RoutedEventArgs());
            Drain();
            Require(row.SelectedAction == row.Recommendation && row.SelectedAction == "Upload" && dispatches == 0,
                "Use recommended choices did not restore the recommendation as a draft action: " + row.SelectedAction);
            Require(Control<TextBlock>(window, "BootstrapChoiceSummary").Text.Contains("recommended action",
                    StringComparison.Ordinal),
                "Use recommended choices did not state its draft result");

            var reservation = SyncAuthorityCommandReservation.Mint();
            var newer = problem with
            {
                CommandId = reservation.CommandId, ReportedUtc = DateTimeOffset.UtcNow,
                Message = "105 actions completed. Download b-failed.txt failed with HTTP 500."
            };
            var authority = window.AuthorityHost.Authority;
            Pump(authority.CreateAcceptedCommandAdmissionAsync(reservation, "run_pair", pair.Id, null, null,
                "{}", SyncAuthorityCommandPurpose.NormalEffect));
            Pump(authority.TransitionCommandRequestAsync(newer.CommandId, "blocked",
                System.Text.Json.JsonSerializer.Serialize(new { pairId = pair.Id, firstSyncProblem = newer })));
            // Leave the previous problem in the GUI cache. The actual GUI-backed
            // CLI entry point must read the current command before returning it.
            var currentAttention = window.LoadCliAttentionItemsAsync();
            Pump(currentAttention);
            Require(currentAttention.Result.Any(item => item.RunId == newer.CommandId &&
                        item.Description.Contains("105 actions completed", StringComparison.Ordinal)) &&
                    !currentAttention.Result.Any(item => item.RunId == problem.CommandId),
                "GUI-backed attention replaces its stale cached failure with the exact newer retained run");
            Field<Dictionary<string, SyncFirstSyncProblem>>(window, "_firstSyncProblemsByPair")[pair.Id] = problem;
            MainWindow.SummaryHistoryLoaderForTest = _ => Task.FromException<IReadOnlyList<SyncAuthorityActivityProjection>>(
                new IOException("fixture: unrelated summary history unavailable"));
            try { Pump((Task)InvokeResult(window, "RefreshAuthorityProjectionsAsync", false, null)!); }
            finally { MainWindow.SummaryHistoryLoaderForTest = null; }
            Require(window.TrayAttentionCases.Any(item => item.Description.Contains(newer.CommandId, StringComparison.Ordinal)) &&
                    !window.TrayAttentionCases.Any(item => item.Description.Contains(problem.CommandId, StringComparison.Ordinal)),
                "current failure remains visible even if a later dashboard history refresh fails");
        }
        finally
        {
            MainWindow.BootstrapReviewPageLoaderForTest = null;
            MainWindow.BootstrapActionPlanForTest = null;
            window.RequestProductExit();
        }
    }

    private static DropboxFolderPage Page(string? cursor, params string[] names) =>
        new(names.Select(name => new DropboxFolderEntry(name, "/" + name, "id:" + name)).ToArray(), cursor);

    private static string Status(DropboxFolderBrowserWindow window) =>
        ((TextBlock)window.FindName("BrowserStatus")).Text;

    private static List<string> CaseTitles(StackPanel list)
    {
        var titles = new List<string>();
        foreach (var border in list.Children.OfType<Border>())
            if (border.Child is DockPanel panel &&
                panel.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<Button>().FirstOrDefault()
                    is { Content: TextBlock text })
                titles.Add(text.Text);
        return titles;
    }

    private static T Control<T>(MainWindow window, string name) => (T)window.FindName(name);
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Invoke(MainWindow window, string name, params object?[] arguments) => typeof(MainWindow)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments);
    private static object? InvokeResult(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, arguments);

    private static void InvokeOn(object target, string name, params object?[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidDataException(description);
        Console.WriteLine("PASS " + description);
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void Pump(Task task)
    {
        while (!task.IsCompleted) Drain();
        task.GetAwaiter().GetResult();
    }

    private static void WaitUntil(Func<bool> ready, string failure)
    {
        var limit = DateTime.UtcNow.AddSeconds(15);
        while (!ready() && DateTime.UtcNow < limit) Drain();
        if (!ready()) throw new TimeoutException(failure);
    }
}
