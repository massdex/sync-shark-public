using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static partial class Program
{
    private const string PairEndpointLocalFixture = @"C:\fixture\local";

    private static readonly SyncEndpointKind[] PairEndpointKinds =
        [SyncEndpointKind.Local, SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive];

    private static readonly SyncMode[] PairEndpointDirections =
        [SyncMode.TwoWay, SyncMode.UploadOnly, SyncMode.DownloadOnly];

    private static string PairEndpointLocalPath(string suffix) => @"C:\fixture\" + suffix;

    private static SyncEndpointRecord PairEndpointRecord(SyncEndpointKind kind, string suffix) =>
        kind == SyncEndpointKind.Local
            ? new SyncEndpointRecord(SyncEndpointKind.Local, PairEndpointLocalPath(suffix))
            : new SyncEndpointRecord(kind, Provider: SyncEndpoints.ProviderLiteral(kind),
                ConnectionId: "connection-" + suffix, FolderId: "id:" + suffix, FolderPath: "/" + suffix);

    private static SyncPair PairEndpointFixturePair(string id, string localRoot,
        string connection = "fixture-connection", string provider = SyncProviders.Dropbox) =>
        new(id, "Endpoint mark " + id, localRoot, 1, 1, "/Shared", "id:shared", ConnectionId: connection,
            Provider: provider, InitialReviewApproved: true);

    private static string PairEndpointExpectedGlyph(SyncMode direction) => direction switch
    {
        SyncMode.UploadOnly => "\u2193",
        SyncMode.DownloadOnly => "\u2191",
        _ => "\u21C5"
    };

    private static int RunPairEndpointMarkChecks(string rawRoot)
    {
        var root = Path.GetFullPath(rawRoot);
        Directory.CreateDirectory(root);
        var localRoot = Directory.CreateDirectory(Path.Combine(root, "local")).FullName;
        var priorStartup = MainWindow.SuppressLoadedStartupForTest;
        App? app = null;
        MainWindow? window = null;
        PilotInstanceLock? instance = null;
        ThemeManager? theme = null;
        try
        {
            AssertPairEndpointMarkValues();
            AssertPairEndpointMarkLayout();

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
            AssertPairEndpointMarkRows(window, localRoot);
            AssertPairEndpointMarkUnknownRendering(window, localRoot);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                result = "PASS",
                selector = "--pair-endpoint-mark-checks",
                source = "apps/SyncShark/PairEndpointMark.cs and the real MainWindow pair rows",
                layout_scales = new[] { "100%", "150%", "200%" }
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

    // The pure presentation value: every ordered endpoint combination, every direction, the explicit
    // document beating the legacy fields, the legacy convention only when a document is absent, and
    // every unnameable explicit record shown as unknown rather than assumed.
    private static void AssertPairEndpointMarkValues()
    {
        var combinations = 0;
        var observedGlyphs = new List<string>();
        foreach (var left in PairEndpointKinds)
        foreach (var right in PairEndpointKinds)
        foreach (var direction in PairEndpointDirections)
        {
            var document = new SyncEndpointDocument(SyncEndpointSchema.SymmetricEndpoints,
                PairEndpointRecord(left, "left"), PairEndpointRecord(right, "right"));
            var marks = PairEndpointMarks.FromDocument(document, direction);
            var label = left + "/" + right + " " + SyncEndpointPresentation.DirectionLabel(direction);
            EndpointUiRequire(marks.Left.IsKnown && marks.Right.IsKnown && marks.Left.Kind == left &&
                marks.Right.Kind == right && marks.Left.Side == SyncPairSide.Left &&
                marks.Right.Side == SyncPairSide.Right,
                "pair endpoint marks: " + label + " keeps its ordered endpoint kinds and logical sides");
            EndpointUiRequire(marks.Left.IconTemplateKey == SyncEndpointPresentation.IconTemplateKey(left) &&
                marks.Right.IconTemplateKey == SyncEndpointPresentation.IconTemplateKey(right),
                "pair endpoint marks: " + label + " resolves each side through the central presentation mapping");
            EndpointUiRequire(marks.ConnectorGlyph == PairEndpointExpectedGlyph(direction) &&
                marks.DirectionLabel == SyncEndpointPresentation.DirectionLabel(direction),
                "pair endpoint marks: " + label + " binds that direction to its own connector");
            EndpointUiRequire(
                marks.AccessibleName.Contains(SyncEndpointPresentation.DirectionLabel(direction), StringComparison.Ordinal) &&
                marks.AccessibleName.Contains(SyncEndpointPresentation.ShortLabel(left), StringComparison.Ordinal) &&
                marks.AccessibleName.Contains(SyncEndpointPresentation.ShortLabel(right), StringComparison.Ordinal),
                "pair endpoint marks: " + label + " describes both endpoints and the direction");
            observedGlyphs.Add(marks.ConnectorGlyph);
            combinations++;
        }
        EndpointUiRequire(combinations == 27 && observedGlyphs.Distinct().Count() == 3,
            "pair endpoint marks: all nine ordered endpoint combinations and all three directions were exercised");

        var legacy = PairEndpointFixturePair("explicit-wins", PairEndpointLocalFixture);
        var explicitPair = legacy with
        {
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = PairEndpointRecord(SyncEndpointKind.GoogleDrive, "left"),
            RightEndpoint = PairEndpointRecord(SyncEndpointKind.Local, "right")
        };
        var explicitMarks = PairEndpointMarks.For(explicitPair);
        EndpointUiRequire(explicitMarks.Left.Kind == SyncEndpointKind.GoogleDrive &&
            explicitMarks.Right.Kind == SyncEndpointKind.Local &&
            explicitMarks.Left.IconTemplateKey == SyncEndpointPresentation.GoogleDriveIconTemplateKey,
            "pair endpoint marks: an explicit document overrides the legacy Dropbox and local fields");

        var legacyMarks = PairEndpointMarks.For(legacy);
        EndpointUiRequire(legacyMarks.Left.Kind == SyncEndpointKind.Local &&
            legacyMarks.Right.Kind == SyncEndpointKind.Dropbox &&
            legacyMarks.Left.IconTemplateKey == SyncEndpointPresentation.LocalIconTemplateKey &&
            legacyMarks.Right.IconTemplateKey == SyncEndpointPresentation.DropboxIconTemplateKey,
            "pair endpoint marks: a genuinely absent document keeps the established left local, right cloud legacy convention");
        EndpointUiRequire(PairEndpointMarks.For(legacy with { Provider = SyncProviders.GoogleDrive })
                .Right.Kind == SyncEndpointKind.GoogleDrive,
            "pair endpoint marks: the legacy right endpoint follows the pair's own stored provider binding");
        EndpointUiRequire(PairEndpointMarks.For(legacy with { Provider = "unregistered-provider" })
                .Right.State == PairEndpointMarkState.Unknown,
            "pair endpoint marks: an unregistered legacy provider is unknown rather than assumed Dropbox");
        EndpointUiRequire(PairEndpointMarks.For(legacy with { ConnectionId = string.Empty }).Right.IsKnown,
            "pair endpoint marks: a legacy pair without an account id keeps its provider mark, not an invented unknown");

        var invalid = new (string Description, SyncEndpointDocument Document)[]
        {
            ("a missing explicit side", new SyncEndpointDocument(SyncEndpointSchema.SymmetricEndpoints, null,
                PairEndpointRecord(SyncEndpointKind.Dropbox, "right"))),
            ("an unsupported endpoint schema version", new SyncEndpointDocument(3,
                PairEndpointRecord(SyncEndpointKind.Dropbox, "left"),
                PairEndpointRecord(SyncEndpointKind.Dropbox, "right"))),
            ("a local record carrying cloud identity", new SyncEndpointDocument(SyncEndpointSchema.SymmetricEndpoints,
                new SyncEndpointRecord(SyncEndpointKind.Local, PairEndpointLocalPath("mixed"),
                    Provider: SyncProviders.Dropbox, ConnectionId: "connection-mixed", FolderId: "id:mixed"), null)),
            ("a cloud record carrying local identity", new SyncEndpointDocument(SyncEndpointSchema.SymmetricEndpoints,
                null, new SyncEndpointRecord(SyncEndpointKind.Dropbox, PairEndpointLocalPath("mixed"),
                    Provider: SyncProviders.Dropbox, ConnectionId: "connection-mixed", FolderId: "id:mixed"))),
            ("a persisted provider disagreeing with its kind", new SyncEndpointDocument(
                SyncEndpointSchema.SymmetricEndpoints, null, new SyncEndpointRecord(SyncEndpointKind.Dropbox,
                    Provider: SyncProviders.GoogleDrive, ConnectionId: "connection-mixed", FolderId: "id:mixed"))),
            ("an unregistered persisted endpoint kind", new SyncEndpointDocument(SyncEndpointSchema.SymmetricEndpoints,
                new SyncEndpointRecord((SyncEndpointKind)77, ConnectionId: "connection-mixed", FolderId: "id:mixed"),
                null)),
            ("a cloud record without an account", new SyncEndpointDocument(SyncEndpointSchema.SymmetricEndpoints,
                null, new SyncEndpointRecord(SyncEndpointKind.Dropbox, Provider: SyncProviders.Dropbox,
                    FolderId: "id:mixed")))
        };
        foreach (var (description, document) in invalid)
        {
            var marks = PairEndpointMarks.FromDocument(document, SyncMode.TwoWay);
            var unknown = new[] { marks.Left, marks.Right }
                .Where(side => side.State == PairEndpointMarkState.Unknown).ToArray();
            EndpointUiRequire(unknown.Length > 0 && unknown.All(side => side.Kind is null &&
                    side.IconTemplateKey == PairEndpointMarks.UnknownIconTemplateKey &&
                    side.IconTemplateKey != SyncEndpointPresentation.DropboxIconTemplateKey &&
                    side.IconTemplateKey != SyncEndpointPresentation.GoogleDriveIconTemplateKey &&
                    side.AccessibleName.Contains("unknown", StringComparison.Ordinal)),
                "pair endpoint marks: " + description + " is an unknown endpoint, never assumed Dropbox or local");
        }

        var sharedAccountRecord = new SyncEndpointRecord(SyncEndpointKind.Dropbox,
            Provider: SyncProviders.Dropbox, ConnectionId: "connection-a", FolderId: "id:a");
        var sameProvider = new PairEndpointMarks(
            PairEndpointMarks.Side(sharedAccountRecord, SyncPairSide.Left),
            PairEndpointMarks.Side(PairEndpointRecord(SyncEndpointKind.Dropbox, "b"), SyncPairSide.Right),
            SyncMode.TwoWay);
        EndpointUiRequire(sameProvider.Left.IconTemplateKey == sameProvider.Right.IconTemplateKey &&
            sameProvider.Left.IsKnown,
            "pair endpoint marks: two accounts on one provider share the canonical mark instead of an invented difference");
    }

    // The reusable mark itself, measured and arranged at 100, 150 and 200 per cent equivalent layout
    // scale: nonzero, intact, and never clipped by its own host or by the scaled layout.
    private static void AssertPairEndpointMarkLayout()
    {
        var document = new SyncEndpointDocument(SyncEndpointSchema.SymmetricEndpoints,
            PairEndpointRecord(SyncEndpointKind.Dropbox, "left"),
            PairEndpointRecord(SyncEndpointKind.GoogleDrive, "right"));
        foreach (var scale in new[] { 1.0, 1.5, 2.0 })
        {
            var described = (scale * 100).ToString("F0") + " per cent";
            var mark = new PairEndpointMark
            {
                Presentation = PairEndpointMarks.FromDocument(document, SyncMode.DownloadOnly)
            };
            var scaler = new Grid { LayoutTransform = new ScaleTransform(scale, scale) };
            scaler.Children.Add(mark);
            var host = new Border { Width = 400, Height = 400, Child = scaler };
            host.Measure(new Size(400, 400));
            var scaled = scaler.DesiredSize;
            EndpointUiRequire(scaled.Width > 0 && scaled.Height > 0 &&
                Math.Abs(scaled.Width - 28 * scale) < 0.75 && Math.Abs(scaled.Height - 52 * scale) < 0.75 &&
                scaled.Width <= 400 && scaled.Height <= 400,
                "pair endpoint mark layout: the mark measures a nonzero 28x52 host at " + described + " and fits it");
            host.Arrange(new Rect(0, 0, 400, 400));
            host.UpdateLayout();
            EndpointUiRequire(mark.RenderSize.Width > 0 && mark.RenderSize.Height > 0,
                "pair endpoint mark layout: the arranged mark is nonzero at " + described);

            var sides = FindVisualChildren<ContentControl>(mark)
                .Where(item => !ReferenceEquals(item, mark) &&
                               AutomationProperties.GetName(item).Contains(" endpoint: ", StringComparison.Ordinal))
                .ToArray();
            EndpointUiRequire(sides.Length == 2 &&
                sides.Any(item => AutomationProperties.GetName(item).StartsWith("Left endpoint", StringComparison.Ordinal)) &&
                sides.Any(item => AutomationProperties.GetName(item).StartsWith("Right endpoint", StringComparison.Ordinal)),
                "pair endpoint mark layout: both ordered endpoint marks render at " + described +
                " with their own accessible side names");
            EndpointUiRequire(AutomationProperties.GetName(mark).Contains("direction", StringComparison.Ordinal) &&
                AutomationProperties.GetName(mark).Contains(SyncEndpointPresentation.DirectionLabel(
                    SyncMode.DownloadOnly), StringComparison.Ordinal),
                "pair endpoint mark layout: the mark itself carries the accessible directional description at " + described);

            var bounds = new Rect(0, 0, mark.RenderSize.Width, mark.RenderSize.Height);
            foreach (var side in sides)
            {
                var sideBounds = side.TransformToAncestor(mark).TransformBounds(new Rect(side.RenderSize));
                EndpointUiRequire(sideBounds.Width > 0 && sideBounds.Height > 0 &&
                    bounds.Contains(sideBounds.TopLeft) && bounds.Contains(sideBounds.BottomRight),
                    "pair endpoint mark layout: an endpoint mark is intact and unclipped inside the pair mark at " + described);
            }
        }
    }

    private static void AssertPairEndpointMarkRows(MainWindow window, string localRoot)
    {
        // The pair list only realises its rows on the visible Pairs page, exactly as the existing
        // pairs contract navigates before it inspects a row.
        if (window.FindName("Navigation") is ListBox navigation)
            navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>()
                .Single(item => Equals(item.Tag, "Pairs"));
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        window.UpdateLayout();

        var dropboxPair = PairEndpointFixturePair("pair-mark-dropbox", localRoot, connection: "account-one");
        var googlePair = PairEndpointFixturePair("pair-mark-google", localRoot, connection: "account-two",
            provider: SyncProviders.GoogleDrive);
        window.LoadPairSetForTest(new SyncPairSet([dropboxPair, googlePair], dropboxPair.Id));
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        PumpUntil(window, () => window.PairRows.Count == 2, "The two endpoint-mark fixture pairs did not load.");

        foreach (var pair in new[] { dropboxPair, googlePair })
        {
            var row = window.PairRows.Single(item => item.Id == pair.Id);
            PumpUntil(window, () => PairEndpointToggleOrNull(window, row) is not null,
                "The leading pair mark control was not rendered for " + pair.Id + ".");
            var toggle = PairEndpointToggleOrNull(window, row)!;
            EndpointUiRequire(AutomationProperties.GetName(toggle)
                    .Contains("Expand or collapse", StringComparison.Ordinal) && toggle.Focusable,
                "pair endpoint mark rows: the leading control keeps its expand or collapse accessible name and keyboard focus");
            var mark = FindVisualChildren<PairEndpointMark>(toggle).SingleOrDefault() ??
                       throw new InvalidDataException("The leading pair control no longer hosts a pair endpoint mark.");
            var marks = row.EndpointMarks;
            EndpointUiRequire(marks.Left.Kind == SyncEndpointKind.Local && marks.Right.Kind is { } rightKind &&
                rightKind.ToString() == PairEndpointProviderKindOf(pair),
                "pair endpoint mark rows: the real row shows both of the pair's own endpoints");
            var leftMark = FindVisualChildren<ContentControl>(mark).First(item =>
                AutomationProperties.GetName(item).StartsWith("Left endpoint", StringComparison.Ordinal));
            var rightMark = FindVisualChildren<ContentControl>(mark).First(item =>
                AutomationProperties.GetName(item).StartsWith("Right endpoint", StringComparison.Ordinal));
            EndpointUiRequire(ReferenceEquals(leftMark.ContentTemplate, window.FindResource("FolderIconTemplate")) &&
                ReferenceEquals(rightMark.ContentTemplate,
                    window.FindResource(SyncEndpointPresentation.IconTemplateKey(marks.Right.Kind!.Value))),
                "pair endpoint mark rows: the row renders the real local mark above and the pair's provider mark below");
            EndpointUiRequire(AutomationProperties.GetHelpText(toggle)
                    .Contains(marks.DirectionLabel, StringComparison.Ordinal) &&
                AutomationProperties.GetHelpText(toggle)
                    .Contains(SyncEndpointPresentation.ShortLabel(marks.Right.Kind!.Value), StringComparison.Ordinal),
                "pair endpoint mark rows: the leading control describes its endpoints and direction");
            EndpointUiRequire(FindVisualChildren<ContentControl>(window).Any(item =>
                    string.Equals(ProviderMark.GetProvider(item), pair.Provider, StringComparison.Ordinal)),
                "pair endpoint mark rows: the cloud column keeps its own provider mark control");
        }

        var dropboxRow = window.PairRows.Single(item => item.Id == dropboxPair.Id);
        var dropboxToggle = PairEndpointToggleOrNull(window, dropboxRow)!;
        EndpointUiRequire(AutomationProperties.GetItemStatus(dropboxToggle) is "Expanded" or "Collapsed",
            "pair endpoint mark rows: the leading control keeps its accessible expanded state");
        var wasExpanded = dropboxRow.IsExpanded;
        dropboxToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, dropboxToggle));
        PumpUntil(window, () => dropboxRow.IsExpanded != wasExpanded,
            "The leading pair mark no longer toggles the row's expand or collapse state.");
        dropboxToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, dropboxToggle));
        PumpUntil(window, () => dropboxRow.IsExpanded == wasExpanded,
            "The leading pair mark no longer restores the row's expand or collapse state.");

        // A reused row refreshes its endpoint presentation in place: the shared row instance survives
        // and its visual follows the pair's own saved endpoint document.
        var retainedRow = dropboxRow;
        var converted = dropboxPair with
        {
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = PairEndpointRecord(SyncEndpointKind.GoogleDrive, "left"),
            RightEndpoint = PairEndpointRecord(SyncEndpointKind.Local, "right")
        };
        window.LoadPairSetForTest(new SyncPairSet([converted, googlePair], converted.Id));
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        var refreshed = window.PairRows.Single(item => item.Id == converted.Id);
        EndpointUiRequire(ReferenceEquals(refreshed, retainedRow) &&
            refreshed.EndpointMarks.Left.Kind == SyncEndpointKind.GoogleDrive &&
            refreshed.EndpointMarks.Right.Kind == SyncEndpointKind.Local,
            "pair endpoint mark rows: a reused row refreshes its endpoint presentation without being rebuilt");
        PumpUntil(window, () => PairEndpointToggleOrNull(window, refreshed) is not null,
            "The reused row lost its leading pair mark control.");
        var refreshedMark = FindVisualChildren<PairEndpointMark>(PairEndpointToggleOrNull(window, refreshed)!).Single();
        EndpointUiRequire(ReferenceEquals(FindVisualChildren<ContentControl>(refreshedMark).First(item =>
                    AutomationProperties.GetName(item).StartsWith("Left endpoint", StringComparison.Ordinal)).ContentTemplate,
                window.FindResource("GoogleDriveIconTemplate")),
            "pair endpoint mark rows: the refreshed reused row renders the newly saved left endpoint mark");

        // Same provider, different accounts: the existing account text separates the rows and neither
        // row invents a logo difference.
        var accountOne = new PairRow("account-one-row", "Account one", localRoot, "/Shared", "one@example.test",
            true, 0, false, false, "Not running", direction: SyncMode.TwoWay, provider: SyncProviders.Dropbox);
        var accountTwo = new PairRow("account-two-row", "Account two", localRoot, "/Shared", "two@example.test",
            true, 0, false, false, "Not running", direction: SyncMode.TwoWay, provider: SyncProviders.Dropbox);
        EndpointUiRequire(accountOne.Connection != accountTwo.Connection &&
            accountOne.EndpointMarks.Right.IconTemplateKey == accountTwo.EndpointMarks.Right.IconTemplateKey,
            "pair endpoint mark rows: two accounts on one provider stay distinguishable by their existing account text");
    }

    private static string PairEndpointProviderKindOf(SyncPair pair) =>
        SyncEndpoints.TryMapProvider(pair.Provider, out var kind) ? kind.ToString() : string.Empty;

    private static Button? PairEndpointToggleOrNull(MainWindow window, PairRow row) =>
        FindVisualChildren<Button>(window).FirstOrDefault(item => ReferenceEquals(item.DataContext, row) &&
            AutomationProperties.GetName(item).Contains("Expand or collapse", StringComparison.Ordinal));

    // The DRAWN unknown control, not the helper constant: the real side control a pair row shows for an
    // explicit endpoint this build cannot name, and for a side with no presentation at all, is the
    // existing outline attention command glyph in the product's muted ink. It is asserted against the
    // artwork the local folder mark and the provider marks actually render, so the distinction is a
    // rendering fact rather than a resource key comparison.
    private static void AssertPairEndpointMarkUnknownRendering(MainWindow window, string localRoot)
    {
        if (window.FindName("Navigation") is ListBox navigation)
            navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>()
                .Single(item => Equals(item.Tag, "Pairs"));
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        window.UpdateLayout();

        var knownPair = PairEndpointFixturePair("endpoint-render-known", localRoot, connection: "render-known");
        var unknownPair = PairEndpointFixturePair("endpoint-render-unknown", localRoot, connection: "render-unknown") with
        {
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = null,
            RightEndpoint = PairEndpointRecord(SyncEndpointKind.Dropbox, "render-right")
        };
        var absentPair = PairEndpointFixturePair("endpoint-render-absent", localRoot, connection: "render-absent") with
        {
            EndpointSchemaVersion = SyncEndpointSchema.SymmetricEndpoints,
            LeftEndpoint = null,
            RightEndpoint = null
        };
        window.LoadPairSetForTest(new SyncPairSet([knownPair, unknownPair, absentPair], knownPair.Id));
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        PumpUntil(window, () => window.PairRows.Count == 3, "The endpoint rendering fixture pairs did not load.");

        var knownMarks = PairEndpointRenderedSideControls(window, knownPair);
        var unknownMarks = PairEndpointRenderedSideControls(window, unknownPair);
        var absentMarks = PairEndpointRenderedSideControls(window, absentPair);

        // The value keeps its honest unknown identity: a null kind, the explicit unknown accessible text
        // and the same value the rendered mark is bound to.
        var unknownRow = window.PairRows.Single(item => item.Id == unknownPair.Id);
        EndpointUiRequire(unknownRow.EndpointMarks.Left.Kind is null &&
            unknownRow.EndpointMarks.Left.State == PairEndpointMarkState.Unknown &&
            unknownRow.EndpointMarks.Left.IconTemplateKey == PairEndpointMarks.UnknownIconTemplateKey &&
            unknownRow.EndpointMarks.Left.AccessibleName == "Left endpoint: unknown or not recorded" &&
            unknownRow.EndpointMarks.Left.Side == SyncPairSide.Left && unknownRow.EndpointMarks.Right.IsKnown,
            "pair endpoint mark rendering: an explicit unnameable side keeps a null kind and its explicit unknown name");

        EndpointUiRequire(unknownMarks.Left.Content is CommandGlyph && unknownMarks.Left.ContentTemplate is null &&
            absentMarks.Left.Content is CommandGlyph { IconName: "attention" } &&
            absentMarks.Right.Content is CommandGlyph { IconName: "attention" },
            "pair endpoint mark rendering: an unknown side and an absent side both draw a real attention command glyph");

        var muted = (SolidColorBrush)window.FindResource("MutedInk");
        var attentionGlyph = (CommandGlyph)unknownMarks.Left.Content!;
        var attentionStroke = FindVisualChildren<System.Windows.Shapes.Path>(attentionGlyph).Single().Stroke;
        var attentionData = RenderedPathData(attentionGlyph);
        var folderData = RenderedTemplatePathData(window, "FolderIconTemplate");
        var dropboxData = RenderedTemplatePathData(window, "DropboxIconTemplate");
        var googleData = RenderedTemplatePathData(window, "GoogleDriveIconTemplate");
        EndpointUiRequire(attentionGlyph.IconName == "attention" &&
            attentionStroke is SolidColorBrush stroke && stroke.Color == muted.Color &&
            attentionData.Length == 1 && attentionData[0].Length > 0 &&
            !folderData.Contains(attentionData[0]) && !dropboxData.Contains(attentionData[0]) &&
            !googleData.Contains(attentionData[0]),
            "pair endpoint mark rendering: the drawn unknown mark is the muted attention outline and never the folder or provider artwork");

        // The known sides keep their own exact templates and their own filled brand artwork, so the
        // rendered unknown control is distinct from both the folder mark and the provider marks.
        EndpointUiRequire(ReferenceEquals(knownMarks.Left.ContentTemplate, window.FindResource("FolderIconTemplate")) &&
            knownMarks.Left.Content is null &&
            ReferenceEquals(knownMarks.Right.ContentTemplate, window.FindResource("DropboxIconTemplate")) &&
            knownMarks.Right.Content is null &&
            ReferenceEquals(unknownMarks.Right.ContentTemplate, window.FindResource("DropboxIconTemplate")) &&
            unknownMarks.Right.Content is null,
            "pair endpoint mark rendering: the known sides keep their unchanged folder and provider templates beside the drawn unknown mark");

        // A mark with no presentation at all takes the same path: both sides draw the attention outline
        // and publish the explicit unknown name instead of any provider artwork.
        var bare = new PairEndpointMark();
        var bareHost = new Border { Width = 200, Height = 200, Child = bare };
        bareHost.Measure(new Size(200, 200));
        bareHost.Arrange(new Rect(0, 0, 200, 200));
        bareHost.UpdateLayout();
        var bareSides = FindVisualChildren<ContentControl>(bare).Where(item =>
            string.Equals(AutomationProperties.GetName(item), "endpoint unknown or not recorded", StringComparison.Ordinal)).ToArray();
        EndpointUiRequire(bareSides.Length == 2 && bareSides.All(item =>
                item.Content is CommandGlyph { IconName: "attention" } && item.ContentTemplate is null),
            "pair endpoint mark rendering: a mark with no presentation draws the same attention outline for both sides");
    }

    private static (ContentControl Left, ContentControl Right) PairEndpointRenderedSideControls(MainWindow window, SyncPair pair)
    {
        var row = window.PairRows.Single(item => item.Id == pair.Id);
        PumpUntil(window, () => PairEndpointToggleOrNull(window, row) is not null,
            "The leading pair mark control was not rendered for " + pair.Id + ".");
        var mark = FindVisualChildren<PairEndpointMark>(PairEndpointToggleOrNull(window, row)!).SingleOrDefault() ??
                   throw new InvalidDataException("The pair row no longer hosts a pair endpoint mark.");
        ContentControl Side(string side) => FindVisualChildren<ContentControl>(mark).First(item =>
            AutomationProperties.GetName(item).StartsWith(side + " endpoint", StringComparison.Ordinal));
        return (Side("Left"), Side("Right"));
    }

    private static string[] RenderedPathData(DependencyObject root) =>
        FindVisualChildren<System.Windows.Shapes.Path>(root)
            .Select(item => item.Data?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
            .Where(data => data.Length > 0).ToArray();

    private static string[] RenderedTemplatePathData(MainWindow window, string templateKey)
    {
        if (((DataTemplate)window.FindResource(templateKey)).LoadContent() is not FrameworkElement artwork)
            return Array.Empty<string>();
        var host = new Border { Width = 200, Height = 200, Child = artwork };
        host.Measure(new Size(200, 200));
        host.Arrange(new Rect(0, 0, 200, 200));
        host.UpdateLayout();
        return RenderedPathData(artwork);
    }
}
