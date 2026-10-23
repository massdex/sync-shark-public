using Massdex.SyncShark;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace SyncShark.UiFixture;

// UI11 real-consumer checks on the actual MainWindow Runs surface.
//
// The Runs tab, its retained-run projection, its compact/detailed rows, its shared view switch, its
// details host, its filters and its retained current-session comparison are driven through the
// production members the running application uses: the real bound run-row collection, the real
// DataGrid selection handler, the real LoadRunHistoryAsync page contract, the real
// SetDetailedViewAsync preference write, the real workspace account picker and the real Current
// Session Comparison expander that already existed before this change.
//
// This file was written under the UI11 source-only hold and has NOT been built, run or rendered. No
// restore, provider call, credential read, signing, install or user-file effect is performed: the
// retained-run page read is replaced by RunHistoryLoaderForTest only so the delayed-page,
// account-scope and load-more/reset orderings can be completed deliberately, and every fixture run is
// constructed in memory. The authority database the window is given is the fixture's own empty state
// root, so the retained history itself is never mutated, appended to or pruned by these checks.
internal static class RunHistoryUiChecks
{
    private const string Selector = "--run-history-ui-checks";

    private const string PairA = "pair-runs-a";
    private const string PairB = "pair-runs-b";
    private const string RemovedPair = "pair-removed";
    private const string AccountA = "fixture-account-a";
    private const string AccountB = "fixture-account-b";
    private const string AccountLabelA = "Dropbox: alex@example.invalid";
    private const string AccountLabelB = "Dropbox: blair@example.invalid";
    private const string NotRecorded = RunHistoryRow.NotRecorded;
    private const string MultiAccountSuffix = " (multi-account run)";

    private const string RunMulti = "run-completed-multi";
    private const string RunAttention = "run-attention";
    private const string RunPartial = "run-partial-counts";
    private const string RunRunning = "run-running";
    private const string RunRemoved = "run-removed-pair";
    private const string RunLegacy = "run-legacy-malformed";
    private const string RunQueued = "run-queued";
    private const string RunStopped = "run-stopped";
    private const string RunRejected = "run-rejected";

    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var preferences = new UiPreferencesStore(appLock.StateAuthority);
        using var theme = new ThemeManager(preferences);
        theme.Initialize();
        MainWindow.SuppressLoadedStartupForTest = true;
        var pending = new List<TaskCompletionSource<IReadOnlyList<SyncAuthorityRunHistory>>>();
        var requests = new List<(int Limit, int Offset)>();
        MainWindow.RunHistoryLoaderForTest = (limit, offset) =>
        {
            var source = new TaskCompletionSource<IReadOnlyList<SyncAuthorityRunHistory>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(source);
            requests.Add((limit, offset));
            return source.Task;
        };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var window = new MainWindow(appLock.StateAuthority, theme) { Width = 1240, Height = 790,
            Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            SeedPairs(window);
            window.Show();
            Drain();
            Call(window, "NavigateTo", "Activity", true);
            Drain();

            CheckInitialPage(window, pending, requests);
            CheckCompactRows(window);
            CheckSelectionDetails(window);
            CheckViewSwitch(window, preferences);
            CheckFilters(window);
            CheckSelectionDiscard(window);
            CheckAccountSwitchPreservesPage(window);
            CheckDelayedScopeRefilter(window, pending, requests);
            CheckLoadMoreAndReset(window, pending, requests);
            CheckComparisonReachable(window);
            CheckFailureAndEmptyStates(window, pending, requests);
            CheckRetainedProducer(window);
            CheckLocalEndpointAccount(window);
            Console.WriteLine("PASS " + Selector +
                "; real-consumer Runs presentation checks only, no provider, credential, signing, install" +
                " or user-file effect");
            return 0;
        }
        finally
        {
            MainWindow.RunHistoryLoaderForTest = null;
            ReleasePending(window, pending);
            window.RequestProductExit();
            MainWindow.SuppressLoadedStartupForTest = false;
            app.Shutdown();
        }
    }

    // Two accounts deliberately carrying the same folder-pair name, so a row title alone cannot tell
    // them apart and the account label has to do that work honestly.
    private static void SeedPairs(MainWindow window)
    {
        Pump(window.AuthorityHost.LoadPairsAsync());
        var pairA = new SyncPair(PairA, "Documents", @"C:\fixture\documents-a", 1, 1, "/Documents", "id:runs-a",
            ConnectionId: AccountA);
        var pairB = new SyncPair(PairB, "Documents", @"C:\fixture\documents-b", 2, 2, "/Documents", "id:runs-b",
            ConnectionId: AccountB);
        window.LoadPairSetForTest(new SyncPairSet([pairA, pairB], PairA));
        Set(window, "_accountLabels", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AccountA] = AccountLabelA,
            [AccountB] = AccountLabelB
        });
        Call(window, "RefreshWorkspaceAccountPickers");
    }

    private static void CheckLocalEndpointAccount(MainWindow window)
    {
        var original = Field<SyncPairSet>(window, "_pairSet");
        var local = original.Pairs[0] with
        {
            Id = "runs-local-only", Name = "Local folders", Provider = SyncProviders.Local, ConnectionId = "",
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = SyncEndpointRecord.From(SyncEndpoint.Local(@"C:\fixture\left", new(1, 101))),
            RightEndpoint = SyncEndpointRecord.From(SyncEndpoint.Local(@"C:\fixture\right", new(1, 102)))
        };
        try
        {
            window.LoadPairSetForTest(new SyncPairSet([local], local.Id));
            var run = FullPage()[0] with { RequestJson = "{\"pairIds\":[\"runs-local-only\"]}" };
            var row = (RunHistoryRow)Call(window, "ProjectRun", run)!;
            Require(row.AccountsLabel == "This computer ↔ This computer" &&
                !(bool)Call(window, "RunTouchesConnection", row, AccountA)!,
                "R536 real run projection and account filter never borrow the primary Dropbox account for local/local");
        }
        finally { window.LoadPairSetForTest(original); }
    }

    // --- the first real page: bounded 50, offset 0, compact by default ----------------------------

    private static void CheckInitialPage(MainWindow window,
        List<TaskCompletionSource<IReadOnlyList<SyncAuthorityRunHistory>>> pending,
        List<(int Limit, int Offset)> requests)
    {
        // The window's own startup load is the first real request; the Runs tab is then opened and its
        // own reset page is served, exactly as the production tab-selection handler asks for it.
        Require(pending.Count >= 1 && requests.All(request => request == (50, 0)),
            "UI11 page contract: the first retained-run reads are the existing bounded 50 at offset 0");
        foreach (var source in pending.ToArray()) source.TrySetResult(FullPage());
        Drain();
        PumpUntil(() => !Field<bool>(window, "_loadingRuns") && Retained(window).Count == 9,
            "the startup run page to settle");

        var before = pending.Count;
        Field<TabControl>(window, "_activityTabs").SelectedIndex = 2;
        Drain();
        NextRequest(pending, before, "the Runs tab reset page").SetResult(FullPage());
        Drain();
        PumpUntil(() => !Field<bool>(window, "_loadingRuns"), "the Runs tab page to settle");
        Require(requests[^1] == (50, 0),
            "UI11 page contract: opening Runs asks for the bounded 50 at offset 0");
        Require(Retained(window).Count == 9, "UI11 page contract: the reset page replaces the loaded window");

        var grid = RunGrid(window);
        Require(ReferenceEquals(grid.ItemsSource, RowCollection(window)),
            "UI11 list: the real Runs grid is bound to the presented run rows");
        Require(RowCollection(window).Select(row => row.Id).SequenceEqual(
                new[] { RunQueued, RunRunning, RunMulti, RunAttention, RunPartial, RunStopped, RunRemoved, RunLegacy, RunRejected }),
            "UI11 list: every retained run is projected once, in the retained newest-first order");
        Require(CountText(window).Text == "Matching runs: 9 · Loaded: 9",
            "UI11 count: the loaded count is stated compactly with the list: " + CountText(window).Text);
        Require(grid.HeadersVisibility == DataGridHeadersVisibility.None &&
                Math.Abs(grid.RowHeight - SharedPresentation.CompactRowHeight) < 0.1,
            "UI11 default: Runs opens in the shared compact row presentation");
        Require(!grid.Columns.Select(column => column.Header?.ToString()).Contains("Totals"),
            "UI11 default: the Detailed columns are absent until the shared switch asks for them");
        var viewSwitch = Field<SimpleDetailedSwitch>(window, "_runViewSwitch");
        Require(viewSwitch.SimpleSegment.IsChecked == true && viewSwitch.DetailedSegment.IsChecked == false,
            "UI11 default: the shared Simple/Detailed switch shows Simple as the selected segment");
        Require(viewSwitch.PageKey == "runs",
            "UI11 default: the Runs view mode uses the existing lowercase per-page preference key");

        // The compact row carries the Sync Shark mark rather than a file-type association icon, and it
        // keeps the three compact lines the surface presents in Simple mode.
        var runColumn = (DataGridTemplateColumn)grid.Columns[0];
        var runContent = runColumn.CellTemplate.LoadContent()!;
        var mark = FindDescendant<ShellTypeImage>(runContent);
        mark!.DataContext = Row(window, RunMulti);
        foreach (var style in new[] { "Symbol", "Colour", "Combined" })
        {
            mark.StatusIconStyle = style;
            Require(mark.Source is System.Windows.Media.DrawingImage && ReferenceEquals(mark.Source,
                    BrandedItemImages.Create(style, Row(window, RunMulti).StatusGlyph, "sync")),
                "UI11 rows: the compact run row uses the shared state-aware Sync Shark mark in " + style);
        }
        var compactLines = FindDescendants<TextBlock>(runContent).ToArray();
        Require(compactLines.Length == 3 && compactLines[0].FontWeight == FontWeights.SemiBold,
            "UI11 rows: the shared compact row keeps title, status/trigger and account identity");
        Require(grid.Columns[1].Header?.ToString() == "Time" && grid.Columns[2].Header?.ToString() == "Duration",
            "UI11 rows: shared Time column precedes Duration, with no in-row timestamp duplication");
    }

    // --- the projection itself: honest pairs, accounts, status, times and totals ------------------

    private static void CheckCompactRows(MainWindow window)
    {
        var multi = Row(window, RunMulti);
        Require(multi.Title == "Documents, Documents",
            "UI11 projection: a multi-pair run states every retained folder pair");
        Require(multi.StatusLine == "Status: Completed · Started By: Manual",
            "UI11 projection: the compact row states status and trigger together: " + multi.StatusLine);
        Require(multi.StartedLine.StartsWith("Started: ", StringComparison.Ordinal) &&
                multi.StartedTooltip.StartsWith("Started: ", StringComparison.Ordinal),
            "UI11 projection: the started time is a separate labelled value with a full timestamp tooltip");
        Require(multi.DurationLabel == "02:03:07" && multi.DurationAccessibleLabel == "Duration: 02:03:07",
            "UI11 projection: the duration is projected from the retained start and completion");
        Require(multi.CompletedLabel.Length > 0 && multi.CompletedLabel != NotRecorded,
            "UI11 projection: a completed run states its completion time");
        Require(multi.StatusGlyph == "✓",
            "UI11 status: a completed run uses the shared success glyph so status is never colour alone");
        Require(multi.TotalsLabel == "Uploaded: 4 · Downloaded: 2 · Unchanged: 14 · Needs Attention: 0",
            "UI11 totals: the retained per-pair counts are aggregated: " + multi.TotalsLabel);
        Require(multi.AccountsLabel == AccountLabelA + " · " + AccountLabelB + MultiAccountSuffix,
            "UI11 accounts: a run spanning two accounts states both accounts explicitly: " + multi.AccountsLabel);

        // One pair reported nothing at all: the aggregate must not present the other pair's real
        // numbers as the run total, and no missing count may appear as zero.
        var partial = Row(window, RunPartial);
        Require(partial.TotalsLabel == NotRecorded,
            "UI11 totals: a partially recorded run states Not recorded instead of a smaller real total");
        var partialFields = Fields(partial);
        Require(partialFields.GetValueOrDefault("Uploaded") == NotRecorded &&
                partialFields.GetValueOrDefault("Downloaded") == NotRecorded &&
                partialFields.GetValueOrDefault("Unchanged") == NotRecorded &&
                partialFields.GetValueOrDefault("Needs Attention") == NotRecorded,
            "UI11 totals: a missing per-pair count is stated as Not recorded, never as zero");
        Require(partialFields.GetValueOrDefault("Per-Pair Counts")?.Contains(
                PairA + " — Uploaded: 2 · Downloaded: 1 · Unchanged: 4 · Needs Attention: 0", StringComparison.Ordinal) == true &&
                partialFields.GetValueOrDefault("Per-Pair Counts")?.Contains(PairB + " — " + NotRecorded,
                    StringComparison.Ordinal) == true,
            "UI11 totals: a multi-pair run keeps its per-pair counts with Not recorded for the pair that reported none");

        // A pair that is no longer retained is named by its recorded id with an explicit marker, and
        // never by a guessed current account.
        var removed = Row(window, RunRemoved);
        Require(removed.Title == RemovedPair + " · " + NotRecorded &&
                removed.PairsLabel == RemovedPair + " · " + NotRecorded,
            "UI11 missing pair: a run whose pair is gone uses the recorded id plus Not recorded: " + removed.Title);
        Require(removed.AccountsLabel == NotRecorded &&
                !removed.AccountsLabel.Contains(AccountLabelA, StringComparison.Ordinal) &&
                !removed.AccountsLabel.Contains(AccountLabelB, StringComparison.Ordinal),
            "UI11 missing pair: a missing pair never borrows a current account label");
        Require(removed.StatusLabel == "Needs Attention" && removed.StatusGlyph == "!",
            "UI11 status: a blocked run is labelled Needs Attention with the shared attention glyph");

        var running = Row(window, RunRunning);
        Require(running.StatusLabel == "Running" && running.StatusGlyph == "↻" &&
                running.CompletedLabel == "In progress" && running.DurationLabel == "In progress",
            "UI11 running: an unfinished running run states its real state instead of a completion time");
        var queued = Row(window, RunQueued);
        Require(queued.StatusLabel == "Queued" && queued.CompletedLabel == NotRecorded &&
                queued.DurationLabel == NotRecorded,
            "UI11 queued: an accepted run with no completion states Not recorded rather than In progress");
        Require(Row(window, RunStopped).StatusLabel == "Stopped",
            "UI11 stopped: the retained Stopped outcome overrides the base recorded status");
        Require(Row(window, RunRejected).StatusLabel == "Rejected" && Row(window, RunStopped).StatusGlyph == "■",
            "UI11 status: rejected status is humanised and stopped uses the stop icon");
        Require(Row(window, RunAttention).TotalsLabel == NotRecorded &&
                Row(window, RunAttention).StatusLabel == "Needs Attention",
            "UI11 attention: a failed pair reports Needs Attention with its counts left Not recorded");

        // A truncated legacy record stays a visible, identifiable row rather than crashing the page.
        var legacy = Row(window, RunLegacy);
        Require(legacy.Title == "Sync Run " + RunLegacy &&
                legacy.StatusLine.Contains("Started By: " + NotRecorded, StringComparison.Ordinal),
            "UI11 legacy: an unreadable retained request stays a visible row identified by its run id");
        Require(!legacy.RequestReadable && Fields(legacy).GetValueOrDefault("Folder Pairs") == NotRecorded &&
                Fields(legacy).GetValueOrDefault("Recorded Pairs")?.Contains("could not be read", StringComparison.Ordinal) == true,
            "UI11 legacy: unreadable retained JSON is stated explicitly as not recorded");
        Require(RowCollection(window).Count == 9,
            "UI11 legacy: a malformed legacy record never removes or crashes the other rows");
    }

    // --- the selected run's details: sidebar, plain-text record and expanded dialog ----------------

    private static void CheckSelectionDetails(MainWindow window)
    {
        var grid = RunGrid(window);
        grid.SelectedItem = Row(window, RunMulti);
        Drain();
        Require(Field<Border>(window, "_workspaceInspector").Visibility == Visibility.Visible &&
                Field<Grid>(window, "_workspaceBody").ColumnDefinitions[1].Width.Value > 0,
            "UI11 details: selecting a run opens the shared details sidebar");
        Require(Field<TextBlock>(window, "_workspaceDetailTitle").Text == "Run Details",
            "UI11 details: the opened inspector identifies the selected entity as a run");
        var fields = RenderedFields(window);
        Require(fields.GetValueOrDefault("Run ID:") == RunMulti,
            "UI11 details: the selected run states its exact retained run id");
        Require(fields.GetValueOrDefault("Folder Pairs:") == "Documents\nDocuments",
            "UI11 details: every associated folder pair is stated, including identical pair names");
        Require(fields.GetValueOrDefault("Accounts:") == AccountLabelA + " · " + AccountLabelB + MultiAccountSuffix,
            "UI11 details: a multi-account run states each account explicitly");
        Require(fields.GetValueOrDefault("Started By:") == "Manual" &&
                fields.GetValueOrDefault("Completed:") is { Length: > 0 } completed && completed != NotRecorded &&
                fields.GetValueOrDefault("Duration:") == "02:03:07" &&
                fields.GetValueOrDefault("Status:") == "Completed",
            "UI11 details: trigger, completion, duration and status are all projected");
        Require(fields.GetValueOrDefault("Uploaded:") == "4" && fields.GetValueOrDefault("Downloaded:") == "2" &&
                fields.GetValueOrDefault("Unchanged:") == "14" &&
                fields.GetValueOrDefault("Needs Attention:") == "0",
            "UI11 details: the recorded counts are projected exactly");
        var row = Row(window, RunMulti);
        string? copied = null;
        MainWindow.ClipboardTextSetterForTest = text => copied = text;
        try { Call(window, "CopyActivityDetails_Click", window, new RoutedEventArgs()); }
        finally { MainWindow.ClipboardTextSetterForTest = null; }
        Require(copied == row.DetailText && copied.Contains("Run ID: " + RunMulti, StringComparison.Ordinal),
            "UI11 details: the actual copy-details command copies the selected run, not stale file activity");
        var technical = Field<TextBox>(window, "TechnicalText").Text;
        Require(technical.Contains("Run ID: " + RunMulti, StringComparison.Ordinal) &&
                technical.Contains("Recorded Kind: run_all", StringComparison.Ordinal) &&
                technical.Contains("Recorded Status: completed", StringComparison.Ordinal) &&
                technical.Contains("\"failedPairs\":0", StringComparison.Ordinal),
            "UI11 details: the technical section keeps the exact retained status and result");
        // The inspector's Activity actions cannot act on a run, so none of them may stay enabled.
        Require(!Field<Button>(window, "_workspacePairAction").IsEnabled &&
                !Field<Button>(window, "_workspaceRecoveryAction").IsEnabled &&
                !Field<Button>(window, "_inspectorLocal").IsEnabled &&
                !Field<Button>(window, "_inspectorCloud").IsEnabled &&
                !Field<Button>(window, "_inspectorLink").IsEnabled &&
                ReferenceEquals(Field<FrameworkElement>(window, "_inspectorTypeIcon").DataContext, row),
            "UI11 details: no Activity action stays enabled for a selected run");

        // Expanding moves the same content into the dialog, and returning keeps the exact selection.
        var host = window._detailsHost;
        Require(host is not null, "UI11 details: the Runs surface reuses the shared details host");
        var details = host!;
        var content = details.Content;
        details.Expand(window);
        Drain();
        Require(details.Expanded && details.Dialog is { IsVisible: true } &&
                ReferenceEquals(details.Dialog!.HostedContent, content),
            "UI11 details: expanding opens one dialog hosting the same details element, not a copy");
        Require(ReferenceEquals(grid.SelectedItem, row) &&
                RenderedFields(window).GetValueOrDefault("Run ID:") == RunMulti,
            "UI11 details: expanding keeps the selected run and its exact values");
        details.Collapse();
        Drain();
        Require(!details.Expanded && ReferenceEquals(grid.SelectedItem, row) &&
                RenderedFields(window).GetValueOrDefault("Run ID:") == RunMulti,
            "UI11 details: returning from the dialog restores the same selection in the sidebar");
    }

    // --- the shared Simple/Detailed switch and its persisted per-page preference -------------------

    private static void CheckViewSwitch(MainWindow window, UiPreferencesStore preferences)
    {
        var grid = RunGrid(window);
        var viewSwitch = Field<SimpleDetailedSwitch>(window, "_runViewSwitch");
        Search(window).Text = "Documents";
        Drain();
        var selected = grid.SelectedItem;
        var filtered = RowCollection(window).Count;
        Require(filtered == 7, "UI11 switch: the typed filter narrows the presented rows before the switch is used");

        viewSwitch.DetailedSegment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Drain();
        var headers = grid.Columns.Select(column => column.Header?.ToString()).ToList();
        Require(grid.HeadersVisibility == DataGridHeadersVisibility.Column &&
                Math.Abs(grid.RowHeight - SharedPresentation.DetailedRowHeight) < 0.1,
            "UI11 switch: Detailed shows the column headings and the detailed row density");
        Require(headers.Contains("Folder Pairs") && headers.Contains("Accounts") && headers.Contains("Started") &&
                headers.Contains("Completed") && headers.Contains("Started By") && headers.Contains("Totals") &&
                headers.Count(header => header == "Duration") == 1 && headers.Count(header => header == "Run") == 1,
            "UI11 switch: Detailed adds the run detail columns without duplicating an always-visible column");
        Require(ReferenceEquals(grid.SelectedItem, selected) && Search(window).Text == "Documents" &&
                RowCollection(window).Count == filtered,
            "UI11 switch: the same rows, selection and filters survive the switch");
        Require(ScrollViewer.GetHorizontalScrollBarVisibility(grid) == ScrollBarVisibility.Auto,
            "UI11 switch: a detailed row set may scroll horizontally");
        PumpUntil(() => preferences.Load().PageViewModes?.GetValueOrDefault("runs") == UiPreferencesStore.DetailedView,
            "the Runs view preference to be saved under the existing per-page key");

        viewSwitch.SimpleSegment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Drain();
        Require(grid.HeadersVisibility == DataGridHeadersVisibility.None &&
                !grid.Columns.Select(column => column.Header?.ToString()).Contains("Totals") &&
                ScrollViewer.GetHorizontalScrollBarVisibility(grid) == ScrollBarVisibility.Disabled,
            "UI11 switch: returning to Simple removes the detail columns and their horizontal viewport");
        Require(ReferenceEquals(grid.SelectedItem, selected) &&
                RowCollection(window).Count == filtered,
            "UI11 switch: returning to Simple keeps the exact selected run and the filter");

        // The saved per-page mode is restored from the existing store on a fresh preference load.
        PumpUntil(() => preferences.Load().PageViewModes?.GetValueOrDefault("runs") == UiPreferencesStore.SimpleView,
            "the Simple switch write to finish before simulating the next preference load");
        Pump(preferences.SaveAsync(Preferences(preferences, UiPreferencesStore.DetailedView)));
        Set(window, "_workspacePreferences", preferences.Load());
        Call(window, "LoadViewModes");
        Drain();
        Require(grid.HeadersVisibility == DataGridHeadersVisibility.Column && viewSwitch.IsDetailed,
            "UI11 switch: a saved Detailed Runs preference is restored through the existing preferences store");
        Require(preferences.Load().PageViewModes?.GetValueOrDefault("activity") is null,
            "UI11 switch: the Runs preference is stored under its own page key and cannot move another page");
        Pump(preferences.SaveAsync(Preferences(preferences, UiPreferencesStore.SimpleView)));
        Set(window, "_workspacePreferences", preferences.Load());
        Call(window, "LoadViewModes");
        Drain();
        Require(grid.HeadersVisibility == DataGridHeadersVisibility.None && !viewSwitch.IsDetailed,
            "UI11 switch: a saved Simple Runs preference is restored the same way");
        Search(window).Text = string.Empty;
        Drain();
    }

    private static UiPreferences Preferences(UiPreferencesStore store, string mode)
    {
        var modes = (store.Load().PageViewModes ?? new Dictionary<string, string>(StringComparer.Ordinal))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        modes["runs"] = mode;
        return store.Load() with { PageViewModes = modes };
    }

    // --- filters: search, pair, date, status, trigger and the exact account scope ------------------

    private static void CheckFilters(MainWindow window)
    {
        ClearFilters(window);
        Require(RowCollection(window).Count == 9, "UI11 filters: clearing every filter presents every loaded run");

        var status = Field<ComboBox>(window, "_runStatusFilter");
        Require(status.Visibility == Visibility.Visible &&
                Field<ComboBox>(window, "_runTriggerFilter").Visibility == Visibility.Visible,
            "UI11 filters: the Runs status and trigger filters are offered in the existing Filters area");
        status.SelectedItem = "Completed";
        Drain();
        Require(RowCollection(window).Count == 3 && RowCollection(window).All(row => row.StatusLabel == "Completed"),
            "UI11 filters: the status filter presents exactly the runs with that recorded status");
        status.SelectedItem = "All run statuses";
        Drain();

        var trigger = Field<ComboBox>(window, "_runTriggerFilter");
        trigger.SelectedItem = "Manual";
        Drain();
        Require(RowCollection(window).Count == 4 && RowCollection(window).All(row => row.TriggerLabel == "Manual"),
            "UI11 filters: the trigger filter presents exactly the runs started by that recorded trigger");
        trigger.SelectedItem = "All triggers";
        Drain();

        var pairBox = Field<ComboBox>(window, "ActivityPairBox");
        pairBox.SelectedItem = window.ActivityPairChoices.First(choice => choice.Id == PairB);
        Drain();
        Require(RowCollection(window).Count == 4 && RowCollection(window).All(row => row.PairIds.Contains(PairB)),
            "UI11 filters: the reused pair filter presents exactly the runs that touched that pair");
        pairBox.SelectedItem = window.ActivityPairChoices.First(choice => choice.Id is null && choice.ConnectionId is null);
        Drain();

        var date = Field<ComboBox>(window, "ActivityDateBox");
        date.SelectedIndex = 1;
        Drain();
        var today = RowCollection(window).Select(row => row.Id).ToArray();
        Require(today.Contains(RunMulti) && today.Contains(RunRunning) && today.Contains(RunQueued) &&
                !today.Contains(RunAttention) && !today.Contains(RunLegacy),
            "UI11 filters: the reused date filter uses the recorded start time and keeps only that range");
        date.SelectedIndex = 0;
        Drain();

        // A legacy run with an unreadable request is still findable by its run id.
        var search = Search(window);
        search.Text = RunLegacy;
        Drain();
        Require(RowCollection(window).Count == 1 && RowCollection(window)[0].Id == RunLegacy,
            "UI11 filters: a malformed legacy run stays findable by its exact run id");
        search.Text = string.Empty;
        Drain();

        // The exact app-wide account scope: a run that touches the scoped account stays visible with
        // every account it really touched, and a run that does not touch it is never leaked.
        SetAccountScope(window, AccountB);
        Require(RowCollection(window).Select(row => row.Id).SequenceEqual(
                new[] { RunQueued, RunMulti, RunPartial, RunStopped }),
            "UI11 scope: only runs that touch the scoped account are presented");
        Require(Row(window, RunMulti).AccountsLabel == AccountLabelA + " · " + AccountLabelB + MultiAccountSuffix,
            "UI11 scope: a multi-account run stays honest about every account it touched");
        Require(RowCollection(window).All(row => !row.Id.Equals(RunLegacy, StringComparison.Ordinal)),
            "UI11 scope: a run whose retained request cannot be read is not claimed for the scoped account");
        SetAccountScope(window, null);
        Require(RowCollection(window).Count == 9,
            "UI11 scope: returning to All Accounts presents every loaded run again");
    }

    // --- selection identity across filtering and view changes -------------------------------------

    private static void CheckSelectionDiscard(MainWindow window)
    {
        var grid = RunGrid(window);
        grid.SelectedItem = Row(window, RunRemoved);
        Drain();
        Require(Field<string?>(window, "_runSelectedId") == RunRemoved &&
                RenderedFields(window).GetValueOrDefault("Run ID:") == RunRemoved,
            "UI11 selection: the inspected run identity is retained by id, not by row index");

        var status = Field<ComboBox>(window, "_runStatusFilter");
        status.SelectedItem = "Completed";
        Drain();
        Require(!RowExists(window, RunRemoved) && grid.SelectedItem is null &&
                Field<string?>(window, "_runSelectedId") is null &&
                Field<Border>(window, "_workspaceInspector").Visibility == Visibility.Collapsed,
            "UI11 selection: a run filtered out of the presented set discards its selection and its details");

        var retained = RowCollection(window)[0];
        grid.SelectedItem = retained;
        Drain();
        status.SelectedItem = "All run statuses";
        Drain();
        Require(ReferenceEquals(grid.SelectedItem, Row(window, retained.Id)) &&
                Field<string?>(window, "_runSelectedId") == retained.Id &&
                RenderedFields(window).GetValueOrDefault("Run ID:") == retained.Id,
            "UI11 selection: a run that is still presented keeps its exact selection across a filter change");
    }

    // --- a delayed page must be re-filtered against the scope that is current when it arrives ------

    // --- a header account change never navigates or re-opens retained run details from another page

    private static void CheckAccountSwitchPreservesPage(MainWindow window)
    {
        ClearFilters(window);
        var runsTab = Field<TabControl>(window, "_activityTabs");
        var grid = RunGrid(window);
        var inspector = Field<Border>(window, "_workspaceInspector");
        var activityPage = window.ActivityPage;
        var attentionPage = Field<Border>(window, "_attentionPage");
        var dashboardPage = window.DashboardPage;

        // Leave a retained run selected with its details presented on the visible Runs surface, as a
        // user would before walking over to Needs Attention.
        Call(window, "NavigateTo", "Activity", true);
        Drain();
        runsTab.SelectedIndex = 2;
        Drain();
        PumpUntil(() => RowExists(window, RunMulti), "the retained runs to be presented before the switch");
        grid.SelectedItem = Row(window, RunMulti);
        Drain();
        Require(Field<string?>(window, "_runSelectedId") == RunMulti &&
                RenderedFields(window).GetValueOrDefault("Run ID:") == RunMulti,
            "UI11 account switch: a retained run and its details are established on the visible Runs surface");

        // Needs Attention, All Accounts to one account through that page's own header picker. The page
        // must stay put, the off-screen Runs surface must not reopen, and the previous scope's run
        // identity, selection and inspector must be dropped rather than re-presented here.
        Call(window, "NavigateTo", "Attention", true);
        Drain();
        Require(Field<string>(window, "_workspacePage") == "Attention" && attentionPage.Visibility == Visibility.Visible,
            "UI11 account switch: Needs Attention is the visible page before the header account changes");
        SetAccountScope(window, AccountB, "Needs Attention");
        Require(Field<string>(window, "_workspacePage") == "Attention" && attentionPage.Visibility == Visibility.Visible,
            "UI11 account switch: changing the header account from Needs Attention keeps that page visible");
        Require(activityPage.Visibility != Visibility.Visible && inspector.Visibility != Visibility.Visible,
            "UI11 account switch: the off-screen Runs surface does not reopen and its inspector stays closed");
        Require(Field<string?>(window, "_runSelectedId") is null && grid.SelectedItem is null,
            "UI11 account switch: the obsolete retained run identity and selection are dropped with the scope");

        // The same change in the other direction, still from Needs Attention.
        SetAccountScope(window, null, "Needs Attention");
        Require(Field<string>(window, "_workspacePage") == "Attention" && attentionPage.Visibility == Visibility.Visible,
            "UI11 account switch: returning to All Accounts from Needs Attention keeps that page visible");

        // The equivalent Home case.
        Call(window, "NavigateTo", "Dashboard", true);
        Drain();
        Require(Field<string>(window, "_workspacePage") == "Dashboard" && dashboardPage.Visibility == Visibility.Visible,
            "UI11 account switch: Home is the visible page before the header account changes");
        SetAccountScope(window, AccountB);
        Require(Field<string>(window, "_workspacePage") == "Dashboard" && dashboardPage.Visibility == Visibility.Visible &&
                activityPage.Visibility != Visibility.Visible,
            "UI11 account switch: changing the header account from Home keeps Home visible");

        // A programmatic Runs re-render while Activity is hidden must not navigate or inspect.
        Call(window, "NavigateTo", "Attention", true);
        Drain();
        Call(window, "RenderRunHistory");
        Drain();
        Require(Field<string>(window, "_workspacePage") == "Attention" && attentionPage.Visibility == Visibility.Visible &&
                activityPage.Visibility != Visibility.Visible && inspector.Visibility != Visibility.Visible,
            "UI11 account switch: a programmatic Runs re-render while Activity is hidden cannot navigate or inspect");

        // The visible Runs surface still follows the account scope, and an explicit user activation
        // still presents exactly that run.
        Call(window, "NavigateTo", "Activity", true);
        Drain();
        runsTab.SelectedIndex = 2;
        Drain();
        SetAccountScope(window, AccountB);
        Require(RowCollection(window).Select(row => row.Id).SequenceEqual(
                new[] { RunQueued, RunMulti, RunPartial, RunStopped }),
            "UI11 account switch: on the visible Runs surface the account scope still re-projects the list");
        Require(Field<string?>(window, "_runSelectedId") is null && grid.SelectedItem is null,
            "UI11 account switch: the visible Runs list drops the previous scope's selection");
        grid.SelectedItem = Row(window, RunMulti);
        Drain();
        Require(Field<string?>(window, "_runSelectedId") == RunMulti &&
                RenderedFields(window).GetValueOrDefault("Run ID:") == RunMulti,
            "UI11 account switch: explicit user activation of a run row still presents that run's details");
        SetAccountScope(window, null);
        Drain();
    }

    private static void CheckDelayedScopeRefilter(MainWindow window,
        List<TaskCompletionSource<IReadOnlyList<SyncAuthorityRunHistory>>> pending,
        List<(int Limit, int Offset)> requests)
    {
        ClearFilters(window);
        var grid = RunGrid(window);
        grid.SelectedItem = Row(window, RunRejected);
        Drain();
        Require(Field<string?>(window, "_runSelectedId") == RunRejected,
            "UI11 delayed page: an account-scoped run is selected before the delayed page starts");

        var before = pending.Count;
        var load = (Task)Call(window, "LoadRunHistoryAsync", true)!;
        PumpUntil(() => pending.Count == before + 1, "the delayed Runs page to start");
        Require(requests[^1] == (50, 0), "UI11 delayed page: the delayed request keeps the bounded 50 at offset 0");
        SetAccountScope(window, AccountB);
        Require(!RowExists(window, RunRejected) && Field<string?>(window, "_runSelectedId") is null,
            "UI11 delayed page: changing the account scope immediately hides the unrelated run and its selection");
        pending[^1].SetResult(FullPage());
        Pump(load);
        Drain();
        PumpUntil(() => !Field<bool>(window, "_loadingRuns"), "the delayed page to settle");
        Require(RowCollection(window).Select(row => row.Id).SequenceEqual(
                new[] { RunQueued, RunMulti, RunPartial, RunStopped }),
            "UI11 delayed page: the page that arrived late is re-filtered against the current scope");
        Require(Field<string?>(window, "_runSelectedId") is null,
            "UI11 delayed page: the scope change did not resurrect the discarded selection");
        SetAccountScope(window, null);
        Require(RowCollection(window).Count == 9,
            "UI11 delayed page: All Accounts presents the full retained page again");
    }

    // --- bounded Load More, duplicate identities and a required newer reset ------------------------

    private static void CheckLoadMoreAndReset(MainWindow window,
        List<TaskCompletionSource<IReadOnlyList<SyncAuthorityRunHistory>>> pending,
        List<(int Limit, int Offset)> requests)
    {
        ClearFilters(window);
        var before = pending.Count;
        var more = (Task)Call(window, "LoadRunHistoryAsync", false)!;
        PumpUntil(() => pending.Count == before + 1, "the Load More page to start");
        Require(requests[^1] == (50, 9),
            "UI11 paging: Load More keeps the bounded 50 and continues at the loaded offset");
        Require(CountText(window).Text == "Matching runs: 9 · Loaded: 9",
            "UI11 paging: the count still describes only what is loaded while the next page is pending");
        // The next page overlaps the first one, as a newly retained run between the two reads would.
        pending[^1].SetResult(
        [
            Run(RunRemoved, "run_pair", TodayAt(0, 30, 3), TodayAt(0, 31, 3), "blocked",
                RequestJson([RemovedPair], "Manual"), "{\"reason\":\"pair not retained\"}"),
            Run("run-older-one", "run_all", TodayAt(0, 0, 6), TodayAt(0, 9, 6), "completed",
                RequestJson([PairA], "Tray"),
                "{\"failedPairs\":0,\"Stopped\":false,\"pairs\":[{\"pairId\":\"pair-runs-a\",\"uploaded\":7," +
                "\"downloaded\":1,\"unchanged\":2,\"conflicts\":0,\"blocked\":0}]}")
        ]);
        Pump(more);
        Drain();
        PumpUntil(() => !Field<bool>(window, "_loadingRuns"), "the Load More page to settle");
        Require(Retained(window).Count == 10 &&
                Retained(window).Select(run => run.Id).Distinct(StringComparer.Ordinal).Count() == 10,
            "UI11 paging: an overlapping page never appends the same retained run id twice");
        Require(Row(window, "run-older-one").TotalsLabel ==
                "Uploaded: 7 · Downloaded: 1 · Unchanged: 2 · Needs Attention: 0",
            "UI11 paging: the older paged run is projected with its own retained totals");
        Require(RowExists(window, RunMulti),
            "UI11 paging: loading an older page keeps every already loaded run");

        // A reset that arrives while a page is in flight must not be dropped.
        before = pending.Count;
        var inFlight = (Task)Call(window, "LoadRunHistoryAsync", false)!;
        PumpUntil(() => pending.Count == before + 1, "the in-flight page before the newer reset");
        Pump((Task)Call(window, "LoadRunHistoryAsync", true)!);
        Require(Field<bool>(window, "_runReloadPending"),
            "UI11 paging: a newer reset is remembered instead of being dropped while a page is in flight");
        pending[^1].SetResult(FullPage());
        PumpUntil(() => pending.Count == before + 2, "the remembered reset to run");
        Require(requests[^1] == (50, 0),
            "UI11 paging: the remembered reset runs at offset 0 after the in-flight page settles");
        pending[^1].SetResult(FullPage());
        Pump(inFlight);
        Drain();
        PumpUntil(() => !Field<bool>(window, "_loadingRuns"), "the remembered reset to settle");
        Require(!Field<bool>(window, "_runReloadPending"),
            "UI11 paging: the remembered reset is consumed exactly once");
        Require(Retained(window).Count == 9 && !RowExists(window, "run-older-one"),
            "UI11 paging: the reset replaces the loaded window without keeping the older page's rows");
        Require(RowCollection(window).Count == 9 && CountText(window).Text == "Matching runs: 9 · Loaded: 9",
            "UI11 paging: the count describes the page that was actually applied");
    }

    // --- the retained current-session comparison stays reachable, including from the details -------

    private static void CheckComparisonReachable(MainWindow window)
    {
        var expander = Field<Expander>(window, "_runComparisonExpander");
        var prior = (Grid)expander.Content;
        Require(
                ContainsElement(prior, Field<DataGrid>(window, "ResultGrid")) &&
                ContainsElement(prior, Field<TextBox>(window, "FilterBox")) &&
                ContainsElement(prior, Field<TextBlock>(window, "SummaryText")),
            "UI11 comparison: the existing current-session comparison keeps its real controls");
        Require(FindDescendant<ComboBox>(prior) is { } runChoice &&
                ReferenceEquals(runChoice.ItemsSource, Field<object>(window, "_workspaceRuns")),
            "UI11 comparison: the comparison keeps its session run selector bound to the same session runs");

        var grid = RunGrid(window);
        grid.SelectedItem = Row(window, RunMulti);
        Drain();
        var action = Field<StackPanel>(window, "_detailFields").Children.OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Current Session Comparison");
        Require(!expander.IsExpanded, "UI11 comparison: the comparison starts collapsed in its existing control");
        action.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Drain();
        Require(expander.IsExpanded,
            "UI11 comparison: the selected run's details make the same existing comparison control reachable");
        var runType = typeof(MainWindow).GetNestedType("WorkspaceRun", BindingFlags.NonPublic)!;
        var sessionRuns = (System.Collections.IList)Field<object>(window, "_workspaceRuns");
        var pair = new SyncPair(PairA, "Documents", @"C:\fixture\documents-a", 1, 1, "/Documents", "id:runs-a", ConnectionId: AccountA);
        var result = new SyncRunResult(PairA, [new SyncDecision("proof.txt", SyncDecisionKind.Uploaded, "Session comparison proof")]);
        var sessionRun = Activator.CreateInstance(runType, pair, result, DateTimeOffset.Now)!;
        sessionRuns.Add(sessionRun);
        FindDescendant<ComboBox>(prior)!.SelectedItem = sessionRun;
        Drain();
        Require(Field<DataGrid>(window, "ResultGrid").Items.OfType<DecisionRow>().Any(item => item.Path == "proof.txt") &&
            Field<TextBlock>(window, "SummaryText").Text.Contains("Uploaded: 1", StringComparison.Ordinal),
            "UI11 comparison: choosing an actual session run renders its decisions and counts");
        Field<TextBox>(window, "FilterBox").Text = "not-present"; Drain();
        Require(Field<DataGrid>(window, "ResultGrid").Items.Count == 0,
            "UI11 comparison: the existing search filters the selected session result");
        Field<TextBox>(window, "FilterBox").Text = ""; Drain();
        expander.IsExpanded = false;
    }

    // --- busy, failure and empty feedback ---------------------------------------------------------

    private static void CheckFailureAndEmptyStates(MainWindow window,
        List<TaskCompletionSource<IReadOnlyList<SyncAuthorityRunHistory>>> pending,
        List<(int Limit, int Offset)> requests)
    {
        ClearFilters(window);
        var before = pending.Count;
        var load = (Task)Call(window, "LoadRunHistoryAsync", true)!;
        PumpUntil(() => pending.Count == before + 1, "the busy Runs page to start");
        Require(Field<TextBlock>(window, "_runNote").Text.StartsWith("Loading", StringComparison.Ordinal) &&
                !Field<Button>(window, "_runLoadMore").IsEnabled,
            "UI11 busy: a page in flight reports its busy state and refuses a second Load More");
        pending[^1].SetResult(FullPage());
        Pump(load);
        Drain();
        PumpUntil(() => !Field<bool>(window, "_loadingRuns"), "the busy page to settle");
        Require(Field<TextBlock>(window, "_runNote").Text.Length == 0 &&
                Field<Button>(window, "_runLoadMore").IsEnabled,
            "UI11 busy: a settled page clears its note and offers Load More again");

        before = pending.Count;
        var failing = (Task)Call(window, "LoadRunHistoryAsync", true)!;
        PumpUntil(() => pending.Count == before + 1, "the failing Runs page to start");
        pending[^1].SetException(new InvalidDataException("fixture page failure"));
        Pump(failing);
        Drain();
        PumpUntil(() => !Field<bool>(window, "_loadingRuns"), "the failing page to settle");
        Require(Field<TextBlock>(window, "_runNote").Text.Contains("could not be loaded", StringComparison.Ordinal) &&
                Field<TextBlock>(window, "_runNote").Text.Contains("retries", StringComparison.Ordinal),
            "UI11 failure: a failed page states the failure and how to retry");
        Require(RowCollection(window).Count == 9,
            "UI11 failure: a failed page never discards the runs that were already loaded");

        before = pending.Count;
        var empty = (Task)Call(window, "LoadRunHistoryAsync", true)!;
        PumpUntil(() => pending.Count == before + 1, "the empty Runs page to start");
        pending[^1].SetResult([]);
        Pump(empty);
        Drain();
        PumpUntil(() => !Field<bool>(window, "_loadingRuns"), "the empty page to settle");
        Require(RowCollection(window).Count == 0 &&
                Field<TextBlock>(window, "_runEmpty").Visibility == Visibility.Visible &&
                Field<TextBlock>(window, "_runEmpty").Text.Contains("retained yet", StringComparison.Ordinal),
            "UI11 empty: a run history with nothing retained explains the empty state instead of a blank list");
        Require(CountText(window).Text == "Matching runs: 0 · Loaded: 0",
            "UI11 empty: the count reports an empty retained history honestly");
    }

    // --- fixture records --------------------------------------------------------------------------

    private static void CheckRetainedProducer(MainWindow window)
    {
        MainWindow.RunHistoryLoaderForTest = null;
        var authority = window.AuthorityHost.Authority;
        var accepted = authority.CreateAcceptedCommandAdmissionAsync(null, "run_all", null, null, null,
            RequestJson([PairA], "CLI"), SyncAuthorityCommandPurpose.NormalEffect);
        Pump(accepted);
        var retainedRunId = accepted.Result.CommandRequestId;
        Pump(authority.TransitionCommandRequestAsync(retainedRunId, "executing"));
        Pump(authority.TransitionCommandRequestAsync(retainedRunId, "completed",
            "{\"completedPairs\":1,\"failedPairs\":0,\"pairs\":[{\"pairId\":\"pair-runs-a\",\"uploaded\":1,\"downloaded\":0,\"unchanged\":2,\"blocked\":0,\"conflicts\":0}]}"));
        ClearFilters(window);
        Pump((Task)Call(window, "LoadRunHistoryAsync", true)!);
        Require(RowCollection(window).Count == 1 && Row(window, retainedRunId).TotalsLabel ==
            "Uploaded: 1 · Downloaded: 0 · Unchanged: 2 · Needs Attention: 0",
            "UI11 retained producer: real command transitions, database loader and Runs projection preserve the result");
        RunGrid(window).SelectedItem = Row(window, retainedRunId); Drain();
        Require(RenderedFields(window).GetValueOrDefault("Started By:") == "CLI",
            "UI11 retained producer: actual selection renders the persisted request trigger");
    }

    // Local-anchored, so the reused date filter is deterministic at any time of day: the first three
    // runs are always today, and every later run is always at least a full day older.
    private static DateTimeOffset TodayAt(int hour, int minute, int daysBack = 0)
    {
        var local = DateTimeOffset.Now.Date.AddDays(-daysBack).AddHours(hour).AddMinutes(minute);
        return new DateTimeOffset(local, DateTimeOffset.Now.Offset);
    }

    private static IReadOnlyList<SyncAuthorityRunHistory> FullPage() =>
    [
        Run(RunQueued, "run_all", TodayAt(0, 38), null, "accepted", RequestJson([PairB], "Explorer"), null),
        Run(RunRunning, "run_pair", TodayAt(0, 35), null, "executing", RequestJson([PairA], "Tray"), null),
        Run(RunMulti, "run_all", TodayAt(0, 31), TodayAt(2, 34).AddSeconds(7), "completed",
            RequestJson([PairA, PairB], "Manual"),
            "{\"pairCount\":2,\"completedPairs\":2,\"failedPairs\":0,\"Stopped\":false,\"pairs\":[" +
            "{\"pairId\":\"pair-runs-a\",\"pairName\":\"Documents\",\"uploaded\":3,\"downloaded\":2," +
            "\"unchanged\":5,\"conflicts\":0,\"blocked\":0,\"stopped\":false,\"failureType\":null}," +
            "{\"pairId\":\"pair-runs-b\",\"pairName\":\"Documents\",\"uploaded\":1,\"downloaded\":0," +
            "\"unchanged\":9,\"conflicts\":0,\"blocked\":0,\"stopped\":false,\"failureType\":null}]}"),
        Run(RunAttention, "run_all", TodayAt(22, 30, 1), TodayAt(22, 34, 1), "completed",
            RequestJson([PairA], "Automatic check"),
            "{\"failedPairs\":1,\"Stopped\":false,\"pairs\":[{\"pairId\":\"pair-runs-a\"," +
            "\"pairName\":\"Documents\",\"uploaded\":null,\"downloaded\":null,\"unchanged\":null," +
            "\"conflicts\":null,\"blocked\":null,\"failureType\":\"IOException\"}]}"),
        Run(RunPartial, "run_all", TodayAt(22, 30, 2), TodayAt(22, 41, 2), "completed",
            RequestJson([PairA, PairB], "Manual"),
            "{\"failedPairs\":0,\"Stopped\":false,\"pairs\":[" +
            "{\"pairId\":\"pair-runs-a\",\"pairName\":\"Documents\",\"uploaded\":2,\"downloaded\":1," +
            "\"unchanged\":4,\"conflicts\":0,\"blocked\":0}," +
            "{\"pairId\":\"pair-runs-b\",\"pairName\":\"Documents\",\"uploaded\":null,\"downloaded\":null," +
            "\"unchanged\":null,\"conflicts\":null,\"blocked\":null}]}"),
        Run(RunStopped, "run_all", TodayAt(0, 30, 2), TodayAt(0, 32, 2), "cancelled",
            RequestJson([PairA, PairB], "Manual"),
            "{\"Stopped\":true,\"pairs\":[{\"pairId\":\"pair-runs-a\",\"pairName\":\"Documents\"," +
            "\"uploaded\":1,\"downloaded\":0,\"unchanged\":3,\"conflicts\":0,\"blocked\":0,\"stopped\":true}]}"),
        Run(RunRemoved, "run_pair", TodayAt(0, 30, 3), TodayAt(0, 31, 3), "blocked",
            RequestJson([RemovedPair], "Manual"), "{\"reason\":\"pair not retained\"}"),
        Run(RunLegacy, "run_all", TodayAt(0, 30, 4), TodayAt(0, 30, 4).AddSeconds(30), "completed",
            "{\"pairIds\":[\"pair-ru", "{oops"),
        Run(RunRejected, "run_pair", TodayAt(0, 30, 5), TodayAt(0, 30, 5).AddSeconds(5), "rejected",
            RequestJson([PairA], "Startup recovery"), "{\"reason\":\"rejected before any effect\"}")
    ];

    private static SyncAuthorityRunHistory Run(string id, string kind, DateTimeOffset started,
        DateTimeOffset? completed, string status, string requestJson, string? resultJson) =>
        new(id, kind, started, completed, status, requestJson, resultJson);

    private static string RequestJson(IReadOnlyList<string> pairIds, string trigger) =>
        "{\"trigger\":\"" + trigger + "\",\"recoveryOnly\":false,\"pairIds\":[" +
        string.Join(",", pairIds.Select(id => "\"" + id + "\"")) + "]}";

    // --- production-consumer helpers ---------------------------------------------------------------

    private static DataGrid RunGrid(MainWindow window) => Field<DataGrid>(window, "_runHistoryGrid");
    private static ObservableCollection<RunHistoryRow> RowCollection(MainWindow window) =>
        Field<ObservableCollection<RunHistoryRow>>(window, "_runRows");
    private static List<SyncAuthorityRunHistory> Retained(MainWindow window) =>
        Field<List<SyncAuthorityRunHistory>>(window, "_retainedRuns");
    private static TextBlock CountText(MainWindow window) => Field<TextBlock>(window, "_runCountText");
    private static TextBox Search(MainWindow window) => Field<TextBox>(window, "ActivitySearchBox");
    private static RunHistoryRow Row(MainWindow window, string id) =>
        RowCollection(window).FirstOrDefault(row => row.Id == id)!;
    private static bool RowExists(MainWindow window, string id) =>
        RowCollection(window).Any(row => row.Id == id);
    private static Dictionary<string, string> Fields(RunHistoryRow row) =>
        row.Fields.ToDictionary(field => field.Label, field => field.Value, StringComparer.Ordinal);

    // The rendered sidebar is the real details panel: each shared Field helper renders its label
    // heading above its value, and the trailing comparison action is a Button rather than a value.
    private static Dictionary<string, string> RenderedFields(MainWindow window)
    {
        var panel = Field<StackPanel>(window, "_detailFields");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in panel.Children.OfType<StackPanel>())
        {
            var texts = field.Children.OfType<TextBlock>().ToArray();
            if (texts.Length >= 2) fields[texts[0].Text] = texts[1].Text;
        }
        return fields;
    }

    private static void ClearFilters(MainWindow window)
    {
        Search(window).Text = string.Empty;
        Field<ComboBox>(window, "ActivityDateBox").SelectedIndex = 0;
        Field<ComboBox>(window, "_runStatusFilter").SelectedItem = "All run statuses";
        Field<ComboBox>(window, "_runTriggerFilter").SelectedItem = "All triggers";
        Field<ComboBox>(window, "ActivityPairBox").SelectedItem = window.ActivityPairChoices
            .First(choice => choice.Id is null && choice.ConnectionId is null);
        SetAccountScope(window, null);
        Drain();
    }

    // The app-wide scope is changed through the real account picker, so the production scope handler
    // and the Runs surface's own scope follow-on both run exactly as they do for a user.
    private static void SetAccountScope(MainWindow window, string? connectionId) =>
        SetAccountScope(window, connectionId, "Activity");

    // The same scope change made through the header picker of the surface the user is actually on.
    private static void SetAccountScope(MainWindow window, string? connectionId, string surface)
    {
        var picker = Field<List<ComboBox>>(window, "_workspaceAccountPickers")
            .First(box => AutomationProperties.GetName(box) == surface + " Account");
        var choice = picker.Items.OfType<ActivityPairChoice>()
            .First(item => string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal));
        picker.SelectedItem = choice;
        Drain();
        PumpUntil(() => string.Equals(Field<string?>(window, "_workspaceAccountId"), connectionId,
            StringComparison.Ordinal), "the app-wide account scope to change");
        Drain();
    }

    private static bool ContainsElement(DependencyObject root, DependencyObject target)
    {
        if (ReferenceEquals(root, target)) return true;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (ContainsElement(child, target)) return true;
        return false;
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool>? predicate = null)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match && (predicate is null || predicate(match))) return match;
            var descendant = FindDescendant(child, predicate);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in FindDescendants<T>(child)) yield return descendant;
        }
    }

    private static TaskCompletionSource<IReadOnlyList<SyncAuthorityRunHistory>> NextRequest(
        List<TaskCompletionSource<IReadOnlyList<SyncAuthorityRunHistory>>> pending, int before, string label)
    {
        PumpUntil(() => pending.Count > before, label);
        return pending[^1];
    }

    private static void ReleasePending(MainWindow window,
        List<TaskCompletionSource<IReadOnlyList<SyncAuthorityRunHistory>>> pending)
    {
        var started = Environment.TickCount64;
        while (Field<bool>(window, "_loadingRuns"))
        {
            foreach (var source in pending) source.TrySetResult([]);
            Drain();
            Thread.Sleep(5);
            if (Environment.TickCount64 - started > 20000)
                throw new InvalidOperationException("Timed out releasing outstanding Runs page loads");
        }
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Pump(Task task)
    {
        var started = Environment.TickCount64;
        while (!task.IsCompleted)
        {
            Drain();
            Thread.Sleep(1);
            if (Environment.TickCount64 - started > 20000)
                throw new InvalidOperationException("Timed out waiting for a Runs task to complete");
        }
        task.GetAwaiter().GetResult();
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

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
