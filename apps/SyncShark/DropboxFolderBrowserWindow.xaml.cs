using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Massdex.SyncShark;

public partial class DropboxFolderBrowserWindow : Window
{
    private readonly IDropboxFolderBrowserService _browserService;
    private readonly DropboxFolderGraphBudget _graphBudget;
    private readonly bool _allowFolderCreation;
    private readonly Func<DropboxFolderEntry, bool>? _selectionPredicate;
    private readonly string _selectionRestrictionMessage;
    private readonly string _providerName;
    public string BrowserTitle => "Browse " + _providerName;
    private DropboxFolderNode? _selected;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _createActive;
    private int _loadVersion;
    // WF1: expanding a folder or running a search continues the provider's existing paging API automatically, in
    // bounded sequential calls. This is a per-expansion bound, not a new scan: only the opened level's sibling
    // pages are requested, never descendants, and the retained-graph budget remains the hard ceiling.
    private const int MaximumAutomaticPages = 40;

    public ObservableCollection<DropboxFolderNode> Roots { get; } = [];
    public DropboxFolderEntry? SelectedFolder { get; private set; }

    public DropboxFolderBrowserWindow(IDropboxFolderBrowserService browserService, string connectionLabel,
        string provider = SyncProviders.Dropbox)
        : this(browserService, connectionLabel, DropboxFolderGraphBudget.CreateDefault(), true, null, string.Empty, provider)
    {
    }

    internal DropboxFolderBrowserWindow(IDropboxFolderBrowserService browserService, string connectionLabel,
        DropboxFolderGraphBudget graphBudget, bool allowFolderCreation = true,
        Func<DropboxFolderEntry, bool>? selectionPredicate = null,
        string? selectionRestrictionMessage = null, string provider = SyncProviders.Dropbox)
    {
        _providerName = ProviderPresentation.For(provider).Name;
        _browserService = browserService;
        _graphBudget = graphBudget ?? throw new ArgumentNullException(nameof(graphBudget));
        _allowFolderCreation = allowFolderCreation;
        _selectionPredicate = selectionPredicate;
        _selectionRestrictionMessage = string.IsNullOrWhiteSpace(selectionRestrictionMessage)
            ? "This folder is not available for this selection."
            : selectionRestrictionMessage;
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionLabel);
        InitializeComponent();
        Title = BrowserTitle + " folders";
        BreadcrumbText.Text = _providerName + " /";
        BrowserStatus.Text = "Loading " + _providerName + " folders…";
        FolderSearchBox.ToolTip = "Search folders in the connected " + _providerName + " without first loading the whole hierarchy.";
        System.Windows.Automation.AutomationProperties.SetName(AccountText, _providerName + " folder browser account");
        System.Windows.Automation.AutomationProperties.SetName(FolderSearchBox, "Search " + _providerName + " folders");
        System.Windows.Automation.AutomationProperties.SetName(BreadcrumbText, "Selected " + _providerName + " breadcrumb");
        System.Windows.Automation.AutomationProperties.SetName(FolderTree, _providerName + " folders");
        System.Windows.Automation.AutomationProperties.SetName(NewFolderName, "New " + _providerName + " folder name");
        System.Windows.Automation.AutomationProperties.SetName(CreateFolderButton, "Create " + _providerName + " folder in selected folder");
        LoadMoreButton.ToolTip = "Load the next folder page for the selected location or search.";
        if (!_allowFolderCreation)
        {
            CreateFolderPanel.Visibility = Visibility.Collapsed;
            CreateFolderContextMenuItem.Visibility = Visibility.Collapsed;
        }
        AccountText.Text = connectionLabel;
        AccountText.ToolTip = $"Browsing with {connectionLabel}";
        DataContext = this;
        var root = DropboxFolderNode.CreateRoot(_graphBudget, rootName: _providerName);
        Roots.Add(root);
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await SearchAsync();
        };
        // The expanded root can start loading before Window.Loaded. Both entry points
        // share the existing load guard; opening the window must not start a second
        // sequence and discard the first sequence's cursor/budget outcome.
        Loaded += async (_, _) => await LoadNodeAsync(root, refresh: false);
    }

    internal DropboxFolderGraphBudget GraphBudget => _graphBudget;

    protected override void OnClosed(EventArgs e)
    {
        Interlocked.Increment(ref _loadVersion);
        _lifetime.Cancel();
        _searchTimer.Stop();
        _lifetime.Dispose();
        base.OnClosed(e);
    }

    private async void FolderTree_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: DropboxFolderNode node })
            await LoadNodeAsync(node, refresh: false);
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selected = e.NewValue as DropboxFolderNode;
        var canSelect = CanSelect(_selected?.Entry);
        SelectFolderButton.IsEnabled = canSelect;
        BreadcrumbText.Text = _selected is null ? _providerName + " /" : _providerName + "  ›  " + (_selected.Path == "/" ? string.Empty : _selected.Path.TrimStart('/').Replace("/", "  ›  "));
        BrowserStatus.Text = _selected?.Entry is null
            ? _providerName + " root. Choose a specific folder to bind."
            : canSelect
                ? $"Selected {_selected.Path}. Its stable folder ID will be verified again when the pair is bound."
                : _selectionRestrictionMessage;
        UpdateLoadMoreButton();
    }

    private void FolderTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item is null) return;
        item.IsSelected = true;
        item.Focus();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(FolderSearchBox.Text) && (_selected is null || _selected == Roots[0]))
            await SearchAsync();
        else
            await LoadNodeAsync(_selected ?? Roots[0], refresh: true);
    }

    private async Task<string?> LoadNodeAsync(DropboxFolderNode node, bool refresh)
    {
        if (node.IsLoading && !refresh) return "A refresh for this folder is already in progress.";
        if (!refresh && node.IsLoaded) return null;
        var version = Interlocked.Increment(ref _loadVersion);
        node.ActiveOperationVersion = version;
        node.IsLoading = true;
        // Refresh explicitly replaces an in-flight listing; keep that recovery action available.
        RefreshButton.IsEnabled = true;
        BrowserStatus.Text = $"Loading {node.Path}…";
        try
        {
            var page = await _browserService.ListFoldersAsync(node.Entry?.FolderId, _lifetime.Token);
            if (version != _loadVersion || node.ActiveOperationVersion != version) return null;
            node.ReplacePage(page, isSearchResults: false);
            var continued = await ContinueRemainingPagesAsync(node, version);
            if (version != _loadVersion || node.ActiveOperationVersion != version) return null;
            if (continued is null)
                BrowserStatus.Text = node.PaginationBlockedReason ?? (page.Folders.Count == 0 && node.LoadedChildCount == 0
                    ? $"{node.Path} has no child folders."
                    : $"Loaded all {node.LoadedChildCount:N0} child folder{(node.LoadedChildCount == 1 ? string.Empty : "s")}.");
            UpdateLoadMoreButton();
            return continued;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return "The folder browser was closed."; }
        catch (Exception exception)
        {
            if (version != _loadVersion || node.ActiveOperationVersion != version) return null;
            var message = _providerName + " folders could not be loaded: " + exception.Message;
            BrowserStatus.Text = message;
            return message;
        }
        finally
        {
            if (node.ActiveOperationVersion == version) node.IsLoading = false;
            if (version == _loadVersion)
            {
                RefreshButton.IsEnabled = true;
                UpdateLoadMoreButton();
            }
        }
    }

    private async void LoadMore_Click(object sender, RoutedEventArgs e) => await LoadMoreAsync();

    // WF1: the one bounded continuation used by a folder expansion and by a search alike. It keeps the visible
    // busy text and the loaded count truthful, stops on the first error or blocked budget without claiming the
    // level is complete, and returns null only when every page of this level has actually arrived.
    private async Task<string?> ContinueRemainingPagesAsync(DropboxFolderNode node, int version)
    {
        var requested = 0;
        while (node.CanLoadMore && requested < MaximumAutomaticPages)
        {
            var cursor = node.ContinueCursor;
            if (cursor is null) break;
            if (version != _loadVersion || node.ActiveOperationVersion != version || _lifetime.IsCancellationRequested)
                return "The folder browser stopped loading before this level was complete.";
            BrowserStatus.Text = "Loading more of " + node.Path + "… " + node.LoadedChildCount.ToString("N0") +
                " folder(s) loaded so far.";
            DropboxFolderPage page;
            try
            {
                page = node.IsSearchResults
                    ? await _browserService.ContinueFolderSearchAsync(cursor, _lifetime.Token)
                    : await _browserService.ContinueFoldersAsync(cursor, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return "The folder browser was closed.";
            }
            catch (Exception exception)
            {
                if (version != _loadVersion || node.ActiveOperationVersion != version) return null;
                var message = "More " + _providerName + " folders could not be loaded after " +
                    node.LoadedChildCount.ToString("N0") + " folder(s): " + exception.Message +
                    " The list is incomplete; use Load more or Refresh to continue.";
                BrowserStatus.Text = message;
                return message;
            }
            if (version != _loadVersion || node.ActiveOperationVersion != version) return null;
            try
            {
                node.AppendPage(page);
            }
            catch (InvalidDataException invalid)
            {
                // A blocked budget or a repeated cursor is reported by the node itself and never retried.
                var message = node.PaginationBlockedReason ?? invalid.Message;
                BrowserStatus.Text = message + " " + node.LoadedChildCount.ToString("N0") +
                    " folder(s) are shown; the list is incomplete.";
                return message;
            }
            requested++;
        }
        if (node.CanLoadMore && requested >= MaximumAutomaticPages)
        {
            var message = "Showing the first " + node.LoadedChildCount.ToString("N0") +
                " folder(s) of this level. Use Load more to continue; the list is not complete yet.";
            BrowserStatus.Text = message;
            return message;
        }
        return null;
    }

    private async Task LoadMoreAsync()
    {
        var node = _selected ?? Roots[0];
        if (node.IsLoading || node.ContinueCursor is null) return;
        if (!node.CanLoadMore)
        {
            BrowserStatus.Text = node.PaginationBlockedReason ?? "Folder continuation is not available.";
            UpdateLoadMoreButton();
            return;
        }
        var version = Interlocked.Increment(ref _loadVersion);
        node.ActiveOperationVersion = version;
        node.IsLoading = true;
        LoadMoreButton.IsEnabled = false;
        try
        {
            var cursor = node.ContinueCursor;
            var page = node.IsSearchResults
                ? await _browserService.ContinueFolderSearchAsync(cursor, _lifetime.Token)
                : await _browserService.ContinueFoldersAsync(cursor, _lifetime.Token);
            if (version != _loadVersion || node.ActiveOperationVersion != version) return;
            node.AppendPage(page);
            BrowserStatus.Text = node.PaginationBlockedReason ??
                                 ($"Loaded {node.LoadedChildCount:N0} folder result(s)" +
                                  (node.ContinueCursor is null ? "." : "; more remain."));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (version == _loadVersion && node.ActiveOperationVersion == version)
                BrowserStatus.Text = "The next " + _providerName + " folder page could not be loaded: " + exception.Message;
        }
        finally
        {
            if (node.ActiveOperationVersion == version) node.IsLoading = false;
            if (version == _loadVersion) UpdateLoadMoreButton();
        }
    }

    private void FolderSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async Task SearchAsync()
    {
        var query = FolderSearchBox.Text.Trim();
        var root = Roots[0];
        if (query.Length == 0)
        {
            _selected = root;
            await LoadNodeAsync(root, refresh: true);
            return;
        }
        var version = Interlocked.Increment(ref _loadVersion);
        root.ActiveOperationVersion = version;
        root.IsLoading = true;
        RefreshButton.IsEnabled = true;
        BrowserStatus.Text = $"Searching the connected {_providerName} for folders matching ‘{query}’…";
        try
        {
            var page = await _browserService.SearchFoldersAsync(query, cancellationToken: _lifetime.Token);
            if (version != _loadVersion || root.ActiveOperationVersion != version ||
                !string.Equals(query, FolderSearchBox.Text.Trim(), StringComparison.Ordinal))
                return;
            root.ReplacePage(page, isSearchResults: true);
            _selected = root;
            var continued = await ContinueRemainingPagesAsync(root, version);
            if (version != _loadVersion || root.ActiveOperationVersion != version) return;
            if (continued is null)
                BrowserStatus.Text = root.PaginationBlockedReason ??
                                     $"Loaded all {root.LoadedChildCount:N0} matching folder(s).";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (version == _loadVersion && root.ActiveOperationVersion == version)
                BrowserStatus.Text = _providerName + " folder search could not be completed: " + exception.Message;
        }
        finally
        {
            if (root.ActiveOperationVersion == version) root.IsLoading = false;
            if (version == _loadVersion)
            {
                RefreshButton.IsEnabled = true;
                UpdateLoadMoreButton();
            }
        }
    }

    private void UpdateLoadMoreButton()
    {
        if (LoadMoreButton is null) return;
        var node = _selected ?? Roots.FirstOrDefault();
        var hasMore = node?.ContinueCursor is not null;
        LoadMoreButton.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreButton.IsEnabled = hasMore && node?.IsLoading != true && node?.CanLoadMore == true;
        LoadMoreButton.ToolTip = node?.PaginationBlockedReason ??
                                 "Load the next folder page for the selected location or search.";
    }

    private async void CreateFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!_allowFolderCreation)
        {
            BrowserStatus.Text = _selectionRestrictionMessage;
            return;
        }
        if (_createActive) return;
        _createActive = true;
        CreateFolderButton.IsEnabled = false;
        NewFolderName.IsEnabled = false;
        var parent = _selected ?? Roots[0];
        BrowserStatus.Text = $"Creating a folder inside {parent.Path}…";
        try
        {
            var created = await _browserService.CreateFolderAsync(parent.Entry?.FolderId, NewFolderName.Text,
                _lifetime.Token);
            NewFolderName.Clear();
            var refreshError = await LoadNodeAsync(parent, refresh: true);
            BrowserStatus.Text = refreshError is null
                ? $"Created {created.Path}. Select it in the refreshed folder list."
                : $"Created {created.Path}, but the list could not be refreshed. Do not repeat creation; use Refresh. {refreshError}";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { BrowserStatus.Text = "Folder creation could not be confirmed. Refresh before retrying: " + exception.Message; }
        finally
        {
            _createActive = false;
            if (!_lifetime.IsCancellationRequested)
            {
                CreateFolderButton.IsEnabled = true;
                NewFolderName.IsEnabled = true;
            }
        }
    }

    private void FocusCreateFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!_allowFolderCreation)
        {
            BrowserStatus.Text = _selectionRestrictionMessage;
            return;
        }
        NewFolderName.Focus();
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.Entry is null) { BrowserStatus.Text = "Choose a specific " + _providerName + " folder first."; return; }
        if (!CanSelect(_selected.Entry)) { BrowserStatus.Text = _selectionRestrictionMessage; return; }
        SelectedFolder = _selected.Entry;
        DialogResult = true;
    }

    internal bool AllowsFolderCreationForTest => _allowFolderCreation;
    internal bool CanSelectFolderForTest(DropboxFolderEntry entry) => CanSelect(entry);

    private bool CanSelect(DropboxFolderEntry? entry)
    {
        if (entry is null) return false;
        if (_selectionPredicate is null) return true;
        try { return _selectionPredicate(entry); }
        catch { return false; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }
}

public sealed class DropboxFolderNode : INotifyPropertyChanged
{
    private readonly DropboxFolderGraphBudget _graphBudget;
    private readonly HashSet<string> _seenCursors = new(StringComparer.Ordinal);
    private int _retainedPageCount;
    private bool? _isChecked;
    private bool _isExpanded;
    private bool _isFilterVisible = true;
    private string _status = string.Empty;

    // Every consumer of a node (the chevron handler, saved-path expansion and
    // pagination) shares this gate.  Previously a second consumer observed
    // IsLoading and returned immediately, which made an in-flight page look like
    // a complete empty folder and produced the false "no longer exists" warning.
    internal SemaphoreSlim LoadGate { get; } = new(1, 1);

    private DropboxFolderNode(string name, string path, DropboxFolderEntry? entry,
        DropboxFolderGraphBudget graphBudget)
    {
        Name = name;
        Path = path;
        Entry = entry;
        IsPlaceholder = false;
        _graphBudget = graphBudget;
        Children.Add(Placeholder(graphBudget));
    }

    public string Name { get; }
    public string Path { get; }
    public DropboxFolderEntry? Entry { get; }
    public bool IsPlaceholder { get; }
    public bool CanSelect => !IsPlaceholder;
    public ObservableCollection<DropboxFolderNode> Children { get; } = [];
    public bool IsLoaded { get; private set; }
    public bool IsLoading { get; set; }
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); }
    }
    public bool IsFilterVisible
    {
        get => _isFilterVisible;
        private set { if (_isFilterVisible == value) return; _isFilterVisible = value; OnPropertyChanged(nameof(IsFilterVisible)); }
    }
    public bool? IsChecked
    {
        get => _isChecked;
        private set { if (_isChecked == value) return; _isChecked = value; OnPropertyChanged(nameof(IsChecked)); }
    }
    public string Status
    {
        get => _status;
        private set { if (_status == value) return; _status = value; OnPropertyChanged(nameof(Status)); }
    }
    public string? ContinueCursor { get; private set; }
    public bool IsSearchResults { get; private set; }
    public int LoadedChildCount => Children.Count(child => child.Entry is not null);
    public bool CanLoadMore => ContinueCursor is not null && PaginationBlockedReason is null;
    public bool CanExpand => !IsPlaceholder && (!IsLoaded || Children.Any(child => child.Entry is not null));
    /// <summary>
    /// Bounded left indentation for this row. The lazy editors keep the folder name and the expand
    /// chevron inside the visible panel even when a hierarchy is nested deeply.
    /// </summary>
    public System.Windows.Thickness IndentMargin =>
        new(FolderVirtualizingPanel.BoundedIndent(Depth), 0, 0, 0);
    private int Depth
    {
        get
        {
            var normalized = PilotPath.NormalizeDropboxRoot(Path);
            return normalized == "/" ? 0 : normalized.Count(character => character == '/');
        }
    }
    internal int ActiveOperationVersion { get; set; }
    internal string? PaginationBlockedReason { get; private set; }
    internal int RetainedPageCount => _retainedPageCount;
    internal DropboxFolderGraphBudget GraphBudget => _graphBudget;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetChecked(bool? value) => IsChecked = value;
    public void SetStatus(string? value) => Status = value ?? string.Empty;
    public FolderStorageChoice? StorageChoice { get; private set; }
    public void SetStorageChoice(FolderStorageChoice? value)
    { StorageChoice = value; OnPropertyChanged(nameof(StorageChoice)); }
    public void SetFilterVisible(bool value) => IsFilterVisible = value;

    internal static DropboxFolderNode CreateRoot(DropboxFolderGraphBudget graphBudget,
        DropboxFolderEntry? rootEntry = null, string? rootName = null) =>
        new(rootName ?? rootEntry?.Name ?? "Dropbox", "/", rootEntry, graphBudget) { IsExpanded = true };

    public void ReplacePage(DropboxFolderPage page, bool isSearchResults)
    {
        ArgumentNullException.ThrowIfNull(page);
        var entries = UniqueEntries(page.Folders);
        var oldCost = MeasureOwnedSubtree();
        var failure = _graphBudget.TryReplace(oldCost.Pages, oldCost.Folders, 1, entries.Count);
        if (failure is not null)
        {
            BlockPagination(failure);
            throw new InvalidDataException(failure);
        }

        Children.Clear();
        foreach (var entry in entries)
            Children.Add(new DropboxFolderNode(entry.Name, entry.Path, entry, _graphBudget));
        SortChildren();
        _retainedPageCount = 1;
        _seenCursors.Clear();
        ContinueCursor = page.ContinueCursor;
        if (page.ContinueCursor is not null) _seenCursors.Add(page.ContinueCursor);
        IsSearchResults = isSearchResults;
        IsLoaded = true;
        PaginationBlockedReason = null;
        OnPropertyChanged(nameof(CanExpand));
    }

    public void ReplaceChildren(IEnumerable<DropboxFolderEntry> entries) =>
        ReplacePage(new DropboxFolderPage(entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray(), null), isSearchResults: false);

    public void AppendPage(DropboxFolderPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!CanLoadMore)
            throw new InvalidOperationException(PaginationBlockedReason ?? "This folder has no continuation page.");

        if (page.ContinueCursor is not null && _seenCursors.Contains(page.ContinueCursor))
        {
            var cycle = "Dropbox returned a repeated or cyclic continuation cursor. Loading more has been stopped for this folder.";
            BlockPagination(cycle);
            throw new InvalidDataException(cycle);
        }

        var known = Children.Where(child => child.Entry is not null)
            .Select(child => child.Entry!.FolderId).ToHashSet(StringComparer.Ordinal);
        var additions = page.Folders.Where(entry => known.Add(entry.FolderId)).ToArray();
        var failure = _graphBudget.TryReserve(1, additions.Length);
        if (failure is not null)
        {
            BlockPagination(failure);
            throw new InvalidDataException(failure);
        }

        foreach (var entry in additions)
            Children.Add(new DropboxFolderNode(entry.Name, entry.Path, entry, _graphBudget));
        SortChildren();
        _retainedPageCount++;
        ContinueCursor = page.ContinueCursor;
        OnPropertyChanged(nameof(CanExpand));
        if (page.ContinueCursor is not null) _seenCursors.Add(page.ContinueCursor);
    }

    private void BlockPagination(string reason)
    {
        PaginationBlockedReason = reason;
        IsLoaded = true;
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    // WF1: an appended provider page is merged into one globally ordered list, so the visible siblings keep a
    // single truthful alphabetical order instead of page-by-page append order. Placeholders stay last.
    internal void SortChildren()
    {
        var ordered = Children.Where(child => child.Entry is not null)
            .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(child => child.Path, StringComparer.Ordinal)
            .Concat(Children.Where(child => child.Entry is null)).ToArray();
        for (var target = 0; target < ordered.Length; target++)
        {
            var current = Children.IndexOf(ordered[target]);
            if (current != target) Children.Move(current, target);
        }
    }

    private (int Pages, int Folders) MeasureOwnedSubtree()
    {
        var pages = 0;
        var folders = 0;
        var pending = new Stack<DropboxFolderNode>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            pages = checked(pages + current._retainedPageCount);
            foreach (var child in current.Children.Where(child => child.Entry is not null))
            {
                folders = checked(folders + 1);
                pending.Push(child);
            }
        }
        return (pages, folders);
    }

    private static IReadOnlyList<DropboxFolderEntry> UniqueEntries(IEnumerable<DropboxFolderEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return entries.Where(entry => seen.Add(entry.FolderId)).ToArray();
    }

    private static DropboxFolderNode Placeholder(DropboxFolderGraphBudget graphBudget) =>
        new("Loading…", "/", null, graphBudget, placeholder: true);

    private DropboxFolderNode(string name, string path, DropboxFolderEntry? entry,
        DropboxFolderGraphBudget graphBudget, bool placeholder)
    {
        Name = name;
        Path = path;
        Entry = entry;
        IsPlaceholder = placeholder;
        _graphBudget = graphBudget;
        IsLoaded = placeholder;
    }
}

internal sealed class DropboxFolderGraphBudget
{
    internal const int DefaultMaxRetainedPages = 512;
    internal const int DefaultMaxRetainedFolders = 10_000;
    private readonly object _gate = new();

    internal DropboxFolderGraphBudget(int maxRetainedPages, int maxRetainedFolders)
    {
        if (maxRetainedPages <= 0) throw new ArgumentOutOfRangeException(nameof(maxRetainedPages));
        if (maxRetainedFolders <= 0) throw new ArgumentOutOfRangeException(nameof(maxRetainedFolders));
        MaxRetainedPages = maxRetainedPages;
        MaxRetainedFolders = maxRetainedFolders;
    }

    internal int MaxRetainedPages { get; }
    internal int MaxRetainedFolders { get; }
    internal int RetainedPages { get; private set; }
    internal int RetainedFolders { get; private set; }

    internal static DropboxFolderGraphBudget CreateDefault() =>
        new(DefaultMaxRetainedPages, DefaultMaxRetainedFolders);

    internal string? TryReserve(int pages, int folders) => TryReplace(0, 0, pages, folders);

    internal string? TryReplace(int oldPages, int oldFolders, int newPages, int newFolders)
    {
        if (oldPages < 0 || oldFolders < 0 || newPages < 0 || newFolders < 0)
            throw new ArgumentOutOfRangeException(nameof(oldPages));

        lock (_gate)
        {
            var nextPages = checked(RetainedPages - oldPages + newPages);
            var nextFolders = checked(RetainedFolders - oldFolders + newFolders);
            if (nextPages > MaxRetainedPages)
                return $"This Dropbox browser reached its retained-page ceiling ({MaxRetainedPages:N0}). Refresh or start a new search to continue safely.";
            if (nextFolders > MaxRetainedFolders)
                return $"This Dropbox browser reached its retained-folder ceiling ({MaxRetainedFolders:N0}). Refresh or start a new search to continue safely.";
            if (nextPages < 0 || nextFolders < 0)
                throw new InvalidOperationException("Dropbox folder graph accounting became inconsistent.");

            RetainedPages = nextPages;
            RetainedFolders = nextFolders;
            return null;
        }
    }
}
