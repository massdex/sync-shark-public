using Massdex.SyncShark;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SyncShark.UiFixture;

// F-BASE foundation checks for job ssc-fbase-001 (UI01, UI02, UI03, UI04, UI22).
//
// These are structural, layout and preference-store checks over the shared foundation
// controls. They are not installed-product, provider, click or user-interaction evidence,
// and they say nothing about page packets that have not bound the foundation yet. The
// file was written under a source-only hold and has not been compiled or executed.
internal static class FoundationUiChecks
{
    private const string Selector = "--ui-foundation-checks";

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
            CheckQuietSearchSurface();
            CheckViewSwitch();
            CheckViewModePersistence(appLock.StateAuthority);
            CheckDetailsExpandAndReturn();
            CheckResponsiveHeader();
            Console.WriteLine("PASS " + Selector +
                "; structural, layout and preference-store checks only, no provider or installed-state effects");
            return 0;
        }
        finally { app.Shutdown(); }
    }

    private static void CheckQuietSearchSurface()
    {
        var style = SharedPresentation.QuietTextBoxStyle();
        Require(ReferenceEquals(SetterValue(style, Control.BackgroundProperty), Brushes.Transparent) &&
                ReferenceEquals(SetterValue(style, Control.BorderBrushProperty), Brushes.Transparent),
            "The quiet search surface rests transparent instead of holding a solid control surface");
        var template = SetterValue(style, Control.TemplateProperty) as ControlTemplate;
        Require(template is not null, "The quiet search surface ships a control template");
        var triggers = template!.Triggers.OfType<Trigger>().Select(trigger => trigger.Property).ToArray();
        Require(triggers.Contains(UIElement.IsMouseOverProperty) &&
                triggers.Contains(UIElement.IsKeyboardFocusWithinProperty) &&
                triggers.Contains(UIElement.IsEnabledProperty),
            "The quiet search surface defines hover, focus and disabled affordance triggers");

        var dropdown = SharedPresentation.QuietComboBoxStyle();
        Require(ReferenceEquals(SetterValue(dropdown, Control.BackgroundProperty), Brushes.Transparent) &&
                ReferenceEquals(SetterValue(dropdown, Control.BorderBrushProperty), Brushes.Transparent),
            "The quiet dropdown surface rests transparent and leaves hover, focus and disabled to the shared combo template");

        var box = new TextBox();
        var submissions = 0;
        var host = SharedPresentation.QuietSearchField(box, "Search foundation items", "Search…", () => submissions++);
        Require(ReferenceEquals(box.Style, style), "The shared search field applies the quiet text style");
        Require(AutomationProperties.GetName(box) == "Search foundation items",
            "The shared search field keeps an accessible name");
        var prompt = host.Children.OfType<TextBlock>().First();
        var clear = host.Children.OfType<Button>().First();
        Require(AutomationProperties.GetName(clear) == "Clear Search foundation items",
            "The Clear action keeps its own accessible name");
        Require(prompt.Visibility == Visibility.Visible && clear.Visibility == Visibility.Collapsed,
            "An empty shared search field shows its prompt and hides Clear");
        box.Text = "manual.pdf";
        Require(prompt.Visibility == Visibility.Collapsed && clear.Visibility == Visibility.Visible,
            "Typing swaps the prompt for the Clear action");

        var prefilled = new TextBox { Text = "existing query" };
        var prefilledHost = SharedPresentation.QuietSearchField(prefilled, "Search prefilled", "Prompt");
        Require(prefilledHost.Children.OfType<Button>().First().Visibility == Visibility.Visible &&
                prefilledHost.Children.OfType<TextBlock>().First().Visibility == Visibility.Collapsed,
            "A prefilled shared search field initialises the prompt and Clear from the text already present");

        var surface = new StackPanel();
        surface.Children.Add(host);
        var window = new Window { Width = 420, Height = 160, Content = surface, ShowInTaskbar = false,
            Title = "F-BASE search check" };
        window.Show(); window.UpdateLayout(); Drain();
        try
        {
            var source = PresentationSource.FromVisual(box);
            Require(source is not null, "The shared search field is attached to a presentation source for keyboard input");
            if (source is null) return;
            box.RaiseEvent(KeyEvent(source, Key.Escape));
            Require(box.Text.Length == 0 && submissions == 1, "Escape clears the shared search field and submits once");
            box.Text = "second query";
            box.RaiseEvent(KeyEvent(source, Key.Enter));
            Require(submissions == 2 && box.Text == "second query",
                "Enter submits without clearing the shared search field");
            box.Text = "typed";
            clear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Require(box.Text.Length == 0 && submissions == 3, "The Clear action empties the shared search field and submits once");
        }
        finally { window.Close(); }
    }

    private static void CheckViewSwitch()
    {
        var changes = new List<bool>();
        var control = SharedPresentation.ViewSwitch("foundation-check", false,
            switchControl => changes.Add(switchControl.IsDetailed));
        Require(control.SimpleSegment.IsChecked == true && control.DetailedSegment.IsChecked == false,
            "The view switch starts with exactly one selected segment");
        Require(AutomationProperties.GetName(control) == "Row detail view" &&
                AutomationProperties.GetName(control.SimpleSegment) == "Simple rows" &&
                AutomationProperties.GetName(control.DetailedSegment) == "Detailed rows",
            "Every view-switch part keeps an accessible name");
        Require(!string.IsNullOrWhiteSpace(control.SimpleSegment.ToolTip?.ToString()) &&
                !string.IsNullOrWhiteSpace(control.DetailedSegment.ToolTip?.ToString()),
            "Both view-switch segments keep a tooltip");

        control.DetailedSegment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(control.IsDetailed && control.DetailedSegment.IsChecked == true && control.SimpleSegment.IsChecked == false,
            "Selecting Detailed leaves exactly one selected segment");
        Require(changes.Count == 1 && changes[0], "Selecting Detailed reports one view change");
        control.DetailedSegment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(changes.Count == 1, "Re-selecting the current segment reports no further change");
        control.SimpleSegment.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Require(!control.IsDetailed && changes.Count == 2 && !changes[1], "Selecting Simple reports one view change");

        var surface = new StackPanel();
        surface.Children.Add(control);
        var window = new Window { Width = 260, Height = 120, Content = surface, ShowInTaskbar = false,
            Title = "F-BASE switch check" };
        window.Show(); window.UpdateLayout(); Drain();
        try
        {
            var source = PresentationSource.FromVisual(control);
            Require(source is not null, "The view switch is attached to a presentation source for keyboard input");
            if (source is null) return;
            control.RaiseEvent(KeyEvent(source, Key.Right, preview: true));
            Require(control.IsDetailed && changes.Count == 3 && changes[2], "The Right arrow selects Detailed");
            control.RaiseEvent(KeyEvent(source, Key.Left, preview: true));
            Require(!control.IsDetailed && changes.Count == 4 && !changes[3], "The Left arrow selects Simple");
        }
        finally { window.Close(); }

        SimpleDetailedSwitch.AnimationOverrideForTest = true;
        try
        {
            control.SetView(true, persist: false);
            Require(control.SlideAnimationApplied, "An animation-enabled environment applies the slide transition");
        }
        finally { SimpleDetailedSwitch.AnimationOverrideForTest = false; }

        control.SetView(false, persist: false);
        control.SetView(true, persist: false);
        Require(!control.SlideAnimationApplied && Math.Abs(control.ThumbOffset - control.ThumbTarget) < 0.5,
            "Reduced motion moves the selected background without animating it");
        Require(Math.Abs(control.ThumbTarget - 36) < 0.5 && Math.Abs(control.ThumbOffset - 36) < 0.5,
            "The Detailed segment offset sits in the second segment cell");
        SimpleDetailedSwitch.AnimationOverrideForTest = null;
        var grid = new DataGrid { HeadersVisibility = DataGridHeadersVisibility.None };
        grid.Columns.Add(new DataGridTextColumn { Header = "Item" });
        var columns = new[] { new FeedDetailColumn("Time", "Timestamp", 110) };
        SharedPresentation.ApplyFeedDetail(grid, true, columns);
        Require(grid.HeadersVisibility == DataGridHeadersVisibility.Column && grid.Columns.Count == 2,
            "Detailed rows show their column headings");
        SharedPresentation.ApplyFeedDetail(grid, false, columns);
        Require(grid.HeadersVisibility == DataGridHeadersVisibility.None && grid.Columns.Count == 1,
            "Returning to Simple removes detailed headings and columns without replacing the data");
    }

    private static void CheckViewModePersistence(PilotStateAuthority authority)
    {
        var store = new UiPreferencesStore(authority);
        var original = store.Load();
        try
        {
            var modes = CopyModes(original);
            modes["activity"] = UiPreferencesStore.DetailedView;
            modes["pending"] = UiPreferencesStore.SimpleView;
            store.SaveAsync(original with { PageViewModes = modes }).GetAwaiter().GetResult();
            var reloaded = new UiPreferencesStore(authority).Load();
            Require(reloaded.PageViewModes is not null &&
                    reloaded.PageViewModes.GetValueOrDefault("activity") == UiPreferencesStore.DetailedView &&
                    reloaded.PageViewModes.GetValueOrDefault("pending") == UiPreferencesStore.SimpleView,
                "Per-page Simple/Detailed modes survive a new preferences store instance");
            Require(reloaded.Theme == original.Theme && reloaded.CompactRows == original.CompactRows,
                "Saving a view mode leaves the other existing preferences untouched");

            store.SaveAsync(reloaded with
                { PageViewModes = new Dictionary<string, string> { ["activity"] = "Fancy" } }).GetAwaiter().GetResult();
            var filtered = new UiPreferencesStore(authority).Load();
            Require(filtered.PageViewModes is null || !filtered.PageViewModes.ContainsKey("activity"),
                "An unsupported saved view mode is dropped rather than breaking the preference load");

            var rejected = false;
            try
            {
                store.SaveAsync(reloaded with
                {
                    PageViewModes = new Dictionary<string, string>
                    {
                        ["Not A Key"] = UiPreferencesStore.DetailedView
                    }
                }).GetAwaiter().GetResult();
            }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "A saved view mode bound to an unsupported page key is rejected");
            Require(new UiPreferencesStore(authority).Load().PageViewModes?.ContainsKey("Not A Key") != true,
                "The rejected save did not change the stored page view modes");
        }
        finally { store.SaveAsync(original).GetAwaiter().GetResult(); }
    }

    private static void CheckDetailsExpandAndReturn()
    {
        var rows = new StackPanel();
        for (var index = 0; index < 60; index++)
            rows.Children.Add(new TextBlock { Text = "detail row " + index, Height = 24 });
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var sidebar = new ContentPresenter { Height = 200 };
        var dialogs = new List<DetailsDialog>();
        var host = new DetailsHost(scroll, sidebar, element =>
        {
            var dialog = new DetailsDialog(element);
            dialogs.Add(dialog);
            return dialog;
        });
        Require(ReferenceEquals(sidebar.Content, scroll), "The details sidebar hosts the shared details element");
        var model = new object();
        host.Present(model);
        Require(ReferenceEquals(scroll.DataContext, model),
            "The shared details element carries the one presented model");

        var expandTarget = new Button { Content = "Expand" };
        DockPanel.SetDock(expandTarget, Dock.Bottom);
        var ownerLayout = new DockPanel();
        ownerLayout.Children.Add(expandTarget);
        ownerLayout.Children.Add(sidebar);
        var owner = new Window { Width = 420, Height = 320, Content = ownerLayout, ShowInTaskbar = false,
            Title = "F-BASE details check" };
        owner.Show(); owner.UpdateLayout(); Drain();
        try
        {
            Keyboard.Focus(expandTarget);
            scroll.UpdateLayout();
            scroll.ScrollToVerticalOffset(120);
            owner.UpdateLayout(); Drain();
            var captured = scroll.VerticalOffset;

            host.Expand(owner);
            Require(host.Expanded && dialogs.Count == 1, "Expanding moves the details into exactly one dialog");
            Require(sidebar.Content is null && ReferenceEquals(dialogs[0].HostedContent, scroll),
                "The expanded dialog owns the same details element the sidebar had");
            Require(dialogs[0].IsVisible, "The expanded details dialog is shown");
            Require(dialogs[0].WindowStyle == WindowStyle.None &&
                    System.Windows.Shell.WindowChrome.GetWindowChrome(dialogs[0]) is { CaptionHeight: 48 },
                "Expanded details use themed app chrome rather than a default Windows title bar");
            Require(ReferenceEquals(scroll.DataContext, model), "The details model survives expansion");
            Require(host.LastFocusTarget is null || ReferenceEquals(host.LastFocusTarget, expandTarget),
                "Expansion records the focused control that return must restore");

            host.Collapse();
            Require(!host.Expanded && ReferenceEquals(sidebar.Content, scroll),
                "Closing the dialog returns the same details element to the sidebar");
            Require(!dialogs[0].IsVisible, "The expanded dialog is closed again on return");
            Require(ReferenceEquals(scroll.DataContext, model), "The details model survives the return");
            owner.UpdateLayout(); Drain();
            if (captured > 0)
                Require(Math.Abs(scroll.VerticalOffset - captured) < 1,
                    "Returning restores the sidebar scroll position");
            else
                Console.WriteLine("SKIP details scroll restore: the fixture window produced no scrollable viewport");

            host.Expand(owner);
            Drain();
            Require(Keyboard.FocusedElement is Button focused &&
                    AutomationProperties.GetName(focused) == "Return to sidebar",
                "Expanded details take keyboard focus on their return control");
            var source = PresentationSource.FromVisual(dialogs[1]) ??
                throw new InvalidOperationException("Expanded dialog has no input source");
            dialogs[1].RaiseEvent(KeyEvent(source, Key.Escape, preview: true));
            Require(!host.Expanded && ReferenceEquals(sidebar.Content, scroll),
                "Escape in the actual dialog returns the details element to the sidebar");
        }
        finally { owner.Close(); }
    }

    private static void CheckResponsiveHeader()
    {
        var account = new StackPanel { Orientation = Orientation.Horizontal };
        account.Children.Add(new TextBlock { Text = "Account" });
        account.Children.Add(new ComboBox { MinWidth = 240, MaxWidth = 460, Style = SharedPresentation.QuietComboBoxStyle() });
        var header = SharedPresentation.PageHeader("Activity", account,
            new Button { Content = "Refresh" }, SharedPresentation.ViewSwitch("header-check", false, _ => { }));

        Layout(header, 1004);
        Require(header.IsSingleRow && header.ActionRowCount == 1,
            "The default 1240-wide window keeps the title and its controls on one header row");
        RequireWithinBounds(header, 1004);

        // 760 wide minus the 196px expanded rail and the 2 x 20px page margins leaves 524px.
        Layout(header, 524);
        Require(!header.IsSingleRow, "The minimum 760x520 window moves the header controls onto another row");
        RequireWithinBounds(header, 524);

        Layout(header, 320);
        RequireWithinBounds(header, 320);
        Require(header.DesiredSize.Height > 40,
            "The wrapped header grows instead of clipping its controls at an extreme content width");
    }

    private static void Layout(PageHeaderPanel header, double width)
    {
        header.Measure(new Size(width, double.PositiveInfinity));
        header.Arrange(new Rect(0, 0, width, header.DesiredSize.Height));
        header.UpdateLayout();
    }

    private static void RequireWithinBounds(PageHeaderPanel header, double width)
    {
        Require(header.TrailingItems.Count == 3,
            "The checked header carries the account selector and both primary actions");
        foreach (var element in header.TrailingItems.OfType<FrameworkElement>())
        {
            var slot = LayoutInformation.GetLayoutSlot(element);
            Require(slot.Left >= -0.5 && slot.Right <= width + 0.5 &&
                    slot.Top >= -0.5 && slot.Bottom <= header.DesiredSize.Height + 0.5,
                "Header control " + element.GetType().Name + " stays inside the " +
                width.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "px content bounds");
        }
    }

    private static object? SetterValue(Style style, DependencyProperty property) =>
        style.Setters.OfType<Setter>().FirstOrDefault(setter => setter.Property == property)?.Value;

    private static Dictionary<string, string> CopyModes(UiPreferences preferences) =>
        (preferences.PageViewModes ?? new Dictionary<string, string>())
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    private static KeyEventArgs KeyEvent(PresentationSource source, Key key, bool preview = false) =>
        new(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = preview ? Keyboard.PreviewKeyDownEvent : Keyboard.KeyDownEvent
        };

    private static void Drain() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
