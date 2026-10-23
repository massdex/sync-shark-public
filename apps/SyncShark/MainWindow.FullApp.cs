using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Control = System.Windows.Controls.Control;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using Binding = System.Windows.Data.Binding;
using Brushes = System.Windows.Media.Brushes;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using TabControl = System.Windows.Controls.TabControl;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private ListBox? _utilityNavigation;
    private Border? _attentionPage;
    private TabControl? _attentionTabs;
    private StackPanel? _attentionList;
    private StackPanel? _attentionProblems;
    private Grid? _attentionProblemSlot;
    private TextBlock? _attentionAllSummary;
    private TextBlock? _attentionProblemSummary;
    private Popup? _attentionGuidancePopup;
    // One fixed summary row per subtab keeps every subtab's filter, table or list at the same
    // origin, so switching subtabs cannot move rows the user is reading.
    private const double AttentionSummaryRowHeight = 26;
    private StackPanel? _detailFields;
    private TextBlock? _detailSubtitle;
    private string _attentionFingerprint = string.Empty;
    private bool _fullAppReady;
    private StackPanel? _homeAttention;
    private readonly Dictionary<TabItem, Grid> _activityContents = new();
    private TabControl? _recoveryTabs;
    private UiPreferencesStore _workspacePreferencesStore = null!;
    private UiPreferences _workspacePreferences = new(UiPreferencesStore.SystemTheme);
    private readonly SemaphoreSlim _workspacePreferencesGate = new(1, 1);
    private TextBlock? _storageSummary;
    private Button? _inspectorLocal, _inspectorCloud, _inspectorLink;
    private ShellTypeImage? _inspectorTypeIcon;

    private static FrameworkElement SettingRow(string title, string description, UIElement value)
    {
        var grid = new Grid { Margin = new Thickness(0, 16, 0, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        words.Children.Add(Text(title, 14, true)); var sub = Text(description, 12, brush: "MutedInk"); sub.Margin = new Thickness(0, 5, 0, 0); words.Children.Add(sub);
        grid.Children.Add(words); Grid.SetColumn(value, 1); grid.Children.Add(value);
        var line = new Border { Child = grid, BorderThickness = new Thickness(0, 0, 0, 1) };
        line.SetResourceReference(Border.BorderBrushProperty, "Divider"); return line;
    }

    private static UIElement FormPage(StackPanel form) => new ScrollViewer
    { Content = Card(form), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    private void BuildConnectionPage()
    {
        var body = new StackPanel();
        body.Children.Add(BuildAccountPicker());
        var heading = new DockPanel();
        var check = AsyncButton("Refresh account", async (_, _) => await RefreshConnectionStateAsync());
        check.ToolTip = "Verify saved access with the provider and refresh account details and storage usage. Does not sync files.";
        check.Margin = new Thickness(18, 0, 0, 0); DockPanel.SetDock(check, Dock.Right); heading.Children.Add(check);
        // The real provider mark replaces the generic outline. RefreshAccountPickers binds the Dropbox
        // or Google Drive template from the selected connection's own provider and composes the label.
        var providerIcon = new ContentControl
        {
            Width = 28, Height = 28, Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            Content = CommandIcons.Icon("cloud", 28)
        };
        System.Windows.Automation.AutomationProperties.SetName(providerIcon, "Account provider");
        _connectionProviderIcon = providerIcon;
        heading.Children.Add(providerIcon);
        var identity = new StackPanel(); identity.Children.Add(Detach(ConnectionNameText)); ConnectionNameText.FontSize = 18;
        identity.Children.Add(Detach(ConnectionEmailText)); heading.Children.Add(identity); body.Children.Add(heading);
        body.Children.Add(SettingRow("Connection Status", "Account access", Detach(ConnectionHealthText)));
        body.Children.Add(SettingRow("Last Checked", "Folder availability is checked separately.", Detach(ConnectionVerifiedText)));
        // One clickable linked-pair count with its own icon replaces the old count plus the separate
        // View Pairs action. It opens Folder Pairs filtered to this account's exact connection id.
        var pairsLink = Button("Linked Folder Pairs", (_, _) => OpenPairsForAccount(), quiet: true);
        var pairsContent = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        var pairsIcon = CommandIcons.Icon("folder", 16); pairsIcon.Margin = new Thickness(0, 0, 8, 0);
        pairsContent.Children.Add(pairsIcon);
        ConnectionPairsText.Margin = new Thickness(0);
        ConnectionPairsText.VerticalAlignment = VerticalAlignment.Center;
        pairsContent.Children.Add(Detach(ConnectionPairsText));
        ConnectionPairsText.SetResourceReference(TextBlock.ForegroundProperty, "FocusBrush");
        pairsLink.Content = pairsContent;
        pairsLink.Padding = new Thickness(0);
        pairsLink.HorizontalAlignment = HorizontalAlignment.Left;
        pairsLink.ToolTip = "Folder Pairs linked to this account. Opens Folder Pairs filtered to this account.";
        _connectionPairsLink = pairsLink;
        body.Children.Add(SettingRow("Folder Pairs", "Pairs linked to this account", pairsLink));
        _storageSummary = Text("Storage usage will appear after the account check.", 13, brush: "MutedInk");
        body.Children.Add(SettingRow("Storage", "Selected account capacity", _storageSummary));
        _sharedDrivesToggle = new System.Windows.Controls.CheckBox {
            Style = (Style)FindResource("PairToggle"), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Allow shared drives for this account. This does not grant Google permissions or start syncing. Turning it off prevents existing shared-drive pairs from syncing until re-enabled."
        };
        System.Windows.Automation.AutomationProperties.SetName(_sharedDrivesToggle, "Enable shared drives for this Google account");
        _sharedDrivesToggle.Click += async (_, _) => await SetSharedDrivesFromUiAsync();
        _sharedDrivesRow = (FrameworkElement)SettingRow("Shared drives", "Include shared drives this account can access when choosing folders.", _sharedDrivesToggle);
        body.Children.Add(_sharedDrivesRow);
        foreach (var (label, value) in new (string, FrameworkElement)[]
        { ("Provider",ConnectionProviderText),("Plan",ConnectionTypeText),("Access",ConnectionAccessText),
          ("Last Successful Sync",ConnectionLastSyncText),("Connection ID",ConnectionIdText) })
            body.Children.Add(SettingRow(label, "", Detach(value)));
        var actions = new WrapPanel();
        _accountPauseButton = AsyncButton("Pause Account", async (_, _) =>
        {
            if (_selectedConnectionId is { } account && _authorityHost.IsAccountPaused(account))
                await SetAccountPauseAsync(account, null, false);
            else OpenAccountPauseMenu(_accountPauseButton!);
        });
        _accountPauseButton.Margin = new Thickness(0, 0, 8, 0); actions.Children.Add(_accountPauseButton);
        foreach (var control in new Button[] { ConnectionConnectButton, ConnectionReauthorizeButton, ConnectionManageButton, ConnectionDisconnectButton })
        { actions.Children.Add(Detach(control)); control.Margin = new Thickness(0, 0, 8, 0); }
        body.Children.Add(actions);
        ConnectionsPage.Child = PageShell("Connections", "Connected accounts and available storage", FormPage(body),
            Detach(_accountPicker!), [Detach(_addAccountButton!)]);
        UpdateConnectionIdentity();
    }

    // One existing collector supplies the on-page results, the copy action and this preview, so
    // the redacted report can never disagree with what the Diagnostics page shows.
    private string DiagnosticSummary() => BuildDiagnosticReportText();

    // Existing owner test-seam pattern: both are unset in production, so the real confirmation
    // dialog and the real SaveFileDialog remain the only production path. They let a fixture prove
    // the confirmed save and the cancelled no-effect path without opening a modal dialog.
    internal Func<bool>? DiagnosticPreviewConfirmForTest { get; set; }
    internal Func<string?>? DiagnosticSavePathForTest { get; set; }

    // Preview first, then save only to a path the user chooses. Cancelling either step writes
    // nothing, and the report is never uploaded.
    private void PreviewDiagnosticReport()
    {
        var report = DiagnosticSummary();
        var confirmed = DiagnosticPreviewConfirmForTest?.Invoke() ?? AppDialogWindow.Confirm(this, "Diagnostic Report",
            "Review the local, redacted report before saving it", report, "Save Report\u2026");
        if (!confirmed) return;
        string? destination;
        if (DiagnosticSavePathForTest is not null) destination = DiagnosticSavePathForTest();
        else
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save the Local Diagnostic Report", Filter = "Text report|*.txt", FileName = "SyncShark-diagnostics.txt" };
            destination = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        if (destination is null) return;
        try { File.WriteAllText(destination, report); ShowStatus("Diagnostic report saved. Nothing was uploaded."); }
        catch (Exception e) { ShowStatus("Diagnostic report was not saved: " + e.Message); }
    }

    private static Grid PageShell(string title, string subtitle, UIElement content) =>
        PageShell(title, subtitle, content, null, []);

    // One shared compact header for every page: title left, account selector and primary
    // actions on the same row while they fit, wrapping intentionally at the minimum width.
    // The former decorative subtitle line is gone; its text remains available as help text.
    internal static Grid PageShell(string title, string subtitle, UIElement content, UIElement? accountSelector,
        IReadOnlyList<UIElement> primaryActions)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = SharedPresentation.PageHeader(title, accountSelector, primaryActions.ToArray());
        if (subtitle.Length > 0) System.Windows.Automation.AutomationProperties.SetHelpText(header, subtitle);
        grid.Children.Add(header); Grid.SetRow(content, 1); grid.Children.Add(content); return grid;
    }

    private static DataTemplate RowTemplate(string body) => (DataTemplate)System.Windows.Markup.XamlReader.Parse(
        "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='clr-namespace:Massdex.SyncShark;assembly=SyncShark'>" + body + "</DataTemplate>");

    private void ModernizePairPage()
    {
        PairList.HeadersVisibility = DataGridHeadersVisibility.None;
        PairList.RowHeight = 116; PairList.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        PairList.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Divider");
        ScrollViewer.SetHorizontalScrollBarVisibility(PairList, ScrollBarVisibility.Auto);
        var expand = PairList.Columns[0]; var status = PairList.Columns[5]; var live = PairList.Columns[6];
        PairList.Columns.Clear(); PairList.Columns.Add(expand);
        PairList.Columns.Add(new DataGridTemplateColumn { MinWidth = 240, Width = new DataGridLength(1, DataGridLengthUnitType.Star), CellTemplate = RowTemplate("""
          <StackPanel Margin="8,10,16,10" VerticalAlignment="Center">
            <TextBlock Text="{Binding Name}" FontSize="16" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
            <TextBlock Text="{Binding Connection, StringFormat=Account: {0}}" FontSize="11" Foreground="{DynamicResource MutedInk}" TextTrimming="CharacterEllipsis" ToolTip="{Binding Connection}" Margin="0,3,0,0"/>
            <Button Tag="local" Background="Transparent" BorderThickness="0" Padding="0" MinHeight="20" Height="22" Margin="0,4,0,0" HorizontalContentAlignment="Stretch" ToolTip="Local Location actions">
              <DockPanel><local:CommandGlyph IconName="computer" Width="15" Height="15" Margin="0,0,7,0" DockPanel.Dock="Left"/><TextBlock Text="{Binding LocalRoot}" FontSize="12" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/></DockPanel>
            </Button>
            <Button Tag="cloud" Background="Transparent" BorderThickness="0" Padding="0" MinHeight="20" Height="22" Margin="0,3,0,0" HorizontalContentAlignment="Stretch" ToolTip="{Binding CloudName,StringFormat={}{0} Location actions}">
              <DockPanel><ContentControl Width="15" Height="15" Margin="0,0,7,0" DockPanel.Dock="Left" local:ProviderMark.Provider="{Binding Provider}"/><TextBlock Text="{Binding DropboxRoot}" FontSize="12" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/></DockPanel>
            </Button>
          </StackPanel>
          """) });
        status.Width = 158; live.Width = 130; PairList.Columns.Add(status); PairList.Columns.Add(live);
        PairList.Columns.Add(new DataGridTemplateColumn { Width = 42, CellTemplate = RowTemplate("""
          <Button Tag="menu" Content="⋯" Background="Transparent" BorderThickness="0" Padding="4" Margin="0" ToolTip="Folder Pair actions" AutomationProperties.Name="Folder Pair actions"/>
          """) });
        PairList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            var button = FindAncestor<Button>(e.OriginalSource as DependencyObject);
            if (button?.DataContext is not PairRow pair || button.Tag is not string action) return;
            PairList.SelectedItem = pair;
            if (action == "menu")
            { PairList.ContextMenu.PlacementTarget = button; PairList.ContextMenu.IsOpen = true; }
            else if (action is "local" or "cloud")
                OpenPathMenu(button, new ActivityRow("", "", "", "", pair.Connection, pair.Id, pair.Name, null, "Folder", "Folder", LocalRoot: pair.LocalRoot, DropboxRoot: pair.DropboxRoot, ConnectionId: _pairSet.Pairs.FirstOrDefault(item => item.Id == pair.Id)?.ConnectionId), action);
            e.Handled = true;
        }));
        // UI12: the leading control shows the pair's real cloud provider mark and the row body
        // toggles the same details. The handlers also receive already-handled events so a data
        // grid class handler cannot swallow the row activation, and they mark nothing handled.
        if (!_pairRowBodyHandlersAttached)
        {
            _pairRowBodyHandlersAttached = true;
            PairList.AddHandler(PreviewMouseLeftButtonDownEvent,
                new System.Windows.Input.MouseButtonEventHandler(PairRowBody_PreviewMouseLeftButtonDown),
                handledEventsToo: true);
            PairList.AddHandler(PreviewMouseLeftButtonUpEvent,
                new System.Windows.Input.MouseButtonEventHandler(PairRowBody_PreviewMouseLeftButtonUp),
                handledEventsToo: true);
            PairList.AddHandler(KeyDownEvent,
                new System.Windows.Input.KeyEventHandler(PairRowBody_KeyDown), handledEventsToo: true);
        }

        PairEditorTabs.ItemContainerStyle = WorkspaceTabStyle(); PairEditorTabs.BorderThickness = new Thickness(0);
        var names = new[] { "Folders", "Preview", "Sync Settings", "Status", "Advanced", "Storage" };
        for (var i = 0; i < PairEditorTabs.Items.Count; i++) ((TabItem)PairEditorTabs.Items[i]).Header = names[i];
        PairEditorPanel.BorderThickness = new Thickness(0); PairEditorPanel.Padding = new Thickness(18);
        PairIdentityDisclosure.Header = CommandIcons.Label("Pair Details", "folder");
        BindRootActions(LocalRootBox, "local"); BindRootActions(DropboxRootBox, "cloud");
        PairEditMenuItem.Header = CommandIcons.Label("Edit Pair"); PairSyncNowMenuItem.Header = CommandIcons.Label("Sync Now");
        PairDuplicateMenuItem.Header = CommandIcons.Label("Duplicate Pair", "copy"); PairDeleteMenuItem.Header = CommandIcons.Label("Delete Pair", "close");
        PairList.ContextMenu.Template = WorkspaceMenu().Template;
        PairList.ContextMenu.ItemContainerStyleSelector = WorkspaceMenu().ItemContainerStyleSelector;
    }

    private void PairHealthPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var pair = _pairSet.Pairs.FirstOrDefault(item => item.Id == (PairList.SelectedItem as PairRow)?.Id);
        if (!TryResolveCurrentEndpoints(pair, out var context)) return;
        var side = button.Tag?.ToString() == "local" ? SyncAuthorityCloudSides.LogicalLeft : SyncAuthorityCloudSides.LogicalRight;
        var endpoint = context.Left.CompatibilityPort == side ? context.Left : context.Right;
        var row = new ActivityRow("", "", "", "", PairId: pair!.Id, PairName: pair.Name,
            ItemKind: "Folder", FileType: "Folder", EndpointContext: context);
        var action = ActivityEndpointLocationActionFor(row, endpoint);
        var menu = WorkspaceMenu(); menu.PlacementTarget = button; menu.Placement = PlacementMode.Bottom;
        MenuAction(menu, action.Label, endpoint.IsLocal ? "folder" : "external", action.Reason,
            (_, _) => OpenEndpointLocationAsync(row, endpoint), action.Enabled);
        MenuAction(menu, "Copy folder location", "copy", "Copy this endpoint's recorded folder location.",
            (_, _) => SetClipboardText(action.Location!), action.Location is not null);
        menu.IsOpen = true;
    }

    private void BindRootActions(TextBox box, string side)
    {
        void UpdateAvailability()
        {
            var available = !string.IsNullOrWhiteSpace(box.Text);
            box.IsHitTestVisible = available;
            box.Focusable = available;
            box.Cursor = available ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
            box.ToolTip = available ? "Open actions for this selected location" : null;
        }
        box.TextChanged += (_, _) => UpdateAvailability();
        UpdateAvailability();
        box.PreviewMouseLeftButtonUp += (_, e) => { Open(); e.Handled = true; };
        box.PreviewMouseRightButtonUp += (_, e) => { Open(); e.Handled = true; };
        void Open()
        {
            if (box.Text.Length == 0) return;
            var pair = PairList.SelectedItem as PairRow;
            OpenPathMenu(box, new ActivityRow("", "", "", "", PairId: pair?.Id, PairName: PairNameBox.Text,
                ItemKind: "Folder", FileType: "Folder", LocalRoot: LocalRootBox.Text.Length == 0 ? null : LocalRootBox.Text,
                DropboxRoot: DropboxRootBox.Text.Length == 0 ? null : DropboxRootBox.Text, ConnectionId: SelectedPairConnectionId), side);
        }
    }

    private Grid? _recoveryBrowser;
    // UI16: the recovery page's shared presentation state. The view switch, the details host and the
    // context identity all belong to the recovery surface, so a pair change can discard the selected
    // saved copy without touching the shared inspector used by the rest of the workspace.
    internal SimpleDetailedSwitch? _recoveryViewSwitch;
    internal DetailsHost? _recoveryDetailsHost;
    internal StackPanel? _recoveryEmptyPanel;
    private StackPanel? _recoveryDetailFields;
    private ContentPresenter? _recoveryDetailsSidebar;
    private Border? _recoveryInspector;
    private ColumnDefinition? _recoveryInspectorColumn;
    private bool _recoveryPresentationReady;
    private bool _pauseChangeActive;
    // SS-D02: the ONE reader of a retained run result. The Runs surface, the Home latest-run summary and
    // the tray notification all classify through this method, so no consumer keeps its own reading of the
    // same retained JSON and none of them can disagree about an outcome or about whether its counts are
    // complete.
    //
    // Completeness follows the authority's own rule for a retained pair result: the record must state its
    // failed, blocked, conflicts, needsVerification and notAttempted counts as integers. A record that
    // states fewer (an older, truncated or malformed summary) is classified NotRecorded and can never
    // become a success, while every count it does state is still shown and the ones it does not are
    // stated as not recorded rather than as zero.
    internal sealed record RetainedRunOutcome
    {
        public required SyncRunOutcomeProjection Outcome { get; init; }
        /// <summary>A retained result field exists at all.</summary>
        public bool ResultPresent { get; init; }
        /// <summary>The retained result was read as an object.</summary>
        public bool ResultReadable { get; init; }
        /// <summary>The record carries the per-pair array its counts are stated in.</summary>
        public bool PairsRecorded { get; init; }
        public int? Uploaded { get; init; }
        public int? Downloaded { get; init; }
        public int? Unchanged { get; init; }
        /// <summary>The blocked and conflicting count this record states, aggregated over its pairs.</summary>
        public int? Attention { get; init; }
        public int? Blocked { get; init; }
        public int? Conflicts { get; init; }
        public int? Failed { get; init; }
        public int? NotAttempted { get; init; }
        public int? NeedsVerification { get; init; }
        public int? Skipped { get; init; }
        /// <summary>The run's own failed-pair count, which is failure evidence even without pair counts.</summary>
        public int? FailedPairs { get; init; }
        public bool Stopped { get; init; }
        public bool FirstSyncIncomplete { get; init; }
        public IReadOnlyList<string> PerPairCounts { get; init; } = [];
    }

    private const string RetainedCountNotRecorded = "Not recorded";

    private static string RetainedCountLabel(int? value) =>
        value is { } recorded ? recorded.ToString("N0") : RetainedCountNotRecorded;

    // A record that states no usable outcome at all. Nothing is presented as a zero count and the
    // classification stays NotRecorded.
    private static RetainedRunOutcome UnrecordedRetainedOutcome(bool completed, bool resultPresent = false,
        bool resultReadable = false) => new()
    {
        Outcome = SyncRunOutcomeProjection.Classify(new SyncRunOutcomeInput
        {
            Completed = completed,
            CountsRecorded = false
        }),
        ResultPresent = resultPresent,
        ResultReadable = resultReadable
    };

    // The authority's own rule for a judgeable retained pair result: these counts must be stated as
    // integers. A record that omits them cannot support a success claim.
    private static bool HasOutcomeCounts(JsonElement element) =>
        NumberValue(element, "failed") is not null && NumberValue(element, "blocked") is not null &&
        NumberValue(element, "conflicts") is not null && NumberValue(element, "needsVerification") is not null &&
        NumberValue(element, "notAttempted") is not null;

    // A recorded status that merely claims completion is not completion evidence: the counts decide.
    private static bool ClaimsCompletion(string? status) =>
        string.Equals(status?.Trim(), "completed", StringComparison.OrdinalIgnoreCase);

    // The recorded status word for a run whose counts cannot judge it. A completion claim is deliberately
    // absent here so no unjudgeable record is presented with the success word.
    private static string RecordedRunStatusWord(string status) => status.ToLowerInvariant() switch
    {
        "blocked" => "Needs Attention",
        "executing" => "Running",
        "cancelled" or "stopped" => "Stopped",
        "rejected" => "Rejected",
        "stale" => "Out of Date",
        "requested" or "accepted" or "pending" => "Queued",
        _ => status
    };

    private static RetainedRunOutcome ProjectRetainedRun(SyncAuthorityRunHistory run)
    {
        var completed = run.CompletedUtc is not null;
        if (string.IsNullOrWhiteSpace(run.ResultJson)) return UnrecordedRetainedOutcome(completed);
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(run.ResultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return UnrecordedRetainedOutcome(completed, resultPresent: true);
            var stopped = IsTrue(root, "Stopped") || IsTrue(root, "stopped");
            var failedPairs = NumberValue(root, "failedPairs");
            var firstSyncIncomplete = root.TryGetProperty("firstSyncComplete", out var firstComplete) &&
                firstComplete.ValueKind == JsonValueKind.False;
            int? uploaded, downloaded, unchanged, attention, blocked, conflicts;
            int? failed, notAttempted, needsVerification, skipped;
            var countsComplete = false;
            var perPair = new List<string>();
            var pairsRecorded = root.TryGetProperty("pairs", out var pairs) && pairs.ValueKind == JsonValueKind.Array;
            if (pairsRecorded)
            {
                var entries = pairs.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object).ToArray();
                uploaded = AggregateCount(entries, "uploaded", "Uploaded");
                downloaded = AggregateCount(entries, "downloaded", "Downloaded");
                unchanged = AggregateCount(entries, "unchanged", "Unchanged");
                attention = AggregateAttention(entries);
                blocked = AggregateCount(entries, "blocked");
                conflicts = AggregateCount(entries, "conflicts");
                failed = AggregateCount(entries, "failed");
                notAttempted = AggregateCount(entries, "notAttempted");
                needsVerification = AggregateCount(entries, "needsVerification");
                skipped = AggregateCount(entries, "skipped");
                stopped = stopped || entries.Any(entry => IsTrue(entry, "stopped"));
                countsComplete = entries.Length > 0 && entries.All(HasOutcomeCounts);
                if (entries.Length > 1)
                    foreach (var entry in entries)
                    {
                        var pairCounts = new List<string>();
                        AddCount(pairCounts, "Uploaded", NumberValue(entry, "uploaded", "Uploaded"));
                        AddCount(pairCounts, "Downloaded", NumberValue(entry, "downloaded", "Downloaded"));
                        AddCount(pairCounts, "Unchanged", NumberValue(entry, "unchanged", "Unchanged"));
                        AddCount(pairCounts, "Needs Attention", AggregatedAttention(entry));
                        var identity = StringValue(entry, "pairId");
                        perPair.Add((string.IsNullOrEmpty(identity) ? RunHistoryRow.NotRecorded : identity) +
                            " — " + (pairCounts.Count == 0 ? RunHistoryRow.NotRecorded : string.Join(" · ", pairCounts)));
                    }
            }
            else
            {
                uploaded = NumberValue(root, "uploaded", "Uploaded");
                downloaded = NumberValue(root, "downloaded", "Downloaded");
                unchanged = NumberValue(root, "unchanged", "Unchanged", "identical", "Identical");
                attention = AggregatedAttention(root);
                blocked = NumberValue(root, "blocked", "Blocked");
                conflicts = NumberValue(root, "conflicts", "Conflicts");
                failed = NumberValue(root, "failed");
                notAttempted = NumberValue(root, "notAttempted");
                needsVerification = NumberValue(root, "needsVerification");
                skipped = NumberValue(root, "skipped");
                countsComplete = HasOutcomeCounts(root);
            }
            var outcome = SyncRunOutcomeProjection.Classify(new SyncRunOutcomeInput
            {
                Outcome = StringValue(root, "outcome"),
                Pending = NumberValue(root, "pending") ?? 0,
                Deferred = NumberValue(root, "deferred") ?? 0,
                Completed = completed,
                CountsRecorded = countsComplete,
                Stopped = stopped,
                Uploaded = uploaded ?? 0,
                Downloaded = downloaded ?? 0,
                Unchanged = unchanged ?? 0,
                // The run's own failed-pair count is failure evidence for that run even when the per-pair
                // counts are missing, so a failed batch is never classified as a success.
                Failed = Math.Max(failed ?? 0, failedPairs ?? 0),
                Blocked = blocked ?? 0,
                Conflicts = conflicts ?? 0,
                NeedsVerification = needsVerification ?? 0,
                NotAttempted = notAttempted ?? 0,
                Skipped = skipped ?? 0
            });
            return new RetainedRunOutcome
            {
                Outcome = outcome,
                ResultPresent = true,
                ResultReadable = true,
                PairsRecorded = pairsRecorded,
                Uploaded = uploaded,
                Downloaded = downloaded,
                Unchanged = unchanged,
                Attention = attention,
                Blocked = blocked,
                Conflicts = conflicts,
                Failed = failed,
                NotAttempted = notAttempted,
                NeedsVerification = needsVerification,
                Skipped = skipped,
                FailedPairs = failedPairs,
                Stopped = stopped,
                FirstSyncIncomplete = firstSyncIncomplete,
                PerPairCounts = perPair
            };
        }
        catch (JsonException)
        {
            return UnrecordedRetainedOutcome(completed, resultPresent: true);
        }
        catch (ArgumentException)
        {
            return UnrecordedRetainedOutcome(completed, resultPresent: true);
        }
        finally { document?.Dispose(); }
    }

    private static string RetainedRunSummary(SyncAuthorityRunHistory run)
    {
        var retained = ProjectRetainedRun(run);
        // A record that claims completion without the counts needed to judge it says so rather than
        // borrowing the success word; every other unjudgeable record keeps its own recorded status.
        var status = retained.Outcome.Kind == SyncRunOutcomeKind.NotRecorded && !ClaimsCompletion(run.Status)
            ? RecordedRunStatusWord(run.Status)
            : retained.Outcome.StatusWord;
        var timestamp = run.CompletedUtc is { } completed
            ? $"Completed: {completed.ToLocalTime():g}"
            : $"Started: {run.StartedUtc.ToLocalTime():g}";
        var summary = $"{timestamp}\nStatus: {status}";
        if (!retained.ResultPresent) return summary + "\n\nCounts: " + RetainedCountNotRecorded;
        if (!retained.ResultReadable)
            return summary + "\n\nCounts: " + RetainedCountNotRecorded + " (unreadable retained result)";
        if (!retained.PairsRecorded) return summary + "\nSee Activity for the recorded outcome.";
        // The shared outcome line every consumer shows, stated only while the counts it names are
        // recorded; a partially recorded total keeps its per-count not-recorded markers instead.
        if (retained.Outcome.CountsRecorded && retained.Uploaded is not null && retained.Downloaded is not null &&
            retained.Unchanged is not null && retained.Attention is not null)
            return summary + "\n\n" + string.Join("\n", retained.Outcome.CountLines);
        var counts = new[] { ("Uploaded", RetainedCountLabel(retained.Uploaded)),
            ("Downloaded", RetainedCountLabel(retained.Downloaded)),
            ("Unchanged", RetainedCountLabel(retained.Unchanged)),
            ("Needs Attention", RetainedCountLabel(retained.Attention)) };
        return summary + (counts.All(value => value.Item2 == RetainedCountNotRecorded)
            ? "\n\nCounts: " + RetainedCountNotRecorded
            : "\n\n" + string.Join("\n", counts.Select(value => $"{value.Item1}: {value.Item2}")));
    }
    internal bool GlobalPauseActive => _workspacePreferences.PauseUntilResumed || _workspacePreferences.PauseUntilUtc > DateTimeOffset.UtcNow;
    internal string PauseDescription => PauseSchedule.Describe(_workspacePreferences.PauseUntilResumed,
        _workspacePreferences.PauseUntilUtc, _workspacePreferences.PauseTimeZoneId, DateTimeOffset.UtcNow);
    internal string PauseDetail => PauseSchedule.DescribeDetail(_workspacePreferences.PauseUntilResumed,
        _workspacePreferences.PauseUntilUtc, _workspacePreferences.PauseTimeZoneId, DateTimeOffset.UtcNow);
    internal string TrayPair => _workspaceRunningPair ?? (_pairSet.Pairs.Count == 1 ? _pairSet.Pairs[0].Name : $"{_pairSet.Pairs.Count} Folder Pairs");
    internal string TrayTiming => !_workspaceReady ? "Starting…" : _runActive && _workspaceRunStarted is { } started
        ? $"Run Started: {started.ToLocalTime():HH:mm:ss}    Elapsed: {(DateTimeOffset.Now - started):hh\\:mm\\:ss}"
        : _retainedRuns.FirstOrDefault(item => item.CompletedUtc is not null)?.CompletedUtc is { } ended
            ? "Last Run Finished: " + ended.ToLocalTime().ToString("g") : "No completed run recorded yet";
    internal UiPreferences WorkspacePreferences => _workspacePreferences;
    internal void OpenWorkspaceSettings() { ShowFromBackground(); NavigateTo("Settings"); }
    internal async Task HandleWorkspacePauseActionAsync(UIElement target)
    {
        if (TrayPaused) await SetSyncPauseAsync(null, false);
        else OpenPauseMenu(target);
    }
    internal void OpenPauseMenu(UIElement target)
    {
        var menu = WorkspaceMenu(); menu.PlacementTarget = target; menu.Placement = PlacementMode.Bottom;
        // The option set comes from the one shared definition; nothing here owns its own list.
        // Scope and deadline live on the menu itself rather than on every row.
        menu.ToolTip = "All accounts. " + PauseDetail;
        if (TrayPaused)
            MenuAction(menu, "Resume Sync", "play", "Resumes the global pause. Account and pair pauses are unchanged.",
                async (_, _) => await ResumePausedAsync(null));
        foreach (var option in PauseOption.All)
            MenuAction(menu, option.Label, option.StopSquare ? "stop" : "pause", option.Detail,
                async (_, _) => await SetPausedAsync(option, null));
        menu.IsOpen = true;
    }

    // Compatibility shim for existing callers. A supplied duration is preserved exactly: a shared
    // option when it matches one, its own resolved minutes otherwise, and an explicit refusal when
    // it is out of range. Nothing is silently substituted.
    internal async Task SetSyncPauseAsync(TimeSpan? duration, bool indefinitely)
    {
        if (indefinitely) { await SetPausedAsync(PauseOption.Indefinitely, null); return; }
        if (duration is not { } delay) { await ResumePausedAsync(null); return; }
        await SetPausedDurationAsync(delay, null);
    }

    internal async Task SetPausedDurationAsync(TimeSpan duration, string? connectionId)
    {
        var minutes = duration.TotalMinutes;
        if (minutes < 1 || minutes > 43_200 || Math.Abs(minutes - Math.Round(minutes)) > 0.0001)
        {
            ShowActionStatus("A pause of between 1 and 43200 whole minutes is required. Nothing was changed.");
            return;
        }
        var whole = (int)Math.Round(minutes);
        if (PauseOption.FromMinutes(whole) is { } option) { await SetPausedAsync(option, connectionId); return; }
        await SetPausedMinutesAsync(whole, connectionId);
    }

    internal Task SetPausedMinutesAsync(int minutes, string? connectionId)
    {
        if (minutes is < 1 or > 43_200)
        {
            ShowActionStatus("A pause of between 1 and 43200 whole minutes is required. Nothing was changed.");
            return Task.CompletedTask;
        }
        return ApplyPauseAsync(null, minutes, connectionId);
    }

    // Explicit pause for one resolved option in the exact scope. Scope, identity and the resolved
    // deadline are all decided at the mutation boundary, after the gate, so no earlier check can go
    // stale across an await, and no other scope is ever changed.
    internal Task SetPausedAsync(PauseOption option, string? connectionId)
    {
        ArgumentNullException.ThrowIfNull(option);
        return ApplyPauseAsync(option, null, connectionId);
    }

    private async Task ApplyPauseAsync(PauseOption? option, int? minutes, string? connectionId)
    {
        if (_pauseChangeActive) { ShowActionStatus("A pause change is already being applied. Nothing was changed."); return; }
        if (connectionId is not null && !PauseScopeRules.IsExactConnectionId(connectionId))
        {
            ShowActionStatus("That account identity is not an approved connection identifier. Nothing was changed.");
            return;
        }
        _pauseChangeActive = true;
        var applied = false;
        var refused = false;
        var runPaused = false;
        try
        {
            await _workspacePreferencesGate.WaitAsync();
            try
            {
                if (connectionId is not null && !IsCurrentPauseAccount(connectionId))
                {
                    ShowActionStatus("That account is no longer connected. Nothing was changed.");
                    return;
                }
                // Account scope holds no control over an active run, so it is refused while one is
                // running rather than pausing every account.
                if (connectionId is not null && _runActive)
                {
                    refused = true;
                }
                else
                {
                    var nowUtc = DateTimeOffset.UtcNow;
                    var resolution = option is not null
                        ? PauseSchedule.Resolve(option, nowUtc, TimeZoneInfo.Local)
                        : new PauseResolution(false, nowUtc.AddMinutes(minutes!.Value), TimeZoneInfo.Local.Id,
                            TimeZoneInfo.ConvertTime(nowUtc.AddMinutes(minutes.Value), TimeZoneInfo.Local));
                    var current = _workspacePreferencesStore.Load();
                    UiPreferences next;
                    if (connectionId is null)
                        next = current with
                        {
                            PauseUntilResumed = resolution.Indefinitely,
                            PauseUntilUtc = resolution.UntilUtc,
                            PauseTimeZoneId = resolution.TimeZoneId
                        };
                    else
                    {
                        var pauses = (current.AccountPauses ?? [])
                            .Where(item => !string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal))
                            .ToList();
                        pauses.Add(new SyncAccountPause(connectionId, resolution.Indefinitely, resolution.UntilUtc,
                            resolution.TimeZoneId));
                        next = current with { AccountPauses = pauses };
                    }
                    await _workspacePreferencesStore.SaveAsync(next); _workspacePreferences = next;
                    applied = true;
                    if (_runActive && !_workspacePaused) runPaused = await SetRunPausedAsync(true);
                }
            }
            finally { _workspacePreferencesGate.Release(); }
        }
        catch (Exception e)
        {
            // The reported result is the actual one; a failed mutation is never followed by success.
            RefreshWorkspace();
            ShowActionStatus(applied
                ? "The pause preference was saved, but pausing the active run could not be confirmed: " + e.Message
                : "The pause could not be saved. Check the current pause state before retrying: " + e.Message);
            return;
        }
        finally { _pauseChangeActive = false; }
        RefreshWorkspace();
        if (refused) { ShowActionStatus(PauseScopeRules.ActiveRunRefusal("account")); return; }
        if (!applied) return;
        // Continuous sync re-checks eligibility now, so a pause stops automatic runs and a resume starts one.
        KickV2Monitors(null);
        ShowActionStatus(connectionId is null
            ? PauseDetail + (runPaused ? " The active run pauses at its next safe checkpoint." : string.Empty)
            : "This account is paused. Other accounts and other pause scopes are unchanged.");
    }

    // Explicit resume of one scope. Global, account and pair pauses stay independent, and only the
    // global scope may release the run pause, and only when no account pause still holds work.
    internal async Task ResumePausedAsync(string? connectionId)
    {
        if (_pauseChangeActive) { ShowActionStatus("A pause change is already being applied. Nothing was changed."); return; }
        if (connectionId is not null && !PauseScopeRules.IsExactConnectionId(connectionId))
        {
            ShowActionStatus("That account identity is not an approved connection identifier. Nothing was changed.");
            return;
        }
        _pauseChangeActive = true;
        var cleared = false;
        var runReleased = false;
        var runHeldByAccountPause = false;
        try
        {
            await _workspacePreferencesGate.WaitAsync();
            try
            {
                if (connectionId is not null && !IsCurrentPauseAccount(connectionId))
                {
                    ShowActionStatus("That account is no longer connected. Nothing was changed.");
                    return;
                }
                if (connectionId is not null && _runActive)
                {
                    ShowActionStatus(PauseScopeRules.ActiveRunRefusal("account"));
                    return;
                }
                var current = _workspacePreferencesStore.Load();
                var next = connectionId is null
                    ? current with { PauseUntilResumed = false, PauseUntilUtc = null, PauseTimeZoneId = null }
                    : current with
                    {
                        AccountPauses = (current.AccountPauses ?? [])
                            .Where(item => !string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal))
                            .ToArray()
                    };
                await _workspacePreferencesStore.SaveAsync(next); _workspacePreferences = next;
                cleared = true;
                if (connectionId is null && _runActive && _workspacePaused)
                {
                    if (PauseStateProjection.ActiveAccountPauses(next, DateTimeOffset.UtcNow).Count == 0)
                        runReleased = await SetRunPausedAsync(false);
                    else
                        runHeldByAccountPause = true;
                }
            }
            finally { _workspacePreferencesGate.Release(); }
        }
        catch (Exception e)
        {
            RefreshWorkspace();
            ShowActionStatus(cleared
                ? "The pause preference was cleared, but resuming the active run could not be confirmed: " + e.Message
                : "The pause change could not be saved. Check the current pause state before retrying: " + e.Message);
            return;
        }
        finally { _pauseChangeActive = false; }
        RefreshWorkspace();
        if (!cleared) return;
        // A resume lets continuous sync run again now instead of waiting for the next signal.
        KickV2Monitors(null);
        if (runHeldByAccountPause)
        {
            ShowActionStatus("Sync resumed for new work, but the active run stays paused because an account pause is " +
                "still in place. Resuming one account does not release the run for another.");
            return;
        }
        ShowActionStatus(connectionId is null
            ? "Sync resumed. Other pause scopes are unchanged." + (runReleased ? " The active run continues." : string.Empty)
            : "This account resumed. Global and pair pauses are unchanged.");
    }

    // The only run-pause control the app may use. It is explicit, so a scope change can never become
    // a blind toggle of the active run.
    private bool IsCurrentPauseAccount(string connectionId) =>
        _authorityHost.LoadProviderConnections().Any(account => account.IsActive &&
            string.Equals(account.ConnectionId, connectionId, StringComparison.Ordinal));

    private async Task<bool> SetRunPausedAsync(bool paused)
    {
        var result = await _authorityHost.SetActiveRunPausedAsync(paused);
        if (result is null) return false;
        _workspacePaused = result.Value;
        PauseButton.Content = result.Value ? "Resume sync" : "Pause sync";
        return true;
    }

    private async Task CheckPauseDeadlineAsync()
    {
        if (_pauseChangeActive) return;
        var nowUtc = DateTimeOffset.UtcNow;
        if (!_workspacePreferences.PauseUntilResumed && _workspacePreferences.PauseUntilUtc is { } until && until <= nowUtc)
        {
            await SetSyncPauseAsync(null, false);
            // A paused pair that expires is resumed: core v1 runs one scheduled check, core v2 kicks continuous sync.
            if (!GlobalPauseActive && !_runActive && AutomaticSyncEnabled)
            {
                if (HostIsCoreV1) await RunScheduledAsync("Pause expired");
                else KickV2Monitors(null);
            }
        }
        // Core v2 continuous sync only: a timed account pause that lapses must be cleared, and the account's pairs
        // kicked, because its paused loop waits for a Kick that a mere deadline never sends.
        if (!HostIsCoreV1) await ClearExpiredAccountPausesAsync(nowUtc);
    }

    // A timed account pause that has expired is removed from storage and every pair on that account is
    // re-evaluated now. The global deadline above resumes itself; account deadlines were never checked, so their
    // pairs stayed parked in continuous sync's paused branch.
    private async Task ClearExpiredAccountPausesAsync(DateTimeOffset nowUtc)
    {
        if (PauseStateProjection.ExpiredAccountPauses(_workspacePreferences, nowUtc).Count == 0) return;
        await _workspacePreferencesGate.WaitAsync();
        try
        {
            // Recompute under the gate: a pause or resume that landed meanwhile is never overwritten.
            var current = _workspacePreferencesStore.Load();
            var expired = PauseStateProjection.ExpiredAccountPauses(current, DateTimeOffset.UtcNow);
            if (expired.Count == 0) return;
            var ids = expired.Select(item => item.ConnectionId).ToHashSet(StringComparer.Ordinal);
            var next = current with
            {
                AccountPauses = (current.AccountPauses ?? [])
                    .Where(item => !ids.Contains(item.ConnectionId)).ToArray()
            };
            await _workspacePreferencesStore.SaveAsync(next); _workspacePreferences = next;
            foreach (var id in ids) KickV2MonitorsForAccount(id);
        }
        finally { _workspacePreferencesGate.Release(); }
    }
    // RCOPY-1: the one approved provider-neutral tooltip for the visible Recovery refresh action.
    private const string RecoveryRefreshTooltip =
        "Refresh retained Sync Shark archives and available deleted-item metadata for this pair's provider.";

    private void BuildRecoveryTabs()
    {
        _recoveryBrowser = (Grid)RecoveryPage.Child; RecoveryPage.Child = null;
        // The browser is reused between tabs; inherited data context must not disappear in transit.
        _recoveryBrowser.DataContext = this;
        var heading = (StackPanel)_recoveryBrowser.Children.Cast<FrameworkElement>().First(child => Grid.GetRow(child) == 0);
        foreach (var text in heading.Children.OfType<TextBlock>()) if (!ReferenceEquals(text, RecoverySummary)) text.Visibility = Visibility.Collapsed;
        var toolbar = (WrapPanel)_recoveryBrowser.Children.Cast<FrameworkElement>().First(child => Grid.GetRow(child) == 1);
        var revisions = (StackPanel)RecoveryPathBox.Parent;
        revisions.Children.Remove(RecoverySearchBox);
        toolbar.Children.Insert(2, SharedPresentation.QuietSearchField(RecoverySearchBox,
            "Search saved copies", "Search saved copies…",
            tooltip: "Filter the recovery items already loaded; no provider refresh."));
        RecoveryPairBox.Style = SharedPresentation.QuietComboBoxStyle();
        var pathLabel = Text("Relative Path:", 12, brush: "MutedInk");
        pathLabel.VerticalAlignment = VerticalAlignment.Center; pathLabel.Margin = new Thickness(12, 0, 8, 0);
        revisions.Children.Insert(0, pathLabel);
        RecoveryPathBox.Width = 220;
        // RCOPY-1: the live search tooltip is the provider-neutral generic sentence, identical to the
        // identity declared on the same control in MainWindow.xaml; filtering never refreshes a provider.
        RecoverySearchBox.ToolTip = "Filter the recovery items already loaded; no provider refresh.";
        foreach (var button in toolbar.Children.OfType<Button>()) button.Visibility = Visibility.Collapsed;
        // RCOPY-1/AUTH-I2: the legacy XAML command is collapsed, so this visible replacement is the only
        // refresh affordance the user sees. It carries the approved provider-neutral tooltip and an
        // unambiguous accessible name itself instead of inheriting neither from the hidden control.
        var refreshRecovery = Button("Refresh", RefreshRecovery_Click, true);
        refreshRecovery.ToolTip = RecoveryRefreshTooltip;
        System.Windows.Automation.AutomationProperties.SetName(refreshRecovery, "Refresh archives and deleted");
        toolbar.Children.Add(refreshRecovery);
        // UI16: Recovery shares the Activity/Pending Simple/Detailed switch. The switch swaps the row
        // presentation only; the same rows, search filter and selected copy stay in place, and the
        // mode is remembered per page through the existing preferences store.
        _recoveryViewSwitch = SharedPresentation.ViewSwitch("recovery", IsDetailedView("recovery"),
            RecoveryViewChanged);
        toolbar.Children.Add(_recoveryViewSwitch);
        var restoreActions = (StackPanel)RestoreOriginalButton.Parent; restoreActions.Visibility = Visibility.Collapsed;
        RecoveryGrid.Columns.Clear(); RecoveryGrid.HeadersVisibility = DataGridHeadersVisibility.Column;
        RecoveryGrid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal; RecoveryGrid.BorderThickness = new Thickness(0);
        RecoveryGrid.Columns.Add(new DataGridTemplateColumn { Header = "Item", Width = new DataGridLength(1, DataGridLengthUnitType.Star), CellTemplate = RowTemplate("""
          <DockPanel Margin="10,8"><local:ShellTypeImage Width="28" Height="28" Margin="0,0,14,0"/><StackPanel VerticalAlignment="Center">
          <TextBlock Text="{Binding DisplayName}" FontSize="14" FontWeight="SemiBold"/><TextBlock Text="{Binding CompactSubtitle}" ToolTip="{Binding Subtitle}" FontSize="12" Foreground="{DynamicResource MutedInk}" Margin="0,5,0,0" TextTrimming="CharacterEllipsis"/></StackPanel></DockPanel>
          """) });
        RecoveryGrid.Columns.Add(new DataGridTextColumn { Header = "Size", Binding = new Binding("Size"), Width = 90 });
        var retainedStyle = new Style(typeof(TextBlock));
        retainedStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding("Retained")));
        retainedStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        RecoveryGrid.Columns.Add(new DataGridTextColumn { Header = "Saved", Binding = new Binding("RetainedDisplay"),
            ElementStyle = retainedStyle, Width = 155 });
        RecoveryGrid.Columns.Add(new DataGridTemplateColumn { Width = 45, CellTemplate = RowTemplate("<Button Content='⋯' ToolTip='Recovery actions' Background='Transparent' BorderThickness='0'/>") });
        var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false, Margin = new Thickness(24), Visibility = Visibility.Collapsed };
        _recoveryEmptyPanel = empty;
        empty.Children.Add(CommandIcons.Icon("history", 32));
        var emptyTitle = Text("No Saved Copies", 18, true); emptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
        emptyTitle.Margin = new Thickness(0, 14, 0, 8); empty.Children.Add(emptyTitle);
        var emptyDescription = Text("Copies retained before changes will appear here. Nothing has been removed.", 13, brush: "MutedInk");
        emptyDescription.MaxWidth = 460; emptyDescription.TextAlignment = TextAlignment.Center; empty.Children.Add(emptyDescription);
        Grid.SetRow(empty, 2); _recoveryBrowser.Children.Add(empty);
        RecoveryRows.CollectionChanged += (_, _) => empty.Visibility = RecoveryRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecoveryGrid.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        { if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is RecoveryRow row) { OpenRecoveryMenu((UIElement)e.OriginalSource, row); e.Handled = true; } }));
        RecoveryGrid.PreviewMouseRightButtonDown += (_, e) =>
        { if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is RecoveryRow row) { OpenRecoveryMenu(RecoveryGrid, row); e.Handled = true; } };
        // UI16: the shared details host carries one projected recovery model into both the compact
        // sidebar and the expanded dialog, so expanding and returning keeps the selected saved copy,
        // its scroll and its action state. The retired plain-text block is detached here so the old
        // single-string details cannot render beside the structured fields.
        _recoveryDetailFields = new StackPanel();
        var recoveryDetailsScroll = new ScrollViewer { Content = _recoveryDetailFields,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _recoveryDetailsSidebar = new ContentPresenter { HorizontalAlignment = HorizontalAlignment.Stretch };
        _recoveryDetailsHost = new DetailsHost(recoveryDetailsScroll, _recoveryDetailsSidebar);
        var detail = (Grid)RecoveryDetails.Parent;
        Detach(RecoveryDetails);
        Detach(detail);
        var detailHeader = new DockPanel();
        // The Recovery opener shares the one details expand presentation instead of restating it.
        var expandRecovery = DetailsChrome.ExpandButton("Selected Copy Details",
            (_, _) => _recoveryDetailsHost?.Expand(this));
        DockPanel.SetDock(expandRecovery, Dock.Right);
        detailHeader.Children.Add(expandRecovery);
        var closeRecovery = IconButton("Close Selected Copy Details", "close", (_, _) =>
        { _recoveryDetailsHost?.Collapse(); RecoveryGrid.SelectedItem = null; });
        DockPanel.SetDock(closeRecovery, Dock.Right);
        detailHeader.Children.Add(closeRecovery);
        detailHeader.Children.Add(Text("Selected Copy Details", 13, true));
        var inspectorLayout = new DockPanel();
        DockPanel.SetDock(detailHeader, Dock.Top);
        inspectorLayout.Children.Add(detailHeader);
        inspectorLayout.Children.Add(_recoveryDetailsSidebar);
        _recoveryInspector = Card(inspectorLayout, 14);
        _recoveryInspector.Margin = new Thickness(12, 0, 0, 0);
        _recoveryInspector.Visibility = Visibility.Collapsed;
        var results = new Grid();
        results.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _recoveryInspectorColumn = new ColumnDefinition { Width = new GridLength(0), MaxWidth = 360 };
        results.ColumnDefinitions.Add(_recoveryInspectorColumn);
        Detach(RecoveryGrid); Detach(empty);
        Grid.SetRow(RecoveryGrid, 0); Grid.SetRow(empty, 0);
        results.Children.Add(RecoveryGrid); results.Children.Add(empty);
        Grid.SetColumn(_recoveryInspector, 1); results.Children.Add(_recoveryInspector);
        Grid.SetRow(results, 2); _recoveryBrowser.Children.Add(results);
        _recoveryTabs = new TabControl { ItemContainerStyle = WorkspaceTabStyle(), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        _recoveryTabs.Items.Add(new TabItem { Header = "Saved Copies", Content = _recoveryBrowser });
        _recoveryTabs.Items.Add(new TabItem { Header = "Cloud History" });
        _recoveryTabs.Items.Add(new TabItem { Header = "Dropbox Rewind", Content = Detach(RewindPage) });
        RewindPage.Visibility = Visibility.Visible;
        _recoveryTabs.SelectionChanged += async (_, e) =>
        {
            if (!ReferenceEquals(e.Source, _recoveryTabs)) return;
            if (_recoveryTabs.SelectedIndex < 2)
            {
                foreach (var tab in _recoveryTabs.Items.Cast<TabItem>().Take(2)) tab.Content = null;
                ((TabItem)_recoveryTabs.SelectedItem).Content = _recoveryBrowser;
                revisions.Visibility = _recoveryTabs.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
                emptyTitle.Text = _recoveryTabs.SelectedIndex == 1 ? "No Matching Cloud History" : "No Saved Copies";
                emptyDescription.Text = _recoveryTabs.SelectedIndex == 1
                    ? "Enter a relative file path to load versions retained by this provider. Deleted-item browsing depends on the provider."
                    : "Copies retained before changes will appear here. Nothing has been removed.";
                if (_fullAppReady) await RefreshRecoveryAsync(_recoveryTabs.SelectedIndex == 1);
            }
            else RefreshRewindState();
        };
        revisions.Visibility = Visibility.Collapsed;
        _recoveryPresentationReady = true;
        ApplyRecoveryViewMode();
        ShowRecoveryDetails(null);
        RecoveryPage.Child = PageShell("Recovery", "Restore saved versions safely. Nothing is replaced without a preview.", _recoveryTabs);
    }

    private void OpenRecoveryMenu(UIElement target, RecoveryRow row)
    {
        RecoveryGrid.SelectedItem = row;
        var menu = RecoveryRowMenu(row);
        menu.PlacementTarget = target;
        menu.IsOpen = true;
    }

    // UI16: the recovery row menu is composed once so the visible context actions and their exact
    // enablement can never drift. Restore and removal keep their existing CanRestore/CanRemove
    // guards and now also match the run/loading refusal that the restore and remove handlers apply.
    internal ContextMenu RecoveryRowMenu(RecoveryRow row)
    {
        var busy = _runActive || _loadingRecovery;
        var menu = WorkspaceMenu();
        MenuAction(menu, "Restore as Copy", RestoreCopy_Click, row.Item.CanRestoreCopy && !busy);
        MenuAction(menu, "Restore Original", RestoreOriginal_Click, row.Item.CanRestore && !busy);
        // The revision action names the selected pair's own provider through the one existing shared
        // provider lookup, so a Google Drive pair is no longer offered a Dropbox-only action name.
        MenuAction(menu, "Load " + RecoveryProviderName(row.Item.PairId) + " Revisions", LoadRecoveryRevisions_Click, !busy);
        MenuAction(menu, "Copy Relative Path", (_, _) => SetClipboardText(row.Path));
        MenuAction(menu, "Copy Saved Location", (_, _) => SetClipboardText(row.Location));
        MenuAction(menu, "Copy Details", (_, _) => SetClipboardText(row.DetailText()));
        menu.Items.Add(new Separator());
        MenuAction(menu, "Remove Saved Copy", RemoveRecovery_Click, row.Item.CanRemove && !busy);
        return menu;
    }

    private void BuildAttentionPage()
    {
        // UI13: every subtab shares one host shape, a fixed summary row and then that subtab's
        // own filter, table or list, so the tab strip and the content underneath it stay at the
        // same origin while the user switches between All, First Sync, Conflicts and Problems.
        var list = new StackPanel();
        var problems = new StackPanel();
        var allSummary = AttentionSummaryText();
        var problemSummary = AttentionSummaryText();
        var problemSlot = new Grid { Height = AttentionSummaryRowHeight };
        problemSlot.Children.Add(problemSummary);
        _attentionList = list;
        _attentionProblems = problems;
        _attentionAllSummary = allSummary;
        _attentionProblemSummary = problemSummary;
        _attentionProblemSlot = problemSlot;
        // The decorative description the First Sync subtab inherited moves to help only, and the
        // existing status lines move into the fixed rows that align the subtabs.
        System.Windows.Automation.AutomationProperties.SetHelpText(BootstrapPage, BootstrapSubtitle.Text);
        Detach(BootstrapSubtitle);
        var bootstrapSummary = PrepareAttentionSummary(Detach(BootstrapSummary));
        var conflictSummary = PrepareAttentionSummary(Detach(ConflictSelectionSummary));
        var tabs = new TabControl { ItemContainerStyle = WorkspaceTabStyle(), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        _attentionTabs = tabs;
        tabs.Items.Add(new TabItem { Header = "All", Content = AttentionSubtabHost(allSummary,
            new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }) });
        tabs.Items.Add(new TabItem { Header = "First Sync", Content = AttentionSubtabHost(bootstrapSummary, Detach(BootstrapPage)) });
        tabs.Items.Add(new TabItem { Header = "Conflicts", Content = AttentionSubtabHost(conflictSummary, Detach(ConflictsPage)) });
        tabs.Items.Add(new TabItem { Header = "Problems", Content = problems });
        tabs.SelectionChanged += async (_, e) =>
        {
            if (!ReferenceEquals(e.Source, tabs)) return;
            if (tabs.SelectedIndex == 1 && _fullAppReady && !SuppressLoadedStartupForTest)
            {
                try { await RefreshBootstrapReviewAsync(); }
                catch (Exception exception)
                { ApproveBootstrapButton.IsEnabled = false; BootstrapSummary.Text = "The first-sync plan could not be loaded: " + exception.Message; }
            }
            _attentionFingerprint = string.Empty; RefreshFullApp();
        };
        BootstrapPage.Visibility = ConflictsPage.Visibility = Visibility.Visible;
        RemoveEmbeddedHeading(BootstrapPage); RemoveEmbeddedHeading(ConflictsPage);
        var attentionAccount = BuildWorkspaceAccountPicker("Needs Attention");
        SharedPresentation.ApplyQuietDropdowns(attentionAccount);
        var help = BuildAttentionHelpButton();
        BuildAttentionGuidance(help);
        var attentionBody = new Grid();
        attentionBody.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        attentionBody.Children.Add(tabs);
        _attentionPage = new Border { Child = PageShell("Needs Attention", "Decisions and problems that need your input", attentionBody, attentionAccount, [help]), Visibility = Visibility.Collapsed };
        var outer = (Grid)DashboardPage.Parent; Grid.SetRow(_attentionPage, 1); outer.Children.Add(_attentionPage);
        _homeAttention = new StackPanel();
        var header = new DockPanel();
        var all = Button("View All", (_, _) => NavigateTo("Attention"), true); DockPanel.SetDock(all, Dock.Right); header.Children.Add(all);
        header.Children.Add(Text("Needs Attention", 17, true)); _homeAttention.Children.Add(header);
        var card = Card(_homeAttention); card.Margin = new Thickness(0, 0, 0, 18);
        _homeSupport.Children.Insert(0, card);
        if (_homeSupport.Children.Count > 2) _homeSupport.Children.RemoveAt(2);
    }

    // UI13: the fixed summary row is what keeps every subtab's filter, table or list at the same
    // origin. The full text stays reachable through the tooltip and the accessible help text.
    private static TextBlock AttentionSummaryText()
    {
        var summary = Text(string.Empty, 13, brush: "MutedInk");
        summary.TextWrapping = TextWrapping.NoWrap;
        summary.TextTrimming = TextTrimming.CharacterEllipsis;
        summary.VerticalAlignment = VerticalAlignment.Center;
        return summary;
    }

    private static TextBlock PrepareAttentionSummary(TextBlock summary)
    {
        summary.Margin = new Thickness(0);
        summary.TextWrapping = TextWrapping.NoWrap;
        summary.TextTrimming = TextTrimming.CharacterEllipsis;
        summary.VerticalAlignment = VerticalAlignment.Center;
        BindingOperations.SetBinding(summary, ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = summary });
        BindingOperations.SetBinding(summary, System.Windows.Automation.AutomationProperties.HelpTextProperty,
            new Binding(nameof(TextBlock.Text)) { Source = summary });
        return summary;
    }

    private static Grid AttentionSubtabHost(TextBlock summary, UIElement body)
    {
        var host = new Grid();
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(AttentionSummaryRowHeight) });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        host.Children.Add(summary);
        Grid.SetRow(body, 1);
        host.Children.Add(body);
        return host;
    }

    // UI13: one keyboard-reachable question-mark control that offers the conflict guidance from
    // every subtab instead of a guidance block that pushed the Conflicts table down.
    private Button BuildAttentionHelpButton()
    {
        var help = new Button
        {
            Content = new TextBlock { Text = "?", FontSize = 15, FontWeight = FontWeights.SemiBold },
            Width = 32, Height = 32, Padding = new Thickness(0), Margin = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "How to resolve conflicts"
        };
        System.Windows.Automation.AutomationProperties.SetName(help, "Conflict guidance");
        System.Windows.Automation.AutomationProperties.SetHelpText(help,
            "Opens the conflict guidance. Press Enter or Space to open it and Escape to close it.");
        help.Click += (_, _) => ShowConflictGuidance();
        return help;
    }

    private void BuildAttentionGuidance(Button anchor)
    {
        // The existing guidance control moves out of the Conflicts subtab into a flyout, so the
        // text is reused rather than duplicated and the table above keeps the common origin.
        ConflictGuidanceExpander.IsExpanded = true;
        ConflictGuidanceExpander.Margin = new Thickness(0);
        var body = new StackPanel { MaxWidth = 400 };
        body.Children.Add(Text("Conflict guidance", 15, true));
        body.Children.Add(Detach(ConflictGuidanceExpander));
        var close = Button("Close", (_, _) => CloseConflictGuidance(), true);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(close);
        var card = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Padding = new Thickness(16), Child = body };
        card.SetResourceReference(Border.BackgroundProperty, "RaisedSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            CloseConflictGuidance();
            e.Handled = true;
        };
        _attentionGuidancePopup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = card
        };
        _attentionGuidancePopup.Opened += (_, _) => close.Focus();
    }

    private void ShowConflictGuidance()
    {
        if (_attentionGuidancePopup is null) return;
        _attentionGuidancePopup.IsOpen = false;
        _attentionGuidancePopup.IsOpen = true;
    }

    private void CloseConflictGuidance()
    {
        if (_attentionGuidancePopup is not null) _attentionGuidancePopup.IsOpen = false;
    }

    // Test surface: the flyout state and the content it hosts, so the fixture can prove the
    // reused guidance without opening a second window of its own.
    internal bool ConflictGuidanceOpen
    {
        get => _attentionGuidancePopup?.IsOpen == true;
        set { if (_attentionGuidancePopup is not null) _attentionGuidancePopup.IsOpen = value; }
    }

    internal UIElement? ConflictGuidanceContent => _attentionGuidancePopup?.Child;

    private static void RemoveEmbeddedHeading(Border page)
    {
        if (page.Child is not Grid grid) return;
        foreach (var element in grid.Children.OfType<FrameworkElement>().Where(child => Grid.GetRow(child) == 0))
            if (element is TextBlock text && text.FontSize >= 20) element.Visibility = Visibility.Collapsed;
        // UI13: a header row that now holds only hidden text must not reserve space, or the
        // embedded page would start below the other subtabs.
        foreach (var header in grid.Children.OfType<StackPanel>().Where(child => Grid.GetRow(child) == 0))
            if (header.Children.OfType<UIElement>().All(child => child is not FrameworkElement element ||
                    element.Visibility != Visibility.Visible))
                header.Margin = new Thickness(0);
    }

    // UIV3: an attention row is either an internal Sync Shark entity, which carries its own explicit
    // role and state descriptor, or a real file conflict, which keeps the Windows file-type
    // presentation of its own actual filename. The icon is never chosen by matching the row's title.
    internal sealed record WorkspaceAttentionCase(PairRow Pair, string Title, string Description,
        InternalAttentionIcon? InternalIcon, Action Open, bool IsProblem = false, string? RunId = null)
    {
        /// <summary>True when this row is a real file conflict rather than an internal entity.</summary>
        internal bool IsRealFile => InternalIcon is null;
    }

    // All three surfaces use the same decisions and problem predicates, not just the pair's error flag.
    internal IReadOnlyList<WorkspaceAttentionCase> TrayAttentionCases => PresentationAttentionCases;
    internal IReadOnlyList<SyncCliAttentionItemData> CliAttentionItems => PresentationAttentionCases
        .Select(item => new SyncCliAttentionItemData(item.Pair.Id, item.Title, item.Description, item.IsProblem,
            item.RunId)).ToArray();

    private IEnumerable<WorkspaceAttentionCase> BuildAttentionCases()
    {
        foreach (var pair in PairRows)
        {
            if (_commandProblemsByPair.TryGetValue(pair.Id, out var commandProblem))
                yield return new(pair, commandProblem.Title, commandProblem.Description,
                    InternalAttentionIcon.PairProblem,
                    () => OpenRetainedRunLog(commandProblem.PairId, commandProblem.CommandId), true,
                    commandProblem.CommandId);
            var first = _pairSet.Pairs.FirstOrDefault(value => value.Id == pair.Id)?.InitialReviewApproved == false;
            var conflicts = (_attentionConflictRows.Count > 0 ? _attentionConflictRows : ConflictRows.AsEnumerable())
                .Where(row => row.PairId == pair.Id).ToArray();
            var blocked = PairHealthBlockingReason(pair.Id) is not null;
            // ENG4: a retained typed first-sync problem is its own case in All and Problems, and it links the
            // exact retained run so the reason, the effect ledger and the outcome all name the same command.
            if (_firstSyncProblemsByPair.TryGetValue(pair.Id, out var firstSyncProblem))
                yield return new(pair, "First sync needs attention",
                    firstSyncProblem.Message + " " + firstSyncProblem.NextAction + " Run " +
                    firstSyncProblem.CommandId + ".",
                    InternalAttentionIcon.PairProblem, () => OpenFirstSyncProblemRun(firstSyncProblem), true,
                    firstSyncProblem.CommandId);
            if (first && !blocked) yield return new(pair, "Review the First Sync", "Review the proposed changes and excluded folders.",
                InternalAttentionIcon.FirstSyncReview, () => OpenFirstSyncReview(pair.Id));
            foreach (var conflict in conflicts)
                yield return new(pair, Path.GetFileName(conflict.Path), conflict.Reason, null,
                    () => { IncludeAttentionPair(pair.Id); NavigateTo("Conflicts"); ConflictGrid.SelectedItem = conflict; });
            // The progress callback and retained command describe the same failure. Do not
            // count that one failure twice; a different run, monitor or folder-health problem
            // must still remain independently actionable.
            var definition = _pairSet.Pairs.FirstOrDefault(value => value.Id == pair.Id);
            var runProblemIsRetained = definition is not null &&
                _runAttentionReasonByPair.TryGetValue(pair.Id, out var runReason) &&
                (firstSyncProblem is not null &&
                 string.Equals(runReason, HumanizePairFailure(definition, firstSyncProblem.Message), StringComparison.Ordinal) ||
                 commandProblem is not null &&
                 string.Equals(runReason, HumanizePairFailure(definition, commandProblem.Message), StringComparison.Ordinal));
            var independentProblem = IsPairKnownUnhealthy(pair.Id) ||
                                     _monitorAttentionReasonByPair.ContainsKey(pair.Id) ||
                                     _runAttentionReasonByPair.ContainsKey(pair.Id) && !runProblemIsRetained;
            if (pair.NeedsAttention && (blocked || independentProblem ||
                firstSyncProblem is null && commandProblem is null && !first && conflicts.Length == 0))
                yield return new(pair, pair.StatusSummary, pair.AttentionReason, InternalAttentionIcon.PairProblem,
                    () => OpenPairStatus(pair), true);
        }
    }

    private void RefreshFullApp()
    {
        if (!_fullAppReady || _attentionList is null || _attentionTabs is null || _attentionProblems is null ||
            _attentionProblemSlot is null || _attentionAllSummary is null || _attentionProblemSummary is null) return;
        // SS-D12: the scope is joined only after the existing not-ready guards. The change fingerprint below
        // is derived once from the exact inputs this pass reads, and the attention result is materialised once
        // by the shared scope instead of being rebuilt before and after that comparison.
        using var presentation = OpenPresentationScope();
        var attentionCases = PresentationAttentionCases;
        var signature = string.Join('|', PairRows.Select(pair => pair.Id + pair.Status + pair.AttentionReason)) +
                        string.Join('|', _pairSet.Pairs.Select(pair => pair.Id + pair.InitialReviewApproved)) +
                        // R474-4: the retained problem's own identity and content invalidate this cache, so a
                        // replaced or resolved problem can never leave a stale attention reason on screen.
                        string.Join('|', _firstSyncProblemsByPair.OrderBy(item => item.Key, StringComparer.Ordinal)
                            .Select(item => item.Key + item.Value.Code + item.Value.CommandId + item.Value.Message)) +
                        string.Join('|', _commandProblemsByPair.OrderBy(item => item.Key, StringComparer.Ordinal)
                            .Select(item => item.Key + item.Value.Code + item.Value.CommandId + item.Value.Message + item.Value.NextAction)) +
                        string.Join('|', ConflictRows.Select(row => row.PairId + row.Path + row.Reason)) +
                        // SS-D12: every remaining input the case set depends on is part of the same comparison,
                        // so a changed folder, monitor, run or attention-row problem cannot leave a stale row.
                        string.Join('|', _attentionConflictRows.Select(row => row.PairId + row.Path + row.Reason)) +
                        string.Join('|', _pairSet.Pairs.Select(pair => pair.Id + PairHealthBlockingReason(pair.Id) +
                            IsPairKnownUnhealthy(pair.Id))) +
                        string.Join('|', _monitorAttentionReasonByPair.OrderBy(item => item.Key, StringComparer.Ordinal)
                            .Select(item => item.Key + item.Value)) +
                        string.Join('|', _runAttentionReasonByPair.OrderBy(item => item.Key, StringComparer.Ordinal)
                            .Select(item => item.Key + item.Value)) +
                        _attentionTabs.SelectedIndex + _workspaceAccountId;
        if (_attentionFingerprint == signature) return;
        _attentionFingerprint = signature;
        RefreshAttentionPairChoices();
        _attentionList.Children.Clear();
        _attentionProblems!.Children.Clear();
        _attentionProblems.Children.Add(_attentionProblemSlot!);
        if (_homeAttention is not null) while (_homeAttention.Children.Count > 1) _homeAttention.Children.RemoveAt(1);
        var count = 0;
        var problemCount = 0;
        foreach (var item in attentionCases.Where(item => ScopeMatches(item.Pair.Id)))
        {
            AddCase(item.Title, item.Description, item.Pair, item.InternalIcon, item.Open);
            if (item.IsProblem)
            {
                _attentionProblems.Children.Add(CaseRow(item.Title, item.Description, item.Pair, item.InternalIcon, item.Open));
                problemCount++;
            }
        }
        // UI13: the list subtabs report their own count in the shared summary row, so their first
        // row starts where the First Sync and Conflicts status lines end.
        _attentionAllSummary!.Text = count == 0
            ? "Nothing needs your attention. Excluded folders and routine checks stay out of this list."
            : count == 1 ? "1 item needs your attention." : count.ToString("N0") + " items need your attention.";
        _attentionProblemSummary!.Text = problemCount == 0
            ? "No confirmed folder or connection problems."
            : problemCount == 1 ? "1 confirmed problem needs your attention."
            : problemCount.ToString("N0") + " confirmed problems need your attention.";
        if (_homeAttention?.Children.Count == 1) _homeAttention.Children.Add(Text("All clear", 13, brush: "SuccessBrush"));

        void AddCase(string title, string description, PairRow pair, InternalAttentionIcon? internalIcon, Action action)
        {
            _attentionList.Children.Add(CaseRow(title, description, pair, internalIcon, action)); count++;
            if (_homeAttention is not null && _homeAttention.Children.Count < 4)
            {
                var button = Button(title, (_, _) => action(), true);
                button.Margin = new Thickness(0, 8, 0, 0); button.Padding = new Thickness(0, 8, 0, 8);
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                var row = new DockPanel();
                var glyph = SharedPresentation.AttentionIcon.Create(internalIcon, title, 22);
                glyph.Margin = new Thickness(0, 0, 12, 0); row.Children.Add(glyph);
                var words = new StackPanel(); words.Children.Add(Text(title, 13, brush: "FocusBrush"));
                var subtitle = Text(pair.Name + (_accountLabels.Count > 1 ? "\n" + pair.Connection : ""), 12, brush: "MutedInk"); subtitle.Margin = new Thickness(0, 4, 0, 0);
                words.Children.Add(subtitle); row.Children.Add(words); button.Content = row;
                _homeAttention.Children.Add(button);
            }
        }
    }

    private static FrameworkElement CaseRow(string title, string description, PairRow pair,
        InternalAttentionIcon? internalIcon, Action action)
    {
        var dock = new DockPanel { Margin = new Thickness(12, 16, 12, 16) };
        var glyph = SharedPresentation.AttentionIcon.Create(internalIcon, title, 26);
        glyph.Margin = new Thickness(0, 0, 16, 0); DockPanel.SetDock(glyph, Dock.Left); dock.Children.Add(glyph);
        var open = IconButton("Open " + title, "back", (_, _) => action()); open.RenderTransform = new RotateTransform(180, 17, 17);
        DockPanel.SetDock(open, Dock.Right); dock.Children.Add(open);
        var words = new StackPanel();
        var name = Button(title, (_, _) => action(), true); name.Content = Text(title, 14, true); name.Padding = new Thickness(0); name.HorizontalContentAlignment = HorizontalAlignment.Left;
        words.Children.Add(name); words.Children.Add(Text(ProblemPreview(description), 12, brush: "MutedInk"));
        var context = Text(pair.Name + "\nAccount: " + pair.Connection, 12, brush: "FocusBrush"); context.Margin = new Thickness(0, 6, 0, 0); words.Children.Add(context);
        dock.Children.Add(words);
        var row = new Border { Child = dock, BorderThickness = new Thickness(0, 0, 0, 1) }; row.SetResourceReference(Border.BorderBrushProperty, "Divider"); return row;
    }

    private void OpenPairStatus(PairRow pair)
    { NavigateTo("Pairs"); PairList.SelectedItem = pair; ExpandPairRow(pair); PairEditorTabs.SelectedIndex = 3; }

    private void StabilizeActivityTabs()
    {
        if (_activityToolbar is null) return;
        // Reparenting the shared toolbar must not clear its bound selectors between tabs.
        _activityToolbar.DataContext = this;
        ActivityPairBox.Width = 240;
        ActivityPairBox.SetBinding(ToolTipProperty, new Binding("SelectedItem.DisplayName") { Source = ActivityPairBox });
        // The Activity surface consumes the shared quiet search and dropdown surfaces.
        ActivitySearchBox.Style = SharedPresentation.QuietTextBoxStyle();
        QueueSearchBox.Style = SharedPresentation.QuietTextBoxStyle();
        foreach (var dropdown in new[] { ActivityPairBox, ActivityFilterBox, ActivityTypeFilter, ActivityDateBox, QueueFilterBox })
            dropdown.Style = SharedPresentation.QuietComboBoxStyle();
        Detach(_activityToolbar);
        var pendingGrid = ((Grid)QueuePage.Child).Children.OfType<DataGrid>().Single();
        Detach(pendingGrid); var pending = new DockPanel();
        var foot = new StackPanel(); foot.Children.Add(Detach(QueuePageSummary)); foot.Children.Add(Detach(LoadMoreQueueButton));
        LoadMoreQueueButton.HorizontalAlignment = HorizontalAlignment.Left;
        DockPanel.SetDock(foot, Dock.Bottom); pending.Children.Add(foot); pending.Children.Add(pendingGrid); QueuePage.Child = pending;
        foreach (TabItem item in _activityTabs.Items)
        {
            if (item.Header?.ToString() == "Event log") item.Header = "Event Log";
            var content = (UIElement)item.Content; item.Content = null;
            var host = new Grid(); host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(content, 1); host.Children.Add(content); item.Content = host; _activityContents[item] = host;
        }
        PositionActivityToolbar();
        ActivitySearchBox.TextChanged += (_, _) =>
        { if (_activityTabs.SelectedIndex == 1) QueueSearchBox.Text = ActivitySearchBox.Text; };
        ActivityPairBox.SelectionChanged += (_, _) => ApplyPendingViewFilter();
        _activityTabs.SelectionChanged += (_, e) =>
        { if (ReferenceEquals(e.Source, _activityTabs)) { PositionActivityToolbar(); ApplyPendingViewFilter(); } };
        var queueView = CollectionViewSource.GetDefaultView(QueueRows);
        queueView.Filter = value => value is not QueueRow row ||
            ScopeMatches(row.PairId) &&
            (ActivityPairBox.SelectedItem is not ActivityPairChoice { Id: { } id } || row.PairId == id);
    }

    private void PositionActivityToolbar()
    {
        if (_activityToolbar is null || _activityTabs.SelectedItem is not TabItem selected || !_activityContents.TryGetValue(selected, out var host)) return;
        Detach(_activityToolbar); Grid.SetRow(_activityToolbar, 0); host.Children.Add(_activityToolbar);
        var pending = _activityTabs.SelectedIndex == 1;
        var runs = _activityTabs.SelectedIndex == 2;
        ActivityFilterBox.Visibility = pending || runs ? Visibility.Collapsed : Visibility.Visible;
        ActivityTypeFilter.Visibility = runs ? Visibility.Collapsed : Visibility.Visible;
        foreach (var check in _activityExtraFilters.Children.OfType<System.Windows.Controls.CheckBox>())
            check.Visibility = pending || runs ? Visibility.Collapsed : Visibility.Visible;
        // UI07/UI09: the category, severity, entity, run and no-change controls describe the unified
        // activity list only. Pending and Runs keep their own filters and summaries, and the retired
        // Event Log tab's live session messages stay reachable through the unified list's Diagnostics.
        var unified = !pending && !runs;
        if (_activityCategoryBar is not null)
            _activityCategoryBar.Visibility = unified ? Visibility.Visible : Visibility.Collapsed;
        if (_activitySeverityBox is not null)
            _activitySeverityBox.Visibility = unified ? Visibility.Visible : Visibility.Collapsed;
        if (_activityEntityBox is not null)
            _activityEntityBox.Visibility = unified ? Visibility.Visible : Visibility.Collapsed;
        if (_activityRunFilterSurface is not null)
            _activityRunFilterSurface.Visibility = unified ? Visibility.Visible : Visibility.Collapsed;
        if (_activityNoChangesToggle is not null)
            _activityNoChangesToggle.Visibility = unified ? Visibility.Visible : Visibility.Collapsed;
        if (unified) UpdateActivitySessionMessages();
        if (QueueFilterBox.Parent is not WrapPanel panel || !ReferenceEquals(panel, _activityExtraFilters))
            _activityExtraFilters.Children.Add(Detach(QueueFilterBox));
        QueueFilterBox.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        SyncActivityViewSwitch();
    }

    private void ApplyPendingViewFilter()
    {
        if (!_fullAppReady) return;
        CollectionViewSource.GetDefaultView(QueueRows).Refresh();
        if (_activityTabs.SelectedIndex == 1)
        {
            var count = CollectionViewSource.GetDefaultView(QueueRows).Cast<object>().Count();
            ActivityPageSummary.Text = count == 0 ? "No unfinished operations in this view." : $"{count:N0} matching unfinished operation(s)";
        }
    }

    private void BuildFullApp()
    {
        BuildIgnoreRules();
        BuildAttentionPage();
        BuildAccountSharingPage();
        BuildStoragePage();
        BuildPairStorageTab();
        Navigation.SelectionChanged -= Navigation_SelectionChanged;
        var existing = Navigation.Items.OfType<ListBoxItem>().ToDictionary(item => item.Tag!.ToString()!);
        Navigation.Items.Clear();
        foreach (var key in new[] { "Dashboard", "Activity", "Pairs", "Attention", "Recovery", "Storage", "Sharing" })
            Navigation.Items.Add(existing.TryGetValue(key, out var item) ? item : new ListBoxItem { Tag = key });
        foreach (var key in new[] { "Bootstrap", "Queue", "Conflicts", "Rewind" }) Navigation.Items.Add(existing[key]);
        _utilityNavigation = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Margin = new Thickness(10, 8, 10, 0), Resources = Navigation.Resources };
        ScrollViewer.SetHorizontalScrollBarVisibility(_utilityNavigation, ScrollBarVisibility.Disabled);
        _utilityNavigation.Items.Add(existing["Connections"]);
        _utilityNavigation.Items.Add(existing["Settings"]);
        _utilityNavigation.SelectionChanged += (_, _) =>
        {
            if (_utilityNavigation.SelectedItem is ListBoxItem selected) NavigateTo(selected.Tag!.ToString()!);
        };
        var navigationGrid = (Grid)NavigationBorder.Child;
        var utilities = new StackPanel();
        utilities.Children.Add(_utilityNavigation);
        utilities.Children.Add(Detach(SidebarToggle));
        Grid.SetRow(utilities, 1); navigationGrid.Children.Add(utilities);
        Navigation.SelectionChanged += Navigation_SelectionChanged;
        Navigation.SelectedItem = existing["Dashboard"];
        BuildStructuredInspector();
        BuildRunHistory();
        StabilizeActivityTabs();
        LoadViewModes();
        BuildConnectionPage();
        BuildSettingsDraftPage();
        ModernizePairPage();
        ModernizeDecisionPages();
        BuildRecoveryTabs();
        var contextStyle = new Style(typeof(ContextMenu), (Style)FindResource(typeof(ContextMenu)));
        contextStyle.Setters.Add(new Setter(Control.TemplateProperty, WorkspaceMenu().Template));
        System.Windows.Application.Current.Resources[typeof(ContextMenu)] = contextStyle;
        _fullAppReady = true;
        UpdateSidebarPresentation();
        // An expired deadline written before the last shutdown is cleared as soon as the app is up.
        Loaded += async (_, _) => { DecorateCommands(this); await LoadRunHistoryAsync(true); await CheckPauseDeadlineAsync(); };
        RefreshFullApp();
    }

    private IEnumerable<ListBoxItem> SidebarItems() => Navigation.Items.OfType<ListBoxItem>()
        .Concat(_utilityNavigation?.Items.OfType<ListBoxItem>() ?? []);

    private void ModernizeDecisionPages()
    {
        foreach (var page in new[] { BootstrapPage, ConflictsPage })
            if (page.Child is Grid grid)
                foreach (var header in grid.Children.OfType<StackPanel>().Where(child => Grid.GetRow(child) == 0))
                    foreach (var text in header.Children.OfType<TextBlock>().Where(text => text.FontSize >= 20)) text.Visibility = Visibility.Collapsed;
        var action = BootstrapGrid.Columns[2]; BootstrapGrid.Columns.Clear();
        BootstrapGrid.RowHeight = 78; BootstrapGrid.BorderThickness = new Thickness(0);
        ScrollViewer.SetHorizontalScrollBarVisibility(BootstrapGrid, ScrollBarVisibility.Disabled);
        BootstrapGrid.Columns.Add(new DataGridTemplateColumn { Header = "Item", Width = new DataGridLength(1, DataGridLengthUnitType.Star), CellTemplate = RowTemplate("""
          <DockPanel Margin="8"><local:ShellTypeImage Width="28" Height="28" Margin="0,0,12,0"/>
          <StackPanel VerticalAlignment="Center"><TextBlock Text="{Binding Path}" FontSize="14" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
          <TextBlock Text="{Binding DisplayGroup}" Foreground="{DynamicResource MutedInk}" FontSize="12"/><TextBlock Text="{Binding Effect}" FontSize="12" Foreground="{DynamicResource MutedInk}" TextTrimming="CharacterEllipsis"/></StackPanel></DockPanel>
          """) });
        BootstrapGrid.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding("ItemKind"), Width = 70 });
        BootstrapGrid.Columns.Add(new DataGridTextColumn { Header = "Format", Binding = new Binding("FileType"), Width = 80 });
        action.Width = 165; BootstrapGrid.Columns.Add(action);
        BootstrapGrid.RowDetailsTemplate = RowTemplate("""
          <Grid Margin="50,8,15,14"><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition/></Grid.ColumnDefinitions>
            <StackPanel><TextBlock Text="This Computer" FontWeight="SemiBold"/><TextBlock Text="{Binding LocalSize,StringFormat=Size: {0}}"/><TextBlock Text="{Binding LocalModified,StringFormat=Modified: {0}}"/></StackPanel>
            <StackPanel Grid.Column="1"><TextBlock Text="{Binding CloudName}" FontWeight="SemiBold"/><TextBlock Text="{Binding DropboxSize,StringFormat=Size: {0}}"/><TextBlock Text="{Binding DropboxModified,StringFormat=Modified: {0}}"/></StackPanel>
          </Grid>
          """);
        BootstrapGrid.RowDetailsVisibilityMode = DataGridRowDetailsVisibilityMode.VisibleWhenSelected;
        if (BootstrapGrid.Parent is Grid firstGrid)
        {
            var bar = firstGrid.Children.OfType<Grid>().First(child => Grid.GetRow(child) == 1);
            var extra = bar.Children.OfType<WrapPanel>().First(child => Grid.GetRow(child) == 1);
            var expander = new Expander { Header = CommandIcons.Label("Filters and Bulk Actions", "filter"), Content = Detach(extra), Margin = new Thickness(0,10,0,0) };
            Grid.SetRow(expander,1); Grid.SetColumnSpan(expander,6); bar.Children.Add(expander);
        }
        var noticeOptionsColumn = ConflictGrid.Columns.OfType<DataGridTemplateColumn>()
            .Single(column => Equals(column.Header, "Options"));
        ConflictGrid.Columns.Clear(); ConflictGrid.BorderThickness = new Thickness(0);
        ConflictGrid.RowHeight = double.NaN; ConflictGrid.MinRowHeight = 76;
        ConflictGrid.Columns.Add(noticeOptionsColumn);
        ScrollViewer.SetHorizontalScrollBarVisibility(ConflictGrid, ScrollBarVisibility.Auto);
        ConflictGrid.Columns.Add(new DataGridTemplateColumn { Header = "Item", Width = new DataGridLength(1,DataGridLengthUnitType.Star), CellTemplate = RowTemplate("""
          <DockPanel Margin="8"><local:ShellTypeImage Width="25" Height="25" Margin="0,0,12,0"/><StackPanel>
          <TextBlock Text="{Binding Path}" FontSize="14" FontWeight="SemiBold"/><TextBlock Text="{Binding PairName}" FontSize="12" Foreground="{DynamicResource FocusBrush}"/>
          <TextBlock Text="{Binding Reason}" FontSize="12" Foreground="{DynamicResource MutedInk}" TextTrimming="CharacterEllipsis"/></StackPanel></DockPanel>
          """) });
        ConflictGrid.Columns.Add(new DataGridTextColumn { Header = "Comparison", Binding = new Binding("GroupDisplay"), Width = 160 });
        ConflictGrid.Columns.Add(new DataGridTemplateColumn { Width = 138, CellTemplate = RowTemplate("<Button Tag='compare' ToolTip='Compare both versions' AutomationProperties.Name='Compare Both Versions' Background='Transparent' BorderThickness='0' Padding='10,6' Margin='2'><StackPanel Orientation='Horizontal'><local:CommandGlyph IconName='search' Width='16' Height='16' Margin='0,0,7,0'/><TextBlock Text='Compare' VerticalAlignment='Center'/></StackPanel></Button>") });
        ConflictGrid.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        { if (FindAncestor<Button>(e.OriginalSource as DependencyObject)?.DataContext is DecisionRow row) { ConflictGrid.SelectedItem = row; CompareConflict_Click(ConflictGrid,new RoutedEventArgs()); e.Handled=true; } }));
        ConflictGrid.ContextMenu.Template = WorkspaceMenu().Template; ConflictGrid.ContextMenu.ItemContainerStyleSelector = WorkspaceMenu().ItemContainerStyleSelector;
        foreach (var item in ConflictGrid.ContextMenu.Items.OfType<MenuItem>()) DecorateCommands(item);
        if (ConflictSearchBox.Parent is System.Windows.Controls.Panel conflictTools)
        {
            var searchIndex = conflictTools.Children.IndexOf(ConflictSearchBox);
            var search = SharedPresentation.QuietSearchField(Detach(ConflictSearchBox), "Search Conflicts", "Search conflicts…");
            search.Margin = new Thickness(0, 0, 8, 0);
            conflictTools.Children.Insert(searchIndex, search);
            foreach (var control in conflictTools.Children.OfType<FrameworkElement>())
                control.VerticalAlignment = VerticalAlignment.Top;
        }
    }

    private static string PageIcon(string? key) => key switch
    {
        "Dashboard" => "home", "Activity" or "Queue" => "activity", "Pairs" => "folder",
        "Attention" or "Conflicts" or "Bootstrap" => "attention", "Recovery" or "Rewind" => "history",
        "Sharing" => "link", "Storage" => "computer", "Connections" => "cloud", "Settings" => "settings", _ => "info"
    };

    internal static void DecorateCommands(DependencyObject root)
    {
        if (root is Button button && button.Content is string label && label.Length > 1 &&
            button.Style != System.Windows.Application.Current.TryFindResource("CaptionButton"))
        {
            var clean = label.Trim(' ', '＋', '↗', '→', '←', '×');
            button.Content = CommandIcons.Label(System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(clean));
            // Decorative command labels must not replace an explicit accessible
            // name or break its live provider/account binding.
            if (!System.Windows.Data.BindingOperations.IsDataBound(button,
                    System.Windows.Automation.AutomationProperties.NameProperty) &&
                string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(button)))
                System.Windows.Automation.AutomationProperties.SetName(button, clean);
            button.ToolTip ??= clean;
        }
        if (root is MenuItem menu && menu.Header is string title)
        { menu.Header = CommandIcons.Label(title); System.Windows.Automation.AutomationProperties.SetName(menu, title); }
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().ToArray())
            DecorateCommands(child);
    }

    private void BuildStructuredInspector()
    {
        var details = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        // UIV6-H1: the one shared action strip keeps Expand then Close in that order here, and the
        // shared registration below hides the whole strip while the larger window is open.
        var detailsChrome = DetailsChrome.ActionStrip("Details", (_, _) => _detailsHost?.Expand(this),
            (_, _) => CloseWorkspaceDetails(), expandElementName: "DetailsExpandButton");
        detailsChrome.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(detailsChrome, Dock.Right); header.Children.Add(detailsChrome);
        _inspectorTypeIcon = new ShellTypeImage { Width = 24, Height = 24, Margin = new Thickness(0, 3, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(_inspectorTypeIcon, Dock.Left); header.Children.Add(_inspectorTypeIcon);
        var identity = new StackPanel();
        _workspaceDetailTitle = Text("Item Details", 18, true); identity.Children.Add(_workspaceDetailTitle);
        _detailSubtitle = Text("", 12, brush: "MutedInk");
        _detailSubtitle.Margin = new Thickness(0, 5, 0, 0); identity.Children.Add(_detailSubtitle);
        header.Children.Add(identity); details.Children.Add(header);
        _detailFields = new StackPanel(); details.Children.Add(_detailFields);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) };
        _inspectorLocal = IconButton("Show in File Explorer", "folder", (_, _) =>
        { if (_inspectedActivity is { } row) OpenLocalItem(row); });
        _inspectorCloud = IconButton("Open in " + ActivityCloud.UnrecordedLabel, "external", (_, _) =>
        { if (_inspectedActivity is { } row) OpenCloudItem(row); });
        _inspectorLink = IconButton("Copy " + ActivityCloud.UnrecordedLabel + " Location Link", "link", (_, _) =>
        { if (_inspectedActivity is { } row) CopyCloudItemUrl(row); });
        actions.Children.Add(_inspectorLocal); actions.Children.Add(_inspectorCloud); actions.Children.Add(_inspectorLink);
        details.Children.Add(actions);
        TechnicalText.Margin = new Thickness(0, 10, 0, 0); TechnicalText.Padding = new Thickness(0);
        details.Children.Add(new Expander { Header = "Technical Details", Content = Detach(TechnicalText) });
        _workspaceInspector.Padding = new Thickness(20);
        var detailsBody = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var detailsSidebarHost = new ContentPresenter { HorizontalAlignment = HorizontalAlignment.Stretch };
        _detailsHost = new DetailsHost(detailsBody, detailsSidebarHost);
        // The dialog has its own return control; the shared registration hides the sidebar's own
        // expansion and dismissal controls while it is open, so no second expansion or accidental
        // dismissal is offered and this surface keeps no duplicate visibility rule.
        _detailsHost.RegisterSidebarOnly(detailsChrome);
        _workspaceInspector.Child = detailsSidebarHost;
    }

    internal static Button IconButton(string label, string icon, RoutedEventHandler action)
    {
        var button = Button(label, action, quiet: true); button.Content = CommandIcons.Icon(icon, 18);
        button.Width = 34; button.Height = 34; button.Padding = new Thickness(7); return button;
    }

    private static FrameworkElement Field(string label, UIElement value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        var heading = Text(label.TrimEnd(':') + ":", 12, brush: "MutedInk"); heading.Margin = new Thickness(0, 0, 0, 5);
        panel.Children.Add(heading); panel.Children.Add(value); return panel;
    }

    private void RefreshStructuredInspector(ActivityRow row)
    {
        if (_detailFields is null || _detailSubtitle is null) return;
        if (_inspectorTypeIcon is not null) _inspectorTypeIcon.DataContext = row;
        if (_inspectorLocal is not null) _inspectorLocal.IsEnabled = LocalItemPath(row) is not null;
        if (_inspectorCloud is not null) _inspectorCloud.IsEnabled = CanResolveCloudItem(row);
        if (_inspectorLink is not null) _inspectorLink.IsEnabled = CanResolveCloudItem(row);
        // P37: the inspector names the provider the row's exact binding resolves to, so its tooltips
        // and accessibility names always match the account the row actually belongs to.
        var cloudName = ActivityCloudName(row);
        if (_inspectorCloud is not null)
        {
            _inspectorCloud.ToolTip = "Open in " + cloudName;
            System.Windows.Automation.AutomationProperties.SetName(_inspectorCloud, "Open in " + cloudName);
        }
        if (_inspectorLink is not null)
        {
            _inspectorLink.ToolTip = "Copy " + cloudName + " Location Link";
            System.Windows.Automation.AutomationProperties.SetName(_inspectorLink, "Copy " + cloudName + " Location Link");
        }
        _detailSubtitle.Text = WithExactProviderBinding(row).HumanAction;
        _detailFields.Children.Clear();
        // Present retained event evidence in the shared visible inspector, not only the legacy text view.
        if (!string.IsNullOrWhiteSpace(row.Reason))
            _detailFields.Children.Add(Field("What happened", Text(row.Reason, 13)));
        if (!string.IsNullOrWhiteSpace(row.RunId))
            _detailFields.Children.Add(Field("Run", Text(row.RunId, 13)));
        if (row.Severity is "Error" or "Warning")
            _detailFields.Children.Add(Field("Resolve this issue", BuildActivityResolutionActions(row)));
        var pair = Button(row.PairName, OpenActivityPair_Click, quiet: true);
        pair.Content = Text(row.PairName, 13, brush: "FocusBrush"); pair.Padding = new Thickness(0);
        pair.HorizontalAlignment = HorizontalAlignment.Left;
        _detailFields.Children.Add(Field("Folder Pair", pair));
        if (!row.IsAppEvent && !row.Path.StartsWith('<'))
            _detailFields.Children.Add(Field("Relative Path:", PathLink(row, row.Path, "relative")));
        var types = new Grid();
        types.ColumnDefinitions.Add(new ColumnDefinition()); types.ColumnDefinitions.Add(new ColumnDefinition());
        types.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        types.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        types.Children.Add(Field("Type:", Text(row.IsPairEvent ? "Folder Pair" : row.IsAppEvent ? row.Entity : row.ItemKind, 13)));
        if (!row.IsAppEvent && row.ItemKind == "File")
        {
            var format = Field("File Format:", Text(row.FileFormat.Length == 0 ? "No Extension" : row.FileFormat, 13));
            Grid.SetColumn(format, 1); types.Children.Add(format);
            var category = Field("File Type", Text(row.FileCategory, 13)); Grid.SetRow(category, 1); types.Children.Add(category);
            var size = Field("Size", Text(row.SizeLabel, 13)); Grid.SetRow(size, 1); Grid.SetColumn(size, 1); types.Children.Add(size);
        }
        _detailFields.Children.Add(types);
        var timestamp = DateTimeOffset.TryParse(row.When, out var date)
            ? date.ToLocalTime().ToString("ddd, d MMM yyyy, HH:mm:ss") + "\nLocal Time (" +
              (TimeZoneInfo.Local.IsDaylightSavingTime(date) ? TimeZoneInfo.Local.DaylightName : TimeZoneInfo.Local.StandardName) + ")"
            : row.When;
        _detailFields.Children.Add(Field(row.StatusGlyph == "↻" ? "Started" : row.StatusGlyph == "✓" ? "Completed" : "Last Updated", Text(timestamp, 13)));
        if (row.EndpointContext is { } context)
        {
            foreach (var endpoint in new[] { context.Left, context.Right })
            {
                var detail = new System.Windows.Controls.StackPanel();
                detail.Children.Add(Text(ActivityRow.EndpointLabel(endpoint), 13));
                if (!endpoint.IsLocal)
                    // UI398-3: this endpoint's OWN recorded account, named through the one shared display
                    // resolver, so a second account of the same provider keeps its own label and an endpoint
                    // that recorded no account says so rather than borrowing the selected or primary account.
                    detail.Children.Add(Text(EndpointAccountLine(endpoint.ConnectionId), 12, brush: "MutedInk"));
                var location = ActivityEndpointLocation(row, endpoint);
                if (location is not null)
                {
                    // UI398-4: a recorded endpoint location is a literal, so its own casing, punctuation and
                    // surrounding characters are shown exactly as recorded instead of being rewritten.
                    var copy = Button(location, (_, _) => SetClipboardText(location), quiet: true, verbatim: true);
                    copy.HorizontalContentAlignment = HorizontalAlignment.Left;
                    copy.Padding = new Thickness(0);
                    copy.ToolTip = "Copy this endpoint's recorded location. It may have changed since this event.";
                    detail.Children.Add(copy);
                }
                else detail.Children.Add(Text("Location not recorded", 12, brush: "MutedInk"));
                // EPS3B: this endpoint's own location action, so a two-cloud pair offers one Left and one
                // Right action with each endpoint's exact account, and a local-to-local pair offers local
                // actions only. A disabled action keeps its recorded location above it and carries its reason.
                detail.Children.Add(EndpointActionStrip(row, endpoint));
                _detailFields.Children.Add(Field(endpoint.Side == SyncPairSide.Left ? "Left endpoint:" : "Right endpoint:", detail));
            }
        }
        else
        {
            _detailFields.Children.Add(Field("Account:", Text(row.Connection, 13)));
            if (LocalItemPath(row) is { } local)
                _detailFields.Children.Add(Field("Recorded Local Location:", PathLink(row, local, "local")));
            if (CloudItemPath(row) is { } cloud)
                _detailFields.Children.Add(Field(ActivityCloud.LocationFieldLabel(ActivityProviderFor(row)),
                    PathLink(row, cloud, "cloud")));
        }
        if (IsRecoveryActivity(row)) _detailFields.Children.Add(Button("Open Recovery", OpenActivityRecovery_Click, quiet: true));
    }

    // UI398-3: the ONE account line every recorded-endpoint surface shows. A recorded connection id is
    // named through the shared display resolver under its existing caption; a missing identity states the
    // absence on its own rather than captioning a placeholder or resolving the currently selected account.
    internal string EndpointAccountLine(string? connectionId)
    {
        var account = EndpointAccountDisplayLabel(connectionId);
        return account is AccountNotRecordedLabel or AccountUnavailableLabel
            ? account
            : "Account: " + account;
    }

    private UIElement PathLink(ActivityRow row, string display, string side)
    {
        if (side == "local" && LocalItemPath(row) is null || side == "cloud" && CloudItemPath(row) is null ||
            side == "relative" && row.Path.StartsWith('<')) return Text(display, 13, brush: "MutedInk");
        var button = Button(display, (_, _) => { }, quiet: true);
        button.Content = Text(display, 13, brush: "FocusBrush"); button.Padding = new Thickness(0);
        button.HorizontalContentAlignment = HorizontalAlignment.Left; button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.ToolTip = display + "\nClick for location actions";
        button.Click += (_, _) => OpenPathMenu(button, row, side);
        button.PreviewMouseRightButtonDown += (_, e) => { OpenPathMenu(button, row, side); e.Handled = true; };
        return button;
    }

    internal static string? LocalItemPath(ActivityRow row)
    {
        if (row.LocalRoot is null || row.Path.StartsWith('<')) return null;
        try
        {
            var root = Path.GetFullPath(row.LocalRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (row.Path.Length == 0 && row.ItemKind == "Folder") return root;
            var relative = PilotPath.NormalizeRelative(row.Path);
            var item = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            return item.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? item : null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or NotSupportedException) { return null; }
    }

    internal static string? CloudItemPath(ActivityRow row)
    {
        if (row.DropboxRoot is null || row.Path.StartsWith('<')) return null;
        try { return row.Path.Length == 0 && row.ItemKind == "Folder" ? row.DropboxRoot : row.DropboxRoot.TrimEnd('/') + "/" + PilotPath.NormalizeRelative(row.Path); }
        catch (Exception e) when (e is ArgumentException or InvalidDataException) { return null; }
    }

    internal static string? ActivityEndpointLocation(ActivityRow row, SyncEndpointContextSide endpoint)
    {
        var root = endpoint.IsLocal ? endpoint.LocalPath : endpoint.FolderPath;
        if (root is null) return null;
        if (row.IsAppEvent || row.Path.StartsWith('<')) return root;
        var endpointRow = row with { LocalRoot = endpoint.IsLocal ? root : null,
            DropboxRoot = endpoint.IsLocal ? null : root };
        return endpoint.IsLocal ? LocalItemPath(endpointRow) : CloudItemPath(endpointRow);
    }

    // EPS3B: one measured location action per recorded endpoint. The action is built ONLY from that
    // endpoint's recorded provider, account and root. It is never resolved from the current pair, the
    // currently selected account or a primary account, and it performs no effect by itself. A missing
    // recorded identity stays visible and disabled with an explicit reason rather than being omitted.
    internal sealed record ActivityEndpointLocationAction(SyncActivitySide Side, bool IsLocal, string SideHeading,
        string EndpointLabel, string? Account, string? Location, Uri? WebLocation, bool Enabled, string Reason)
    {
        /// <summary>The one label this action carries in the inspector and the row menu.</summary>
        internal string Label => SideHeading + ": " + (IsLocal ? "Open folder" : "Open in " + EndpointLabel);
    }

    /// <summary>The ordered left-then-right location actions of a row that recorded its context, or none.</summary>
    internal IReadOnlyList<ActivityEndpointLocationAction> ActivityEndpointLocationActions(ActivityRow row) =>
        row.EndpointContext is { } context
            ? [ActivityEndpointLocationActionFor(row, context.Left),
               ActivityEndpointLocationActionFor(row, context.Right)]
            : [];

    internal ActivityEndpointLocationAction ActivityEndpointLocationActionFor(ActivityRow row,
        SyncEndpointContextSide endpoint)
    {
        var side = endpoint.Side == SyncPairSide.Left ? SyncActivitySide.Left : SyncActivitySide.Right;
        var heading = endpoint.Side == SyncPairSide.Left ? "Left endpoint" : "Right endpoint";
        var sideWord = side == SyncActivitySide.Left ? "left" : "right";
        var label = ActivityRow.EndpointLabel(endpoint);
        var account = endpoint.IsLocal ? null : endpoint.ConnectionId;
        var location = ActivityEndpointLocation(row, endpoint);
        if (endpoint.IsLocal)
            return location is null
                ? new(side, endpoint.IsLocal, heading, label, account, null, null, false,
                    "This event did not record the " + sideWord + " endpoint's folder path, so its location cannot be opened.")
                : new(side, endpoint.IsLocal, heading, label, account, location, null, true,
                    "Open the folder this event recorded for the " + sideWord + " endpoint.");
        if (endpoint.ConnectionId is null)
            return new(side, endpoint.IsLocal, heading, label, account, location, null, false,
                "This event did not record which " + label + " account held the " + sideWord +
                " endpoint. The recorded location stays visible, and the action is unavailable rather than " +
                "opened through the current or primary account.");
        if (location is null)
            return new(side, endpoint.IsLocal, heading, label, account, null, null, false,
                "This event did not record the " + sideWord + " endpoint's " + label +
                " root, so its location cannot be opened.");
        if (string.Equals(endpoint.Provider, SyncProviders.Dropbox, StringComparison.Ordinal))
        {
            var url = EndpointDropboxItemUrl(row, endpoint);
            return url is null
                ? new(side, endpoint.IsLocal, heading, label, account, location, null, false,
                    "The " + label + " root recorded for the " + sideWord +
                    " endpoint is not a usable folder path, so no provider web location is available for it.")
                : new(side, endpoint.IsLocal, heading, label, account, location, url, true,
                    "Open the " + label + " location this event recorded for the " + sideWord + " endpoint.");
        }
        // EL-1: eligibility is the REQUESTED LOGICAL SIDE of the pair's RESOLVED current endpoint context,
        // never the pair's primary provider and never its primary account. Capture resolves a genuine
        // document-less legacy pair and refuses a malformed document, so a legacy Drive endpoint keeps
        // working and a second Google account on the same pair is offered like the first.
        var pair = _pairSet.Pairs.FirstOrDefault(candidate => candidate.Id == row.PairId);
        // A pair that is no longer retained is refused exactly like one whose saved endpoints cannot be
        // resolved: neither reaches a provider call, and the nullable guard is what proves it to the compiler.
        if (pair is null || !TryResolveCurrentEndpoints(pair, out var resolved))
            return new(side, endpoint.IsLocal, heading, label, account, location, null, false,
                "This pair's saved endpoints cannot be resolved, so the " + label + " location recorded for " +
                "this event cannot be opened. A malformed or incomplete saved endpoint document is refused " +
                "rather than read as a legacy pair.");
        // The existing both-endpoint identity comparison decides whether this event's recorded context is
        // still the pair's current context, so descriptive history alone never authorizes the provider call.
        if (row.EndpointContext is not { } recorded || !RecordedEndpointsStillMatch(recorded, pair))
            return new(side, endpoint.IsLocal, heading, label, account, location, null, false,
                "The endpoints this event recorded are not the pair's current endpoints, so this " + label +
                " location is unavailable instead of being redirected to another account.");
        var current = RecordedSide(resolved, endpoint.Side);
        if (current is null || current.Kind != SyncEndpointKind.GoogleDrive)
            return new(side, endpoint.IsLocal, heading, label, account, location, null, false,
                "The pair's current " + sideWord + " endpoint is not a " + label +
                " endpoint, so this location cannot be opened.");
        if (string.IsNullOrWhiteSpace(current.ConnectionId) ||
            string.IsNullOrWhiteSpace(current.FolderId) && string.IsNullOrWhiteSpace(current.FolderPath))
            return new(side, endpoint.IsLocal, heading, label, account, location, null, false,
                "The pair's current " + label + " " + sideWord + " endpoint does not record its own " +
                "account and folder identity, so this location is unavailable rather than opened through " +
                "another account.");
        return new(side, endpoint.IsLocal, heading, label, account, location, null, true,
            "Open the " + label + " location this event recorded for the " + sideWord + " endpoint.");
    }

    // EL-1: the pair's RESOLVED current endpoint context. SyncEndpointContext.Capture already resolves a
    // genuine document-less legacy pair as left-local and right-cloud, and already refuses a malformed or
    // incomplete explicit endpoint document instead of falling back to its legacy fields, so this helper
    // adds no second resolution rule of its own.
    internal static bool TryResolveCurrentEndpoints(SyncPair? pair, out SyncEndpointContext resolved)
    {
        resolved = null!;
        if (pair is null) return false;
        try
        {
            resolved = SyncEndpointContext.Capture(pair);
            return true;
        }
        catch (Exception)
        {
            // A malformed, unknown or incomplete saved endpoint document is refused here, never re-read as
            // a legacy pair and never rescued by plausible-looking legacy fields.
            return false;
        }
    }

    private static SyncEndpointContextSide? RecordedSide(SyncEndpointContext context, SyncPairSide side) =>
        side == SyncPairSide.Left ? context.Left : context.Right;

    // EL-1: the ONE Drive request shape. The caller passes the recorded pair, the SELECTED logical side,
    // the recorded context and the recorded relative path, so the corrected Core entry point resolves that
    // exact side's account and root. There is no second code path and no account substitution.
    internal static (string PairId, SyncPairSide Side, SyncEndpointContext Context, string RelativePath)
        EndpointDriveRequest(ActivityRow row, SyncEndpointContextSide endpoint) =>
        (row.PairId ?? throw new InvalidDataException("The event did not record its folder pair."),
         endpoint.Side,
         row.EndpointContext ?? throw new InvalidDataException("The event did not record its endpoint context."),
         row.Path.StartsWith('<') || row.Path.Length == 0 ? "." : row.Path);

    // The endpoint-scoped view of a row: the same recorded relative path under THAT endpoint's root. It
    // carries no connection id, because a Dropbox web location is a path fact and never an account claim.
    private static ActivityRow EndpointScopedRow(ActivityRow row, SyncEndpointContextSide endpoint) =>
        row with
        {
            LocalRoot = endpoint.IsLocal ? endpoint.LocalPath : null,
            DropboxRoot = endpoint.IsLocal ? null : endpoint.FolderPath,
            ConnectionId = null,
            EndpointContext = null
        };

    private Uri? EndpointDropboxItemUrl(ActivityRow row, SyncEndpointContextSide endpoint)
    {
        if (endpoint.IsLocal || endpoint.FolderPath is null) return null;
        if (row.Path.StartsWith('<')) return null;
        return DropboxItemUrl(EndpointScopedRow(row, endpoint));
    }

    // One entry point for every per-endpoint location action, so the inspector and the row menus share
    // exactly one executor. Local endpoints open the recorded folder; cloud endpoints use the recorded
    // account through the existing provider adapter and never the current pair's account.
    private async void OpenEndpointLocationAsync(ActivityRow row, SyncEndpointContextSide endpoint)
    {
        var action = ActivityEndpointLocationActionFor(row, endpoint);
        if (!action.Enabled || action.Location is null)
        {
            ShowStatus(action.Reason);
            return;
        }
        if (endpoint.IsLocal)
        {
            OpenExplorerItemPath(action.Location, row.ItemKind);
            ShowStatus("Opened the recorded " + action.EndpointLabel + " location for the " + action.SideHeading.ToLowerInvariant() + ".");
            return;
        }
        try
        {
            var url = action.WebLocation;
            if (url is null)
            {
                // EL-1: ask the corrected Core entry point for the SELECTED logical side's endpoint, with the
                // recorded pair and the recorded context, so that exact side's account and root resolve. The
                // pair's primary account is never named here and a non-primary account is offered normally.
                var request = EndpointDriveRequest(row, endpoint);
                var resolved = await _authorityHost.ResolveGoogleDriveEndpointWebLocationAsync(request.PairId,
                    request.Side, request.Context, request.RelativePath);
                url = new Uri(resolved.WebViewLink);
            }
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            ShowStatus("Opened the recorded " + action.EndpointLabel + " location for the " + action.SideHeading.ToLowerInvariant() + ".");
        }
        catch (Exception error)
        {
            ShowStatus(action.EndpointLabel + " could not open this recorded location: " + error.Message);
        }
    }

    // The inspector strip for one recorded endpoint: the action is always present, and a disabled action
    // carries its reason as its tooltip and accessible name so the omission is never silent.
    private FrameworkElement EndpointActionStrip(ActivityRow row, SyncEndpointContextSide endpoint)
    {
        var action = ActivityEndpointLocationActionFor(row, endpoint);
        var strip = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var label = action.Label;
        var open = Button(label, (_, _) => OpenEndpointLocationAsync(row, endpoint), quiet: true);
        open.IsEnabled = action.Enabled;
        open.ToolTip = action.Enabled
            ? (action.Location is null ? action.Reason : action.Location + Environment.NewLine +
               // UI398-3: the caption names the account of the connection id this action actually carries,
               // never the currently selected or primary one. A local endpoint keeps its existing wording.
               "Account: " + (action.Account is { } captionAccount
                   ? EndpointAccountDisplayLabel(captionAccount) : "Not recorded") + Environment.NewLine + action.Reason)
            : action.Reason;
        System.Windows.Automation.AutomationProperties.SetName(open,
            action.EndpointLabel + " (" + (action.Account is { } namedAccount
                ? EndpointAccountDisplayLabel(namedAccount) : "no recorded account") + ") " +
            action.SideHeading.ToLowerInvariant() + " location action" +
            (action.Enabled ? string.Empty : ". Unavailable: " + action.Reason));
        strip.Children.Add(open);
        return strip;
    }

    private Uri? CloudItemUrl(ActivityRow row)
    {
        // A Drive path is not a Dropbox URL, nor does it prove a Drive item id. Keep the recorded
        // path copyable without fabricating an exact cloud target: the Dropbox-only web location is
        // offered only for a row whose exact provider binding is Dropbox.
        if (row.IsGoogleDrive || ActivityProviderFor(row) != SyncProviders.Dropbox) return null;
        return DropboxItemUrl(row);
    }

    // EPS3B: the ONE Dropbox web-location rule, extracted unchanged so a recorded endpoint can reuse
    // exactly it. The provider gate stays with each caller, because a historical endpoint states its own
    // provider while a legacy row proves one through its recorded binding.
    private static Uri? DropboxItemUrl(ActivityRow row)
    {
        var path = CloudItemPath(row); if (path is null) return null;
        if (row.ItemKind == "Folder") return BuildDropboxFolderUrl(path);
        var index = path.LastIndexOf('/');
        return new Uri(BuildDropboxFolderUrl(index <= 0 ? "/" : path[..index]).AbsoluteUri +
                       "?preview=" + Uri.EscapeDataString(path[(index + 1)..]));
    }

    private void OpenLocalItem(ActivityRow row)
    {
        if (LocalItemPath(row) is not { } path) { ShowStatus("The exact local location was not retained for this event."); return; }
        OpenExplorerItemPath(path, row.ItemKind);
    }

    private void OpenExplorerItemPath(string path, string itemKind)
    {
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.Arguments = itemKind == "Folder" ? "\"" + path.Replace("\"", "") + "\"" : "/select,\"" + path.Replace("\"", "") + "\"";
            Process.Start(start);
        }
        catch (Exception e) { ShowStatus("File Explorer could not open this location: " + e.Message); }
    }

    private bool CanResolveCloudItem(ActivityRow row) => CloudItemUrl(row) is not null ||
        !row.Path.StartsWith('<') && row.PairId is not null && row.ConnectionId is not null && _pairSet.Pairs.Any(pair =>
            pair.Id == row.PairId && pair.Provider == SyncProviders.GoogleDrive && !string.IsNullOrWhiteSpace(pair.ConnectionId) &&
            row.ConnectionId == pair.ConnectionId && row.DropboxRoot == pair.DropboxRoot &&
            (row.EndpointContext is null || RecordedEndpointsStillMatch(row.EndpointContext, pair)));

    private static bool RecordedEndpointsStillMatch(SyncEndpointContext recorded, SyncPair pair)
    {
        try
        {
            var current = SyncEndpointContext.Capture(pair);
            return recorded.PairId == current.PairId && recorded.Left == current.Left && recorded.Right == current.Right;
        }
        catch (InvalidDataException) { return false; }
    }

    private async Task<Uri> ResolveCloudItemUrlAsync(ActivityRow row)
    {
        if (!CanResolveCloudItem(row)) throw new InvalidOperationException("The exact current cloud location is unavailable. Open the current folder pair.");
        var pair = _pairSet.Pairs.SingleOrDefault(pair => pair.Id == row.PairId);
        if (pair?.Provider == SyncProviders.GoogleDrive)
            return new Uri(await _authorityHost.GetGoogleDriveWebLocationAsync(pair.Id, pair.ConnectionId!,
                row.Path.Length == 0 ? "." : row.Path));
        return CloudItemUrl(row) ?? throw new InvalidOperationException("No verified cloud location is available.");
    }

    private async void CopyCloudItemUrl(ActivityRow row)
    {
        try { SetClipboardText((await ResolveCloudItemUrlAsync(row)).AbsoluteUri); ShowStatus("Cloud location copied."); }
        catch (Exception error) { ShowStatus("Cloud location could not be copied: " + error.Message); }
    }

    private async void OpenCloudItem(ActivityRow row)
    {
        var cloudName = ActivityCloudName(row);
        try { var url = await ResolveCloudItemUrlAsync(row); Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception e) { ShowStatus(cloudName + " could not be opened: " + e.Message); }
    }

    private static void MenuAction(ContextMenu menu, string label, RoutedEventHandler action, bool enabled = true)
    {
        var item = new MenuItem { Header = CommandIcons.Label(label), IsEnabled = enabled };
        System.Windows.Automation.AutomationProperties.SetName(item, label); item.Click += action; menu.Items.Add(item);
    }

    // Explicit icon variant: the Indefinitely option must carry the stop square rather than the
    // icon the label text would otherwise imply.
    private static void MenuAction(ContextMenu menu, string label, string icon, RoutedEventHandler action,
        bool enabled = true) => MenuAction(menu, label, icon, string.Empty, action, enabled);

    private static void MenuAction(ContextMenu menu, string label, string icon, string tooltip,
        RoutedEventHandler action, bool enabled = true)
    {
        var item = new MenuItem { Header = CommandIcons.Label(label, icon), IsEnabled = enabled };
        if (tooltip.Length > 0) item.ToolTip = tooltip;
        System.Windows.Automation.AutomationProperties.SetName(item, label); item.Click += action; menu.Items.Add(item);
    }

    private void OpenPathMenu(UIElement target, ActivityRow row, string side)
    {
        _inspectedActivity = row;
        var menu = WorkspaceMenu(); menu.PlacementTarget = target; menu.Placement = PlacementMode.Bottom;
        if (side is "relative") MenuAction(menu, "Copy Relative Path", (_, _) => SetClipboardText(row.Path));
        if (side is "local" or "relative")
        {
            MenuAction(menu, "Show in File Explorer", (_, _) => OpenLocalItem(row), LocalItemPath(row) is not null);
            MenuAction(menu, "Copy Local Path", (_, _) => SetClipboardText(LocalItemPath(row)!), LocalItemPath(row) is not null);
        }
        if (side is "cloud" or "relative")
        {
            var cloudName = ActivityCloudName(row);
            MenuAction(menu, "Open in " + cloudName, (_, _) => OpenCloudItem(row), CanResolveCloudItem(row));
            MenuAction(menu, "Copy " + cloudName + " Path", (_, _) => SetClipboardText(CloudItemPath(row)!), CloudItemPath(row) is not null);
            MenuAction(menu, "Copy " + cloudName + " Location Link", (_, _) => CopyCloudItemUrl(row), CanResolveCloudItem(row));
            MenuAction(menu, "Share & Manage Links", (_, _) => OpenSharing(row),
                !row.Path.StartsWith('<') && _pairSet.Pairs.Any(pair => pair.Id == row.PairId));
        }
        menu.Items.Add(new Separator());
        MenuAction(menu, "Open Folder Pair", OpenActivityPair_Click, PairRows.Any(pair => pair.Id == row.PairId));
        menu.IsOpen = true;
    }

    private string ActivityDetails(ActivityRow row)
    {
        // The recorded outcome and reason decide the wording; only the provider name inside it comes
        // from the resolved binding, so the described row carries its exact provider.
        var described = WithExactProviderBinding(row);
        return $"{described.DisplayName}\n{described.HumanAction}\nFolder Pair: {described.PairName}" +
            (described.IsAppEvent ? $"\nType: {(described.IsPairEvent ? "Folder Pair" : described.Entity)}" : $"\nRelative Path: {described.Path}\nType: {described.ItemKind}") +
            (!described.IsAppEvent && described.ItemKind == "File" ? $"\nFile Format: {described.FileFormat}\nFile Type: {described.FileCategory}\nSize: {described.SizeLabel}" : "") +
            // UI06: the details carry the full recorded date and time as well as the short row time.
            // UI07/UI08: the exact run, account, category, severity and entity stay reachable here so a
            // sync-log row can be tied to its run and account without crowding the row itself.
            $"\nTimestamp: {described.FullTimestamp}" + ActivityEndpointDetails(described) + $"\nCategory: {described.CategoryLabel}" +
            $"\nSeverity: {described.Severity}\nItem Type: {described.Entity}\nRun: {described.RunId ?? "Not recorded"}" +
            (LocalItemPath(row) is { } local ? $"\nRecorded Local Location: {local}" : "") +
            (CloudItemPath(row) is { } cloud ? $"\n{ActivityCloud.LocationFieldLabel(ActivityProviderFor(row))} {cloud}" : "") +
            $"\nDetails: {described.Reason}\nOperation ID: {described.OperationId ?? "Not an operation"}" +
            (described.IsDiagnosticSetup ? "\nNote: Folder pair setup is diagnostic setup, not a transfer." : "");
    }

    // UI398-3: the expanded details name each recorded endpoint's own exact account through the same
    // shared display resolver the sidebar uses, so the two surfaces can never disagree and a missing
    // identity is stated rather than resolved to the currently selected account.
    private string ActivityEndpointDetails(ActivityRow row) => row.EndpointContext is { } context
        ? string.Concat(new[] { context.Left, context.Right }.Select(endpoint =>
            $"\n{endpoint.Side} endpoint: {ActivityRow.EndpointLabel(endpoint)}" +
            (endpoint.IsLocal ? "" : "\n" + EndpointAccountLine(endpoint.ConnectionId)) +
            "\nRecorded location: " + (ActivityEndpointLocation(row, endpoint) ?? "Not recorded")))
        : "\nAccount: " + row.Connection;
}
