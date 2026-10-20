using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Control = System.Windows.Controls.Control;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Panel = System.Windows.Controls.Panel;
using MenuItem = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace Massdex.SyncShark;

// One scoped command lifetime serves buttons, menu commands and toggles.
internal sealed class ActionFeedback : IDisposable
{
    private static readonly ConditionalWeakTable<Control, ActionFeedback> Active = new();
    private static readonly AsyncLocal<ActionFeedback?> Current = new();
    private readonly Control? _control;
    private readonly DependencyProperty? _property;
    private readonly DependencyPropertyDescriptor? _descriptor;
    private readonly ActionFeedback? _previous;
    private object? _content;
    private readonly object? _tooltip;
    private readonly object? _busyContent;
    private readonly RotateTransform? _spin;
    private bool _restoring;
    private string? _lastResult;
    public bool Entered { get; }

    private ActionFeedback(object sender)
    {
        (_control, _property) = sender switch
        {
            ButtonBase button => ((Control?)button, (DependencyProperty?)ContentControl.ContentProperty),
            MenuItem menu => ((Control?)menu, (DependencyProperty?)HeaderedItemsControl.HeaderProperty),
            _ => ((Control?)null, (DependencyProperty?)null)
        };
        if (_control is null) { Entered = true; return; }
        if (Active.TryGetValue(_control, out _)) return;
        Entered = true;
        Active.Add(_control, this);
        _previous = Current.Value;
        Current.Value = this;
        _content = _control.GetValue(_property!);
        _tooltip = _control.ToolTip;
        var label = AutomationProperties.GetName(_control);
        if (string.IsNullOrWhiteSpace(label)) label = Label(_content);
        var spin = _spin = new RotateTransform();
        var glyph = new CommandGlyph { IconName = "sync", Width = 16, Height = 16,
            RenderTransform = spin, RenderTransformOrigin = new Point(.5, .5), Margin = new Thickness(0, 0, 8, 0) };
        spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
            { RepeatBehavior = RepeatBehavior.Forever });
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(glyph);
        panel.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(label) ? "Working…" : label + "…",
            VerticalAlignment = VerticalAlignment.Center });
        _busyContent = panel;
        _control.SetCurrentValue(_property!, panel);
        // Timed presentation refreshes cannot erase an in-flight command.
        _descriptor = DependencyPropertyDescriptor.FromProperty(_property!, _control.GetType());
        _descriptor?.AddValueChanged(_control, PreserveBusyContent);
        _control.ToolTip = "This action is running. Repeated activation is ignored until it finishes.";
        ToolTipService.SetShowOnDisabled(_control, true);
        AutomationProperties.SetItemStatus(_control, "In progress");
    }

    private static string Label(object? content) => content switch
    {
        string text => text.Replace("_", ""),
        TextBlock text => text.Text,
        Panel panel => string.Join(" ", panel.Children.OfType<TextBlock>().Select(text => text.Text)),
        _ => ""
    };

    public static ActionFeedback Begin(object sender) => new(sender);
    internal static void Report(string result)
    {
        if (Current.Value is { Entered: true } active)
            active._lastResult = SyncActivityText.Safe(result);
    }

    private void PreserveBusyContent(object? sender, EventArgs args)
    {
        if (_control is null || _restoring || ReferenceEquals(_control.GetValue(_property!), _busyContent)) return;
        _content = _control.GetValue(_property!);
        _restoring = true;
        try { _control.SetCurrentValue(_property!, _busyContent); }
        finally { _restoring = false; }
    }

    public void Dispose()
    {
        if (!Entered || _control is null) return;
        _descriptor?.RemoveValueChanged(_control, PreserveBusyContent);
        _spin?.BeginAnimation(RotateTransform.AngleProperty, null);
        Active.Remove(_control);
        if (ReferenceEquals(_control.GetValue(_property!), _busyContent))
            _control.SetCurrentValue(_property!, _content);
        _control.ToolTip = _lastResult ?? _tooltip;
        AutomationProperties.SetItemStatus(_control, _lastResult ?? "");
        if (_lastResult is not null) AutomationProperties.SetHelpText(_control, _lastResult);
        // A terminal result must not replace the reason an action is still unavailable.
        ActionAvailability.Refresh(_control);
        Current.Value = _previous;
    }
}
