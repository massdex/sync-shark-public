using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

internal sealed record AccountSharingRow(AccountSharedLink? Link, AccountFileRequest? Request, string PairName, string Outcome = "")
{
    public SharedLinkRevokeTarget Key => new(Link?.ConnectionId ?? Request!.ConnectionId,
        Link?.NamespaceId ?? Request!.NamespaceId, Link?.Url ?? Request!.Url, Link?.PermissionId, Link?.RevisionFingerprint);
    public string DisplayName => Link?.Name ?? Request!.Title;
    public string Path => Link?.Path ?? Request?.Destination ?? "Not reported";
    public string ItemKind => Request is not null ? "File request" : Link?.ItemKind == "folder" ? "Folder" : "File";
    public string ShortAction => Outcome.Length > 0 ? Outcome : Request is { } request
        ? $"{(request.IsOpen ? "Open" : "Closed")} · {request.FileCount:N0} uploads"
        : "Shared link · " + (Link?.Audience switch { "public" => "Anyone with the link", "team" or "team_only" => "Team only", "no_one" => "Existing members", { } value when value.StartsWith("domain:", StringComparison.Ordinal) => "People in " + value[7..], _ => "Restricted access" });
    public string TimeLabel => Request?.Created.ToLocalTime().ToString("HH:mm") ?? "";
    public string FullTimestamp => Request?.Created.ToLocalTime().ToString("f") ?? "";
    public string TimeAccessibleLabel => FullTimestamp;
    public string StatusGlyph => Outcome.Length > 0 ? "!" : "✓";
    public string AccessLabel => ShortAction;
}

public partial class MainWindow
{
    private StackPanel _sharingPermissions = null!;
    private ContentPresenter _sharingSidebar = null!;
    private ColumnDefinition _sharingSidebarColumn = null!;
    private Grid _sharingListLayout = null!;
    private Button _sharingRevoke = null!;
    private Button _sharingSelect = null!;
    private bool _renderingSharing, _sharingPageFailed, _sharingBulkBusy;
    private string? _sharingAcceptedAccount;
    private int _sharingAcceptedTab, _sharingEditorEpoch;
    private DetailsHost? _sharingDetails;
    private SimpleDetailedSwitch? _sharingViewSwitch;
    private SharingEditorSession? _sharingEditor;
    private SharedLinkRevokeTarget? _sharingEditorKey;
    private readonly Dictionary<SharedLinkRevokeTarget, SharedLinkRevokeResult> _sharingRevokeResults = [];

    private FrameworkElement BuildSharingRows()
    {
        _sharingListLayout = new Grid();
        _sharingListLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _sharingSidebarColumn = new ColumnDefinition { Width = new GridLength(0) };
        _sharingListLayout.ColumnDefinitions.Add(_sharingSidebarColumn);
        var feed = new Grid();
        feed.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        feed.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        feed.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _sharingPermissions = new StackPanel(); feed.Children.Add(_sharingPermissions);
        var tools = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _sharingRevoke = AsyncButton("Revoke Selected", async (_, _) => await RevokeSelectedSharingAsync(), true);
        _sharingRevoke.IsEnabled = false; tools.Children.Add(_sharingRevoke);
        _sharingSelect = Button("Select Loaded Results", (_, _) =>
        {
            if (_sharingRequests || _sharingBulkBusy || !TryCloseSharingEditor()) return;
            _renderingSharing = true;
            _sharingRows.SelectedItems.Clear();
            foreach (var row in _sharingRows.Items.OfType<AccountSharingRow>().Where(CanRevokeSharingRow).Take(SharedLinkRevokeBatch.MaximumTargets))
                _sharingRows.SelectedItems.Add(row);
            _renderingSharing = false; UpdateSharingSelection();
        }, true);
        _sharingSelect.ToolTip = "Select up to 100 revocable links in the loaded, filtered results. Unseen links are never selected.";
        tools.Children.Add(_sharingSelect);
        // File Requests are an account collection with no link-bulk concept, so the tab path
        // collapses both Shared Links controls instead of offering disabled link actions.
        _sharingTabs.SelectionChanged += (_, _) => UpdateSharingToolbar();
        UpdateSharingToolbar();
        var view = _sharingViewSwitch = SharedPresentation.ViewSwitch("sharing", IsDetailedView("sharing"), control =>
        {
            SharedPresentation.ApplyFeedDetail(_sharingRows, control.IsDetailed, SharingDetailColumns);
            _ = SetDetailedViewAsync("sharing", control.IsDetailed);
        });
        DockPanel.SetDock(view, Dock.Right); tools.Children.Add(view);
        Grid.SetRow(tools, 1); feed.Children.Add(tools);
        _sharingRows = new DataGrid(); StyleFeed(_sharingRows);
        _sharingRows.SelectionMode = DataGridSelectionMode.Extended;
        _sharingRows.SelectionUnit = DataGridSelectionUnit.FullRow;
        // Keep the shared compact row. Sharing uses an explicit row context menu, not an inert ellipsis.
        _sharingRows.Columns.RemoveAt(_sharingRows.Columns.Count - 1);
        SharedPresentation.ApplyFeedDetail(_sharingRows, view.IsDetailed, SharingDetailColumns);
        AutomationProperties.SetName(_sharingRows, "Shared links and file requests");
        _sharingRows.SelectionChanged += async (_, _) =>
        {
            if (_renderingSharing) return;
            UpdateSharingSelection();
            var selected = _sharingRows.SelectedItems.OfType<AccountSharingRow>().ToArray();
            if (selected.Length != 1)
            {
                if (!TryCloseSharingEditor()) RestoreSharingEditorSelection();
                return;
            }
            var row = selected[0];
            if (_sharingEditorKey == row.Key) return;
            if (row.Link is { } link) await OpenAccountLinkEditorAsync(link);
            else OpenFileRequestEditor(row.Request!.ConnectionId, row.Request);
        };
        _sharingRows.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(async (_, e) =>
        {
            if (e.VerticalChange <= 0 || _sharingPageFailed || _sharingBulkBusy || _sharingLoad is not null || _sharingPages.Count == 0) return;
            if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 180) await LoadAccountSharingAsync(false);
        }));
        var menu = WorkspaceMenu();
        MenuAction(menu, "Copy Link", (_, _) => { if (_sharingRows.SelectedItem is AccountSharingRow row) SetClipboardText(row.Key.Url); });
        MenuAction(menu, "Open Link", (_, _) => { if (_sharingRows.SelectedItem is AccountSharingRow row) OpenAccountSharingUrl(row.Key.Url); });
        _sharingRows.ContextMenu = menu;
        Grid.SetRow(_sharingRows, 2); feed.Children.Add(_sharingRows);
        _sharingListLayout.Children.Add(feed);
        _sharingSidebar = new ContentPresenter { Margin = new Thickness(16, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(_sharingSidebar, 1); _sharingListLayout.Children.Add(_sharingSidebar);
        return _sharingListLayout;
    }

    private static readonly FeedDetailColumn[] SharingDetailColumns =
    [new("Account", nameof(AccountSharingRow.PairName), 180), new("Location", nameof(AccountSharingRow.Path), 220), new("Access / Status", nameof(AccountSharingRow.AccessLabel), 190)];

    private bool CanRevokeSharingRow(AccountSharingRow row) => row.Link?.CanRevoke == true &&
        (!_sharingRevokeResults.TryGetValue(row.Key, out var result) || result.Retryable);

    private void UpdateSharingToolbar()
    {
        var visibility = _sharingRequests ? Visibility.Collapsed : Visibility.Visible;
        _sharingRevoke.Visibility = visibility;
        _sharingSelect.Visibility = visibility;
    }

    private void UpdateSharingSelection()
    {
        UpdateSharingToolbar();
        var rows = _sharingRows.SelectedItems.OfType<AccountSharingRow>().ToArray();
        _sharingRevoke.IsEnabled = !_sharingBulkBusy && !_sharingRequests && rows.Length is > 0 and <= SharedLinkRevokeBatch.MaximumTargets && rows.All(CanRevokeSharingRow);
        _sharingRevoke.Content = CommandIcons.Label(rows.Length == 0 ? "Revoke Selected" : $"Revoke Selected ({rows.Length})", "delete");
        _sharingRevoke.ToolTip = rows.Length > 100 ? "Select at most 100 links." : "Revoke only the selected links; files and folder membership remain unchanged.";
    }

    private void RenderSharingFeed()
    {
        var selected = _sharingRows.SelectedItems.OfType<AccountSharingRow>().Select(r => r.Key).ToHashSet();
        var query = _sharingSearch.Text.Trim();
        var rows = _sharingRequests
            ? _accountRequests.OrderByDescending(r => r.Created).Select(r => new AccountSharingRow(null, r, SharingAccountLabel(r.ConnectionId)))
            : _accountLinks.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(r => new AccountSharingRow(r, null, SharingAccountLabel(r.ConnectionId),
                _sharingRevokeResults.TryGetValue(new(r.ConnectionId, r.NamespaceId, r.Url, r.PermissionId, r.RevisionFingerprint), out var outcome) ? outcome.Message : ""));
        var visible = rows.Where(r => (r.DisplayName + " " + r.Path + " " + r.PairName + " " + r.Request?.Description)
            .Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _renderingSharing = true;
        try
        {
            _sharingRows.ItemsSource = visible;
            foreach (var row in visible.Where(r => selected.Contains(r.Key))) _sharingRows.SelectedItems.Add(row);
        }
        finally { _renderingSharing = false; }
        RenderSharingPermissions(); UpdateSharingSelection();
        var unsupported = _sharingRequests && _authorityHost.LoadProviderConnections().Any(c => c.IsActive && c.Provider != "dropbox" &&
            (SharingAccountId is null || SharingAccountId == c.ConnectionId));
        var warnings = _sharingInventoryWarnings.Concat(_sharingIssueSummary.Lines).ToArray();
        _sharingSummary.Text = $"{visible.Length:N0} shown · {(_sharingRequests ? _accountRequests.Count : _accountLinks.Count):N0} loaded" +
            (_sharingPages.Count > 0 ? " · More results available: Load More or scroll down. Search covers loaded results." :
                warnings.Length > 0 ? " · Listing incomplete; see warnings." : " · All available pages loaded.") +
            (unsupported ? "\nFile requests are a Dropbox feature. Manage Google Drive link permissions in Shared Links." : "");
        if (warnings.Length > 0) _sharingSummary.Text += "\n" + string.Join("\n", warnings);
        if (visible.Length == 0) _sharingSummary.Text = "No items in this view. " + _sharingSummary.Text;
        UpdateSharingPagination();
        _sharingSearchAll.Visibility = query.Length > 0 && _sharingPages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSharingPagination()
    {
        // A provider can return an empty or short page and still have a cursor.
        // Scrolling alone cannot reach that cursor when the rows fit the viewport.
        var caption = _sharingPageFailed ? "Retry Loading" : "Load More";
        _sharingMore.Content = CommandIcons.Label(caption, _sharingPageFailed ? "sync" : "add");
        AutomationProperties.SetName(_sharingMore, caption);
        _sharingMore.Visibility = _sharingPages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _sharingMore.IsEnabled = _sharingPages.Count > 0 && _sharingLoad is null && !_sharingBulkBusy;
        ActionAvailability.Refresh(_sharingMore);
    }

    private async Task RevokeSelectedSharingAsync()
    {
        if (_sharingBulkBusy || !TryCloseSharingEditor()) return;
        var selected = _sharingRows.SelectedItems.OfType<AccountSharingRow>().ToArray();
        if (selected.Length is < 1 or > 100 || selected.Any(r => !CanRevokeSharingRow(r))) return;
        var targets = selected.Select(r => r.Key).ToArray();
        var summary = string.Join("\n", selected.GroupBy(r => r.Key.AccountId).Select(g => $"{SharingAccountLabel(g.Key)}: {g.Count()} links"));
        if (!AppDialogWindow.Confirm(this, "Revoke Selected Links", $"Revoke these {targets.Length} selected links?",
            summary + "\n\n" + string.Join("\n", selected.Select(r => r.DisplayName + " — " + r.Path)) +
            "\n\nFiles, other links and folder membership stay unchanged. No unseen links are included.", "Revoke Selected", destructive: true)) return;
        _sharingBulkBusy = true; UpdateSharingSelection(); UpdateSharingPagination();
        _sharingAccount.IsEnabled = _sharingTabs.IsEnabled = _sharingRows.IsEnabled = _sharingRefresh.IsEnabled = _sharingCreate.IsEnabled = false;
        try
        {
            var results = await _authorityHost.RevokeSharedLinksAsync(targets);
            foreach (var result in results)
            {
                _sharingRevokeResults[result.Target] = result;
                if (result.Status == "revoked") _accountLinks.RemoveAll(l => new SharedLinkRevokeTarget(l.ConnectionId, l.NamespaceId, l.Url, l.PermissionId, l.RevisionFingerprint) == result.Target);
            }
            RenderAccountSharing();
            _sharingSummary.Text = $"Revoked: {results.Count(r => r.Status == "revoked")}. Not confirmed: {results.Count(r => r.Status == "uncertain")}. " +
                $"Not applied: {results.Count(r => r.Retryable)}. Each remaining row shows its result; uncertain links are not retried.";
        }
        catch (Exception error) { _sharingSummary.Text = SharingFailure(error); }
        finally
        {
            _sharingBulkBusy = false;
            _sharingAccount.IsEnabled = _sharingTabs.IsEnabled = _sharingRows.IsEnabled = _sharingRefresh.IsEnabled = _sharingCreate.IsEnabled = true;
            UpdateSharingSelection(); UpdateSharingPagination();
        }
    }

    private bool TryCloseSharingEditor()
    {
        if (_sharingBulkBusy) return false;
        if (_sharingEditor is not null && !_sharingEditor.TryClose()) return false;
        _sharingEditorEpoch++; return true;
    }

    private void RestoreSharingEditorSelection()
    {
        _renderingSharing = true; _sharingRows.SelectedItems.Clear();
        if (_sharingRows.Items.OfType<AccountSharingRow>().FirstOrDefault(r => r.Key == _sharingEditorKey) is { } previous)
            _sharingRows.SelectedItem = previous;
        _renderingSharing = false; UpdateSharingSelection();
    }

    private SharingEditorSession SharingEditor(string title, SharedLinkRevokeTarget? key,
        out StackPanel fields, out TextBlock feedback, out WrapPanel actions)
    {
        fields = new StackPanel(); actions = new WrapPanel(); feedback = Text("", 12, brush: "MutedInk");
        var body = new StackPanel(); var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var session = new SharingEditorSession(this);
        // UIV6-H1: Sharing uses the one shared strip, so its expand then close order, glyph, tooltip
        // and naming match Activity and Storage while its own callbacks stay its own.
        var chrome = DetailsChrome.ActionStrip("Details", (_, _) => _sharingDetails?.Expand(this),
            (_, _) => session.Close());
        DockPanel.SetDock(chrome, Dock.Right); header.Children.Add(chrome); session.SidebarChrome = chrome;
        header.Children.Add(Text(title, 20, true)); body.Children.Add(header); body.Children.Add(fields); body.Children.Add(feedback); body.Children.Add(actions);
        var scroll = new ScrollViewer { Content = body, Padding = new Thickness(18), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "Surface");
        session.Content = scroll; session.Key = key;
        return session;
    }

    private sealed class SharingEditorSession(MainWindow owner)
    {
        internal FrameworkElement Content = null!;
        internal FrameworkElement SidebarChrome = null!;
        internal SharedLinkRevokeTarget? Key;
        internal event CancelEventHandler? Closing;
        internal event EventHandler? Closed;
        internal Window OwnerWindow => owner._sharingDetails?.Dialog ?? (Window)owner;
        internal bool IsClosed { get; private set; }
        internal void Present()
        {
            owner._sharingEditor = this; owner._sharingEditorKey = Key;
            owner._sharingDetails = new DetailsHost(Content, owner._sharingSidebar);
            owner._sharingDetails.ExpandedChanged += (_, _) =>
            {
                var expanded = owner._sharingDetails?.Expanded == true;
                owner._sharingSidebarColumn.Width = new GridLength(expanded ? 0 : 360);
            };
            // The chrome's own visibility is owned by the shared registration; this session keeps only
            // the sidebar column width that belongs to Sharing.
            owner._sharingDetails.RegisterSidebarOnly(SidebarChrome);
            owner._sharingSidebarColumn.Width = new GridLength(360);
            if (owner._sharingListLayout.ActualWidth is > 0 and < 720) owner._sharingDetails.Expand(owner);
        }
        internal void Close() => TryClose();
        internal bool TryClose()
        {
            if (IsClosed) return true;
            var args = new CancelEventArgs(); Closing?.Invoke(this, args); if (args.Cancel) return false;
            IsClosed = true; owner._sharingDetails?.Collapse(); owner._sharingSidebar.Content = null;
            owner._sharingSidebarColumn.Width = new GridLength(0); owner._sharingDetails = null;
            owner._sharingEditor = null; owner._sharingEditorKey = null; owner._sharingEditorEpoch++;
            Closed?.Invoke(this, EventArgs.Empty); return true;
        }
    }
}
