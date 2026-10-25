using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static partial class Program
{
    private static int RunWindowCloseContract()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-shark-ui-inspection", Guid.NewGuid().ToString("N"));
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        var window = new MainWindow(instance.StateAuthority, theme) { Width = 900, Height = 650, ShowActivated = false };
        Exception? failure = null;
        var closed = false;
        var checkpointCalls = 0;
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        timeout.Tick += (_, _) => { failure = new TimeoutException("Window close did not complete."); timeout.Stop(); window.RequestProductExit(); app.Shutdown(); };
        window.Closed += (_, _) => closed = true;
        window.Loaded += async (_, _) =>
        {
            try
            {
                window.Close();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                if (window.IsVisible || !window.IsLoaded)
                    throw new InvalidDataException("Close-to-tray did not retain a hidden runtime.");
                window.ShowFromBackground();
                MainWindow.ExitCheckpointWaitForTest = (_, _) => Task.FromResult(false);
                try { await window.PrepareProductExitAsync(); throw new InvalidDataException("Failed checkpoint allowed exit."); }
                catch (TimeoutException) { }
                if (!window.IsVisible || !window.IsLoaded)
                    throw new InvalidDataException("Failed checkpoint closed the app.");
                typeof(MainWindow).GetField("_workspacePreferences", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(window, new UiPreferences(UiPreferencesStore.SystemTheme, KeepRunningWhenClosed: false));
                MainWindow.ExitCheckpointWaitForTest = (_, _) => { checkpointCalls++; return Task.FromResult(true); };
                // Exercise the real WPF close event, including duplicate requests before
                // the dispatcher returns. Only checkpoint input is synthetic.
                window.Close();
                window.Close();
            }
            catch (Exception error) { failure = error; window.RequestProductExit(); app.Shutdown(); }
        };
        try
        {
            timeout.Start(); app.Run(window);
            if (failure is not null) throw failure;
            if (!closed || checkpointCalls != 1)
                throw new InvalidDataException("Native close was not checkpointed exactly once.");
            Console.WriteLine("PASS: real WPF close-to-tray/reopen, checkpoint failure retention and queued duplicate close with synchronous successful checkpoint.");
            return 0;
        }
        finally { timeout.Stop(); MainWindow.ExitCheckpointWaitForTest = null; MainWindow.SuppressLoadedStartupForTest = false; }
    }

    private static int RunVisualCorrectionsContract()
    {
        if (!MainWindow.UpdateVersionIdentity.Contains('+'))
            throw new InvalidDataException("Update compatibility must retain the exact installed source identity.");
        foreach (var (label, expected) in new[]
                 { ("Discard Changes", "delete"), ("Keep editing", "back"), ("Share & Manage Links", "link"), ("Use Recommended", "check"),
                   ("Choose…", "folder"), ("Review Cleanup", "history"), ("Open in File Explorer", "folder"),
                   ("Save Changes", "save"), ("Sync Now", "sync"), ("Pause Sync", "pause"), ("Cancel Check", "close"),
                   ("Open Tray", "window"), ("Review Conflicts", "attention"), ("Scan Again", "sync"),
                   ("Reconnect", "sync"), ("Disconnect", "close"), ("Prepare for Removal", "shield"),
                   ("Use Local", "computer"), ("Keep Both", "copy"), ("Compare", "search"),
                   ("Select All Matching", "check"), ("Load Revisions", "history"),
                   ("Make Available Offline", "offline"), ("Make Online Only", "cloud"),
                   ("Change Storage", "storage"), ("Manage", "settings"), ("Use Parent Setting", "settings"),
                   ("Search All Items", "search"), ("Change Access", "shield"), ("Up One Folder", "up"),
                   ("Export Report", "down"), ("Review First Sync", "document"),
                   ("Review the First Sync", "document"), ("Browse", "folder"), ("Browse…", "folder") })
            if (CommandIcons.ForLabel(label) != expected) throw new InvalidDataException("Wrong command icon: " + label);
        foreach (var mode in Enum.GetValues<SyncMode>())
        {
            var description = MainWindow.DeletionDirectionDescription(mode);
            var remote = SyncModePolicy.Allows(mode, SyncAuthorityOperationKind.DeleteRemote);
            var local = SyncModePolicy.Allows(mode, SyncAuthorityOperationKind.DeleteLocal);
            if (mode == SyncMode.UploadOnly && (!remote || local || !description.StartsWith("Apply local deletions to Dropbox")) ||
                mode == SyncMode.DownloadOnly && (remote || !local || !description.StartsWith("Apply Dropbox deletions to this computer")) ||
                mode == SyncMode.TwoWay && (!remote || !local || !description.Contains("both directions")))
                throw new InvalidDataException("Deletion description contradicts engine direction: " + mode);
        }
        var sample = new ActivityRow("2026-09-14T12:34:56-06:00", "cloud test.txt", "Completed",
            "The archive local operation completed.", ItemKind:"File", FileType:"TXT", SizeBytes:1024);
        if (sample.HumanAction.Contains('·') || !sample.EventDateLabel.Contains('\n') ||
            sample.EventDateLabel.Contains("-06:00") || sample.SizeLabel != "1 KB" || sample.FileCategory != "Document")
            throw new InvalidDataException("Structured event fields lost detail or retained dot/offset packing.");
        var pending = new QueueRow("pair", sample.When, "Pair A", "cloud test.txt", 20,
            "Upload update", "Effect unknown · recovery first");
        if (pending.ShortAction.Contains('·') || pending.StateLabel != "Verification Needed" ||
            pending.ShortAction != "Action: Upload update\nStatus: Verification Needed" ||
            pending.State != "Effect unknown · recovery first")
            throw new InvalidDataException("Pending presentation lost the original state or hid its action/status labels.");
        foreach (var (state, label) in new[]
                 { ("Effect unknown (Recovery First)", "Verification Needed"),
                   ("Effect issuing (Recovery First)", "Applying the Change"),
                   ("Verifying (Recovery First)", "Verifying the Result"),
                   ("Cleanup pending (Recovery First)", "Finishing Up") })
        {
            var actualProjection = pending with { State = state };
            if (actualProjection.StateLabel != label || actualProjection.State != state)
                throw new InvalidDataException("Real queue projection recovery label lost its status or raw detail: " + state);
        }
        CheckWorkspacePresentationState();
        Console.WriteLine("PASS: command mappings, deletion direction against actual engine policy, structured metadata, actual themed picker containers, tab filter persistence and shared attention cases. Brief isolated WPF windows; no provider calls.");
        return 0;
    }

    private static void CheckWorkspacePresentationState()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-shark-ui-inspection", Guid.NewGuid().ToString("N"));
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        var window = new MainWindow(instance.StateAuthority, theme);
        try
        {
            static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
            {
                yield return parent;
                for (var child = 0; child < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); child++)
                    foreach (var value in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(parent, child))) yield return value;
            }
            foreach (var compact in new[] { false, true })
            {
                var item = new ActivityRow("2026-09-14T12:34:56Z", "a long but readable document.txt", "Downloaded", "Completed",
                    PairName: "Pair A", ItemKind: "File");
                var content = (FrameworkElement)WorkspaceVisuals.ItemTemplate(compact).LoadContent();
                content.DataContext = item;
                content.Measure(new Size(340, 72)); content.Arrange(new Rect(0, 0, 340, 72)); content.UpdateLayout();
                // Time is a full-height trailing column, not a child of the bottom metadata row.
                // Long names cannot push it to the floor; exact time remains accessible.
                var time = Descendants(content).OfType<System.Windows.Controls.TextBlock>().Single(block => block.Text == item.TimeLabel);
                if (time.ToolTip as string != item.FullTimestamp ||
                    System.Windows.Automation.AutomationProperties.GetName(time) != item.TimeAccessibleLabel ||
                    System.Windows.Automation.AutomationProperties.GetHelpText(time) != item.FullTimestamp)
                    throw new InvalidDataException("Feed timestamp must retain the full recorded date and time on hover and for screen readers.");
                if (time.Parent is not System.Windows.Controls.DockPanel row || !ReferenceEquals(row, content) ||
                    System.Windows.Controls.DockPanel.GetDock(time) != System.Windows.Controls.Dock.Right)
                    throw new InvalidDataException("Feed time must be a distinct full-height trailing column outside the metadata row.");
                var timeCenter = time.TranslatePoint(new Point(0, time.ActualHeight / 2), content).Y;
                var icon = Descendants(content).OfType<ShellTypeImage>().Single();
                var iconCenter = icon.TranslatePoint(new Point(0, icon.ActualHeight / 2), content).Y;
                if (Math.Abs(timeCenter - iconCenter) > 1 ||
                    Descendants(content).OfType<System.Windows.Controls.Button>().Count(button => Equals(button.Tag, "pair")) != 1)
                    throw new InvalidDataException("Feed time and item icon must share the row center while the pair link remains intact.");
                var name = Descendants(content).OfType<System.Windows.Controls.TextBlock>().Single(block => block.Text == item.DisplayName);
                if (System.Windows.Controls.Grid.GetRow(name) != 0 || ReferenceEquals(time.Parent, name.Parent))
                    throw new InvalidDataException("Feed timestamp must not compete with the filename.");
            }
            var internalRow = new ActivityRow("", "<run>", "Failed", "", Entity: "Run");
            var branded = new ShellTypeImage { DataContext = internalRow };
            var iconWindow = new Window { Width = 140, Height = 140, Content = branded, ShowInTaskbar = false };
            try
            {
                iconWindow.Show(); iconWindow.UpdateLayout();
                foreach (var style in new[] { "Colour", "Symbol", "Combined" })
                {
                    BrandedItemImages.ApplyStyle(style);
                    iconWindow.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
                    iconWindow.UpdateLayout();
                    if (branded.Source is not System.Windows.Media.DrawingImage image ||
                        !ReferenceEquals(image, BrandedItemImages.For(internalRow, style)) || image.Width != 100 || image.Height != 100)
                        throw new InvalidDataException("Internal icons must update through the shared style resource with fixed vector bounds.");
                }
            }
            finally { iconWindow.Close(); }
            var dangerousDialog = new AppDialogWindow("Unsaved Changes", "Discard edits?", "", "Discard Changes", "Keep editing", true);
            var dialogPrimary = (System.Windows.Controls.Button)dangerousDialog.FindName("PrimaryButton");
            var dialogSecondary = (System.Windows.Controls.Button)dangerousDialog.FindName("SecondaryButton");
            if (dialogPrimary.IsDefault || !dialogSecondary.IsDefault ||
                CommandIcons.ForLabel("Discard Changes") != "delete" || CommandIcons.ForLabel("Keep editing") != "back")
                throw new InvalidDataException("Discard must use the bin and keep editing must own the safe default.");
            dangerousDialog.Close();
            var previousTheme = theme.Preference;
            var scrollContent = new System.Windows.Controls.Border { Width = 1000, Height = 1000 };
            var scroll = new System.Windows.Controls.ScrollViewer { Content = scrollContent,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
            var scrollWindow = new Window { Width = 320, Height = 240, Content = scroll };
            try
            {
                scrollWindow.Show(); scrollWindow.UpdateLayout();
                if (!WorkspaceInput.ScrollFrom(scrollContent, 15, horizontal: true))
                    throw new InvalidDataException("Fractional horizontal wheel input was not routed.");
                scrollWindow.UpdateLayout();
                if (scroll.HorizontalOffset <= 0 || scroll.VerticalOffset != 0)
                    throw new InvalidDataException("Horizontal input did not move only the horizontal offset.");
                if (!WorkspaceInput.ScrollFrom(scrollContent, -15, horizontal: true))
                    throw new InvalidDataException("Reverse horizontal wheel input was not routed.");
                scrollWindow.UpdateLayout();
                if (scroll.HorizontalOffset != 0 || WorkspaceInput.ScrollFrom(scrollContent, -120, horizontal: true))
                    throw new InvalidDataException("Horizontal boundary must chain instead of consuming input.");
                if (!WorkspaceInput.ScrollFrom(scrollContent, -15, horizontal: false))
                    throw new InvalidDataException("Fractional vertical wheel input regressed.");
                scrollWindow.UpdateLayout();
                if (scroll.VerticalOffset <= 0 || scroll.HorizontalOffset != 0)
                    throw new InvalidDataException("Vertical input changed the wrong axis.");
                foreach (var treeName in new[] { "LocalRootTree", "DropboxRootTree" })
                {
                    var tree = (System.Windows.Controls.TreeView)window.FindName(treeName);
                    var row = (FrameworkElement)tree.ItemTemplate.LoadContent();
                    row.DataContext = new { Name = "Outside Pair", StorageChoice = (FolderStorageChoice?)null };
                    scroll.Content = row; scrollWindow.UpdateLayout();
                    var storageButton = ((System.Windows.Controls.Grid)row).Children.OfType<System.Windows.Controls.Button>().Single();
                    if (storageButton.Visibility != Visibility.Collapsed)
                        throw new InvalidDataException("A folder without an availability action must not show an inert info icon: " + treeName);
                    row.DataContext = new { Name = "Inside Pair", StorageChoice = new FolderStorageChoice("test", "folder", true, true, true) };
                    scrollWindow.UpdateLayout();
                    if (storageButton.Visibility != Visibility.Visible)
                        throw new InvalidDataException("A real in-pair availability action was hidden: " + treeName);
                }
            }
            finally { scrollWindow.Close(); }
            static bool HasThemeColour(System.Windows.Media.Brush actual, string resource) =>
                actual is System.Windows.Media.SolidColorBrush colour && System.Windows.Application.Current.FindResource(resource) is System.Windows.Media.SolidColorBrush expected && colour.Color == expected.Color;
            foreach (var mode in new[] { UiPreferencesStore.LightTheme, UiPreferencesStore.DarkTheme, UiPreferencesStore.AmoledTheme })
            {
                theme.PreviewPreference(mode);
                var toggle = new System.Windows.Controls.CheckBox { Style = (Style)app.FindResource("PairToggle"), IsChecked = false };
                var toggleHost = new Window { Content = toggle, Width = 120, Height = 100, ShowInTaskbar = false, ShowActivated = false };
                try
                {
                    toggleHost.Show(); toggleHost.UpdateLayout();
                    var thumb = (System.Windows.Shapes.Ellipse)toggle.Template.FindName("Thumb", toggle);
                    var track = (System.Windows.Controls.Border)toggle.Template.FindName("Track", toggle);
                    if (!HasThemeColour(thumb.Fill, "Ink") ||
                        ((System.Windows.Media.SolidColorBrush)thumb.Fill).Color == ((System.Windows.Media.SolidColorBrush)track.Background).Color)
                        throw new InvalidDataException("Off-switch thumb must remain visible in " + mode);
                    toggle.IsChecked = true; toggleHost.UpdateLayout();
                    if (((System.Windows.Media.SolidColorBrush)thumb.Fill).Color != System.Windows.Media.Colors.White ||
                        !HasThemeColour(track.Background, "DropboxBlue") || thumb.HorizontalAlignment != System.Windows.HorizontalAlignment.Right)
                        throw new InvalidDataException("On-switch contrast or placement regressed in " + mode);
                }
                finally { toggleHost.Close(); }
                // A modal picker is created after theme selection, as in the actual UI.
                // A detached control does not receive application resource invalidations.
                var browser = MainWindow.BuildAccountBrowserList();
                browser.ItemsSource = new[] { new AccountSharingItem("fixture", "personal", "id:folder", "/Documents", "Documents", "folder") };
                var host = new Window { Content = browser, Width = 640, Height = 380, ShowActivated = false, ShowInTaskbar = false, Title = "Sync Shark isolated picker check" };
                try
                {
                    host.Show(); host.UpdateLayout();
                    if (!HasThemeColour(browser.Background, "ControlSurface") || !HasThemeColour(browser.Foreground, "Ink"))
                        throw new InvalidDataException("Dropbox picker did not adopt " + mode + " colours: background=" + browser.Background + ", foreground=" + browser.Foreground);
                    browser.SelectedIndex = 0; host.UpdateLayout();
                    if (browser.ItemContainerGenerator.ContainerFromIndex(0) is not System.Windows.Controls.ListBoxItem selected ||
                        !HasThemeColour(selected.Foreground, "SelectedInk"))
                        throw new InvalidDataException("Dropbox picker selection lost themed text contrast in " + mode + ".");
                }
                finally { host.Close(); }
                var picker = new System.Windows.Controls.DatePicker { SelectedDate = DateTime.Today.AddDays(7) };
                var calendarHost = new Window { Content = picker, Width = 380, Height = 120, ShowInTaskbar = false, ShowActivated = false };
                try
                {
                    calendarHost.Show(); calendarHost.UpdateLayout(); picker.IsDropDownOpen = true; calendarHost.UpdateLayout();
                    var popup = (System.Windows.Controls.Primitives.Popup)picker.Template.FindName("PART_Popup", picker);
                    var calendar = Descendants(popup.Child).OfType<System.Windows.Controls.Calendar>().Single();
                    calendar.UpdateLayout();
                    var item = Descendants(calendar).OfType<System.Windows.Controls.Primitives.CalendarItem>().Single();
                    if (!HasThemeColour(item.Background, "RaisedSurface"))
                        throw new InvalidDataException("Calendar popup background does not follow " + mode);
                    var days = Descendants(item).OfType<System.Windows.Controls.Primitives.CalendarDayButton>().ToArray();
                    if (days.Length != 42 || !days.Any(day => day.IsSelected) ||
                        days.Where(day => !day.IsInactive && !day.IsSelected).Any(day => !HasThemeColour(day.Foreground, "Ink")) ||
                        days.Where(day => day.IsSelected).Any(day => !HasThemeColour(day.Foreground, "SelectedInk")))
                        throw new InvalidDataException("Calendar dates or selection lost readable themed states in " + mode);
                    var header = (System.Windows.Controls.Button)item.Template.FindName("PART_HeaderButton", item);
                    header.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); calendar.UpdateLayout();
                    if (calendar.DisplayMode != System.Windows.Controls.CalendarMode.Year ||
                        Descendants(item).OfType<System.Windows.Controls.Primitives.CalendarButton>().Count() != 12)
                        throw new InvalidDataException("Calendar month/year navigation was lost.");
                    if (!WorkspaceInput.CloseOpenPicker(calendarHost) || picker.IsDropDownOpen || !calendarHost.IsVisible ||
                        WorkspaceInput.CloseOpenPicker(calendarHost))
                        throw new InvalidDataException("Escape must close only an open picker before considering its editor.");
                }
                finally { picker.IsDropDownOpen = false; calendarHost.Close(); }
            }
            theme.PreviewPreference(previousTheme);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            static void FinishUiTask(Task task)
            {
                if (!task.IsCompleted)
                {
                    var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                task.GetAwaiter().GetResult();
            }
            foreach (var indefinitely in new[] { false, true })
            {
                FinishUiTask(window.SetSyncPauseAsync(indefinitely ? null : TimeSpan.FromMinutes(5), indefinitely));
                if (!window.TrayPaused) throw new InvalidDataException("Pause did not reach the actual workspace state.");
                FinishUiTask(window.HandleWorkspacePauseActionAsync(new System.Windows.Controls.Button()));
                var saved = new UiPreferencesStore(instance.StateAuthority).Load();
                if (window.TrayPaused || saved.PauseUntilResumed || saved.PauseUntilUtc is not null)
                    throw new InvalidDataException("Resume action did not clear and persist the actual pause state.");
            }
            var homeFeed = (System.Windows.Controls.DataGrid)typeof(MainWindow).GetField("_homeFeed", flags)!.GetValue(window)!;
            if (homeFeed.VerticalAlignment != VerticalAlignment.Top)
                throw new InvalidDataException("Recent Activity must remain top-aligned in a taller Home card.");
            typeof(MainWindow).GetMethod("ClearMatchingConflictSelection", flags)!.Invoke(window, null);
            var matchingButton = (System.Windows.Controls.Button)typeof(MainWindow)
                .GetField("SelectAllMatchingConflictsButton", flags)!.GetValue(window)!;
            if (matchingButton.Content is not System.Windows.Controls.StackPanel { Children.Count: 2 } ||
                System.Windows.Automation.AutomationProperties.GetName(matchingButton) != "Select All Matching")
                throw new InvalidDataException("Resetting conflict selection removed its icon or accessible label.");
            var selector = (System.Windows.Controls.ComboBox)typeof(MainWindow).GetField("ActivityPairBox", flags)!.GetValue(window)!;
            var tabs = (System.Windows.Controls.TabControl)typeof(MainWindow).GetField("_activityTabs", flags)!.GetValue(window)!;
            var priorityBox = (System.Windows.Controls.TextBox)typeof(MainWindow).GetField("PairPriorityBox", flags)!.GetValue(window)!;
            var priorityChoice = (System.Windows.Controls.ComboBox)typeof(MainWindow).GetField("PairPriorityChoice", flags)!.GetValue(window)!;
            foreach (var priority in new[] { 20, -20, 0, 50, -50 })
            {
                typeof(MainWindow).GetMethod("LoadPairSyncOrder", flags)!.Invoke(window, [priority]);
                if (priorityBox.Text != priority.ToString()) throw new InvalidDataException("Opening pair rewrites saved sync order.");
            }
            priorityChoice.SelectedIndex = 0;
            if (priorityBox.Text != "50") throw new InvalidDataException("Explicit sync-order change no longer applies.");
            // Account scoping now has its own header selector. Exercise the real
            // current pair-choice builder instead of injecting obsolete account
            // entries that a legitimate refresh must replace.
            var pair = new SyncPair("one", "One Pair", root, 1, 1, "/One", "id:one", InitialReviewApproved:false);
            typeof(MainWindow).GetField("_pairSet", flags)!.SetValue(window, new SyncPairSet([pair], pair.Id));
            typeof(MainWindow).GetMethod("RefreshActivityPairChoices", flags)!.Invoke(window, null);
            foreach (var choice in window.ActivityPairChoices.ToArray())
            {
                selector.SelectedItem = choice;
                for (var i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i;
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    if ((selector.SelectedItem as ActivityPairChoice)?.Id != choice.Id ||
                        !ReferenceEquals(selector.ItemsSource, window.ActivityPairChoices))
                        throw new InvalidDataException("Activity filter lost while reparenting tab " + i);
                }
            }
            window.PairRows.Add(new PairRow(pair.Id, pair.Name, root, "/One", "Personal", true, 20, false, false, "Ready"));
            window.ConflictRows.Add(new DecisionRow(pair.Id, pair.Name, "proposal.docx", "Conflict", "Both versions changed."));
            var cases = window.TrayAttentionCases;
            if (cases.Count != 2 || !ReferenceEquals(cases[0].InternalIcon, InternalAttentionIcon.FirstSyncReview) ||
                cases[0].IsRealFile || !cases[1].IsRealFile || window.TrayAttention.Count != 1)
                throw new InvalidDataException("First-sync and conflict cases diverge from tray attention.");
            window.PairRows.Add(new PairRow("problem", "Problem Pair", root, "/Two", "Work", true, 20, false, false,
                "Unavailable", NeedsAttention:true, AttentionReason:"Local folder unavailable"));
            if (window.TrayAttentionCases.Count != 3 || window.TrayAttention.Count != 2)
                throw new InvalidDataException("Independent folder problems were lost or double-counted.");
            window.PairRows.Clear(); window.ConflictRows.Clear();
            if (window.TrayAttentionCases.Count != 0 || window.TrayAttention.Count != 0)
                throw new InvalidDataException("Cleared cases leave stale tray attention.");
            typeof(MainWindow).GetField("_editingPairRootId", flags)!.SetValue(window, pair.Id);
            typeof(MainWindow).GetMethod("UpdatePairSaveAvailability", flags)!.Invoke(window, null);
            const string saveFailure = "Pair was not saved: the original failure remains visible.";
            typeof(MainWindow).GetMethod("ShowPairEditorStatus", flags)!.Invoke(window, [saveFailure, true]);
            for (var refresh = 0; refresh < 10; refresh++)
                typeof(MainWindow).GetMethod("UpdatePairSaveAvailability", flags)!.Invoke(window, null);
            if (((System.Windows.Controls.TextBlock)window.FindName("PairEditorStatusText")).Text != saveFailure ||
                ((System.Windows.Controls.Border)window.FindName("PairEditorStatus")).Visibility != Visibility.Visible)
                throw new InvalidDataException("Routine validation erased the failed-save message.");
            typeof(MainWindow).GetMethod("EndPairDraft", flags)!.Invoke(window, null);
            if (((System.Windows.Controls.Border)window.FindName("PairEditorStatus")).Visibility != Visibility.Collapsed)
                throw new InvalidDataException("A new pair editor retained the previous pair's result.");
        }
        finally { window.RequestProductExit(); MainWindow.SuppressLoadedStartupForTest = false; app.Shutdown(); }
    }

    // Real WPF screens and retained SQLite history; all inputs are disposable examples.
    // The replacement runtime cannot read Windows credentials or send network requests.
    // Synthetic HTTP input exercises the real account, quota and sharing adapters.
    private static int RunDetailedWorkspaceInspection(bool seedPending = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-shark-ui-inspection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
        theme.Initialize();
        theme.SetPreferenceAsync(UiPreferencesStore.AmoledTheme).GetAwaiter().GetResult();
        var inspectionPreferences = new UiPreferencesStore(instance.StateAuthority);
        inspectionPreferences.SaveAsync(inspectionPreferences.Load() with
        { ExplorerIntegrationEnabled = false, AutomaticUpdateChecks = false }).GetAwaiter().GetResult();
        using var http = new HttpClient(new InspectionNetworkBoundary());
        var pairs = new SyncPairStore(instance.StateAuthority);
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        var database = new SyncAuthorityDatabase(Path.Combine(root, "vNext", "state", "syncshark.db"));
        var accounts = new[] { InspectionAccount("personal", "Example Personal"), InspectionAccount("work", "Example Work") };
        var pairSet = Task.Run(async () =>
        {
            await connections.SaveNewAsync(accounts[0]);
            await connections.SaveAsync(accounts[1] with
            { CredentialReference = WindowsDropboxRefreshTokenStore.AccountTarget(accounts[1].ConnectionId) });
            var samplePairs = new List<SyncPair>();
            foreach (var (name, index) in new[] { ("Pair A", 0), ("Projects", 1) })
            {
                var local = Path.Combine(root, "computer", name);
                Directory.CreateDirectory(Path.Combine(local, "Work", "Invoices"));
                Directory.CreateDirectory(Path.Combine(local, "Work", "Photos"));
                Directory.CreateDirectory(Path.Combine(local, "Archive"));
                File.WriteAllText(Path.Combine(local, "cloud test.txt"), "Isolated Sync Shark visual example.\n");
                using var lease = PilotDirectoryLease.Open(local);
                // UIV1-3: every synthetic pair owns its own provider folder identity and recovery
                // root. Two accounts that reused one global remote folder id made the real editor
                // refuse the pair set with DuplicateEndpointAcrossPairs — genuine validation, so
                // the fixtures carry distinct identities instead of the validator being relaxed.
                samplePairs.Add(new SyncPair(Guid.NewGuid().ToString("N"), name,
                    local, lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
                    index == 0 ? "/First" : "/Second", index == 0 ? "id:first" : "id:second",
                    Enabled: true, Priority: 20, PropagateDeletions: true,
                    SelectionRules: [new SyncSelectionRule("Archive", false, "id:archive")],
                    SelectionDefaultIncluded: true, InitialReviewApproved: index != 0,
                    RecoveryRoot: new SyncRemoteRecoveryRootBinding("/Apps/Sync Shark/Recovery",
                        "id:fixture-recovery-" + accounts[index].ConnectionId, accounts[index].RootNamespaceId),
                    ConnectionId: accounts[index].ConnectionId));
            }
            var result = new SyncPairSet(samplePairs, samplePairs[0].Id);
            await new SyncAutomationStore(instance.StateAuthority).SaveAsync(new SyncAutomationSettings(false, 15, false));
            var installationId = await pairs.GetOrCreateInstallationIdAsync();
            await database.InitializeAsync(installationId, "inspection-profile", "local-ui-fixture", "{}");
            // These are current multi-account definitions, not a legacy single-account migration.
            foreach (var pair in samplePairs)
            {
                await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
                    "recoverable", 0, 1, "manual_preserve_both", "content_times_basic_attrs", RecursiveMode: true,
                    AutomaticPruning: false, RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue));
                await database.RetainPairDefinitionAsync(pair, pair.Id == result.SelectedPairId);
                // The read-only pending inspection seeds historical roots. The clean
                // editor journey must let the real BindPairAsync create its own
                // current account/root identities, rather than collide with fixture IDs.
                if (seedPending)
                    await SeedInspectionPathIndexAsync(database, pair, accounts.Single(account => account.ConnectionId == pair.ConnectionId));
            }
            if (seedPending) await SeedInspectionPendingAndConflictAsync(database, samplePairs);
            var completedRun = await database.CreateAcceptedCommandAdmissionAsync(null, "run_pair", samplePairs[0].Id,
                null, null, JsonSerializer.Serialize(new { pairIds = new[] { samplePairs[0].Id }, trigger = "Manual Check" }),
                SyncAuthorityCommandPurpose.NormalEffect);
            await database.TransitionCommandRequestAsync(completedRun.CommandRequestId, "executing");
            await database.TransitionCommandRequestAsync(completedRun.CommandRequestId, "completed",
                "{\"completedPairs\":1,\"failedPairs\":0,\"pairs\":[{\"uploaded\":2,\"downloaded\":1,\"unchanged\":298,\"blocked\":0,\"conflicts\":0}]}");
            return result;
        }).GetAwaiter().GetResult();
        using var runtime = new SyncAuthorityRuntimeHost(instance.StateAuthority, http, pairs,
            new SyncRuntimeStateStore(instance.StateAuthority), connections, new InspectionCredentialBoundary(), database,
            (_, _) => throw new InvalidOperationException("The visual inspection cannot access a provider."));
        var retained = Task.Run(() => runtime.LoadPairsAsync()).GetAwaiter().GetResult();
        if (retained.Pairs.Count != 2 || retained.Pairs.Any(pair => !pair.PropagateDeletions))
            throw new InvalidDataException("Inspection definitions did not survive real runtime loading.");
        var first = pairSet.Pairs[0];
        var now = DateTimeOffset.Now;
        var rows = new[]
        {
            new ActivityRow(now.AddMinutes(-1).ToString("O"), "cloud test.txt", "Completed download", "Downloaded and verified.", PairId:first.Id, PairName:first.Name, OperationId:"inspection-text", ItemKind:"File", FileType:"TXT", SizeBytes:new FileInfo(Path.Combine(first.LocalRoot, "cloud test.txt")).Length, LocalRoot:first.LocalRoot, DropboxRoot:first.DropboxRoot),
            new ActivityRow(now.ToString("O"), "Work/Invoices/manual-with-a-long-clear-filename.pdf", "Starting upload", "Uploading a new file. Byte totals are not yet available.", PairId:first.Id, PairName:first.Name, OperationId:"inspection-upload", ItemKind:"File", FileType:"PDF", SizeBytes:340880, LocalRoot:first.LocalRoot, DropboxRoot:first.DropboxRoot),
            new ActivityRow(now.AddMinutes(-2).ToString("O"), "warranty.pdf", "Completed upload", "Uploaded and verified.", PairId:first.Id, PairName:first.Name, OperationId:"inspection-complete", ItemKind:"File", FileType:"PDF", SizeBytes:1857600, LocalRoot:first.LocalRoot, DropboxRoot:first.DropboxRoot),
            new ActivityRow(now.AddDays(-1).ToString("O"), "Work/Photos/serial-number.jpg", "Completed download", "Downloaded and verified.", PairId:first.Id, PairName:first.Name, OperationId:"inspection-image", ItemKind:"File", FileType:"JPG", SizeBytes:4285020, LocalRoot:first.LocalRoot, DropboxRoot:first.DropboxRoot),
            new ActivityRow(now.AddDays(-1).AddMinutes(-4).ToString("O"), "old-receipt.pdf", "Recoverable deletion", "A displaced copy is retained in Recovery.", PairId:first.Id, PairName:first.Name, OperationId:"inspection-recovery", ItemKind:"File", FileType:"PDF", SizeBytes:8270, LocalRoot:first.LocalRoot, DropboxRoot:first.DropboxRoot),
            new ActivityRow(now.AddDays(-2).ToString("O"), "cloud test.txt", "Completed download", "Downloaded and verified.", PairId:pairSet.Pairs[1].Id, PairName:pairSet.Pairs[1].Name, OperationId:"inspection-work", ItemKind:"File", FileType:"TXT", SizeBytes:new FileInfo(Path.Combine(pairSet.Pairs[1].LocalRoot, "cloud test.txt")).Length, LocalRoot:pairSet.Pairs[1].LocalRoot, DropboxRoot:pairSet.Pairs[1].DropboxRoot)
        };
        // These are newly authored fixture events: capture their account at creation.
        // Production historical rows must never derive an account from today's pair map.
        rows = rows.Select(row =>
        {
            var pair = pairSet.Pairs.Single(item => item.Id == row.PairId);
            return row with
            {
                ConnectionId = pair.ConnectionId,
                // UF5: these are newly authored fixture events, so they record the same captured ordered
                // endpoint context that production captures when the event is created. Historical rows are
                // never backfilled from today's pair names, and no production producer is changed.
                EndpointContext = SyncEndpointContext.Capture(pair),
                Categories = [SyncActivityCategories.Files], Entity = "File"
            };
        }).ToArray();
        MainWindow.SuppressLoadedStartupForTest = true;
        // LH4: this isolated visual fixture opts in to the readonly local-root health pass for its own
        // duration only, so the real both-sides endpoint check runs on the synthetic pairs this route
        // owns. Every other suppressed startup, provider, shell-registration and monitor path stays off,
        // and the flag is restored below.
        MainWindow.LocalRootHealthEnabledForTest = true;
        MainWindow.PairFolderBrowserFactoryForTest = _ => Task.FromResult<IDropboxFolderBrowserService>(new EmbeddedDropboxPagingFixture());
        MainWindow.DropboxConnectionCheckForTest = () => Task.FromResult(true);
        MainWindow.MonitorConfiguratorForTest = (_, _, _) => Task.FromResult<IReadOnlyList<SyncAuthorityMonitorStartResult>>([]);
        MainWindow.MonitorStopperForTest = () => Task.CompletedTask;
        // Pending rows/count and conflict selection must come from the same retained
        // database. Visual-only seeded rows disappeared on a genuine refresh.
        MainWindow.PendingQueueCountLoaderForTest = null;
        MainWindow.PairRunDispatcherForTest = (_, _) => throw new InvalidOperationException("Sync dispatch is disabled in the visual inspection.");
        MainWindow? inspectionWindow = null;
        // Seed inputs in the real retained store. A fixed projection override would hide errors produced by
        // native interactions (including Scan Again), making visual acceptance unable to detect lost history.
        Task.Run(() => LegacyHistoryFixture.SeedAsync(database, rows.Select(row => new SyncActivityEvent(
            "inspection-file-" + row.OperationId, DateTimeOffset.Parse(row.When),
            [SyncActivityCategories.Files], "Success", "File", row.Outcome, row.Reason,
            row.ConnectionId, row.PairId, OperationId: row.OperationId, RelativePath: row.Path,
            EndpointContext: row.EndpointContext)).ToArray())).GetAwaiter().GetResult();
        MainWindow.ActivityProjectionLoaderForTest = null;
        var area = SystemParameters.WorkArea;
        var window = new MainWindow(instance.StateAuthority, theme)
        {
            Title = "Sync Shark visual inspection", Width = Math.Min(1200, area.Width - 32),
            Height = Math.Min(780, area.Height - 32), WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        BindAuthorityHost(window, runtime);
        inspectionWindow = window;
        window.StartupRegistrationForTest = _ => throw new InvalidOperationException("The inspection cannot change Windows startup.");
        MainWindow.DropboxConnectionStatusLoaderForTest = _ =>
        {
            var selected = typeof(MainWindow).GetField("_selectedConnectionId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) as string;
            var binding = connections.Load(selected) ?? accounts[0];
            return Task.FromResult(new DropboxConnectionStatus(binding, true, true, "Connected and verified (example data).", null,
                pairSet.Pairs.Count(pair => pair.ConnectionId == binding.ConnectionId), now.AddMinutes(-8),
                new DropboxSpaceUsage(2400000000000, 3000000000000, false, now)));
        };
        // Taskbar exposure is test-only: it lets native computer control select this separate
        // window without activating its owner and dismissing it. Its real content is unchanged.
        var tray = new TrayFlyoutWindow(window) { ShowInTaskbar = true };
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                // The controller enumerates taskbar windows but omits ordinary owned
                // modals. Expose fixture-only dialogs, preserving their real controls,
                // owner, modality and handlers; do not change the installed product.
                if (sender is Window dialog && !ReferenceEquals(dialog, window))
                    dialog.ShowInTaskbar = true;
            }));
        window.CompactViewRequested += (_, _) => tray.ToggleAtTray();
        window.Loaded += async (_, _) =>
        {
            SeedMainWindow(window);
            window.LoadPairSetForTest(pairSet);
            await window.RefreshConnectionStateForTestAsync(false);
            await window.LoadActivityPageForTestAsync();
            await window.LoadQueuePageForTestAsync();
            await (Task)typeof(MainWindow).GetMethod("LoadConflictPageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [true, null])!;
            var track = typeof(MainWindow).GetMethod("TrackWorkspaceAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var row in rows.Reverse()) track.Invoke(window, [row]);
            if (window.QueueRows.Count != (seedPending ? 2 : 0) || window.ConflictRows.Count != (seedPending ? 1 : 0) ||
                (await runtime.CaptureOpenConflictSelectionAsync(null)).Count != (seedPending ? 1 : 0))
                throw new InvalidDataException("The real inspection projections do not match their retained records.");
        };
        Console.WriteLine("Isolated visual inspection: " + root);
        try { app.Run(window); return 0; }
        finally
        {
            MainWindow.LocalRootHealthEnabledForTest = false;
            MainWindow.SuppressLoadedStartupForTest = false;
            MainWindow.PairFolderBrowserFactoryForTest = null;
            MainWindow.DropboxConnectionCheckForTest = null;
            MainWindow.DropboxConnectionStatusLoaderForTest = null;
            MainWindow.MonitorConfiguratorForTest = null;
            MainWindow.MonitorStopperForTest = null;
            MainWindow.PendingQueueCountLoaderForTest = null;
            MainWindow.PairRunDispatcherForTest = null;
            MainWindow.ActivityProjectionLoaderForTest = null;
        }
    }

    private static DropboxConnectionBinding InspectionAccount(string suffix, string name) => new DropboxConnectionBinding(
        DropboxProviderConfiguration.RegisteredClientId, "inspection-" + suffix, "inspection-root-" + suffix,
        "inspection-home-" + suffix, DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequestedScopes(true),
        DisplayName:name, Email:suffix + "@example.test", AccountType:"Individual",
        ConnectedUtc:DateTimeOffset.Now.AddDays(-3), LastVerifiedUtc:DateTimeOffset.Now).Normalize();

    private static async Task SeedInspectionPathIndexAsync(SyncAuthorityDatabase database, SyncPair pair, DropboxConnectionBinding account)
    {
        var installationId = await database.LoadInstallationIdAsync()
            ?? throw new InvalidDataException("The isolated inspection database has no installation identity.");
        var localId = pair.Id + "-local"; var remoteId = pair.Id + "-remote";
        await database.RegisterRootBindingAsync(localId, pair.Id, "local", 1,
            JsonSerializer.Serialize(new { volumeSerial = pair.LocalVolumeSerialNumber.ToString("x8"), fileId = pair.LocalRootFileId.ToString("x16") }), pair.LocalRoot);
        var providerAccountId = SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox,
            account.AccountId, account.RootNamespaceId, account.HomeNamespaceId);
        await database.RegisterProviderAccountAsync(providerAccountId, installationId, account.AccountId,
            account.RootNamespaceId, account.HomeNamespaceId, account.DisplayName, 1);
        await database.RegisterDropboxRootBindingAsync(remoteId, pair.Id, 1, installationId, providerAccountId,
            account.RootNamespaceId, pair.DropboxFolderId, pair.DropboxRoot);
        var local = await database.BeginScanGenerationAsync(pair.Id, localId, "local", "initial", "{\"scope\":\"complete\"}");
        var remote = await database.BeginScanGenerationAsync(pair.Id, remoteId, "dropbox", "initial", "{\"scope\":\"complete\"}");
        var entries = new List<SyncPathIndexEntry>();
        var localObservations = new List<SyncAuthorityObservationInput>();
        var remoteObservations = new List<SyncAuthorityObservationInput>();
        // UIV1-3: each directory in the index also gets its real retained observation row for its
        // side, through the production append helper. Without them the first-sync review had no
        // retained object kind to read and described every directory as a file.
        foreach (var path in new[] { "Work", "Work/Invoices", "Work/Photos", "Archive", "Cloud Documents" })
        {
            var present = Directory.Exists(Path.Combine(pair.LocalRoot, path));
            using var held = present ? PilotDirectoryLease.Open(Path.Combine(pair.LocalRoot, path)) : null;
            var id = held is null ? null : $"local:{held.Identity.VolumeSerialNumber:x8}:{held.Identity.FileId:x16}";
            if (held is not null)
                localObservations.Add(new SyncAuthorityObservationInput(path, "directory",
                    JsonSerializer.Serialize(new { volumeSerial = held.Identity.VolumeSerialNumber.ToString("x8"),
                        fileId = held.Identity.FileId.ToString("x16") }), null, 0,
                    JsonSerializer.Serialize(new { path, kind = "directory",
                        lastWriteUtc = Directory.GetLastWriteTimeUtc(Path.Combine(pair.LocalRoot, path)).ToString("O") })));
            remoteObservations.Add(new SyncAuthorityObservationInput(path, "directory",
                JsonSerializer.Serialize(new { fileId = RemoteInspectionIdentity(account, path), revision = "rev:" + path }),
                null, 0, present
                    ? JsonSerializer.Serialize(new { path, kind = "directory",
                        modifiedUtc = Directory.GetLastWriteTimeUtc(Path.Combine(pair.LocalRoot, path)).ToString("O") })
                    : JsonSerializer.Serialize(new { path, kind = "directory" })));
            entries.Add(new(path, PilotEntryKind.Directory, true, present, true, false, id, RemoteInspectionIdentity(account, path),
                SyncSelectionPolicy.IsIncluded(path, pair, true)));
        }
        var filePath = Path.Combine(pair.LocalRoot, "cloud test.txt");
        var fileLength = new FileInfo(filePath).Length;
        var fileHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(filePath))).ToLowerInvariant();
        var fileIdentity = WindowsCloudFilesProvider.ReadStorageFileIdentity(filePath, pair.Id, pair.Provider);
        if (fileIdentity.LocalId == 0)
            throw new InvalidDataException("The synthetic example file identity could not be read.");
        localObservations.Add(new SyncAuthorityObservationInput("cloud test.txt", "file",
            JsonSerializer.Serialize(new { volumeSerial = pair.LocalVolumeSerialNumber.ToString("x8"),
                fileId = fileIdentity.LocalId.ToString("x16") }), fileHash, fileLength,
            JsonSerializer.Serialize(new { path = "cloud test.txt", kind = "file", ContentHash = fileHash,
                Size = fileLength, lastWriteUtc = File.GetLastWriteTimeUtc(filePath).ToString("O") })));
        remoteObservations.Add(new SyncAuthorityObservationInput("cloud test.txt", "file",
            JsonSerializer.Serialize(new { fileId = RemoteInspectionIdentity(account, "cloud test.txt"),
                revision = "rev:cloud test.txt" }), fileHash, fileLength,
            JsonSerializer.Serialize(new { path = "cloud test.txt", kind = "file", ContentHash = fileHash,
                Size = fileLength, modifiedUtc = File.GetLastWriteTimeUtc(filePath).ToString("O") })));
        await database.AppendObservationEntriesAsync(local, localObservations);
        await database.AppendObservationEntriesAsync(remote, remoteObservations);
        entries.Add(new("cloud test.txt", PilotEntryKind.File, true, true, true, false,
            null, RemoteInspectionIdentity(account, "cloud test.txt"), true));
        await database.CompleteScanGenerationAsync(local); await database.CompleteScanGenerationAsync(remote);
        await database.ReplacePathIndexAsync(pair.Id, local.ScanGenerationId, remote.ScanGenerationId, entries,
            SyncSelectionPolicy.Compile(pair.SelectionRules, pair.SelectionDefaultIncluded));
    }

    // A provider folder identity belongs to one exact synthetic account. Reusing one id across
    // accounts made the real cross-pair duplicate-endpoint check (correctly) refuse the pair set.
    private static string RemoteInspectionIdentity(DropboxConnectionBinding account, string path) =>
        "id:" + account.ConnectionId + ":" + path;

    private static async Task SeedInspectionPendingAndConflictAsync(SyncAuthorityDatabase database, IReadOnlyList<SyncPair> pairs)
    {
        var hash = new string('a', 64);
        for (var index = 0; index < pairs.Count; index++)
        {
            var pair = pairs[index];
            var id = pair.Id + "-inspection-queue";
            var path = index == 0 ? "Work/Invoices/manual-with-a-long-clear-filename.pdf" : "invoice.pdf";
            var sourceSide = index == 0 ? "local" : "dropbox";
            var sourceRoot = pair.Id + (index == 0 ? "-local" : "-remote");
            var destinationRoot = pair.Id + (index == 0 ? "-remote" : "-local");
            await database.RegisterObjectAsync(id, "file");
            await database.RegisterMembershipAsync(id + "-membership", id, sourceRoot, path, 1);
            await database.RegisterObjectVersionAsync(id + "-version", id, sourceSide, "{}", hash, 340880);
            var fixtureAdmission = await database.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id, null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
            // A fresh action must carry exact current planning authority for its object; the fixture
            // seeds the same object-scoped cut the production planner binds, on the pair's true roots.
            var planningAuthority = await FixturePlanningAuthority.CurrentAsync(database, pair.Id,
                pair.Id + "-local", pair.Id + "-remote", id);
            await database.CreateOperationAsync(new SyncAuthorityOperationPlan(id, id + "-key",
                index == 0 ? SyncAuthorityOperationKind.CreateRemote : SyncAuthorityOperationKind.CreateLocal,
                pair.Id, id, planningAuthority.ConfigurationRevision, "inspection-profile", "{}", pair.Priority,
                [new(id + "-source", pair.Id, id, "source", sourceRoot, id + "-membership", id + "-version", 1, path, "{\"kind\":\"file\"}"),
                 new(id + "-destination", pair.Id, id, "destination", destinationRoot, null, null, null, path, "{\"kind\":\"file\",\"absent\":true}")], [], [],
                PlanningAuthorities: [planningAuthority]), fixtureAdmission);
            if (index == 0)
            {
                var lease = await database.AcquireOperationLeaseAsync(id, "inspection-seed", TimeSpan.FromMinutes(1));
                var attempt = await database.StartDispatchedAttemptAsync(id, lease, hash, "inspection-no-provider-effect");
                await database.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.Unknown,
                    "{}", "{}", "{\"example\":true}", hash);
                await database.ReleaseLeaseAsync(lease);
            }
        }

        var first = pairs[0];
        const string conflictObject = "inspection-conflict-object";
        await database.RegisterObjectAsync(conflictObject, "file");
        foreach (var side in new[] { "local", "dropbox" })
        {
            var member = conflictObject + "-" + side;
            await database.RegisterMembershipAsync(member, conflictObject,
                first.Id + (side == "local" ? "-local" : "-remote"), "proposal.docx", 1);
            await database.RegisterObjectVersionAsync(member + "-version", conflictObject, side,
                JsonSerializer.Serialize(new { lastWriteUtc = DateTimeOffset.UtcNow.AddMinutes(side == "local" ? -1 : -5) }),
                hash, 1024);
        }
        await database.RecordConflictAsync("inspection-conflict", first.Id, conflictObject, "Both versions changed.",
            conflictObject + "-local-version", conflictObject + "-local",
            conflictObject + "-dropbox-version", conflictObject + "-dropbox");
        if (await database.LoadPendingQueueCountAsync() != 2)
            throw new InvalidDataException("The retained inspection queue must contain exactly two operations.");
    }

    private sealed class InspectionCredentialBoundary(string account = "personal") : IDropboxRefreshTokenStore
    {
        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding binding) => new InspectionCredentialBoundary(binding.AccountId.EndsWith("work", StringComparison.Ordinal) ? "work" : "personal");
        public Task<string?> ReadAsync(CancellationToken token = default) => Task.FromResult<string?>("inspection-no-live-authority-" + account);
        public Task WriteAsync(string value, CancellationToken token = default) => throw new InvalidOperationException("No credential writes in visual inspection.");
        public Task DeleteAsync(CancellationToken token = default) => throw new InvalidOperationException("No credential deletion in visual inspection.");
    }
    private sealed class InspectionNetworkBoundary : HttpMessageHandler
    {
        private readonly Dictionary<string, bool> _requestOpen = new(StringComparer.Ordinal);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri?.Host != "api.dropboxapi.com") throw new InvalidOperationException("No external transport in visual inspection.");
            var route = request.RequestUri.AbsolutePath;
            var input = request.Content is null ? "null" : await request.Content.ReadAsStringAsync(token);
            var bearer = request.Headers.Authorization?.Parameter ?? "";
            var account = (route == "/oauth2/token" ? input : bearer).Contains("work", StringComparison.Ordinal) ? "work" : "personal";
            if (route == "/oauth2/token") return Json(new { access_token = "inspection-no-live-authority-" + account,
                expires_in = 14400, account_id = "inspection-" + account, scope = string.Join(' ', DropboxProviderConfiguration.RequestedScopes(true)) });
            if (bearer != "inspection-no-live-authority-" + account) throw new InvalidOperationException("Only synthetic credentials are accepted.");
            if (route == "/2/users/get_current_account") return Json(new {
                account_id = "inspection-" + account, name = new { display_name = "Example " + (account == "work" ? "Work" : "Personal") },
                email = account + "@example.test", account_type = new Dictionary<string, string> { [".tag"] = "individual" },
                root_info = new { root_namespace_id = "inspection-root-" + account, home_namespace_id = "inspection-home-" + account } });
            if (route == "/2/users/get_space_usage") return Json(new { used = account == "work" ? 125000000000L : 2400000000000L,
                allocation = new Dictionary<string, object> { [".tag"] = "individual", ["allocated"] = 3000000000000L } });
            using var body = JsonDocument.Parse(input);
            var ns = request.Headers.GetValues("Dropbox-API-Path-Root").Single();
            if (!ns.Contains("inspection-home-" + account, StringComparison.Ordinal) && !ns.Contains("inspection-root-" + account, StringComparison.Ordinal))
                throw new InvalidOperationException("The synthetic account and namespace do not match.");
            var open = _requestOpen.GetValueOrDefault(account, true);
            if (route == "/2/file_requests/update" && body.RootElement.TryGetProperty("open", out var value)) _requestOpen[account] = open = value.GetBoolean();
            return route switch
            {
                "/2/sharing/list_shared_links" => Json(new { links = new[] { Link(account) }, has_more = false }),
                "/2/sharing/get_shared_link_metadata" => Json(Link(account)),
                "/2/file_requests/list_v2" => Json(new { file_requests = new[] { Request(account, open) }, has_more = false }),
                "/2/file_requests/get" or "/2/file_requests/update" => Json(Request(account, open)),
                "/2/files/list_folder" => Json(new { entries = new[] { Item("/Documents", "folder"), Item("/Documents/Handbook.pdf", "file") }, has_more = false, cursor = "inspection-only" }),
                "/2/files/get_metadata" => Json(InspectionMetadata(body.RootElement.GetProperty("path").GetString()!, account)),
                _ => throw new InvalidOperationException("This action is not enabled in the isolated inspection: " + route)
            };
        }
        private static object Item(string path, string kind) => new Dictionary<string, object> { [".tag"] = kind,
            ["id"] = "id:inspection-" + path, ["path_display"] = path, ["name"] = Path.GetFileName(path) };
        private static object InspectionMetadata(string path, string account)
        {
            var recoveryId = "id:fixture-recovery-" + InspectionAccount(account, "Example").ConnectionId;
            if (path == "/Apps/Sync Shark/Recovery" || path == recoveryId)
                return new Dictionary<string, object> { [".tag"] = "folder", ["id"] = recoveryId,
                    ["path_display"] = "/Apps/Sync Shark/Recovery", ["name"] = "Recovery" };
            return path switch
            {
                "/First" or "id:first" when account == "personal" => new Dictionary<string, object>
                    { [".tag"] = "folder", ["id"] = "id:first", ["path_display"] = "/First", ["name"] = "First" },
                "/Second" or "id:second" when account == "work" => new Dictionary<string, object>
                    { [".tag"] = "folder", ["id"] = "id:second", ["path_display"] = "/Second", ["name"] = "Second" },
                _ => Item(path, "folder")
            };
        }
        private static object Link(string account) => new Dictionary<string, object> {
            [".tag"] = "file", ["id"] = "id:inspection-" + account, ["name"] = account == "work" ? "Project Handbook.pdf" : "Appliance Warranty.pdf",
            ["url"] = "https://www.dropbox.com/scl/fi/inspection-" + account + "/example.pdf?rlkey=inspection-only",
            ["path_lower"] = "/outside-sync-pairs/" + account + "/example.pdf",
            ["link_permissions"] = new { effective_audience = new Dictionary<string, string> { [".tag"] = "public" },
                link_access_level = new Dictionary<string, string> { [".tag"] = "viewer" }, can_revoke = true, can_set_expiry = true,
                can_remove_expiry = true, can_set_password = false, can_remove_password = false, allow_download = true,
                can_allow_download = true, can_disallow_download = true, audience_options = new[] {
                    new { audience = new Dictionary<string, string> { [".tag"] = "public" }, allowed = true },
                    new { audience = new Dictionary<string, string> { [".tag"] = "no_one" }, allowed = true } } } };
        private static object Request(string account, bool open) => new { id = "inspection-" + account,
            url = "https://www.dropbox.com/request/inspection-" + account, title = account == "work" ? "Project Documents" : "Receipts and Warranties",
            destination = "/File Requests/" + account, description = "Upload your documents to this folder.", created = "2026-09-14T10:00:00Z", is_open = open, file_count = account == "work" ? 12 : 3 };
        private static HttpResponseMessage Json(object value) => new(System.Net.HttpStatusCode.OK) {
            Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };
    }
}
