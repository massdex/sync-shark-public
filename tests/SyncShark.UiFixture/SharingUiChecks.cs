using Massdex.SyncShark;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SyncShark.UiFixture;

// Controlled HTTP supplies provider input; actual MainWindow, stores, clients, selectors,
// scroll events, details host and dirty-navigation decisions remain production code.
internal static class SharingUiChecks
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority)); theme.Initialize();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var store = new DropboxConnectionStore(instance.StateAuthority);
        foreach (var name in new[] { "a", "b" })
        {
            var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "dbid:sharing-" + name, name + "-root", name + "-home", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequestedScopes(true), DisplayName: "Same Name", Email: name + "@example.invalid").Normalize();
            Await(store.SaveAsync(binding with { CredentialReference = name == "a" ? WindowsDropboxRefreshTokenStore.RegisteredTarget : WindowsDropboxRefreshTokenStore.AccountTarget(binding.ConnectionId) }));
        }
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http, new SyncPairStore(instance.StateAuthority),
            new SyncRuntimeStateStore(instance.StateAuthority), store, new Tokens(), new SyncAuthorityDatabase(Path.Combine(root, "authority.db")));
        MainWindow.SuppressLoadedStartupForTest = true;
        var window = new MainWindow(instance.StateAuthority, theme) { Width = 1240, Height = 790,
            Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false };
        Get<SyncAuthorityRuntimeHost>(window, "_authorityHost").Dispose(); Set(window, "_authorityHost", host);
        try
        {
            window.Show(); Call(window, "RefreshAccountPickers"); Call(window, "NavigateTo", "Sharing", true);
            Until(() => Get<object?>(window, "_sharingLoad") is null, "initial account pages complete");
            var grid = Get<DataGrid>(window, "_sharingRows");
            Require(grid.Items.Count == 120 && handler.ListCalls == 4, "Opening reads only one page per account/namespace, not the whole account");
            Require(grid.EnableRowVirtualization && grid.SelectionMode == DataGridSelectionMode.Extended, "Sharing rows are virtualized and multi-selectable");
            var rows = grid.Items.Cast<AccountSharingRow>().ToArray();
            Require(rows.Select(r => r.Key).Distinct().Count() == 120 && rows.Select(r => r.Key.AccountId).Distinct().Count() == 2,
                "Duplicate item names remain separate account/namespace identities");
            grid.SelectedItem = rows[0]; Until(() => Get<object?>(window, "_sharingEditor") is not null, "selected link opens its real editor");
            var details = Get<DetailsHost>(window, "_sharingDetails");
            var combos = Descendants<ComboBox>(details.Content).ToArray();
            Require(combos.Length == 4 && combos.All(c => !c.Text.StartsWith("Keep", StringComparison.Ordinal)), "Editor shows current values without Keep prefixes");
            combos[0].SelectedValue = "team";
            combos[2].SelectedValue = "set";
            var password = Descendants<PasswordBox>(details.Content).Single(); password.Password = "in-memory-fixture-only";
            var sameContent = details.Content;
            var sidebarExpand = Descendants<Button>(sameContent).Single(button =>
                System.Windows.Automation.AutomationProperties.GetName(button) == "Expand Details");
            details.Expand(window); Pump();
            Require(!sidebarExpand.IsVisible, "Expanded Sharing editor hides the sidebar-only expand/close controls");
            details.Collapse(); Pump();
            Require(sidebarExpand.IsVisible, "Returning to the sidebar restores its own controls");
            Require(ReferenceEquals(sameContent, details.Content) && combos[0].SelectedValue as string == "team" && password.Password.Length > 0,
                "Sidebar to dialog to sidebar retains the exact editor and unsaved values");
            ReplyToDialog(app, false); Call(window, "NavigateTo", "Dashboard", true);
            Require(Get<Border>(window, "_sharingPage").IsVisible && ReferenceEquals(details, Get<DetailsHost>(window, "_sharingDetails")),
                "Cancelling Discard keeps the current Sharing editor and navigation");
            ReplyToDialog(app, true); Call(window, "NavigateTo", "Dashboard", true);
            Require(password.Password.Length == 0 && Get<object?>(window, "_sharingEditor") is null, "Discard clears the secret input and closes the editor");
            Call(window, "NavigateTo", "Sharing", true); Until(() => Get<object?>(window, "_sharingLoad") is null, "fresh Sharing page");
            grid = Get<DataGrid>(window, "_sharingRows");
            handler.MetadataGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var staleEditor = (Task)Call(window, "OpenAccountLinkEditorAsync", ((AccountSharingRow)grid.Items[0]).Link)!;
            Until(() => handler.MetadataStarted, "old editor read is in flight");
            var accountPicker = Get<ComboBox>(window, "_sharingAccount");
            var nextAccount = rows.Select(r => r.Key.AccountId).Distinct().First(id => id != ((AccountSharingRow)grid.Items[0]).Key.AccountId);
            accountPicker.SelectedValue = nextAccount;
            Until(() => Get<object?>(window, "_sharingLoad") is null && grid.Items.Count == 60, "account switch loads only its rows");
            handler.MetadataGate.SetResult(true); handler.MetadataGate = null; Await(staleEditor);
            Require(Get<object?>(window, "_sharingEditor") is null, "A late old-account read cannot open an editor in the new account");
            accountPicker.SelectedValue = ""; Until(() => Get<object?>(window, "_sharingLoad") is null && grid.Items.Count == 120, "all-account view restored");
            Require(Get<Button>(window, "_sharingCreate").ToolTip is string linkPurpose &&
                    linkPurpose.Contains("who can open the link", StringComparison.Ordinal) &&
                    AutomationProperties.GetHelpText(Get<Button>(window, "_sharingCreate")) == linkPurpose,
                "The actual Shared Links command explains its account, item and access-review purpose after loading");
            var beforePages = handler.ListCalls;
            handler.FailNextPage = true;
            var scroll = Descendants<ScrollViewer>(grid).First();
            scroll.ScrollToEnd(); Pump(); Until(() => Get<object?>(window, "_sharingLoad") is null && Get<Button>(window, "_sharingMore").IsVisible, "failed page retains explicit retry");
            Require(grid.Items.Count == 123 && handler.ListCalls == beforePages + 4, "A failed page never auto-spins and other namespaces retain successful pages");
            Get<Button>(window, "_sharingMore").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Until(() => Get<object?>(window, "_sharingLoad") is null && grid.Items.Count == 124, "retry loads only the retained failed page");
            Require(handler.ListCalls == beforePages + 5, "Successful continuation pages are not fetched again");
            Set(window, "_renderingSharing", true);
            grid.SelectedItems.Add(grid.Items[0]); grid.SelectedItems.Add(grid.Items[1]); grid.SelectedItems.Add(grid.Items[2]);
            Set(window, "_renderingSharing", false); Call(window, "UpdateSharingSelection");
            Require(Get<Button>(window, "_sharingRevoke").IsEnabled, "Three exact visible links enable bounded revoke");
            handler.RejectOneEffect = true; handler.UncertainOneEffect = true;
            ReplyToDialog(app, true); Await((Task)Call(window, "RevokeSelectedSharingAsync")!);
            Require(handler.Effects == 3 && grid.Items.Count == 123, "Bulk revoke keeps failed and uncertain rows and removes only confirmed success");
            Require(Get<TextBlock>(window, "_sharingSummary").Text.Contains("Not applied: 1", StringComparison.Ordinal), "Bulk UI reports partial failure");
            var remaining = grid.Items.OfType<AccountSharingRow>().Where(r => r.Outcome.Length > 0).ToArray();
            Require(remaining.Length == 2 && remaining.Any(r => r.Outcome.Contains("could not", StringComparison.Ordinal)) &&
                remaining.Any(r => r.Outcome.StartsWith("Not confirmed", StringComparison.Ordinal)), "Failed and uncertain rows retain distinct individual results");
            Set(window, "_renderingSharing", true); grid.SelectedItems.Clear(); grid.SelectedItem = remaining.Single(r => r.Outcome.StartsWith("Not confirmed", StringComparison.Ordinal));
            Set(window, "_renderingSharing", false); Call(window, "UpdateSharingSelection");
            Require(!Get<Button>(window, "_sharingRevoke").IsEnabled, "An uncertain mutation is not offered an automatic retry");
            var tabs = Get<TabControl>(window, "_sharingTabs");
            var revoke = Get<Button>(window, "_sharingRevoke");
            var select = Get<Button>(window, "_sharingSelect");
            Require(LinkBulkControlsShown(window),
                "Shared Links keeps both link-bulk controls present and visible in the one realised toolbar");
            // Detection is demonstrated rather than asserted: the oracle reads the realised production
            // toolbar, so it has to refuse a control that toolbar no longer shows. The sentinel touches a
            // real production control, is re-read by the same oracle, and is restored before the next
            // assertion, so no sentinel state is left behind for any later check.
            var sentinelVisibility = revoke.Visibility;
            revoke.Visibility = Visibility.Collapsed; window.UpdateLayout(); Pump();
            Require(!LinkBulkControlsShown(window),
                "The toolbar oracle detects a link-bulk control that the realised toolbar has hidden");
            revoke.Visibility = sentinelVisibility; window.UpdateLayout(); Pump();
            Require(LinkBulkControlsShown(window) && revoke.Visibility == Visibility.Visible && revoke.IsVisible,
                "Restoring the hidden link-bulk control restores the same oracle result and leaves no sentinel behind");
            var viewSwitch = Get<SimpleDetailedSwitch>(window, "_sharingViewSwitch");
            viewSwitch.DetailedSegment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            Require(viewSwitch.IsDetailed && LinkBulkControlsShown(window),
                "Detailed Shared Links rows keep both link-bulk controls visible");
            tabs.SelectedIndex = 1;
            Until(() => Get<object?>(window, "_sharingLoad") is null && grid.Items.Count == 2, "file requests use the account collection only");
            Require(Get<Button>(window, "_sharingCreate").ToolTip is string requestPurpose &&
                    requestPurpose.Contains("lets others upload", StringComparison.Ordinal) &&
                    AutomationProperties.GetHelpText(Get<Button>(window, "_sharingCreate")) == requestPurpose,
                "The actual File Requests command restores its upload purpose after its loading lock clears");
            Require(LinkBulkControlsHidden(window),
                "File Requests collapses both Shared Links bulk controls instead of only disabling them");
            viewSwitch.SimpleSegment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            Require(!viewSwitch.IsDetailed && LinkBulkControlsHidden(window),
                "Simple request rows keep both link-bulk controls collapsed");
            var requestRow = grid.Items.OfType<AccountSharingRow>().First();
            Require(requestRow.Request is not null && requestRow.Link is null && requestRow.ItemKind == "File request",
                "A file request row describes itself as a File request rather than falsely a File");
            grid.SelectedItem = requestRow; Until(() => Get<object?>(window, "_sharingEditor") is not null, "file request opens sidebar editor");
            Require(!revoke.IsEnabled, "File requests cannot enter shared-link revocation");
            // A file request row is an internal Sync Shark entity, so the renderer supplies the one shared
            // product mark for that row and style. The previous expectation of a pack PNG is not produced
            // by the current renderer, so the claim is compared with the real factory instead of a stale
            // literal; the shell-image refusal beside it is unchanged, and no check was weakened.
            var (requestIcon, requestMark) = RequestRowMark(grid);
            Require(requestIcon is not null && requestMark is not null && ReferenceEquals(requestIcon.Source, requestMark),
                "The actual file request row ShellTypeImage chooses the shared Sync Shark product mark");
            Require(!SamePixels(requestIcon?.Source, ShellResolve("")),
                "The actual file request row no longer resolves the extension-less shell image");
            Call(window, "TryCloseSharingEditor");
            accountPicker.SelectedValue = nextAccount;
            Until(() => Get<object?>(window, "_sharingLoad") is null && grid.Items.Count == 1, "one account re-renders only its requests");
            Require(LinkBulkControlsHidden(window),
                "Account re-render keeps both link-bulk controls collapsed on File Requests");
            var (reRenderedIcon, reRenderedMark) = RequestRowMark(grid);
            Require(reRenderedIcon is not null && reRenderedMark is not null &&
                    ReferenceEquals(reRenderedIcon.Source, reRenderedMark),
                "A re-rendered request row still chooses the shared Sync Shark product mark");
            accountPicker.SelectedValue = "";
            Until(() => Get<object?>(window, "_sharingLoad") is null && grid.Items.Count == 2, "all-account request view restored");
            tabs.SelectedIndex = 0;
            Until(() => Get<object?>(window, "_sharingLoad") is null && grid.Items.Count >= 120, "shared links restored after the request tab");
            Require(LinkBulkControlsShown(window),
                "Returning to Shared Links restores both link-bulk controls");
            var folderRow = grid.Items.OfType<AccountSharingRow>().First(r => r.Link is not null && r.ItemKind == "Folder");
            var pdfRow = grid.Items.OfType<AccountSharingRow>().First(r => r.Path is not null && r.Path.EndsWith(".pdf", StringComparison.Ordinal));
            Require(folderRow.Link!.ItemKind == "folder" && System.IO.Path.GetExtension(folderRow.Path!) == "" &&
                pdfRow.ItemKind == "File" && System.IO.Path.GetExtension(pdfRow.Path!) == ".pdf",
                "The fixture rows are a real extension-less shared folder and a real shared PDF");
            var folderShell = ShellResolve("<folder>"); var pdfShell = ShellResolve(".pdf");
            Require(folderShell is not null && pdfShell is not null, "The host shell resolves the shared folder and PDF type images");
            var folderSource = RowIconSource(grid, row => row.Link is not null && row.ItemKind == "Folder", source => SamePixels(source, folderShell));
            Require(SamePixels(folderSource, folderShell) && !IsAppAsset(folderSource),
                "An actual shared folder link keeps the real Windows folder type image");
            var pdfSource = RowIconSource(grid, row => row.Path is not null && row.Path.EndsWith(".pdf", StringComparison.Ordinal),
                source => SamePixels(source, pdfShell));
            Require(SamePixels(pdfSource, pdfShell) && !SamePixels(pdfSource, folderShell) && !IsAppAsset(pdfSource),
                "An actual shared PDF link keeps the real Windows PDF type image");
            Set(window, "_renderingSharing", true); grid.SelectedItems.Clear();
            grid.SelectedItem = grid.Items.OfType<AccountSharingRow>().First(r => r.Link is not null && r.Outcome.Length == 0);
            Set(window, "_renderingSharing", false); Call(window, "UpdateSharingSelection");
            Require(revoke.IsEnabled, "A revocable link still enables bounded revoke after returning to Shared Links");
            viewSwitch.DetailedSegment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            Require(viewSwitch.IsDetailed && LinkBulkControlsShown(window) && revoke.IsEnabled,
                "Detailed Shared Links rows keep both controls visible and the selected link revocable");
            Require(handler.Effects == 3, "Reading, navigation and editor presentation did not mutate provider data");
            Call(window, "TryCloseSharingEditor");
            foreach (var firstPageCount in new[] { 0, 1 })
            {
                handler.FirstPageLinkCount = firstPageCount;
                Await((Task)Call(window, "LoadAccountSharingAsync", true, false)!);
                window.UpdateLayout(); Pump();
                var more = Get<Button>(window, "_sharingMore");
                var shortPageScroll = Descendants<ScrollViewer>(grid).First();
                Require(grid.Items.Count == firstPageCount * 4 && shortPageScroll.ScrollableHeight == 0 &&
                        more.IsVisible && more.IsEnabled && AutomationProperties.GetName(more) == "Load More",
                    $"A {firstPageCount}-item provider page with a cursor exposes Load More even without a scrollable list");
                var callsBeforeMore = handler.ListCalls;
                handler.PageGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Until(() => handler.ListCalls == callsBeforeMore + 1, "explicit continuation reaches the actual provider client");
                Require(!more.IsEnabled && AutomationProperties.GetItemStatus(more) == "In progress" &&
                        more.ToolTip?.ToString()?.Contains("loading", StringComparison.OrdinalIgnoreCase) == true,
                    "Load More shows live command feedback and explains why repeated activation is unavailable");
                more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
                Require(handler.ListCalls == callsBeforeMore + 1, "Repeated Load More cannot duplicate an in-flight page");
                handler.PageGate.SetResult(true); handler.PageGate = null;
                Until(() => Get<object?>(window, "_sharingLoad") is null && grid.Items.Count == (firstPageCount + 1) * 4,
                    "Short-page continuation retains the first page and displays the actual remaining links");
                Require(handler.ListCalls == callsBeforeMore + 4 && more.Visibility == Visibility.Collapsed,
                    "One explicit action reads each remaining namespace once and hides Load More when its cursors are exhausted");
            }
            handler.PaginatedRequests = true;
            tabs.SelectedIndex = 1;
            Until(() => Get<object?>(window, "_sharingLoad") is null && grid.Items.Count == 2,
                "Short File Requests pages retain their account cursors");
            var requestMore = Get<Button>(window, "_sharingMore");
            Require(requestMore.IsVisible && requestMore.IsEnabled && LinkBulkControlsHidden(window),
                "The same Load More control serves File Requests without exposing link mutation controls");
            requestMore.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Until(() => Get<object?>(window, "_sharingLoad") is null && grid.Items.Count == 4,
                "Explicit continuation also loads the remaining File Requests");
            Require(requestMore.Visibility == Visibility.Collapsed && handler.Effects == 3,
                "Pagination exhausts its cursor without creating, editing or revoking provider data");
            Console.WriteLine("PASS Sharing UI real-window checks with controlled HTTP; no live provider or installed-state effects");
            return 0;
        }
        finally
        {
            window.RequestProductExit(); MainWindow.SuppressLoadedStartupForTest = false; app.Shutdown();
        }
    }

    // The one link-bulk toolbar decision, read from the realised production toolbar rather than from
    // the two backing fields alone: both controls must be present in the live visual tree, share their
    // container, and actually be visible or actually be hidden there. Reading the fields by themselves
    // could not detect a control the built toolbar no longer shows, which is the disclosed weakness
    // this oracle replaces. Only the collapse mutation exercised in the fixture is claimed as shown.
    private static bool LinkBulkControlsShown(Window window)
    {
        var (revoke, select) = ToolbarBulkControls(window);
        return revoke is not null && select is not null &&
            revoke.Visibility == Visibility.Visible && select.Visibility == Visibility.Visible &&
            revoke.IsVisible && select.IsVisible;
    }

    private static bool LinkBulkControlsHidden(Window window)
    {
        var (revoke, select) = ToolbarBulkControls(window);
        return revoke is not null && select is not null &&
            revoke.Visibility == Visibility.Collapsed && select.Visibility == Visibility.Collapsed &&
            !revoke.IsVisible && !select.IsVisible;
    }

    // Both controls in the realised tree and in one shared container, or nothing is returned, so a
    // control that left the toolbar fails the oracle instead of passing on its stored property.
    private static (Button? Revoke, Button? Select) ToolbarBulkControls(Window window)
    {
        var revoke = Get<Button>(window, "_sharingRevoke");
        var select = Get<Button>(window, "_sharingSelect");
        var realised = Descendants<Button>(window).ToList();
        return realised.Contains(revoke) && realised.Contains(select) && ReferenceEquals(revoke.Parent, select.Parent)
            ? (revoke, select)
            : (null, null);
    }

    private static void ReplyToDialog(App app, bool yes)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        var deadline = DateTime.UtcNow.AddSeconds(5);
        timer.Tick += (_, _) =>
        {
            if (app.Windows.OfType<AppDialogWindow>().FirstOrDefault() is { } dialog)
            { timer.Stop(); ((Button)dialog.FindName(yes ? "PrimaryButton" : "SecondaryButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
            else if (DateTime.UtcNow > deadline) { timer.Stop(); throw new InvalidDataException("Expected confirmation dialog did not appear."); }
        }; timer.Start();
    }
    private static void Require(bool value, string text) { if (!value) throw new InvalidDataException(text); Console.WriteLine("PASS " + text); }
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void Until(Func<bool> ready, string label)
    { var end = DateTime.UtcNow.AddSeconds(15); while (!ready() && DateTime.UtcNow < end) { Pump(); Thread.Sleep(5); } Require(ready(), label); Pump(); }
    private static void Await(Task task) { Until(() => task.IsCompleted, "async operation completed"); task.GetAwaiter().GetResult(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    // The actual DataGrid row's own ShellTypeImage, resolved through the real virtualized container.
    private static ShellTypeImage? RowIcon(DataGrid grid, Func<AccountSharingRow, bool> match)
    {
        if (grid.Items.OfType<AccountSharingRow>().FirstOrDefault(match) is not { } item) return null;
        grid.ScrollIntoView(item); grid.UpdateLayout();
        if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row) return null;
        row.UpdateLayout();
        return Descendants<ShellTypeImage>(row).FirstOrDefault();
    }
    private static ImageSource? RowIconSource(DataGrid grid, Func<AccountSharingRow, bool> match, Func<ImageSource?, bool> accept)
    {
        var end = DateTime.UtcNow.AddSeconds(20);
        ImageSource? source = null;
        while (DateTime.UtcNow < end)
        {
            source = RowIcon(grid, match)?.Source;
            if (accept(source)) return source;
            Pump(); Thread.Sleep(5);
        }
        return source;
    }
    // The production resolver the control itself calls, so an actual row can be compared with it.
    private static ImageSource? ShellResolve(string key) =>
        (ImageSource?)typeof(ShellTypeImage).GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [key]);
    // The file request row's own branded source, waited for on a recycled row container and compared
    // with the same shared factory the row control calls, so a stale literal cannot stand in for it.
    private static (ShellTypeImage? Icon, ImageSource? Mark) RequestRowMark(DataGrid grid)
    {
        var end = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var icon = RowIcon(grid, row => row.Request is not null);
            var mark = icon is not null && icon.DataContext is not null
                ? BrandedItemImages.For(icon.DataContext, icon.StatusIconStyle) : null;
            if (icon is not null && ReferenceEquals(icon.Source, mark)) return (icon, mark);
            if (DateTime.UtcNow >= end) return (icon, mark);
            Pump(); Thread.Sleep(5);
        }
    }
    private static bool IsAppAsset(ImageSource? source) => source is BitmapImage { UriSource: { } uri } &&
        uri.OriginalString == "pack://application:,,,/SyncShark;component/Assets/SyncShark-64.png";
    private static bool SamePixels(ImageSource? left, ImageSource? right) =>
        left is BitmapSource a && right is BitmapSource b && a.PixelWidth == b.PixelWidth && a.PixelHeight == b.PixelHeight &&
        PixelDigest(a) == PixelDigest(b);
    private static string PixelDigest(BitmapSource bitmap)
    {
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || bitmap.Format.BitsPerPixel <= 0) return "unreadable";
        var stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
    }
    private sealed class Tokens(string account = "") : IDropboxRefreshTokenStore
    {
        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding binding) => new Tokens(binding.AccountId[^1..]);
        public Task<string?> ReadAsync(CancellationToken token = default) => Task.FromResult<string?>(account);
        public Task WriteAsync(string value, CancellationToken token = default) => throw new InvalidOperationException("No credential writes in fixture");
        public Task DeleteAsync(CancellationToken token = default) => throw new InvalidOperationException("No credential deletion in fixture");
    }
    private sealed class Handler : HttpMessageHandler
    {
        // Two fixed link shapes let the real window show a shared folder and a shared PDF beside
        // the ordinary files; both indices stay inside the first page so the counts stay unchanged.
        internal const int FolderLinkIndex = 5, PdfLinkIndex = 9;
        internal int ListCalls, Effects;
        internal int FirstPageLinkCount = 30;
        internal bool RejectOneEffect, UncertainOneEffect, FailNextPage, MetadataStarted, PaginatedRequests;
        internal TaskCompletionSource<bool>? MetadataGate, PageGate;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri?.Host != "api.dropboxapi.com") throw new InvalidOperationException("Unexpected fixture host");
            if (request.RequestUri.AbsolutePath == "/oauth2/token")
            {
                var form = await request.Content!.ReadAsStringAsync(token);
                var account = form.Split('&').Single(s => s.StartsWith("refresh_token=", StringComparison.Ordinal)).Split('=')[1];
                return Json(new { access_token = account, expires_in = 3600, account_id = "dbid:sharing-" + account });
            }
            var id = request.Headers.Authorization!.Parameter!;
            if (request.RequestUri.AbsolutePath == "/2/users/get_current_account")
                return Json(new { account_id = "dbid:sharing-" + id, root_info = new { root_namespace_id = id + "-root", home_namespace_id = id + "-home" } });
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            using var scope = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Path-Root").Single());
            var ns = scope.RootElement.GetProperty("namespace_id").GetString()!;
            if (request.RequestUri.AbsolutePath == "/2/sharing/list_shared_links")
            {
                ListCalls++; var later = body.RootElement.TryGetProperty("cursor", out _);
                if (later && PageGate is { } pageGate) await pageGate.Task.WaitAsync(token);
                if (later && FailNextPage) { FailNextPage = false; return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }; }
                return Json(new { links = Enumerable.Range(later ? 30 : 0, later ? 1 : FirstPageLinkCount).Select(i => Link(ns, i)), has_more = !later, cursor = "fixture-next" });
            }
            if (request.RequestUri.AbsolutePath == "/2/sharing/get_shared_link_metadata")
            {
                if (MetadataGate is { } gate) { MetadataStarted = true; await gate.Task; }
                var url = body.RootElement.GetProperty("url").GetString()!;
                return Json(Link(ns, int.Parse(url.Split('/')[^1].Split('.')[0])));
            }
            if (request.RequestUri.AbsolutePath == "/2/sharing/revoke_shared_link")
            { Effects++; return RejectOneEffect && Effects == 2 ? new(HttpStatusCode.Conflict) { Content = new StringContent("{}") } :
                UncertainOneEffect && Effects == 3 ? new(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") } : Json<object?>(null); }
            if (request.RequestUri.AbsolutePath == "/2/file_requests/list_v2")
                return Json(new { file_requests = new[] { Request(id) }, has_more = PaginatedRequests, cursor = "requests-next" });
            if (request.RequestUri.AbsolutePath == "/2/file_requests/list/continue")
                return Json(new { file_requests = new[] { Request(id + "-next") }, has_more = false });
            if (request.RequestUri.AbsolutePath == "/2/file_requests/get") return Json(Request(id));
            throw new InvalidOperationException("Unexpected fixture route");
        }
        private static object Link(string ns, int index) => new Dictionary<string, object?>
        {
            [".tag"] = index == FolderLinkIndex ? "folder" : "file",
            ["url"] = $"https://www.dropbox.com/s/{ns}/{index}.txt", ["id"] = ns + "-" + index,
            ["name"] = "Same Name " + index,
            ["path_lower"] = index == FolderLinkIndex ? "/Photos " + index
                : index == PdfLinkIndex ? "/Report " + index + ".pdf" : "/" + index + ".txt",
            ["link_permissions"] = new { effective_audience = "public", link_access_level = "viewer", can_revoke = true,
                can_set_password = true, can_remove_password = true, can_set_expiry = true, can_remove_expiry = true,
                allow_download = true, can_allow_download = true, can_disallow_download = true,
                audience_options = new[] { new { audience = "team", allowed = true } } }
        };
        private static object Request(string id) => new { id = "request-" + id, url = "https://www.dropbox.com/request/" + id,
            title = "Collect Files", destination = "/Inbox", description = "Fixture", created = "2026-01-01T00:00:00Z", is_open = true, file_count = 0 };
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
