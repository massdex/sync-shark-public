using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static partial class Program
{
    private static int RunSharedDetailsChromeChecks(string rawRoot)
    {
        var root = Path.GetFullPath(rawRoot);
        Directory.CreateDirectory(root);
        var priorStartup = MainWindow.SuppressLoadedStartupForTest;
        App? app = null;
        MainWindow? window = null;
        PilotInstanceLock? instance = null;
        ThemeManager? theme = null;
        try
        {
            app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
            theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
            theme.Initialize();
            using var http = new HttpClient(new EndpointUiNoNetwork());
            using var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http,
                new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
                new DropboxConnectionStore(instance.StateAuthority), new InspectionCredentialBoundary(),
                new SyncAuthorityDatabase(Path.Combine(root, "state", "authority.db")));
            Task.Run(() => host.LoadPairsAsync()).GetAwaiter().GetResult();
            MainWindow.SuppressLoadedStartupForTest = true;
            window = new MainWindow(instance.StateAuthority, theme)
            {
                Width = 1240, Height = 790, Left = -20000, Top = -20000, ShowInTaskbar = false,
                ShowActivated = false
            };
            BindAuthorityHost(window, host);
            window.Show();
            var strips = new List<DetailsStrip>();
            AssertDetailsHostContract(window);
            AssertActivityDetailsChrome(window, strips);
            AssertSharingDetailsChrome(window, strips);
            AssertStorageDetailsChrome(window, root, strips);
            AssertSharedDetailsActionStrip(strips);
            AssertStorageSelectionCleared(window);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result = "PASS",
                selector = "--shared-details-chrome-checks",
                source = "DetailsHost.RegisterSidebarOnly, SharedPresentation.DetailsChrome.ActionStrip and the real Activity, Sharing and Storage consumers",
                consumers = new[] { "shared-host", "activity", "sharing", "storage", "recovery-tooltip" }
            }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            MainWindow.SuppressLoadedStartupForTest = priorStartup;
            window?.RequestProductExit();
            theme?.Dispose();
            instance?.Dispose();
            app?.Shutdown();
        }
    }

    // One populated details surface: the shared strip actually rendered by that consumer, with the two
    // controls the strip owns. Every consumer asserts against these rendered controls, not a constant.
    private sealed record DetailsStrip(string Consumer, string Subject, StackPanel Strip, Button Expand, Button Close);

    // The shared host contract: exact restoration, one content instance, idempotence and no duplicate
    // handlers, re-registration replacing the previous controls, and an empty registration clearing them.
    private static void AssertDetailsHostContract(MainWindow window)
    {
        var content = new StackPanel();
        var visible = new Button { Visibility = Visibility.Visible };
        var collapsed = new Button { Visibility = Visibility.Collapsed };
        var host = new DetailsHost(content, new ContentPresenter());
        host.Present("details model");
        host.RegisterSidebarOnly(visible, collapsed);
        EndpointUiRequire(visible.Visibility == Visibility.Visible && collapsed.Visibility == Visibility.Collapsed,
            "shared details host: registering while collapsed restores each control's own visibility");
        host.Expand(window);
        EndpointUiRequire(host.Expanded && visible.Visibility == Visibility.Collapsed &&
            collapsed.Visibility == Visibility.Collapsed && ReferenceEquals(host.Content, content) &&
            Equals(content.DataContext, "details model"),
            "shared details host: expanding hides the sidebar-only controls and keeps the one content instance");
        host.Expand(window);
        EndpointUiRequire(host.Expanded && ReferenceEquals(host.Content, content) &&
            visible.Visibility == Visibility.Collapsed && collapsed.Visibility == Visibility.Collapsed,
            "shared details host: a repeated expand is idempotent and adds no second host");
        host.Collapse();
        EndpointUiRequire(!host.Expanded && visible.Visibility == Visibility.Visible &&
            collapsed.Visibility == Visibility.Collapsed && ReferenceEquals(host.Content, content) &&
            Equals(content.DataContext, "details model"),
            "shared details host: returning restores the exact pre-expansion visibility and the same content");
        host.Collapse();
        EndpointUiRequire(!host.Expanded && host.Dialog is null && visible.Visibility == Visibility.Visible &&
            collapsed.Visibility == Visibility.Collapsed,
            "shared details host: a repeated return leaves no open dialog and keeps each control's own visibility");
        // A new registration replaces the previous controls - the shape Storage uses when its selection
        // changes - and applies the current state to the controls it now owns.
        collapsed.Visibility = Visibility.Visible;
        host.RegisterSidebarOnly(collapsed);
        host.Expand(window);
        EndpointUiRequire(visible.Visibility == Visibility.Visible && collapsed.Visibility == Visibility.Collapsed,
            "shared details host: a new registration releases the previous controls and hides the new one");
        host.Collapse();
        EndpointUiRequire(!host.Expanded && visible.Visibility == Visibility.Visible &&
            collapsed.Visibility == Visibility.Visible,
            "shared details host: a released control is no longer driven and the new one is restored");
        // An empty registration clears the set, so a cleared consumer drives nothing at all.
        host.RegisterSidebarOnly();
        host.Expand(window);
        host.Collapse();
        EndpointUiRequire(!host.Expanded && visible.Visibility == Visibility.Visible &&
            collapsed.Visibility == Visibility.Visible,
            "shared details host: an empty registration clears the sidebar-only set");
    }

    // UIV6-2, UIV6-3 and UIV6-H1 for the real Activity sidebar. A synthetic selected record is pushed
    // through the product's own inspection entry point, so the inspector and its shared action strip are
    // actually realised instead of being read from a collapsed, unbuilt panel, and their order, contents,
    // hide-and-restore behaviour and repeated use are checked on the rendered controls.
    private static void AssertActivityDetailsChrome(MainWindow window, List<DetailsStrip> strips)
    {
        DetailsNavigate(window, "Activity");
        var row = new ActivityRow(DateTimeOffset.Now.ToString("O"), "Work/Invoices/amendment.pdf",
            "Completed upload", "Uploaded and verified.", PairId: "amendment-pair", PairName: "Amendment pair",
            OperationId: "amendment-details", ItemKind: "File", FileType: "PDF", SizeBytes: 2048);
        DiscardCall(window, "InspectActivity", new object?[] { row });
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        window.UpdateLayout();
        var host = DiscardField<DetailsHost?>(window, "_detailsHost") ??
                   throw new InvalidDataException("The Activity sidebar details host does not exist.");
        var content = host.Content;
        var (strip, expand, close) = DetailsStripOf(content, "Expand Details", "Close Details");
        EndpointUiRequire(strip.Visibility == Visibility.Visible && expand.Name == "DetailsExpandButton" &&
            ReferenceEquals(close.Parent, strip) && strip.Children.Count == 2,
            "details chrome: the populated Activity sidebar starts with one shared strip whose expand and close controls are visible");
        AssertRenderedDetailsStrip("Activity", "Details", strip, expand, close);
        DetailsRoundTrip(window, "Activity", host, content, strip, useEscape: true);
        DetailsRoundTrip(window, "Activity", host, content, strip, useEscape: false);
        strips.Add(new DetailsStrip("Activity", "Details", strip, expand, close));
    }

    // UIV6-2, UIV6-3 and UIV6-H1 for real Sharing. The header is built exactly as the sharing editor
    // builds it and is presented on its own, so no provider, credential or product-state read starts.
    private static void AssertSharingDetailsChrome(MainWindow window, List<DetailsStrip> strips)
    {
        DetailsNavigate(window, "Sharing");
        var constructor = typeof(MainWindow).GetMethod("SharingEditor", DiscardFlags) ??
                          throw new InvalidDataException("The Sharing detail-header constructor is absent.");
        var arguments = new object?[] { "Manage Shared Link", null, null, null, null };
        var session = constructor.Invoke(window, arguments) ??
                      throw new InvalidDataException("The Sharing detail header was not built.");
        (session.GetType().GetMethod("Present", DiscardFlags) ??
            throw new MissingMethodException(session.GetType().FullName, "Present")).Invoke(session, null);
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        var host = DiscardField<DetailsHost?>(window, "_sharingDetails") ??
                   throw new InvalidDataException("The Sharing details host does not exist.");
        var content = host.Content;
        host.Present("sharing amendment model");
        window.UpdateLayout();
        var (strip, expand, close) = DetailsStripOf(content, "Expand Details", "Close Details");
        EndpointUiRequire(strip.Visibility == Visibility.Visible && ReferenceEquals(close.Parent, strip) &&
            strip.Children.Count == 2,
            "details chrome: the populated Sharing sidebar starts with one shared strip whose expand and close controls are visible");
        AssertRenderedDetailsStrip("Sharing", "Details", strip, expand, close);
        DetailsRoundTrip(window, "Sharing", host, content, strip, useEscape: true);
        DetailsRoundTrip(window, "Sharing", host, content, strip, useEscape: false);
        strips.Add(new DetailsStrip("Sharing", "Details", strip, expand, close));
    }

    // UIV6-2, UIV6-3 and UIV6-H1 for real Storage. One synthetic saved mapping is pushed through the
    // page's own selection entry point after the page's own load has finished, so the details strip is
    // the rendered Storage chrome rather than a helper. No file, provider or product-state effect starts.
    private static void AssertStorageDetailsChrome(MainWindow window, string root, List<DetailsStrip> strips)
    {
        DetailsNavigate(window, "Storage");
        if (typeof(MainWindow).GetMethod("LoadStoragePageAsync", DiscardFlags)?.Invoke(window, new object?[] { true })
            is Task reload)
            PumpTask(window, reload);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var host = DiscardField<DetailsHost>(window, "_storageDetails");
        var row = new SyncStorageInventoryRow("amendment-storage-pair", "amendment-storage-account",
            SyncProviders.Dropbox, "Amendment storage pair", Path.Combine(root, "storage"), ".", "Not inspected",
            "Available offline", "Selected for sync", "Not inspected",
            "Saved pair policy and retained folder index; not a fresh filesystem observation",
            DateTimeOffset.UtcNow, "No retained index");
        DiscardCall(window, "ShowStorageDetails", new object?[]
        {
            new StorageInventoryDisplayRow(row, "amendment-storage-account")
        });
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        window.UpdateLayout();
        var content = host.Content;
        var (strip, expand, close) = DetailsStripOf(content, "Expand Storage Details", "Close Storage Details");
        EndpointUiRequire(strip.Visibility == Visibility.Visible && ReferenceEquals(close.Parent, strip) &&
            strip.Children.Count == 2,
            "details chrome: the populated Storage sidebar starts with one shared strip whose expand and close controls are visible");
        AssertRenderedDetailsStrip("Storage", "Storage Details", strip, expand, close);
        DetailsRoundTrip(window, "Storage", host, content, strip, useEscape: true);
        DetailsRoundTrip(window, "Storage", host, content, strip, useEscape: false);
        strips.Add(new DetailsStrip("Storage", "Storage Details", strip, expand, close));
    }

    // UIV6-H1: the shared strip proved at rendering level. All three populated consumers draw the same
    // expand outline and the same close cross, in the same order, rather than each restating them.
    private static void AssertSharedDetailsActionStrip(IReadOnlyList<DetailsStrip> strips)
    {
        EndpointUiRequire(strips.Count == 3 &&
            strips.Select(item => item.Consumer).Distinct(StringComparer.Ordinal).Count() == 3,
            "details action strip: Activity, Sharing and Storage each populated a real details selection");
        var expandGlyphs = strips.Select(item => RenderedGlyphData(item.Expand)).ToArray();
        var closeGlyphs = strips.Select(item => RenderedGlyphData(item.Close)).ToArray();
        EndpointUiRequire(expandGlyphs.All(data => data.Length == 1 && data[0] == expandGlyphs[0][0]) &&
            closeGlyphs.All(data => data.Length == 1 && data[0] == closeGlyphs[0][0]) &&
            expandGlyphs[0][0] != closeGlyphs[0][0],
            "details action strip: one shared expand outline and one shared close cross are rendered by all three consumers");
        EndpointUiRequire(strips.All(item => Equals(item.Expand.ToolTip, DetailsChrome.ExpandTooltip) &&
                Equals(item.Close.ToolTip, "Close " + item.Subject) &&
                string.Equals(AutomationProperties.GetName(item.Expand), "Expand " + item.Subject, StringComparison.Ordinal) &&
                string.Equals(AutomationProperties.GetName(item.Close), "Close " + item.Subject, StringComparison.Ordinal)),
            "details action strip: all three consumers share the one expand tooltip and the one accessible-name rule");
    }

    // The clearing path the previous check already executed: no Storage selection releases the chrome
    // registration instead of leaving a stale one, and the host still round-trips.
    private static void AssertStorageSelectionCleared(MainWindow window)
    {
        var host = DiscardField<DetailsHost>(window, "_storageDetails");
        DiscardCall(window, "ShowStorageDetails", new object?[] { null });
        window.UpdateLayout();
        var column = DiscardField<ColumnDefinition>(window, "_storageDetailColumn");
        host.Expand(window);
        host.Collapse();
        EndpointUiRequire(Math.Abs(column.Width.Value) < 0.01 && !host.Expanded,
            "details chrome: clearing the Storage selection releases its chrome and the host still round-trips");
    }

    // One real round trip on a rendered consumer: the sidebar strip is hidden while the larger window is
    // open, and the same content instance and model come back either by Escape or by the window's own
    // return command. The caller runs it twice, so a repeated round trip is exercised as well.
    private static void DetailsRoundTrip(MainWindow window, string consumer, DetailsHost host,
        FrameworkElement content, StackPanel strip, bool useEscape)
    {
        var route = useEscape ? "Escape" : "the return command";
        var model = content.DataContext;
        host.Expand(window);
        PumpUntil(window, () => host.Dialog is { IsVisible: true },
            "The " + consumer + " details dialog did not open.");
        var dialog = host.Dialog!;
        EndpointUiRequire(strip.Visibility == Visibility.Collapsed &&
            ReferenceEquals(dialog.HostedContent, content) && Equals(content.DataContext, model),
            "details chrome: the expanded " + consumer + " window hides the sidebar strip and hosts the same content and model");
        if (useEscape) dialog.RaiseEvent(DetailsEscapeKeyEvent(dialog));
        else
        {
            var returnButton = FindVisualChildren<Button>(dialog).FirstOrDefault(item =>
                string.Equals(AutomationProperties.GetName(item), "Return to sidebar", StringComparison.Ordinal)) ??
                throw new InvalidDataException("The expanded " + consumer + " details have no return command.");
            returnButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        PumpUntil(window, () => !host.Expanded, route + " did not return the " + consumer + " details to the sidebar.");
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        EndpointUiRequire(strip.Visibility == Visibility.Visible && ReferenceEquals(host.Content, content) &&
            Equals(content.DataContext, model),
            "details chrome: " + route + " restores the " + consumer + " strip, its content and its model");
    }

    // UIV6-H1: the rendered order of one consumer's strip, read from the arranged controls rather than
    // from the builder source, plus the two rendered drawings and the shared wording.
    private static void AssertRenderedDetailsStrip(string consumer, string subject, StackPanel strip,
        Button expand, Button close)
    {
        var expandX = expand.TransformToAncestor(strip).Transform(default(Point)).X;
        var closeX = close.TransformToAncestor(strip).Transform(default(Point)).X;
        EndpointUiRequire(strip.Children.IndexOf(expand) == 0 && strip.Children.IndexOf(close) == 1 &&
            expandX < closeX,
            "details action strip: " + consumer + " renders Expand then Close in that order");
        var expandGlyph = RenderedGlyphData(expand);
        var closeGlyph = RenderedGlyphData(close);
        EndpointUiRequire(expandGlyph.Length == 1 && closeGlyph.Length == 1 && expandGlyph[0] != closeGlyph[0] &&
            Equals(expand.ToolTip, DetailsChrome.ExpandTooltip) && Equals(close.ToolTip, "Close " + subject),
            "details action strip: " + consumer + " renders the shared expand outline and the close cross as two different drawings");
    }

    private static (StackPanel Strip, Button Expand, Button Close) DetailsStripOf(DependencyObject content,
        string expandName, string closeName)
    {
        var expand = FindVisualChildren<Button>(content).SingleOrDefault(item =>
            string.Equals(AutomationProperties.GetName(item), expandName, StringComparison.Ordinal)) ??
            throw new InvalidDataException("The details sidebar has no '" + expandName + "' control.");
        var close = FindVisualChildren<Button>(content).SingleOrDefault(item =>
            string.Equals(AutomationProperties.GetName(item), closeName, StringComparison.Ordinal)) ??
            throw new InvalidDataException("The details sidebar has no '" + closeName + "' control.");
        var strip = expand.Parent as StackPanel ??
                    throw new InvalidDataException("The details controls are not one shared action strip.");
        return (strip, expand, close);
    }

    private static string[] RenderedGlyphData(Button button) =>
        FindVisualChildren<System.Windows.Shapes.Path>(button)
            .Select(item => item.Data?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
            .Where(data => data.Length > 0).ToArray();

    private static KeyEventArgs DetailsEscapeKeyEvent(Window dialog)
    {
        var source = PresentationSource.FromVisual(dialog) ??
                     throw new InvalidDataException("The expanded details dialog has no input source for its Escape command.");
        return new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
    }

    private static void DetailsNavigate(MainWindow window, string page)
    {
        if (window.FindName("Navigation") is ListBox navigation)
            navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), page, StringComparison.OrdinalIgnoreCase));
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        window.UpdateLayout();
    }
}

