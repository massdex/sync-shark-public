using Massdex.SyncShark;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace SyncShark.UiFixture;

internal static class WorkspaceUiChecks
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(appLock.StateAuthority));
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        var window = new MainWindow(appLock.StateAuthority, theme) { Width = 1100, Height = 740 };
        try
        {
            window.Show(); Drain();
            // UI13: Needs Attention carries the account in the shared compact header, one common
            // subtab origin and a keyboard-reachable question-mark control for conflict guidance.
            Call(window, "NavigateTo", "Attention", true); Drain(); window.UpdateLayout();
            var attentionTabs = Field<TabControl>(window, "_attentionTabs");
            Require(attentionTabs.Items.Cast<TabItem>().Select(tab => tab.Header?.ToString()).SequenceEqual(
                    new[] { "All", "First Sync", "Conflicts", "Problems" }),
                "Needs Attention keeps its four subtabs");
            var attentionHeader = ((Grid)Field<Border>(window, "_attentionPage").Child)
                .Children.OfType<PageHeaderPanel>().Single();
            Require(attentionHeader.Title == "Needs Attention" &&
                    attentionHeader.TrailingItems.OfType<StackPanel>().Any(panel =>
                        panel.Children.OfType<ComboBox>().Any(picker =>
                            System.Windows.Automation.AutomationProperties.GetName(picker) == "Needs Attention Account")),
                "Needs Attention shows the account selector in the compact header");
            var contentOrigins = new List<double>();
            var stripTop = 0.0;
            for (var index = 0; index < attentionTabs.Items.Count; index++)
            {
                ((TabItem)attentionTabs.Items[index]).IsSelected = true; Drain(); window.UpdateLayout();
                if (index == 0) stripTop = attentionTabs.TranslatePoint(new Point(0, 0), window).Y;
                Require(Math.Abs(attentionTabs.TranslatePoint(new Point(0, 0), window).Y - stripTop) < 1,
                    "The Needs Attention subtab strip stays fixed: " + index);
                var subtab = (UIElement)((TabItem)attentionTabs.Items[index]).Content;
                contentOrigins.Add(Math.Round(index == 3
                    ? ((FrameworkElement)((StackPanel)subtab).Children[0]).Height
                    : ((FrameworkElement)((Grid)subtab).Children.OfType<UIElement>()
                        .Single(child => Grid.GetRow(child) == 1)).TranslatePoint(new Point(0, 0), subtab).Y, 1));
            }
            Require(contentOrigins.Distinct().Count() == 1 && contentOrigins[0] > 0,
                "Every Needs Attention subtab starts its filter, table or list at one common origin: " +
                string.Join(", ", contentOrigins));
            ((TabItem)attentionTabs.Items[0]).IsSelected = true; Drain();
            var guidanceHelp = attentionHeader.TrailingItems.OfType<Button>().SingleOrDefault(button =>
                System.Windows.Automation.AutomationProperties.GetName(button) == "Conflict guidance");
            Require(guidanceHelp is { Focusable: true, IsTabStop: true, IsEnabled: true } &&
                    !string.IsNullOrWhiteSpace(guidanceHelp!.ToolTip?.ToString()) &&
                    !string.IsNullOrWhiteSpace(
                        System.Windows.Automation.AutomationProperties.GetHelpText(guidanceHelp)),
                "The conflict guidance control is keyboard reachable and explains itself");
            var guidanceBody = ((Border?)window.ConflictGuidanceContent)?.Child as StackPanel;
            Require(guidanceBody is not null && guidanceBody.Children.OfType<Expander>().Any(expander =>
                    expander.Header?.ToString() == "How to Resolve Conflicts" &&
                    expander.Content is TextBlock guidance &&
                    guidance.Text.Contains("never chooses for you", StringComparison.Ordinal)),
                "The guidance flyout reuses the existing conflict guidance text");
            Require(guidanceBody is not null && guidanceBody.Children.OfType<Button>().Any(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == "Close"),
                "The guidance flyout offers its own Close control");
            guidanceHelp!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain();
            Require(window.ConflictGuidanceOpen, "The conflict guidance control opens its flyout");
            window.ConflictGuidanceOpen = false; Drain();
            Require(!window.ConflictGuidanceOpen, "The guidance flyout closes without changing the page");
            Call(window, "NavigateTo", "Dashboard", true); Drain();
            Call(window, "NavigateTo", "Connections", true); Drain();
            var navigation = Field<ListBox>(window, "Navigation");
            navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(item => item.Tag?.ToString() == "Dashboard"); Drain();
            Require(Field<ListBox>(window, "_utilityNavigation").SelectedItem is null && navigation.SelectedItem is not null,
                "Clicking Home clears the previous utility-sidebar selection");
            var homeColumns = Field<Grid>(window, "_homeColumns");
            var wideHome = homeColumns.ColumnDefinitions[1].Width.Value;
            Call(window, "NavigateTo", "Connections", true); Drain(); Call(window, "RefreshWorkspace");
            Require(wideHome > 0 && homeColumns.ColumnDefinitions[1].Width.Value == wideHome,
                "Refreshing a hidden Home does not collapse its support cards");
            Call(window, "NavigateTo", "Dashboard", true);
            Require(homeColumns.ColumnDefinitions[1].Width.Value == wideHome,
                "Home returns with its correct columns before the next timer tick");
            window.Width = 790; Drain();
            Require(homeColumns.ColumnDefinitions[1].Width.Value == 0, "Home still adapts immediately to a narrow window");
            window.Width = 1100; Drain();
            var homeMenu = ((Panel)Field<Button>(window, "RunButton").Parent).Children.OfType<Button>().Last().ContextMenu;
            Require(homeMenu.Items.OfType<MenuItem>().Count() == 3 && homeMenu.Items.OfType<MenuItem>().All(item =>
                item.Header is StackPanel header && header.Children[0] is Viewbox &&
                !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(item))),
                "Every Home overflow command has an icon and an accessible name");
            // Navigation starts an asynchronous retained-history load. Settle a newer load before
            // seeding projection-only rows, so an older response cannot erase these fixture inputs.
            Call(window, "NavigateTo", "Activity", true);
            Pump(window.LoadActivityPageForTestAsync(true));
            var upload = new ActivityRow("2026-09-13 12:00:00 +00:00", "manual.pdf", "Starting upload", "Sending file", PairId: "one", PairName: "One", OperationId: "same", ItemKind: "File", FileType: "PDF");
            var complete = upload with { When = "2026-09-13 12:00:01 +00:00", Outcome = "Completed upload" };
            var check = upload with { OperationId = null, Outcome = "Unchanged" };
            window.ActivityRows.Add(complete); window.ActivityRows.Add(upload); window.ActivityRows.Add(check);
            Call(window, "TrackWorkspaceAction", upload); Call(window, "TrackWorkspaceAction", complete); Call(window, "TrackWorkspaceAction", check);
            Call(window, "RefreshWorkspaceFeeds"); Drain();
            Require(Field<DataGrid>(window,"ActivityGrid").Columns[0].ActualWidth > 300,"Grouped Activity keeps a readable name column after navigation");
            var recent = Field<System.Collections.ObjectModel.ObservableCollection<ActivityRow>>(window, "_recentRows");
            // UI07: the unified list keeps every distinct retained event. Deduplication is by stable
            // event identity, so two stages that happen to share one operation id are never collapsed.
            Require(recent.Count == 2 && recent[0].Outcome == "Completed upload" && recent[1].Outcome == "Starting upload",
                "The unified list keeps distinct lifecycle stages instead of collapsing them by operation id; " +
                $"recent={recent.Count}, retained={window.ActivityRows.Count}, " +
                $"outcomes=[{string.Join(", ", recent.Select(row => row.Outcome))}], " +
                $"scope={Field<string?>(window, "_workspaceAccountId") ?? "all"}, routine={RoutineChecksIncluded(window)}");
            Require(window.ActivityRows.Count == 3, "The retained activity collection keeps the entire history");
            Require(window.TrayActivity.Count == 2 && SharedPresentation.SummaryRows(window.TrayActivity,
                null, SummaryActivityFilter.All, 12).Length == 1,
                "Tray retains category candidates until the shared summary selector runs");
            CheckSummarySelection(window, upload);
            Call(window, "InspectActivity", complete); Drain();
            Require(Field<Border>(window, "_workspaceInspector").Visibility == Visibility.Visible, "Row opens details");
            Require(Field<TextBox>(window, "DetailsText").Text.Contains("manual.pdf"), "Details retain path and reason");
            Require(ReferenceEquals(Field<ActivityRow>(window, "_inspectedActivity"), complete), "Context actions bind inspected item even outside a filtered list");
            // UI398-3: the expanded details name the EXACT account of the endpoint that recorded it, and an
            // endpoint that recorded no account says so instead of resolving the currently selected one.
            var endpointAccount = "connection-" + new string('b', 24);
            const string endpointAccountLabel = "exact@example.test (Exact Holder)";
            var endpointContext = new SyncEndpointContext("one", "One",
                new SyncEndpointContextSide(SyncPairSide.Left, SyncEndpointKind.Local, "local",
                    LocalPath: "C:\\VirtualizationFixture"),
                new SyncEndpointContextSide(SyncPairSide.Right, SyncEndpointKind.Dropbox, "dropbox",
                    SyncProviders.Dropbox, endpointAccount, "root-ns", "id:endpoint", "/Recorded right"));
            var endpointRow = complete with
                { Path = "Docs/report.txt", ConnectionId = null, EndpointContext = endpointContext };
            typeof(MainWindow).GetField("_accountLabels", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, new Dictionary<string, string>(StringComparer.Ordinal)
                    { [endpointAccount] = endpointAccountLabel });
            Call(window, "InspectActivity", endpointRow); Drain();
            var endpointDetails = Field<TextBox>(window, "DetailsText").Text;
            Require(endpointDetails.Contains("Account: " + endpointAccountLabel, StringComparison.Ordinal) &&
                    !endpointDetails.Contains(endpointAccount, StringComparison.Ordinal),
                "The expanded details name the exact account of the endpoint that recorded it and never the connection identity");
            Call(window, "InspectActivity", endpointRow with
                { EndpointContext = endpointContext with { Right = endpointContext.Right with { ConnectionId = null } } });
            Drain();
            var unrecordedDetails = Field<TextBox>(window, "DetailsText").Text;
            Require(unrecordedDetails.Contains("Account not recorded", StringComparison.Ordinal) &&
                    !unrecordedDetails.Contains(endpointAccountLabel, StringComparison.Ordinal) &&
                    !unrecordedDetails.Contains("connection-", StringComparison.Ordinal),
                "An endpoint that recorded no account states the absence rather than resolving the selected account");
            typeof(MainWindow).GetField("_accountLabels", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, new Dictionary<string, string>(StringComparer.Ordinal));
            // UI398-4: the shared command caption pass stays the default, and a caller that presents a
            // recorded literal opts out of it so the literal keeps its own casing and punctuation.
            var defaultCaption = DisplayedCaption(WorkspaceVisuals.Button("Left endpoint: Open in Dropbox", (_, _) => { }));
            var verbatimCaption = DisplayedCaption(WorkspaceVisuals.Button("Docs/Avant d'Été/Report'.PDF",
                (_, _) => { }, quiet: true, verbatim: true));
            Require(defaultCaption != "Left endpoint: Open in Dropbox" &&
                    defaultCaption.Contains("Left Endpoint:", StringComparison.Ordinal) &&
                    verbatimCaption == "Docs/Avant d'Été/Report'.PDF",
                "The shared button keeps its command-caption pass by default and shows a recorded literal verbatim on request ('" +
                defaultCaption + "' / '" + verbatimCaption + "')");
            Call(window, "CloseWorkspaceDetails");
            Require(Field<Border>(window, "_workspaceInspector").Visibility == Visibility.Collapsed, "Details have a working close action");
            var pairEvent = new ActivityRow("2026-09-18T01:00:00Z", "<operation>", "Completed",
                "The allocate_control operation completed.", PairId: "one", PairName: "Same pair", ItemKind: "Folder");
            Call(window, "InspectActivity", pairEvent); Drain();
            var iconStyle = System.Windows.Application.Current.Resources[BrandedItemImages.StyleResource] as string ?? "Combined";
            var expectedBrandedMark = BrandedItemImages.For(pairEvent, iconStyle);
            var inspectorIcon = Field<ShellTypeImage>(window, "_inspectorTypeIcon");
            Require(pairEvent.IsPairEvent && inspectorIcon.Source is { } brandedMark &&
                    ReferenceEquals(brandedMark, expectedBrandedMark) && brandedMark.IsFrozen,
                "A retained pair event uses the branded mark for the current icon style in the real details consumer");
            foreach (var style in new[] { "Symbol", "Colour", "Combined" })
            {
                BrandedItemImages.ApplyStyle(style);
                Call(window, "InspectActivity", complete); Drain();
                Call(window, "InspectActivity", pairEvent); Drain();
                Require(pairEvent.IsPairEvent && ReferenceEquals(Field<ShellTypeImage>(window, "_inspectorTypeIcon").Source,
                        BrandedItemImages.For(pairEvent, style)),
                    "Each selected icon style refreshes the inspected pair mark through the real consumer: " + style);
            }
            BrandedItemImages.ApplyStyle(iconStyle);
            var pairDetails = Field<TextBox>(window, "DetailsText").Text;
            Require(pairDetails.Contains("Type: Folder Pair") && !pairDetails.Contains("Relative Path:") &&
                    !pairDetails.Contains("Local Location:") && !pairDetails.Contains("Dropbox Location:"),
                "Pair event details omit fake relative paths and unretained locations");
            var googleEvent = complete with { ConnectionId = "gdrive-connection-" + new string('a', 24) };
            Require(googleEvent.HumanAction == "Uploaded to Google Drive", "The recorded Google account identity determines the activity provider label");
            Call(window, "InspectActivity", complete); Drain();
            Require(!complete.IsAppEvent, "Ordinary file rows keep their shell file-type icon path");
            Call(window, "CloseWorkspaceDetails");
            Set(window, "_uiRunActive", true); Set(window, "_workspacePhaseDetail", "Comparing observed folders");
            Call(window, "ShowStatus", "An unrelated folder-navigation message");
            Require(Field<TextBlock>(window, "PhaseText").Text == "Comparing observed folders", "Browsing cannot replace active run status");
            Set(window, "_globalPendingQueueCount", 3);
            window.QueueRows.Add(new QueueRow("one", "today", "One", "a", 1, "pair check", "pending"));
            Call(window, "UpdateQueueCount");
            Require(window.TrayPending == 3 && System.Windows.Automation.AutomationProperties.GetName(Field<Button>(window, "_homePending")) == "View current sync activity",
                "During a run Home links to live activity while the tray retains the durable pending count");
            Set(window, "_uiRunActive", false); Call(window, "RefreshWorkspace");
            Require(window.TrayPending == 3 && System.Windows.Automation.AutomationProperties.GetName(Field<Button>(window, "_homePending")).StartsWith("3 "),
                "When idle Pending uses the durable operation count, not the loaded row count");
            Require(!Field<TextBlock>(window, "PhaseText").Text.Contains("unrelated"), "Idle Home does not retain navigation warnings");
            var tabs = Field<TabControl>(window, "_activityTabs");
            // The user now requires a full-size, exact-run operation log, alongside summaries.
            Require(tabs.Items.Cast<TabItem>().Select(tab => tab.Header.ToString()).SequenceEqual(new[] { "Recent", "Pending", "Runs", "Operation log" }),
                "Activity exposes the full Operation Log beside Recent, Pending and Runs");
            Require(Field<ListBox>(window, "_utilityNavigation").Items.Cast<ListBoxItem>().Single(item => (string)item.Tag == "Connections").Visibility == Visibility.Visible, "Connections remains reachable in bottom utility navigation");
            Require(Field<Button>(window,"_workspaceBack").Visibility == Visibility.Collapsed, "No universal Back control");
            var tabTop = tabs.TranslatePoint(new Point(0,0),window).Y;
            for (var i=0;i<tabs.Items.Count;i++) { tabs.SelectedIndex=i; Drain(); Require(Math.Abs(tabs.TranslatePoint(new Point(0,0),window).Y-tabTop)<1,"Activity tab strip stays fixed: " + i); }
            tabs.SelectedIndex=0;
            var menu = WorkspaceVisuals.WorkspaceMenu();
            menu.Items.Add(new MenuItem { Header = "Before" }); menu.Items.Add(new Separator()); menu.Items.Add(new MenuItem { Header = "After" });
            menu.PlacementTarget=window; menu.IsOpen=true; Drain(); Require(menu.IsOpen && menu.Items.Count==3,"Themed context menu accepts separators without crashing"); menu.IsOpen=false;
            var label = new Button { Content="Left", HorizontalContentAlignment=HorizontalAlignment.Left };
            label.ApplyTemplate(); Require(label.Template != null,"Shared command template retains explicit content alignment");
            var rowState=window.QueueRows[0].State; Call(window,"SetQueueState","one","Completed"); Require(window.QueueRows[0].State==rowState,"Pair completion cannot overwrite an operation state");
            Call(window,"NavigateTo","Bootstrap",true); Drain(); Require(Field<Border>(window,"_attentionPage").Visibility==Visibility.Visible && Field<TabControl>(window,"_attentionTabs").SelectedIndex==1,"First Sync routes inside Needs Attention");
            Call(window,"NavigateTo","Conflicts",true); Drain(); Require(Field<TabControl>(window,"_attentionTabs").SelectedIndex==2,"Conflicts keep contextual tabs");
            Require(complete.FileFormat=="PDF" && complete.FileCategory=="Document" && complete.SizeBytes is null && complete.SizeLabel=="—","Type, format, category and unknown size remain distinct; absent size is not zero");
            Pump(window.SetSyncPauseAsync(TimeSpan.FromMinutes(5),false));
            var preferences=new UiPreferencesStore(appLock.StateAuthority);
            Require(window.GlobalPauseActive && preferences.Load().PauseUntilUtc>DateTimeOffset.UtcNow,"Timed pause is persistent");
            Pump(theme.SetPreferenceAsync(UiPreferencesStore.DarkTheme));
            Require(preferences.Load().PauseUntilUtc is not null,"Theme changes preserve pause and other preferences");
            Pump(window.SetSyncPauseAsync(null,false)); Require(!window.GlobalPauseActive && preferences.Load().PauseUntilUtc is null,"Resume clears only pause state");
            var expired = preferences.Load() with { PauseUntilUtc = DateTimeOffset.UtcNow.AddSeconds(-1) };
            Pump(preferences.SaveAsync(expired)); Set(window,"_workspacePreferences",expired);
            Pump((Task)typeof(MainWindow).GetMethod("CheckPauseDeadlineAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!);
            Require(preferences.Load().PauseUntilUtc is null && !window.GlobalPauseActive,"An elapsed persisted pause deadline is cleared");
            var layoutPasses=window.ResponsiveLayoutPasses;
            for(var i=0;i<100;i++) Call(window,"UpdateResponsiveLayout");
            Require(window.ResponsiveLayoutPasses==layoutPasses,"Repeated same-size refresh does not remeasure the pair editor");
            // Use the production tree template, with a large in-memory folder, without provider calls.
            // This fails if the template materializes every off-screen folder control.
            var tree = Field<TreeView>(window,"LocalRootTree");
            ((Grid)tree.Parent).Children.Remove(tree);
            tree.Visibility = Visibility.Visible;
            var largeFolder = LocalFolderNode.CreateForTest("C:\\VirtualizationFixture", Enumerable.Range(0, 10000).Select(i => "Folder-" + i));
            largeFolder.IsExpanded = true;
            tree.ItemsSource = new[] { largeFolder };
            var treeWindow = new Window { Content = tree, Width = 480, Height = 360 };
            var treeWatch = System.Diagnostics.Stopwatch.StartNew();
            treeWindow.Show(); Drain(); tree.UpdateLayout();
            var realized = CountVisual<TreeViewItem>(tree);
            Require(realized < 100, $"10,000-folder production tree realizes only visible controls ({realized})");
            Require(treeWatch.Elapsed < TimeSpan.FromSeconds(5),"Large folder renders without blocking navigation");
            var last = typeof(MainWindow).GetMethod("FindTreeItemContainer",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[tree,largeFolder.Children[^1]]);
            Require(last is TreeViewItem,"Saved folder can be revealed beyond virtualized siblings");
            Require(CountVisual<TreeViewItem>(tree)<100,"Revealing a distant folder keeps rendering bounded");
            treeWindow.Content=null; treeWindow.Close();
            var feed = Field<DataGrid>(window, "_homeFeed");
            Require(feed.RowHeight >= 64 && feed.Columns.Count == 3, "Two-line rows have room and a compact column set");
            Require(new ActivityRow("", "", "Superseded", "").StatusGlyph != "✓", "Unknown and superseded states are not labelled success");
            Require(new ActivityRow("", "file.txt", "Completed", "The download new operation completed.").HumanAction == "Downloaded to this computer", "Retained download direction is visible");
            Require(new ActivityRow("", "file.txt", "Completed", "The upload update operation completed.").HumanAction == "Updated in Cloud", "Retained upload direction is visible without inventing a missing historical provider");
            Require(new ActivityRow("", "file.txt", "Completed", "Unrecognized upload text").HumanAction == "Completed", "Unknown historical details are not guessed");
            var broken = new SyncPair("stale", "Stale", "C:\\missing", 1, 1, "/missing", "id:missing");
            var valid = broken with { Id = "valid", Name = "Valid" };
            Set(window, "_pairSet", new SyncPairSet([broken, valid], valid.Id));
            Call(window, "PresentBatchProgressAsync", new SyncAuthorityBatchProgress(broken, "Blocked", null, "IOException", "Missing folder"), "test", true); Drain();
            Require(window.ActivityRows.Single(row => row.Outcome == "Recovery blocked").PairId == broken.Id, "Background failure belongs to its source pair, not the selected pair");
            Call(window, "PresentBatchProgressAsync", new SyncAuthorityBatchProgress(valid, "Running", null, null, null), "test", true); Drain();
            Require(Field<string>(window, "_workspaceRunningPair") == valid.Name, "Next pair replaces stale running-pair label");
            Call(window, "PresentMonitorResults", (object)new SyncAuthorityMonitorStartResult[] { new(broken.Id, "Unavailable", "IOException", "Missing folder") });
            Drain();
            Require(window.ActivityRows.Single(row => row.Outcome == "Monitor blocked").PairId == broken.Id, "Monitor warning also belongs to its source pair");
            window.LoadPairSetForTest(new SyncPairSet([valid],valid.Id));
            MainWindow.PairHealthLoaderForTest = (_,_) => Task.FromException<SyncFolderSelectionTree>(new OperationCanceledException());
            var healthTask = (Task)typeof(MainWindow).GetMethod("EnsurePairHealthAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[window.PairRows[0],true])!;
            Pump(healthTask);
            Require(!window.PairKnownUnhealthyForTest(valid.Id),"A first health-check timeout does not invent a blocking folder failure");
            window.SetPairAttentionSourcesForTest(valid.Id,"Confirmed missing endpoint",null,null,false);
            Pump((Task)typeof(MainWindow).GetMethod("EnsurePairHealthAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[window.PairRows[0],true])!);
            Require(window.PairKnownUnhealthyForTest(valid.Id),"A timeout does not erase a previously confirmed endpoint failure");
            window.LoadPairSetForTest(new SyncPairSet([valid with { InitialReviewApproved = false }],valid.Id));
            window.SetPairAttentionSourcesForTest(valid.Id,"Confirmed missing endpoint",null,null,false);
            Call(window,"RefreshFullApp");
            var problems = (StackPanel)((TabItem)Field<TabControl>(window,"_attentionTabs").Items[3]).Content;
            Require(problems.Children.OfType<Border>().Any(), "A first-sync decision cannot hide a confirmed folder problem");
            Pump((Task)typeof(MainWindow).GetMethod("PresentBatchProgressAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,
                [new SyncAuthorityBatchProgress(valid,"Checking",null,null,null,Comparison:new SyncComparisonProgress("Reading Local Inventory","item.txt",3,0,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow)),"test",false])!);
            Require(Field<ProgressBar>(window,"RunProgress").IsIndeterminate,"Unknown inventory total remains indeterminate in the actual UI");
            MainWindow.PairHealthLoaderForTest = null;
            var oldRun = new SyncAuthorityRunHistory("old","sync",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,"Completed","{}","{\"pairs\":[{\"uploaded\":null}]}");
            var oldSummary=(string)typeof(MainWindow).GetMethod("RetainedRunSummary",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[oldRun])!;
            Require(oldSummary.Contains("Not recorded"),"Unknown historical run counts are not shown as zero or thrown");
            Require(oldSummary.Split("Not recorded").Length == 2 && oldSummary.Contains("Completed: ") && oldSummary.Contains("Status: "),
                "An entirely unknown old run shows one truthful counts note and labelled time/status");
            var activeRun = oldRun with { CompletedUtc = null, Status = "Executing", ResultJson = null };
            var activeSummary = (string)typeof(MainWindow).GetMethod("RetainedRunSummary",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[activeRun])!;
            Require(activeSummary.Contains("Started: ") && activeSummary.Contains("Status: Running"),
                "An unfinished run shows its actual start instead of a blank completion timestamp");
            CheckOwnershipAndPreviewPresentation(window, valid);
            var operationProjection = typeof(SyncAuthorityDatabase).GetMethod("ProjectionOperationName", BindingFlags.Static | BindingFlags.NonPublic)!;
            var actualOperationLabels = new[] { "create_remote", "update_remote", "create_local", "update_local",
                "delete_remote", "delete_local", "move_remote", "move_local", "cross_pair_move", "resolve_conflict", "cleanup", "reconcile" }
                .Select(kind => (string)operationProjection.Invoke(null, [kind])!).ToArray();
            Require(MainWindow.CountPreviewOperations(actualOperationLabels) == new PairPreviewCounts(2, 2, 2, 3, 3),
                "Preview categories count the actual database projection labels, including cross-pair moves");
            var previewState = new PairPreviewRow("Upload new", "example.txt", "Effect unknown");
            Require(previewState.StateLabel == "Verification Needed" && previewState.State == "Effect unknown",
                "Preview uses the same readable status as Pending and retains the recorded state for its tooltip");
            // ---- P-FEED: UI06, UI07 and UI09 ----------------------------------------------------
            // UI06: the activity table carries a real Time column before Size; the shown time is bare
            // with no At prefix; the full recorded date and time stay reachable for hover and for the
            // details surface; and the time never shares one metadata line with the folder pair.
            var activityHeaders = Field<DataGrid>(window, "ActivityGrid").Columns
                .Select(column => column.Header?.ToString()).ToList();
            Require(activityHeaders.IndexOf("Time") >= 0 && activityHeaders.IndexOf("Size") > activityHeaders.IndexOf("Time"),
                "The activity table puts its Time column before Size: " + string.Join(", ", activityHeaders));
            // H1: the Activity detailed view labels its connection column Endpoints, because
            // ActivityRow.Connection carries the endpoint label rather than an account identity. The
            // binding and every other column are unchanged, and Recovery keeps its own Account column.
            var activityDetailColumns = (FeedDetailColumn[])typeof(MainWindow)
                .GetField("ActivityDetailColumns", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var recoveryDetailColumns = (FeedDetailColumn[])typeof(MainWindow)
                .GetField("RecoveryDetailColumns", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Require(activityDetailColumns.Single(column => column.Path == nameof(ActivityRow.Connection)).Header == "Endpoints" &&
                    activityDetailColumns.All(column => column.Header != "Account"),
                "The Activity detailed view labels its connection column Endpoints and keeps no Account column: " +
                string.Join(", ", activityDetailColumns.Select(column => column.Header)));
            Require(recoveryDetailColumns.Single(column => column.Path == nameof(RecoveryRow.Connection)).Header == "Account",
                "The Recovery detailed view keeps its own Account column");
            var endpointGrid = new DataGrid { HeadersVisibility = DataGridHeadersVisibility.None };
            endpointGrid.Columns.Add(new DataGridTextColumn { Header = "Status" });
            SharedPresentation.ApplyFeedDetail(endpointGrid, true, activityDetailColumns);
            Require(endpointGrid.Columns.Single(column => column.Header?.ToString() == "Endpoints") is DataGridTextColumn
                    { Binding: System.Windows.Data.Binding { Path: { Path: "Connection" } } },
                "The shared detail mechanism still binds the Endpoints column to ActivityRow.Connection");
            Require(complete.TimeLabel.Length is > 0 and <= 8 &&
                    !complete.TimeLabel.Contains("At ", StringComparison.Ordinal) &&
                    complete.FullTimestamp.Length > complete.TimeLabel.Length &&
                    complete.TimeAccessibleLabel == "Recorded " + complete.FullTimestamp &&
                    complete.EventDateLabel.Contains('\n') &&
                    complete.Subtitle == complete.HumanAction + " · " + complete.PairName &&
                    !complete.Subtitle.Contains("At ", StringComparison.Ordinal),
                "Time renders bare and keeps the full recorded date and time for hover and details without sharing a line with the folder pair");

            // UI07 / D3: All plus one pressed control per category, defaulting to File activity, with
            // genuine multiselect, one appearance per event under All and the shared category
            // vocabulary rather than a second product-specific list.
            var categoryBar = Field<WrapPanel>(window, "_activityCategoryBar");
            var chips = categoryBar.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().ToList();
            var expectedCategories = new[] { "All" }.Concat(SyncActivityCategories.All).ToArray();
            Require(chips.Select(chip => chip.Content?.ToString()).SequenceEqual(expectedCategories),
                "The unified feed offers All plus every shared category as its own control, in the canonical order");
            Require(chips.Any(chip => chip.Content?.ToString() == SyncActivityCategories.Pairs),
                "Folder pairs is presented as its own category, separating pair setup from file activity");
            System.Windows.Controls.Primitives.ToggleButton Chip(string name) =>
                chips.Single(chip => chip.Content?.ToString() == name);
            Require(Chip(SyncActivityCategories.Files).IsChecked == true &&
                    chips.Where(chip => !ReferenceEquals(chip, Chip(SyncActivityCategories.Files)))
                        .All(chip => chip.IsChecked != true),
                "File activity is the default category and All starts unpressed");
            Require(chips.All(chip => !string.IsNullOrWhiteSpace(chip.ToolTip?.ToString()) &&
                !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetHelpText(chip))),
                "Every category control explains itself on hover and to a screen reader");
            Chip(SyncActivityCategories.Sync).IsChecked = true;
            Require(Chip(SyncActivityCategories.Files).IsChecked == true &&
                    Chip(SyncActivityCategories.Sync).IsChecked == true && Chip("All").IsChecked != true,
                "Category controls combine as a real multiselect");
            Chip("All").IsChecked = true;
            Require(chips.Where(chip => chip.Content?.ToString() != "All").All(chip => chip.IsChecked != true),
                "All clears the specific categories so one event is never presented twice");
            Chip(SyncActivityCategories.Pairs).IsChecked = true;
            Require(Chip("All").IsChecked != true && Chip(SyncActivityCategories.Pairs).IsChecked == true,
                "Choosing one category clears All");
            Chip(SyncActivityCategories.Pairs).IsChecked = false;
            Require(Chip("All").IsChecked == true, "Clearing the last category returns to All rather than to an empty list");

            // UI07: severity, entity and run are separate controls from the categories and from each
            // other; the shared date, account, pair, type and text filters stay untouched.
            var severityBox = Field<ComboBox>(window, "_activitySeverityBox");
            var entityBox = Field<ComboBox>(window, "_activityEntityBox");
            var runBox = Field<TextBox>(window, "_activityRunBox");
            Require(severityBox.SelectedIndex == 0 && entityBox.SelectedIndex == 0 && runBox.Text.Length == 0 &&
                    Query<string>(window, "CurrentActivitySeverity").Length == 0 &&
                    Query<string>(window, "CurrentActivityEntity").Length == 0 &&
                    Query<string>(window, "CurrentActivityRun").Length == 0,
                "Severity, entity and run start unfiltered and independent of the selected category");
            Require(severityBox.Items.OfType<ComboBoxItem>().Select(item => item.Content?.ToString()).SequenceEqual(
                        new[] { "All severities", "Information", "Success", "Warning", "Error" }) &&
                    entityBox.Items.OfType<ComboBoxItem>().Select(item => item.Content?.ToString()).SequenceEqual(
                        new[] { "All entities", "File", "Folder", "Folder pair", "Run", "Account", "Application" }),
                "Severity and entity offer the shared vocabulary including their All value");
            entityBox.SelectedIndex = 2; runBox.Text = "run-a";
            Require(Query<string>(window, "CurrentActivityEntity") == "Folder" &&
                    Query<string>(window, "CurrentActivityRun") == "run-a" &&
                    Query<string>(window, "CurrentActivitySeverity").Length == 0 && chips[0].IsChecked == true,
                "Severity, entity and run filter independently without disturbing the selected category");
            entityBox.SelectedIndex = 0; runBox.Text = string.Empty;

            // UI08 / 6.1: the Sync log reads as a chronology. Selecting it alone orders oldest first so
            // a run reads start, plan, operations and terminal row; including File activity returns to
            // the newest-first activity order.
            chips[2].IsChecked = true; chips[0].IsChecked = false;
            Require(Query<bool>(window, "ActivityReadsOldestFirst"),
                "The Sync log reads oldest first so a run's stages appear in the order they happened");
            chips[1].IsChecked = true;
            Require(!Query<bool>(window, "ActivityReadsOldestFirst"),
                "Adding File activity returns to the newest-first activity order");
            chips[2].IsChecked = false; chips[1].IsChecked = false;
            Require(chips[0].IsChecked == true, "The unified list returns to All for the remaining checks");

            // UI07: deduplication is by stable event identity only. Two stages that share one
            // operation id stay separate, an exact repeat of one event identity is dropped, and the
            // no-change gate is the only thing that hides a routine check.
            var activityRows = window.ActivityRows;
            var recentRows = Field<System.Collections.ObjectModel.ObservableCollection<ActivityRow>>(window, "_recentRows");
            // The category/filter controls above also start asynchronous loads. Complete a newer
            // request before seeding this independent projection assertion, preserving real loading.
            Pump(window.LoadActivityPageForTestAsync(true));
            activityRows.Clear();
            var starting = new ActivityRow("2026-09-13 12:00:00 +00:00", "manual.pdf", "Starting upload", "Sending file",
                PairId: "one", PairName: "One", OperationId: "same", ItemKind: "File", FileType: "PDF", EventId: "evt-1");
            var finished = starting with { When = "2026-09-13 12:00:01 +00:00", Outcome = "Completed upload", EventId = "evt-2" };
            var repeat = finished with { };
            var routine = starting with { OperationId = null, Outcome = "Unchanged", EventId = "evt-3" };
            var actionBox = Field<ComboBox>(window, "ActivityFilterBox");
            var queueActionBox = Field<ComboBox>(window, "QueueFilterBox");
            foreach (var box in new[] { actionBox, queueActionBox })
                Require(box.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "Transfers"))
                        .Content?.ToString() == "Uploads / downloads",
                    "Recent and Pending describe both directions while preserving the shared Transfers query key");
            actionBox.SelectedItem = actionBox.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "Transfers"));
            Require(Query<bool>(window, "ActivityRowMatchesCurrentFilter", starting) &&
                    Query<bool>(window, "ActivityRowMatchesCurrentFilter", starting with { Outcome = "Completed download" }) &&
                    !Query<bool>(window, "ActivityRowMatchesCurrentFilter", routine),
                "The actual live filter accepts uploads and downloads but excludes unchanged checks");
            actionBox.SelectedIndex = 0;
            Pump(window.LoadActivityPageForTestAsync(true));
            activityRows.Clear();
            activityRows.Add(starting); activityRows.Add(finished); activityRows.Add(repeat); activityRows.Add(routine);
            Call(window, "RefreshWorkspaceFeeds");
            Require(recentRows.Count == 2 && recentRows[0].Outcome == "Completed upload" && recentRows[1].Outcome == "Starting upload",
                "Unsorted input is displayed newest first; distinct stages are kept and only exact repeated event identities are dropped");
            Set(window, "_includeRoutineChecks", true); Call(window, "RefreshWorkspaceFeeds");
            Require(recentRows.Count == 3 && recentRows.Any(row => row.IsRoutineCheck),
                "The no-change gate reveals the retained routine check when it is pressed");
            Set(window, "_includeRoutineChecks", false); Call(window, "RefreshWorkspaceFeeds");
            Require(recentRows.Count == 2 && recentRows.All(row => !row.IsRoutineCheck),
                "A routine check stays out of the list unless the no-change control is pressed");

            // UI09: the no-change control is a styled pressed toggle with its own explanation, never
            // a native checkbox, and the retained checks are driven by it.
            var noChanges = Field<System.Windows.Controls.Primitives.ToggleButton>(window, "_activityNoChangesToggle");
            Require(noChanges is not System.Windows.Controls.CheckBox && noChanges.Focusable && noChanges.IsThreeState == false &&
                    noChanges.Template is not null && !string.IsNullOrWhiteSpace(noChanges.ToolTip?.ToString()) &&
                    !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetHelpText(noChanges)),
                "Show no-change checks is a styled pressed toggle that carries its own explanation");
            Require(noChanges.IsChecked == false, "The no-change control starts unpressed");
            noChanges.IsChecked = true;
            Require(RoutineChecksIncluded(window), "Pressing the no-change control includes routine checks");
            noChanges.IsChecked = false;
            Require(!RoutineChecksIncluded(window), "Unpressing the no-change control hides routine checks again");
            Pump(window.LoadActivityPageForTestAsync(true));
            // Checked/Unchecked handlers above each start a real asynchronous read. The newest
            // request finishing does not mean older requests have left their finally blocks.
            // Wait for those readers, rather than overriding the production busy state, before
            // asserting the independently seeded finished-page projection below.
            SettleActivityReaders(window);
            CheckOutstandingActivityReader(window);
            activityRows.Clear(); Call(window, "RefreshWorkspaceFeeds");

            // UI07 / UI09: the count describes only the loaded scope and never invents a total, and a
            // producer that could not retain an event is named instead of being hidden.
            Set(window, "_activityHasMore", true); Set(window, "_activityLoadFailed", false); Set(window, "_activityLoadFailure", null);
            var partialCount = Query<string>(window, "ActivityCountText");
            Require(partialCount.Contains("more available", StringComparison.Ordinal) &&
                    !partialCount.Contains(" of ", StringComparison.Ordinal),
                "A partially loaded list says more are available and states no invented total: " + partialCount);
            Set(window, "_activityHasMore", false);
            Require(Query<string>(window, "ActivityCountText").Contains("all retained matches loaded", StringComparison.Ordinal),
                "A fully loaded list states that it is the complete matching retained set");
            var authorityHost = Field<SyncAuthorityRuntimeHost>(window, "_authorityHost");
            var incompleteFlag = typeof(SyncAuthorityRuntimeHost).GetProperty("ActivityLogIncomplete")!;
            incompleteFlag.GetSetMethod(true)!.Invoke(authorityHost, [true]);
            Require(Query<string>(window, "ActivityIncompleteNote").Contains("incomplete", StringComparison.Ordinal) &&
                    Query<string>(window, "ActivityCountText").Contains("incomplete", StringComparison.Ordinal),
                "An incomplete producer log is named in the unified feed instead of being hidden");
            incompleteFlag.GetSetMethod(true)!.Invoke(authorityHost, [false]);
            Require(!Query<string>(window, "ActivityCountText").Contains("incomplete", StringComparison.Ordinal),
                "A complete producer log carries no gap note");

            // UI09: Refresh gives real busy, completed, unavailable and failed feedback through its own
            // note, never through the shared run status, and a superseded response cannot be applied.
            var refreshButton = Field<Button>(window, "_activityRefresh");
            var refreshNote = Field<TextBlock>(window, "_activityRefreshNote");
            MainWindow.ActivityProjectionLoaderForTest = (_, _, _, _, _, _, _, _) =>
                Task.FromException<IReadOnlyList<SyncAuthorityActivityProjection>>(new IOException("fixture activity load refused"));
            refreshButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            for (var guard = 0; guard < 500 && !refreshButton.IsEnabled; guard++) Drain();
            Require(refreshButton.IsEnabled && refreshNote.Text.Length > 0 &&
                    !Field<TextBlock>(window, "PhaseText").Text.Contains("Refreshing", StringComparison.Ordinal) &&
                    !Field<TextBlock>(window, "PhaseText").Text.Contains("activity list", StringComparison.Ordinal),
                "Refresh reports its own state and never writes refresh feedback into the shared run status");
            Require(refreshNote.Text.StartsWith("Refreshed at ", StringComparison.Ordinal) ||
                    refreshNote.Text.StartsWith("Refresh failed", StringComparison.Ordinal) ||
                    refreshNote.Text.StartsWith("There is no saved sync history", StringComparison.Ordinal),
                "Refresh states a real completed, failed or unavailable outcome: " + refreshNote.Text);
            MainWindow.ActivityProjectionLoaderForTest = null;
            Console.WriteLine("PASS focused workspace UI checks; no provider or installed-state effects");
            return 0;
        }
        finally { MainWindow.PairHealthLoaderForTest = null; MainWindow.MonitorConfiguratorForTest = null; Set(window, "_uiRunActive", false); window.RequestProductExit(); MainWindow.SuppressLoadedStartupForTest = false; app.Shutdown(); }
    }

    private static void CheckSummarySelection(MainWindow window, ActivityRow sample)
    {
        var file = sample with { Outcome = "Uploaded", When = "2026-09-22T22:26:11Z",
            Categories = [SyncActivityCategories.Files], ConnectionId = "summary-account-a", EventId = "summary-file" };
        var log = file with { When = "2026-09-22T22:26:12Z", Categories = [SyncActivityCategories.Sync],
            Outcome = "Upload — Completed", EventId = "summary-log" };
        var other = file with { ConnectionId = "summary-account-b", OperationId = "other", EventId = "summary-other" };
        var unchanged = file with { Outcome = "Unchanged", OperationId = "unchanged", EventId = "summary-unchanged" };
        var source = new[] { log, other, unchanged, file, file };
        var files = SharedPresentation.SummaryRows(source, "summary-account-a", SummaryActivityFilter.Files, 5);
        Require(files.Length == 1 && ReferenceEquals(files[0], file),
            "A newer same-operation log never hides the file; duplicates, foreign accounts and no-change checks stay excluded");
        Require(SharedPresentation.SummaryRows(source, "summary-account-a", SummaryActivityFilter.Sync, 12)
            .SequenceEqual([log]), "Sync summaries select the log from the same operation");
        Require(SharedPresentation.SummaryRows(source, "summary-account-a", SummaryActivityFilter.All, 12)
            .SequenceEqual([log]), "All summaries retain the newest eligible event per operation");
        var many = Enumerable.Range(0, 20).Select(i => file with { OperationId = "summary-" + i,
            EventId = "summary-" + i }).ToArray();
        Require(SharedPresentation.SummaryRows(many, null, SummaryActivityFilter.Files, 5).Length == 5 &&
            SharedPresentation.SummaryRows(many, null, SummaryActivityFilter.Files, 12).Length == 12,
            "Shared Home and tray selection preserves their five/twelve bounds without modifying source history");
        Require(source.Length == 5 && many.Length == 20, "Summary projection never mutates full history");
        var oldRows = window.ActivityRows.ToArray();
        Call(window, "TrackWorkspaceAction", file); Call(window, "TrackWorkspaceAction", log);
        Call(window, "RefreshWorkspaceFeeds"); Drain();
        var home = Field<System.Collections.ObjectModel.ObservableCollection<ActivityRow>>(window, "_homeRows");
        Require(home.Contains(file) && !home.Contains(log) &&
            SharedPresentation.SummaryRows(window.TrayActivity, null, SummaryActivityFilter.Files, 12).Contains(file),
            "Real Home history and tray source retain the uploaded row after the newer sync event");
        Require(window.ActivityRows.SequenceEqual(oldRows), "Home refresh leaves the complete Activity collection unchanged");
    }

    private static void CheckOwnershipAndPreviewPresentation(MainWindow window, SyncPair pair)
    {
        Set(window, "_uiRunActive", false);
        window.PairOwnershipLoaderForTest = _ => Task.FromResult<IReadOnlyList<ExplorerIntegration.CompetingRoot>>([]);
        window.LoadPairSetForTest(new SyncPairSet([pair], pair.Id)); Drain();
        var list = Field<DataGrid>(window, "PairList");
        list.SelectedItem = window.PairRows.Single(row => row.Id == pair.Id); Drain();
        var actions = Field<WrapPanel>(window, "PairOwnershipActions");
        var text = Field<TextBlock>(window, "PairOwnershipText");
        var method = typeof(MainWindow).GetMethod("RefreshPairOwnershipAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var requests = new Queue<TaskCompletionSource<IReadOnlyList<ExplorerIntegration.CompetingRoot>>>();
        window.PairOwnershipLoaderForTest = _ => requests.Count > 0 ? requests.Dequeue().Task
            : Task.FromResult<IReadOnlyList<ExplorerIntegration.CompetingRoot>>([]);
        TaskCompletionSource<IReadOnlyList<ExplorerIntegration.CompetingRoot>> Enqueue()
        {
            var request = new TaskCompletionSource<IReadOnlyList<ExplorerIntegration.CompetingRoot>>(TaskCreationOptions.RunContinuationsAsynchronously);
            requests.Enqueue(request); return request;
        }
        Task Refresh() => (Task)method.Invoke(window, [pair])!;
        try
        {
            var old = Enqueue(); var current = Enqueue();
            var oldTask = Refresh(); var currentTask = Refresh();
            current.SetResult([new("current", pair.LocalRoot, "Current Provider", true)]); Pump(currentTask);
            old.SetResult([new("old", pair.LocalRoot, "Stale Provider", true)]); Pump(oldTask);
            Require(actions.Children.Count == 1 && text.Text.StartsWith("Current Provider") && !text.Text.Contains("Stale"),
                "Overlapping ownership refreshes produce one current action, not duplicate or stale actions");
            var failing = Enqueue(); var newer = Enqueue();
            var failingTask = Refresh(); var newerTask = Refresh();
            newer.SetResult([new("newer", pair.LocalRoot, "Newest Provider", true)]); Pump(newerTask);
            failing.SetException(new IOException("stale lookup failed")); Pump(failingTask);
            Require(actions.Children.Count == 1 && text.Text.StartsWith("Newest Provider"),
                "An older ownership error cannot overwrite a newer successful result");
            var changed = Enqueue(); var changedTask = Refresh(); list.SelectedItem = null; Drain();
            var before = text.Text; var beforeActions = actions.Children.Count;
            changed.SetResult([new("other", pair.LocalRoot, "Wrong Selection", true)]); Pump(changedTask);
            Require(text.Text == before && actions.Children.Count == beforeActions,
                "An ownership result for a deselected pair cannot change the panel");
            list.SelectedItem = window.PairRows.Single(row => row.Id == pair.Id); Drain();
            var failure = Enqueue(); var failureTask = Refresh();
            failure.SetException(new IOException("bounded lookup failure")); Pump(failureTask);
            Require(actions.Children.Count == 0 && text.Text.Contains("bounded lookup failure"),
                "A current ownership failure removes stale actions and explains the failure");
            Call(window, "PresentPairPreviewTotals", new PairPreviewCounts(2, 3, 4, 5, 6), 7, 8L, 9L);
            var totals = Field<WrapPanel>(window, "PairPreviewTotals");
            var expected = new[] { ("Uploads", "2"), ("Downloads", "3"), ("Recoverable Deletions", "4"),
                ("Moves", "5"), ("Conflicts", "7"), ("Excluded Items", "8"), ("Online-Only Items", "9"), ("Other Changes", "6") };
            Require(totals.Children.OfType<StackPanel>().Select(panel =>
                    (((TextBlock)panel.Children[0]).Text, ((TextBlock)panel.Children[1]).Text)).SequenceEqual(expected),
                "Preview preserves every nonzero count with its own label instead of dot packing");
            Call(window, "PresentPairPreviewTotals", new PairPreviewCounts(0, 0, 0, 0, 0), 0, 0L, 0L);
            Require(totals.Children.Count == 8 && totals.Children.OfType<StackPanel>().All(panel => ((TextBlock)panel.Children[1]).Text == "0"),
                "Refreshing empty Preview replaces previous counts rather than retaining stale values");
        }
        finally { window.PairOwnershipLoaderForTest = null; }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;
    private static T Query<T>(object target, string name, params object[] args) =>
        (T)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args)!;
    private static bool RoutineChecksIncluded(MainWindow window) => Field<bool>(window, "_includeRoutineChecks");
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void CheckOutstandingActivityReader(MainWindow window)
    {
        var old = new TaskCompletionSource<IReadOnlyList<SyncAuthorityActivityProjection>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new TaskCompletionSource<IReadOnlyList<SyncAuthorityActivityProjection>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        MainWindow.ActivityProjectionLoaderForTest = (_, _, _, _, _, _, _, _) =>
            ++calls == 1 ? old.Task : current.Task;
        try
        {
            var oldRead = window.LoadActivityPageForTestAsync(true);
            var currentRead = window.LoadActivityPageForTestAsync(true);
            current.SetResult([]); Pump(currentRead);
            Require(Field<int>(window, "_activityPageLoads") == 1 &&
                    window.ActivityCountText().StartsWith("Loading retained activity", StringComparison.Ordinal),
                "A completed latest request does not pretend an older activity reader has finished");
            Dispatcher.CurrentDispatcher.BeginInvoke(() => old.SetResult([]), DispatcherPriority.Background);
            SettleActivityReaders(window); Pump(oldRead);
            Require(Field<int>(window, "_activityPageLoads") == 0 &&
                    !window.ActivityCountText().StartsWith("Loading retained activity", StringComparison.Ordinal),
                "Finished-page assertions start only after every actual activity reader has completed");
        }
        finally
        {
            old.TrySetResult([]); current.TrySetResult([]);
            MainWindow.ActivityProjectionLoaderForTest = null;
        }
    }
    private static void SettleActivityReaders(MainWindow window)
    {
        var started = Environment.TickCount64;
        var initial = Field<int>(window, "_activityPageLoads");
        while (Field<int>(window, "_activityPageLoads") > 0)
        {
            Drain(); Thread.Sleep(1);
            if (Environment.TickCount64 - started > 20000)
                throw new InvalidOperationException("Outstanding activity readers did not finish: " +
                    Field<int>(window, "_activityPageLoads") + "; " + window.ActivityCountText());
        }
        Console.WriteLine("Activity finished-page ordering: initialReaders=" + initial +
            "; remainingReaders=" + Field<int>(window, "_activityPageLoads"));
    }
    private static void Pump(Task task) { while(!task.IsCompleted) { Drain(); Thread.Sleep(1); } task.GetAwaiter().GetResult(); }
    private static void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }

    // The visible caption of a shared command button, read from the real element the production helper built.
    private static string DisplayedCaption(Button button) =>
        button.Content is StackPanel panel && panel.Children.OfType<TextBlock>().SingleOrDefault() is { } caption
            ? caption.Text : string.Empty;
    private static int CountVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        var count=parent is T ? 1 : 0;
        for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++) count+=CountVisual<T>(System.Windows.Media.VisualTreeHelper.GetChild(parent,i));
        return count;
    }
}
