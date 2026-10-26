using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

internal sealed record StorageInventoryDisplayRow(SyncStorageInventoryRow Item, string AccountLabel)
{
    public string DisplayName => Item.RelativePath == "." ? Item.PairName : System.IO.Path.GetFileName(Item.LocalPath);
    // UIV1-1: the row action states what the row actually is. A selective-sync exclusion is named
    // as the exclusion it is, so its retained availability ("Mixed at last index" and similar)
    // stays where it belongs — in the details. A saved policy that no observation confirmed is
    // labelled as the saved policy, so a saved policy alone never reads as an observed state.
    public string ShortAction => SyncStorageInventory.IsSelectiveSyncMapping(Item)
        ? "Excluded from sync"
        : IsUnobservedPolicy(Item.Availability) ? Item.DesiredPolicy + " (saved policy)" : Item.Availability;
    private static bool IsUnobservedPolicy(string availability) =>
        availability is "Not observed; saved policy only" or "Not inspected";
    public string PairName => Item.PairName + " · " + AccountLabel;
    public string Path => Item.LocalPath;
    public string ItemKind => "Folder";
    public string TimeLabel => Item.ObservedUtc?.ToLocalTime().ToString("HH:mm") ?? "";
    public string FullTimestamp => Item.ObservedUtc?.ToLocalTime().ToString("f") ?? "Not observed";
    public string TimeAccessibleLabel => FullTimestamp;
    public string StatusGlyph => Item.Completeness.StartsWith("Incomplete") ? "!" : "";
    public string Policy => Item.DesiredPolicy;
    public string Participation => Item.Participation;
    public string Ownership => Item.Ownership;
}

public partial class MainWindow
{
    private Border? _storagePage;
    private ComboBox _storageAccount = null!, _storagePair = null!, _storageDrive = null!, _storageFilter = null!;
    private TextBox _storageSearch = null!;
    private DataGrid _storageRows = null!;
    private TextBlock _storageMessage = null!;
    private Button _storageMore = null!, _storageRefresh = null!, _storageCancel = null!;
    private bool _refreshingStorageChoices;
    private CancellationTokenSource? _storageLoad;
    private int _storageOffset;
    // UIV1-1: the footer is derived at render time, so the retained page coverage and an explicit
    // inspection outcome are held as distinct provenance instead of being re-typed into the text.
    private bool _storageHasMore;
    private string? _storageCoverageNotice;
    private string? _storageOutcomeNotice;
    private readonly List<SyncStorageInventoryRow> _storageInventory = [];
    private readonly Dictionary<string, (DateTimeOffset Checked, SyncAccountStatus Status)> _storageCloudStatus = new(StringComparer.Ordinal);
    private StackPanel _storageDetailFields = null!;
    private ContentPresenter _storageDetailHost = null!;
    private ColumnDefinition _storageDetailColumn = null!;
    private DetailsHost? _storageDetails;
    private SimpleDetailedSwitch? _storageViewSwitch;
    private StackPanel _storageDeviceSpace = null!;

    private void BuildStoragePage()
    {
        var body = new Grid();
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            body.RowDefinitions.Add(new RowDefinition { Height = height });
        _storageAccount = StoragePicker("Storage Account");
        _storagePair = StoragePicker("Storage Folder Pair"); _storageDrive = StoragePicker("Storage Drive");
        _storageRefresh = AsyncButton("Refresh", async (_, _) => await LoadStoragePageAsync(true), true,
            explanation: "Reload saved online-only and selective-sync mappings, and refresh device free space. This does not scan folders, download files or start a sync.");
        _storageCancel = Button("Cancel Inspection", (_, _) => _storageLoad?.Cancel(), true);
        _storageCancel.Visibility = Visibility.Collapsed;
        var inspect = Button("Find existing online-only folders", (_, _) =>
        {
            var menu = WorkspaceMenu();
            MenuAction(menu, "Choose a Folder…", async (_, _) =>
            {
                var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Inspect storage — read-only metadata" };
                if (picker.ShowDialog(this) == true) await InspectStorageAsync(picker.FolderName, null, null);
            });
            foreach (var root in StorageInventoryWindows.RegisteredRoots())
                MenuAction(menu, root.Provider + ": " + root.Path, async (_, _) => await InspectStorageAsync(root.Path, null, null));
            menu.PlacementTarget = inspectStorageAnchor; menu.IsOpen = true;
        }, true);
        inspectStorageAnchor = inspect;
        inspect.ToolTip = "Inspect an exact local or registered provider folder without downloading files or changing ownership. Extra roots are shown for this session.";
        body.Children.Add(SharedPresentation.PageHeader("Storage", _storageAccount, inspect, _storageRefresh, _storageCancel));
        _storageDeviceSpace = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(_storageDeviceSpace, 1); body.Children.Add(_storageDeviceSpace);
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        tools.Children.Add(_storageDrive); tools.Children.Add(_storagePair);
        _storageFilter = new ComboBox { ItemsSource = new[] { "Online-only folders", "Selective sync" },
            SelectedIndex = 0, MinWidth = 130, MaxWidth = 180, Style = SharedPresentation.QuietComboBoxStyle() };
        AutomationProperties.SetName(_storageFilter, "Storage State");
        tools.Children.Add(_storageFilter);
        _storageSearch = new TextBox(); _storageSearch.TextChanged += (_, _) => RenderStorageInventory();
        tools.Children.Add(SharedPresentation.QuietSearchField(_storageSearch, "Search Storage", "Search saved mappings…", width: 230));
        _storageViewSwitch = SharedPresentation.ViewSwitch("storage", IsDetailedView("storage"), control =>
        { SharedPresentation.ApplyFeedDetail(_storageRows, control.IsDetailed, StorageDetailColumns); _ = SetDetailedViewAsync("storage", control.IsDetailed); });
        tools.Children.Add(_storageViewSwitch);
        _storageFilter.SelectionChanged += (_, _) => RenderStorageInventory();
        _storageAccount.SelectionChanged += async (_, _) => { if (!_refreshingStorageChoices) { RefreshStorageChoices(false); await LoadStoragePageAsync(true); } };
        _storageDrive.SelectionChanged += async (_, _) => { if (!_refreshingStorageChoices) { RefreshStorageChoices(false); await LoadStoragePageAsync(true); } };
        _storagePair.SelectionChanged += async (_, _) => { if (!_refreshingStorageChoices) await LoadStoragePageAsync(true); };
        Grid.SetRow(tools, 2); body.Children.Add(tools);
        var results = new Grid(); results.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _storageDetailColumn = new ColumnDefinition { Width = new GridLength(0) }; results.ColumnDefinitions.Add(_storageDetailColumn);
        _storageRows = new DataGrid(); StyleFeed(_storageRows);
        _storageRows.Columns.RemoveAt(_storageRows.Columns.Count - 1);
        SharedPresentation.ApplyFeedDetail(_storageRows, IsDetailedView("storage"), StorageDetailColumns);
        AutomationProperties.SetName(_storageRows, "Storage Inventory");
        _storageRows.SelectionChanged += (_, _) => ShowStorageDetails(_storageRows.SelectedItem as StorageInventoryDisplayRow);
        results.Children.Add(_storageRows);
        _storageDetailFields = new StackPanel();
        _storageDetailHost = new ContentPresenter { Margin = new Thickness(16, 0, 0, 0) };
        _storageDetails = new DetailsHost(new ScrollViewer { Content = _storageDetailFields,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, _storageDetailHost);
        Grid.SetColumn(_storageDetailHost, 1); results.Children.Add(_storageDetailHost);
        Grid.SetRow(results, 3); body.Children.Add(results);
        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        _storageMore = AsyncButton("Load more saved mappings", async (_, _) => await LoadStoragePageAsync(false), true);
        _storageMore.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_storageMore, Dock.Right); footer.Children.Add(_storageMore);
        _storageMessage = Text("", 12, brush: "MutedInk"); _storageMessage.TextWrapping = TextWrapping.Wrap; footer.Children.Add(_storageMessage);
        Grid.SetRow(footer, 4); body.Children.Add(footer);
        _storagePage = new Border { Visibility = Visibility.Collapsed, Child = body };
        Grid.SetRow(_storagePage, Grid.GetRow(DashboardPage)); ((Grid)DashboardPage.Parent).Children.Add(_storagePage);
        RefreshStorageChoices(true);
    }
    private UIElement? inspectStorageAnchor;
    private static readonly FeedDetailColumn[] StorageDetailColumns =
    [new("Desired policy", nameof(StorageInventoryDisplayRow.Policy), 150), new("Sync participation", nameof(StorageInventoryDisplayRow.Participation), 160),
        new("Owner evidence", nameof(StorageInventoryDisplayRow.Ownership), 180)];

    private static ComboBox StoragePicker(string name)
    {
        var picker = new ComboBox { MinWidth = 120, MaxWidth = name == "Storage Account" ? 280 : 200,
            Margin = new Thickness(0, 0, 10, 0), Style = SharedPresentation.QuietComboBoxStyle(),
            DisplayMemberPath = nameof(SharingChoice.Label), SelectedValuePath = nameof(SharingChoice.Id) };
        AutomationProperties.SetName(picker, name); return picker;
    }
    private void RefreshStorageChoices(bool accounts)
    {
        if (_storagePage is null) return;
        _refreshingStorageChoices = true;
        try
        {
            void Set(ComboBox picker, IEnumerable<SharingChoice> values)
            {
                var selected = picker.SelectedValue as string; var choices = values.ToArray(); picker.ItemsSource = choices;
                picker.SelectedValue = choices.Any(item => item.Id == selected) ? selected : "";
            }
            if (accounts) Set(_storageAccount, new[] { new SharingChoice("", "All Accounts") }.Concat(_accountLabels.Select(x => new SharingChoice(x.Key, x.Value))));
            var account = _storageAccount.SelectedValue as string;
            var pairs = _pairSet.Pairs.Where(pair => string.IsNullOrEmpty(account) || TrayMatchesAccount(pair.Id, account)).ToArray();
            Set(_storageDrive, new[] { new SharingChoice("", "All Drives") }.Concat(pairs.Select(p => Path.GetPathRoot(p.LocalRoot) ?? "")
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(p => p.Length > 0).Select(p => new SharingChoice(p, p))));
            var drive = _storageDrive.SelectedValue as string;
            Set(_storagePair, new[] { new SharingChoice("", "All Folder Pairs") }.Concat(pairs.Where(pair => string.IsNullOrEmpty(drive) ||
                string.Equals(Path.GetPathRoot(pair.LocalRoot), drive, StringComparison.OrdinalIgnoreCase)).Select(pair => new SharingChoice(pair.Id, pair.Name))));
        }
        finally { _refreshingStorageChoices = false; }
    }
    private async Task LoadStoragePageAsync(bool reset)
    {
        if (_storagePage is null || _storageRows is null) return;
        _storageLoad?.Cancel(); using var load = new CancellationTokenSource(TimeSpan.FromSeconds(45)); _storageLoad = load;
        if (reset)
        {
            _storageInventory.Clear(); _storageOffset = 0;
            _storageHasMore = false; _storageCoverageNotice = null; _storageOutcomeNotice = null;
            ShowStorageDetails(null); RenderStorageInventory();
        }
        _storageOutcomeNotice = "Loading saved storage information…"; PresentStorageSummary();
        _storageRefresh.IsEnabled = false;
        try
        {
            if (reset)
            {
                var devices = await Task.Run(SyncStorageInventory.ReadDeviceSpace, load.Token);
                if (!ReferenceEquals(load, _storageLoad)) return;
                _storageDeviceSpace.Children.Clear();
                foreach (var device in devices)
                    _storageDeviceSpace.Children.Add(Text(device.AvailableBytes is { } free && device.CapacityBytes is { } capacity
                        ? $"{device.Drive}   {StorageBytes(free)} available of {StorageBytes(capacity)}" : device.Drive + "   " + device.Error,
                        14, margin: new Thickness(0, 0, 0, 4)));
            }
            SyncStorageInventoryPage page;
            var pageCount = 0;
            do
            {
                page = await _authorityHost.LoadStorageInventoryAsync(EmptyToNull(_storageAccount.SelectedValue as string),
                    EmptyToNull(_storagePair.SelectedValue as string), _storageOffset, 100, load.Token);
                if (!ReferenceEquals(load, _storageLoad)) return;
                MergeStorageInventory(page.Rows); _storageOffset = page.NextOffset;
                RenderStorageInventory();
                // Most indexed folders consume no storage policy. Do not present an empty map
                // just because the first page contains ordinary folders. Work remains bounded.
            } while (page.HasMore && _storageRows.Items.Count == 0 && ++pageCount < 20);
            _storageMore.Visibility = page.HasMore ? Visibility.Visible : Visibility.Collapsed;
            _storageHasMore = page.HasMore;
            _storageOutcomeNotice = null;
            RenderStorageInventory();
        }
        catch (Exception error)
        {
            if (ReferenceEquals(load, _storageLoad))
            {
                _storageOutcomeNotice = "Storage could not be loaded: " +
                    (error is OperationCanceledException ? "Cancelled or timed out; Refresh to retry." : error.Message);
                PresentStorageSummary();
            }
        }
        finally { if (ReferenceEquals(load, _storageLoad)) { _storageLoad = null; _storageRefresh.IsEnabled = true; } }
    }
    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;
    private void MergeStorageInventory(IEnumerable<SyncStorageInventoryRow> incoming)
    {
        foreach (var row in incoming)
        {
            _storageInventory.RemoveAll(r => r.PairId == row.PairId && string.Equals(r.LocalPath, row.LocalPath, StringComparison.OrdinalIgnoreCase));
            _storageInventory.Add(row);
        }
    }
    private void RenderStorageInventory()
    {
        if (_storageRows is null) return;
        var prior = (_storageRows.SelectedItem as StorageInventoryDisplayRow)?.Item;
        var query = _storageSearch.Text.Trim(); var filter = _storageFilter.SelectedIndex;
        var drive = _storageDrive.SelectedValue as string;
        var visible = _storageInventory.Where(r => (string.IsNullOrEmpty(drive) || string.Equals(Path.GetPathRoot(r.LocalPath), drive, StringComparison.OrdinalIgnoreCase)) &&
            (r.LocalPath + " " + r.PairName + " " + r.Provider).Contains(query, StringComparison.OrdinalIgnoreCase) &&
            (filter == 1 ? SyncStorageInventory.IsSelectiveSyncMapping(r) : SyncStorageInventory.IsOnlineOnlyMapping(r)))
            .Select(r => new StorageInventoryDisplayRow(r, r.ConnectionId is null ? "No account assigned" : SharingAccountLabel(r.ConnectionId)))
            .OrderBy(r => r.Item.PairName).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        _storageRows.ItemsSource = visible;
        if (prior is not null) _storageRows.SelectedItem = visible.FirstOrDefault(r => r.Item.PairId == prior.PairId &&
            string.Equals(r.Path, prior.LocalPath, StringComparison.OrdinalIgnoreCase));
        PresentStorageSummary();
    }
    // UIV1-1: one render-time presentation function derives the footer from the CURRENT filter and
    // the rows actually shown, so a filter, search, account, pair, drive or page-result transition
    // can never leave the previous empty-state sentence beside a populated grid. Retained page
    // coverage and an explicit inspection outcome/error stay separate provenance and are not
    // erased by an ordinary render.
    private void PresentStorageSummary()
    {
        if (_storageMessage is null) return;
        var selective = _storageFilter?.SelectedIndex == 1;
        var label = selective ? "Selective sync" : "Online-only folders";
        var shown = _storageRows?.Items.Count ?? 0;
        var parts = new List<string>
        {
            shown == 0
                ? "No " + (selective ? "selective-sync exclusions" : "online-only folders") + " are recorded for this selection."
                : label + ": " + shown + " shown for this selection.",
            "Online-only saves local space while keeping items visible. Selective sync excludes folders from this pair; existing local files are not automatically removed."
        };
        if (_storageHasMore) parts.Add("More saved records remain to load.");
        if (_storageCoverageNotice is { Length: > 0 }) parts.Add(_storageCoverageNotice);
        parts.Add("Saved mappings are not a fresh disk scan.");
        if (_storageOutcomeNotice is { Length: > 0 }) parts.Add(_storageOutcomeNotice);
        _storageMessage.Text = string.Join(" ", parts);
    }
    private void ShowStorageDetails(StorageInventoryDisplayRow? display)
    {
        _storageDetails?.Collapse(); _storageDetailFields.Children.Clear();
        if (display is null) { _storageDetails?.RegisterSidebarOnly(); _storageDetailColumn.Width = new GridLength(0); return; }
        var row = display.Item;
        var header = new DockPanel();
        // UIV6-H1: Storage uses the one shared strip as well, so its actions render Expand then Close
        // instead of the previous reversed order; only its own subject and its own callbacks differ.
        var storageChrome = DetailsChrome.ActionStrip("Storage Details", (_, _) => _storageDetails?.Expand(this),
            (_, _) => _storageRows.SelectedItem = null);
        DockPanel.SetDock(storageChrome, Dock.Right); header.Children.Add(storageChrome);
        header.Children.Add(Text(display.DisplayName, 17, true));
        _storageDetailFields.Children.Add(header);
        // The selection owns the registration: this pair of controls is sidebar-only, and opening the
        // larger window hides them so it offers only its own return command.
        _storageDetails?.RegisterSidebarOnly(storageChrome);
        void Add(string label, string value) { var field = Field(label + ":", Text(value, 13)); field.Margin = new Thickness(0, 12, 0, 0); _storageDetailFields.Children.Add(field); }
        Add("Location", row.LocalPath); Add("Account", display.AccountLabel); Add("Folder pair", row.PairName);
        Add("Observed availability", row.Availability); Add("Desired policy", row.DesiredPolicy); Add("Sync participation", row.Participation);
        Add("Ownership", row.Ownership); Add("Evidence", row.Evidence); Add("Observed", display.FullTimestamp); Add("Coverage", row.Completeness);
        if (row.LogicalBytes is not null) Add("Direct files — logical size", StorageBytes(row.LogicalBytes.Value));
        if (row.ResidentBytes is not null) Add("Direct files — resident size", StorageBytes(row.ResidentBytes.Value));
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        actions.Children.Add(AsyncButton("Inspect This Folder", async (_, _) => await InspectStorageAsync(row.LocalPath, row.PairId, row.ConnectionId), true));
        if (row.PairId is not null)
            actions.Children.Add(Button("Manage Storage", (_, _) => _ = OpenPairStorageAsync(row.PairId, row.RelativePath == "." ? null : row.RelativePath), true));
        _storageDetailFields.Children.Add(actions); _storageDetails?.Present(row);
        _storageDetailColumn.Width = new GridLength(360);
        if (_storagePage!.ActualWidth is > 0 and < 720) { _storageDetailColumn.Width = new GridLength(0); _storageDetails?.Expand(this); }
    }
    private async Task InspectStorageAsync(string root, string? pairId, string? accountId)
    {
        _storageLoad?.Cancel(); using var load = new CancellationTokenSource(TimeSpan.FromSeconds(45)); _storageLoad = load;
        _storageCancel.Visibility = Visibility.Visible; _storageRefresh.IsEnabled = false;
        _storageOutcomeNotice = "Inspecting on-disk metadata only…"; PresentStorageSummary();
        try
        {
            var page = await _authorityHost.InspectStorageRootAsync(root, pairId, accountId, 100, load.Token);
            if (!ReferenceEquals(load, _storageLoad)) return;
            // An explicit unpaired inspection switches the view to All Accounts, without assigning ownership.
            if (pairId is null)
            {
                _refreshingStorageChoices = true;
                _storageAccount.SelectedValue = ""; _storageDrive.SelectedValue = ""; _storagePair.SelectedValue = "";
                _refreshingStorageChoices = false;
            }
            MergeStorageInventory(page.Rows);
            _storageMore.Visibility = page.HasMore ? Visibility.Visible : Visibility.Collapsed;
            _storageHasMore = page.HasMore;
            _storageCoverageNotice = page.Notice;
            _storageOutcomeNotice = null;
            RenderStorageInventory();
        }
        catch (Exception error)
        {
            if (ReferenceEquals(load, _storageLoad))
            {
                _storageOutcomeNotice = "Inspection did not finish: " +
                    (error is OperationCanceledException ? "Cancelled or timed out. No content or ownership was changed." : error.Message);
                PresentStorageSummary();
            }
        }
        finally { if (ReferenceEquals(load, _storageLoad)) { _storageLoad = null; _storageCancel.Visibility = Visibility.Collapsed; _storageRefresh.IsEnabled = true; } }
    }
    private static void EnsureStorageOwnershipAvailable(SyncPair pair)
    {
        var conflict = ExplorerIntegration.CompetingRoots(pair.LocalRoot).FirstOrDefault();
        if (conflict is not null) throw new InvalidOperationException(conflict.Provider + " owns the parent location " + conflict.Path + ". Open this pair's Status tab to resolve ownership first.");
    }
    private static string StorageLabel(SyncDirectoryStorage storage) => storage == SyncDirectoryStorage.CloudOnly ? "Online Only" : "Available Offline";
    private static string StorageBytes(long bytes)
    { var units = new[] { "B", "KB", "MB", "GB", "TB" }; double value = bytes; var i = 0; while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; } return value.ToString(i == 0 ? "N0" : "N1") + " " + units[i]; }
}

