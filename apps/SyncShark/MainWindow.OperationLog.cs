using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBox = System.Windows.Controls.TextBox;
using Binding = System.Windows.Data.Binding;
using Orientation = System.Windows.Controls.Orientation;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using ScrollEventHandler = System.Windows.Controls.Primitives.ScrollEventHandler;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private const string OperationLogViewKey = "operationlog";
    private static readonly FeedDetailColumn[] OperationLogDetailColumns =
    [
        new("Operation", nameof(OperationLogRow.Action), 175, Wrap: true),
        new("Exactly what happened", nameof(OperationLogRow.Detail), 480, Wrap: true),
        new("Progress / duration", nameof(OperationLogRow.Progress), 180, Wrap: true),
        new("Endpoints", nameof(OperationLogRow.Endpoints), 175, Wrap: true),
        new("Run", nameof(OperationLogRow.RunId), 230, Wrap: true)
    ];
    private readonly ObservableCollection<OperationLogRow> _operationLogRows = [];
    private DataGrid? _operationLogGrid;
    private TextBox? _operationLogRun, _operationLogSearch;
    private ComboBox? _operationLogSeverity, _operationLogAction;
    private TextBlock? _operationLogNote;
    private ToggleButton? _operationLogFollow;
    private Button? _operationLogRunLink, _operationLogLoadMore;
    private long _operationLogCursor;
    private int _operationLogGeneration;
    private bool _operationLogLoading;
    private bool _operationLogResetPending;
    private (string? Account, string Run, string Search, string? Severity, string Action)? _operationLogLoadingScope;
    private bool _operationLogHasMore, _operationLogLoadFailed;
    private SyncRunRetryPlan? _operationRetryPlan;
    private CancellationTokenSource? _operationRetryReadCancellation;
    private string? _operationRetryReadDetail;
    private (string Run, string? Pair, string? Account)? _operationRetryScope;
    private string _operationLogPageNote = "";
    internal Func<Task>? OperationRetryReadCheckpointForTest { get; set; }
    private Button? _retrySelected, _retryFailed, _resumeUnattempted, _verifyRunOutcomes;
    private Button? _cancelLogExport;
    private CancellationTokenSource? _logExportCancellation;
    private TextBlock? _operationExportFeedback;
    internal Func<string, bool>? OperationLogExportConfirmForTest { get; set; }
    internal Func<string?>? OperationLogExportPathForTest { get; set; }
    internal Action<int>? OperationLogExportProgressForTest { get; set; }

    private void BuildOperationLog()
    {
        var page = new DockPanel();
        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        _operationLogRun = new TextBox();
        _operationLogRun.TextChanged += (_, _) => { UpdateOperationRunLink(); UpdateOperationRetryActions(); };
        _operationLogSearch = new TextBox();
        controls.Children.Add(SharedPresentation.QuietSearchField(_operationLogRun, "Exact run ID",
            "All runs — enter a run ID", () => _ = LoadOperationLogAsync(true), 280,
            "Enter a retained run ID and press Enter to show its operations, including failures and checks."));
        controls.Children.Add(SharedPresentation.QuietSearchField(_operationLogSearch, "Search operation log",
            "File, operation or error…", () => _ = LoadOperationLogAsync(true), 280,
            "Enter a file, operation or error and press Enter. Searches the complete retained log, not only loaded rows."));
        _operationLogSeverity = new ComboBox { Width = 145, Margin = new Thickness(4, 0, 4, 0),
            ItemsSource = new[] { "All results", "Error", "Warning", "Success", "Information" }, SelectedIndex = 0 };
        _operationLogAction = new ComboBox { Width = 180, Margin = new Thickness(4, 0, 4, 0),
            ItemsSource = new[] { "All actions", "Transfers", "Moves", "Conflicts", "Recovery / deletions", "Needs attention" }, SelectedIndex = 0 };
        AutomationProperties.SetName(_operationLogSeverity, "Filter log by result");
        AutomationProperties.SetName(_operationLogAction, "Filter log by action");
        _operationLogSeverity.ToolTip = "Filter the complete retained log by result, including errors outside the loaded page.";
        _operationLogAction.ToolTip = "Filter the complete retained log by the recorded operation type.";
        _operationLogSeverity.SelectionChanged += (_, _) => _ = LoadOperationLogAsync(true);
        _operationLogAction.SelectionChanged += (_, _) => _ = LoadOperationLogAsync(true);
        controls.Children.Add(_operationLogSeverity); controls.Children.Add(_operationLogAction);
        _operationLogFollow = PressedToggle("Follow live", "Show the latest 500 matching events as they happen. Turn off or load earlier events to read history without jumping. The complete log remains retained and exportable.");
        _operationLogFollow.IsChecked = true;
        _operationLogFollow.Click += async (_, _) =>
        {
            if (_operationLogFollow.IsChecked == true) await LoadOperationLogAsync(true);
        };
        controls.Children.Add(_operationLogFollow);
        controls.Children.Add(AsyncButton("Refresh log", async (_, _) =>
        {
            await Task.Run(() => _authorityHost.RetryPendingActivityPersistenceAsync());
            await LoadOperationLogAsync(true);
        }, true, explanation: "Retry saving pending activity records and reload the retained log for the current account, run and filters. This does not start a sync or change files."));
        _operationLogLoadMore = AsyncButton("Load earlier", async (_, _) =>
        {
            _operationLogFollow.IsChecked = false;
            await LoadOperationLogAsync(false);
        }, true);
        _operationLogLoadMore.ToolTip = "Pause live following and load the preceding page of matching retained operations. No log records are removed.";
        ActionAvailability.Bind(_operationLogLoadMore, OperationLogLoadMoreUnavailableReason);
        controls.Children.Add(_operationLogLoadMore);
        UpdateOperationLogLoadMore();
        controls.Children.Add(AsyncButton("Export complete log", async (_, _) => await ExportOperationLogAsync(), true,
            explanation: "Preview, then save locally all retained operations matching the current account, run and filters, not just visible rows. Nothing is uploaded."));
        _cancelLogExport = AsyncButton("Cancel export", (_, _) =>
        { _logExportCancellation?.Cancel(); return Task.CompletedTask; }, true);
        _cancelLogExport.ToolTip = "Cancel this export. The retained log and any existing destination file stay unchanged.";
        _cancelLogExport.Visibility = Visibility.Collapsed;
        controls.Children.Add(_cancelLogExport);
        _operationLogRunLink = AsyncButton("Open selected run", async (_, _) => await OpenLogRowRunAsync(), true);
        ActionAvailability.Bind(_operationLogRunLink, OperationRunLinkUnavailableReason);
        UpdateOperationRunLink();
        controls.Children.Add(_operationLogRunLink);
        _retrySelected = AsyncButton("Retry selected files", async (_, _) => await RetryLogFilesAsync("all", true), true);
        _retryFailed = AsyncButton("Retry failed files", async (_, _) => await RetryLogFilesAsync("failed", false), true);
        _resumeUnattempted = AsyncButton("Resume unattempted files", async (_, _) => await RetryLogFilesAsync("unattempted", false), true);
        _verifyRunOutcomes = AsyncButton("Verify uncertain outcomes", async (_, _) => await VerifyLogOutcomesAsync(), true);
        foreach (var action in new[] { _retrySelected, _retryFailed, _resumeUnattempted, _verifyRunOutcomes })
        {
            controls.Children.Add(action);
            ActionAvailability.Bind(action, () => OperationRetryUnavailableReason(action));
        }
        UpdateOperationRetryActions();
        DockPanel.SetDock(controls, Dock.Top); page.Children.Add(controls);
        _operationLogNote = Text("Loading retained operation log…", 12, brush: "MutedInk");
        _operationLogNote.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetLiveSetting(_operationLogNote, AutomationLiveSetting.Polite);
        var footer = new StackPanel();
        _operationExportFeedback = Text("", 12, brush: "MutedInk");
        _operationExportFeedback.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_operationExportFeedback, AutomationLiveSetting.Polite);
        footer.Children.Add(_operationExportFeedback); footer.Children.Add(_operationLogNote);
        DockPanel.SetDock(footer, Dock.Bottom); page.Children.Add(footer);
        _operationLogGrid = new DataGrid { ItemsSource = _operationLogRows };
        StyleFeed(_operationLogGrid, activityColumns: true);
        _operationLogGrid.SelectionMode = DataGridSelectionMode.Extended;
        _operationLogGrid.MinRowHeight = SharedPresentation.CompactRowHeight;
        _operationLogGrid.EnableColumnVirtualization = true;
        // One record set and the existing feed chrome serve both modes. The compact item
        // uses the same historical activity projection, branded/file icon and details host.
        var item = new FrameworkElementFactory(typeof(ContentPresenter));
        item.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(OperationLogRow.Activity)));
        item.SetValue(ContentPresenter.ContentTemplateProperty,
            ItemTemplate(trailingTime: false, metadataLink: false, summaryPath: nameof(ActivityRow.EventText),
                titlePath: nameof(ActivityRow.OperationTarget)));
        _operationLogGrid.Columns[0].Header = "File / target";
        _operationLogGrid.Columns[0].SortMemberPath = nameof(OperationLogRow.Target);
        ((DataGridTemplateColumn)_operationLogGrid.Columns[0]).CellTemplate = new DataTemplate { VisualTree = item };
        _operationLogGrid.Columns[1].Width = 110;
        _operationLogGrid.Columns[1].SortMemberPath = nameof(OperationLogRow.Time);
        _operationLogGrid.Columns[2] = new DataGridTextColumn
        { Header = "Result", Binding = new Binding(nameof(OperationLogRow.Severity)), Width = 100 };
        _operationLogGrid.Columns.RemoveAt(4); // Do not retain an inert Activity-only menu.
        // Layout changes, new rows and ScrollIntoView also raise ScrollChanged. Only
        // actual reading/navigation input may turn off follow, not a view-mode change.
        _operationLogGrid.AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler((_, e) =>
        {
            if (e.OriginalSource is ScrollBar { Orientation: Orientation.Vertical })
                _operationLogFollow.IsChecked = false;
        }));
        _operationLogGrid.PreviewMouseWheel += (_, e) =>
        {
            if (e.Delta != 0) _operationLogFollow.IsChecked = false;
        };
        _operationLogGrid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down or
                System.Windows.Input.Key.PageUp or System.Windows.Input.Key.PageDown or
                System.Windows.Input.Key.Home or System.Windows.Input.Key.End)
                _operationLogFollow.IsChecked = false;
        };
        _operationLogGrid.SelectionChanged += (_, _) =>
        {
            UpdateOperationRetryActions();
            UpdateOperationRunLink();
            if (_operationLogGrid.SelectedItem is not OperationLogRow row) return;
            InspectActivity(row.Activity);
            if (row.RunId == _operationLogRun.Text.Trim())
            {
                _ = RefreshOperationRetryPlanAsync();
            }
        };
        AutomationProperties.SetName(_operationLogGrid, "Retained operation log");
        page.Children.Add(_operationLogGrid);
        _activityTabs.Items.Add(new TabItem { Header = "Operation log", Content = page });
        ApplyOperationLogViewMode();
    }

    private void ApplyOperationLogViewMode()
    {
        if (_operationLogGrid is null) return;
        var detailed = IsDetailedView(OperationLogViewKey);
        SharedPresentation.ApplyFeedDetail(_operationLogGrid, detailed, OperationLogDetailColumns,
            SharedPresentation.CompactRowHeight, double.NaN);
        if (_activityTabs.SelectedIndex == 3) _activityViewSwitch?.SetView(detailed, persist: false);
    }

    internal void OpenOperationLog(string? runId)
    {
        if (_operationLogRun is null) return;
        _operationLogRun.Text = runId ?? "";
        _operationLogSearch!.Clear();
        _operationLogSeverity!.SelectedIndex = 0;
        _operationLogAction!.SelectedIndex = 0;
        OpenWorkspaceActivity(3);
        _ = LoadOperationLogAsync(true);
    }

    private void RefreshFollowingOperationLog()
    {
        UpdateOperationRetryActions();
        if (_workspaceReady && _workspacePage == "Activity" && _activityTabs.SelectedIndex == 3 &&
            _operationLogFollow?.IsChecked == true && !_operationLogLoading)
            _ = LoadOperationLogAsync(false);
    }

    private async Task LoadOperationLogAsync(bool reset)
    {
        if (_operationLogNote is null) return;
        var account = _workspaceAccountId;
        var run = _operationLogRun!.Text.Trim();
        var search = _operationLogSearch!.Text.Trim();
        var severity = _operationLogSeverity?.SelectedIndex > 0 ? (string?)_operationLogSeverity.SelectedItem : null;
        var actionFilter = (string?)_operationLogAction?.SelectedItem ?? "All actions";
        var scope = (account, run, search, severity, actionFilter);
        // Opening a tab and setting its filters can request the same reset twice.
        // Coalesce that exact in-flight page, but still invalidate changed scopes.
        if (_operationLogLoading && reset && _operationLogLoadingScope == scope) return;
        if (reset) _operationLogGeneration++;
        if (_operationLogLoading) { _operationLogResetPending |= reset; return; }
        if (!_authorityHost.AuthorityStateExists)
        {
            _operationLogHasMore = false;
            UpdateOperationLogLoadMore();
            _operationLogNote.Text = "No retained operations yet. Operations will appear here as they happen.";
            return;
        }
        _operationLogLoading = true;
        _operationLogLoadingScope = scope;
        _operationLogLoadFailed = false;
        UpdateOperationLogLoadMore();
        var generation = _operationLogGeneration;
        if (_operationRetryScope is { } retryScope && (retryScope.Run != run || retryScope.Account != account))
        {
            _operationRetryReadCancellation?.Cancel();
            _operationRetryScope = null; _operationRetryPlan = null;
            UpdateOperationRetryActions();
        }
        var loadEarlier = !reset && _operationLogFollow!.IsChecked != true;
        long? through = loadEarlier && _operationLogRows.Count > 0
            ? Math.Max(0, (_operationLogRows[0].Entry.Sequence ?? 0) - 1) : null;
        if (reset) _operationLogNote.Text = "Loading the retained operation log…";
        try
        {
            // A live view must start at the retained tail, not replay every old page
            // before it can display the operation actually running now. Cursor readers
            // and complete exports retain their ascending, gap-free query contract.
            var query = new SyncActivityQuery(Severity: severity, RunId: run.Length == 0 ? null : run,
                DetailedLog: true, ThroughSequence: through, NewestLogFirst: true);
            await _authorityHost.MaintainOrdinaryActivityHistoryAsync();
            // The retention window is a SQLite read like the page below, so it runs on the worker with
            // the page instead of on the UI thread: a slow read here would freeze the Operation Log refresh.
            var (retainedWindow, rows, hasMore) = await Task.Run(async () =>
            {
                var window = await _authorityHost.LoadActivityRetentionWindowAsync(runId: query.RunId);
                var page = await _authorityHost.LoadActivityProjectionPageAsync(500, 0, search, actionFilter,
                    connectionId: account, query: query);
                // Peek before the oldest row; an exactly full last page is not evidence
                // of more history. Sequence bounds are stable while new records arrive.
                var more = page.Count == 500 && (await _authorityHost.LoadActivityProjectionPageAsync(1, 0,
                    search, actionFilter, connectionId: account,
                    query: query with { ThroughSequence = Math.Max(0, (page[^1].Sequence ?? 0) - 1) })).Count != 0;
                return (window, page.Reverse().ToArray(), more);
            });
            // Following events must also follow the run's eligibility. In particular,
            // an active run can finish without producing a row matching the current
            // filters. Never leave its old "still active" reason in the footer/buttons.
            // Avoid repeatedly rebuilding a settled retry plan when nothing changed.
            var newEvents = rows.Any(row => row.Sequence > _operationLogCursor);
            if (generation != _operationLogGeneration || account != _workspaceAccountId) return;
            _operationLogHasMore = hasMore;
            if (reset) { _operationLogRows.Clear(); _operationLogCursor = 0; }
            if (!loadEarlier)
            {
                var visible = rows.Select(row => row.EventId).ToHashSet(StringComparer.Ordinal);
                for (var index = _operationLogRows.Count - 1; index >= 0; index--)
                    if (!visible.Contains(_operationLogRows[index].Entry.EventId)) _operationLogRows.RemoveAt(index);
            }
            var retained = _operationLogRows.Select(row => row.Entry.EventId).ToHashSet(StringComparer.Ordinal);
            var insertAt = 0;
            foreach (var row in rows)
            {
                if (row.Sequence is not { } sequence || !retained.Add(row.EventId)) continue;
                if (loadEarlier) _operationLogRows.Insert(insertAt++, new OperationLogRow(row));
                else _operationLogRows.Add(new OperationLogRow(row));
                _operationLogCursor = Math.Max(_operationLogCursor, sequence);
            }
            _operationLogPageNote = $"{_operationLogRows.Count:N0} retained events shown. " +
                (hasMore ? "Earlier events remain retained. Load Earlier pauses live following; Export Complete Log includes every matching event." :
                    _operationLogRows.Count == 0 ? "No events match this run/search/account." : "All matching events currently retained are loaded.") +
                " " + ActivityIncompleteNote() + " " + retainedWindow.Describe();
            // Paint the actual log page before reading eligibility. A large historical
            // approval must not hide its own errors behind a separate expensive read.
            _ = RefreshOperationRetryPlanAsync(reset || newEvents || _operationRetryPlan?.UnavailableReason is not null);
            UpdateOperationLogNote();
            UpdateOperationRunLink();
            if (_operationLogFollow!.IsChecked == true && rows.Length > 0 && _operationLogRows.Count > 0)
                _operationLogGrid!.ScrollIntoView(_operationLogRows[^1]);
        }
        catch (Exception error)
        {
            _operationLogLoadFailed = true;
            _operationLogNote.Text = "Could not read the operation log: " + SyncActivityText.Safe(error.Message) + ". Existing rows are preserved; Refresh retries.";
        }
        finally
        {
            _operationLogLoading = false;
            UpdateOperationLogLoadMore();
            if (_operationLogResetPending) { _operationLogResetPending = false; _ = LoadOperationLogAsync(true); }
        }
    }

    private async Task RefreshOperationRetryPlanAsync(bool force = false)
    {
        if (_closing) return;
        var run = _operationLogRun?.Text.Trim() ?? "";
        var selected = (_operationLogGrid?.SelectedItem as OperationLogRow)?.Entry;
        var scope = (Run: run, Pair: selected?.RunId == run ? selected.PairId : null, Account: _workspaceAccountId);
        if (_operationRetryScope == scope && (_operationRetryReadCancellation is not null ||
                !force && _operationRetryPlan is not null)) return;
        _operationRetryReadCancellation?.Cancel();
        _operationRetryScope = scope;
        _operationRetryPlan = null;
        _operationRetryReadDetail = null;
        if (run.Length == 0)
        {
            _operationRetryReadCancellation = null;
            UpdateOperationLogNote(); UpdateOperationRetryActions(); return;
        }
        using var cancellation = new CancellationTokenSource();
        _operationRetryReadCancellation = cancellation;
        UpdateOperationLogNote(); UpdateOperationRetryActions();
        try
        {
            if (OperationRetryReadCheckpointForTest is { } checkpoint) await checkpoint();
            var plan = await Task.Run(() => _authorityHost.LoadRunRetryAsync(run, cancellation.Token, scope.Pair,
                detail => Dispatcher.Invoke(() =>
                {
                    if (_closing || _operationRetryScope != scope || cancellation.IsCancellationRequested) return;
                    _operationRetryReadDetail = detail;
                    UpdateOperationLogNote();
                })), cancellation.Token);
            if (_operationRetryScope == scope && !cancellation.IsCancellationRequested &&
                _operationLogRun?.Text.Trim() == run && _workspaceAccountId == scope.Account)
                _operationRetryPlan = plan;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_operationRetryScope == scope && !cancellation.IsCancellationRequested)
                _operationRetryPlan = new(run, scope.Pair, "Retry availability could not be read: " + SyncActivityText.Failure(error), []);
        }
        finally
        {
            if (ReferenceEquals(_operationRetryReadCancellation, cancellation))
            {
                _operationRetryReadCancellation = null;
                UpdateOperationLogNote(); UpdateOperationRetryActions();
            }
        }
    }

    private void UpdateOperationLogNote()
    {
        if (_operationLogNote is null || _operationLogLoadFailed) return;
        _operationLogNote.Text = _operationLogPageNote;
        if (_operationRetryReadCancellation is not null)
            _operationLogNote.Text += " " + (_operationRetryReadDetail ??
                "Checking retry eligibility against retained file outcomes; the log is ready to read.");
        else if (_operationRetryPlan is { } retry)
            _operationLogNote.Text += " " + (retry.UnavailableReason ??
                $"Retry eligibility: {retry.Items.Count(item => item.State == "Failed"):N0} failed remaining; {retry.Items.Count(item => item.State == "Not attempted"):N0} not attempted; {retry.Items.Count(item => item.State == "Verify first"):N0} require verification.");
    }

    private string OperationLogLoadMoreUnavailableReason() => _operationLogLoading
        ? "The matching operation-log page is being loaded. Wait for it to finish."
        : !_authorityHost.AuthorityStateExists ? "There are no retained operations yet."
        : _operationLogLoadFailed ? "The log could not be read. Use Refresh Log to retry; existing rows are preserved."
        : "All matching events currently retained are loaded. Follow Live or Refresh Log will show newly recorded operations.";

    private void UpdateOperationLogLoadMore()
    {
        if (_operationLogLoadMore is null) return;
        _operationLogLoadMore.IsEnabled = !_operationLogLoading && !_operationLogLoadFailed &&
            _operationLogHasMore && _authorityHost.AuthorityStateExists;
        ActionAvailability.Refresh(_operationLogLoadMore);
    }

    private void UpdateOperationRetryActions()
    {
        if (_retrySelected is null) return;
        var items = _operationRetryPlan?.Items ?? [];
        var ready = !UiOperationBusy && HasCurrentOperationRetryPlan && _operationRetryPlan!.UnavailableReason is null;
        _retrySelected.IsEnabled = ready && (_operationLogGrid?.SelectedItems.OfType<OperationLogRow>()
            .Any(row => items.Any(item => item.Path == row.Entry.RelativePath && item.CanRetry)) ?? false);
        _retryFailed!.IsEnabled = ready && items.Any(item => item.State == "Failed" && item.CanRetry);
        _resumeUnattempted!.IsEnabled = ready && items.Any(item => item.State == "Not attempted" && item.CanRetry);
        _verifyRunOutcomes!.IsEnabled = ready && items.Any(item => item.State == "Verify first");
        foreach (var button in new[] { _retrySelected, _retryFailed, _resumeUnattempted, _verifyRunOutcomes })
            ActionAvailability.Refresh(button!);
    }

    private bool HasCurrentOperationRetryPlan => !_closing && _operationRetryReadCancellation is null &&
        _operationRetryPlan is not null && _operationRetryScope is { } scope &&
        scope.Run == _operationLogRun?.Text.Trim() && scope.Account == _workspaceAccountId;

    private string OperationRetryUnavailableReason(Button action)
    {
        if (UiOperationBusy) return "A scan or sync is running. Finish or stop it safely first.";
        if (string.IsNullOrWhiteSpace(_operationLogRun?.Text)) return "Open an exact retained run first.";
        if (!HasCurrentOperationRetryPlan || _operationRetryPlan is not { } plan)
            return "Retry eligibility for this run is still being loaded.";
        if (plan.UnavailableReason is { } reason) return reason;
        if (action == _resumeUnattempted) return "This run has no unattempted files eligible to resume.";
        if (action == _verifyRunOutcomes) return "This run has no uncertain file outcomes requiring verification.";
        if (action == _retryFailed) return RetryFailedUnavailableReason(plan);
        return RetrySelectedUnavailableReason(plan);
    }

    // A disabled Retry explanation describes this run's actual retained outcomes. It must never
    // assert a state class that is not present, and an empty item list is not all-complete.
    private string RetryFailedUnavailableReason(SyncRunRetryPlan plan)
    {
        const string summary = "This run has no failed files currently eligible for a safe retry.";
        if (plan.Items.Count == 0) return summary + " This run retains no approved file choices to retry.";
        var groups = plan.Items.GroupBy(item => item.State, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Count():N0} {Label(group.Key)}").ToArray();
        var explanation = summary + " Retained outcomes: " + string.Join("; ", groups) + ".";
        var conflicts = plan.Items.Count(item => item.State == "Conflict");
        var uncertain = plan.Items.Count(item => item.State == "Verify first");
        if (conflicts > 0) explanation += $" {conflicts:N0} conflicting file(s) must be resolved first.";
        if (uncertain > 0) explanation += $" {uncertain:N0} uncertain outcome(s) must be verified first.";
        if (plan.Items.All(item => item.State == "Completed"))
            explanation += " Every approved file in this run is already completed; retry never replays a completed file.";
        var retained = plan.Items.Where(item => !item.CanRetry).Select(item => item.Reason)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (retained.Length > 0)
        {
            explanation += " " + string.Join(" ", retained.Take(3));
            if (retained.Length > 3)
                explanation += $" Plus {retained.Length - 3:N0} more distinct retained reason(s) in the log.";
        }
        return explanation;

        static string Label(string state) => state switch
        {
            "Failed" => "failed",
            "Not attempted" => "not attempted",
            "Completed" => "already completed",
            "Skipped" => "explicitly skipped",
            "Conflict" => "conflicting",
            "Verify first" => "awaiting verification",
            _ => state.ToLowerInvariant()
        };
    }

    // Retry Selected is enabled only for a failed or unattempted approved choice. When it is
    // disabled with rows selected, the explanation names each selected file's real retained
    // state and reason, including the exact linked retry that completed it.
    private string RetrySelectedUnavailableReason(SyncRunRetryPlan plan)
    {
        var selected = _operationLogGrid?.SelectedItems.OfType<OperationLogRow>().ToArray() ?? [];
        if (selected.Length == 0)
            return "Select a retryable failed or unattempted file from this run. Completed files are not replayed.";
        var explained = selected.Select(row =>
        {
            var path = row.Entry.RelativePath;
            var item = plan.Items.FirstOrDefault(candidate => candidate.Path == path);
            return item is null
                ? $"'{path}' is not an approved file choice in this run, so retry leaves it untouched."
                : $"'{path}' is retained as {item.State}: {item.Reason}";
        });
        return string.Join(" ", explained) +
            " Only an approved failed or unattempted file can be retried; completed files are never replayed.";
    }

    private string OperationRunLinkUnavailableReason() =>
        "Select an event with a recorded run ID or enter an exact retained run ID first.";

    private void UpdateOperationRunLink()
    {
        if (_operationLogRunLink is null) return;
        var runId = (_operationLogGrid?.SelectedItem as OperationLogRow)?.RunId;
        if (string.IsNullOrWhiteSpace(runId)) runId = _operationLogRun?.Text.Trim();
        _operationLogRunLink.IsEnabled = !string.IsNullOrWhiteSpace(runId);
        if (_operationLogRunLink.IsEnabled)
        {
            _operationLogRunLink.ToolTip = "Open the retained result for run " + runId;
            AutomationProperties.SetHelpText(_operationLogRunLink, (string)_operationLogRunLink.ToolTip);
        }
        ActionAvailability.Refresh(_operationLogRunLink);
    }

    private async Task RetryLogFilesAsync(string mode, bool selectedOnly)
    {
        if (UiOperationBusy || !HasCurrentOperationRetryPlan || _operationRetryPlan is not { } plan) return;
        var selected = selectedOnly ? _operationLogGrid!.SelectedItems.OfType<OperationLogRow>()
            .Where(row => row.Entry.PairId == plan.PairId)
            .Select(row => row.Entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : null;
        _runActive = true;
        ResetRunPresentation("Preparing retry", "Checking retained approvals and previous outcomes. Completed files will not be replayed.");
        SetRunControls(); UpdateOperationRetryActions();
        string? retryRunId = null;
        try
        {
            _operationLogNote!.Text = "Retry started. Validating the originally approved paths; completed files will not be replayed.";
            var result = await Task.Run(() => _authorityHost.RetryRunAsync(plan.RunId, selected, mode,
                async progress =>
                {
                    if (retryRunId is null && _authorityHost.ActiveRun?.Id is { } id)
                    {
                        retryRunId = id;
                        await Dispatcher.InvokeAsync(() =>
                        { _operationLogFollow!.IsChecked = true; OpenOperationLog(id); });
                    }
                    await PresentBatchProgressAsync(progress, "Retry", recoveryOnly: false);
                }, selectedPairId: plan.PairId, onPreparationProgress: detail => Dispatcher.Invoke(() =>
                {
                    if (_closing) return;
                    _operationLogNote!.Text = detail;
                    ShowRunStatus(detail);
                })));
            ShowResult(result.Pair, result.Result);
            ShowStatus($"Retry finished: {result.Result.Uploaded:N0} copied to the destination, {result.Result.Downloaded:N0} copied back, {result.Result.Failed:N0} failed, {result.Result.NotAttempted:N0} not attempted, {result.Result.NeedsVerification:N0} require verification. Original and retry histories are retained.");
        }
        catch (Exception error)
        {
            await PresentCommandFailureAsync(error, _pairSet.Pairs.Where(pair => pair.Id == plan.PairId), "Retry command");
        }
        finally
        {
            await FinishRunPresentationAsync("Retry", async () =>
            {
                await RefreshAuthorityProjectionsAsync(requireSuccess: true);
                await LoadOperationLogAsync(true);
                if (_operationLogLoadFailed)
                    throw new IOException(_operationLogNote?.Text ?? "The retained retry log could not be read.");
            });
            UpdateOperationRetryActions();
        }
    }

    private async Task VerifyLogOutcomesAsync()
    {
        if (UiOperationBusy || !HasCurrentOperationRetryPlan || _operationRetryPlan?.PairId is not { } pairId) return;
        var pair = _pairSet.Pairs.SingleOrDefault(item => item.Id == pairId);
        if (pair is null) return;
        _runActive = true;
        ResetRunPresentation("Verifying retained outcomes", "Checking uncertain file effects against their retained receipts; no new sync is being admitted.");
        SetRunControls();
        string? terminalFeedback = null;
        try
        {
            var result = await Task.Run(() => _authorityHost.VerifyRunOutcomesAsync(_operationRetryPlan.RunId,
                progress => PresentBatchProgressAsync(progress, "Verify retained outcomes", recoveryOnly: true),
                onReadProgress: detail => Dispatcher.Invoke(() =>
                {
                    if (_closing) return;
                    _operationLogNote!.Text = detail;
                    ShowRunStatus(detail);
                })));
            var unresolved = result.Items.Count(item => item.FailureMessage is not null || item.Result is { Blocked: > 0 } or { Conflicts: > 0 });
            terminalFeedback = unresolved > 0 ? "Verification finished with unresolved outcomes. Their exact reasons remain in the log; no uncertain action was blindly retried."
                : "Outcome verification finished. No Ready operation was admitted as a new sync; inspect the retained results before retrying.";
            ShowStatus(terminalFeedback);
        }
        catch (Exception error) { await PresentCommandFailureAsync(error, [pair], "Outcome verification"); }
        finally
        {
            await FinishRunPresentationAsync("Outcome verification", async () =>
            {
                await RefreshAuthorityProjectionsAsync(requireSuccess: true);
                await LoadOperationLogAsync(true);
                if (_operationLogLoadFailed)
                    throw new IOException(_operationLogNote?.Text ?? "The retained verification log could not be read.");
            }, terminalFeedback: terminalFeedback);
            UpdateOperationRetryActions();
        }
    }

    private async Task ExportOperationLogAsync()
    {
        if (_logExportCancellation is not null) return;
        var run = _operationLogRun!.Text.Trim();
        var search = _operationLogSearch!.Text.Trim();
        var account = _workspaceAccountId;
        var severity = _operationLogSeverity?.SelectedIndex > 0 ? (string?)_operationLogSeverity.SelectedItem : null;
        var actionFilter = (string?)_operationLogAction?.SelectedItem ?? "All actions";
        using var cancel = _logExportCancellation = new CancellationTokenSource();
        _cancelLogExport!.Visibility = Visibility.Visible;
        string? partial = null;
        void Feedback(string message)
        {
            _operationExportFeedback!.Text = message;
            _operationExportFeedback.Visibility = Visibility.Visible;
            ActionFeedback.Report(message);
        }
        try
        {
            var through = await Task.Run(() => _authorityHost.LoadActivitySequenceAsync(cancel.Token), cancel.Token);
            var exportWindow = await Task.Run(() => _authorityHost.LoadActivityRetentionWindowAsync(cancel.Token,
                runId: run.Length == 0 ? null : run), cancel.Token);
            Feedback("Preparing a redacted export preview; no file has been written.");
            var sample = await Task.Run(() => _authorityHost.LoadActivityProjectionPageAsync(20, 0, search, actionFilter, null, null, null,
                cancellationToken: cancel.Token, connectionId: account, query: new SyncActivityQuery(Severity: severity,
                    RunId: run.Length == 0 ? null : run, OldestFirst: true, DetailedLog: true, ThroughSequence: through)), cancel.Token);
            var preview = "This local export contains file and folder names, paths, account identifiers, operations and error details. " +
                "Credentials and URL query strings are redacted; file contents are never included. Nothing is uploaded. " +
                "Review before sharing it. Every currently retained matching event will be exported, including unloaded rows.\n" +
                exportWindow.Describe() + "\n\n" +
                "Preview — first " + sample.Count + " matching events:\n" +
                string.Join("\n\n", sample.Select(item => $"{item.OccurredUtc:O} | {item.Outcome} | {item.RelativePath}\n{item.Reason}"));
            if (!(OperationLogExportConfirmForTest?.Invoke(preview) ?? AppDialogWindow.Confirm(this,
                    "Operation Log Export", "Review the local diagnostic data before saving", preview, "Choose save location")))
            { Feedback("Export cancelled at preview; no file was written."); return; }
            string? destination;
            if (OperationLogExportPathForTest is { } choosePath) destination = choosePath();
            else
            {
                var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Export retained operation log",
                    FileName = "sync-shark-operation-log.jsonl", Filter = "JSON Lines (*.jsonl)|*.jsonl" };
                destination = dialog.ShowDialog(this) == true ? dialog.FileName : null;
            }
            if (destination is null) { Feedback("Export cancelled; no file was written."); return; }
            partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
            long cursor = 0; var count = 0;
            await using (var output = new StreamWriter(partial, false, new UTF8Encoding(false)))
            {
                while (true)
                {
                    var rows = await Task.Run(() => _authorityHost.LoadActivityProjectionPageAsync(500, 0, search, actionFilter, null, null, null,
                        cancellationToken: cancel.Token, connectionId: account, query: new SyncActivityQuery(Severity: severity,
                            RunId: run.Length == 0 ? null : run, OldestFirst: true, AfterSequence: cursor,
                            DetailedLog: true, ThroughSequence: through)), cancel.Token);
                    foreach (var row in rows)
                    {
                        await output.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(row).AsMemory(), cancel.Token);
                        cursor = row.Sequence!.Value; count++;
                    }
                    Feedback($"Exporting retained events: {count:N0} written to the temporary export. Existing destination is unchanged until completion.");
                    OperationLogExportProgressForTest?.Invoke(count);
                    cancel.Token.ThrowIfCancellationRequested();
                    if (rows.Count < 500) break;
                }
            }
            cancel.Token.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: true);
            Feedback($"Exported {count:N0} retained events to {destination}. " + ActivityIncompleteNote() + " " + exportWindow.Describe());
        }
        catch (OperationCanceledException)
        { Feedback("Export cancelled. The retained log and existing destination file were not changed."); }
        catch (Exception error)
        { Feedback("Export could not be completed: " + SyncActivityText.Failure(error) + ". The retained log and existing destination remain unchanged."); }
        finally
        {
            _logExportCancellation = null;
            _cancelLogExport.Visibility = Visibility.Collapsed;
            if (partial is not null && File.Exists(partial))
                try { File.Delete(partial); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Feedback(_operationExportFeedback!.Text + " Temporary export cleanup failed: " + SyncActivityText.Failure(error) + ". Temporary file: " + partial); }
        }
    }

    private async Task OpenLogRowRunAsync()
    {
        var runId = (_operationLogGrid?.SelectedItem as OperationLogRow)?.RunId;
        if (string.IsNullOrWhiteSpace(runId)) runId = _operationLogRun?.Text.Trim();
        if (!string.IsNullOrWhiteSpace(runId)) await OpenRetainedRunAsync(runId);
    }

    private async Task OpenRetainedRunAsync(string runId)
    {
        var run = await Task.Run(() => _authorityHost.LoadRunByIdAsync(runId));
        if (run is not null)
        {
            AppendRunHistory([run]);
            OpenWorkspaceActivity(2);
            InspectRun(ProjectRun(run));
            return;
        }
        ShowStatus("Run " + runId + " has no retained run record. Its operations remain visible in the log; no different run was substituted.");
    }

    // Resolution routes reuse the existing account, pair and conflict workflows. They
    // never infer a new binding from the selected account, clear history, or retry an
    // uncertain effect merely because its event is selected.
    private FrameworkElement BuildActivityResolutionActions(ActivityRow row)
    {
        var panel = new StackPanel();
        panel.Children.Add(Text("The recorded reason above is retained. Fix the cause, then use this run's safe retry controls; successful files are not repeated.", 12, brush: "MutedInk"));
        void Add(string label, Action open, bool available, string help, string unavailable)
        {
            var button = Button(label, (_, _) => open(), true);
            button.IsEnabled = available;
            button.ToolTip = available ? help : unavailable;
            ToolTipService.SetShowOnDisabled(button, true);
            AutomationProperties.SetHelpText(button, (string)button.ToolTip);
            AutomationProperties.SetItemStatus(button, available ? help : unavailable);
            panel.Children.Add(button);
        }
        Add("Open retry and verification controls", () => OpenOperationLog(row.RunId),
            !string.IsNullOrWhiteSpace(row.RunId),
            "Open this exact run's retained log, failed-only retry, unattempted files and outcome verification.",
            "This historical event has no recorded run identity; no retry target can be inferred.");
        var pair = PairRows.SingleOrDefault(item => item.Id == row.PairId);
        Add("Open pair repair and status", () =>
        {
            NavigateTo("Pairs");
            if (_workspacePage != "Pairs") return; // Respect a cancelled dirty-draft navigation.
            PairList.SelectedItem = pair; ExpandPairRow(pair!); PairEditorTabs.SelectedIndex = 3;
        }, pair is not null, "Open this folder pair's status and folder-repair controls. No binding is changed by opening it.",
            "This event's folder pair is no longer available; its history is preserved.");
        var conflict = ConflictRows.FirstOrDefault(item => item.PairId == row.PairId &&
            string.Equals(item.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        Add("Resolve this file's conflict", () =>
        {
            IncludeAttentionPair(row.PairId!); NavigateTo("Conflicts");
            if (_workspacePage == "Attention") ConflictGrid.SelectedItem = conflict;
        }, conflict is not null, "Open the retained conflict for this exact pair and path to compare versions and choose a resolution.",
            "No open conflict is currently loaded for this exact pair and file. A provider refusal is not automatically a conflict.");
        var accountIds = row.EndpointContext is { } endpoints
            ? new[] { endpoints.Left.ConnectionId, endpoints.Right.ConnectionId }
            : new[] { row.ConnectionId };
        foreach (var id in accountIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            var accountId = id!;
            Add("Open account: " + EndpointAccountDisplayLabel(accountId), () =>
            {
                NavigateTo("Connections");
                if (_workspacePage != "Connections") return;
                _selectedConnectionId = accountId; RefreshAccountPickers();
                ShowStatus("This is the account recorded by the failed operation. Refresh access or reconnect here, then return to the retained run to retry.");
            }, _authorityHost.LoadAccountBinding(accountId) is not null,
                "Open this exact recorded account's access, storage and reconnect controls. Reauthorization is not started automatically.",
                "This event's recorded account is no longer connected. No other account will be substituted.");
        }
        Add("Open device space and storage saving", () => NavigateTo("Storage"), true,
            "Inspect available device space, online-only files and selective sync. Opening the page changes no files.", "");
        panel.Children.Add(Text("A locked file or provider refusal may require closing its editor or changing provider access. An internal identity/plan defect requires a verified product correction; these links do not clear or bypass it.", 12, brush: "MutedInk"));
        return panel;
    }
}

public sealed record OperationLogRow(SyncAuthorityActivityProjection Entry)
{
    public ActivityRow Activity { get; } = MainWindow.RetainedActivityRow(Entry);
    public string TimeLabel => Time;
    public string FullTimestamp => Activity.FullTimestamp;
    public string TimeAccessibleLabel => Activity.TimeAccessibleLabel;
    public string StatusGlyph => Activity.StatusGlyph;
    public string Progress => Entry.Progress is not { } measured ? "Not recorded" :
        $"{measured.Completed:N0}" + (measured.Total is { } total ? $" / {total:N0}" : " / total unknown") +
        " " + measured.Unit + $"\nObserved over {measured.ElapsedMilliseconds / 1000.0:N1}s";
    public string Time => Entry.OccurredUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public string Severity => Entry.Severity;
    public string Action => Activity.OutcomeLabel;
    public string Endpoints => Entry.EndpointContext is { } context
        ? SyncEndpointPresentation.SideLabel(context.Left.Side) + ": " + Endpoint(context.Left) +
          " ⇄ " + SyncEndpointPresentation.SideLabel(context.Right.Side) + ": " + Endpoint(context.Right)
        : "Not recorded";
    private static string Endpoint(SyncEndpointContextSide side) => side.IsLocal ? "This computer" :
        side.Provider is { Length: > 0 } provider ? SyncProviders.DisplayName(provider) : "Provider not recorded";
    public string Target => string.IsNullOrWhiteSpace(Entry.RelativePath) ? "Run / background process" : Entry.RelativePath;
    public string Detail => Entry.Reason;
    public string RunId => Entry.RunId ?? "";
}
