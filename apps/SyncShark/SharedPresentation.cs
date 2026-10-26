using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using Brushes = System.Windows.Media.Brushes;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using Size = System.Windows.Size;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ComboBox = System.Windows.Controls.ComboBox;
using Binding = System.Windows.Data.Binding;
using Orientation = System.Windows.Controls.Orientation;

namespace Massdex.SyncShark;

internal enum SummaryActivityFilter { Files, Sync, All }

// Shared UI foundation for the UI iteration. Later page packets bind these instead of
// copying a page-local header, search field, view switch or details host.
// The frozen presentation contract for iteration three lives in SharedPresentation.Contract.cs.
internal static partial class SharedPresentation
{
    // UF3: the one page-local inline feedback surface. The caller owns its target TextBlock, placed beneath
    // its own toolbar, so an explicit user action can report loading, a result, a refusal or a failure where
    // the user is actually looking, while background messages keep the existing ShowStatus routing. The
    // target is collapsed until a user action so an untouched page never reserves space for it, and the text
    // is wrapped and named for accessibility rather than truncated.
    internal static void SetInlineFeedback(TextBlock? target, string? message, bool isError)
    {
        if (target is null) return;
        var text = message?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            target.Text = string.Empty;
            target.Visibility = Visibility.Collapsed;
            return;
        }
        target.Text = text;
        target.TextWrapping = TextWrapping.Wrap;
        target.SetResourceReference(Control.ForegroundProperty, isError ? "ErrorBrush" : "MutedInk");
        AutomationProperties.SetName(target, isError ? "Action failed" : "Action status");
        target.Visibility = Visibility.Visible;
    }

    // The same surface with no message yet: used when an explicit action starts, so the previous outcome is
    // cleared rather than left beside a new in-progress state.
    internal static void ClearInlineFeedback(TextBlock? target) => SetInlineFeedback(target, null, false);
    // The one account-item presentation. U1 froze ProviderAccountTemplate in
    // SharedPresentation.Contract.cs; this existing entry point returns that same template so the
    // workspace scope picker, the Sharing account picker and the Connections pickers cannot drift
    // into two competing account presentations. The item text binds Label, which every account item
    // type already carries as its own identity: the account alone for a storage choice, its account
    // name or its neutral All Accounts name for a scope choice.
    internal static DataTemplate AccountChoiceTemplate() => ProviderAccountTemplate();

    internal static bool MatchesSummary(ActivityRow row, SummaryActivityFilter filter) => filter switch
    {
        SummaryActivityFilter.Files => row.IsFileActivity,
        SummaryActivityFilter.Sync => row.HasCategory(SyncActivityCategories.Sync),
        _ => true
    };

    // A log event may be newer than a file event for the same operation. Select the requested
    // account/category first so the log cannot erase the file from Home or tray summaries.
    internal static ActivityRow[] SummaryRows(IEnumerable<ActivityRow> source, string? accountId,
        SummaryActivityFilter filter, int limit) => source
        .Where(row => !row.IsRoutineCheck && row.MatchesAccount(accountId) && MatchesSummary(row, filter))
        .OrderByDescending(row => DateTimeOffset.TryParse(row.When, out var when) ? when : DateTimeOffset.MinValue)
        .DistinctBy(row => string.IsNullOrEmpty(row.OperationId) ? row.EventKey : "operation:" + row.OperationId,
            StringComparer.Ordinal)
        .Take(limit).ToArray();

    // One compact, manual selector for Home and tray. No rotation, timers or hidden selection.
    internal static FrameworkElement SummarySelector(Action<SummaryActivityFilter> selected)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new List<ToggleButton>();
        foreach (var mode in Enum.GetValues<SummaryActivityFilter>())
        {
            var label = mode == SummaryActivityFilter.Files ? "File changes" : mode == SummaryActivityFilter.Sync ? "Sync log" : "All activity";
            var button = new ToggleButton { Content = "●", IsChecked = mode == SummaryActivityFilter.Files,
                Style = (Style)System.Windows.Application.Current.FindResource("SegmentToggle"),
                Width = 30, Height = 24, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(2, 0, 2, 0),
                ToolTip = label };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => { foreach (var peer in buttons) peer.IsChecked = ReferenceEquals(peer, button); selected(mode); };
            buttons.Add(button); panel.Children.Add(button);
        }
        return panel;
    }

    internal static Grid LabelledSummary(string summary)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var line in summary.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var split = line.IndexOf(':');
            var label = WorkspaceVisuals.Text(split > 0 ? line[..(split + 1)] : line, 13, brush: "MutedInk");
            label.VerticalAlignment = VerticalAlignment.Top; label.Margin = new Thickness(0, 0, split > 0 ? 10 : 0, 5);
            Grid.SetRow(label, row); grid.Children.Add(label);
            if (split > 0)
            {
                var value = WorkspaceVisuals.Text(line[(split + 1)..].TrimStart(), 13);
                value.VerticalAlignment = VerticalAlignment.Top; value.Margin = new Thickness(0, 0, 0, 5);
                Grid.SetRow(value, row); Grid.SetColumn(value, 1); grid.Children.Add(value);
            }
            else Grid.SetColumnSpan(label, 2);
        }
        return grid;
    }
    internal const double HeaderItemGap = 12;
    internal const double CompactRowHeight = 72;
    internal const double DetailedRowHeight = 92;

    internal static PageHeaderPanel PageHeader(string title, UIElement? accountSelector,
        params UIElement[] primaryActions) => new(title, accountSelector, primaryActions);

    // Quiet search surface: themed hover/focus/disabled states, an accessible name, a
    // visible prompt, Enter to submit, Escape to clear and an explicit clear action.
    internal static Grid QuietSearchField(TextBox box, string accessibleName, string placeholder,
        Action? submit = null, double width = 240, string? tooltip = null)
    {
        var host = new Grid { Width = width, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0) };
        box.MinWidth = 0;
        box.Padding = new Thickness(32, 7, 28, 7);
        box.Style = QuietTextBoxStyle();
        box.Margin = new Thickness(0);
        AutomationProperties.SetName(box, accessibleName);
        host.Children.Add(WorkspaceVisuals.Detach(box));
        box.ToolTip = tooltip ?? placeholder;
        var glyph = new CommandGlyph { IconName = "search", Width = 15, Height = 15,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(9, 0, 0, 0), IsHitTestVisible = false };
        glyph.SetResourceReference(Control.ForegroundProperty, "MutedInk");
        host.Children.Add(glyph);
        var prompt = WorkspaceVisuals.Text(placeholder, 13, brush: "MutedInk");
        prompt.Margin = new Thickness(33, 0, 26, 0);
        prompt.IsHitTestVisible = false;
        prompt.TextWrapping = TextWrapping.NoWrap;
        prompt.TextTrimming = TextTrimming.CharacterEllipsis;
        host.Children.Add(prompt);
        var clear = new Button { Content = CommandIcons.Icon("close", 11), Width = 24, Height = 24, MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0), Padding = new Thickness(0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(clear, "Clear " + accessibleName);
        clear.ToolTip = "Clear " + accessibleName;
        clear.Click += (_, _) => { box.Clear(); box.Focus(); submit?.Invoke(); };
        host.Children.Add(clear);
        void RefreshSearchChrome()
        {
            var empty = box.Text.Length == 0;
            prompt.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            clear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }
        box.TextChanged += (_, _) => RefreshSearchChrome();
        // Initialise from the text already in the box, not only from later changes.
        RefreshSearchChrome();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (box.Text.Length == 0) return;
                box.Clear(); submit?.Invoke(); e.Handled = true;
            }
            else if (e.Key == Key.Enter) { submit?.Invoke(); e.Handled = true; }
        };
        return host;
    }

    internal static Style QuietTextBoxStyle() =>
        (Style)System.Windows.Application.Current.FindResource("QuietTextBox");

    internal static Style QuietComboBoxStyle() =>
        (Style)System.Windows.Application.Current.FindResource("QuietComboBox");

    // Applies the shared quiet dropdown surface to a tree built before the shared styles were
    // reachable, such as the compact header's account selector. Walks the logical tree because
    // the tree is not visualised yet when a header is composed.
    internal static void ApplyQuietDropdowns(object root)
    {
        if (root is ComboBox combo) combo.Style = QuietComboBoxStyle();
        if (root is not DependencyObject node) return;
        foreach (var child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject element) ApplyQuietDropdowns(element);
    }

    // F-BASE owns its own presentation glyphs so the foundation does not need to widen the
    // shared command-icon registry. Names outside this set fall through to CommandIcons.
    private static readonly Dictionary<string, string> FoundationGlyphs = new(StringComparer.Ordinal)
    {
        ["compactRows"] = "M4,7 H20 M4,12 H20 M4,17 H20",
        ["detailRows"] = "M3,5 H21 V21 H3 Z M6,9 H18 M6,13 H18 M6,17 H14",
        ["expand"] = "M9,4 H4 V9 M15,20 H20 V15 M20,9 V4 H15 M4,15 V20 H9"
    };

    internal static FrameworkElement Glyph(string name, double size = 18)
    {
        if (!FoundationGlyphs.TryGetValue(name, out var data)) return CommandIcons.Icon(name, size);
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data), Width = 24, Height = 24, StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.None
        };
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding("Foreground")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1)
        });
        return new Viewbox
        {
            Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Canvas { Width = 24, Height = 24, Children = { path } }
        };
    }

    internal static SimpleDetailedSwitch ViewSwitch(string pageKey, bool detailed, Action<SimpleDetailedSwitch> changed)
    {
        var control = new SimpleDetailedSwitch(pageKey);
        control.ViewChanged += (_, _) => changed(control);
        control.SetView(detailed, persist: false);
        return control;
    }

    // Simple and Detailed swap presentation only: the same records, selection, filters and
    // sort stay in place because only the column set and row density change.
    internal static void ApplyFeedDetail(DataGrid grid, bool detailed, IReadOnlyList<FeedDetailColumn> detailColumns,
        double? compactHeight = null, double? detailedHeight = null)
    {
        grid.HeadersVisibility = detailed ? DataGridHeadersVisibility.Column : DataGridHeadersVisibility.None;
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, detailed ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        foreach (var column in grid.Columns)
        {
            if (column.Header is "Status") column.Width = detailed ? 64 : 40;
            else if (column.Header is "Actions") column.Width = detailed ? 66 : 38;
        }
        var existing = grid.Columns.Where(column => column.Header is string header &&
            detailColumns.Any(expected => string.Equals(expected.Header, header, StringComparison.Ordinal))).ToArray();
        if (detailed)
        {
            if (existing.Length == 0)
            {
                var insertAt = Math.Max(1, grid.Columns.Count - 2);
                foreach (var column in detailColumns)
                {
                    var textStyle = new Style(typeof(TextBlock), DataGridTextColumn.DefaultElementStyle);
                    textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,
                        column.Wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis));
                    textStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty,
                        column.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap));
                    textStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
                    textStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0)));
                    textStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(column.Path)));
                    grid.Columns.Insert(insertAt++, new DataGridTextColumn
                    {
                        Header = column.Header, Width = column.Width, SortMemberPath = column.Path,
                        ElementStyle = textStyle,
                        Binding = new Binding(column.Path) { Mode = BindingMode.OneWay }
                    });
                }
            }
        }
        else
        {
            foreach (var column in existing) grid.Columns.Remove(column);
        }
        // Row density stays owned by the existing saved density preference unless the caller
        // supplies an explicit pair of heights for its own surface.
        if (compactHeight is { } compact && detailedHeight is { } expanded)
            grid.RowHeight = detailed ? expanded : compact;
    }

    // UIV3: the one shared attention-row icon. An internal attention entity is a Sync Shark item, so
    // it renders through the existing branded renderer with the explicit role and state its row
    // carries; a real file conflict keeps the Windows file-type presentation of its own actual
    // filename. Home, the Needs Attention list and problems tab and the tray flyout all call this one
    // factory, and the host states the bounded size it wants, so no surface repeats the choice or
    // draws its own artwork.
    internal static class AttentionIcon
    {
        internal static FrameworkElement Create(InternalAttentionIcon? internalIcon, string title, double size) =>
            new ShellTypeImage
            {
                Width = size,
                Height = size,
                DataContext = (object?)internalIcon ?? new ActivityRow(string.Empty, title, string.Empty,
                    string.Empty, ItemKind: "File")
            };
    }
}

internal sealed record FeedDetailColumn(string Header, string Path, double Width, bool Wrap = false);

// Compact page header: title left, account selector and primary actions on the same row
// while they fit, with an intentional second row at the minimum supported width.
internal sealed class PageHeaderPanel : Panel
{
    private readonly TextBlock _title;
    private readonly List<UIElement> _trailing = [];
    private readonly List<List<UIElement>> _actionRows = [];

    internal PageHeaderPanel(string title, UIElement? accountSelector, IEnumerable<UIElement> primaryActions)
    {
        _title = WorkspaceVisuals.Text(title, 24, true);
        _title.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_title);
        if (accountSelector is not null) _trailing.Add(accountSelector);
        _trailing.AddRange(primaryActions.Where(action => action is not null));
        foreach (var element in _trailing) Children.Add(element);
        Margin = new Thickness(0, 0, 0, 14);
    }

    internal string Title => _title.Text;
    internal IReadOnlyList<UIElement> TrailingItems => _trailing;
    internal bool IsSingleRow { get; private set; } = true;
    internal double ActionRowWidth { get; private set; }
    internal double ActionRowHeight { get; private set; }
    internal int ActionRowCount => _actionRows.Count;

    protected override Size MeasureOverride(Size availableSize)
    {
        var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _title.Measure(infinite);
        foreach (var element in _trailing) element.Measure(infinite);
        var available = double.IsInfinity(availableSize.Width) ? double.MaxValue : Math.Max(0, availableSize.Width);
        ActionRowWidth = RowWidth(_trailing);
        ActionRowHeight = _trailing.Count == 0 ? 0 : _trailing.Max(element => element.DesiredSize.Height);
        IsSingleRow = _trailing.Count == 0 ||
            _title.DesiredSize.Width + SharedPresentation.HeaderItemGap + ActionRowWidth <= available;
        _actionRows.Clear();
        if (_trailing.Count > 0)
        {
            if (IsSingleRow) _actionRows.Add([.. _trailing]);
            else
            {
                // The account selector and primary actions wrap inside the real content width,
                // so a minimum-width window never places a control outside the header bounds.
                var row = new List<UIElement>();
                var rowWidth = 0d;
                foreach (var element in _trailing)
                {
                    var candidate = row.Count == 0 ? element.DesiredSize.Width
                        : rowWidth + SharedPresentation.HeaderItemGap + element.DesiredSize.Width;
                    if (row.Count > 0 && candidate > available)
                    {
                        _actionRows.Add(row); row = []; candidate = element.DesiredSize.Width;
                    }
                    row.Add(element); rowWidth = candidate;
                }
                if (row.Count > 0) _actionRows.Add(row);
            }
        }
        var titleHeight = _title.DesiredSize.Height;
        if (!IsSingleRow && _title.DesiredSize.Width > available)
        {
            // A very narrow content column wraps the title instead of clipping it.
            _title.Measure(new Size(available, double.PositiveInfinity));
            titleHeight = _title.DesiredSize.Height;
        }
        var rowsHeight = _actionRows.Sum(row => row.Max(element => element.DesiredSize.Height));
        var rowsWidth = _actionRows.Count == 0 ? 0 : _actionRows.Max(RowWidth);
        if (IsSingleRow)
            return new Size(Math.Min(available,
                    Math.Max(_title.DesiredSize.Width + SharedPresentation.HeaderItemGap + ActionRowWidth, rowsWidth)),
                Math.Max(titleHeight, ActionRowHeight));
        return new Size(Math.Min(available, Math.Max(_title.DesiredSize.Width, rowsWidth)),
            titleHeight + SharedPresentation.HeaderItemGap + rowsHeight +
            (_actionRows.Count > 1 ? SharedPresentation.HeaderItemGap * (_actionRows.Count - 1) : 0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var titleHeight = _title.DesiredSize.Height;
        if (IsSingleRow)
        {
            var rowHeight = Math.Max(titleHeight, ActionRowHeight);
            _title.Arrange(new Rect(0, Math.Max(0, (rowHeight - titleHeight) / 2),
                Math.Min(_title.DesiredSize.Width, finalSize.Width), titleHeight));
            var x = Math.Max(0, finalSize.Width - ActionRowWidth);
            foreach (var element in _trailing)
            {
                var width = Math.Min(element.DesiredSize.Width, Math.Max(0, finalSize.Width - x));
                element.Arrange(new Rect(x, Math.Max(0, (rowHeight - element.DesiredSize.Height) / 2),
                    width, element.DesiredSize.Height));
                x += element.DesiredSize.Width + SharedPresentation.HeaderItemGap;
            }
            return finalSize;
        }
        _title.Arrange(new Rect(0, 0, Math.Min(_title.DesiredSize.Width, finalSize.Width), titleHeight));
        var y = titleHeight;
        foreach (var row in _actionRows)
        {
            y += SharedPresentation.HeaderItemGap;
            var rowHeight = row.Max(element => element.DesiredSize.Height);
            var x = 0d;
            foreach (var element in row)
            {
                var width = Math.Min(element.DesiredSize.Width, Math.Max(0, finalSize.Width - x));
                element.Arrange(new Rect(x, y + Math.Max(0, (rowHeight - element.DesiredSize.Height) / 2),
                    width, element.DesiredSize.Height));
                x += width + SharedPresentation.HeaderItemGap;
            }
            y += rowHeight;
        }
        return finalSize;
    }

    private static double RowWidth(IReadOnlyList<UIElement> row) => row.Count == 0 ? 0
        : row.Sum(element => element.DesiredSize.Width) + SharedPresentation.HeaderItemGap * (row.Count - 1);
}

// The shared Simple/Detailed segmented icon switch: one selected segment, a sliding
// selected background, tooltips, accessible names and toggle state, keyboard operation
// and no animation when Windows reduced motion is active.
internal sealed class SimpleDetailedSwitch : Grid
{
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(160);
    private readonly Border _thumb = new();
    private readonly TranslateTransform _slide = new();
    private readonly ToggleButton _simpleSegment;
    private readonly ToggleButton _detailedSegment;
    private readonly double _segmentWidth;
    private bool _detailedSelected;

    internal static bool? AnimationOverrideForTest { get; set; }
    internal static bool AnimationsEnabled => AnimationOverrideForTest ?? SystemParameters.ClientAreaAnimation;

    internal SimpleDetailedSwitch(string pageKey, double segmentWidth = 36, double height = 30)
    {
        PageKey = pageKey;
        _segmentWidth = segmentWidth;
        Width = segmentWidth * 2 + 2;
        Height = height;
        Background = Brushes.Transparent;
        VerticalAlignment = VerticalAlignment.Center;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AutomationProperties.SetName(this, "Row detail view");
        var track = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        track.SetResourceReference(Border.BackgroundProperty, "ControlSurface");
        track.SetResourceReference(Border.BorderBrushProperty, "Border");
        SetColumnSpan(track, 2);
        Children.Add(track);
        _thumb.Width = segmentWidth - 4;
        _thumb.Height = height - 6;
        _thumb.CornerRadius = new CornerRadius(6);
        _thumb.HorizontalAlignment = HorizontalAlignment.Left;
        _thumb.VerticalAlignment = VerticalAlignment.Center;
        _thumb.Margin = new Thickness(2, 0, 0, 0);
        _thumb.RenderTransform = _slide;
        _thumb.SetResourceReference(Border.BackgroundProperty, "SelectedSurface");
        SetColumnSpan(_thumb, 2);
        Children.Add(_thumb);
        _simpleSegment = Segment("compactRows", "Simple rows", "Show the compact row layout");
        _detailedSegment = Segment("detailRows", "Detailed rows", "Show the detailed row layout");
        SetColumn(_simpleSegment, 0); SetColumn(_detailedSegment, 1);
        Children.Add(_simpleSegment); Children.Add(_detailedSegment);
        _simpleSegment.Click += (_, _) => SetView(false);
        _detailedSegment.Click += (_, _) => SetView(true);
        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Left or Key.Home: SetView(false); e.Handled = true; break;
                case Key.Right or Key.End: SetView(true); e.Handled = true; break;
            }
        };
        SetView(false, persist: false);
    }

    internal event EventHandler? ViewChanged;
    internal string PageKey { get; set; }
    internal bool IsDetailed => _detailedSelected;
    internal ToggleButton SimpleSegment => _simpleSegment;
    internal ToggleButton DetailedSegment => _detailedSegment;
    internal bool SlideAnimationApplied { get; private set; }
    internal double ThumbTarget => _detailedSelected ? _segmentWidth : 0;
    internal double ThumbOffset => _slide.X;

    internal void SetView(bool detailed, bool persist = true)
    {
        var changed = _detailedSelected != detailed;
        _detailedSelected = detailed;
        _simpleSegment.IsChecked = !detailed;
        _detailedSegment.IsChecked = detailed;
        // Resource references stay theme-reactive; the selected segment is the one that
        // carries the selected ink over the sliding background.
        _simpleSegment.SetResourceReference(Control.ForegroundProperty, detailed ? "MutedInk" : "SelectedInk");
        _detailedSegment.SetResourceReference(Control.ForegroundProperty, detailed ? "SelectedInk" : "MutedInk");
        SlideAnimationApplied = false;
        var target = ThumbTarget;
        if (AnimationsEnabled)
        {
            SlideAnimationApplied = true;
            _slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(target, SlideDuration) { FillBehavior = FillBehavior.HoldEnd });
        }
        else
        {
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _slide.X = target;
        }
        if (changed && persist) ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private ToggleButton Segment(string icon, string name, string tooltip)
    {
        var segment = new ToggleButton
        {
            Content = SharedPresentation.Glyph(icon, 16), Style = (Style)System.Windows.Application.Current.FindResource("SegmentToggle"),
            Focusable = true, ToolTip = tooltip, Margin = new Thickness(1, 0, 1, 0)
        };
        AutomationProperties.SetName(segment, name);
        AutomationProperties.SetHelpText(segment, tooltip);
        return segment;
    }

}

// One details model shared by the sidebar and the expanded dialog. The same content
// element is moved between hosts, so edits, selection and scroll identity survive and
// closing the dialog returns the element to the sidebar and restores focus.
internal sealed class DetailsHost
{
    private readonly ContentPresenter _sidebarHost;
    private readonly Func<FrameworkElement, DetailsDialog> _dialogFactory;
    private readonly ScrollViewer? _scroll;
    private DetailsDialog? _dialog;
    private EventHandler? _dialogClosed;
    private double _sidebarOffset;
    private readonly List<(FrameworkElement Control, Visibility Restored)> _sidebarOnly = [];

    // The shared details model is presented through FrameworkElement.DataContext, so the
    // host contract is FrameworkElement rather than the narrower UIElement.
    internal DetailsHost(FrameworkElement content, ContentPresenter sidebarHost,
        Func<FrameworkElement, DetailsDialog>? dialogFactory = null)
    {
        Content = content;
        _sidebarHost = sidebarHost;
        _scroll = content as ScrollViewer ?? (content as ContentControl)?.Content as ScrollViewer;
        _dialogFactory = dialogFactory ?? (element => new DetailsDialog(element));
        _sidebarHost.Content = content;
    }

    internal FrameworkElement Content { get; }
    internal bool Expanded => _dialog is not null;
    internal DetailsDialog? Dialog => _dialog;
    internal IInputElement? LastFocusTarget { get; private set; }
    internal event EventHandler? ExpandedChanged;

    internal void Present(object model) => Content.DataContext = model;

    /// <summary>
    /// Registers the controls that belong to the sidebar only. While the details are expanded they are
    /// hidden; returning to the sidebar restores exactly the visibility each control had when it was
    /// registered, so a control that is already collapsed - or hidden for its own reason - stays that
    /// way instead of being revealed by the return. A new registration releases the previous
    /// references and applies the current expanded state immediately.
    /// </summary>
    internal void RegisterSidebarOnly(params FrameworkElement[] controls)
    {
        _sidebarOnly.Clear();
        foreach (var control in controls)
            if (control is not null) _sidebarOnly.Add((control, control.Visibility));
        ApplySidebarOnly();
    }

    private void ApplySidebarOnly()
    {
        foreach (var (control, restored) in _sidebarOnly)
            control.Visibility = _dialog is null ? restored : Visibility.Collapsed;
    }

    internal void Expand(Window? owner = null)
    {
        if (_dialog is not null) return;
        _sidebarOffset = _scroll?.VerticalOffset ?? 0;
        LastFocusTarget = Keyboard.FocusedElement;
        _sidebarHost.Content = null;
        var dialog = _dialogFactory(Content);
        _dialog = dialog;
        ApplySidebarOnly();
        var targetOwner = owner ?? dialog.Owner;
        // A not-yet-shown owner cannot be assigned to a WPF Window.
        if (targetOwner is { IsLoaded: true }) dialog.Owner = targetOwner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _dialogClosed = (_, _) => Collapse();
        dialog.Closed += _dialogClosed;
        dialog.Show();
        dialog.Activate();
        dialog.FocusReturnControl();
        ExpandedChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void Collapse()
    {
        var dialog = _dialog;
        _dialog = null;
        ApplySidebarOnly();
        if (dialog is null) _sidebarHost.Content = Content;
        else
        {
            if (_dialogClosed is not null) dialog.Closed -= _dialogClosed;
            _dialogClosed = null;
            _sidebarHost.Content = dialog.Release() ?? Content;
            if (dialog.IsVisible) dialog.Close();
        }
        if (_scroll is not null) _scroll.ScrollToVerticalOffset(_sidebarOffset);
        ExpandedChanged?.Invoke(this, EventArgs.Empty);
        if (LastFocusTarget is IInputElement target) Keyboard.Focus(target);
    }
}

// UIV6-H1: one shared details action strip. Every details surface offers the same two commands in the
// same rendered order - expand first, close second - with one quiet icon-button style, the existing
// four-corner expand glyph, the existing close cross, the one expand tooltip and one accessible-name
// rule, so no page can drift into its own order, artwork or wording. Only the host callbacks differ;
// the caller states the subject its own names are built from, keeping the names an already accepted
// check publishes exact.
internal static class DetailsChrome
{
    /// <summary>The one tooltip every details opener carries.</summary>
    internal const string ExpandTooltip = "Open these details in a larger window";

    /// <summary>The shared expand command: the four-corner glyph, the tooltip and the name rule.</summary>
    internal static Button ExpandButton(string subject, RoutedEventHandler expand, string? expandElementName = null)
    {
        var button = DetailsIconButton("Expand " + subject, "expand", ExpandTooltip, expand);
        if (!string.IsNullOrWhiteSpace(expandElementName)) button.Name = expandElementName;
        return button;
    }

    /// <summary>The one details action strip: expand first, close second, in a single horizontal row.</summary>
    internal static StackPanel ActionStrip(string subject, RoutedEventHandler expand, RoutedEventHandler close,
        string? expandElementName = null)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        strip.Children.Add(ExpandButton(subject, expand, expandElementName));
        strip.Children.Add(DetailsIconButton("Close " + subject, "close", "Close " + subject, close));
        return strip;
    }

    private static Button DetailsIconButton(string accessibleName, string glyph, string tooltip,
        RoutedEventHandler action)
    {
        var button = WorkspaceVisuals.Button(accessibleName, action, quiet: true);
        button.Content = SharedPresentation.Glyph(glyph, 18);
        button.Width = 34;
        button.Height = 34;
        button.Padding = new Thickness(7);
        AutomationProperties.SetName(button, accessibleName);
        button.ToolTip = tooltip;
        return button;
    }
}

internal sealed class DetailsDialog : Window
{
    private readonly ContentControl _host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private Button? _returnButton;
    internal void FocusReturnControl() => _returnButton?.Focus();

    internal DetailsDialog(FrameworkElement content)
    {
        Title = "Details";
        Width = 640; Height = 680; MinWidth = 420; MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 48, ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(10), GlassFrameThickness = new Thickness(0)
        });
        SetResourceReference(BackgroundProperty, "Surface");
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var caption = new Border { BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(10, 10, 0, 0) };
        caption.SetResourceReference(Border.BackgroundProperty, "RaisedSurface");
        caption.SetResourceReference(Border.BorderBrushProperty, "Border");
        var captionRow = new DockPanel { LastChildFill = true, Margin = new Thickness(18, 0, 8, 0) };
        var returnButton = new Button { Content = CommandIcons.Icon("close"), Width = 36, Height = 34,
            MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(0), ToolTip = "Return to sidebar" };
        _returnButton = returnButton;
        AutomationProperties.SetName(returnButton, "Return to sidebar");
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(returnButton, true);
        returnButton.Click += (_, _) => Close();
        DockPanel.SetDock(returnButton, Dock.Right);
        captionRow.Children.Add(returnButton);
        captionRow.Children.Add(new TextBlock { Text = "Details", FontSize = 17,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        caption.Child = captionRow;
        layout.Children.Add(caption);
        var body = new Border { Padding = new Thickness(20), Child = _host };
        Grid.SetRow(body, 1); layout.Children.Add(body);
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = layout };
        frame.SetResourceReference(Border.BorderBrushProperty, "Border");
        Content = frame;
        _host.Content = content;
        SourceInitialized += (_, _) =>
        {
            Icon = Owner?.Icon;
            MaxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width - 24);
            MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24);
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    internal FrameworkElement? Release()
    {
        var content = _host.Content as FrameworkElement;
        _host.Content = null;
        return content;
    }

    internal FrameworkElement? HostedContent => _host.Content as FrameworkElement;
}
