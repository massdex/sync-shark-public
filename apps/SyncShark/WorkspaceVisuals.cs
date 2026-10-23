using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Panel = System.Windows.Controls.Panel;
using Control = System.Windows.Controls.Control;
using Orientation = System.Windows.Controls.Orientation;
using Brushes = System.Windows.Media.Brushes;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace Massdex.SyncShark;

internal static class WorkspaceVisuals
{
    // Overview text is not the diagnostic record. Keep the actual reason/counts
    // readable without letting retained stack traces consume the entire page.
    // Callers retain the original record and its existing exact-run drilldown.
    internal static string ProblemPreview(string detail)
    {
        const int limit = 360;
        var trace = detail.IndexOf(" [application:", StringComparison.Ordinal);
        var text = trace >= 0 ? detail[..trace] : detail;
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length <= limit && trace < 0) return text;
        if (text.Length > limit)
        {
            var cut = text.LastIndexOf(' ', limit, limit);
            if (cut < limit / 2) cut = limit;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            text = text[..cut].TrimEnd();
        }
        return text + " … Full details are in the operation log.";
    }

    internal static TextBlock Text(string text, double size = 14, bool strong = false, string brush = "Ink",
        Thickness? margin = null)
    {
        var value = new TextBlock { Text = text, FontSize = size,
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Margin = margin ?? new Thickness(0) };
        value.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return value;
    }

    // UI398-4: the default command caption keeps its established readability pass, which is right for a
    // command sentence. A caller that presents a recorded literal - a real endpoint location, for example -
    // opts into verbatim text instead, so its own casing, punctuation and surrounding characters reach the
    // user exactly as they were recorded. The icon, accessible name and tooltip behaviour are identical in
    // both forms, so opting in never changes what the click does.
    internal static Button Button(string text, RoutedEventHandler action, bool quiet = false, bool verbatim = false,
        string? explanation = null)
    {
        var clean = text.Trim(' ', '↗', '→', '←');
        var caption = verbatim ? text : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(clean);
        var value = new Button { Content = text == "⋯" ? CommandIcons.Icon("more") : CommandIcons.Label(caption), Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0), BorderThickness = new Thickness(0) };
        if (quiet) { value.Background = Brushes.Transparent; value.SetResourceReference(Control.ForegroundProperty, "FocusBrush"); }
        value.Click += action;
        System.Windows.Automation.AutomationProperties.SetName(value, text);
        value.ToolTip = explanation ?? text;
        if (explanation is not null)
            System.Windows.Automation.AutomationProperties.SetHelpText(value, explanation);
        return value;
    }

    internal static Button AsyncButton(string text, Func<object, RoutedEventArgs, Task> action,
        bool quiet = false, bool verbatim = false, string? explanation = null) => Button(text, async (sender, args) =>
    {
        using var feedback = ActionFeedback.Begin(sender);
        if (!feedback.Entered) return;
        try { await action(sender, args); }
        catch (Exception error)
        {
            var message = text + (error is OperationCanceledException ? " cancelled. " : " did not complete. ") +
                SyncActivityText.Failure(error);
            ActionFeedback.Report(message);
            if (sender is DependencyObject control && Window.GetWindow(control) is MainWindow owner)
                await owner.ReportActionExceptionAsync(text, message);
        }
    }, quiet, verbatim, explanation);

    internal static Border Card(UIElement content, double padding = 20)
    {
        var value = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(padding), Child = content };
        value.SetResourceReference(Border.BackgroundProperty, "RaisedSurface");
        return value;
    }

    internal static FrameworkElement SearchField(System.Windows.Controls.TextBox input, string placeholder)
    {
        var box = new Grid();
        input.Padding = new Thickness(34, 7, 9, 7);
        box.Children.Add(input);
        var prompt = Text(placeholder, 13, brush: "MutedInk");
        prompt.Margin = new Thickness(35, 0, 10, 0);
        prompt.IsHitTestVisible = false;
        prompt.TextTrimming = TextTrimming.CharacterEllipsis;
        prompt.TextWrapping = TextWrapping.NoWrap;
        void RefreshPrompt() => prompt.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        input.TextChanged += (_, _) => RefreshPrompt(); RefreshPrompt(); box.Children.Add(prompt);
        var icon = new CommandGlyph { IconName = "search", Width = 16, Height = 16,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(10, 0, 0, 0), IsHitTestVisible = false };
        icon.SetResourceReference(Control.ForegroundProperty, "MutedInk"); box.Children.Add(icon);
        return box;
    }

    // These pilot menus are single-level action lists, with no empty check/icon gutter.
    internal static ContextMenu WorkspaceMenu()
    {
        var menu = new ContextMenu { Padding = new Thickness(6), MinWidth = 190 };
        menu.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ContextMenu">
              <Border Background="{DynamicResource RaisedSurface}" BorderBrush="{DynamicResource Border}"
                      BorderThickness="1" CornerRadius="8" Padding="{TemplateBinding Padding}">
                <ScrollViewer CanContentScroll="True" VerticalScrollBarVisibility="Auto"
                              HorizontalScrollBarVisibility="Disabled"><ItemsPresenter/></ScrollViewer>
              </Border>
            </ControlTemplate>
            """);
        var actionStyle = (Style)System.Windows.Markup.XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="MenuItem">
              <Setter Property="Foreground" Value="{DynamicResource Ink}"/>
              <Setter Property="Padding" Value="12,10"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="MenuItem">
                <Border x:Name="row" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" CornerRadius="6" Background="Transparent" Padding="{TemplateBinding Padding}">
                  <ContentPresenter ContentSource="Header" RecognizesAccessKey="True"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="row" Property="Background" Value="{DynamicResource SelectedSurface}"/><Setter Property="Foreground" Value="{DynamicResource SelectedInk}"/></Trigger>
                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
        menu.Resources[typeof(MenuItem)] = actionStyle;
        menu.ItemContainerStyleSelector = new MenuActionStyleSelector(actionStyle);
        return menu;
    }

    internal static T Detach<T>(T value) where T : FrameworkElement
    {
        switch (value.Parent)
        {
            case Panel panel: panel.Children.Remove(value); break;
            case Decorator decorator: decorator.Child = null; break;
            case ContentControl content: content.Content = null; break;
        }
        return value;
    }

    // All compact feeds share the same full-height, trailing time column. Keeping it outside
    // the metadata row prevents Home, tray and Storage from pinning it to the row's bottom.
    // Activity has a real Time column and requests the same content without this element.
    internal static DataTemplate ItemTemplate(bool compact = false, bool trailingTime = true, bool metadataLink = true,
        string summaryPath = "ShortAction", string titlePath = "DisplayName")
    {
        const string template = """
          <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:local="clr-namespace:Massdex.SyncShark;assembly=SyncShark">
            <DockPanel Margin="8,6,4,6">
              <local:ShellTypeImage Width="28" Height="28" Margin="0,0,12,0" VerticalAlignment="Center"/>
              <!--TRAILINGTIME-->
              <Grid VerticalAlignment="Center">
                <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                <TextBlock Text="{Binding DisplayName}" FontSize="14" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" ToolTip="{Binding Path}"/>
                <TextBlock Grid.Row="1" Text="{Binding ShortAction}" FontSize="12" Foreground="{DynamicResource MutedInk}" Margin="0,3,0,0" TextTrimming="CharacterEllipsis" ToolTip="{Binding ShortAction}"/>
                <DockPanel Grid.Row="2" Margin="0,3,0,0" LastChildFill="True">
                  <Button Tag="pair" Background="Transparent" BorderThickness="0" Padding="0" Margin="0" MinHeight="0" MaxWidth="160" HorizontalAlignment="Left" HorizontalContentAlignment="Left" ToolTip="{Binding PairName}">
                    <TextBlock Text="{Binding PairName}" FontSize="11" Foreground="{DynamicResource FocusBrush}" TextTrimming="CharacterEllipsis"/>
                  </Button>
                </DockPanel>
              </Grid>
            </DockPanel>
          </DataTemplate>
          """;
        var body = trailingTime
            ? template.Replace("<!--TRAILINGTIME-->",
                "<TextBlock DockPanel.Dock=\"Right\" Text=\"{Binding TimeLabel}\" FontSize=\"11\" Foreground=\"{DynamicResource MutedInk}\" Margin=\"12,0,0,0\" VerticalAlignment=\"Center\" ToolTip=\"{Binding FullTimestamp}\" AutomationProperties.Name=\"{Binding TimeAccessibleLabel}\" AutomationProperties.HelpText=\"{Binding FullTimestamp}\"/>")
            : template.Replace("<!--TRAILINGTIME-->", string.Empty);
        if (!metadataLink)
            body = body.Replace("<Button Tag=\"pair\" Background=\"Transparent\" BorderThickness=\"0\" Padding=\"0\" Margin=\"0\" MinHeight=\"0\" MaxWidth=\"160\" HorizontalAlignment=\"Left\" HorizontalContentAlignment=\"Left\" ToolTip=\"{Binding PairName}\">",
                "<ContentControl HorizontalAlignment=\"Stretch\" ToolTip=\"{Binding PairName}\">")
                .Replace("</Button>", "</ContentControl>");
        body = body.Replace("{Binding ShortAction}", "{Binding " + summaryPath + "}");
        body = body.Replace("{Binding DisplayName}", "{Binding " + titlePath + "}");
        return (DataTemplate)System.Windows.Markup.XamlReader.Parse(compact
            ? body.Replace("FontSize=\"14\"", "FontSize=\"13\"")
            : body);
    }

    // UI09: the no-changes control is a styled pressed toggle. It must never be a native
    // CheckBox, and it always carries its explanation as both tooltip and accessible help. It
    // reuses the shared keyed ToggleButton presentation from App.xaml instead of a second design,
    // so the pressed state uses the one theme-aware selected surface and selected ink.
    internal static ToggleButton PressedToggle(string text, string explanation)
    {
        var toggle = new ToggleButton { Content = text, Margin = new Thickness(0),
            ToolTip = explanation, Focusable = true, IsThreeState = false,
            VerticalAlignment = VerticalAlignment.Center };
        if (System.Windows.Application.Current?.TryFindResource("QuietToggleButton") is Style quiet)
            toggle.Style = quiet;
        System.Windows.Automation.AutomationProperties.SetName(toggle, text);
        System.Windows.Automation.AutomationProperties.SetHelpText(toggle, explanation);
        return toggle;
    }

    internal static void GroupFeed(DataGrid grid)
    {
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        if (!view.CanGroup || view.GroupDescriptions.Count != 0) return;
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ActivityRow.DateGroup)));
        grid.GroupStyle.Add(new GroupStyle { HeaderTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
          <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <TextBlock Text="{Binding Name}" Foreground="{DynamicResource MutedInk}" FontSize="12" FontWeight="SemiBold" Margin="8,14,0,6"/>
          </DataTemplate>
          """) });
        VirtualizingPanel.SetIsVirtualizingWhenGrouping(grid, true);
    }

    internal static DataTemplate LegacyItemTemplate()
    {
        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 13, 8, 13));
        var icon = new FrameworkElementFactory(typeof(ShellTypeImage));
        icon.SetValue(FrameworkElement.WidthProperty, 28d);
        icon.SetValue(FrameworkElement.HeightProperty, 28d);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 14, 0));
        icon.SetValue(DockPanel.DockProperty, Dock.Left);
        root.AppendChild(icon);
        var words = new FrameworkElementFactory(typeof(StackPanel));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ActivityRow.DisplayName)));
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.FontSizeProperty, 14d);
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        words.AppendChild(name);
        var subtitle = new FrameworkElementFactory(typeof(TextBlock));
        subtitle.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ActivityRow.Subtitle)));
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
        subtitle.SetValue(TextBlock.FontSizeProperty, 12d);
        subtitle.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 5, 0, 0));
        subtitle.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        words.AppendChild(subtitle);
        root.AppendChild(words);
        return new DataTemplate { VisualTree = root };
    }

    internal static DataTemplate StatusTemplate()
    {
        var root = new FrameworkElementFactory(typeof(CommandGlyph));
        root.SetBinding(CommandGlyph.IconNameProperty, new System.Windows.Data.Binding(nameof(ActivityRow.StatusGlyph))
            { Converter = new ActivityStatusIconConverter() });
        root.SetBinding(Control.ForegroundProperty, new System.Windows.Data.Binding(nameof(ActivityRow.StatusGlyph))
            { Converter = new ActivityStatusBrushConverter() });
        root.SetValue(FrameworkElement.WidthProperty, 18d);
        root.SetValue(FrameworkElement.HeightProperty, 18d);
        root.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        root.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        return new DataTemplate { VisualTree = root };
    }

    // UI06: the activity table carries a real Time column immediately before Size. The shared
    // row template then omits its own trailing time so the value is not shown twice, while the
    // Home and tray feeds keep the trailing time position.
    internal static void StyleFeed(DataGrid grid, bool activityColumns = false, bool fitNameColumn = true)
    {
        grid.AutoGenerateColumns = false;
        grid.IsReadOnly = true;
        grid.CanUserAddRows = false;
        grid.CanUserDeleteRows = false;
        grid.CanUserResizeRows = false;
        grid.HeadersVisibility = DataGridHeadersVisibility.None;
        grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        grid.BorderThickness = new Thickness(0);
        grid.Background = Brushes.Transparent;
        grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Divider");
        grid.SelectionMode = DataGridSelectionMode.Single;
        grid.EnableRowVirtualization = true;
        VirtualizingPanel.SetScrollUnit(grid, ScrollUnit.Pixel);
        grid.RowHeight = 72;
        grid.MinColumnWidth = 24;
        grid.RowBackground = Brushes.Transparent;
        grid.AlternatingRowBackground = Brushes.Transparent;
        var cellStyle = new Style(typeof(DataGridCell), (Style)System.Windows.Application.Current.FindResource(typeof(DataGridCell)));
        cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        grid.CellStyle = cellStyle;
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
        grid.Columns.Clear();
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Item and action", Width = new DataGridLength(1, DataGridLengthUnitType.Star), CellTemplate = ItemTemplate(trailingTime: !activityColumns) });
        if (activityColumns)
        {
            grid.Columns.Add(new DataGridTemplateColumn { Header = "Time", Width = 74, CellTemplate = FeedCell(
                "Text=\"{Binding TimeLabel}\" ToolTip=\"{Binding FullTimestamp}\" AutomationProperties.Name=\"{Binding TimeAccessibleLabel}\" AutomationProperties.HelpText=\"{Binding FullTimestamp}\"") });
            grid.Columns.Add(new DataGridTemplateColumn { Header = "Size", Width = 90, CellTemplate = FeedCell(
                "Text=\"{Binding SizeLabel}\"") });
        }
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Status", Width = 40, CellTemplate = StatusTemplate() });
        var menu = new FrameworkElementFactory(typeof(Button));
        menu.SetValue(ContentControl.ContentProperty, "⋯");
        menu.SetValue(FrameworkElement.WidthProperty, 32d);
        menu.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
        menu.SetValue(Control.PaddingProperty, new Thickness(0));
        menu.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        menu.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        menu.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        menu.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, "Actions for this activity item");
        grid.Columns.Add(new DataGridTemplateColumn { Header = "Actions", Width = 38, CellTemplate = new DataTemplate { VisualTree = menu } });
        // Grouped rows with hidden column headers can retain the zero-width star calculation
        // from their initially collapsed page. Size the name from the real viewport instead.
        void FitNameColumn()
        {
            if (!fitNameColumn || !grid.IsVisible || grid.ActualWidth <= 0 || grid.Columns.Count == 0) return;
            var fixedWidth = grid.Columns.Skip(1).Sum(column => column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth);
            var width = Math.Max(grid.HeadersVisibility == DataGridHeadersVisibility.Column ? 220 : 100,
                grid.ActualWidth - fixedWidth - 24);
            if (Math.Abs(grid.Columns[0].ActualWidth - width) > 1) grid.Columns[0].Width = width;
        }
        grid.SizeChanged += (_, _) => FitNameColumn();
        grid.Columns.CollectionChanged += (_, _) => grid.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded, FitNameColumn);
        grid.IsVisibleChanged += (_, _) => grid.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, FitNameColumn);
    }

    private static DataTemplate FeedCell(string attributes) =>
        (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock VerticalAlignment=\"Center\" TextWrapping=\"Wrap\" " +
            attributes + "/></DataTemplate>");

    internal static Style WorkspaceTabStyle() => (Style)System.Windows.Markup.XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TabItem">
          <Setter Property="Foreground" Value="{DynamicResource MutedInk}"/><Setter Property="Padding" Value="16,11"/>
          <Setter Property="Background" Value="Transparent"/><Setter Property="BorderBrush" Value="Transparent"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem">
            <Border BorderThickness="0,0,0,2" BorderBrush="{TemplateBinding BorderBrush}" Padding="{TemplateBinding Padding}" Background="{TemplateBinding Background}"><ContentPresenter ContentSource="Header"/></Border>
          </ControlTemplate></Setter.Value></Setter>
          <Style.Triggers><Trigger Property="IsSelected" Value="True"><Setter Property="Foreground" Value="{DynamicResource FocusBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource FocusBrush}"/></Trigger>
          <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{DynamicResource HoverSurface}"/></Trigger>
          <Trigger Property="IsKeyboardFocused" Value="True"><Setter Property="BorderBrush" Value="{DynamicResource FocusBrush}"/></Trigger></Style.Triggers>
        </Style>
        """);
}

internal sealed class ActivityStatusIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() switch { "!" => "attention", "↻" => "sync", "✓" => "check", "■" => "stop", "Ⅱ" => "pause", _ => "info" };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

internal sealed class ActivityStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? string.Empty;
        var key = text switch { "!" => "WarningBrush", "↻" => "FocusBrush", "✓" => "SuccessBrush", _ => "MutedInk" };
        return System.Windows.Application.Current.FindResource(key);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

internal sealed class MenuActionStyleSelector(Style actionStyle) : StyleSelector
{
    public override Style? SelectStyle(object item, DependencyObject container) =>
        container is MenuItem ? actionStyle : null;
}

// Resolve an association icon from type attributes, never from an actual user file.
// No hydration, thumbnails, overlays or third-party per-file handlers on the UI thread.
public sealed class ShellTypeImage : Image
{
    public static readonly DependencyProperty StatusIconStyleProperty = DependencyProperty.Register(
        nameof(StatusIconStyle), typeof(string), typeof(ShellTypeImage), new PropertyMetadata("Combined", async (owner, _) =>
            await ((ShellTypeImage)owner).LoadAsync()));
    public string StatusIconStyle { get => (string)GetValue(StatusIconStyleProperty); set => SetValue(StatusIconStyleProperty, value); }
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;
    public ShellTypeImage()
    {
        SetResourceReference(StatusIconStyleProperty, BrandedItemImages.StyleResource);
        DataContextChanged += async (_, _) => await LoadAsync();
    }
    private async Task LoadAsync()
    {
        var generation = ++_generation;
        Source = FallbackIcon();
        // A file request is a Sync Shark entity with no Windows file type of its own, so it uses
        // the product image exactly as pair, run and application entities do.
        if (DataContext is InternalAttentionIcon or ActivityRow { IsAppEvent: true } or RunHistoryRow
            or AccountSharingRow { Request: not null })
        {
            Source = BrandedItemImages.For(DataContext, StatusIconStyle);
            return;
        }
        var (kind, path) = DataContext switch
        {
            ActivityRow row => (row.ItemKind, row.Path),
            QueueRow row => (row.ItemKind, row.Path),
            DecisionRow row => ("Unknown", row.Path),
            RecoveryRow row => (row.Item.ObjectKind == "folder" ? "Folder" : "File", row.Path),
            BootstrapRow row => (row.ItemKind, row.Path),
            AccountSharingRow row => (row.ItemKind, row.Path),
            StorageInventoryDisplayRow row => (row.ItemKind, row.Path),
            _ => ("Unknown", string.Empty)
        };
        var key = string.Equals(kind, "Folder", StringComparison.OrdinalIgnoreCase)
            ? "<folder>" : System.IO.Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
        if (Cache.Count > 256) Cache.Clear();
        var result = await Cache.GetOrAdd(key, value => Task.Run(() => Read(value)));
        // A temporary shell/association failure must not become a process-lifetime fallback.
        if (result is null) Cache.TryRemove(key, out _);
        if (generation == _generation && result is not null) Source = result;
    }
    private static ImageSource FallbackIcon()
    {
        var drawing = new DrawingImage(new GeometryDrawing(Brushes.LightGray, null,
            Geometry.Parse("M6,2 L19,2 26,9 26,30 6,30 Z M19,2 L19,9 26,9")));
        drawing.Freeze(); return drawing;
    }
    private static ImageSource? Read(string key)
    {
        var info = new ShellFileInfo();
        var comInitialized = false;
        try
        {
            // Shell lookup requires a COM-initialized worker; never invoke file handlers on the UI thread.
            comInitialized = CoInitializeEx(IntPtr.Zero, 0) >= 0;
            var folder = key == "<folder>";
            // Packaged associations (including modern Windows Notepad) expose their document
            // image through a PRI indirect resource. SHGFI_USEFILEATTRIBUTES can return the
            // unassociated page for these, despite a valid per-user association.
            if (!folder && ReadPackagedAssociationIcon(key) is { } packaged) return packaged;
            if (SHGetFileInfo(folder ? "folder" : "file" + key, folder ? 0x10u : 0x80u,
                    ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100u | 0x10u) == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch { return null; }
        finally
        {
            if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon);
            if (comInitialized) CoUninitialize();
        }
    }
    private static ImageSource? ReadPackagedAssociationIcon(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        var association = new System.Text.StringBuilder(32768); uint length = (uint)association.Capacity;
        if (AssocQueryString(0, 15, extension, null, association, ref length) != 0 ||
            !association.ToString().StartsWith("@{", StringComparison.Ordinal)) return null;
        var resource = new System.Text.StringBuilder(32768);
        if (SHLoadIndirectString(association.ToString(), resource, (uint)resource.Capacity, IntPtr.Zero) != 0) return null;
        var path = resource.ToString();
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            System.IO.Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".ico" or ".bmp")) return null;
        var file = new System.IO.FileInfo(path);
        if (!file.Exists || file.Length > 8 * 1024 * 1024) return null;
        using var stream = file.OpenRead();
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = decoder.Frames.OrderBy(frame => Math.Abs(frame.PixelWidth - 48)).First();
        bitmap.Freeze(); return bitmap;
    }
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int AssocQueryString(uint flags, uint kind,
        string association, string? extra, System.Text.StringBuilder result, ref uint length);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int SHLoadIndirectString(string source,
        System.Text.StringBuilder result, uint length, IntPtr reserved);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon; public int IconIndex; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}
