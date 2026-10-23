using Massdex.SyncShark;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Rectangle = System.Windows.Shapes.Rectangle;
using ShapePath = System.Windows.Shapes.Path;

namespace SyncShark.UiFixture;

// U1 checks for job ds--u1-001: the frozen shared presentation contract.
//
// These are structural, layout, preference-store and real-control-tree checks over the shared
// contract. They are not installed-product, provider, click or user-interaction evidence, and
// they say nothing about the pages that have not bound the contract yet. The file was written
// under a source-only hold and has not been compiled or executed.
internal static class SharedPresentationContractChecks
{
    private const string Selector = "--shared-presentation-contract-checks";

    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var app = new App { SuppressProductStartup = true, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var theme = new ThemeManager(new UiPreferencesStore(appLock.StateAuthority));
        theme.Initialize();
        try
        {
            CheckStyleVocabulary();
            CheckPalette();
            CheckEntityStatusMapping();
            CheckEveryEntityStatusStyleCombination();
            CheckUnknownValueFallback();
            CheckDisplayScaling();
            CheckPreferenceRoundTrip(appLock.StateAuthority);
            CheckFeedTimestampColumn();
            CheckStatusIconStyleSelector();
            CheckDetailsHeaderFactory();
            CheckRealWindowConsumers(appLock.StateAuthority, theme);
            Console.WriteLine("PASS " + Selector +
                "; U1 shared presentation contract, no provider call, no install and no live effect");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    // --- Pure contract checks ------------------------------------------------------------------

    private static void CheckStyleVocabulary()
    {
        Require(SharedPresentation.StatusIconStyles.SequenceEqual(new[] { "Colour", "Symbol", "Combined" }),
            "The three selectable status-icon styles are Colour, Symbol and Combined in that order");
        Require(SharedPresentation.DefaultStatusIconStyle == "Combined",
            "The frozen default status-icon style is Combined");
        Require(UiPreferencesStore.IconSets.SequenceEqual(SharedPresentation.StatusIconStyles) &&
                SharedPresentation.ColourStatusIconStyle == UiPreferencesStore.ColourIconSet &&
                SharedPresentation.SymbolStatusIconStyle == UiPreferencesStore.SymbolIconSet &&
                SharedPresentation.CombinedStatusIconStyle == UiPreferencesStore.CombinedIconSet,
            "The persisted preference and the shared presentation use one canonical style vocabulary");
        Require(SharedPresentation.IsStatusIconStyle("Colour") && SharedPresentation.IsStatusIconStyle("Symbol") &&
                SharedPresentation.IsStatusIconStyle("Combined"),
            "All three canonical styles are accepted");
        Require(!SharedPresentation.IsStatusIconStyle("colour") && !SharedPresentation.IsStatusIconStyle("Colour + symbol") &&
                !SharedPresentation.IsStatusIconStyle("Fancy") && !SharedPresentation.IsStatusIconStyle(null),
            "A mis-cased, renamed or absent style is not accepted as a supported value");
        var tooltips = SharedPresentation.StatusIconStyles.Select(style => SharedPresentation.StatusIconStyleTooltip(style)).ToArray();
        Require(tooltips.All(tip => !string.IsNullOrWhiteSpace(tip)) && tooltips.Distinct(StringComparer.Ordinal).Count() == 3,
            "Each style segment has its own non-empty explanatory tooltip");
        Require(SharedPresentation.NormalizeStatusIconStyle("Fancy") == SharedPresentation.DefaultStatusIconStyle &&
                SharedPresentation.NormalizeStatusIconStyle(null) == SharedPresentation.DefaultStatusIconStyle &&
                SharedPresentation.NormalizeStatusIconStyle("Symbol") == SharedPresentation.SymbolStatusIconStyle,
            "Style normalisation keeps a supported value and resolves anything else to the frozen default");
    }

    private static void CheckPalette()
    {
        Require(SharedPresentation.AttentionColourHex == "#E74949" && SharedPresentation.DestructiveColourHex == "#E74949",
            "Attention and destructive share the exact tray red #E74949");
        Require(SharedPresentation.AttentionColour == Color.FromRgb(231, 73, 73),
            "The attention colour is the exact RGB 231/73/73");
        Require(SharedPresentation.StatusColour(SharedPresentation.PresentationStatus.Attention) == SharedPresentation.AttentionColour &&
                SharedPresentation.StatusColour(SharedPresentation.PresentationStatus.Paused) == SharedPresentation.PausedColour &&
                SharedPresentation.StatusColour(SharedPresentation.PresentationStatus.Running) == SharedPresentation.RunningColour &&
                SharedPresentation.StatusColour(SharedPresentation.PresentationStatus.Finished) == SharedPresentation.FinishedColour &&
                SharedPresentation.StatusColour(SharedPresentation.PresentationStatus.Unknown) == SharedPresentation.NeutralColour,
            "One palette supplies a distinct frozen colour for every status and a neutral colour for unknown");
        var distinct = SharedPresentation.StatusIconStyles.Length;
        var colours = new[]
        {
            SharedPresentation.AttentionColour, SharedPresentation.PausedColour, SharedPresentation.RunningColour,
            SharedPresentation.FinishedColour, SharedPresentation.NeutralColour
        };
        Require(colours.Distinct().Count() == colours.Length && distinct == 3,
            "No two status colours collide, so text never has to carry the whole meaning alone");
        foreach (var status in Enum.GetValues<SharedPresentation.PresentationStatus>())
        {
            var brush = SharedPresentation.StatusBrush(status);
            Require(brush.IsFrozen && brush.Color == SharedPresentation.StatusColour(status),
                "The shared frozen brush for " + status + " carries its frozen colour");
        }
    }

    private static void CheckEntityStatusMapping()
    {
        Require(SharedPresentation.StatusFor("Running") == SharedPresentation.PresentationStatus.Running &&
                SharedPresentation.StatusFor("finished") == SharedPresentation.PresentationStatus.Finished &&
                SharedPresentation.StatusFor("NEEDS ATTENTION") == SharedPresentation.PresentationStatus.Attention &&
                SharedPresentation.StatusFor("Paused") == SharedPresentation.PresentationStatus.Paused &&
                SharedPresentation.StatusFor(SharedPresentation.PresentationStatus.Attention) == SharedPresentation.PresentationStatus.Attention,
            "The status vocabulary maps the real record words case-insensitively, and a status value passes through");
        Require(SharedPresentation.EntityFor("Folder Pair") == SharedPresentation.PresentationEntity.FolderPair &&
                SharedPresentation.EntityFor("run") == SharedPresentation.PresentationEntity.SyncRun &&
                SharedPresentation.EntityFor("connection") == SharedPresentation.PresentationEntity.Account &&
                SharedPresentation.EntityFor("storage policy") == SharedPresentation.PresentationEntity.StoragePolicy &&
                SharedPresentation.EntityFor("App Setting") == SharedPresentation.PresentationEntity.AppSetting &&
                SharedPresentation.EntityFor("diagnostics") == SharedPresentation.PresentationEntity.Diagnostics &&
                SharedPresentation.EntityFor("recovery") == SharedPresentation.PresentationEntity.Recovery &&
                SharedPresentation.EntityFor("file") == SharedPresentation.PresentationEntity.File &&
                SharedPresentation.EntityFor("dropbox") == SharedPresentation.PresentationEntity.Provider,
            "The entity vocabulary maps every internal item kind and keeps files and providers distinct");
        Require(SharedPresentation.UsesBrandMark(SharedPresentation.PresentationEntity.FolderPair) &&
                SharedPresentation.UsesBrandMark(SharedPresentation.PresentationEntity.SyncRun) &&
                SharedPresentation.UsesBrandMark(SharedPresentation.PresentationEntity.Account) &&
                SharedPresentation.UsesBrandMark(SharedPresentation.PresentationEntity.StoragePolicy) &&
                SharedPresentation.UsesBrandMark(SharedPresentation.PresentationEntity.AppSetting) &&
                SharedPresentation.UsesBrandMark(SharedPresentation.PresentationEntity.Diagnostics) &&
                SharedPresentation.UsesBrandMark(SharedPresentation.PresentationEntity.Recovery),
            "Pair, run, connection, settings, diagnostic and recovery items use the branded entity treatment");
        Require(!SharedPresentation.UsesBrandMark(SharedPresentation.PresentationEntity.File) &&
                !SharedPresentation.UsesBrandMark(SharedPresentation.PresentationEntity.Provider),
            "Real files and cloud providers do not take the branded mark");
        Require(SharedPresentation.EntityGlyphName(SharedPresentation.PresentationEntity.FolderPair) == "folder" &&
                SharedPresentation.EntityGlyphName(SharedPresentation.PresentationEntity.SyncRun) == "sync" &&
                SharedPresentation.EntityGlyphName(SharedPresentation.PresentationEntity.Account) == "user" &&
                SharedPresentation.EntityGlyphName(SharedPresentation.PresentationEntity.StoragePolicy) == "storage" &&
                SharedPresentation.EntityGlyphName(SharedPresentation.PresentationEntity.AppSetting) == "settings" &&
                SharedPresentation.EntityGlyphName(SharedPresentation.PresentationEntity.Diagnostics) == "shield" &&
                SharedPresentation.EntityGlyphName(SharedPresentation.PresentationEntity.Recovery) == "history",
            "Each internal entity kind has its own existing role symbol");
        Require(SharedPresentation.StatusGlyphName(SharedPresentation.PresentationStatus.Finished) == "check" &&
                SharedPresentation.StatusGlyphName(SharedPresentation.PresentationStatus.Running) == "sync" &&
                SharedPresentation.StatusGlyphName(SharedPresentation.PresentationStatus.Paused) == "pause" &&
                SharedPresentation.StatusGlyphName(SharedPresentation.PresentationStatus.Attention) == "attention" &&
                SharedPresentation.StatusGlyphName(SharedPresentation.PresentationStatus.Unknown) == "info",
            "Each status has its own distinct state symbol, with the informational symbol reserved for unknown");
    }

    private static void CheckEveryEntityStatusStyleCombination()
    {
        var entities = Enum.GetValues<SharedPresentation.PresentationEntity>();
        var statuses = Enum.GetValues<SharedPresentation.PresentationStatus>();
        var styles = SharedPresentation.StatusIconStyles;
        var combinations = 0;
        var branded = 0;
        var unbranded = 0;
        foreach (var entity in entities)
        foreach (var status in statuses)
        foreach (var style in styles)
        {
            combinations++;
            var parts = SharedPresentation.EntityIconParts(entity, status, style);
            var icon = SharedPresentation.EntityIcon(entity, status, style);
            if (!SharedPresentation.UsesBrandMark(entity))
            {
                unbranded++;
                Assert(icon is null && parts.Count == 0,
                    "No branded mark is offered for " + entity + " in the " + status + "/" + style + " combination");
                continue;
            }
            branded++;
            var tinted = SharedPresentation.MarkIsTinted(status, style);
            var symbols = SharedPresentation.StateSymbolShown(style);
            var label = entity + "/" + status + "/" + style;
            Assert(icon is Grid, "The branded mark for " + label + " is one composed element");
            var grid = (Grid)icon!;
            Assert(parts.Count == (symbols ? 3 : 2), "The " + label + " description has the expected part count");
            Assert(parts[0] == (tinted ? "mark:tinted" : "mark:normal"),
                "The " + label + " mark tint decision is frozen");
            Assert(parts[1] == "role:" + SharedPresentation.EntityGlyphName(entity),
                "The " + label + " role symbol is the entity kind symbol");
            Assert(grid.Children.OfType<Rectangle>().Any() == tinted && grid.Children.OfType<Image>().Any() == !tinted,
                "The " + label + " visual is a tinted shark exactly when the frozen rule tints it");
            Assert(grid.Children.Count == (symbols ? 3 : 2) && grid.Children.OfType<CommandGlyph>().Count() == (symbols ? 2 : 1),
                "The " + label + " visual shows the state symbol exactly when the style includes symbols");
            if (symbols)
                Assert(parts[2] == "state:" + SharedPresentation.StatusGlyphName(status),
                    "The " + label + " state symbol is the status symbol");
        }
        Require(combinations == entities.Length * statuses.Length * styles.Length &&
                unbranded == 2 * statuses.Length * styles.Length &&
                branded == combinations - unbranded,
            "Every entity, status and style combination was checked (" + combinations + " combinations, " +
            branded + " branded and " + unbranded + " deliberately unbranded)");
    }

    private static void CheckUnknownValueFallback()
    {
        Require(SharedPresentation.StatusFor(null) == SharedPresentation.PresentationStatus.Unknown &&
                SharedPresentation.StatusFor("") == SharedPresentation.PresentationStatus.Unknown &&
                SharedPresentation.StatusFor("   ") == SharedPresentation.PresentationStatus.Unknown &&
                SharedPresentation.StatusFor("wat") == SharedPresentation.PresentationStatus.Unknown,
            "An absent, blank or unrecognised status resolves to the safe unknown status");
        Require(SharedPresentation.EntityFor(null) == SharedPresentation.PresentationEntity.Unknown &&
                SharedPresentation.EntityFor("") == SharedPresentation.PresentationEntity.Unknown &&
                SharedPresentation.EntityFor("wat") == SharedPresentation.PresentationEntity.Unknown,
            "An absent, blank or unrecognised entity resolves to the safe unknown entity");
        Require(SharedPresentation.UsesBrandMark(SharedPresentation.PresentationEntity.Unknown) &&
                SharedPresentation.StatusColour(SharedPresentation.PresentationStatus.Unknown) == SharedPresentation.NeutralColour &&
                SharedPresentation.StatusGlyphName(SharedPresentation.PresentationStatus.Unknown) == "info",
            "An unknown status resolves to the neutral colour and the informational symbol");
        foreach (var style in SharedPresentation.StatusIconStyles)
        {
            Require(!SharedPresentation.MarkIsTinted(SharedPresentation.PresentationStatus.Unknown, style),
                "An unknown status is never tinted in the " + style + " style");
            var icon = SharedPresentation.EntityIcon(SharedPresentation.PresentationEntity.Unknown,
                SharedPresentation.PresentationStatus.Unknown, style);
            Require(icon is Grid grid && grid.Children.OfType<Image>().Any() && !grid.Children.OfType<Rectangle>().Any(),
                "The unknown fallback shows the uncoloured shark in the " + style + " style");
        }
        Require(SharedPresentation.EntityIcon(SharedPresentation.PresentationEntity.File,
                    SharedPresentation.PresentationStatus.Attention, SharedPresentation.CombinedStatusIconStyle) is null &&
                SharedPresentation.EntityIcon(SharedPresentation.PresentationEntity.Provider,
                    SharedPresentation.PresentationStatus.Attention, SharedPresentation.CombinedStatusIconStyle) is null,
            "A real file and a cloud provider are never given the branded mark");
        Require(SharedPresentation.ProviderTemplateKey(SyncProviders.Dropbox) == "DropboxIconTemplate" &&
                SharedPresentation.ProviderTemplateKey(SyncProviders.GoogleDrive) == "GoogleDriveIconTemplate" &&
                SharedPresentation.ProviderTemplateKey("unknown") is null &&
                SharedPresentation.ProviderTemplateKey(null) is null,
            "A known provider resolves to its official mark and an unknown provider claims none");
    }

    private static void CheckDisplayScaling()
    {
        Require(SharedPresentation.SupportedDpis.SequenceEqual(new uint[] { 96, 144, 192 }),
            "The display-scaling contract covers 100, 150 and 200 percent");
        Require(SharedPresentation.DpiPercent(96) == 100 && SharedPresentation.DpiPercent(144) == 150 &&
                SharedPresentation.DpiPercent(192) == 200,
            "The supported device resolutions map to 100, 150 and 200 percent");
        var expected = new[] { 18, 27, 36 };
        var logical = SharedPresentation.StatusIconLogicalSize;
        Require(SharedPresentation.SupportedDpis.Select(dpi => SharedPresentation.PhysicalPixels(logical, dpi)).SequenceEqual(expected),
            "The 18 unit status mark renders at exactly 18/27/36 whole device pixels");
        var provider = new[] { 20, 30, 40 };
        Require(SharedPresentation.SupportedDpis.Select(dpi => SharedPresentation.PhysicalPixels(20, dpi)).SequenceEqual(provider),
            "The 20 unit provider mark renders at exactly 20/30/40 whole device pixels");
        Require(Math.Abs(SharedPresentation.ScaledSize(logical, 144) - 27) < 0.001 &&
                Math.Abs(SharedPresentation.ScaledSize(logical, 192) - 36) < 0.001,
            "A logical size scales by the exact device ratio");
    }

    private static void CheckPreferenceRoundTrip(PilotStateAuthority authority)
    {
        var store = new UiPreferencesStore(authority);
        var original = store.Load();
        try
        {
            Require(original.IconSet == UiPreferencesStore.CombinedIconSet,
                "An unset status-icon style loads as the frozen combined default");
            Save(store, original, UiPreferencesStore.ColourIconSet);
            Require(new UiPreferencesStore(authority).Load().IconSet == UiPreferencesStore.ColourIconSet,
                "The Colour status-icon style survives a preference round trip");
            Save(store, original, "Fancy");
            var afterUnsupported = new UiPreferencesStore(authority).Load();
            Require(afterUnsupported.IconSet == UiPreferencesStore.CombinedIconSet,
                "An unsupported stored style is rejected and resolves to the frozen default rather than persisting");
            Save(store, original, UiPreferencesStore.SymbolIconSet);
            var reloaded = new UiPreferencesStore(authority).Load();
            Require(reloaded.IconSet == UiPreferencesStore.SymbolIconSet,
                "The Symbol status-icon style survives a preference round trip");
            Require(reloaded.Theme == original.Theme && reloaded.CompactRows == original.CompactRows &&
                    reloaded.KeepRunningWhenClosed == original.KeepRunningWhenClosed,
                "Choosing a status-icon style leaves the other existing preferences untouched");
            Save(store, original, null);
            Require(new UiPreferencesStore(authority).Load().IconSet == UiPreferencesStore.CombinedIconSet,
                "Clearing the style falls back to the frozen combined default");
        }
        finally { store.SaveAsync(original).GetAwaiter().GetResult(); }
    }

    private static void Save(UiPreferencesStore store, UiPreferences baseline, string? iconSet) =>
        store.SaveAsync(baseline with { IconSet = iconSet }).GetAwaiter().GetResult();

    private static void CheckFeedTimestampColumn()
    {
        var column = SharedPresentation.FeedTimestampColumn();
        Require(column.Header as string == "Time" && Math.Abs(column.Width.Value - SharedPresentation.FeedTimestampColumnWidth) < 0.001,
            "The shared feed timestamp is its own Time column at the contract width");
        var cell = (FrameworkElement)column.CellTemplate.LoadContent();
        Require(cell is TextBlock { VerticalAlignment: VerticalAlignment.Center },
            "The shared timestamp cell is vertically centred in its own column");
        var text = (TextBlock)cell;
        text.DataContext = new TimestampCell
        {
            TimeLabel = "12:41:07", FullTimestamp = "2026-09-19 12:41:07",
            TimeAccessibleLabel = "Recorded 12:41:07"
        };
        // The template's bindings are applied by the data-binding engine at DataBind priority, so the
        // cell is drained before its bound text, tooltip and accessible text are read.
        Drain();
        Require(text.Text == "12:41:07" && !text.Text.StartsWith("At", StringComparison.Ordinal),
            "The shared timestamp text carries no At prefix");
        Require(text.ToolTip as string == "2026-09-19 12:41:07",
            "The shared timestamp keeps the full value on hover");
        Require(AutomationProperties.GetName(text) == "Recorded 12:41:07" &&
                AutomationProperties.GetHelpText(text) == "2026-09-19 12:41:07",
            "The shared timestamp keeps its accessible name and full help text");
    }

    private static void CheckStatusIconStyleSelector()
    {
        var changes = new List<string>();
        var selector = new StatusIconStyleSelector("Status icon style", null, style => changes.Add(style));
        Require(selector.SelectedStyle == UiPreferencesStore.CombinedIconSet,
            "The style selector starts on the frozen combined default, not on an asserted installed choice");
        Require(selector.Segments.Count == 3 &&
                selector.Segments.Select(segment => AutomationProperties.GetName(segment)).SequenceEqual(
                    new[] { "Colour status icons", "Symbol status icons", "Combined status icons" }),
            "All three style segments are present with their own accessible names");
        Require(selector.Segments.Count(segment => segment.IsChecked == true) == 1 && selector.Segments[2].IsChecked == true,
            "Exactly one style segment is selected at rest");
        Require(selector.Segments.All(segment => segment.ToolTip is string tip && tip.Length > 0 &&
                !string.Equals(tip, segment.Content?.ToString(), StringComparison.Ordinal)),
            "Every style segment explains its effect instead of repeating its label");
        Require(selector.Preview.Children.Count == 2 &&
                selector.Preview.Children.OfType<Grid>().All(part => part.Children.OfType<Rectangle>().Any()),
            "The preview draws both samples through the shared presentation source in the tinted combined style");

        selector.Segments[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(selector.SelectedStyle == UiPreferencesStore.ColourIconSet &&
                changes.SequenceEqual(new[] { UiPreferencesStore.ColourIconSet }),
            "Selecting Colour reports exactly one style change");
        Require(selector.Preview.Children.OfType<Grid>().All(part => part.Children.OfType<Rectangle>().Any()) &&
                !selector.Preview.Children.OfType<Grid>().Any(part => part.Children.OfType<CommandGlyph>().Any(glyph => glyph.IconName == "attention")),
            "The Colour preview tints the shark and shows no state symbol");

        selector.SetStyle(UiPreferencesStore.SymbolIconSet);
        Require(selector.SelectedStyle == UiPreferencesStore.SymbolIconSet && changes.Last() == UiPreferencesStore.SymbolIconSet,
            "The Symbol style is selectable and reports one change");
        Require(selector.Preview.Children.OfType<Grid>().All(part => part.Children.OfType<Image>().Any()) &&
                selector.Preview.Children.OfType<Grid>().Any(part =>
                    part.Children.OfType<CommandGlyph>().Any(glyph => glyph.IconName == "attention")),
            "The Symbol preview keeps the normal shark and adds the distinct state symbol");

        selector.SetStyle("Fancy");
        Require(selector.SelectedStyle == UiPreferencesStore.CombinedIconSet &&
                changes.Last() == UiPreferencesStore.CombinedIconSet,
            "An unsupported style request resolves to the frozen default instead of selecting a new state");

        var host = new StackPanel();
        host.Children.Add(selector);
        var surface = new Window
        {
            Width = 420, Height = 140, Content = host, ShowInTaskbar = false, Title = "U1 status style selector check",
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
        };
        surface.Show(); surface.UpdateLayout(); Drain();
        try
        {
            var source = PresentationSource.FromVisual(selector);
            Require(source is not null, "The style selector is attached to a presentation source for keyboard input");
            selector.SetStyle(UiPreferencesStore.ColourIconSet);
            changes.Clear();
            selector.RaiseEvent(PreviewKey(source!, Key.Right));
            Require(selector.SelectedStyle == UiPreferencesStore.SymbolIconSet && changes.Count == 1,
                "The Right arrow selects the next style and reports one change");
            selector.RaiseEvent(PreviewKey(source!, Key.Left));
            Require(selector.SelectedStyle == UiPreferencesStore.ColourIconSet && changes.Count == 2,
                "The Left arrow selects the previous style and reports one change");
        }
        finally { surface.Close(); }
    }

    private static void CheckDetailsHeaderFactory()
    {
        var expanded = 0;
        var closed = 0;
        var header = SharedPresentation.DetailsHeader(() => expanded++, () => closed++);
        Require(AutomationProperties.GetName(header.ExpandButton) == SharedPresentation.DetailsExpandAccessibleName &&
                AutomationProperties.GetHelpText(header.ExpandButton) == SharedPresentation.DetailsExpandAccessibleName &&
                header.ExpandButton.ToolTip as string == SharedPresentation.DetailsExpandAccessibleName,
            "The shared details header names and explains its expand control");
        Require(header.CloseButton is not null &&
                AutomationProperties.GetName(header.CloseButton) == SharedPresentation.DetailsCloseAccessibleName &&
                header.CloseButton.ToolTip as string == SharedPresentation.DetailsCloseAccessibleName,
            "The shared details header names its close control when the consumer has one");
        Require(header.Chrome.Children.OfType<Button>().Count() == 2 &&
                ReferenceEquals(header.Chrome.Children[0], header.ExpandButton) &&
                ReferenceEquals(header.Chrome.Children[1], header.CloseButton),
            "The shared details header supplies the expand and close actions in one chrome panel");
        Require(GlyphGeometry(header.ExpandButton.Content) == GlyphGeometry(SharedPresentation.Glyph(SharedPresentation.DetailsExpandGlyphName, 18)) &&
                GlyphGeometry(header.CloseButton!.Content) == GlyphGeometry(SharedPresentation.Glyph(SharedPresentation.DetailsCloseGlyphName, 18)),
            "The shared details header uses the shared expand glyph and the shared close glyph");
        Require(GlyphGeometry(header.ExpandButton.Content) != GlyphGeometry(CommandIcons.Icon("external", 18)),
            "The shared details header never uses the external-navigation arrow for expanding");
        header.ExpandButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        header.CloseButton!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(expanded == 1 && closed == 1, "Both shared details header controls invoke their own action once");
        Require(SharedPresentation.DetailsHeader(() => { }).CloseButton is null,
            "A consumer without a sidebar close control receives no second close button");
    }

    // --- Real control-tree checks --------------------------------------------------------------

    private static void CheckRealWindowConsumers(PilotStateAuthority authority, ThemeManager theme)
    {
        MainWindow.SuppressLoadedStartupForTest = true;
        var window = new MainWindow(authority, theme) { Width = 1240, Height = 790, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout(); Drain();
        try
        {
            // The workspace details consumer is on the page the window opens on, so it is checked
            // before navigation replaces the visible page.
            // The workspace details consumer is realised the same way the production Activity inspection
            // realises it: its expanded chrome lives behind a ContentPresenter that has no child in either
            // tree until a real item is presented, so the consumer is shown before its action is read.
            Call(window, "InspectActivity", new ActivityRow(DateTimeOffset.Now.ToString("O"),
                "Work/U1/shared-presentation.txt", "Completed upload", "Uploaded and verified.",
                OperationId: "u1-shared-presentation", ItemKind: "File", FileType: "TXT", SizeBytes: 1024));
            Drain();
            window.UpdateLayout();
            Drain();
            var workspaceExpand = window.FindName("DetailsExpandButton") as Button ??
                Descendants<Button>(window).SingleOrDefault(button =>
                    AutomationProperties.GetName(button) == SharedPresentation.DetailsExpandAccessibleName);
            Require(workspaceExpand is not null &&
                    GlyphGeometry(workspaceExpand.Content) == GlyphGeometry(SharedPresentation.Glyph(SharedPresentation.DetailsExpandGlyphName, 18)),
                "The workspace details consumer already presents the same shared expand glyph");
            var factoryExpand = SharedPresentation.DetailsHeader(() => { }).ExpandButton;
            Require(GlyphGeometry(workspaceExpand!.Content) == GlyphGeometry(factoryExpand.Content),
                "The workspace consumer and the shared details header supply the same expand glyph");

            var session = CreateSharingEditorSession(window);
            var chrome = session.GetType()
                .GetField("SidebarChrome", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?.GetValue(session) as StackPanel;
            Require(chrome is not null, "The Sharing editor exposes the shared details header chrome");
            var buttons = chrome!.Children.OfType<Button>().ToArray();
            var sharingExpand = buttons.SingleOrDefault(button =>
                AutomationProperties.GetName(button) == SharedPresentation.DetailsExpandAccessibleName);
            var sharingClose = buttons.SingleOrDefault(button =>
                AutomationProperties.GetName(button) == SharedPresentation.DetailsCloseAccessibleName);
            Require(sharingExpand is not null && sharingClose is not null &&
                    GlyphGeometry(sharingExpand.Content) == GlyphGeometry(factoryExpand.Content),
                "The Sharing details consumer now supplies the same expand glyph and actions as every other consumer");
            Require(GlyphGeometry(sharingExpand!.Content) != GlyphGeometry(CommandIcons.Icon("external", 18)),
                "The Sharing details header no longer uses the external-navigation arrow for expanding");

            Call(window, "NavigateTo", "Connections", true);
            Drain();
            window.UpdateLayout();

            var picker = Field<ComboBox>(window, "_accountPicker");
            Require(picker.ItemTemplate is not null &&
                    ReferenceEquals(picker.ItemTemplate, SharedPresentation.ProviderAccountTemplate()),
                "The Connections account picker binds the one shared provider-mark item template");
            Require(string.IsNullOrEmpty(picker.DisplayMemberPath),
                "The account picker no longer repeats the provider as visible text through a display path");
            Require(AutomationProperties.GetName(picker) == "Storage Account",
                "The account picker keeps its accessible label");

            var template = SharedPresentation.ProviderAccountTemplate();
            var root = (FrameworkElement)template.LoadContent();
            Require(root is Grid { ColumnDefinitions.Count: 2 },
                "The shared account item template lays out a mark column and an identity column");
            var mark = ((Grid)root).Children.OfType<ContentControl>().Single();
            var identity = ((Grid)root).Children.OfType<TextBlock>().Single();
            Require(string.IsNullOrEmpty(identity.Text) && mark.Content is null,
                "The shared account item template binds its identity and mark from the item, not from fixed text");
            Require(window.FindResource(SharedPresentation.ProviderAccountTemplateResourceKey) is DataTemplate,
                "The neutral account mark is an application template available to every picker");

            var dropbox = new StorageAccountChoice("dropbox-id", "Dropbox", "work@example.test (Shared Holder)", true);
            Require(dropbox.Label == "work@example.test (Shared Holder)" && !dropbox.Label.Contains("Dropbox", StringComparison.Ordinal) &&
                    dropbox.ProviderKey == SyncProviders.Dropbox &&
                    dropbox.AccessibleName == "Dropbox work@example.test (Shared Holder)",
                "A Dropbox account shows the account identity with no textual provider prefix and keeps the provider accessible");
            var google = new StorageAccountChoice("google-id", "Google Drive", "google@example.test", true);
            Require(google.Label == "google@example.test" && google.ProviderKey == SyncProviders.GoogleDrive &&
                    google.AccessibleName == "Google Drive google@example.test",
                "A Google Drive account keeps its own identity, mark key and accessible provider");
            var all = new StorageAccountChoice("", "All Providers", "All Accounts", true);
            Require(all.Label == "All Accounts" && all.AccessibleName == "All Accounts" && all.ProviderKey == "unknown",
                "The All Accounts choice keeps its neutral label, accessible name and neutral mark");

            // The provider mark is proved by resolving the real template in a real visual tree, so
            // the trigger, the official mark and the neutral fallback are observed rather than
            // inferred from the template text.
            var probe = new ContentPresenter { ContentTemplate = template, Content = dropbox };
            var probeWindow = new Window
            {
                Width = 320, Height = 120, Content = probe, ShowInTaskbar = false, Title = "U1 account item check",
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
            };
            probeWindow.Resources["DropboxIconTemplate"] = window.FindResource("DropboxIconTemplate");
            probeWindow.Resources["GoogleDriveIconTemplate"] = window.FindResource("GoogleDriveIconTemplate");
            probeWindow.Resources[SharedPresentation.ProviderAccountTemplateResourceKey] =
                window.FindResource(SharedPresentation.ProviderAccountTemplateResourceKey);
            probeWindow.Show(); probeWindow.UpdateLayout(); Drain();
            try
            {
                var dropboxMark = Descendants<ContentControl>(probe).FirstOrDefault();
                Require(dropboxMark is not null &&
                        ReferenceEquals(dropboxMark.ContentTemplate, probeWindow.Resources["DropboxIconTemplate"]),
                    "A Dropbox account resolves the real Dropbox mark inside the shared account item template");
                var accountText = Descendants<TextBlock>(probe).FirstOrDefault();
                Require(accountText is not null && accountText.Text == "work@example.test (Shared Holder)" &&
                        !accountText.Text.Contains("Dropbox", StringComparison.Ordinal),
                    "The shared account item shows the account identity with no textual provider prefix");
                probe.Content = google; Drain();
                var googleMark = Descendants<ContentControl>(probe).FirstOrDefault();
                Require(googleMark is not null &&
                        ReferenceEquals(googleMark.ContentTemplate, probeWindow.Resources["GoogleDriveIconTemplate"]),
                    "A Google Drive account resolves its own real provider mark through the same template");
                probe.Content = all; Drain();
                var neutralMark = Descendants<ContentControl>(probe).FirstOrDefault();
                Require(neutralMark is not null && ReferenceEquals(neutralMark.ContentTemplate,
                        probeWindow.Resources[SharedPresentation.ProviderAccountTemplateResourceKey]),
                    "An unknown provider resolves the neutral mark instead of claiming a provider");
            }
            finally { probeWindow.Close(); Drain(); }

            // The accepted no-clipping contract, decided once and then exercised on the real marks and
            // on deliberately broken copies: a provider template is one unsized, uniformly scaled
            // drop-in, and the consumer's single square host bounds it. The withdrawn 21x18 template is
            // the failure this predicate must detect, so it is required to reject a fixed or oversized
            // mark rather than only to accept the real ones.
            foreach (var key in new[] { "DropboxIconTemplate", "GoogleDriveIconTemplate" })
            {
                var providerRoot = (FrameworkElement)((DataTemplate)window.FindResource(key)).LoadContent();
                Require(IsUnsizedUniformMarkRoot(providerRoot),
                    key + " is one unsized, uniformly scaled drop-in mark that declares no fixed size of its own");
                foreach (var side in ProviderConsumerSides)
                {
                    var slot = MeasureMark(providerRoot, side, key + " in its " + side + " DIP consumer slot");
                    Require(slot.Fits,
                        key + " fits a " + side + " DIP consumer slot without clipping its retained artwork");
                    foreach (var dpi in SharedPresentation.SupportedDpis)
                        Require(slot.FitsWholeDevicePixels(dpi),
                            key + " keeps its whole device pixels inside a " + side + " DIP slot at " +
                            SharedPresentation.DpiPercent(dpi) + " percent display scaling");
                }
            }
            foreach (var provider in new[] { SyncProviders.Dropbox, SyncProviders.GoogleDrive })
            {
                var key = SharedPresentation.ProviderTemplateKey(provider)!;
                foreach (var side in ProviderConsumerSides)
                {
                    var consumerHost = SharedPresentation.ProviderMark(provider, side, window);
                    Require(Math.Abs(consumerHost.Width - consumerHost.Height) < LayoutTolerance &&
                            consumerHost.Width == side && consumerHost.MinWidth == side &&
                            ReferenceEquals(consumerHost.ContentTemplate, window.FindResource(key)),
                        "The shared " + provider + " host is one square " + side +
                        " DIP consumer slot carrying the official mark");
                    Require(MeasureMark(consumerHost, side, provider + " shared host at " + side + " DIP").Fits,
                        "The shared " + provider + " host at " + side + " DIP stays inside its own square slot");
                }
            }
            // Detection is demonstrated rather than asserted: the withdrawn fixed root and a
            // deliberately fixed oversized mark are handed to the same predicate the real marks passed.
            var withdrawn = (Viewbox)((DataTemplate)window.FindResource("DropboxIconTemplate")).LoadContent();
            withdrawn.Width = 20;
            withdrawn.Height = 20;
            Require(!IsUnsizedUniformMarkRoot(withdrawn),
                "The oracle rejects the withdrawn template that declared its own fixed 20x20 size");
            Require(!MeasureMark(withdrawn, 15, "the withdrawn fixed 20x20 root in a 15 DIP slot").Fits,
                "The oracle rejects the withdrawn fixed 20x20 root in the 15 DIP consumer slot");
            var oversized = (Viewbox)((DataTemplate)window.FindResource("DropboxIconTemplate")).LoadContent();
            oversized.Width = 40;
            oversized.Height = 40;
            Require(!IsUnsizedUniformMarkRoot(oversized) &&
                    !MeasureMark(oversized, 15, "a deliberately fixed 40 DIP mark in a 15 DIP slot").Fits,
                "The oracle rejects a deliberately fixed oversized mark dropped into the 15 DIP consumer slot");
            var unknownHost = SharedPresentation.ProviderMark("unknown", 20, window);
            Require(unknownHost.ContentTemplate is null && unknownHost.Content is FrameworkElement,
                "An unknown provider claims no official mark and falls back to a neutral symbol");
        }
        finally { window.Close(); Drain(); }
    }

    private static object CreateSharingEditorSession(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod("SharingEditor", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("The Sharing editor entry point is no longer present on MainWindow.");
        var arguments = new object?[] { "Sharing", null, null, null, null };
        return method.Invoke(window, arguments)
            ?? throw new InvalidDataException("The Sharing editor session was not created.");
    }

    // --- Helpers -------------------------------------------------------------------------------

    // The one provider-mark decision, used by both the accepted and the rejected cases above: a
    // drop-in mark is scaled by its consumer, so a mark that declares a fixed size of its own cannot
    // shrink into a smaller consumer slot and is rejected here rather than silently clipped.
    private static bool IsUnsizedUniformMarkRoot(FrameworkElement root) =>
        root is Viewbox { Child: not null, Stretch: Stretch.Uniform } viewbox &&
        double.IsNaN(viewbox.Width) && double.IsNaN(viewbox.Height);

    // The consumer slots this product actually uses: the 15 DIP tree row and the 20/32 DIP marks.
    private static readonly double[] ProviderConsumerSides = [15, 20, 32];

    private const double LayoutTolerance = 0.001;

    // One measurement of a mark inside a real square consumer slot. Both sizes are kept because a
    // template root carrying its own fixed size reports a clamped DesiredSize yet still arranges and
    // renders outside the slot, which is exactly the clipping this contract refuses.
    private static MarkMeasurement MeasureMark(FrameworkElement mark, double side, string label)
    {
        mark.Measure(new Size(side, side));
        mark.Arrange(new Rect(0, 0, side, side));
        mark.UpdateLayout();
        var measured = new MarkMeasurement(side, mark.DesiredSize, mark.RenderSize);
        Console.WriteLine("MEASURE " + label + ": slot " + side + " DIP, desired " + measured.Desired +
            ", rendered " + measured.Rendered);
        return measured;
    }

    private readonly record struct MarkMeasurement(double Side, Size Desired, Size Rendered)
    {
        internal bool Fits =>
            Desired.Width <= Side + LayoutTolerance && Desired.Height <= Side + LayoutTolerance &&
            Rendered.Width <= Side + LayoutTolerance && Rendered.Height <= Side + LayoutTolerance;

        // Whole device pixels at the supported display scaling: the mark may never need part of a
        // pixel the slot does not have, which the one shared PhysicalPixels helper decides.
        internal bool FitsWholeDevicePixels(uint dpi) =>
            SharedPresentation.PhysicalPixels(Rendered.Width, dpi) <= SharedPresentation.PhysicalPixels(Side, dpi) &&
            SharedPresentation.PhysicalPixels(Rendered.Height, dpi) <= SharedPresentation.PhysicalPixels(Side, dpi);
    }

    private static string GlyphGeometry(object? content)
    {
        if (content is not Viewbox { Child: Canvas canvas }) return string.Empty;
        return canvas.Children.OfType<ShapePath>().Select(path => path.Data?.ToString() ?? string.Empty)
            .FirstOrDefault(value => value.Length > 0) ?? string.Empty;
    }

    private static KeyEventArgs PreviewKey(PresentationSource source, Key key) =>
        new(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };

    private static object? Call(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(window, arguments);

    private static T Field<T>(MainWindow window, string name) =>
        (T)(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidDataException("The expected field " + name + " is missing.")).GetValue(window)!;

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        // Both trees are walked because a shared details header can sit behind a ContentPresenter
        // whose content is only a logical child until the page is measured.
        // DependencyObject does not override equality, so the default comparer is reference equality.
        var seen = new HashSet<DependencyObject>();
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            foreach (var child in Children(queue.Dequeue()))
            {
                if (!seen.Add(child)) continue;
                if (child is T match) yield return match;
                queue.Enqueue(child);
            }
        }
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject logical) yield return logical;
        if (node is not Visual) yield break;
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < count; index++) yield return VisualTreeHelper.GetChild(node, index);
    }

    private static void Drain() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Assert(bool condition, string label)
    {
        if (!condition) throw new InvalidDataException(label);
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidDataException(label);
        Console.WriteLine("PASS " + label);
    }

    private sealed class TimestampCell
    {
        public string TimeLabel { get; init; } = string.Empty;
        public string FullTimestamp { get; init; } = string.Empty;
        public string TimeAccessibleLabel { get; init; } = string.Empty;
    }
}
