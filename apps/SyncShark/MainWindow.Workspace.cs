using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TabControl = System.Windows.Controls.TabControl;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Control = System.Windows.Controls.Control;
using Brushes = System.Windows.Media.Brushes;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Binding = System.Windows.Data.Binding;
using Orientation = System.Windows.Controls.Orientation;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private bool _workspaceReady;
    private TextBlock? _operationFeedback;
    private bool _workspaceRefreshQueued;
    private string _workspacePage = "Dashboard";
    private readonly Stack<string> _workspaceBackStack = new();
    private readonly ObservableCollection<ActivityRow> _recentRows = [];
    private readonly ObservableCollection<ActivityRow> _homeRows = [];
    private readonly List<ActivityRow> _homeHistory = [];
    private readonly ObservableCollection<WorkspaceRun> _workspaceRuns = [];
    private readonly DispatcherTimer _workspaceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? _workspaceRunStarted;
    private string? _workspaceRunningPair;
    private bool _workspacePaused;
    private string _workspaceStage = "Checking";
    private string _workspacePhaseDetail = "Waiting for the next sync check.";
    private (string Detail, HashSet<string> PairIds, DateTimeOffset ReportedUtc)? _workspaceCommandFailure;
    private string? _workspaceReadbackFailure;
    private DateTimeOffset? _workspacePhaseStarted, _workspaceProgressUpdated;
    // ENG4: the latest retained typed first-sync problem of each pair, loaded from the retained command results.
    private Dictionary<string, SyncFirstSyncProblem> _firstSyncProblemsByPair = new(StringComparer.Ordinal);
    private Dictionary<string, SyncCommandProblem> _commandProblemsByPair = new(StringComparer.Ordinal);
    private string? _lastReportedWorkspaceStage;
    private Button _homePairLink = null!;
    private CommandGlyph _homeStateGlyph = null!;
    private Button _homeStateAction = null!;
    private ActivityRow? _inspectedActivity;
    private TextBlock _homeTitle = null!, _homeTiming = null!, _homeConnection = null!;
    private ContentPresenter _homeLastSync = null!;
    private SummaryActivityFilter _homeSummaryFilter = SummaryActivityFilter.Files;
    internal PageHeaderPanel _homeHeader = null!, _activityHeader = null!;
    internal DataGrid _pendingGrid = null!;
    internal SimpleDetailedSwitch? _activityViewSwitch;
    internal DetailsHost? _detailsHost;
    private readonly Dictionary<string, bool> _viewModes = new(StringComparer.Ordinal);
    private static readonly FeedDetailColumn[] ActivityDetailColumns =
        [new("Folder Pair", nameof(ActivityRow.PairName), 150), new("Endpoints", nameof(ActivityRow.Connection), 160),
         new("Format", nameof(ActivityRow.FileFormat), 80)];
    private static readonly FeedDetailColumn[] PendingDetailColumns =
        [new("Folder Pair", nameof(QueueRow.Pair), 160), new("Queued", nameof(QueueRow.Enqueued), 140)];
    // UI16: the recovery page's Detailed columns. Recovery keeps its own account, saved-location and
    // version columns so the exact saved copy stays identifiable, while the shared detail helper adds
    // and removes them without reloading, reordering or reselecting the rows.
    private static readonly FeedDetailColumn[] RecoveryDetailColumns =
        [new("Account", nameof(RecoveryRow.Connection), 160),
         new("Saved Location", nameof(RecoveryRow.Location), 220),
         new("Version", nameof(RecoveryRow.VersionIdentity), 200),
         new("Retained", nameof(RecoveryRow.Retained), 150),
         new("Retention", nameof(RecoveryRow.Retention), 180),
         new("Health", nameof(RecoveryRow.Health), 190)];
    private Button _homePending = null!, _workspaceBack = null!;
    private Button _workspacePairAction = null!, _workspaceRecoveryAction = null!;
    private Grid _homeColumns = null!, _workspaceBody = null!;
    private StackPanel _homeSupport = null!;
    private Border _workspaceInspector = null!;
    private TextBlock _workspaceDetailTitle = null!;
    private TabControl _activityTabs = null!;
    private DataGrid _homeFeed = null!;
    private WrapPanel _activityExtraFilters = null!;
    private TextBlock _workspaceEmpty = null!;
    private TextBlock _activityEmpty = null!;
    private bool _includeRoutineChecks;
    private StackPanel? _activityToolbar;
    private int _workspaceActivityTab;
    // P-FEED: the unified feed's own controls. The category chips, the styled no-changes toggle,
    // the independent severity/entity/run filters and the refresh feedback all live here so the
    // existing shared navigation and status paths are untouched.
    private WrapPanel? _activityCategoryBar;
    private readonly List<ToggleButton> _activityCategoryChips = [];
    private readonly Dictionary<string, ToggleButton> _activityCategoryChipByValue = new(StringComparer.Ordinal);
    private ToggleButton? _activityAllCategoriesChip, _activityNoChangesToggle;
    private TextBlock? _activityRefreshNote;
    private FrameworkElement? _activityRunFilterSurface;
    private ComboBox? _activitySeverityBox, _activityEntityBox;
    private System.Windows.Controls.TextBox? _activityRunBox;
    private Button? _activityRefresh;
    private bool _activityRefreshing, _activityUpdatingCategories, _activityLoadFailed, _activityHasMore;
    private string? _activityLoadFailure;
    // UI09: Refresh reports the query generation that actually reached the list rather than assuming
    // its own request won, so a superseded response is never described as applied.
    private int _activityLastAppliedQueryVersion;
    internal event EventHandler? WorkspacePresentationChanged;
    internal event EventHandler? CompactViewRequested;
    internal IReadOnlyList<ActivityRow> TrayActivity => _homeHistory.ToArray();
    internal string TrayStatus => _workspaceReady ? GlobalWorkspaceStatus : "Starting Sync Shark";
    private string GlobalWorkspaceStatus => TrayPaused ? "Sync Paused" : _runActive ? _workspaceStage :
        _workspaceReadbackFailure is not null ? "Results Could Not Be Refreshed" :
        _workspaceCommandFailure is not null ? "Needs Attention" :
        PresentationAttentionCases.Any() ? "Needs Attention" : _globalPendingQueueCount > 0 ? "Work Waiting to Finish" :
        _pairSet.Pairs.Count == 0 ? "Choose Folders to Sync" : _connectionStatus is { HasProtectedCredential: false } ? "Connect Dropbox" : !AutomaticSyncEnabled ? "Live Sync Paused" :
        _connectionStatus?.LiveVerified == true && _connectionStatus.LastSuccessfulSyncUtc is not null ? "Up to Date" : "Ready to Check";
    internal string TrayDetail => WorkspacePhaseDetail;
    private string WorkspacePhaseDetail => GlobalPauseActive ? PauseDescription + ". Folder settings are unchanged." : _runActive ? _workspacePhaseDetail : ReadbackFailureDetail(scoped: false) ?? CommandFailureDetail(scoped: false) ?? AttentionProblemDetail(scoped: false) ?? (PresentationAttentionCases.Any()
        ? "Open Needs Attention to review decisions and folder or connection problems."
        : _globalPendingQueueCount > 0 ? "Open Pending to see the exact operations still waiting."
        : _pairSet.Pairs.Count == 0 ? "Add a folder pair to choose what stays in sync."
        : !AutomaticSyncEnabled ? "Automatic sync is paused. You can still sync manually."
        : _connectionStatus?.LiveVerified == true ? "Your selected folders are monitored for changes."
        : _connectionStatus?.Health ?? "Checking the saved Dropbox connection…");

    // A terminal failure must remain readable after the run controls reset. The same
    // attention cases drive Home, tray and Needs Attention; account filtering must not
    // replace a real problem with generic navigation text or hide its description.
    private string? CommandFailureDetail(bool scoped) => _workspaceCommandFailure is { } failure &&
        (!scoped || _workspaceAccountId is null || failure.PairIds.Any(ScopeMatches)) ? failure.Detail : null;

    private string? ReadbackFailureDetail(bool scoped) => _workspaceReadbackFailure is { } detail
        ? detail + (CommandFailureDetail(scoped) is { } command ? Environment.NewLine + command : "") : null;

    private string? AttentionProblemDetail(bool scoped)
    {
        var problem = PresentationAttentionCases
            .Where(item => item.IsProblem && (!scoped || ScopeMatches(item.Pair.Id)))
            .OrderByDescending(item => item.Pair.Id == _workspaceRunningPairId)
            .FirstOrDefault();
        if (problem is null) return null;
        var currentFailure = problem.Pair.Id == _workspaceRunningPairId &&
            _runFailurePairIds.Contains(problem.Pair.Id) &&
            _workspaceStage.Contains("needs attention", StringComparison.OrdinalIgnoreCase);
        return currentFailure
            ? _workspacePhaseDetail + Environment.NewLine + problem.Description
            : problem.Pair.Name + ": " + problem.Description;
    }
    internal string TrayConnection => _connectionStatus?.Health ?? "Checking connection";
    internal bool TrayRunActive => _runActive;
    // Core v2 continuous sync only: any pair running automatically shows the tray as working, while _runActive
    // stays the manual run's own flag. Under core v1 no automatic run is tracked, so this is always false.
    internal bool TrayAutoRunActive => _autoRunningPairs.Count > 0;
    // SS-D02: the newest run this window can actually classify, with the completion identity the tray
    // de-duplicates on. The tray notification is built from that run's classified outcome, so a
    // timestamp never decides what the notification says and a stopped, partial or unrecorded run is
    // never announced as a completion.
    internal TrayLatestRun? LatestRunForTray
    {
        get
        {
            var current = _workspaceRuns.FirstOrDefault() is { } run
                ? new TrayLatestRun(run.Pair.Id + "@" + run.Completed.ToUnixTimeMilliseconds(),
                    run.Completed, run.Pair.Name, SyncRunOutcomeProjection.FromResult(run.Result))
                : _retainedRuns.FirstOrDefault(item => item.CompletedUtc is not null)
                    is { CompletedUtc: { } retainedCompleted } retained
                    ? new TrayLatestRun(retained.Id + "@" + retainedCompleted.ToUnixTimeMilliseconds(),
                        retainedCompleted, RetainedRunPairNames(retained), ProjectRetainedRun(retained).Outcome)
                    : null;
            // Core v2 only: the newest automatic (continuous-sync) run joins the comparison; null under v1.
            if (_latestAutoRunForTray is { } auto && (current is null || auto.CompletedUtc > current.CompletedUtc))
                return auto;
            return current;
        }
    }
    internal bool TrayProgressIndeterminate => RunProgress.IsIndeterminate;
    internal double TrayProgressValue => RunProgress.Value;
    internal double TrayProgressMaximum => RunProgress.Maximum;
    internal string TrayProgressDetails => _workspacePhaseDetail +
        (_workspacePhaseStarted is { } stage ? $"\nStage Started {stage.ToLocalTime():HH:mm:ss}" : "") +
        (_workspaceProgressUpdated is { } updated ? $"\nLast Progress {updated.ToLocalTime():HH:mm:ss}" +
            (DateTimeOffset.UtcNow - updated > TimeSpan.FromSeconds(20)
                ? $" — no further progress reported for {(DateTimeOffset.UtcNow - updated).TotalSeconds:N0}s; cause not yet known. Pause/stop remains available."
                : "") : "");
    internal string? LastWorkspaceFailure { get; private set; }
    internal bool TrayPaused => _workspacePaused || GlobalPauseActive;
    internal int TrayPending => _globalPendingQueueCount;
    internal IReadOnlyList<PairRow> TrayAttention => PresentationAttentionCases.Select(item => item.Pair).DistinctBy(pair => pair.Id).ToArray();

    private void BuildWorkspace()
    {
        WorkspaceInput.Enable(this);
        var outer = (Grid)DashboardPage.Parent;
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        foreach (UIElement child in outer.Children) Grid.SetRow(child, 1);
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _operationFeedback = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxHeight = 72,
            Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        Grid.SetRow(_operationFeedback, 2);
        outer.Children.Add(_operationFeedback);
        _workspaceBack = Button("← Back", WorkspaceBack_Click, quiet: true);
        _workspaceBack.HorizontalAlignment = HorizontalAlignment.Left;
        _workspaceBack.Margin = new Thickness(0, 0, 0, 10);
        _workspaceBack.Visibility = Visibility.Collapsed;
        outer.Children.Add(_workspaceBack);

        var home = new Grid();
        home.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        home.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        home.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var homeAccount = BuildWorkspaceAccountPicker("Home");
        SharedPresentation.ApplyQuietDropdowns(homeAccount);
        _homeHeader = SharedPresentation.PageHeader("Home", homeAccount);
        home.Children.Add(_homeHeader);

        var status = new Grid();
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        status.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        status.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _homeTitle = Text("Checking saved state", 24, true);
        _homeStateGlyph = new CommandGlyph { IconName = "sync", Width = 38, Height = 38, Margin = new Thickness(0, 0, 18, 0) };
        Grid.SetRowSpan(_homeStateGlyph, 3); status.Children.Add(_homeStateGlyph);
        Grid.SetColumn(_homeTitle, 1); status.Children.Add(_homeTitle);
        var runActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        RunButton.Content = "Sync now";
        runActions.Children.Add(Detach(RunButton));
        runActions.Children.Add(Detach(PauseButton));
        var overflow = Button("⋯", (_, _) => { }, quiet: true);
        var controlMenu = WorkspaceMenu();
        var stop = new MenuItem { Header = CommandIcons.Label("Stop Current Sync", "stop") };
        System.Windows.Automation.AutomationProperties.SetName(stop, "Stop Current Sync");
        stop.Click += Stop_Click;
        ActionAvailability.Bind(stop, () => "There is no active scan or sync to stop.");
        controlMenu.Items.Add(stop);
        var activity = new MenuItem { Header = CommandIcons.Label("View Full Activity", "activity") };
        System.Windows.Automation.AutomationProperties.SetName(activity, "View Full Activity");
        activity.Click += OpenActivity_Click;
        controlMenu.Items.Add(activity);
        var compact = new MenuItem { Header = CommandIcons.Label("Open Compact View", "computer") };
        System.Windows.Automation.AutomationProperties.SetName(compact, "Open Compact View");
        compact.Click += (_, _) => CompactViewRequested?.Invoke(this, EventArgs.Empty);
        controlMenu.Items.Add(compact);
        overflow.ContextMenu = controlMenu;
        overflow.Click += (_, _) => { stop.IsEnabled = _runActive; controlMenu.PlacementTarget = overflow; controlMenu.IsOpen = true; };
        runActions.Children.Add(overflow);
        Grid.SetColumn(runActions, 2); status.Children.Add(runActions);
        var progress = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        _homePairLink = Button("", (_, _) => OpenRunningPair(), true);
        _homePairLink.HorizontalAlignment = HorizontalAlignment.Left; _homePairLink.Padding = new Thickness(0);
        progress.Children.Add(_homePairLink);
        progress.Children.Add(Detach(PhaseText));
        _homeStateAction = Button("Review needs attention", HomeStateAction_Click, true);
        _homeStateAction.HorizontalAlignment = HorizontalAlignment.Left;
        _homeStateAction.Padding = new Thickness(0, 6, 0, 0);
        progress.Children.Add(_homeStateAction);
        PhaseText.FontSize = 13;
        PhaseText.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
        progress.Children.Add(Detach(RunProgress));
        RunProgress.Height = 4;
        RunProgress.BorderThickness = new Thickness(0);
        RunProgress.Margin = new Thickness(0, 18, 0, 8);
        RunProgress.SetResourceReference(Control.ForegroundProperty, "FocusBrush");
        Grid.SetRow(progress, 1); Grid.SetColumn(progress, 1); Grid.SetColumnSpan(progress, 2); status.Children.Add(progress);
        _homeTiming = Text("Waiting for the first check", 12, brush: "MutedInk");
        Grid.SetRow(_homeTiming, 2); Grid.SetColumn(_homeTiming, 1); status.Children.Add(_homeTiming);
        _homePending = Button("Pending work →", (_, _) =>
        {
            if (_runActive) OpenOperationLog(_authorityHost.ActiveRun?.Id);
            else OpenScopedActivity(1);
        }, quiet: true);
        Grid.SetRow(_homePending, 2); Grid.SetColumn(_homePending, 2); status.Children.Add(_homePending);
        var statusCard = Card(status);
        statusCard.Margin = new Thickness(0, 0, 0, 18);
        Grid.SetRow(statusCard, 1); home.Children.Add(statusCard);

        _homeColumns = new Grid();
        _homeColumns.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _homeColumns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _homeColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _homeColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        var recent = new Grid();
        recent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        recent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        recent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var recentHeader = new DockPanel { Margin = new Thickness(6, 0, 4, 12) };
        var viewAll = Button("View all ↗", (_, _) => OpenWorkspaceActivity(0), quiet: true);
        DockPanel.SetDock(viewAll, Dock.Right); recentHeader.Children.Add(viewAll);
        recentHeader.Children.Add(Text("Recent Activity", 17, true));
        recent.Children.Add(recentHeader);
        _homeFeed = new DataGrid { ItemsSource = _homeRows, MinHeight = 260, MaxHeight = 370, VerticalAlignment = VerticalAlignment.Top };
        StyleFeed(_homeFeed); WireFeed(_homeFeed);
        Grid.SetRow(_homeFeed, 1); recent.Children.Add(_homeFeed);
        _workspaceEmpty = Text("No recent actions yet. Your next sync will appear here.", 14, brush: "MutedInk");
        _workspaceEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _workspaceEmpty.Margin = new Thickness(18); _workspaceEmpty.IsHitTestVisible = false;
        Grid.SetRow(_workspaceEmpty, 1); recent.Children.Add(_workspaceEmpty);
        var summarySelector = SharedPresentation.SummarySelector(mode => { _homeSummaryFilter = mode; RefreshWorkspaceFeeds(); });
        Grid.SetRow(summarySelector, 2); recent.Children.Add(summarySelector);
        var recentCard = Card(recent, 14); recentCard.Margin = new Thickness(0, 0, 18, 0);
        _homeColumns.Children.Add(recentCard);
        _homeSupport = new StackPanel();
        var last = new StackPanel(); last.Children.Add(Text("Last Sync Run", 17, true));
        _homeLastSync = new ContentPresenter { Content = SharedPresentation.LabelledSummary("No completed sync yet.") };
        _homeLastSync.Margin = new Thickness(0, 18, 0, 14); last.Children.Add(_homeLastSync);
        last.Children.Add(Button("View runs ↗", (_, _) => OpenWorkspaceActivity(2), quiet: true));
        var lastCard = Card(last); lastCard.Margin = new Thickness(0, 0, 0, 18); _homeSupport.Children.Add(lastCard);
        var connection = new StackPanel(); connection.Children.Add(Text("Dropbox", 17, true));
        _homeConnection = Text("Checking account…", 13, brush: "MutedInk");
        _homeConnection.Margin = new Thickness(0, 14, 0, 16); connection.Children.Add(_homeConnection);
        connection.Children.Add(Detach(ConnectButton));
        _homeSupport.Children.Add(Card(connection));
        Grid.SetColumn(_homeSupport, 1); _homeColumns.Children.Add(_homeSupport);
        Grid.SetRow(_homeColumns, 2); home.Children.Add(_homeColumns);
        DashboardPage.Child = new ScrollViewer { Content = home, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var workspace = new Grid();
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // UI09: Refresh reports its own busy, completed and error state through a dedicated note,
        // never by reusing the shared navigation/status path.
        var refresh = Button("Refresh", RefreshActivity_Click, quiet: true,
            explanation: "Reload recorded activity for the selected account and filters. This does not scan folders or start a sync.");
        _activityRefresh = refresh;
        _activityViewSwitch = SharedPresentation.ViewSwitch("activity", false, ActivityViewChanged);
        var activityAccount = BuildWorkspaceAccountPicker("Activity");
        SharedPresentation.ApplyQuietDropdowns(activityAccount);
        _activityHeader = SharedPresentation.PageHeader("Activity", activityAccount, refresh, _activityViewSwitch);
        workspace.Children.Add(_activityHeader);
        var toolbar = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        _activityToolbar = toolbar;
        _activityCategoryBar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        BuildActivityCategoryBar(_activityCategoryBar);
        toolbar.Children.Add(_activityCategoryBar);
        var primaryFilters = new WrapPanel();
        var searchSurface = SharedPresentation.QuietSearchField(ActivitySearchBox, "Search activity",
            "Search activity…", SubmitActivitySearch, tooltip: "Search activity by name, path, pair, action or explanation");
        searchSurface.Margin = new Thickness(0, 0, 12, 8);
        primaryFilters.Children.Add(searchSurface);
        primaryFilters.Children.Add(Detach(ActivityPairBox)); ActivityPairBox.Margin = new Thickness(0, 0, 12, 8);
        primaryFilters.Children.Add(Button("Filters", (_, _) => _activityExtraFilters.Visibility =
            _activityExtraFilters.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible, quiet: true));
        primaryFilters.Children.Add(Button("Review conflicts", OpenConflicts_Click, quiet: true));
        toolbar.Children.Add(primaryFilters);
        _activityExtraFilters = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 4) };
        _activityExtraFilters.Children.Add(Detach(ActivityFilterBox));
        _activityExtraFilters.Children.Add(Detach(ActivityTypeFilter));
        _activityExtraFilters.Children.Add(Detach(ActivityDateBox)); toolbar.Children.Add(_activityExtraFilters);
        _activitySeverityBox = new ComboBox { Width = 140, SelectedIndex = 0, Style = SharedPresentation.QuietComboBoxStyle(), ToolTip = "Filter by severity. Severity is independent of category.", Margin = new Thickness(0, 0, 12, 0) };
        System.Windows.Automation.AutomationProperties.SetName(_activitySeverityBox, "Filter activity by severity");
        foreach (var severity in new[] { "All severities", "Information", "Success", "Warning", "Error" })
            _activitySeverityBox.Items.Add(new ComboBoxItem { Content = severity });
        _activitySeverityBox.SelectionChanged += ActivityFilter_Changed;
        _activityExtraFilters.Children.Add(_activitySeverityBox);
        _activityEntityBox = new ComboBox { Width = 150, SelectedIndex = 0, Style = SharedPresentation.QuietComboBoxStyle(), ToolTip = "Filter by the kind of record. The entity is independent of category.", Margin = new Thickness(0, 0, 12, 0) };
        System.Windows.Automation.AutomationProperties.SetName(_activityEntityBox, "Filter activity by entity");
        foreach (var entity in new[] { "All entities", "File", "Folder", "Folder pair", "Run", "Account", "Application" })
            _activityEntityBox.Items.Add(new ComboBoxItem { Content = entity });
        _activityEntityBox.SelectionChanged += ActivityFilter_Changed;
        _activityExtraFilters.Children.Add(_activityEntityBox);
        _activityRunBox = new System.Windows.Controls.TextBox();
        _activityRunBox.LostKeyboardFocus += (_, e) => ActivityFilter_Changed(_activityRunBox, e);
        _activityRunFilterSurface = SharedPresentation.QuietSearchField(_activityRunBox,
            "Filter activity by run", "Run ID…", () => ActivityFilter_Changed(_activityRunBox, new RoutedEventArgs()),
            width: 180, tooltip: "Filter by one exact run id. The run filter is independent of category.");
        _activityRunFilterSurface.Margin = new Thickness(0, 0, 12, 0);
        _activityExtraFilters.Children.Add(_activityRunFilterSurface);
        // UI09: the no-changes control is a styled pressed toggle with its own explanation, never a
        // native checkbox, and routine checks stay out of the feed until it is pressed.
        _activityNoChangesToggle = PressedToggle("Show no-change checks",
            "Include routine checks that found no changes. They are hidden because setup and repeated checks are not transfers.");
        _activityNoChangesToggle.IsChecked = _includeRoutineChecks;
        _activityNoChangesToggle.Checked += async (_, _) => { _includeRoutineChecks = true; await LoadActivityPageAsync(true); };
        _activityNoChangesToggle.Unchecked += async (_, _) => { _includeRoutineChecks = false; await LoadActivityPageAsync(true); };
        var checks = new WrapPanel { Margin = new Thickness(12, 0, 0, 6) };
        checks.Children.Add(_activityNoChangesToggle);
        _activityExtraFilters.Children.Add(checks);
        var activityFooter = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        ActivityPageSummary.Margin = new Thickness(0, 0, 16, 0);
        activityFooter.Children.Add(Detach(ActivityPageSummary));
        _activityRefreshNote = Text("", 12, brush: "MutedInk");
        _activityRefreshNote.Margin = new Thickness(0);
        System.Windows.Automation.AutomationProperties.SetName(_activityRefreshNote, "Activity refresh status");
        activityFooter.Children.Add(_activityRefreshNote);
        Grid.SetRow(toolbar, 1); workspace.Children.Add(toolbar);
        _workspaceBody = new Grid();
        _workspaceBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _workspaceBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        _activityTabs = new TabControl { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        _activityTabs.ItemContainerStyle = WorkspaceTabStyle();
        var recentPage = new DockPanel();
        DockPanel.SetDock(activityFooter, Dock.Bottom); recentPage.Children.Add(activityFooter);
        DockPanel.SetDock(Detach(LoadMoreActivityButton), Dock.Bottom); recentPage.Children.Add(LoadMoreActivityButton);
        LoadMoreActivityButton.HorizontalAlignment = HorizontalAlignment.Left;
        // UI06: the activity table carries a real Time column immediately before Size.
        StyleFeed(ActivityGrid, activityColumns: true); WireFeed(ActivityGrid); ActivityGrid.ItemsSource = _recentRows;
        GroupFeed(ActivityGrid);
        var recentSurface = new Grid(); recentSurface.Children.Add(Detach(ActivityGrid));
        _activityEmpty = Text("No matching actions. Change the filters or wait for your next sync.", 14, brush: "MutedInk");
        _activityEmpty.HorizontalAlignment = HorizontalAlignment.Center; _activityEmpty.Margin = new Thickness(18, 28, 18, 0);
        _activityEmpty.IsHitTestVisible = false; recentSurface.Children.Add(_activityEmpty);
        // The former Event Log tab's live session messages must stay reachable after the tab is
        // removed, so they move into the unified feed's Diagnostics presentation.
        // Detailed operations have their own full-size retained table, not a truncated session console.
        Detach(ConsoleText);
        recentPage.Children.Add(recentSurface);
        _activityTabs.Items.Add(new TabItem { Header = "Recent", Content = recentPage });
        Detach(QueuePage); QueuePage.Visibility = Visibility.Visible;
        var queueGrid = ((Grid)QueuePage.Child).Children.OfType<DataGrid>().Single();
        _pendingGrid = queueGrid;
        StyleFeed(queueGrid);
        queueGrid.RowHeight = 90;
        queueGrid.SelectionChanged += (_, _) => { if (queueGrid.SelectedItem is QueueRow row) InspectQueue(row); };
        queueGrid.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not FrameworkElement { DataContext: QueueRow row }) return;
            var item = QueueActivity(row);
            OpenWorkspaceMenu(e.OriginalSource as UIElement ?? queueGrid, item); e.Handled = true;
        }));
        queueGrid.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is not QueueRow row) return;
            OpenWorkspaceMenu(queueGrid, QueueActivity(row)); e.Handled = true;
        };
        _activityTabs.Items.Add(new TabItem { Header = "Pending", Content = QueuePage });

        var runs = new Grid();
        runs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        runs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        runs.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var runChoice = new ComboBox { ItemsSource = _workspaceRuns, DisplayMemberPath = nameof(WorkspaceRun.Label), Margin = new Thickness(0, 10, 0, 8) };
        runChoice.SelectionChanged += (_, _) => { if (runChoice.SelectedItem is WorkspaceRun run) ShowWorkspaceRun(run); };
        runs.Children.Add(runChoice);
        var runIntro = new StackPanel();
        runIntro.Children.Add(Text("Runs completed during this app session stay a summary view. The per-event detail is in the unified activity list.", 12, brush: "MutedInk"));
        runIntro.Children.Add(Detach(SummaryText)); SummaryText.Margin = new Thickness(0, 5, 0, 10);
        runIntro.Children.Add(Detach(FilterBox)); FilterBox.ToolTip = "Filter this run's complete comparison, including unchanged items";
        Grid.SetRow(runIntro, 1); runs.Children.Add(runIntro);
        ResultGrid.BorderThickness = new Thickness(0); ResultGrid.MinHeight = 140;
        Grid.SetRow(Detach(ResultGrid), 2); runs.Children.Add(ResultGrid);
        _activityTabs.Items.Add(new TabItem { Header = "Runs", Content = runs });
        BuildOperationLog();
        // UI07: Recent and Event Log are now one unified activity list. Nothing from the old Event
        // Log tab is dropped: its rows are the same retained events, shown once each by stable event
        // identity, and its live session messages are reachable under the Diagnostics category.
        _activityTabs.SelectedIndex = 0;
        _activityTabs.SelectionChanged += async (_, e) =>
        {
            if (!ReferenceEquals(e.Source, _activityTabs)) return;
            if (_workspaceActivityTab == _activityTabs.SelectedIndex) return;
            _workspaceActivityTab = _activityTabs.SelectedIndex;
            toolbar.Visibility = _activityTabs.SelectedIndex == 3 ? Visibility.Collapsed : Visibility.Visible;
            PositionActivityToolbar();
            if (_workspaceReady) CloseWorkspaceDetails();
            if (_workspaceReady) await RefreshVisibleActivityAsync();
        };
        _workspaceBody.Children.Add(_activityTabs);

        var details = new StackPanel();
        var close = Button("Close details ×", (_, _) => CloseWorkspaceDetails(), quiet: true);
        close.HorizontalAlignment = HorizontalAlignment.Right; details.Children.Add(close);
        _workspaceDetailTitle = Text("Item details", 18, true); _workspaceDetailTitle.Margin = new Thickness(0, 12, 0, 12);
        details.Children.Add(_workspaceDetailTitle);
        Detach(DetailsText); DetailsText.BorderThickness = new Thickness(0); DetailsText.Background = Brushes.Transparent;
        DetailsText.TextWrapping = TextWrapping.Wrap; DetailsText.MinHeight = 170;
        details.Children.Add(DetailsText);
        var detailActions = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
        _workspacePairAction = Button("Open pair", OpenActivityPair_Click, quiet: true);
        detailActions.Children.Add(_workspacePairAction);
        detailActions.Children.Add(Button("Copy details", CopyActivityDetails_Click, quiet: true));
        _workspaceRecoveryAction = Button("Recovery", OpenActivityRecovery_Click, quiet: true);
        detailActions.Children.Add(_workspaceRecoveryAction);
        details.Children.Add(detailActions);
        Detach(TechnicalText); TechnicalText.TextWrapping = TextWrapping.Wrap; TechnicalText.MinHeight = 100;
        details.Children.Add(new Expander { Header = "Technical details", Content = TechnicalText });
        _workspaceInspector = Card(new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 14);
        _workspaceInspector.Margin = new Thickness(16, 0, 0, 0); _workspaceInspector.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_workspaceInspector, 1); _workspaceBody.Children.Add(_workspaceInspector);
        Grid.SetRow(_workspaceBody, 2); workspace.Children.Add(_workspaceBody);
        ActivityPage.Child = workspace;
        ActivityRows.CollectionChanged += (_, _) => QueueWorkspaceRefresh();
        _workspaceTimer.Tick += (_, _) => { RefreshWorkspace(); _ = RefreshLocalRootHealthAsync(); RefreshFollowingOperationLog(); };
        _workspaceTimer.Start();
        _workspaceReady = true;
        DashboardPage.SizeChanged += (_, _) => UpdateHomeResponsiveLayout();
        RefreshWorkspace();
    }

    private void WireFeed(DataGrid feed)
    {
        feed.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && feed.SelectedItem is ActivityRow row) { InspectActivity(row); e.Handled = true; }
        };
        feed.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
            if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is ActivityRow row) InspectActivity(row);
        };
        feed.PreviewMouseRightButtonDown += (_, e) =>
        {
            var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as ActivityRow;
            if (row is null) return;
            ActivityGrid.SelectedItem = row;
            OpenWorkspaceMenu(feed, row); e.Handled = true;
        };
        feed.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            var button = FindAncestor<Button>(e.OriginalSource as DependencyObject);
            if (button?.DataContext is not ActivityRow row) return;
            if (button.Tag as string == "pair") { OpenActivityPair(row); e.Handled = true; return; }
            OpenWorkspaceMenu(e.OriginalSource as UIElement ?? feed, row); e.Handled = true;
        }));
    }

    internal void OpenActivityPair(ActivityRow row)
    {
        _inspectedActivity = row; ShowFromBackground(); OpenActivityPair_Click(this, new RoutedEventArgs());
    }

    internal void OpenWorkspaceMenu(UIElement target, ActivityRow row)
    {
        _inspectedActivity = row;
        var menu = WorkspaceMenu(); menu.PlacementTarget = target; menu.Placement = PlacementMode.MousePoint;
        Add("Details", (_, _) => InspectActivity(row));
        // EPS3B: a row that recorded its ordered context offers ONE location action per recorded endpoint,
        // each carrying that endpoint's exact provider, account and root. A disabled action is present and
        // carries its reason as its tooltip. Legacy rows keep the existing single current-binding action.
        if (row.EndpointContext is { } context)
            foreach (var action in ActivityEndpointLocationActions(row))
            {
                // One entry per recorded endpoint, built from the same action the details sidebar renders,
                // so the menu and the inspector can never disagree about the side, account or enablement.
                var endpoint = action.Side == SyncActivitySide.Left ? context.Left : context.Right;
                MenuAction(menu, action.Label, action.IsLocal ? "folder" : "external", action.Reason,
                    (_, _) => OpenEndpointLocationAsync(row, endpoint), action.Enabled);
            }
        Add("Show in File Explorer", (_, _) => OpenLocalItem(row), LocalItemPath(row) is not null);
        Add("Open in " + row.CloudName, (_, _) => OpenCloudItem(row), CanResolveCloudItem(row));
        Add("Copy " + row.CloudName + " Location Link", (_, _) => CopyCloudItemUrl(row), CanResolveCloudItem(row));
        Add("Share & Manage Links", (_, _) => OpenSharing(row), !row.Path.StartsWith('<') && _pairSet.Pairs.Any(pair => pair.Id == row.PairId));
        menu.Items.Add(new Separator());
        Add("Open Folder Pair", OpenActivityPair_Click, PairRows.Any(pair => pair.Id == row.PairId));
        if (IsRecoveryActivity(row)) Add("Open Recovery", OpenActivityRecovery_Click);
        menu.Items.Add(new Separator());
        Add("Copy Relative Path", (_, _) => SetClipboardText(row.Path), !row.Path.StartsWith('<'));
        Add("Copy Local Path", (_, _) => SetClipboardText(LocalItemPath(row)!), LocalItemPath(row) is not null);
        Add("Copy " + row.CloudName + " Path", (_, _) => SetClipboardText(CloudItemPath(row)!), CloudItemPath(row) is not null);
        Add("Copy Details", (_, _) => SetClipboardText(ActivityDetails(row)));
        menu.IsOpen = true;
        void Add(string title, RoutedEventHandler action, bool enabled = true)
        { MenuAction(menu, title, action, enabled); }
    }

    private void QueueWorkspaceRefresh()
    {
        if (!_workspaceReady || _workspaceRefreshQueued) return;
        _workspaceRefreshQueued = true;
        Dispatcher.BeginInvoke(() => { _workspaceRefreshQueued = false; RefreshWorkspaceFeeds(); }, DispatcherPriority.Background);
    }

    private void RefreshWorkspaceFeeds()
    {
        if (!_workspaceReady) return;
        _recentRows.Clear();
        // UI07: the unified list keeps every distinct retained event. Deduplication is by stable
        // event identity only, so a shared operation id never collapses a start, an operation and
        // a terminal row into one entry the way the Home and tray summary still deliberately do.
        var eligible = ActivityRows
            .Where(row => row.MatchesAccount(_workspaceAccountId))
            .Where(row => _includeRoutineChecks || !row.IsRoutineCheck)
            .DistinctBy(row => row.EventKey, StringComparer.Ordinal);
        // UI08/6.1: the Sync log is a chronology, so it is ordered by the recorded instant. A row
        // without a usable recorded time has no honest position in that sequence and sorts after
        // the dated rows instead of being given an invented one.
        if (ActivityReadsOldestFirst())
            eligible = eligible.Select((row, index) => (row, index))
                .OrderBy(item => ActivityInstant(item.row) ?? DateTimeOffset.MaxValue)
                .ThenBy(item => item.index).Select(item => item.row);
        else
            eligible = eligible.OrderByDescending(row => ActivityInstant(row) ?? DateTimeOffset.MinValue);
        foreach (var row in eligible) _recentRows.Add(row);
        _homeRows.Clear();
        foreach (var row in SharedPresentation.SummaryRows(_workspaceAccountId is null ? _homeHistory :
                     _scopedHomeHistory.Concat(_homeHistory), _workspaceAccountId, _homeSummaryFilter, 5)) _homeRows.Add(row);
        _workspaceEmpty.Visibility = _homeRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _activityEmpty.Visibility = _recentRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _workspaceEmpty.Text = EmptyHomeFeedText();
        _activityEmpty.Text = _activityPageLoads > 0 ? "Loading retained activity…" :
            _activityLoadFailed ? ActivityCountText() : "No activity matches the current account and filters.";
        if (_activityTabs.SelectedIndex == 0)
            ActivityPageSummary.Text = ActivityCountText();
        WorkspacePresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    // ENG5: an empty Home feed explains the current account scope and summary filter instead of implying that
    // history is empty or was purged. The unfiltered default keeps its original wording.
    private string EmptyHomeFeedText() =>
        _workspaceAccountId is null && _homeSummaryFilter == SummaryActivityFilter.Files
            ? "No recent actions yet. Your next sync will appear here."
            : "No recent " + (_homeSummaryFilter == SummaryActivityFilter.Files ? "file " : "") + "actions" +
              (_workspaceAccountId is null ? "" : " for " + WorkspaceScopeLabel) +
              ". Other retained history is unchanged; choose All Accounts or another summary to see it.";

    private static DateTimeOffset? ActivityInstant(ActivityRow row) =>
        DateTimeOffset.TryParse(row.When, out var occurred) ? occurred.ToUniversalTime() : null;

    // ---------------------------------------------------------------------------------------------
    // P-FEED (UI06, UI07, UI09): the unified feed's categories, its independent filters, its
    // honest counts and gap disclosure, and its own refresh feedback.
    // ---------------------------------------------------------------------------------------------

    // UI07 / D3: one multi-select chip per category plus All. Categories combine and All is one
    // appearance per event, which is simply the absence of a category constraint. Nothing that was
    // only reachable from the retired Event Log tab is dropped: the retained rows are the same
    // events, and the live session messages stay reachable under Diagnostics.
    private void BuildActivityCategoryBar(WrapPanel bar)
    {
        _activityCategoryChips.Clear(); _activityCategoryChipByValue.Clear();
        _activityUpdatingCategories = true;
        try
        {
            _activityAllCategoriesChip = PressedToggle("All",
                "Show every category once each, with no category filter.");
            _activityAllCategoriesChip.Margin = new Thickness(0, 0, 8, 8);
            _activityAllCategoriesChip.Checked += ActivityCategory_Click;
            _activityAllCategoriesChip.Unchecked += ActivityCategory_Click;
            bar.Children.Add(_activityAllCategoriesChip);
            foreach (var category in SyncActivityCategories.All)
            {
                var chip = PressedToggle(category, category + " records shown in this one list. " +
                    "Categories combine, and none of them changes the severity, entity, run, date, account, pair or text filters.");
                chip.Tag = category;
                chip.Margin = new Thickness(0, 0, 8, 8);
                chip.Checked += ActivityCategory_Click;
                chip.Unchecked += ActivityCategory_Click;
                _activityCategoryChips.Add(chip);
                _activityCategoryChipByValue[category] = chip;
                bar.Children.Add(chip);
            }
            // D3 default: File activity, so the first view is the familiar activity list.
            _activityCategoryChipByValue[SyncActivityCategories.Files].IsChecked = true;
        }
        finally { _activityUpdatingCategories = false; }
        UpdateActivitySessionMessages();
    }

    private void ActivityCategory_Click(object sender, RoutedEventArgs e)
    {
        if (_activityUpdatingCategories) return;
        _activityUpdatingCategories = true;
        try
        {
            if (sender is ToggleButton chip && !ReferenceEquals(chip, _activityAllCategoriesChip))
            {
                _activityAllCategoriesChip!.IsChecked = false;
                // Clearing the last category returns to All rather than to a filter that matches nothing.
                if (chip.IsChecked != true && _activityCategoryChips.All(item => item.IsChecked != true))
                    _activityAllCategoriesChip.IsChecked = true;
            }
            else if (ReferenceEquals(sender, _activityAllCategoriesChip))
            {
                if (_activityAllCategoriesChip!.IsChecked == true)
                    foreach (var item in _activityCategoryChips) item.IsChecked = false;
                else if (_activityCategoryChips.All(item => item.IsChecked != true))
                    _activityCategoryChipByValue[SyncActivityCategories.Files].IsChecked = true;
            }
        }
        finally { _activityUpdatingCategories = false; }
        UpdateActivitySessionMessages();
        if (_workspaceReady) _ = LoadActivityPageAsync(true);
    }

    private void SelectAllActivityCategories()
    {
        if (_activityAllCategoriesChip is null) return;
        _activityUpdatingCategories = true;
        try
        {
            foreach (var chip in _activityCategoryChips) chip.IsChecked = false;
            _activityAllCategoriesChip.IsChecked = true;
        }
        finally { _activityUpdatingCategories = false; }
        UpdateActivitySessionMessages();
    }

    // D3: no selected category means All, which is every category once each, not a filter.
    private IReadOnlyList<string> SelectedActivityCategories() =>
        _activityAllCategoriesChip?.IsChecked == true || _activityCategoryChips.Count == 0
            ? []
            : _activityCategoryChips.Where(chip => chip.IsChecked == true)
                .Select(chip => (string)chip.Tag!).ToArray();

    // UI07: severity, entity and run are independent of the category selection and of each other.
    private string CurrentActivitySeverity() => SelectedActivityValue(_activitySeverityBox, "All severities");
    private string CurrentActivityEntity() => SelectedActivityValue(_activityEntityBox, "All entities");
    private string CurrentActivityRun() => _activityRunBox?.Text.Trim() ?? string.Empty;

    private static string SelectedActivityValue(ComboBox? box, string allLabel) =>
        box?.SelectedItem is ComboBoxItem item && item.Content?.ToString() is { Length: > 0 } value &&
        !string.Equals(value, allLabel, StringComparison.Ordinal) ? value : string.Empty;

    // UI08 / 6.1: the Sync log reads as a chronology, oldest first, so one run's start, plan,
    // operations, warnings and terminal row appear in the order they happened. Every other
    // selection keeps the newest-first activity order.
    private bool ActivityReadsOldestFirst()
    {
        var selected = SelectedActivityCategories();
        return selected.Contains(SyncActivityCategories.Sync, StringComparer.Ordinal) &&
               !selected.Contains(SyncActivityCategories.Files, StringComparer.Ordinal);
    }

    // UI07: the live session messages are this app session's producer console. They belong to
    // Diagnostics, so they are revealed when Diagnostics or All is selected.
    private void UpdateActivitySessionMessages()
    {
        // The retained Operation log has an independent full-size tab; category chips
        // never hide it or substitute the transient producer console for its history.
    }

    // 6.1: an event the producer could not retain is named instead of being hidden.
    private SyncActivityRetentionWindow? _activityRetentionWindow;

    private string ActivityIncompleteNote() => _authorityHost.ActivityLogIncomplete
        ? "Log is incomplete. " + (_authorityHost.ActivityLogPendingCount > 0
            ? $"{_authorityHost.ActivityLogPendingCount:N0} events are waiting in memory to be saved; keep the app open. "
            : "Some events could not be retained. ") +
          _authorityHost.ActivityLogError : string.Empty;

    // UI07/UI09: the count describes only what was actually loaded and never invents a total the
    // query did not return. A failed load and an incomplete producer log are both stated.
    internal string ActivityCountText()
    {
        if (_activityPageLoads > 0)
            return "Loading retained activity for " + (_workspaceAccountId is null ? "All Accounts" : "the selected account") +
                   "… Existing events remain visible until the read finishes.";
        var gap = ActivityIncompleteNote();
        if (_activityLoadFailed)
            return "Activity could not be loaded" +
                   (_activityLoadFailure is { Length: > 0 } failure ? ": " + failure : string.Empty) +
                   ". Refresh to retry.";
        var loaded = _recentRows.Count == 0 ? "No matching events loaded" : $"{_recentRows.Count:N0} events loaded";
        var text = loaded + (_activityHasMore ? "; more available." : "; all retained matches loaded.");
        text += " " + (_activityRetentionWindow?.Describe() ?? "");
        return gap.Length == 0 ? text : text + " " + gap;
    }

    // UI09: Refresh reports real busy, completed and error feedback through its own note and never
    // through the shared navigation status. A response that arrives after a newer request, a filter
    // change or an account change is discarded, and the note then describes what actually reached
    // the list rather than what was requested.
    private async void RefreshActivity_Click(object sender, RoutedEventArgs e)
    {
        using var actionFeedback = ActionFeedback.Begin(sender);
        if (!actionFeedback.Entered) return;
        // The control is captured in a local: a field's nullable state cannot be relied on across an
        // await, and this handler must be able to re-enable exactly the button it disabled.
        var refresh = _activityRefresh;
        if (!_workspaceReady || refresh is null) return;
        if (_activityTabs.SelectedIndex == 3) { await LoadOperationLogAsync(false); return; }
        if (_activityRefreshing) return;               // one refresh at a time; it reports its own result
        // UI09: Refresh refreshes the list you are actually looking at. Only the unified list
        // carries the full busy, completed and error note; Pending and Runs keep their own existing
        // summaries and loaders, so no control claims to refresh something it does not touch.
        if (_activityTabs.SelectedIndex != 0)
        {
            var other = _activityTabs.SelectedIndex;
            refresh.IsEnabled = false;
            try
            {
                if (other == 1) await LoadQueuePageAsync(true); else await LoadRunHistoryAsync(true);
                SetActivityRefreshNote(other == 2 && _runLoadFailed ? "Run history refresh failed. The loaded rows are unchanged; retry Refresh." :
                    "Refreshed at " + DateTimeOffset.Now.ToString("HH:mm:ss") + ".", other == 2 && _runLoadFailed ? "ErrorBrush" : "SuccessBrush");
            }
            finally { refresh.IsEnabled = true; }
            return;
        }
        if (!_authorityHost.AuthorityStateExists)
        {
            SetActivityRefreshNote("There is no saved sync history to refresh yet. Refresh retries once a run has completed.", "MutedInk");
            return;
        }
        var appliedBefore = _activityLastAppliedQueryVersion;
        _activityRefreshing = true;
        refresh.IsEnabled = false;
        SetActivityRefreshNote("Refreshing the activity list…", "MutedInk");
        string? failure = null;
        try { await LoadActivityPageAsync(true); }
        catch (Exception exception) { failure = exception.Message; }
        _activityRefreshing = false;
        // Report what actually reached the list before the control becomes usable again, so the note
        // is never observed half-written. A response the loader discarded as stale is not reported as
        // applied.
        if (failure is not null)
            SetActivityRefreshNote("Refresh failed: " + failure +
                ". The rows already loaded are still shown; Refresh retries.", "ErrorBrush");
        else if (_activityLoadFailed)
            SetActivityRefreshNote("Refresh failed" +
                (_activityLoadFailure is { Length: > 0 } detail ? ": " + detail : string.Empty) +
                ". The rows already loaded are still shown; Refresh retries.", "ErrorBrush");
        else if (_activityLastAppliedQueryVersion == appliedBefore)
            SetActivityRefreshNote("Refresh finished at " + DateTimeOffset.Now.ToString("HH:mm:ss") +
                ", but a newer request replaced it before it could apply.", "MutedInk");
        else
            SetActivityRefreshNote("Refreshed at " + DateTimeOffset.Now.ToString("HH:mm:ss") + ".", "SuccessBrush");
        refresh.IsEnabled = true;
        LoadMoreActivityButton.IsEnabled = _activityHasMore;
    }

    private void SetActivityRefreshNote(string text, string brush)
    {
        if (_activityRefreshNote is null) return;
        _activityRefreshNote.Text = text;
        _activityRefreshNote.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    private async Task RefreshHomeHistoryAsync()
    {
        if (!_workspaceReady || !_authorityHost.AuthorityStateExists) return;
        var page = await LoadSummaryHistoryAsync(null);
        _homeHistory.Clear();
        foreach (var entry in page.Where(entry => entry.Outcome is not ("Identical" or "Unchanged")))
            _homeHistory.Add(RetainedActivityRow(entry));
        RefreshWorkspaceFeeds();
        if (_workspaceAccountId is not null) await RefreshWorkspaceScopeDataAsync();
    }

    private void TrackWorkspaceAction(ActivityRow row)
    {
        if (row.IsRoutineCheck) return;
        // A run summary repeats a just-completed lifecycle event. Keep both in the
        // full event log, but do not add a second unbound card to the recent feed.
        if (row.OperationId is null && _workspaceRunStarted is { } started &&
            _homeHistory.Any(item => item.OperationId is not null && item.PairId == row.PairId &&
                item.Path == row.Path && item.HumanAction == row.HumanAction &&
                DateTimeOffset.TryParse(item.When, out var occurred) &&
                occurred.ToUnixTimeSeconds() >= started.ToUnixTimeSeconds())) return;
        _homeHistory.Insert(0, row);
        // Keep a bounded independent file slice so frequent diagnostics do not erase
        // every useful file row from Home and the tray between retained refreshes.
        var retained = _homeHistory.Take(40).Concat(_homeHistory.Where(item => item.IsFileActivity).Take(40))
            .DistinctBy(item => item.EventKey, StringComparer.Ordinal).ToArray();
        _homeHistory.Clear(); _homeHistory.AddRange(retained);
        QueueWorkspaceRefresh();
    }

    private async Task<IReadOnlyList<SyncAuthorityActivityProjection>> LoadSummaryHistoryAsync(string? accountId)
    {
        if (SummaryHistoryLoaderForTest is { } loader) return await loader(accountId);
        // SQLite's async facade can perform the projection synchronously. Keep the
        // sequential reads off the dispatcher; presentation resumes on its context.
        IReadOnlyList<SyncAuthorityActivityProjection> recent = [], files = [];
        var scope = accountId is null ? "All Accounts" : "the selected account";
        await RunStartupProjectionWorkAsync($"Reading the 40 most recent changes for {scope}.", async () =>
            recent = await Task.Run(() => _authorityHost.LoadActivityProjectionPageAsync(40, 0, null, "Changes", "All types", connectionId: accountId)));
        await RunStartupProjectionWorkAsync($"Reading the 40 most recent File activity rows for {scope}; category filtering precedes pagination.", async () =>
            files = await Task.Run(() => _authorityHost.LoadActivityProjectionPageAsync(40, 0, null, "Changes", "All types", connectionId: accountId,
                query: new SyncActivityQuery([SyncActivityCategories.Files]))));
        return recent.Concat(files).DistinctBy(item => item.EventId ??
                string.Join("|", item.OperationId, item.OccurredUtc.ToString("O"), item.PairId,
                    item.RelativePath, item.Outcome, item.Reason), StringComparer.Ordinal)
            .OrderByDescending(item => item.OccurredUtc).ToArray();
    }

    private void RefreshWorkspace()
    {
        if (!_workspaceReady) return;
        // SS-D12: one presentation scope for this whole refresh. Every account, label, pause and attention
        // lookup below reads the one snapshot this scope established, and nested rendering joins it instead
        // of reading the stores per pair. The scope and its once-per-pass attention result are disposed here.
        using var presentation = OpenPresentationScope();
        var attentionCases = PresentationAttentionCases;
        _ = CheckPauseDeadlineAsync();
        RefreshExplorerRootStates();
        var scopePairs = _pairSet.Pairs.Count(pair => ScopeMatches(pair.Id));
        var runningHere = _runActive && (_readingRunOutcomes || ScopeMatches(_workspaceRunningPairId));
        var accountPaused = _workspaceAccountId is not null && PresentationAccountPaused(_workspaceAccountId);
        var pending = _workspaceAccountId is null ? _globalPendingQueueCount : _workspaceScopePending;
        var scopedProblemDetail = AttentionProblemDetail(scoped: true);
        var commandFailure = CommandFailureDetail(scoped: true);
        var readbackFailure = ReadbackFailureDetail(scoped: true);
        PhaseText.Text = _workspaceAccountId is null ? (_runActive && !GlobalPauseActive ? TrayProgressDetails : WorkspacePhaseDetail)
            : runningHere ? TrayProgressDetails : accountPaused ? "This account is paused. Other accounts can continue syncing."
            : _runActive ? "Another account is syncing. This account's history and pending work are shown below."
            : readbackFailure ?? commandFailure ?? scopedProblemDetail ?? "Showing this account's folder pairs and activity.";
        // Keep an action acknowledgement in its original account, but never let
        // it hide the current process/file and timing of an active scoped run.
        if (_workspaceActionFeedback is { } feedback && feedback.UntilUtc > DateTimeOffset.UtcNow &&
            string.Equals(feedback.AccountId, _workspaceAccountId, StringComparison.Ordinal))
            PhaseText.Text = runningHere &&
                !PhaseText.Text.Contains(feedback.Text, StringComparison.Ordinal)
                    ? feedback.Text + Environment.NewLine + PhaseText.Text
                    : runningHere ? PhaseText.Text : feedback.Text;
        else
            _workspaceActionFeedback = null;
        // Never shorten active file/process progress. Only terminal overview
        // prose is abbreviated; the exact retained problem and run links remain.
        if (!runningHere && (readbackFailure is not null || commandFailure is not null || scopedProblemDetail is not null))
            PhaseText.Text = ProblemPreview(PhaseText.Text);
        _homePairLink.Content = Text(_workspaceRunningPair ?? "", 13, brush: "FocusBrush");
        _homePairLink.Visibility = runningHere && _workspaceRunningPair is not null ? Visibility.Visible : Visibility.Collapsed;
        _homeTitle.Text = _workspaceAccountId is null ? GlobalWorkspaceStatus :
            accountPaused || GlobalPauseActive ? "Sync Paused" : runningHere ? _workspaceStage :
            readbackFailure is not null ? "Results Could Not Be Refreshed" : commandFailure is not null ? "Needs Attention" :
            attentionCases.Any(item => ScopeMatches(item.Pair.Id)) ? "Needs Attention" : pending > 0 ? "Work Waiting to Finish" :
            scopePairs == 0 ? "Choose Folders to Sync" : _workspaceScopeStatus is { HasProtectedCredential: false } ? "Reconnect Account" : "Ready to Check";
        var attentionHere = readbackFailure is not null || commandFailure is not null || attentionCases.Any(item => ScopeMatches(item.Pair.Id));
        // Current work and retained attention are separate dimensions. A working retry
        // stays blue without erasing the prior problem; terminal attention stays red.
        PhaseText.Visibility = attentionHere && readbackFailure is null && commandFailure is null && scopedProblemDetail is null && !_runActive && _workspaceActionFeedback is null &&
            !GlobalPauseActive && !accountPaused ? Visibility.Collapsed : Visibility.Visible;
        var pausedState = accountPaused || GlobalPauseActive || (_workspaceAccountId is null && TrayPaused);
        _homeStateGlyph.IconName = runningHere ? "sync" : pausedState ? "pause" : attentionHere ? "attention" :
            _homeTitle.Text == "Up to Date" ? "cloud-check" : "cloud";
        _homeStateGlyph.SetResourceReference(Control.ForegroundProperty,
            pausedState ? "WarningBrush" : runningHere ? "FocusBrush" :
            attentionHere ? "ErrorBrush" : _homeTitle.Text == "Up to Date" ? "SuccessBrush" : "FocusBrush");
        _homeStateAction.Visibility = !runningHere && (attentionHere || pending > 0 || scopePairs == 0 ||
            _homeTitle.Text.Contains("Account", StringComparison.Ordinal) || _homeTitle.Text.Contains("Connect", StringComparison.Ordinal))
            ? Visibility.Visible : Visibility.Collapsed;
        var attentionCount = attentionCases.Count(item => ScopeMatches(item.Pair.Id));
        var stateActionLabel = commandFailure is not null ? "View command failure" : readbackFailure is not null ? "View result refresh error" : attentionHere ? $"Review {attentionCount} {(attentionCount == 1 ? "issue" : "issues")}" : pending > 0 ? "View pending operations" :
            scopePairs == 0 ? "Add a folder pair" : "Manage connection";
        _homeStateAction.Content = stateActionLabel;
        _homeStateAction.ToolTip = commandFailure is not null
            ? "Open the operation log. Accepted commands retain their exact run links and failure causes; completed file outcomes are unchanged."
            : readbackFailure is not null ? "Inspect the recorded display error. Refresh Activity or Runs; do not replay successful files because a display refresh failed."
            : attentionHere ? "Open every current folder, account or first-sync issue in this account's scope."
            : pending > 0 ? "Inspect unfinished operations and their retained reasons before retrying."
            : scopePairs == 0 ? "Choose two folders and review the proposed first-sync changes."
            : "Inspect this account's connection status and available recovery actions.";
        System.Windows.Automation.AutomationProperties.SetName(_homeStateAction, stateActionLabel);
        var reconnectAccount = _workspaceAccountId is not null && _workspaceScopeStatus is { HasProtectedCredential: false };
        RunButton.Content = CommandIcons.Label(reconnectAccount ? "Reconnect Account" : _workspaceAccountId is null ? "Sync All" : "Sync Account", "sync");
        RunButton.ToolTip = _workspaceAccountId is null ? "Sync all enabled folder pairs across accounts." : "Sync enabled folder pairs in " + WorkspaceScopeLabel;
        RunButton.Visibility = _runActive ? Visibility.Collapsed : Visibility.Visible;
        var scopedPause = _workspaceAccountId is not null && !GlobalPauseActive;
        var pausedHere = scopedPause ? accountPaused : TrayPaused;
        PauseButton.Visibility = scopePairs > 0 || pausedHere ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.IsEnabled = (scopePairs > 0 || pausedHere) && !_readingRunOutcomes;
        ActionAvailability.Refresh(PauseButton);
        PauseButton.Content = CommandIcons.Label(pausedHere ? (scopedPause ? "Resume Account" : "Resume Sync") :
            scopedPause ? "Pause Account" : "Pause Sync", pausedHere ? "play" : "pause");
        PauseButton.ToolTip = scopedPause ? "Change pause settings for this account only." :
            TrayPaused ? PauseDescription : "Pause all accounts for a set time or until resumed";
        RunProgress.Visibility = runningHere ? Visibility.Visible : Visibility.Collapsed;
        var pendingLabel = runningHere ? "View current sync activity" : pending is null ? "Loading Pending Operations…" : pending == 0 ? "No Pending Operations" : $"{pending:N0} Pending Operations";
        _homePending.Content = CommandIcons.Label(pendingLabel, "activity");
        _homePending.Visibility = runningHere || pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(_homePending, pendingLabel);
        _homeTiming.Text = runningHere && _workspaceRunStarted is { } started
            ? $"Run Started: {started.ToLocalTime():HH:mm:ss}    Elapsed: {(DateTimeOffset.Now - started):hh\\:mm\\:ss}"
            : ScopeLastSuccessfulSyncUtc is { } completed
                ? "Last successful sync: " + completed.ToLocalTime().ToString("g") : "Last successful sync: Not recorded";
        _homeConnection.Text = _workspaceAccountId is not null ? WorkspaceScopeLabel + "\n" + (_workspaceScopeStatus?.Health ?? "Loading connection status…") :
            _accountLabels.Count > 1 ? $"{_accountLabels.Count} Accounts\nManage Each Account's Connection and Storage" :
            ConnectionLabel + "\n" + (_connectionStatus?.Health ?? "Checking connection…");
        string lastSummary;
        if (_workspaceRuns.FirstOrDefault(item => ScopeMatches(item.Pair.Id)) is { } run)
        {
            // SS-D02: the same classified outcome the tray notification and the Runs surface use. A
            // stopped, unverified or partial run states that here instead of borrowing "Finished".
            var latestOutcome = SyncRunOutcomeProjection.FromResult(run.Result);
            lastSummary = "Pair: " + run.Pair.Name + "\nCompleted: " + run.Completed.ToLocalTime().ToString("g") +
                "\n\nStatus: " + latestOutcome.StatusWord + "\n" + string.Join("\n", latestOutcome.CountLines);
        }
        else if (_retainedRuns.FirstOrDefault(run => run.CompletedUtc is not null && ScopeMatchesRun(run)) is { } retained)
            lastSummary = "Pair: " + RetainedRunPairNames(retained) + "\n" + RetainedRunSummary(retained);
        else lastSummary = ScopeLastSuccessfulSyncUtc is { } last
            ? "Completed: " + last.ToLocalTime().ToString("g") + "\nPair: Not recorded\nCounts: Not recorded\n\nEarlier item history is in Activity."
            : "No completed sync yet.";
        _homeLastSync.Content = SharedPresentation.LabelledSummary(lastSummary);
        UpdateHomeResponsiveLayout();
        RefreshFullApp();
        WorkspacePresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateHomeResponsiveLayout()
    {
        // Hidden pages measure at zero. Retain their layout until a visible size
        // is available, and apply the next real size before the first paint.
        if (!_workspaceReady || !DashboardPage.IsVisible || DashboardPage.ActualWidth <= 0) return;
        var narrow = DashboardPage.ActualWidth < 800;
        _homeColumns.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 300);
        Grid.SetColumn(_homeSupport, narrow ? 0 : 1); Grid.SetRow(_homeSupport, narrow ? 1 : 0);
        _homeSupport.Orientation = narrow ? Orientation.Horizontal : Orientation.Vertical;
        _homeSupport.Margin = narrow ? new Thickness(0, 18, 0, 0) : new Thickness(0);
    }

    private void HomeStateAction_Click(object sender, RoutedEventArgs e)
    {
        if (CommandFailureDetail(scoped: true) is not null)
        {
            // Only this attempt's actual retained failure supplies a run link.
            // A pre-admission error must not borrow an older accepted command.
            var retained = _commandProblemsByPair.Values
                .Where(problem => ScopeMatches(problem.PairId) &&
                    _workspaceCommandFailure!.Value.PairIds.Contains(problem.PairId) &&
                    _workspaceRunStarted is { } started && problem.ReportedUtc >= started)
                .OrderByDescending(problem => problem.ReportedUtc).FirstOrDefault();
            if (retained is null) OpenOperationLog(null);
            else OpenRetainedRunLog(retained.PairId, retained.CommandId);
            return;
        }
        if (_workspaceReadbackFailure is not null) { OpenOperationLog(null); return; }
        var cases = PresentationAttentionCases.Where(item => ScopeMatches(item.Pair.Id)).ToArray();
        if (cases.Length == 1) { cases[0].Open(); return; }
        if (cases.Length > 1) { OpenAllIssuesOverview(); return; }
        if ((_workspaceAccountId is null ? _globalPendingQueueCount : _workspaceScopePending) > 0)
        { OpenScopedActivity(1); return; }
        if (!_pairSet.Pairs.Any(pair => ScopeMatches(pair.Id)))
        { NavigateTo("Pairs"); NewPair_Click(sender, e); return; }
        NavigateTo("Connections");
    }

    // UI3: the aggregate "Review N issues" entry always lands on the All subtab for the current account. A
    // previously selected subtab - typically First Sync - is never preserved for aggregate navigation, so every
    // current-account case, a first-sync review or an unavailable local root included, stays reachable from the
    // same overview. A single case still opens that exact case through its own action.
    private void OpenAllIssuesOverview()
    {
        if (_fullAppReady && _attentionTabs is not null && _attentionTabs.SelectedIndex != 0)
            _attentionTabs.SelectedIndex = 0;
        NavigateTo("Attention");
        _attentionFingerprint = string.Empty;
        RefreshFullApp();
    }

    private string RetainedRunPairNames(SyncAuthorityRunHistory run)
    {
        try
        {
            using var request = System.Text.Json.JsonDocument.Parse(run.RequestJson);
            if (request.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !request.RootElement.TryGetProperty("pairIds", out var ids) || ids.ValueKind != System.Text.Json.JsonValueKind.Array) return "Not recorded";
            var names = ids.EnumerateArray().Where(item => item.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(item => item.GetString()).Where(id => id is not null)
                .Select(id => _pairSet.Pairs.FirstOrDefault(pair => pair.Id == id)?.Name ?? id).ToArray();
            return names.Length == 0 ? "Not recorded" : string.Join(", ", names);
        }
        catch (System.Text.Json.JsonException) { return "Not recorded"; }
    }

    internal void OpenRunningPair()
    {
        var pair = PairRows.FirstOrDefault(item => item.Id == _workspaceRunningPairId);
        if (pair is null) return;
        ShowFromBackground();
        OpenPairsForAccount(_pairSet.Pairs.FirstOrDefault(item => item.Id == pair.Id)?.ConnectionId);
        PairList.SelectedItem = pair; ExpandPairRow(pair);
    }

    internal void OpenWorkspaceActivity(int tab, ActivityRow? row = null)
    {
        // An explicit tray/history item may belong to a different account from the main window.
        // Bring its account into scope before presenting its details under that header.
        if (row is not null && !row.MatchesAccount(_workspaceAccountId))
        {
            // A two-account or historical unknown event has no arbitrary primary account.
            ApplyAccountScopeTransition(null);
            RefreshWorkspaceAccountPickers(); RefreshAttentionPairChoices(); RefreshActivityAccountScope();
        }
        ShowFromBackground(); NavigateTo("Activity");
        // UI07: the separate Event Log tab is removed. Its former entry point, and any other stale
        // index, opens the one unified list showing every category with the live session messages
        // revealed, instead of selecting a tab that no longer exists.
        if (tab < 0 || tab >= _activityTabs.Items.Count)
        {
            _activityTabs.SelectedIndex = 0;
            SelectAllActivityCategories();
            _ = LoadActivityPageAsync(true);
        }
        else _activityTabs.SelectedIndex = tab;
        if (row is not null) { InspectActivity(row); _ = RefreshVisibleActivityAsync(); }
        if (tab == 1) { QueueSearchBox.Text = string.Empty; QueueFilterBox.SelectedIndex = 0; _ = LoadQueuePageAsync(true); }
    }

    internal void OpenWorkspaceConnection() { ShowFromBackground(); NavigateTo("Connections"); }

    // ENG4: the exact retained run behind a first-sync problem, reached from Needs Attention and from the First
    // sync page. The Activity run filter names that exact command, so the reason and the outcome agree.
    internal void OpenFirstSyncProblemRun(SyncFirstSyncProblem problem)
        => OpenRetainedRunLog(problem.PairId, problem.CommandId);

    // First-sync and ordinary command failures share exact-run navigation and
    // filter reset. Neither route approves a review, pauses sync or retries files.
    internal void OpenRetainedRunLog(string pairId, string commandId)
    {
        // R474-4: an explicit problem drilldown resets every incompatible activity filter, so a previously selected
        // category, search, action, type, date, severity or entity filter cannot hide the exact failure event. No
        // pause setting is touched and ordinary user filters are left alone elsewhere.
        SelectAllActivityCategories();
        if (ActivitySearchBox is not null) ActivitySearchBox.Text = string.Empty;
        if (ActivityFilterBox is not null) ActivityFilterBox.SelectedIndex = 0;
        if (ActivityTypeFilter is not null) ActivityTypeFilter.SelectedIndex = 0;
        if (ActivityDateBox is not null) ActivityDateBox.SelectedIndex = 0;
        if (_activitySeverityBox is not null) _activitySeverityBox.SelectedIndex = 0;
        if (_activityEntityBox is not null) _activityEntityBox.SelectedIndex = 0;
        if (_activityRunBox is not null) _activityRunBox.Text = commandId;
        IncludeAttentionPair(pairId);
        if (ActivityPairBox is not null)
            ActivityPairBox.SelectedItem = ActivityPairChoices.FirstOrDefault(item => item.Id == pairId) ??
                                           ActivityPairChoices.FirstOrDefault();
        ShowFromBackground();
        OpenOperationLog(commandId);
    }
    internal void OpenWorkspaceAttention() { ShowFromBackground(); NavigateTo("Attention"); }
    internal async Task ToggleWorkspacePauseAsync()
    {
        // Explicit operations only; a pause is never derived from the inverse of a display flag.
        if (TrayPaused) { await ResumePausedAsync(null); return; }
        await SetPausedAsync(PauseOption.Indefinitely, null);
    }

    private void InspectActivity(ActivityRow row)
    {
        if (!_workspaceReady) return;
        _inspectedActivity = row;
        if (ActivityPage.Visibility != Visibility.Visible) NavigateTo("Activity");
        _workspaceDetailTitle.Text = row.DisplayName;
        var pair = _pairSet.Pairs.FirstOrDefault(value => value.Id == row.PairId);
        _workspacePairAction.IsEnabled = pair is not null;
        _workspaceRecoveryAction.IsEnabled = pair is not null && IsRecoveryActivity(row);
        DetailsText.Text = ActivityDetails(row);
        RefreshStructuredInspector(row);
        _detailsHost?.Present(row);
        TechnicalText.Text = $"pair_id={row.PairId}\noperation_id={row.OperationId ?? "Not an operation"}\nraw_outcome={row.Outcome}\nconnection={row.Connection}";
        _workspaceInspector.Visibility = Visibility.Visible;
        _workspaceBody.ColumnDefinitions[1].Width = new GridLength(Math.Min(340, Math.Max(260, ActualWidth * .27)));
    }

    private static ActivityRow QueueActivity(QueueRow row) => new(row.Enqueued, row.Path,
        row.StateLabel, $"Action: {row.Operation}\nStatus: {row.StateLabel}\nRecorded State: {row.State}\nSync Order: {row.Priority}", row.Connection,
        row.PairId, row.Pair, row.OperationId, row.ItemKind, row.FileType);
    private void InspectQueue(QueueRow row) => InspectActivity(QueueActivity(row));

    private void CloseWorkspaceDetails()
    {
        _detailsHost?.Collapse();
        _workspaceInspector.Visibility = Visibility.Collapsed;
        _workspaceBody.ColumnDefinitions[1].Width = new GridLength(0);
    }

    private void ShowWorkspaceRun(WorkspaceRun run)
    {
        _allRows.Clear();
        _allRows.AddRange(run.Result.Decisions.Select(decision => new DecisionRow(run.Pair.Id, run.Pair.Name,
            decision.RelativePath, decision.Kind.ToString(), decision.Reason, LabelForPair(run.Pair))));
        ApplyFilter();
        SummaryText.Text = run.Label + $"\nUploaded: {run.Result.Uploaded}\nDownloaded: {run.Result.Downloaded}\nUnchanged: {run.Result.Identical}\nNeeds Attention: {run.Result.Blocked + run.Result.Conflicts}";
    }

    private void WorkspaceBack_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceBackStack.Count == 0) return;
        var page = _workspaceBackStack.Pop();
        _workspacePage = page;
        NavigateTo(page);
    }

    // Per-page Simple/Detailed persistence uses the existing preferences store. Unknown or
    // missing page entries fall back to the Simple compact presentation.
    internal void LoadViewModes()
    {
        _viewModes.Clear();
        foreach (var item in _workspacePreferences.PageViewModes ?? new Dictionary<string, string>())
            if (string.Equals(item.Value, UiPreferencesStore.DetailedView, StringComparison.Ordinal))
                _viewModes[item.Key] = true;
        ApplyViewModes();
        SyncActivityViewSwitch();
    }

    internal bool IsDetailedView(string pageKey) => _viewModes.TryGetValue(pageKey, out var detailed) && detailed;

    private static string? ViewModePage(int tabIndex) => tabIndex switch
        { 0 => "activity", 1 => "pending", 2 => RunsViewKey, 3 => OperationLogViewKey, _ => null };

    internal void ApplyViewModes()
    {
        SharedPresentation.ApplyFeedDetail(ActivityGrid, IsDetailedView("activity"), ActivityDetailColumns);
        SharedPresentation.ApplyFeedDetail(_pendingGrid, IsDetailedView("pending"), PendingDetailColumns);
        ApplyRecoveryViewMode();
        if (_sharingRows is not null)
            SharedPresentation.ApplyFeedDetail(_sharingRows, IsDetailedView("sharing"), SharingDetailColumns);
        _sharingViewSwitch?.SetView(IsDetailedView("sharing"), persist: false);
        if (_storageRows is not null)
            SharedPresentation.ApplyFeedDetail(_storageRows, IsDetailedView("storage"), StorageDetailColumns);
        _storageViewSwitch?.SetView(IsDetailedView("storage"), persist: false);
        // UI11: the Runs surface keeps its own per-page mode under the same preferences mechanism.
        ApplyRunViewMode();
        ApplyOperationLogViewMode();
    }

    // UI16: the recovery page is built after the first preference load, so its mode is applied once
    // the recovery grid exists and again on every later switch or preference change.
    internal void ApplyRecoveryViewMode()
    {
        if (!_recoveryPresentationReady) return;
        SharedPresentation.ApplyFeedDetail(RecoveryGrid, IsDetailedView("recovery"), RecoveryDetailColumns,
            SharedPresentation.CompactRowHeight, SharedPresentation.DetailedRowHeight);
    }

    internal async Task SetDetailedViewAsync(string pageKey, bool detailed)
    {
        _viewModes[pageKey] = detailed;
        if (pageKey is "activity" or "pending" or "recovery" or RunsViewKey or OperationLogViewKey) ApplyViewModes();
        await _workspacePreferencesGate.WaitAsync();
        try
        {
            var current = _workspacePreferencesStore.Load();
            var modes = (current.PageViewModes ?? new Dictionary<string, string>())
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            modes[pageKey] = detailed ? UiPreferencesStore.DetailedView : UiPreferencesStore.SimpleView;
            var next = current with { PageViewModes = modes };
            await _workspacePreferencesStore.SaveAsync(next);
            _workspacePreferences = next;
        }
        catch (Exception error) { ShowStatus("The row detail preference was not saved: " + error.Message); }
        finally { _workspacePreferencesGate.Release(); }
    }

    private void ActivityViewChanged(SimpleDetailedSwitch control)
    {
        if (_activityTabs is null || ViewModePage(_activityTabs.SelectedIndex) is not { } pageKey) return;
        _ = SetDetailedViewAsync(pageKey, control.IsDetailed);
    }

    private void RecoveryViewChanged(SimpleDetailedSwitch control) =>
        _ = SetDetailedViewAsync("recovery", control.IsDetailed);

    private void SyncActivityViewSwitch()
    {
        var control = _activityViewSwitch;
        if (control is null || _activityTabs is null) return;
        var pageKey = ViewModePage(_activityTabs.SelectedIndex);
        control.Visibility = pageKey is null ? Visibility.Collapsed : Visibility.Visible;
        if (pageKey is not null)
        {
            control.PageKey = pageKey;
            control.SetView(IsDetailedView(pageKey), persist: false);
        }
    }

    private void SubmitActivitySearch()
    {
        if (!_fullAppReady || !_authorityHost.AuthorityStateExists) return;
        if (_activityTabs.SelectedIndex == 1) _ = LoadQueuePageAsync(true);
        else if (_activityTabs.SelectedIndex == 0) _ = LoadActivityPageAsync(true);
    }

    private sealed record WorkspaceRun(SyncPair Pair, SyncRunResult Result, DateTimeOffset Completed)
    {
        public string Label => Pair.Name + "\nCompleted: " + Completed.ToLocalTime().ToString("g");
    }

    /// <summary>
    /// SS-D02: the newest run's classified outcome with the completion identity the tray de-duplicates
    /// on. The tray states the outcome; the identity only decides whether this exact run was already
    /// announced, so a timestamp can never substitute for the classified result.
    /// </summary>
    internal sealed record TrayLatestRun(string Identity, DateTimeOffset CompletedUtc, string PairName,
        SyncRunOutcomeProjection Outcome);
}
