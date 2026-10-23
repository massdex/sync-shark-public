using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static partial class Program
{
    private const BindingFlags InternalAttentionFlags =
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static T InternalAttentionGet<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, InternalAttentionFlags)?.GetValue(target)
            ?? throw new MissingFieldException(target.GetType().FullName, name));

    private static void InternalAttentionSet(object target, string name, object? value) =>
        (target.GetType().GetField(name, InternalAttentionFlags)
         ?? throw new MissingFieldException(target.GetType().FullName, name)).SetValue(target, value);

    private static void InternalAttentionCall(object target, string name) =>
        (target.GetType().GetMethod(name, InternalAttentionFlags)
         ?? throw new MissingMethodException(target.GetType().FullName, name)).Invoke(target, null);

    private static void InternalAttentionRequire(bool condition, string description)
    {
        if (!condition) throw new InvalidDataException(description);
        Console.WriteLine("PASS " + description);
    }

    private static int RunInternalAttentionIconChecks(string rawRoot)
    {
        var root = Path.GetFullPath(rawRoot);
        Directory.CreateDirectory(root);
        var localRoot = Directory.CreateDirectory(Path.Combine(root, "local")).FullName;
        var priorStartup = MainWindow.SuppressLoadedStartupForTest;
        App? app = null;
        MainWindow? window = null;
        TrayFlyoutWindow? tray = null;
        PilotInstanceLock? instance = null;
        ThemeManager? theme = null;
        try
        {
            app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            // The branded renderer resolves its retained vector mark through the application pack
            // resources, so the descriptor and renderer checks run once the application exists.
            AssertInternalAttentionDescriptors();
            instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
            theme = new ThemeManager(new UiPreferencesStore(instance.StateAuthority));
            theme.Initialize();
            var preferences = new UiPreferencesStore(instance.StateAuthority);
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
            tray = AssertInternalAttentionSurfaces(window, localRoot, preferences, out var currentStyle);
            AssertInternalAttentionTray(tray, currentStyle);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result = "PASS",
                selector = "--internal-attention-icon-checks",
                source = "BrandedItemImages InternalAttentionIcon, SharedPresentation.AttentionIcon",
                surfaces = new[] { "home", "needs-attention-all", "needs-attention-problems", "tray-flyout" }
            }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            MainWindow.SuppressLoadedStartupForTest = priorStartup;
            tray?.Close();
            window?.RequestProductExit();
            theme?.Dispose();
            instance?.Dispose();
            app?.Shutdown();
        }
    }

    // The typed internal-icon descriptor and the existing branded renderer it dispatches to.
    private static void AssertInternalAttentionDescriptors()
    {
        InternalAttentionRequire(InternalAttentionIcon.FirstSyncReview.Role == "sync" &&
            InternalAttentionIcon.FirstSyncReview.State == "!",
            "internal attention icons: the first-sync review entity carries the sync role and needs-action state");
        InternalAttentionRequire(InternalAttentionIcon.PairProblem.Role == "attention" &&
            InternalAttentionIcon.PairProblem.State == "!" &&
            !ReferenceEquals(InternalAttentionIcon.FirstSyncReview, InternalAttentionIcon.PairProblem),
            "internal attention icons: a pair problem carries the attention role and needs-action state");
        var perStyle = new Dictionary<string, ImageSource>();
        foreach (var style in new[] { "Symbol", "Colour", "Combined" })
        {
            var review = BrandedItemImages.For(InternalAttentionIcon.FirstSyncReview, style);
            var problem = BrandedItemImages.For(InternalAttentionIcon.PairProblem, style);
            InternalAttentionRequire(review is DrawingImage && review.IsFrozen && problem is DrawingImage &&
                problem.IsFrozen && ReferenceEquals(review,
                    BrandedItemImages.For(InternalAttentionIcon.FirstSyncReview, style)),
                "internal attention icons: the existing branded renderer serves the " + style +
                " style for every internal attention entity");
            perStyle[style] = review;
        }
        InternalAttentionRequire(perStyle.Values.Distinct().Count() == 3,
            "internal attention icons: Colour, Symbol and Combined each render the internal entity differently");
    }

    // The three real surfaces, the file-conflict case, the live style preview and the actual row action.
    private static TrayFlyoutWindow AssertInternalAttentionSurfaces(MainWindow window, string localRoot,
        UiPreferencesStore preferences, out string currentStyle)
    {
        if (window.FindName("Navigation") is ListBox navigation)
            navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>()
                .Single(item => Equals(item.Tag, "Dashboard"));
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        var reviewPair = InternalAttentionPair("attention-review", localRoot, approved: false);
        var brokenPair = InternalAttentionPair("attention-broken", localRoot, approved: false);
        var conflictPair = InternalAttentionPair("attention-conflict", localRoot, approved: true);
        window.LoadPairSetForTest(new SyncPairSet([reviewPair, brokenPair, conflictPair], reviewPair.Id));
        // A confirmed folder problem on a pair that has not passed its first sync: the problem is
        // reported and the first-sync review for that pair is suppressed, as before.
        window.SetPairAttentionSourcesForTest(brokenPair.Id, "The paired folder is unavailable.",
            "The account needs reconnecting.", null, false);
        window.ConflictRows.Add(new DecisionRow(conflictPair.Id, conflictPair.Name, "report.pdf", "Conflict",
            "Both versions changed."));
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        InternalAttentionSet(window, "_attentionFingerprint", string.Empty);
        InternalAttentionCall(window, "RefreshFullApp");
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        var cases = window.TrayAttentionCases;
        InternalAttentionRequire(cases.Any(item => item.Pair.Id == reviewPair.Id &&
                ReferenceEquals(item.InternalIcon, InternalAttentionIcon.FirstSyncReview) && !item.IsRealFile),
            "internal attention icons: a real first-sync review row carries the typed sync descriptor");
        InternalAttentionRequire(cases.Any(item => item.Pair.Id == brokenPair.Id &&
                ReferenceEquals(item.InternalIcon, InternalAttentionIcon.PairProblem) && item.IsProblem),
            "internal attention icons: a broken pair carries the typed attention descriptor as a problem");
        InternalAttentionRequire(!cases.Any(item => item.Pair.Id == brokenPair.Id && !item.IsProblem) &&
                cases.Any(item => item.Pair.Id == reviewPair.Id && !item.IsProblem),
            "internal attention icons: a broken pair still suppresses its own first-sync review");
        var fileCase = cases.SingleOrDefault(item => item.Pair.Id == conflictPair.Id && item.IsRealFile) ??
                       throw new InvalidDataException("The real file conflict produced no attention row.");
        InternalAttentionRequire(fileCase.InternalIcon is null && fileCase.Title == "report.pdf",
            "internal attention icons: a real PDF conflict keeps its own filename and no internal descriptor");
        var fileGlyph = SharedPresentation.AttentionIcon.Create(fileCase.InternalIcon, fileCase.Title, 26);
        InternalAttentionRequire(fileGlyph is ShellTypeImage { DataContext: ActivityRow { ItemKind: "File" } fileRow } &&
                fileRow.Path == "report.pdf" && fileGlyph.Width > 0,
            "internal attention icons: a real file conflict keeps the Windows file-type presentation of its filename");

        // Home, the Needs Attention list and the problems tab all render through the one factory.
        var home = InternalAttentionGet<Panel>(window, "_homeAttention");
        var all = InternalAttentionGet<Panel>(window, "_attentionList");
        var problems = InternalAttentionGet<Panel>(window, "_attentionProblems");
        var surfaces = new[]
        {
            ("home", (Panel)home, true), ("needs attention", (Panel)all, true), ("problems", (Panel)problems, false)
        };
        var savedStyle = preferences.Load().StatusIconStyle;
        foreach (var (name, panel, listsEveryCase) in surfaces)
        {
            var marks = FindVisualChildren<ShellTypeImage>(panel)
                .Where(item => item.DataContext is InternalAttentionIcon).ToArray();
            InternalAttentionRequire(marks.Length > 0 &&
                    marks.All(item => ReferenceEquals(item.Source,
                        BrandedItemImages.For(item.DataContext, savedStyle))),
                "internal attention icons: the " + name +
                " surface renders internal entities through the shared branded factory");
            var rendered = FindVisualChildren<ShellTypeImage>(panel).ToArray();
            if (listsEveryCase)
                InternalAttentionRequire(rendered.Any(item =>
                        item.DataContext is ActivityRow { ItemKind: "File", Path: "report.pdf" }),
                    "internal attention icons: the " + name +
                    " surface keeps the real file-type presentation for the PDF conflict");
            else
                InternalAttentionRequire(rendered.All(item => item.DataContext is InternalAttentionIcon) &&
                        !rendered.Any(item => ReferenceEquals(item.DataContext, InternalAttentionIcon.FirstSyncReview)),
                    "internal attention icons: the " + name +
                    " surface lists its confirmed problems and nothing else");
        }

        // The real General settings preview: the same glyph objects take the new style with no window
        // recreation, and Discard restores the saved style.
        var styleControl = InternalAttentionGet<ComboBox>(window, "_settingsIconStyle");
        var discard = InternalAttentionGet<Button>(window, "_settingsDiscard");
        var previewStyle = savedStyle == "Symbol" ? "Colour" : "Symbol";
        var persistentMarks = FindVisualChildren<ShellTypeImage>(all)
            .Where(item => item.DataContext is InternalAttentionIcon).ToArray();
        styleControl.SelectedItem = previewStyle;
        PumpUntil(window, () => persistentMarks.All(item => ReferenceEquals(item.Source,
                BrandedItemImages.For(item.DataContext, previewStyle))),
            "The live status icon preview did not restyle the existing attention marks.");
        InternalAttentionRequire(persistentMarks.Length > 0 && persistentMarks.All(item => item.Parent is not null) &&
            Equals(Application.Current.Resources[BrandedItemImages.StyleResource], previewStyle),
            "internal attention icons: the preview restyles the existing attention marks without a new window");
        discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(window, () => persistentMarks.All(item => ReferenceEquals(item.Source,
                BrandedItemImages.For(item.DataContext, savedStyle))),
            "Discarding the settings draft did not restore the saved attention marks.");
        InternalAttentionRequire(Equals(styleControl.SelectedItem, savedStyle) &&
            Equals(Application.Current.Resources[BrandedItemImages.StyleResource], savedStyle),
            "internal attention icons: Discard reverts the shared marks to the saved status icon style");

        // The real row action still navigates to the problem pair.
        InternalAttentionSet(window, "_attentionFingerprint", string.Empty);
        InternalAttentionCall(window, "RefreshFullApp");
        window.UpdateLayout();
        var problemAction = FindVisualChildren<Button>(home).FirstOrDefault(button =>
            FindVisualChildren<ShellTypeImage>(button).Any(item =>
                ReferenceEquals(item.DataContext, InternalAttentionIcon.PairProblem))) ??
            throw new InvalidDataException("The home surface lost its problem row action.");
        problemAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        var pairList = (DataGrid)window.FindName("PairList")!;
        PumpUntil(window, () => (pairList.SelectedItem as PairRow)?.Id == brokenPair.Id,
            "The attention row action no longer opens its own folder pair.");
        InternalAttentionRequire((pairList.SelectedItem as PairRow)?.Id == brokenPair.Id,
            "internal attention icons: the attention row action still opens its own pair");

        currentStyle = savedStyle;
        return new TrayFlyoutWindow(window) { ShowInTaskbar = false, ShowActivated = false };
    }

    private static void AssertInternalAttentionTray(TrayFlyoutWindow tray, string previewStyle)
    {
        tray.Show();
        InternalAttentionGet<TabControl>(tray, "_tabs").SelectedIndex = 1;
        tray.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var attention = InternalAttentionGet<StackPanel>(tray, "_attention");
        InternalAttentionRequire(PumpTrayGlyphs(tray, attention, previewStyle),
            "internal attention icons: the tray flyout renders its attention rows through the same shared factory");
        InternalAttentionRequire(FindVisualChildren<ShellTypeImage>(attention).Any(item =>
                item.DataContext is ActivityRow { ItemKind: "File", Path: "report.pdf" }),
            "internal attention icons: the tray flyout keeps the real file-type presentation for the PDF conflict");
    }

    private static bool PumpTrayGlyphs(TrayFlyoutWindow tray, StackPanel attention, string style)
    {
        InternalAttentionCall(tray, "Refresh");
        tray.UpdateLayout();
        tray.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        var marks = FindVisualChildren<ShellTypeImage>(attention)
            .Where(item => item.DataContext is InternalAttentionIcon).ToArray();
        return marks.Length > 0 && marks.All(item => ReferenceEquals(item.Source,
            BrandedItemImages.For(item.DataContext, style)));
    }

    private static SyncPair InternalAttentionPair(string id, string localRoot, bool approved) =>
        new(id, "Attention " + id, localRoot, 1, 1, "/Shared", "id:shared", ConnectionId: "attention-connection",
            Provider: SyncProviders.Dropbox, InitialReviewApproved: approved);
}
