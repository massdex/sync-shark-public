using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Brushes = System.Windows.Media.Brushes;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using static Massdex.SyncShark.WorkspaceVisuals;

namespace Massdex.SyncShark;

internal sealed class TrayFlyoutWindow : Window
{
    private readonly MainWindow _ownerWindow;
    private readonly TextBlock _status, _detail, _pair;
    private readonly TextBlock _currentDetail;
    private readonly Expander _currentDisclosure;
    private readonly Button _pairLink;
    private readonly Button _pause, _pending;
    private readonly Button _accounts, _pauseMore;
    private readonly DataGrid _feed;
    private readonly ProgressBar _progress;
    private readonly ObservableCollection<ActivityRow> _rows = [];
    private readonly TextBlock _empty;
    private readonly StackPanel _attention = new();
    private readonly System.Windows.Controls.TabControl _tabs;
    private int _tab;
    private SummaryActivityFilter _summaryFilter = SummaryActivityFilter.Files;
    private string? _accountFilter;
    private int _scopeGeneration;
    private TrayAccountSnapshot? _accountSnapshot;
    private string? _scopeError;
    internal TrayFlyoutWindow(MainWindow owner)
    {
        _ownerWindow = owner;
        WorkspaceInput.Enable(this);
        Title = "Sync Shark Tray"; Width = 414; Height = 620;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true;
        SetResourceReference(BackgroundProperty, "Surface");
        var layout = new Grid { Margin = new Thickness(18) };
        for (var i = 0; i < 3; i++) layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var open = Button("", (_, _) => { owner.ShowFromBackground(); Hide(); }, quiet: true);
        open.Content = CommandIcons.Icon("external"); open.ToolTip = "Open Sync Shark";
        System.Windows.Automation.AutomationProperties.SetName(open, "Open Sync Shark");
        DockPanel.SetDock(open, Dock.Right); header.Children.Add(open);
        var fin = new System.Windows.Controls.Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/SyncShark;component/Assets/SyncShark-64.png")), Width = 26, Height = 26, Margin = new Thickness(0,0,9,0) };
        header.Children.Add(fin); header.Children.Add(Text("Sync Shark", 16, true));
        layout.Children.Add(header);
        _tabs = new System.Windows.Controls.TabControl { ItemContainerStyle = WorkspaceTabStyle(),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 8) };
        foreach (var label in new[] { "Activity", "Needs Attention" })
        {
            _tabs.Items.Add(new TabItem { Header = CommandIcons.Label(label, label == "Activity" ? "activity" : "attention") });
        }
        _tabs.SelectedIndex = 0;
        Grid.SetRow(_tabs, 1); layout.Children.Add(_tabs);
        var status = new StackPanel();
        _status = Text("Checking", 17, true); status.Children.Add(_status);
        _pair = Text("", 12, brush: "FocusBrush");
        _pairLink = Button("Current Folder Pair", (_, _) => { owner.OpenRunningPair(); Hide(); }, true);
        _pairLink.Content = _pair; _pairLink.Padding = new Thickness(0); _pairLink.Margin = new Thickness(0,5,0,0);
        _pairLink.MinHeight = 18; _pairLink.Height = 20;
        _pairLink.HorizontalAlignment = HorizontalAlignment.Left; status.Children.Add(_pairLink);
        _detail = Text("Loading saved state…", 11, brush: "MutedInk"); _detail.Margin = new Thickness(0, 5, 0, 8); status.Children.Add(_detail);
        _progress = new ProgressBar { Height = 4, BorderThickness = new Thickness(0), IsIndeterminate = true };
        _progress.SetResourceReference(ForegroundProperty, "FocusBrush"); status.Children.Add(_progress);
        _currentDetail = Text("", 11, brush: "MutedInk");
        _currentDisclosure = new Expander { Header = CommandIcons.Label("Current Operation", "activity"), Content = _currentDetail, FontSize = 11, Margin = new Thickness(0,7,0,0) };
        status.Children.Add(_currentDisclosure);
        var statusCard = Card(status, 12); statusCard.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetRow(statusCard, 2); layout.Children.Add(statusCard);
        var activity = new Grid();
        activity.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        activity.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _feed = new DataGrid { ItemsSource = _rows };
        StyleFeed(_feed);
        _feed.RowHeight = 84; GroupFeed(_feed);
        ((DataGridTemplateColumn)_feed.Columns[0]).CellTemplate = ItemTemplate(compact: true);
        _feed.Columns[1].Width = 26; _feed.Columns[2].Width = 30;
        _feed.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (FindParent<Button>(e.OriginalSource as DependencyObject) is not null) return;
            if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is ActivityRow row)
                owner.OpenWorkspaceActivity(0, row);
        };
        _feed.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is not ActivityRow row) return;
            owner.OpenWorkspaceMenu(_feed, row); e.Handled = true;
        };
        _feed.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _feed.SelectedItem is ActivityRow row)
            { owner.OpenWorkspaceActivity(0, row); e.Handled = true; }
        };
        _feed.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            var button = FindParent<Button>(e.OriginalSource as DependencyObject);
            if (button?.DataContext is not ActivityRow row) return;
            if (button.Tag as string == "pair") { owner.OpenActivityPair(row); Hide(); e.Handled = true; return; }
            owner.OpenWorkspaceMenu(e.OriginalSource as UIElement ?? _feed, row); e.Handled = true;
        }));
        activity.Children.Add(_feed);
        _empty = Text("No recent actions", 13, brush: "MutedInk");
        _empty.HorizontalAlignment = HorizontalAlignment.Center; _empty.IsHitTestVisible = false;
        activity.Children.Add(_empty);
        activity.Children.Add(_attention);
        var summarySelector = SharedPresentation.SummarySelector(mode => { _summaryFilter = mode; Refresh(); });
        Grid.SetRow(summarySelector, 1); activity.Children.Add(summarySelector);
        _tabs.SelectionChanged += (_, _) => summarySelector.Visibility = _tabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(activity, 3); layout.Children.Add(activity);
        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _accounts = Button("Accounts", (_, e) =>
        {
            var menu = WorkspaceMenu(); menu.PlacementTarget = e.Source as UIElement;
            var choices = owner.TrayAccounts;
            var all = new MenuItem { Header = "All Accounts", IsCheckable = true, IsChecked = _accountFilter is null };
            all.Click += async (_, _) => await SelectAccountAsync(null); menu.Items.Add(all);
            foreach (var choice in choices)
            {
                var filter = new MenuItem { Header = choice.Label, IsCheckable = true, IsChecked = _accountFilter == choice.Id };
                filter.Click += async (_, _) => await SelectAccountAsync(choice.Id); menu.Items.Add(filter);
            }
            menu.IsOpen = true;
        }, quiet: true);
        _accounts.HorizontalContentAlignment = HorizontalAlignment.Left;
        _accounts.ToolTip = "Choose the account shown here. This does not change sync settings.";
        Grid.SetColumnSpan(_accounts, 2); footer.Children.Add(_accounts);
        _pause = AsyncButton("Pause Sync", async (sender, _) =>
        { await owner.HandleTrayPauseAsync((UIElement)sender, _accountFilter); Refresh(); }, true);
        _pause.HorizontalAlignment = HorizontalAlignment.Left;
        _pauseMore = Button("More Pause Options", (sender, _) => owner.OpenTrayPauseMenu((UIElement)sender, _accountFilter), true);
        _pauseMore.Content = CommandIcons.Icon("more");
        _pauseMore.Width = 34; _pauseMore.Padding = new Thickness(0);
        var pauseActions = new StackPanel { Orientation = Orientation.Horizontal };
        pauseActions.Children.Add(_pause); pauseActions.Children.Add(_pauseMore);
        Grid.SetRow(pauseActions, 1); footer.Children.Add(pauseActions);
        _pending = Button("View all activity ↗", (_, _) =>
        {
            owner.OpenTrayScope(_accountFilter, _tab == 1); Hide();
        }, quiet: true);
        Grid.SetColumn(_pending, 1); Grid.SetRow(_pending, 1); footer.Children.Add(_pending);
        var connections = Button("Connections", (_, _) => { owner.OpenWorkspaceConnection(); Hide(); }, true);
        var settings = Button("Settings", (_, _) => { owner.OpenWorkspaceSettings(); Hide(); }, true);
        Grid.SetRow(connections, 2); footer.Children.Add(connections);
        Grid.SetRow(settings, 2); Grid.SetColumn(settings, 1); footer.Children.Add(settings);
        Grid.SetRow(footer, 4); layout.Children.Add(footer);
        var frame = new Border { Child = layout, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Border");
        frame.SetResourceReference(Border.BackgroundProperty, "Surface");
        Content = frame;
        _tabs.SelectionChanged += (_, e) =>
        { if (ReferenceEquals(e.Source, _tabs)) { _tab = _tabs.SelectedIndex; Refresh(); } };
        owner.WorkspacePresentationChanged += OwnerChanged;
        Deactivated += (_, _) => Hide();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        Closed += (_, _) => owner.WorkspacePresentationChanged -= OwnerChanged;
    }

    private static T? FindParent<T>(DependencyObject? value) where T : DependencyObject
    {
        while (value is not null) { if (value is T result) return result; value = VisualTreeHelper.GetParent(value); }
        return null;
    }
    private void OwnerChanged(object? sender, EventArgs e) { if (IsVisible) Refresh(); }

    internal async Task SelectAccountAsync(string? accountId)
    {
        _accountFilter = accountId;
        _accountSnapshot = null; _scopeError = null;
        var generation = ++_scopeGeneration;
        Refresh();
        if (accountId is null) return;
        try
        {
            var snapshot = await _ownerWindow.LoadTrayAccountSnapshotAsync(accountId);
            if (generation != _scopeGeneration || accountId != _accountFilter) return;
            _accountSnapshot = snapshot;
        }
        catch (Exception error)
        { if (generation == _scopeGeneration) _scopeError = "This account's activity could not be loaded: " + error.Message; }
        if (generation == _scopeGeneration) Refresh();
    }

    internal void ToggleAtTray()
    {
        if (IsVisible) { Hide(); return; }
        Refresh();
        if (_accountFilter is not null) _ = SelectAccountAsync(_accountFilter);
        var cursor = System.Windows.Forms.Cursor.Position;
        var work = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var scale = 1d;
        var monitor = MonitorFromPoint(new NativePoint { X = cursor.X, Y = cursor.Y }, 2);
        if (GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0) scale = dpi / 96d;
        Width = Math.Min(414, work.Width / scale - 24);
        Height = Math.Min(620, work.Height / scale - 24);
        Left = work.Right / scale - Width - 12;
        Top = work.Bottom / scale - Height - 12;
        Show(); Activate();
    }

    private void Refresh()
    {
        // SS-D12: one presentation scope for this whole synchronous tray render, so the account list, the
        // scoped status, the attention list and the per-pair labels below share one validated snapshot
        // instead of re-reading the stores through each of them.
        using var presentation = _ownerWindow.OpenPresentationScope();
        var accounts = _ownerWindow.TrayAccounts;
        if (_accountFilter is not null && !accounts.Any(account => account.Id == _accountFilter))
        { _accountFilter = null; _accountSnapshot = null; _scopeError = null; ++_scopeGeneration; }
        var scope = _ownerWindow.TrayScope(_accountFilter, _accountSnapshot?.Pending);
        _status.Text = scope.Status;
        _pair.Text = scope.Pair;
        _pairLink.Visibility = _pair.Text.Length > 0 && scope.Running ? Visibility.Visible : Visibility.Collapsed;
        _detail.Text = _scopeError ?? (scope.Running ? _ownerWindow.TrayTiming : scope.Detail);
        _detail.ToolTip = _scopeError ?? scope.Detail;
        _accounts.Content = new TextBlock { Text = scope.Label + " ▾", TextTrimming = TextTrimming.CharacterEllipsis };
        System.Windows.Automation.AutomationProperties.SetName(_accounts, "Accounts: " + scope.Label);
        _accounts.ToolTip = scope.Label + ". Select an account to view; no sync settings change.";
        _pause.Content = CommandIcons.Label(scope.Paused ? "Resume Sync" : "Pause Sync", scope.Paused ? "play" : "pause");
        _pause.IsEnabled = !scope.Paused || scope.OwnPause;
        _pause.ToolTip = scope.Paused && !scope.OwnPause ? scope.Detail :
            scope.Label + ": " + (scope.Paused ? "Resume this scope only; other pauses remain." : "Choose how long to pause this scope.");
        System.Windows.Automation.AutomationProperties.SetName(_pause, scope.Paused ? "Resume Sync" : "Pause Sync");
        _pauseMore.ToolTip = "More pause options for " + scope.Label;
        _progress.Visibility = scope.Running && !scope.Paused ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = _ownerWindow.TrayProgressIndeterminate;
        _progress.Maximum = _ownerWindow.TrayProgressMaximum; _progress.Value = _ownerWindow.TrayProgressValue;
        _currentDisclosure.Visibility = scope.Running ? Visibility.Visible : Visibility.Collapsed;
        _currentDetail.Text = _ownerWindow.TrayProgressDetails;
        var selected = SharedPresentation.SummaryRows(_ownerWindow.TrayActivity.Concat(_accountSnapshot?.Rows ?? []),
            _accountFilter, _summaryFilter, 12);
        if (!_rows.SequenceEqual(selected)) { _rows.Clear(); foreach (var row in selected) _rows.Add(row); }
        _feed.Visibility = _tab == 1 ? Visibility.Collapsed : Visibility.Visible;
        _attention.Visibility = _tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        var attention = _ownerWindow.TrayAttentionCases.Where(item => _ownerWindow.TrayMatchesAccount(item.Pair.Id, _accountFilter)).ToArray();
        if (_tab == 1)
        {
            _attention.Children.Clear();
            foreach (var item in attention.Take(3))
            {
                var row = new DockPanel();
                var glyph = SharedPresentation.AttentionIcon.Create(item.InternalIcon, item.Title, 22);
                glyph.Margin = new Thickness(0, 0, 10, 0); row.Children.Add(glyph);
                var words = new StackPanel(); words.Children.Add(Text(item.Title, 13, true));
                words.Children.Add(Text(item.Description, 12, brush: "MutedInk"));
                words.Children.Add(Text(item.Pair.Name, 11, brush: "FocusBrush"));
                if (accounts.Count > 1) words.Children.Add(Text(_ownerWindow.TrayLabelForPair(item.Pair.Id), 11, brush: "MutedInk"));
                row.Children.Add(words);
                var action = Button(item.Title, (_, _) => { _ownerWindow.ShowFromBackground(); item.Open(); }, quiet: true);
                action.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                action.Content = row; action.Margin = new Thickness(0, 6, 0, 8); _attention.Children.Add(action);
            }
        }
        _empty.Text = _tab == 1 ? "Nothing needs your attention." : "No recent actions yet";
        _empty.Visibility = (_tab == 1 ? attention.Length == 0 : _rows.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        _pending.Content = CommandIcons.Label(_tab == 1 ? "View All" : "All Activity", "external");
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
