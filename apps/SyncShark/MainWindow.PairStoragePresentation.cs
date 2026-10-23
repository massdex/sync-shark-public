using System.IO;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

// UI17: this file holds the ONE per-pair directory policy editor. It is mounted in the pair details
// Storage tab, which is deliberately the LAST pair-details tab so the existing Folders/Sync Preview/
// Sync Settings/Sync Status/Advanced indexes keep their meaning. The standalone global Storage page is
// now only a compact read-only saved-pair list that hands off through OpenPairStorageAsync, so no
// second competing storage editor exists.
//
// Every write keeps the existing guards: the unsaved pair-draft prompt, the provider capability gate,
// the target/ownership refusal, the exact account guard, the optimistic policy-revision guard
// (delegated to the authority) and the active-run refusal. A choice is never applied to a pair,
// account or revision that no longer matches what the user confirmed.
public partial class MainWindow
{
    internal const int PairStorageTabIndex = 5;

    internal sealed record PairStorageConfirmation(string Title, string Prompt, string Details, string Action);

    // These seams replace ONLY the external reads and effects (the Dropbox connection lookup, the
    // authority overview read, the authority mutation and the modal confirmation). Pair resolution,
    // the provider guard, staleness, the selection cap, the confirmation, the scope revalidation and
    // every refusal below still run for real.
    internal static Func<string, string, int, string?, string?, CancellationToken, Task<SyncStorageOverview>>? PairStorageOverviewLoaderForTest { get; set; }
    internal static Func<PairStorageConfirmation, bool>? PairStorageConfirmationForTest { get; set; }
    internal static Func<string, string, IReadOnlyList<string>?, SyncDirectoryStorage?, long, Task<SyncCloudAvailabilityResult>>? PairStorageChangeForTest { get; set; }
    internal static Func<string, string, SyncFileStorageRule, long, Task<SyncCloudAvailabilityResult>>? PairStorageFileResetForTest { get; set; }

    private Border? _pairStoragePanel;
    private ComboBox _pairStorageFilter = null!;
    private TextBox _pairStorageSearch = null!;
    private StackPanel _pairStorageRows = null!, _pairStorageCapacity = null!;
    private TextBlock _pairStorageMessage = null!, _pairStorageScope = null!, _pairStorageNote = null!;
    private Button _pairStorageRefresh = null!, _pairStorageMore = null!, _pairStorageBulk = null!;
    private CancellationTokenSource? _pairStorageLoad;
    private SyncStorageOverview? _pairStorageOverview;
    private readonly List<SyncFolderSelectionNode> _pairStorageFolders = [];
    private readonly HashSet<string> _pairStorageSelected = new(StringComparer.OrdinalIgnoreCase);
    private int _pairStorageOffset, _pairStorageFileLimit = 50, _pairStorageGeneration, _pairStorageOpenGeneration;
    private bool _pairStorageBuilt, _pairStorageFilesExpanded, _pairStorageCapacityExpanded,
        _changingPairStorage, _suppressPairStorageTabLoad;

    private void BuildPairStorageTab()
    {
        var body = new Grid();
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            body.RowDefinitions.Add(new RowDefinition { Height = height });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        _pairStorageScope = Text("", 14, true);
        _pairStorageNote = Text("Choices here apply to this saved pair immediately. Folder-tree storage choices on the Folders tab apply when you save the pair.", 12, brush: "MutedInk", margin: new Thickness(0, 4, 0, 0));
        _pairStorageNote.TextWrapping = TextWrapping.Wrap;
        heading.Children.Add(_pairStorageScope);
        heading.Children.Add(_pairStorageNote);
        body.Children.Add(heading);
        var tools = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _pairStorageRefresh = AsyncButton("Refresh", async (_, _) => { _storageCloudStatus.Clear(); await ReloadCurrentPairStorageAsync(); }, true);
        DockPanel.SetDock(_pairStorageRefresh, Dock.Right);
        tools.Children.Add(_pairStorageRefresh);
        _pairStorageFilter = new ComboBox { ItemsSource = new[] { "All folders", "Online only", "Excluded", "Pending", "Errors" }, SelectedIndex = 0, Width = 180, Margin = new Thickness(12, 0, 12, 0), Style = SharedPresentation.QuietComboBoxStyle() };
        System.Windows.Automation.AutomationProperties.SetName(_pairStorageFilter, "Pair Storage Filter");
        _pairStorageFilter.SelectionChanged += async (_, _) =>
        {
            if (!_pairStorageBuilt || _refreshingStorageChoices) return;
            if (PairEditorTabs.SelectedIndex != PairStorageTabIndex) return;
            await ReloadCurrentPairStorageAsync();
        };
        DockPanel.SetDock(_pairStorageFilter, Dock.Right);
        tools.Children.Add(_pairStorageFilter);
        _pairStorageSearch = new TextBox { ToolTip = "Search this pair's indexed folder paths. Press Enter to search." };
        System.Windows.Automation.AutomationProperties.SetName(_pairStorageSearch, "Search Pair Storage");
        _pairStorageSearch.KeyDown += async (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            await ReloadCurrentPairStorageAsync();
        };
        tools.Children.Add(SharedPresentation.QuietSearchField(_pairStorageSearch, "Search Pair Storage", "Search folder paths…", width: 230));
        Grid.SetRow(tools, 1);
        body.Children.Add(tools);
        _pairStorageRows = new StackPanel();
        _pairStorageCapacity = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var scrollingContent = new StackPanel();
        scrollingContent.Children.Add(_pairStorageCapacity);
        scrollingContent.Children.Add(_pairStorageRows);
        var scroll = new ScrollViewer { Content = scrollingContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2);
        body.Children.Add(scroll);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        _pairStorageMore = AsyncButton("Load More", async (_, _) => await LoadMorePairStorageAsync(), true);
        _pairStorageMore.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_pairStorageMore, Dock.Right);
        footer.Children.Add(_pairStorageMore);
        _pairStorageBulk = Button("Change Selected Folders", (_, _) => OpenPairStorageChoiceMenu(_pairStorageBulk, _pairStorageSelected.ToArray()), true);
        _pairStorageBulk.IsEnabled = false;
        _pairStorageBulk.Visibility = Visibility.Collapsed;
        ActionAvailability.Bind(_pairStorageBulk, () => _changingPairStorage ? "The selected storage changes are being applied."
            : "Select one or more included folders before changing their storage settings.");
        ActionAvailability.Bind(_pairStorageRefresh, () => "This pair's storage information is being loaded or changed.");
        ActionAvailability.Bind(_pairStorageMore, () => "The next page of storage information is being loaded.");
        DockPanel.SetDock(_pairStorageBulk, Dock.Right);
        footer.Children.Add(_pairStorageBulk);
        _pairStorageMessage = Text("", 12, brush: "MutedInk");
        _pairStorageMessage.TextWrapping = TextWrapping.Wrap;
        footer.Children.Add(_pairStorageMessage);
        Grid.SetRow(footer, 3);
        body.Children.Add(footer);
        var shell = new Border { Padding = new Thickness(12), Child = body };
        System.Windows.Automation.AutomationProperties.SetName(shell, "Pair Storage");
        _pairStoragePanel = shell;
        PairStorageTabHost.Children.Add(shell);
        _pairStorageBuilt = true;
    }

    // The single entry point used by the global Storage saved-pair list and by the folder rows in this
    // tab. It awaits the existing unsaved-draft guard, re-resolves the exact saved pair, selects and
    // expands that exact pair, opens the Storage tab and applies an optional relative path FILTER.
    private async Task OpenPairStorageAsync(string pairId, string? relativePath = null)
    {
        var opening = ++_pairStorageOpenGeneration;
        try
        {
            if (!await PrepareToLeaveCurrentPairDraftAsync()) return;
            var pair = SavedPairForStorage(pairId);
            if (pair is null)
            {
                _pairSet = await _authorityHost.LoadPairsAsync();
                pair = SavedPairForStorage(pairId);
            }
            if (pair is null) { ShowStatus("That folder pair is no longer saved. Refresh the pair list and try again."); return; }
            NavigateTo("Pairs");
            var row = PairRows.FirstOrDefault(item => string.Equals(item.Id, pairId, StringComparison.Ordinal));
            if (row is null) { ShowStatus("That folder pair is not in the current pair list. Clear the pair filter and try again."); return; }
            // Complete saved selection before opening a tab. An unawaited SelectionChanged save
            // otherwise resets the editor to Folders and cancels the storage read after hand-off.
            _suppressPairSelection = true;
            try { PairList.SelectedItem = row; }
            finally { _suppressPairSelection = false; }
            if (_pairSet.SelectedPairId != pairId) await SelectPairAsync(pairId);
            if (opening != _pairStorageOpenGeneration || PairList.SelectedItem is not PairRow selected || selected.Id != pairId) return;
            ExpandPairRow(row);
            _pairStorageSearch.Text = StoragePathFilter(relativePath);
            _suppressPairStorageTabLoad = true;
            try { PairEditorTabs.SelectedIndex = PairStorageTabIndex; }
            finally { _suppressPairStorageTabLoad = false; }
            if (SavedPairForStorage(pairId) is not { } current)
            {
                ShowPairStorageUnavailable("That folder pair is no longer saved. Refresh the pair list and try again.");
                return;
            }
            await LoadPairStorageAsync(current, reset: true);
        }
        catch (Exception error) { ShowStatus("Pair storage could not be opened: " + error.Message); }
    }

    // A relative path is a filter, never a command or a traversal: anything unsafe is ignored.
    private static string StoragePathFilter(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return string.Empty;
        try { return PilotPath.NormalizeRelative(relativePath); }
        catch (ArgumentException) { return string.Empty; }
    }

    private SyncPair? SavedPairForStorage(string pairId) =>
        _pairSet.Pairs.FirstOrDefault(item => string.Equals(item.Id, pairId, StringComparison.Ordinal));

    private SyncPair? CurrentPairStoragePair() => _editingNewPair || PairList.SelectedItem is not PairRow row
        ? null : SavedPairForStorage(row.Id);

    // Called whenever the Storage tab becomes the selected pair-details tab.
    internal async Task RefreshPairStorageTabAsync()
    {
        if (CurrentPairStoragePair() is not { } pair)
        {
            ShowPairStorageUnavailable("Save this pair before managing its folder storage.");
            return;
        }
        await LoadPairStorageAsync(pair, reset: true);
    }

    private Task ReloadCurrentPairStorageAsync() => CurrentPairStoragePair() is { } pair
        ? LoadPairStorageAsync(pair, reset: true) : Task.CompletedTask;

    private Task LoadMorePairStorageAsync() => CurrentPairStoragePair() is { } pair
        ? LoadPairStorageAsync(pair, reset: false) : Task.CompletedTask;

    // Pair change and tab teardown clear the previous pair's rows, selection and actions immediately,
    // and cancel any in-flight load so a late completion cannot populate another pair.
    internal void ClearPairStorageTab()
    {
        _pairStorageLoad?.Cancel();
        _pairStorageLoad = null;
        _pairStorageOverview = null;
        _pairStorageFolders.Clear();
        _pairStorageSelected.Clear();
        _pairStorageOffset = 0;
        _pairStorageFileLimit = 50;
        _pairStorageGeneration++;
        if (!_pairStorageBuilt) return;
        _pairStorageRows.Children.Clear();
        _pairStorageCapacity.Children.Clear();
        _pairStorageSearch.Text = string.Empty;
        if (_pairStorageFilter.SelectedIndex != 0) _pairStorageFilter.SelectedIndex = 0;
        _pairStorageScope.Text = string.Empty;
        _pairStorageMessage.Text = string.Empty;
        _pairStorageMore.Visibility = Visibility.Collapsed;
        SetPairStorageBusy(false);
    }

    private void ShowPairStorageUnavailable(string message)
    {
        _pairStorageLoad?.Cancel();
        _pairStorageLoad = null;
        _pairStorageOverview = null;
        _pairStorageFolders.Clear();
        _pairStorageSelected.Clear();
        _pairStorageOffset = 0;
        _pairStorageGeneration++;
        if (!_pairStorageBuilt) return;
        _pairStorageRows.Children.Clear();
        _pairStorageCapacity.Children.Clear();
        _pairStorageMore.Visibility = Visibility.Collapsed;
        _pairStorageBulk.IsEnabled = false;
        _pairStorageBulk.Visibility = Visibility.Collapsed;
        _pairStorageScope.Text = string.Empty;
        _pairStorageNote.Text = string.Empty;
        _pairStorageMessage.Text = message;
        SetPairStorageBusy(false);
    }

    private void SetPairStorageBusy(bool busy)
    {
        if (!_pairStorageBuilt) return;
        _pairStoragePanel!.IsEnabled = !busy;
        _pairStorageRefresh.IsEnabled = !busy;
        _pairStorageMore.IsEnabled = !busy;
    }

    private bool IsCurrentPairStorageLoad(int generation, SyncPair requested, CancellationTokenSource load) =>
        ReferenceEquals(load, _pairStorageLoad) && generation == _pairStorageGeneration &&
        CurrentPairStoragePair() is { } current && current.Id == requested.Id &&
        current.ConnectionId == requested.ConnectionId && current.Provider == requested.Provider &&
        current.PolicyRevision == requested.PolicyRevision && current.LocalRoot == requested.LocalRoot &&
        current.DropboxFolderId == requested.DropboxFolderId;

    // The pair id, query generation and query inputs are captured before the read. A late completion
    // for a different pair, or for a superseded query, is discarded instead of displayed.
    private async Task LoadPairStorageAsync(SyncPair pair, bool reset)
    {
        if (!_pairStorageBuilt || _changingPairStorage) return;
        _pairStorageLoad?.Cancel();
        using var load = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        _pairStorageLoad = load;
        var generation = ++_pairStorageGeneration;
        var pairId = pair.Id;
        if (reset)
        {
            _pairStorageFolders.Clear();
            _pairStorageSelected.Clear();
            _pairStorageOffset = 0;
            _pairStorageFileLimit = 50;
            _pairStorageOverview = null;
            _pairStorageRows.Children.Clear();
            _pairStorageCapacity.Children.Clear();
        }
        _pairStorageBulk.IsEnabled = _pairStorageSelected.Count > 0;
        _pairStorageBulk.Visibility = _pairStorageSelected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _pairStorageRefresh.IsEnabled = false;
        _pairStorageMessage.Text = "Loading saved storage information…";
        try
        {
            if (!SupportsManagedStorage(pair))
            {
                ShowPairStorageUnavailable(StorageProviderUnavailable(pair));
                return;
            }
            var offset = reset ? 0 : _pairStorageOffset;
            var overview = await LoadPairStorageOverviewAsync(pair, offset, _pairStorageSearch.Text.Trim(),
                _pairStorageFilter.SelectedItem as string, load.Token);
            if (!IsCurrentPairStorageLoad(generation, pair, load)) return;
            _pairStorageOverview = overview;
            _pairStorageFolders.AddRange(overview.Folders?.Folders ?? []);
            _pairStorageOffset = offset + (overview.Folders?.Folders.Count ?? 0);
            _pairStorageScope.Text = "Folder Pair: " + overview.Pair.Name + " — " + LabelForPair(overview.Pair);
            _pairStorageNote.Text = "Choices here apply to this saved pair immediately. Folder-tree storage choices on the Folders tab apply when you save the pair.";
            await RenderPairStorageCapacityAsync(pair, load, generation, pairId);
            if (!IsCurrentPairStorageLoad(generation, pair, load)) return;
            RenderPairStorageRows(overview);
        }
        catch (Exception error)
        {
            if (!IsCurrentPairStorageLoad(generation, pair, load)) return;
            ShowPairStorageUnavailable("Storage information could not be loaded: " +
                (error is OperationCanceledException ? "The request timed out; refresh to retry." : error.Message) +
                "\nSelect Refresh to try again.");
        }
        finally
        {
            if (ReferenceEquals(load, _pairStorageLoad)) _pairStorageLoad = null;
            if (_pairStorageBuilt && !_changingPairStorage) _pairStorageRefresh.IsEnabled = true;
        }
    }

    // Replaces only the external connection lookup and authority read. The provider guard, staleness
    // check and rendering below still run against the real contract.
    private async Task<SyncStorageOverview> LoadPairStorageOverviewAsync(SyncPair pair, int offset, string search,
        string? filter, CancellationToken cancellationToken)
    {
        if (PairStorageOverviewLoaderForTest is { } loader)
            return await loader(pair.Id, pair.ConnectionId, offset, search, filter, cancellationToken);
        var connection = _authorityHost.LoadAccountBinding(pair.ConnectionId)
            ?? throw new InvalidOperationException("Reconnect this account first.");
        return await _authorityHost.LoadStorageOverviewAsync(pair.Id, connection.ConnectionId, offset, search, filter, cancellationToken);
    }

    private static bool SupportsManagedStorage(SyncPair pair) =>
        pair.Provider is SyncProviders.Dropbox or SyncProviders.GoogleDrive;

    // The same capability gate guards every read and every write, so an unsupported provider is refused
    // by name before any account lookup could imply that reconnecting would solve it.
    private static void EnsureStorageTargetSupported(SyncPair pair)
    {
        if (!SupportsManagedStorage(pair)) throw new InvalidOperationException(StorageProviderUnavailable(pair));
    }

    // Unsupported endpoint types are refused before account lookup; supported clouds share one editor.
    private static string StorageProviderUnavailable(SyncPair pair) =>
            "Folder storage choices require a supported cloud pair. This pair is provided by " +
              (string.IsNullOrWhiteSpace(pair.Provider) ? "an unknown provider" : pair.Provider) +
              ", so its storage is managed there.";

    private async Task RenderPairStorageCapacityAsync(SyncPair pair, CancellationTokenSource load, int generation, string pairId)
    {
        _pairStorageCapacity.Children.Clear();
        var capacities = new WrapPanel();
        var volume = Path.GetPathRoot(pair.LocalRoot);
        if (!string.IsNullOrEmpty(volume))
        {
            string value;
            try { var info = new DriveInfo(volume); value = $"Used: {StorageBytes(info.TotalSize - info.AvailableFreeSpace)}\nFree: {StorageBytes(info.AvailableFreeSpace)}\nCapacity: {StorageBytes(info.TotalSize)}"; }
            catch (IOException) { value = "Drive Unavailable"; }
            var card = Card(Field("Local Drive " + volume, Text(value, 13)), 14);
            card.MinWidth = 240;
            card.Margin = new Thickness(0, 0, 10, 8);
            capacities.Children.Add(card);
        }
        if (_authorityHost.LoadAccountBinding(pair.ConnectionId)?.ConnectionId is { } connectionId)
        {
            SyncAccountStatus status;
            if (_storageCloudStatus.TryGetValue(connectionId, out var cached) && DateTimeOffset.UtcNow - cached.Checked < TimeSpan.FromMinutes(5)) status = cached.Status;
            else
            {
                status = await Task.Run(() => _authorityHost.GetAccountStatusAsync(true, load.Token, connectionId), load.Token);
                if (!IsCurrentPairStorageLoad(generation, pair, load)) return;
                _storageCloudStatus[connectionId] = (DateTimeOffset.UtcNow, status);
                RememberAccountStatus(status);
            }
            var value = status.SpaceUsage is { } usage ? usage.IsUnlimited ? $"Used: {StorageBytes(usage.UsedBytes)}\nCapacity: No storage limit" : $"Used: {StorageBytes(usage.UsedBytes)}\nAvailable: {StorageBytes(Math.Max(0, usage.AllocatedBytes - usage.UsedBytes))}\nCapacity: {StorageBytes(usage.AllocatedBytes)}" :
                !status.HasProtectedCredential ? "Account Disconnected" : "Cloud capacity is unavailable. Refresh to retry.";
            var card = Card(Field(SharingAccountLabel(connectionId), Text(value, 13)), 14);
            card.MinWidth = 240;
            card.MaxWidth = 420;
            card.Margin = new Thickness(0, 0, 10, 8);
            capacities.Children.Add(card);
        }
        var details = new Expander { Header = CommandIcons.Label("Drive and Cloud Space", "storage"), Content = capacities,
            IsExpanded = _pairStorageCapacityExpanded, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
        details.Expanded += (_, _) => _pairStorageCapacityExpanded = true;
        details.Collapsed += (_, _) => _pairStorageCapacityExpanded = false;
        _pairStorageCapacity.Children.Add(details);
    }

    // Defaults, inherited versus explicit folder policy, excluded/ignored participation and
    // pending-versus-applied storage changes each stay separately visible here.
    private void RenderPairStorageRows(SyncStorageOverview data)
    {
        var pair = data.Pair;
        _pairStorageRows.Children.Clear();
        var defaultRow = new DockPanel();
        var changeDefault = Button("Change Default", (sender, _) => OpenPairStorageChoiceMenu((UIElement)sender, null), true);
        DockPanel.SetDock(changeDefault, Dock.Right);
        defaultRow.Children.Add(changeDefault);
        defaultRow.Children.Add(Text("New Files: " + StorageLabel(SyncDirectoryStoragePolicy.Default(pair)), 14, true));
        var defaultCard = Card(defaultRow, 10);
        defaultCard.Margin = new Thickness(0, 0, 0, 4);
        _pairStorageRows.Children.Add(defaultCard);
        _pairStorageRows.Children.Add(Text("Saved Default for New Files. A Folder Override replaces it for the exact folder it names, and a file override made in File Explorer replaces it for that exact file.", 12, brush: "MutedInk", margin: new Thickness(0, 0, 0, 10)));
        _pairStorageRows.Children.Add(new Expander { Header = "How Online Only Works", FontSize = 12, Margin = new Thickness(0, 0, 0, 10),
            Content = Text("New local files upload first. Online-only storage releases their local contents only after the cloud provider has the exact saved version. Changed or busy files stay local and retry safely.", 12, brush: "MutedInk", margin: new Thickness(0, 6, 0, 6)) });
        RenderPairStorageFileOverrides(pair);
        foreach (var folder in _pairStorageFolders)
        {
            var selection = new CheckBox { IsChecked = _pairStorageSelected.Contains(folder.RelativePath), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0), ToolTip = "Select this folder for a storage change; this does not alter sync participation." };
            System.Windows.Automation.AutomationProperties.SetName(selection, "Select " + folder.RelativePath);
            selection.SetResourceReference(StyleProperty, "FolderTreeCheckBox");
            selection.Checked += (_, _) =>
            {
                if (_pairStorageSelected.Count >= 100 && !_pairStorageSelected.Contains(folder.RelativePath))
                {
                    selection.IsChecked = false;
                    _pairStorageMessage.Text = "Choose up to 100 folders in one change.";
                    return;
                }
                _pairStorageSelected.Add(folder.RelativePath);
                _pairStorageBulk.IsEnabled = true;
                _pairStorageBulk.Visibility = Visibility.Visible;
            };
            selection.Unchecked += (_, _) =>
            {
                _pairStorageSelected.Remove(folder.RelativePath);
                _pairStorageBulk.IsEnabled = _pairStorageSelected.Count > 0;
                _pairStorageBulk.Visibility = _pairStorageSelected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            };
            var preference = SyncDirectoryStoragePolicy.Resolve(pair, folder.RelativePath);
            var inherited = !(pair.DirectoryStorageRules ?? []).Any(rule => string.Equals(rule.FolderPath, folder.RelativePath, StringComparison.OrdinalIgnoreCase));
            var pending = (pair.DirectoryStorageApplyPaths ?? []).Any(path => SyncDirectoryStoragePolicy.Contains(path, folder.RelativePath) || SyncDirectoryStoragePolicy.Contains(folder.RelativePath, path));
            var actions = new WrapPanel();
            actions.Children.Add(selection);
            actions.Children.Add(Button("Change Storage", (sender, _) => OpenPairStorageChoiceMenu((UIElement)sender, [folder.RelativePath]), true));
            var focus = AsyncButton("Show This Folder", async (_, _) => await OpenPairStorageAsync(pair.Id, folder.RelativePath), true);
            focus.ToolTip = "Filter this pair's storage list to this folder path.";
            actions.Children.Add(focus);
            var local = folder.Presence switch { SyncFolderPresence.CloudOnly => "Not on This Computer", SyncFolderPresence.OnlineOnly => "Online Only",
                SyncFolderPresence.LocalUnloaded => "Not Inspected (Excluded)", SyncFolderPresence.Excluded => "Not Present", _ => "Folder Present; Contents May Be Mixed" };
            var included = SyncSelectionPolicy.IsIncluded(folder.RelativePath, pair, true);
            selection.IsEnabled = included;
            var participation = SyncIgnorePolicy.IsIgnored(folder.RelativePath, true, pair.IgnoreRules ?? []) ? "Ignored" : included ? "Selected" : "Not Selected";
            var description = "Storage Preference: " + StorageLabel(preference) +
                (participation != "Selected" ? "\nSync Participation: " + participation : "") + (pending ? "\nStorage Change: Pending" : "");
            var details = "Folder Pair: " + pair.Name + "\nLast Observed Availability: " + local +
                "\nSync Participation: " + participation + "\nPreference Source: " + (inherited ? "Inherited" : "Folder Override");
            _pairStorageRows.Children.Add(SharingListRow(preference == SyncDirectoryStorage.CloudOnly ? "cloud" : "folder", folder.RelativePath, "", description, actions, details));
        }
        _pairStorageMore.Visibility = data.Folders?.HasMore == true ? Visibility.Visible : Visibility.Collapsed;
        _pairStorageMessage.Text = data.Folders is null ? "No indexed folders yet. Run a sync to inspect this pair; opening Storage does not start one." :
            $"{_pairStorageFolders.Count:N0} of {data.Folders.TotalMatchingFolders:N0} Matching Folders\nLast Synced File Size: {StorageBytes(data.KnownSyncedBytes)} across {data.KnownSyncedFiles:N0} files\n" +
            (data.IndexedUtc is { } date ? "Availability Last Indexed: " + date.ToLocalTime().ToString("g") : "Availability has not been indexed.");
    }

    private void RenderPairStorageFileOverrides(SyncPair pair)
    {
        var search = _pairStorageSearch.Text.Trim();
        var rules = (pair.FileStorageRules ?? []).Where(rule => search.Length == 0 || rule.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rules.Length == 0) return;
        var contents = new StackPanel();
        contents.Children.Add(Text("File choices made in Explorer override their folder's storage setting. They do not change whether a file participates in sync.", 12, brush: "MutedInk", margin: new Thickness(0, 8, 0, 12)));
        foreach (var rule in rules.Take(_pairStorageFileLimit))
        {
            var actions = new WrapPanel();
            actions.Children.Add(AsyncButton("Use Parent Setting", async (_, _) => await ResetPairStorageFileChoiceAsync(pair, rule), true));
            contents.Children.Add(SharingListRow(rule.Storage == SyncDirectoryStorage.CloudOnly ? "cloud" : "file", Path.GetFileName(rule.RelativePath),
                "Relative Path: " + rule.RelativePath,
                "Storage Preference: " + StorageLabel(rule.Storage) + "\nParent Setting: " + StorageLabel(SyncDirectoryStoragePolicy.Resolve(pair with { FileStorageRules = null }, rule.RelativePath)), actions));
        }
        if (rules.Length > _pairStorageFileLimit)
            contents.Children.Add(Button("Show More File Overrides", (_, _) =>
            {
                _pairStorageFileLimit += 50;
                if (_pairStorageOverview is { } data) RenderPairStorageRows(data);
            }, true));
        var expander = new Expander { Header = $"File Overrides ({rules.Length:N0})", Content = contents, Margin = new Thickness(0, 0, 0, 16),
            IsExpanded = _pairStorageFilesExpanded, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
        expander.Expanded += (_, _) => _pairStorageFilesExpanded = true;
        expander.Collapsed += (_, _) => _pairStorageFilesExpanded = false;
        System.Windows.Automation.AutomationProperties.SetName(expander, "File Overrides");
        _pairStorageRows.Children.Add(expander);
    }

    private void OpenPairStorageChoiceMenu(UIElement target, string[]? folders)
    {
        if (_pairStorageOverview is not { } data || _changingPairStorage || folders is { Length: 0 }) return;
        var menu = WorkspaceMenu();
        menu.PlacementTarget = target;
        MenuAction(menu, "Make Available Offline", async (_, _) => await ApplyPairStorageChoiceAsync(data.Pair, folders, SyncDirectoryStorage.AlwaysAvailable));
        MenuAction(menu, "Make Online Only", async (_, _) => await ApplyPairStorageChoiceAsync(data.Pair, folders, SyncDirectoryStorage.CloudOnly));
        if (folders is not null) MenuAction(menu, "Use Parent Setting", async (_, _) => await ApplyPairStorageChoiceAsync(data.Pair, folders, null));
        menu.IsOpen = true;
    }

    private async Task ApplyPairStorageChoiceAsync(SyncPair pair, string[]? folders, SyncDirectoryStorage? storage)
    {
        if (_changingPairStorage || _runActive) { ShowStatus("Wait for the current operation before changing storage."); return; }
        if (!SupportsManagedStorage(pair)) { ShowStatus(StorageProviderUnavailable(pair)); return; }
        if (!await PrepareToLeaveCurrentPairDraftAsync()) return;
        var confirmation = new PairStorageConfirmation("Change Storage",
            folders is null ? "Change this pair's default storage?" : $"Change storage for {folders.Length} selected folder(s)?",
            LabelForPair(pair) + "\nFolder Pair: " + pair.Name + "\nStorage: " + (storage is { } value ? StorageLabel(value) : "Use Parent Setting") +
            "\nSync selection stays unchanged. Downloads may use local disk space. Online-only changes preserve unsynced or changed files; explicit child overrides remain in effect.", "Apply Storage");
        if (!ConfirmPairStorage(confirmation)) return;
        if (!TryRevalidatePairStorageScope(pair, out var current)) return;
        _changingPairStorage = true;
        SetPairStorageBusy(true);
        try
        {
            ShowStatus("Applying the selected storage preference; verifying its account, paths and current policy.");
            var result = await DispatchPairStorageChangeAsync(current, folders, storage);
            _pairSet = await _authorityHost.LoadPairsAsync();
            RefreshPairRows(current.Id);
            await RefreshExplorerIntegrationAsync();
            ShowStatus(result.Failures.Count == 0 ? $"Storage preference saved. Updated {result.FilesChanged:N0} files and {result.DirectoriesChanged:N0} folders." :
                $"Storage preference saved. {result.Failures.Count:N0} items remain local or unchanged and will retry safely. " +
                string.Join(Environment.NewLine, result.Failures.Select(SyncActivityText.Safe)));
        }
        catch (Exception error) { ShowStatus("Storage change could not finish: " + error.Message); }
        finally
        {
            _changingPairStorage = false;
            SetPairStorageBusy(false);
            if (SavedPairForStorage(pair.Id) is { } refreshed) await LoadPairStorageAsync(refreshed, reset: true);
            else ShowPairStorageUnavailable("This folder pair is no longer saved. Storage choices are unavailable.");
        }
    }

    private async Task ResetPairStorageFileChoiceAsync(SyncPair pair, SyncFileStorageRule rule)
    {
        if (_changingPairStorage || _runActive) { ShowStatus("Wait for the current operation before changing storage."); return; }
        if (!SupportsManagedStorage(pair)) { ShowStatus(StorageProviderUnavailable(pair)); return; }
        if (!await PrepareToLeaveCurrentPairDraftAsync()) return;
        var confirmation = new PairStorageConfirmation("Use Parent Setting", "Remove this file's storage override?",
            LabelForPair(pair) + "\nFolder Pair: " + pair.Name + "\nRelative Path: " + rule.RelativePath +
            "\nParent Setting: " + StorageLabel(SyncDirectoryStoragePolicy.Resolve(pair with { FileStorageRules = null }, rule.RelativePath)) +
            "\nThe file stays selected for sync. A renamed file is followed by its identity; an unrelated replacement is not targeted.", "Use Parent Setting");
        if (!ConfirmPairStorage(confirmation)) return;
        if (!TryRevalidatePairStorageScope(pair, out var current)) return;
        _changingPairStorage = true;
        SetPairStorageBusy(true);
        try
        {
            ShowStatus("Removing the selected file override; verifying its retained identity and parent storage preference.");
            var result = await DispatchPairStorageFileResetAsync(current, rule);
            _pairSet = await _authorityHost.LoadPairsAsync();
            RefreshPairRows(current.Id);
            await RefreshExplorerIntegrationAsync();
            ShowStatus(result.Failures.Count == 0 ? "File override removed. This file now uses its parent folder's storage setting." :
                "File override removed. Some storage changes will retry safely: " +
                string.Join(Environment.NewLine, result.Failures.Select(SyncActivityText.Safe)));
        }
        catch (Exception error) { ShowStatus("Storage change could not finish: " + error.Message); }
        finally
        {
            _changingPairStorage = false;
            SetPairStorageBusy(false);
            if (SavedPairForStorage(pair.Id) is { } refreshed) await LoadPairStorageAsync(refreshed, reset: true);
            else ShowPairStorageUnavailable("This folder pair is no longer saved. Storage choices are unavailable.");
        }
    }

    private bool ConfirmPairStorage(PairStorageConfirmation confirmation) =>
        PairStorageConfirmationForTest?.Invoke(confirmation) ?? AppDialogWindow.Confirm(this, confirmation.Title,
            confirmation.Prompt, confirmation.Details, confirmation.Action);

    // A confirmed choice is only despatched while the exact pair, account and policy revision the user
    // saw are still current. The authority independently validates the optimistic revision on write.
    private bool TryRevalidatePairStorageScope(SyncPair requested, out SyncPair current)
    {
        var saved = SavedPairForStorage(requested.Id);
        current = saved ?? requested;
        var loaded = _pairStorageOverview?.Pair;
        var stale = saved is null || loaded is null || _editingNewPair ||
            requested.ConnectionId != current.ConnectionId || requested.PolicyRevision != current.PolicyRevision ||
            requested.LocalRoot != current.LocalRoot || requested.DropboxFolderId != current.DropboxFolderId ||
            !string.Equals(loaded.Id, current.Id, StringComparison.Ordinal) ||
            !string.Equals(loaded.ConnectionId, current.ConnectionId, StringComparison.Ordinal) ||
            loaded.PolicyRevision != current.PolicyRevision ||
            PairList.SelectedItem is not PairRow row || !string.Equals(row.Id, current.Id, StringComparison.Ordinal);
        if (!stale) return true;
        ShowStatus("This folder pair changed while the storage choice was open. Refresh storage and choose again.");
        return false;
    }

    // Replaces only the external provider effect. The capability gate, the ownership/target refusal and
    // the exact account refusal below still run, and the authority still validates the expected policy
    // revision.
    private async Task<SyncCloudAvailabilityResult> DispatchPairStorageChangeAsync(SyncPair pair,
        IReadOnlyList<string>? folders, SyncDirectoryStorage? storage)
    {
        EnsureStorageTargetSupported(pair);
        EnsureStorageOwnershipAvailable(pair);
        if (PairStorageChangeForTest is { } dispatch)
            return await dispatch(pair.Id, pair.ConnectionId, folders, storage, pair.PolicyRevision);
        var account = _authorityHost.LoadAccountBinding(pair.ConnectionId)
            ?? throw new InvalidOperationException("Reconnect this account first.");
        return await _authorityHost.ChangeManagedStorageBatchAsync(pair.Id, account.ConnectionId, folders, storage, pair.PolicyRevision,
            onProgress: ReportPairStorageProgressAsync);
    }

    private async Task<SyncCloudAvailabilityResult> DispatchPairStorageFileResetAsync(SyncPair pair, SyncFileStorageRule rule)
    {
        EnsureStorageTargetSupported(pair);
        EnsureStorageOwnershipAvailable(pair);
        if (PairStorageFileResetForTest is { } dispatch)
            return await dispatch(pair.Id, pair.ConnectionId, rule, pair.PolicyRevision);
        var account = _authorityHost.LoadAccountBinding(pair.ConnectionId)
            ?? throw new InvalidOperationException("Reconnect this account first.");
        return await _authorityHost.ResetManagedFileStorageAsync(pair.Id, account.ConnectionId, rule, pair.PolicyRevision,
            onProgress: ReportPairStorageProgressAsync);
    }

    private Task ReportPairStorageProgressAsync(SyncAuthorityBatchProgress entry) =>
        Dispatcher.InvokeAsync(() => ShowStatus(entry.Comparison is { } detail
            ? SyncProgressDescription.Describe(detail) : entry.State)).Task;
}

