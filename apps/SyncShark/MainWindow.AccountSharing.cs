using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using TabControl = System.Windows.Controls.TabControl;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

public partial class MainWindow
{
    private Border? _sharingPage;
    private ComboBox _sharingAccount = null!;
    private TextBox _sharingSearch = null!;
    private TabControl _sharingTabs = null!;
    private DataGrid _sharingRows = null!;
    private TextBlock _sharingSummary = null!;
    private Button _sharingRefresh = null!, _sharingMore = null!, _sharingCreate = null!, _sharingSearchAll = null!, _sharingCancel = null!;
    private readonly List<AccountSharedLink> _accountLinks = [];
    private readonly List<AccountFileRequest> _accountRequests = [];
    private readonly HashSet<string> _sharingInventoryWarnings = new(StringComparer.Ordinal);
    private readonly AccountSharingIssueSummary _sharingIssueSummary = new();
    private readonly List<(IAccountSharingClient Client, string Namespace, AccountSharingCursor? Cursor)> _sharingPages = [];
    private CancellationTokenSource? _sharingLoad;
    private bool _updatingSharingAccounts;
    private bool _sharingRequests => _sharingTabs.SelectedIndex == 1;
    private string? SharingAccountId => string.IsNullOrEmpty(_sharingAccount.SelectedValue as string) ? null : _sharingAccount.SelectedValue as string;

    private void BuildAccountSharingPage()
    {
        var content = new Grid();
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            content.RowDefinitions.Add(new RowDefinition { Height = height });
        _sharingCreate = AsyncButton("Create Shared Link", async (_, _) => await OpenSharingCreateAsync(),
            explanation: "Choose an account and a file or folder, then review who can open the link before creating it. This does not sync files.");
        _sharingRefresh = AsyncButton("Refresh", async (_, _) => await LoadAccountSharingAsync(true), true,
            explanation: "Reload shared links or file requests from the selected accounts. Existing links, requests and files are not changed.");
        _sharingCancel = Button("Cancel Loading", (_, _) => _sharingLoad?.Cancel(), true,
            explanation: "Stop loading sharing information. Already loaded results stay visible; existing links, requests and files are not changed.");
        _sharingCancel.Visibility = Visibility.Collapsed;
        _sharingAccount = new ComboBox { MinWidth = 140, MaxWidth = 300, ItemTemplate = SharedPresentation.AccountChoiceTemplate(),
            SelectedValuePath = nameof(StorageAccountChoice.Id), HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(_sharingAccount, "Sharing Account");
        _sharingAccount.SelectionChanged += async (_, _) =>
        {
            if (_updatingSharingAccounts || !_fullAppReady) return;
            if (!TryCloseSharingEditor())
            { _updatingSharingAccounts = true; _sharingAccount.SelectedValue = _sharingAcceptedAccount ?? ""; _updatingSharingAccounts = false; return; }
            _sharingAcceptedAccount = SharingAccountId; await LoadAccountSharingAsync(true);
        };
        _sharingAccount.Style = SharedPresentation.QuietComboBoxStyle();
        content.Children.Add(SharedPresentation.PageHeader("Sharing", _sharingAccount, _sharingCreate, _sharingRefresh, _sharingCancel));
        var tabRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 12) };
        _sharingSearch = new TextBox {
            ToolTip = "Search the loaded names, paths, accounts, or request descriptions." };
        System.Windows.Automation.AutomationProperties.SetName(_sharingSearch, "Search Sharing");
        _sharingSearch.TextChanged += (_, _) => RenderAccountSharing();
        var searchField = SharedPresentation.QuietSearchField(_sharingSearch, "Search Sharing", "Search loaded items…", width: 220);
        DockPanel.SetDock(searchField, Dock.Right); tabRow.Children.Add(searchField);
        _sharingTabs = new TabControl { BorderThickness = new Thickness(0), ItemContainerStyle = WorkspaceTabStyle() };
        _sharingTabs.Items.Add(new TabItem { Header = CommandIcons.Label("Shared Links", "link") });
        _sharingTabs.Items.Add(new TabItem { Header = CommandIcons.Label("File Requests", "document") });
        System.Windows.Automation.AutomationProperties.SetName((TabItem)_sharingTabs.Items[0], "Shared Links");
        System.Windows.Automation.AutomationProperties.SetName((TabItem)_sharingTabs.Items[1], "File Requests");
        _sharingTabs.SelectedIndex = 0;
        _sharingTabs.SelectionChanged += async (_, e) =>
        {
            if (!ReferenceEquals(e.Source, _sharingTabs) || !_fullAppReady || _updatingSharingAccounts) return;
            if (!TryCloseSharingEditor())
            { _updatingSharingAccounts = true; _sharingTabs.SelectedIndex = _sharingAcceptedTab; _updatingSharingAccounts = false; return; }
            _sharingAcceptedTab = _sharingTabs.SelectedIndex; await LoadAccountSharingAsync(true);
        };
        tabRow.Children.Add(_sharingTabs); Grid.SetRow(tabRow, 1); content.Children.Add(tabRow);
        var rows = BuildSharingRows(); Grid.SetRow(rows, 2); content.Children.Add(rows);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        _sharingSearchAll = AsyncButton("Load Remaining Search Results", async (_, _) => await LoadAccountSharingAsync(false, true), true,
            explanation: "Read the remaining pages from the selected accounts so search covers their complete sharing list. No links, requests or files are changed.");
        DockPanel.SetDock(_sharingSearchAll, Dock.Right); footer.Children.Add(_sharingSearchAll);
        _sharingMore = AsyncButton("Load More", async (_, _) => { _sharingPageFailed = false; await LoadAccountSharingAsync(false); }, true,
            explanation: "Read the next page from each account with remaining results. Already loaded items stay visible; no links or files are changed.");
        DockPanel.SetDock(_sharingMore, Dock.Right); footer.Children.Add(_sharingMore);
        _sharingSummary = Text("Choose an account or show all accounts.", 12, brush: "MutedInk"); footer.Children.Add(_sharingSummary);
        Grid.SetRow(footer, 3); content.Children.Add(footer);
        _sharingPage = new Border { Visibility = Visibility.Collapsed, Margin = new Thickness(24, 20, 24, 16), Child = content };
        Grid.SetRow(_sharingPage, Grid.GetRow(DashboardPage)); ((Grid)DashboardPage.Parent).Children.Add(_sharingPage);
        RefreshSharingAccountChoices();
        ActionAvailability.Bind(_sharingCreate, () => _sharingLoad is not null ? "Sharing information is loading."
            : _sharingBulkBusy ? "Wait for the selected sharing changes to finish."
            : _sharingRequests ? "File requests are supported for Dropbox accounts only. Select a Dropbox account."
            : "Select a connected account before creating a shared link.");
        ActionAvailability.Bind(_sharingRefresh, () => _sharingBulkBusy ? "Sharing changes are being applied." : "Sharing information is loading.");
        ActionAvailability.Bind(_sharingMore, () => _sharingLoad is not null ? "Sharing information is loading."
            : _sharingBulkBusy ? "Wait for the selected sharing changes to finish." : "All available results have loaded.");
        ActionAvailability.Bind(_sharingSearchAll, () => _sharingBulkBusy ? "Sharing changes are being applied." : "Sharing information is loading.");
        ActionAvailability.Bind(_sharingRevoke, () => _sharingBulkBusy ? "The selected links are being revoked."
            : _sharingRequests ? "Select shared links, not file requests."
            : _sharingRows.SelectedItems.Count == 0 ? "Select one or more links to revoke."
            : _sharingRows.SelectedItems.Count > SharedLinkRevokeBatch.MaximumTargets ? "Select at most 100 links at once."
            : "One or more selected links cannot be revoked by this account. Open their details to check ownership and permissions.");
    }

    private void RefreshSharingAccountChoices()
    {
        if (_sharingPage is null) return;
        var selected = SharingAccountId;
        _updatingSharingAccounts = true;
        try
        {
            _sharingAccount.ItemsSource = new[] { new StorageAccountChoice("", "All Providers", "All Accounts", true) }
                .Concat(_authorityHost.LoadProviderConnections().Where(item => item.IsActive)
                    .Select(item => new StorageAccountChoice(item.ConnectionId, SyncProviders.DisplayName(item.Provider), string.IsNullOrWhiteSpace(item.Email) ? item.DisplayName : item.Email, true, item.Provider))).ToArray();
            _sharingAccount.SelectedValue = selected is not null && _sharingAccount.Items.OfType<StorageAccountChoice>().Any(item => item.Id == selected) ? selected : "";
            _sharingAcceptedAccount = SharingAccountId;
        }
        finally { _updatingSharingAccounts = false; }
    }

    private async Task LoadAccountSharingAsync(bool reset, bool loadAll = false)
    {
        if (_sharingPage is null) return;
        if (!reset && _sharingLoad is not null) return;
        if (_sharingBulkBusy) return;
        _sharingLoad?.Cancel();
        using var load = new CancellationTokenSource(TimeSpan.FromMinutes(2)); _sharingLoad = load;
        var requests = _sharingRequests; var accountId = SharingAccountId;
        _sharingRefresh.IsEnabled = _sharingMore.IsEnabled = _sharingCreate.IsEnabled = _sharingSearchAll.IsEnabled = false;
        _sharingCancel.Visibility = Visibility.Visible;
        _sharingSummary.Text = "Loading from connected accounts…";
        _sharingCreate.Content = CommandIcons.Label(requests ? "Create File Request" : "Create Shared Link", requests ? "add" : "link");
        System.Windows.Automation.AutomationProperties.SetName(_sharingCreate, requests ? "Create File Request" : "Create Shared Link");
        ActionAvailability.SetExplanation(_sharingCreate, requests
            ? "Choose a Dropbox account and a destination folder, then create a request that lets others upload files there."
            : "Choose an account and a file or folder, then review who can open the link before creating it. This does not sync files.");
        if (reset) { _sharingInventoryWarnings.Clear(); _sharingIssueSummary.Clear(); }
        var failures = _sharingInventoryWarnings;
        try
        {
            if (reset)
            {
                _sharingPageFailed = false;
                _accountLinks.Clear(); _accountRequests.Clear(); _sharingPages.Clear();
                _renderingSharing = true; _sharingRows.ItemsSource = null; _renderingSharing = false;
                foreach (var binding in _authorityHost.LoadProviderConnections().Where(item => item.IsActive && (accountId is null || item.ConnectionId == accountId)))
                {
                    if (requests && binding.Provider != SyncProviders.Dropbox) continue;
                    var saved = _authorityHost.LoadAccountBinding(binding.ConnectionId);
                    if (saved is null) continue;
                    if (requests && !DropboxProviderConfiguration.HasFileRequestAccess(saved.Scopes))
                    { failures.Add(SharingAccountLabel(saved.ConnectionId) + ": Enable File Requests to grant access."); continue; }
                    try
                    {
                        var client = await _authorityHost.CreateAccountSharingClientAsync(saved.ConnectionId, load.Token);
                        if (!ReferenceEquals(load, _sharingLoad)) return;
                        // File requests are an account collection, not a separate collection per team namespace.
                        foreach (var ns in requests ? client.Namespaces.Take(1) : client.Namespaces)
                            _sharingPages.Add((client, ns, null));
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { if (!ReferenceEquals(load, _sharingLoad)) return; failures.Add(SharingAccountLabel(saved.ConnectionId) + ": " + error.Message); }
                }
            }
            var pagesRead = 0;
            do
            {
            var pageFailures = 0;
            foreach (var page in _sharingPages.ToArray())
            {
                load.Token.ThrowIfCancellationRequested();
                try
                {
                    AccountSharingCursor? next;
                    if (requests)
                    {
                        var result = await page.Client.ListRequestsAsync(page.Namespace, page.Cursor, load.Token); next = result.Next;
                        if (!ReferenceEquals(load, _sharingLoad)) return;
                        foreach (var item in result.Items) if (!_accountRequests.Any(prior => prior.ConnectionId == item.ConnectionId && prior.NamespaceId == item.NamespaceId && prior.Id == item.Id)) _accountRequests.Add(item);
                    }
                    else
                    {
                        var result = await page.Client.ListLinksAsync(page.Namespace, page.Cursor, load.Token); next = result.Next;
                        if (!ReferenceEquals(load, _sharingLoad)) return;
                        foreach (var item in result.Items)
                        {
                            if (!_accountLinks.Any(prior => prior.ConnectionId == item.ConnectionId && prior.NamespaceId == item.NamespaceId && prior.Url == item.Url && prior.PermissionId == item.PermissionId)) _accountLinks.Add(item);
                            // An explicit refresh obtained current provider metadata. It does not repeat a mutation.
                            if (reset) _sharingRevokeResults.Remove(new(item.ConnectionId, item.NamespaceId, item.Url, item.PermissionId, item.RevisionFingerprint));
                        }
                        _sharingIssueSummary.AddPage(page.Client.ConnectionId,
                            SharingAccountLabel(page.Client.ConnectionId), result.Issues, result.Incomplete);
                    }
                    _sharingPages.Remove(page); if (next is not null) _sharingPages.Add((page.Client, page.Namespace, next));
                    pagesRead++;
                    if (loadAll) { RenderAccountSharing(); _sharingSummary.Text += " Loading remaining pages…"; }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { if (!ReferenceEquals(load, _sharingLoad)) return; pageFailures++; failures.Add(SharingAccountLabel(page.Client.ConnectionId) + ": " + error.Message); }
            }
            // A failed page is retained for an explicit retry, never spun on in this request.
            if (pageFailures > 0) { _sharingPageFailed = true; break; }
            } while (loadAll && _sharingPages.Count > 0 && pagesRead < 2000);
            if (!ReferenceEquals(load, _sharingLoad)) return;
            RenderAccountSharing();
        }
        catch (OperationCanceledException)
        { if (ReferenceEquals(load, _sharingLoad)) { failures.Add("Loading stopped. Loaded results are kept; refresh to try again."); RenderAccountSharing(); } }
        finally
        {
            if (ReferenceEquals(load, _sharingLoad))
            { _sharingLoad = null; _sharingRefresh.IsEnabled = _sharingCreate.IsEnabled = _sharingSearchAll.IsEnabled = true;
                _sharingCancel.Visibility = Visibility.Collapsed;
                UpdateSharingPagination();
                _sharingCreate.IsEnabled = SharingAccountId is null || _authorityHost.LoadProviderConnections().Any(c => c.ConnectionId == SharingAccountId && (!requests || c.Provider == SyncProviders.Dropbox)); }
        }
    }

    private string SharingAccountLabel(string id) => _accountLabels.GetValueOrDefault(id, "Account Unavailable (" + id + ")");
    private void RenderAccountSharing()
    {
        if (_sharingPage is null) return;
        RenderSharingFeed();
    }

    private void RenderSharingPermissions()
    {
        _sharingPermissions.Children.Clear();
        if (_sharingRequests)
        {
            foreach (var account in _authorityHost.LoadProviderConnections().Where(item => item.IsActive && (SharingAccountId is null || item.ConnectionId == SharingAccountId)))
            {
                var binding = _authorityHost.LoadDropboxConnection(account.ConnectionId);
                if (binding is null || DropboxProviderConfiguration.HasFileRequestAccess(binding.Scopes)) continue;
                var words = new StackPanel(); words.Children.Add(Text(SharingAccountLabel(account.ConnectionId), 14, true));
                words.Children.Add(Text("Allow Sync Shark to manage this account's file requests. Existing sync access stays unchanged if you cancel.", 12, brush: "MutedInk"));
                words.Children.Add(AsyncButton("Enable File Requests", async (_, _) => await EnableFileRequestsAsync(account.ConnectionId)));
                _sharingPermissions.Children.Add(Card(words));
            }
        }
    }

    private static FrameworkElement SharingListRow(string icon, string name, string account, string description, UIElement actions, string? details = null)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var symbol = CommandIcons.Icon(icon, 24); symbol.VerticalAlignment = VerticalAlignment.Top; symbol.Margin = new Thickness(0, 2, 12, 0); row.Children.Add(symbol);
        var words = new StackPanel { Margin = new Thickness(0, 0, 16, 0) }; words.Children.Add(Text(name, 15, true));
        if (account.Length > 0) words.Children.Add(Text(account, 12, brush: "FocusBrush", margin: new Thickness(0, 4, 0, 6)));
        var summary = Text(description, 12, brush: "MutedInk");
        summary.TextWrapping = TextWrapping.NoWrap;
        summary.TextTrimming = TextTrimming.CharacterEllipsis;
        summary.ToolTip = description;
        words.Children.Add(summary); Grid.SetColumn(words, 1); row.Children.Add(words);
        if (!string.IsNullOrEmpty(details))
            words.Children.Add(new Expander { Header = "Details", Content = Text(details, 12, brush: "MutedInk", margin: new Thickness(0, 6, 0, 0)),
                FontSize = 12, Margin = new Thickness(0, 6, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch });
        if (actions is FrameworkElement commands) commands.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(actions, 2); row.Children.Add(actions);
        var card = Card(row, 14); card.Margin = new Thickness(0, 0, 0, 10); return card;
    }

    private async Task EnableFileRequestsAsync(string id)
    {
        try
        {
            await AuthorizeDropboxFromUiAsync(token => _authorityHost.ReauthorizeDropboxAsync(OpenAuthorizationPageForUiAsync,
                token, connectionId: id, includeFileRequests: true));
            RefreshAccountPickers(); NavigateTo("Sharing"); _sharingTabs.SelectedIndex = 1;
            await LoadAccountSharingAsync(true);
        }
        catch (Exception error) { ShowStatus("File Requests were not enabled: " + error.Message); }
    }

    private async Task OpenSharingCreateAsync()
    {
        var id = SharingAccountId;
        if (id is null)
        {
            var menu = WorkspaceMenu(); menu.PlacementTarget = _sharingCreate;
            foreach (var account in _sharingAccount.Items.OfType<StorageAccountChoice>().Where(item => item.Id.Length > 0))
                MenuAction(menu, account.Label, async (_, _) => await OpenSharingCreateForAccountAsync(account.Id));
            if (menu.Items.Count == 0) MenuAction(menu, "Add an Account", (_, _) => NavigateTo("Connections"));
            menu.IsOpen = true; return;
        }
        await OpenSharingCreateForAccountAsync(id);
    }
    private async Task OpenSharingCreateForAccountAsync(string id)
    {
        if (_sharingRequests)
        {
            if (_authorityHost.LoadAccountBinding(id)?.Provider == SyncProviders.GoogleDrive)
            { ShowStatus("File requests are a Dropbox feature. Google Drive link permissions are available in Shared Links."); return; }
            var binding = _authorityHost.LoadDropboxConnection(id);
            if (binding is null || !DropboxProviderConfiguration.HasFileRequestAccess(binding.Scopes))
            { ShowStatus("Enable File Requests for this account first."); return; }
            OpenFileRequestEditor(id, null);
        }
        else await OpenCreateAccountLinkAsync(id);
    }
    private void OpenAccountSharingUrl(string url)
    {
        try { AccountSharingLookup.ValidateUrl(url); Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) { ShowStatus("The sharing page could not be opened: " + error.Message); }
    }
}
