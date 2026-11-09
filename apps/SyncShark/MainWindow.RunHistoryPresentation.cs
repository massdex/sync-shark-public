using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

// UI11 (Runs presentation). The Runs tab previously drew one expander card per retained run and a
// single concatenated summary, so a run could not be compared, filtered or told apart by account the
// way Activity, Pending and Recovery rows already can. Runs now uses that same family: the shared
// compact/detailed row metrics, the shared Simple/Detailed switch under the existing per-page view
// preference, one details host for the selected run and the shared status glyph/colour presentation.
//
// Everything here is a projection of records the authority already retains. No provider, core or CLI
// behaviour changes, and nothing is created, deleted or pruned: a value the retained run does not
// carry keeps an explicit "Not recorded" marker instead of being shown as zero or guessed.
public partial class MainWindow
{
    // UI11: bounded seam for the retained-run page fetch. It exists so a fixture can prove the
    // delayed-page, account-scope and load-more/reset ordering with a controlled completion order; a
    // null value uses the real authority exactly as before.
    internal static Func<int, int, Task<IReadOnlyList<SyncAuthorityRunHistory>>>? RunHistoryLoaderForTest { get; set; }

    // UI11: the Runs surface keeps its view mode in the existing preferences store. That store
    // validates every page key as lowercase, so the Runs key uses the same lowercase form the
    // activity, pending and recovery rows already use.
    private const string RunsViewKey = "runs";
    private const int RunHistoryPageSize = 50;

    private readonly List<SyncAuthorityRunHistory> _retainedRuns = new();
    // The next server-page offset is tracked on its own, not as _retainedRuns.Count: OpenRetainedRunAsync
    // appends an older run reached by id to _retainedRuns, so the list can hold more rows than the pages
    // fetched and using its count as the offset would skip an unseen row on the next Load More.
    private int _runHistoryPagingOffset;
    private readonly ObservableCollection<RunHistoryRow> _runRows = [];
    private DataGrid? _runHistoryGrid;
    private TextBlock? _runCountText;
    private TextBlock? _runNote;
    private TextBlock? _runEmpty;
    private Button? _runLoadMore;
    private SimpleDetailedSwitch? _runViewSwitch;
    private Expander? _runComparisonExpander;
    private ComboBox? _runStatusFilter;
    private ComboBox? _runTriggerFilter;
    private string? _runStatusFilterValue;
    private string? _runTriggerFilterValue;
    private string? _runSelectedId;
    private bool _loadingRuns;
    private bool _runLoadFailed;
    private bool _runReloadPending;
    private bool _updatingRunFilters;
    private bool _runPresentationReady;

    // UI11: the Runs surface's Detailed columns. None of these headers is used by an always-visible
    // Runs column, so the shared detail helper can add and remove exactly this set without touching
    // the row identity, the selection, the filters or the duration column.
    private static readonly FeedDetailColumn[] RunDetailColumns =
    [
        new("Folder Pairs", nameof(RunHistoryRow.PairsLabel), 220),
        new("Accounts", nameof(RunHistoryRow.AccountsLabel), 220),
        new("Started", nameof(RunHistoryRow.StartedLabel), 170),
        new("Completed", nameof(RunHistoryRow.CompletedLabel), 150),
        new("Started By", nameof(RunHistoryRow.TriggerLabel), 120),
        new("Totals", nameof(RunHistoryRow.TotalsLabel), 320)
    ];

    // ---------------------------------------------------------------------------------------------
    // UI11: the Runs surface. Filters, list, details and the retained comparison all live in the
    // existing Runs tab, and the current-session comparison keeps its existing expander.
    // ---------------------------------------------------------------------------------------------

    private void BuildRunHistory()
    {
        var tab = (TabItem)_activityTabs.Items[2];
        var prior = (UIElement)tab.Content;
        tab.Content = null;
        // The current-session comparison keeps its existing control, wording and content. UI11 only
        // places it below the retained-run list and makes that same control reachable from the
        // selected run's details, so the comparison is never replaced by a second presentation.
        _runComparisonExpander = new Expander
        {
            Header = CommandIcons.Label("Current Session Comparison", "search"),
            Content = prior,
            MaxHeight = 320,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var surface = new Grid();
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // The loaded/matching count belongs with the list rather than as a floating explanatory
        // sentence beside the shared Activity summary. View mode stays in the shared Activity header
        // across all three tabs, rather than adding another toolbar row only for Runs.
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _runCountText = Text("", 12, brush: "MutedInk");
        _runCountText.Margin = new Thickness(0, 0, 12, 8);
        _runCountText.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetName(_runCountText, "Runs matching and loaded count");
        header.Children.Add(_runCountText);
        _runViewSwitch = _activityViewSwitch;
        surface.Children.Add(header);

        _runHistoryGrid = BuildRunHistoryGrid();
        Grid.SetRow(_runHistoryGrid, 1);
        surface.Children.Add(_runHistoryGrid);
        _runEmpty = Text("", 14, brush: "MutedInk");
        _runEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _runEmpty.TextAlignment = TextAlignment.Center;
        _runEmpty.Margin = new Thickness(18, 28, 18, 0);
        _runEmpty.IsHitTestVisible = false;
        Grid.SetRow(_runEmpty, 1);
        surface.Children.Add(_runEmpty);

        var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        _runNote = Text("", 12, brush: "MutedInk");
        _runNote.Margin = new Thickness(0, 0, 0, 6);
        AutomationProperties.SetName(_runNote, "Runs load status");
        footer.Children.Add(_runNote);
        _runLoadMore = AsyncButton("Load More Runs", async (_, _) => await LoadRunHistoryAsync(false), true);
        _runLoadMore.HorizontalAlignment = HorizontalAlignment.Left;
        _runLoadMore.ToolTip = "Load the next retained page of sync runs. History is never changed or pruned.";
        footer.Children.Add(_runLoadMore);
        Grid.SetRow(footer, 2);
        surface.Children.Add(footer);

        Grid.SetRow(_runComparisonExpander, 3);
        surface.Children.Add(_runComparisonExpander);
        tab.Content = surface;
        _runPresentationReady = true;
        ApplyRunViewMode();
        AddRunFiltersToFilterArea();
        UpdateRunFilterVisibility();

        _activityTabs.SelectionChanged += async (_, e) =>
        {
            if (!ReferenceEquals(e.Source, _activityTabs)) return;
            UpdateRunFilterVisibility();
            if (_activityTabs.SelectedIndex == 2 && _fullAppReady) await LoadRunHistoryAsync(true);
        };
        // The Runs list follows the app-wide exact-account scope immediately, so a scope change hides
        // an unrelated selection at once instead of leaving the previous account's rows on screen
        // until the next load returns. It may only do that while the Runs surface is the one on
        // screen: keying this on the retained tab index alone re-projected the retained run from
        // another page, and restoring its selection there re-opened its details and navigated away.
        foreach (var picker in _workspaceAccountPickers)
            picker.SelectionChanged += (_, _) =>
            {
                if (_updatingWorkspaceAccounts) return;
                if (ActivityPage.Visibility != Visibility.Visible || _activityTabs.SelectedIndex != 2) return;
                RenderRunHistory();
            };
    }

    private DataGrid BuildRunHistoryGrid()
    {
        var grid = new DataGrid { ItemsSource = _runRows, MinHeight = 180 };
        // The shared feed chrome: read-only rows, real virtualization, quiet cells and the same
        // responsive name-column sizing every other list on this page uses.
        StyleFeed(grid, activityColumns: true);
        grid.Columns[0].Header = "Run";
        ((DataGridTemplateColumn)grid.Columns[0]).CellTemplate = ItemTemplate(trailingTime: false, metadataLink: false);
        grid.Columns[2] = new DataGridTemplateColumn
        {
            Header = "Duration",
            Width = 96,
            CellTemplate = RowTemplate("<TextBlock VerticalAlignment=\"Center\" Text=\"{Binding DurationLabel}\" ToolTip=\"{Binding DurationTooltip}\" AutomationProperties.Name=\"{Binding DurationAccessibleLabel}\"/>")
        };
        grid.Columns.RemoveAt(4); // No inert activity-only menu on a run.
        grid.RowHeight = SharedPresentation.CompactRowHeight;
        AutomationProperties.SetName(grid, "Retained sync runs");
        // Selecting a row while the Activity surface is hidden is presentation, never inspection: a
        // programmatic restore must not navigate or open the run inspector from an unrelated page.
        grid.SelectionChanged += (_, _) =>
        {
            if (ActivityPage.Visibility != Visibility.Visible) return;
            if (grid.SelectedItem is RunHistoryRow row) InspectRun(row);
        };
        grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && grid.SelectedItem is RunHistoryRow row) { InspectRun(row); e.Handled = true; }
        };
        return grid;
    }

    // UI11: the Runs-specific status and trigger filters use the existing Filters area that already
    // carries the Activity search, pair and date controls, and they are only visible while the Runs
    // tab is selected.
    private void AddRunFiltersToFilterArea()
    {
        if (_activityExtraFilters is null) return;
        _runStatusFilter = RunFilterCombo("Filter runs by their recorded status", 160);
        _runStatusFilter.SelectionChanged += (_, _) =>
        {
            if (_updatingRunFilters) return;
            _runStatusFilterValue = SelectedRunFilterValue(_runStatusFilter);
            RenderRunHistory();
        };
        _activityExtraFilters.Children.Add(_runStatusFilter);
        _runTriggerFilter = RunFilterCombo("Filter runs by the recorded trigger that started them", 180);
        _runTriggerFilter.SelectionChanged += (_, _) =>
        {
            if (_updatingRunFilters) return;
            _runTriggerFilterValue = SelectedRunFilterValue(_runTriggerFilter);
            RenderRunHistory();
        };
        _activityExtraFilters.Children.Add(_runTriggerFilter);
    }

    private static ComboBox RunFilterCombo(string tooltip, double width)
    {
        var box = new ComboBox { MinWidth = width, MaxWidth = width + 80, ToolTip = tooltip,
            Margin = new Thickness(0, 0, 12, 8), Visibility = Visibility.Collapsed };
        box.Style = SharedPresentation.QuietComboBoxStyle();
        AutomationProperties.SetName(box, tooltip);
        return box;
    }

    private void UpdateRunFilterVisibility()
    {
        var runs = _activityTabs.SelectedIndex == 2;
        if (_runStatusFilter is not null)
            _runStatusFilter.Visibility = runs ? Visibility.Visible : Visibility.Collapsed;
        if (_runTriggerFilter is not null)
            _runTriggerFilter.Visibility = runs ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------------------------------------
    // UI11: loading and projecting the retained history.
    // ---------------------------------------------------------------------------------------------

    private async Task LoadRunHistoryAsync(bool reset)
    {
        if (_loadingRuns)
        {
            // A newer reset that arrives while a page is in flight must not be dropped: it runs once
            // the current page has finished, so a required reload is never lost.
            if (reset) _runReloadPending = true;
            return;
        }
        if (RunHistoryLoaderForTest is null && !_authorityHost.AuthorityStateExists)
        {
            if (reset) { _retainedRuns.Clear(); _runHistoryPagingOffset = 0; }
            RenderRunHistory();
            return;
        }
        _loadingRuns = true;
        _runLoadFailed = false;
        if (_runLoadMore is not null) _runLoadMore.IsEnabled = false;
        SetRunNote(_retainedRuns.Count == 0 ? "Loading retained runs…" : "Loading older runs…", "MutedInk");
        try
        {
            var offset = reset ? 0 : _runHistoryPagingOffset;
            var rows = RunHistoryLoaderForTest is null
                ? await Task.Run(() => _authorityHost.LoadRunHistoryAsync(RunHistoryPageSize, offset))
                : await RunHistoryLoaderForTest(RunHistoryPageSize, offset);
            // V6: this retained-page load only keeps the CURRENT scope's last successful retained run
            // current; it never infers that time from the loaded page, the newest failure or a recovery.
            // All Accounts resolves the latest success across EVERY pair through the existing authority
            // query and retains it as its own projection, while the primary/selected connection status
            // stays primary-specific instead of being rewritten with another scope's number. The scope
            // generation captured before the read keeps a late response from overriding a newer scope.
            var scopeGeneration = _workspaceScopeGeneration;
            var scopeAccount = _workspaceAccountId;
            var scopeLastSuccessful = await ResolveScopeLastSuccessfulSyncUtcAsync();
            if (scopeGeneration == _workspaceScopeGeneration &&
                string.Equals(scopeAccount, _workspaceAccountId, StringComparison.Ordinal))
                _scopeLastSuccessfulUtc = scopeLastSuccessful;
            if (reset) _retainedRuns.Clear();
            AppendRunHistory(rows);
            _runHistoryPagingOffset = offset + rows.Count;
            SetRunNote(string.Empty, "MutedInk");
            RenderRunHistory();
            RefreshWorkspace();
        }
        catch (Exception e)
        {
            _runLoadFailed = true;
            SetRunNote("Run history could not be loaded: " + e.Message +
                ". The runs already loaded are still shown; Refresh or Load More retries.", "ErrorBrush");
        }
        finally
        {
            _loadingRuns = false;
            if (_runLoadMore is not null) _runLoadMore.IsEnabled = true;
        }
        if (_runReloadPending)
        {
            _runReloadPending = false;
            await LoadRunHistoryAsync(true);
        }
    }

    // One page can overlap a previous page when a newer run is retained between the two reads.
    // Duplicate identities are not appended twice, and nothing is removed from the loaded history.
    private void AppendRunHistory(IEnumerable<SyncAuthorityRunHistory> rows)
    {
        var known = _retainedRuns.Select(run => run.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var run in rows) if (known.Add(run.Id)) _retainedRuns.Add(run);
    }

    private void SetRunNote(string text, string brush)
    {
        if (_runNote is null) return;
        _runNote.Text = text;
        _runNote.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    private void RenderRunHistory()
    {
        if (_runHistoryGrid is null || _activityTabs.SelectedIndex != 2) return;
        var search = ActivitySearchBox.Text.Trim();
        var choice = ActivityPairBox.SelectedItem as ActivityPairChoice;
        var pairId = choice?.Id;
        var connectionId = choice?.ConnectionId;
        var since = ActivitySinceUtc();
        var projected = _retainedRuns.Select(run => (Run: run, Row: ProjectRun(run))).ToArray();
        RebuildRunFilterChoices(projected.Select(item => item.Row));
        var visible = new List<RunHistoryRow>();
        foreach (var item in projected)
        {
            var row = item.Row;
            if (since is { } sinceUtc && item.Run.StartedUtc < sinceUtc) continue;
            if (pairId is not null && !row.PairIds.Contains(pairId, StringComparer.Ordinal)) continue;
            if (connectionId is not null && !RunTouchesConnection(row, connectionId)) continue;
            if (!RunMatchesAccountScope(item.Run, row)) continue;
            if (_runStatusFilterValue is { Length: > 0 } status &&
                !string.Equals(row.StatusLabel, status, StringComparison.Ordinal)) continue;
            if (_runTriggerFilterValue is { Length: > 0 } trigger &&
                !string.Equals(row.TriggerLabel, trigger, StringComparison.Ordinal)) continue;
            if (search.Length > 0 && !row.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            visible.Add(row);
        }
        _runRows.Clear();
        foreach (var row in visible) _runRows.Add(row);
        if (_runEmpty is not null)
        {
            _runEmpty.Text = visible.Count > 0 ? string.Empty
                : _retainedRuns.Count == 0
                    ? "No sync run has been retained yet. A completed run appears here with its retained outcome."
                    : "No retained run matches the current filters. Change the account, pair, date, status, trigger or search filter.";
            _runEmpty.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_runCountText is not null)
        {
            var unknown = projected.Count(item => !item.Row.RequestReadable || item.Row.PairIds.Count == 0 ||
                item.Row.PairIds.Any(id => !_pairSet.Pairs.Any(pair => pair.Id == id)));
            _runCountText.Text = $"Matching runs: {visible.Count:N0} · Loaded: {_retainedRuns.Count:N0}" +
                ((_workspaceAccountId is not null || connectionId is not null || pairId is not null) && unknown > 0
                    ? $" · {unknown:N0} unclassified run(s) available under All Accounts / All Pairs" : "");
        }
        // The Runs count lives with the Runs list, so the shared summary line is not left describing
        // another surface's scope while the Runs tab is the one on screen.
        ActivityPageSummary.Text = string.Empty;
        if (_runLoadMore is not null) _runLoadMore.IsEnabled = !_loadingRuns;
        RestoreRunSelection();
    }

    private void RestoreRunSelection()
    {
        if (_runHistoryGrid is null || _runSelectedId is not { Length: > 0 } selected) return;
        var restored = _runRows.FirstOrDefault(row => string.Equals(row.Id, selected, StringComparison.Ordinal));
        if (restored is not null) { _runHistoryGrid.SelectedItem = restored; return; }
        // The selected run is no longer part of the presented set, so the selection and the details
        // that described it are discarded together rather than left describing a row that is gone.
        _runSelectedId = null;
        _runHistoryGrid.SelectedItem = null;
        if (_workspaceInspector.Visibility == Visibility.Visible) CloseWorkspaceDetails();
    }

    // The account-scope invalidation drops the retained run identity and its grid selection together,
    // exactly as it already drops the activity selection, so a run belonging to the previous scope
    // cannot be restored or re-presented after the header account changes.
    private void ClearRetainedRunSelection()
    {
        _runSelectedId = null;
        if (_runHistoryGrid is not null) _runHistoryGrid.SelectedItem = null;
    }

    // The Activity pair filter is an exact pair id or an exact account; the Runs list keeps the same
    // meaning, and a run that touches several pairs stays visible while any of them matches.
    private bool RunTouchesConnection(RunHistoryRow row, string connectionId) =>
        row.PairIds.Any(id => TrayMatchesAccount(id, connectionId));

    // UI11: the app-wide exact-account scope. ScopeMatchesRun parses the retained request, so a
    // legacy run whose request cannot be decoded is never presented as if it belonged to the scoped
    // account; it stays available under All Accounts with its explicit unknown marker.
    private bool RunMatchesAccountScope(SyncAuthorityRunHistory run, RunHistoryRow row) =>
        _workspaceAccountId is null || (row.RequestReadable && RunTouchesConnection(row, _workspaceAccountId));

    // ---------------------------------------------------------------------------------------------
    // UI11: the retained-run projection.
    // ---------------------------------------------------------------------------------------------

    private RunHistoryRow ProjectRun(SyncAuthorityRunHistory run)
    {
        var pairIds = Array.Empty<string>();
        var requestReadable = false;
        string? trigger = null;
        if (!string.IsNullOrWhiteSpace(run.RequestJson))
        {
            try
            {
                using var request = JsonDocument.Parse(run.RequestJson);
                var root = request.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    requestReadable = true;
                    if (root.TryGetProperty("pairIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
                        pairIds = ids.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                            .Select(value => value.GetString()).Where(value => !string.IsNullOrEmpty(value))
                            .Select(value => value!).ToArray();
                    if (root.TryGetProperty("trigger", out var recorded) && recorded.ValueKind == JsonValueKind.String)
                        trigger = recorded.GetString();
                }
            }
            catch (JsonException) { requestReadable = false; }
            catch (ArgumentException) { requestReadable = false; }
        }

        var statusLabel = RunStatusLabel(run.Status);
        int? uploaded = null, downloaded = null, unchanged = null, attention = null;
        int? failed = null, notAttempted = null, needsVerification = null, skipped = null;
        var perPair = new List<string>();
        // SS-D02: the same retained-result reader and the same classified outcome the Home latest-run
        // summary and the tray notification use, so this surface cannot state a different truth about
        // the same retained run.
        var retained = ProjectRetainedRun(run);
        if (retained.ResultPresent)
        {
            uploaded = retained.Uploaded;
            downloaded = retained.Downloaded;
            unchanged = retained.Unchanged;
            attention = retained.Attention;
            failed = retained.Failed;
            notAttempted = retained.NotAttempted;
            needsVerification = retained.NeedsVerification;
            skipped = retained.Skipped;
            perPair.AddRange(retained.PerPairCounts);
            if (needsVerification is > 0) statusLabel = "Needs Verification";
            else if (retained.Stopped || retained.Outcome.Kind == SyncRunOutcomeKind.Stopped) statusLabel = "Stopped";
            else if (retained.FailedPairs is > 0 || attention is > 0 ||
                retained.Outcome.Kind == SyncRunOutcomeKind.NeedsAttention)
                statusLabel = (uploaded ?? 0) + (downloaded ?? 0) > 0
                    ? "Completed with Failures" : uploaded is not null && downloaded is not null
                        ? "Blocked before Transfers" : "Needs Attention";
            else if (retained.FirstSyncIncomplete) statusLabel = "Awaiting Remaining Choices";
            // A plain completion is only stated for a run the shared classification confirms succeeded;
            // every other row keeps its recorded status or the truthful label derived above.
            else if (retained.Outcome.Kind == SyncRunOutcomeKind.Success) statusLabel = "Completed";
        }

        var pairLabels = pairIds.Select(id => RunPairLabel(id)).ToArray();
        var accountLabels = pairIds.Select(RunAccountLabel)
            .Distinct(StringComparer.Ordinal).ToArray();
        var accounts = accountLabels.Length == 0
            ? RunHistoryRow.NotRecorded : string.Join(" · ", accountLabels);
        if (accountLabels.Count(label => !string.Equals(label, RunHistoryRow.NotRecorded, StringComparison.Ordinal)) > 1)
            accounts += " (multi-account run)";
        var pairs = pairLabels.Length == 0 ? RunHistoryRow.NotRecorded : string.Join("\n", pairLabels);
        var startedLabel = RunLocalTime(run.StartedUtc);
        var running = string.Equals(statusLabel, "Running", StringComparison.Ordinal);
        var completedLabel = run.CompletedUtc is { } completed ? RunLocalTime(completed)
            : running ? "In progress" : RunHistoryRow.NotRecorded;
        // A duration is only stated when the retained start and completion actually bound the
        // interval; both exact timestamps stay visible either way.
        var duration = run.CompletedUtc is { } completedAt && completedAt >= run.StartedUtc
            ? FormatDuration(completedAt - run.StartedUtc)
            : running ? "In progress" : RunHistoryRow.NotRecorded;
        var title = pairLabels.Length > 0 ? string.Join(", ", pairLabels)
            : requestReadable ? "Sync Run" : "Sync Run " + run.Id;
        var startedBy = string.IsNullOrWhiteSpace(trigger) ? RunHistoryRow.NotRecorded : trigger;
        var totals = new List<string>();
        AddCount(totals, "Uploaded", uploaded);
        AddCount(totals, "Downloaded", downloaded);
        AddCount(totals, "Unchanged", unchanged);
        AddCount(totals, "Needs Attention", attention);
        AddCount(totals, "Failed", failed);
        AddCount(totals, "Not Attempted", notAttempted);
        AddCount(totals, "Needs Verification", needsVerification);
        AddCount(totals, "Skipped", skipped);
        var totalsLabel = totals.Count == 0 ? RunHistoryRow.NotRecorded : string.Join(" · ", totals);
        // First-sync and outer command failures retain the exact safe cause on
        // the same Runs surface; neither implies that every file failed.
        var typedReason = RunCommandProblemReason(run.ResultJson);
        var statusLine = "Status: " + statusLabel + " · Started By: " + startedBy +
                         (typedReason is { } problemReason
                             ? " · " + ProblemPreview(problemReason.Message) + " Next: " + problemReason.NextAction +
                               " Run " + problemReason.CommandId + "."
                             : string.Empty);
        var fields = new List<RunDetailField>
        {
            new("Run ID", run.Id),
            new("Folder Pairs", pairs),
            new("Accounts", accounts),
            new("Started", startedLabel),
            new("Started By", startedBy),
            new("Completed", completedLabel),
            new("Duration", duration),
            new("Status", statusLabel),
            new("Uploaded", CountLabel(uploaded)),
            new("Downloaded", CountLabel(downloaded)),
            new("Unchanged", CountLabel(unchanged)),
            new("Needs Attention", CountLabel(attention))
        };
        fields.Add(new("Failed Files", CountLabel(failed)));
        fields.Add(new("Not Attempted", CountLabel(notAttempted)));
        fields.Add(new("Needs Verification", CountLabel(needsVerification)));
        fields.Add(new("Skipped", CountLabel(skipped)));
        if (typedReason is { } failure)
        {
            fields.Add(new("What happened", failure.Message, RequiresReading: true));
            fields.Add(new("Next action", failure.NextAction, RequiresReading: true));
        }
        if (perPair.Count > 0) fields.Add(new("Per-Pair Counts", string.Join("\n", perPair)));
        if (!requestReadable)
            fields.Add(new("Recorded Pairs", "The retained request for this run could not be read, so its " +
                "folder pairs are not recorded here. The retained request and result are shown in Technical Details.",
                RequiresReading: true));
        return new RunHistoryRow
        {
            Id = run.Id,
            Title = title,
            StatusLabel = statusLabel,
            StatusGlyph = RunStatusGlyph(statusLabel),
            StatusLine = statusLine,
            StartedLabel = startedLabel,
            StartedLine = "Started: " + startedLabel,
            StartedTooltip = "Started: " + RunLocalStamp(run.StartedUtc),
            TimeLabel = run.StartedUtc.ToLocalTime().ToString("HH:mm:ss"),
            CompletedLabel = completedLabel,
            DurationLabel = duration,
            DurationTooltip = "Duration: " + duration,
            DurationAccessibleLabel = "Duration: " + duration,
            TriggerLabel = startedBy,
            PairsLabel = pairs == RunHistoryRow.NotRecorded ? pairs : string.Join(", ", pairLabels),
            AccountsLabel = accounts,
            TotalsLabel = totalsLabel,
            PairIds = pairIds,
            RequestReadable = requestReadable,
            Fields = fields,
            SearchText = string.Join("\n", new[] { run.Id, title, statusLine, pairs, accounts, totalsLabel,
                string.Join(" ", pairIds) }),
            RecordedStatus = run.Status,
            RecordedKind = run.Kind,
            RetainedRequest = run.RequestJson,
            RetainedResult = run.ResultJson ?? "No result recorded"
        };
    }

    // Read the existing retained result, without substituting a first-sync
    // navigation action for a command-level failure or inventing file outcomes.
    private static (string Message, string NextAction, string CommandId)? RunCommandProblemReason(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            JsonElement problem = default;
            var found = false;
            var firstSyncProblem = false;
            foreach (var property in document.RootElement.EnumerateObject())
                if ((string.Equals(property.Name, "firstSyncProblem", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(property.Name, "commandFailure", StringComparison.OrdinalIgnoreCase)) &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    problem = property.Value;
                    found = true;
                    firstSyncProblem = string.Equals(property.Name, "firstSyncProblem", StringComparison.OrdinalIgnoreCase);
                    break;
                }
            if (!found) return null;
            var message = Text(problem, "Message");
            if (string.IsNullOrWhiteSpace(message)) return null;
            return (SyncActivityText.Safe(message),
                SyncActivityText.Safe(Text(problem, "NextAction") ?? (firstSyncProblem
                    ? "Open First sync review to continue."
                    : "Open this run's operation log and retained outcomes before retrying.")),
                Text(problem, "CommandId") ?? RunHistoryRow.NotRecorded);
        }
        catch (JsonException) { return null; }

        static string? Text(JsonElement element, string name)
        {
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
            return null;
        }
    }

    // A pair the window still retains keeps its current name; a pair that is no longer retained is
    // named by its recorded id with an explicit marker, never by a guessed current account.
    private string RunPairLabel(string pairId) =>
        _pairSet.Pairs.FirstOrDefault(pair => string.Equals(pair.Id, pairId, StringComparison.Ordinal)) is { } pair
            ? pair.Name : pairId + " · " + RunHistoryRow.NotRecorded;

    private string RunAccountLabel(string pairId)
    {
        if (_pairSet.Pairs.FirstOrDefault(pair => string.Equals(pair.Id, pairId, StringComparison.Ordinal))
            is not { } pair) return RunHistoryRow.NotRecorded;
        if (pair.EndpointDocument is { Left: not null, Right: not null }) return LabelForPair(pair);
        var connectionId = pair.ConnectionId;
        if (string.IsNullOrEmpty(connectionId)) return RunHistoryRow.NotRecorded;
        return _accountLabels.GetValueOrDefault(connectionId, "Account Unavailable");
    }

    // A retained run always carries a status; a legacy or empty value is stated as not recorded
    // rather than being presented as a status the record does not have.
    private static string RunStatusLabel(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return RunHistoryRow.NotRecorded;
        return status.Trim().ToLowerInvariant() switch
        {
            "completed" => "Completed",
            "executing" => "Running",
            "blocked" => "Needs Attention",
            "cancelled" => "Cancelled",
            "stopped" => "Stopped",
            "rejected" => "Rejected",
            "stale" => "Out of Date",
            "requested" or "accepted" or "pending" => "Queued",
            _ => "Unknown (" + status.Trim() + ")"
        };
    }

    // The shared status glyph vocabulary: the same characters the Activity rows already map to an
    // icon and a status brush, so Runs status is always icon, text and colour together.
    private static string RunStatusGlyph(string statusLabel) => statusLabel switch
    {
        "Completed" => "✓",
        "Running" => "↻",
        "Needs Attention" or "Completed with Failures" or "Blocked before Transfers" or "Needs Verification" => "!",
        "Awaiting Remaining Choices" => "Ⅱ",
        "Stopped" or "Cancelled" => "■",
        "Rejected" or "Out of Date" => "!",
        "Queued" => "Ⅱ",
        _ => "·"
    };

    private static string RunLocalTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm:ss");

    private static string RunLocalStamp(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");

    // Whole hours are total hours, so a long run never wraps at 24 hours.
    private static string FormatDuration(TimeSpan span) =>
        ((int)span.TotalHours).ToString("00") + ":" + span.Minutes.ToString("00") + ":" + span.Seconds.ToString("00");

    private static void AddCount(List<string> values, string label, int? value)
    {
        if (value is { } recorded) values.Add(label + ": " + recorded.ToString("N0"));
    }

    private static string CountLabel(int? value) =>
        value is { } recorded ? recorded.ToString("N0") : RunHistoryRow.NotRecorded;

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int? NumberValue(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var number)) return number;
        return null;
    }

    private static string? StringValue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    // An aggregate is only stated when every retained pair reported that count. A partially recorded
    // total stays "Not recorded" rather than being presented as a smaller real number.
    private static int? AggregateCount(JsonElement[] entries, params string[] names)
    {
        if (entries.Length == 0) return null;
        var total = 0;
        foreach (var entry in entries)
        {
            if (NumberValue(entry, names) is not { } value) return null;
            total += value;
        }
        return total;
    }

    private static int? AggregatedAttention(JsonElement element)
    {
        var blocked = NumberValue(element, "blocked", "Blocked");
        var conflicts = NumberValue(element, "conflicts", "Conflicts");
        return blocked is { } blockedValue && conflicts is { } conflictValue ? blockedValue + conflictValue : null;
    }

    private static int? AggregateAttention(JsonElement[] entries)
    {
        if (entries.Length == 0) return null;
        var total = 0;
        foreach (var entry in entries)
        {
            if (AggregatedAttention(entry) is not { } value) return null;
            total += value;
        }
        return total;
    }

    // ---------------------------------------------------------------------------------------------
    // UI11: filters, view switch and the selected-run details.
    // ---------------------------------------------------------------------------------------------

    private void RebuildRunFilterChoices(IEnumerable<RunHistoryRow> rows)
    {
        var materialised = rows.ToArray();
        _updatingRunFilters = true;
        try
        {
            SetRunFilterChoices(_runStatusFilter, "All run statuses",
                materialised.Select(row => row.StatusLabel), _runStatusFilterValue);
            SetRunFilterChoices(_runTriggerFilter, "All triggers",
                materialised.Select(row => row.TriggerLabel), _runTriggerFilterValue);
        }
        finally { _updatingRunFilters = false; }
    }

    private void SetRunFilterChoices(ComboBox? box, string allLabel, IEnumerable<string> values,
        string? selected)
    {
        if (box is null) return;
        var items = new List<string> { allLabel };
        items.AddRange(values.Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        // A filter whose recorded value disappeared from this page stays selectable and applied; it is
        // never silently dropped back to "all".
        if (selected is { Length: > 0 } && !items.Contains(selected, StringComparer.Ordinal)) items.Add(selected);
        var current = selected is { Length: > 0 } ? selected : allLabel;
        if (box.ItemsSource is IEnumerable<string> existing && existing.SequenceEqual(items, StringComparer.Ordinal) &&
            string.Equals(box.SelectedItem as string, current, StringComparison.Ordinal)) return;
        box.ItemsSource = items;
        box.SelectedItem = current;
    }

    private static string? SelectedRunFilterValue(ComboBox? box)
    {
        if (box?.SelectedItem is not string selected || selected.Length == 0) return null;
        var all = box.ItemsSource is IEnumerable<string> items ? items.FirstOrDefault() : null;
        return string.Equals(selected, all, StringComparison.Ordinal) ? null : selected;
    }

    // UI11: the Runs surface applies its own persisted view mode. The shared detail helper swaps the
    // column set and the row density only, so the same rows, selection and filters stay in place.
    internal void ApplyRunViewMode()
    {
        if (!_runPresentationReady || _runHistoryGrid is null) return;
        var detailed = IsDetailedView(RunsViewKey);
        if (_activityTabs.SelectedIndex == 2) _runViewSwitch?.SetView(detailed, persist: false);
        SharedPresentation.ApplyFeedDetail(_runHistoryGrid, detailed, RunDetailColumns,
            SharedPresentation.CompactRowHeight, SharedPresentation.DetailedRowHeight);
        // A detailed row set may be wider than the surface, so it may scroll horizontally; the
        // compact row set never needs to.
        ScrollViewer.SetHorizontalScrollBarVisibility(_runHistoryGrid,
            detailed ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
    }

    // UI11: one selected-run projection drives the shared details host, exactly as the Activity,
    // Pending and Recovery rows do. Expanding moves that same content into the details dialog and
    // returning restores the same selection.
    private void InspectRun(RunHistoryRow row)
    {
        if (!_workspaceReady) return;
        _runSelectedId = row.Id;
        if (ActivityPage.Visibility != Visibility.Visible) NavigateTo("Activity");
        _workspaceDetailTitle.Text = "Run Details";
        if (_detailSubtitle is not null) _detailSubtitle.Text = row.StatusLine;
        // The Activity inspector's actions belong to the inspected activity row, so they are disabled
        // rather than left enabled for a run that they cannot act on.
        _workspacePairAction.IsEnabled = false;
        _workspaceRecoveryAction.IsEnabled = false;
        if (_inspectorLocal is not null) _inspectorLocal.IsEnabled = false;
        if (_inspectorCloud is not null) _inspectorCloud.IsEnabled = false;
        if (_inspectorLink is not null) _inspectorLink.IsEnabled = false;
        if (_inspectorTypeIcon is not null) _inspectorTypeIcon.DataContext = row;
        RefreshRunInspector(row);
        _detailsHost?.Present(row);
        TechnicalText.Text = "Run ID: " + row.Id + "\nRecorded Kind: " + row.RecordedKind +
            "\nRecorded Status: " + row.RecordedStatus + "\nRetained Request: " + row.RetainedRequest +
            "\nRetained Result: " + row.RetainedResult;
        _workspaceInspector.Visibility = Visibility.Visible;
        _workspaceBody.ColumnDefinitions[1].Width = new GridLength(Math.Min(340, Math.Max(260, ActualWidth * .27)));
    }

    private void RefreshRunInspector(RunHistoryRow row)
    {
        if (_detailFields is null) return;
        _detailFields.Children.Clear();
        foreach (var field in row.Fields)
            _detailFields.Children.Add(Field(field.Label + ":",
                Text(field.Value, 13, brush: field.RequiresReading ? "MutedInk" : "Ink")));
        _detailFields.Children.Add(Button("Open detailed operation log", (_, _) => OpenOperationLog(row.Id)));
        try
        {
            using var request = JsonDocument.Parse(row.RetainedRequest);
            if (request.RootElement.TryGetProperty("retryOfRunId", out var original) &&
                original.ValueKind == JsonValueKind.String && original.GetString() is { Length: > 0 } id)
                _detailFields.Children.Add(AsyncButton("Open original failed run", async (_, _) => await OpenRetainedRunAsync(id), true));
        }
        catch (JsonException) { /* Legacy records stay visible without invented links. */ }
        var retries = new StackPanel();
        _detailFields.Children.Add(retries);
        _ = LoadRunRetryLinksAsync(row.Id, retries);
        // The current-session comparison keeps its one existing control; the selected run's details
        // only make that control reachable from here as well.
        _detailFields.Children.Add(Button("Current Session Comparison", (_, _) => ShowRunComparison()));
    }

    private void ShowRunComparison()
    {
        if (_runComparisonExpander is null) return;
        _runComparisonExpander.IsExpanded = true;
        _runComparisonExpander.BringIntoView();
    }

    private async Task LoadRunRetryLinksAsync(string runId, StackPanel target)
    {
        try
        {
            var retries = await Task.Run(() => _authorityHost.LoadRunRetriesAsync(runId));
            if (_runSelectedId != runId) return;
            foreach (var retry in retries)
                target.Children.Add(AsyncButton("Retry " + retry.StartedUtc.ToLocalTime().ToString("dd MMM HH:mm:ss") +
                    " — " + retry.Status, async (_, _) => await OpenRetainedRunAsync(retry.Id), true));
            if (retries.Count == 100) target.Children.Add(Text("Showing the latest 100 linked retries.", 12, brush: "MutedInk"));
        }
        catch (Exception error)
        {
            if (_runSelectedId == runId) target.Children.Add(Text("Retry links could not be read: " + SyncActivityText.Failure(error), 12));
        }
    }
}

// UI11: one projection of a retained run. Every value comes from the retained run, its retained
// request or result, and the pairs and accounts this window actually retains. A value the retained
// record does not carry keeps an explicit marker instead of being shown as zero or guessed, and a
// legacy record whose JSON cannot be decoded stays a visible, identifiable row rather than crashing
// the page. The type is public, and every column-bound member is a public property, because the
// shared Simple/Detailed helper binds its detailed columns through WPF data binding.
public sealed class RunHistoryRow
{
    internal const string NotRecorded = "Not recorded";

    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string DisplayName => Title;
    public string Path => Title;
    public string ShortAction => StatusLine;
    public string PairName => AccountsLabel;
    public string TimeLabel { get; init; } = string.Empty;
    public string FullTimestamp => StartedTooltip;
    public string TimeAccessibleLabel => StartedTooltip;
    public string StatusLabel { get; init; } = string.Empty;
    public string StatusGlyph { get; init; } = "·";
    public string StatusLine { get; init; } = string.Empty;
    public string StartedLabel { get; init; } = string.Empty;
    public string StartedLine { get; init; } = string.Empty;
    public string StartedTooltip { get; init; } = string.Empty;
    public string CompletedLabel { get; init; } = string.Empty;
    public string DurationLabel { get; init; } = string.Empty;
    public string DurationTooltip { get; init; } = string.Empty;
    public string DurationAccessibleLabel { get; init; } = string.Empty;
    public string TriggerLabel { get; init; } = string.Empty;
    public string PairsLabel { get; init; } = string.Empty;
    public string AccountsLabel { get; init; } = string.Empty;
    public string TotalsLabel { get; init; } = string.Empty;
    internal IReadOnlyList<string> PairIds { get; init; } = [];
    internal bool RequestReadable { get; init; }
    internal IReadOnlyList<RunDetailField> Fields { get; init; } = [];
    internal string SearchText { get; init; } = string.Empty;
    internal string RecordedStatus { get; init; } = string.Empty;
    internal string RecordedKind { get; init; } = string.Empty;
    internal string RetainedResult { get; init; } = string.Empty;
    internal string RetainedRequest { get; init; } = string.Empty;

    // The plain-text record carries the same labelled values as the inspector.
    internal string DetailText => string.Join("\n",
        Fields.Select(field => field.Label + ": " + field.Value));
}

internal sealed record RunDetailField(string Label, string Value, bool RequiresReading = false);
