using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;

namespace Massdex.SyncShark;

/// <summary>Chooses one item of a "Held by your choice" notice for a per-item release or stop-syncing choice.</summary>
internal static class HeldItemPicker
{
    public static string? Pick(Window owner, string title, string actionText, IReadOnlyList<string> paths)
    {
        var list = new ListBox { ItemsSource = paths, MinHeight = 160, MaxHeight = 420, Margin = new Thickness(0, 8, 0, 8) };
        var action = new Button { Content = actionText, IsDefault = true, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 96 };
        list.SelectionChanged += (_, _) => action.IsEnabled = list.SelectedItem is string;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        buttons.Children.Add(action);
        buttons.Children.Add(cancel);
        var body = new StackPanel { Margin = new Thickness(16) };
        body.Children.Add(new TextBlock
        {
            Text = paths.Count == 0 ? "Nothing is held by your choice any more. Refresh notices." : "Choose one held item:",
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(list);
        body.Children.Add(buttons);
        var window = new Window
        {
            Owner = owner, Title = title, Content = body, Width = 560, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
        };
        action.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true ? list.SelectedItem as string : null;
    }
}
