using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Massdex.SyncShark;

// Route high-resolution wheel input to the innermost scrollable pane, chaining at its boundary.
// Offsets stay fractional; small precision-touchpad deltas must not be rounded to whole rows.
internal static class WorkspaceInput
{
    private static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(WorkspaceInput), new PropertyMetadata(false));

    internal static void Enable(Window window)
    {
        if ((bool)window.GetValue(EnabledProperty)) return;
        window.SetValue(EnabledProperty, true);
        window.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(RouteWheel), true);
        HwndSource? source = null;
        IntPtr HorizontalWheel(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != 0x020E || Keyboard.Modifiers != ModifierKeys.None) return IntPtr.Zero;
            var delta = unchecked((short)(wParam.ToInt64() >> 16));
            var packed = lParam.ToInt64();
            var screen = new System.Windows.Point(unchecked((short)packed), unchecked((short)(packed >> 16)));
            var hit = window.InputHitTest(window.PointFromScreen(screen)) as DependencyObject;
            handled = ScrollFrom(hit, delta, horizontal: true);
            return IntPtr.Zero;
        }
        void Attach()
        {
            if (source is not null) return;
            source = PresentationSource.FromVisual(window) as HwndSource;
            source?.AddHook(HorizontalWheel);
        }
        window.SourceInitialized += (_, _) => Attach();
        window.Closed += (_, _) => source?.RemoveHook(HorizontalWheel);
        Attach();
    }

    private static void RouteWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = ScrollFrom(e.OriginalSource as DependencyObject, e.Delta, horizontal: false);
    }

    internal static bool CloseOpenPicker(DependencyObject root)
    {
        if (root is DatePicker { IsDropDownOpen: true } date) { date.IsDropDownOpen = false; return true; }
        if (root is System.Windows.Controls.ComboBox { IsDropDownOpen: true } choice) { choice.IsDropDownOpen = false; return true; }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (CloseOpenPicker(VisualTreeHelper.GetChild(root, index))) return true;
        return false;
    }

    internal static bool ScrollFrom(DependencyObject? origin, int delta, bool horizontal)
    {
        if (delta == 0) return false;
        for (var current = origin; current is not null; current = Parent(current))
        {
            if (current is not ScrollViewer scroller) continue;
            var extent = horizontal ? scroller.ScrollableWidth : scroller.ScrollableHeight;
            var offset = horizontal ? scroller.HorizontalOffset : scroller.VerticalOffset;
            if (extent <= 0) continue;
            var forward = horizontal ? delta > 0 : delta < 0;
            if (forward ? offset >= extent - 0.01 : offset <= 0.01)
                continue;
            var distance = SystemParameters.WheelScrollLines < 0 ? (horizontal ? scroller.ViewportWidth : scroller.ViewportHeight)
                : Math.Max(1, SystemParameters.WheelScrollLines) * 16d;
            var next = Math.Clamp(offset + (horizontal ? delta : -delta) / 120d * distance, 0, extent);
            if (horizontal) scroller.ScrollToHorizontalOffset(next);
            else scroller.ScrollToVerticalOffset(next);
            return true;
        }
        return false;
    }

    private static DependencyObject? Parent(DependencyObject item) => item switch
    {
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(item),
        FrameworkContentElement content => content.Parent,
        _ => LogicalTreeHelper.GetParent(item)
    };
}

public sealed class ToolTipContentSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is string ? TextTemplate : null;
}
