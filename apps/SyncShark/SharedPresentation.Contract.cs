using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Massdex.SyncShark;

// U1 frozen shared presentation contract (iteration three).
//
// One presentation source for the Sync Shark status styles, the internal entity and status
// vocabulary, the provider marks, the shared feed timestamp column and the shared details
// header. Packets U2, U3, U4 and S2 bind these members instead of drawing a page-local mark,
// picking a per-page glyph for a shared action or repeating a provider prefix in a label.
//
// Frozen decisions:
//  * three selectable, persisted styles: Colour, Symbol and Combined (default Combined);
//  * one status palette whose attention and destructive colour is the exact tray red #E74949;
//  * one existing shark asset (Assets/SyncShark-64.png) reused and tinted through its own alpha,
//    never a second copy of the artwork and never a new drawing;
//  * internal entity items use the branded entity treatment, real files keep their Windows
//    file-type icon and cloud providers keep their official mark;
//  * an unknown status or entity resolves to a neutral, uncoloured mark rather than inventing a
//    state;
//  * provider marks live in one square, uniform-scaled host with no fixed 21x18 child;
//  * one details header supplies the same expand glyph and actions in every consumer;
//  * logical sizes convert to whole device pixels at 100/150/200 percent display scaling.
internal static partial class SharedPresentation
{
    // --- 1. Status icon style -----------------------------------------------------------------

    internal const string ColourStatusIconStyle = UiPreferencesStore.ColourIconSet;
    internal const string SymbolStatusIconStyle = UiPreferencesStore.SymbolIconSet;
    internal const string CombinedStatusIconStyle = UiPreferencesStore.CombinedIconSet;
    internal const string DefaultStatusIconStyle = UiPreferencesStore.DefaultIconSet;
    internal static readonly string[] StatusIconStyles = [.. UiPreferencesStore.IconSets];

    internal static bool IsStatusIconStyle(string? style) => UiPreferencesStore.IsSupportedIconSet(style);

    // An absent or unsupported style resolves to the frozen default, so no caller can leave the
    // shared presentation without a supported style and no unsupported value is persisted.
    internal static string NormalizeStatusIconStyle(string? style) =>
        UiPreferencesStore.IsSupportedIconSet(style) ? style! : DefaultStatusIconStyle;

    internal static string StatusIconStyleTooltip(string? style) => NormalizeStatusIconStyle(style) switch
    {
        ColourStatusIconStyle => "Tint the shark with the state colour.",
        SymbolStatusIconStyle => "Keep the normal shark and add a distinct state symbol.",
        _ => "Tint the shark and add the distinct state symbol."
    };

    // --- 2. One status palette ----------------------------------------------------------------

    internal const string AttentionColourHex = "#E74949";
    internal const string DestructiveColourHex = AttentionColourHex;
    internal static readonly Color AttentionColour = Color.FromRgb(0xE7, 0x49, 0x49);
    internal static readonly Color PausedColour = Color.FromRgb(0xF4, 0xBC, 0x41);
    internal static readonly Color RunningColour = Color.FromRgb(0x00, 0x66, 0xFF);
    internal static readonly Color FinishedColour = Color.FromRgb(0x0F, 0x50, 0x3C);
    internal static readonly Color NeutralColour = Color.FromRgb(0x5B, 0x56, 0x50);

    private static readonly Dictionary<PresentationStatus, SolidColorBrush> StatusBrushes = BuildStatusBrushes();

    private static Dictionary<PresentationStatus, SolidColorBrush> BuildStatusBrushes()
    {
        var brushes = new Dictionary<PresentationStatus, SolidColorBrush>();
        foreach (var status in Enum.GetValues<PresentationStatus>())
        {
            var brush = new SolidColorBrush(StatusColour(status));
            brush.Freeze();
            brushes[status] = brush;
        }
        return brushes;
    }

    // The status of a Sync Shark record. Unknown is the safe fallback and never carries a colour.
    internal enum PresentationStatus { Unknown, Finished, Running, Paused, Attention }

    // The kind of internal Sync Shark entity. File and Provider deliberately borrow the host
    // application's own icon and the provider's official mark instead of the branded mark.
    internal enum PresentationEntity
    {
        Unknown, FolderPair, SyncRun, Account, StoragePolicy, AppSetting, Diagnostics, Recovery, File, Provider
    }

    private static readonly Dictionary<string, PresentationStatus> StatusTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["finished"] = PresentationStatus.Finished,
        ["done"] = PresentationStatus.Finished,
        ["complete"] = PresentationStatus.Finished,
        ["completed"] = PresentationStatus.Finished,
        ["success"] = PresentationStatus.Finished,
        ["succeeded"] = PresentationStatus.Finished,
        ["ok"] = PresentationStatus.Finished,
        ["up-to-date"] = PresentationStatus.Finished,
        ["up to date"] = PresentationStatus.Finished,
        ["uptodate"] = PresentationStatus.Finished,
        ["healthy"] = PresentationStatus.Finished,
        ["running"] = PresentationStatus.Running,
        ["working"] = PresentationStatus.Running,
        ["syncing"] = PresentationStatus.Running,
        ["active"] = PresentationStatus.Running,
        ["in progress"] = PresentationStatus.Running,
        ["paused"] = PresentationStatus.Paused,
        ["waiting"] = PresentationStatus.Paused,
        ["queued"] = PresentationStatus.Paused,
        ["held"] = PresentationStatus.Paused,
        ["attention"] = PresentationStatus.Attention,
        ["needs attention"] = PresentationStatus.Attention,
        ["needs-attention"] = PresentationStatus.Attention,
        ["needsattention"] = PresentationStatus.Attention,
        ["error"] = PresentationStatus.Attention,
        ["failed"] = PresentationStatus.Attention,
        ["failure"] = PresentationStatus.Attention,
        ["conflict"] = PresentationStatus.Attention,
        ["warning"] = PresentationStatus.Attention,
        ["warn"] = PresentationStatus.Attention,
        ["blocked"] = PresentationStatus.Attention,
        ["unavailable"] = PresentationStatus.Attention
    };

    private static readonly Dictionary<string, PresentationEntity> EntityTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["folder pair"] = PresentationEntity.FolderPair,
        ["folder-pair"] = PresentationEntity.FolderPair,
        ["folderpair"] = PresentationEntity.FolderPair,
        ["pair"] = PresentationEntity.FolderPair,
        ["sync run"] = PresentationEntity.SyncRun,
        ["sync-run"] = PresentationEntity.SyncRun,
        ["run"] = PresentationEntity.SyncRun,
        ["account"] = PresentationEntity.Account,
        ["connection"] = PresentationEntity.Account,
        ["storage policy"] = PresentationEntity.StoragePolicy,
        ["storage-policy"] = PresentationEntity.StoragePolicy,
        ["storage"] = PresentationEntity.StoragePolicy,
        ["policy"] = PresentationEntity.StoragePolicy,
        ["app setting"] = PresentationEntity.AppSetting,
        ["setting"] = PresentationEntity.AppSetting,
        ["settings"] = PresentationEntity.AppSetting,
        ["preference"] = PresentationEntity.AppSetting,
        ["diagnostic"] = PresentationEntity.Diagnostics,
        ["diagnostics"] = PresentationEntity.Diagnostics,
        ["recovery"] = PresentationEntity.Recovery,
        ["restore"] = PresentationEntity.Recovery,
        ["file"] = PresentationEntity.File,
        ["document"] = PresentationEntity.File,
        ["provider"] = PresentationEntity.Provider,
        ["dropbox"] = PresentationEntity.Provider,
        ["google drive"] = PresentationEntity.Provider,
        ["google-drive"] = PresentationEntity.Provider,
        ["cloud"] = PresentationEntity.Provider
    };

    internal static PresentationStatus StatusFor(object? value)
    {
        if (value is PresentationStatus status) return status;
        var text = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text)) return PresentationStatus.Unknown;
        return StatusTokens.TryGetValue(text, out var mapped) ? mapped : PresentationStatus.Unknown;
    }

    internal static PresentationEntity EntityFor(object? value)
    {
        if (value is PresentationEntity entity) return entity;
        var text = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text)) return PresentationEntity.Unknown;
        return EntityTokens.TryGetValue(text, out var mapped) ? mapped : PresentationEntity.Unknown;
    }

    internal static Color StatusColour(PresentationStatus status) => status switch
    {
        PresentationStatus.Finished => FinishedColour,
        PresentationStatus.Running => RunningColour,
        PresentationStatus.Paused => PausedColour,
        PresentationStatus.Attention => AttentionColour,
        _ => NeutralColour
    };

    internal static SolidColorBrush StatusBrush(PresentationStatus status) => StatusBrushes[status];

    internal static string StatusGlyphName(PresentationStatus status) => status switch
    {
        PresentationStatus.Finished => "check",
        PresentationStatus.Running => "sync",
        PresentationStatus.Paused => "pause",
        PresentationStatus.Attention => "attention",
        _ => "info"
    };

    internal static string EntityGlyphName(PresentationEntity entity) => entity switch
    {
        PresentationEntity.FolderPair => "folder",
        PresentationEntity.SyncRun => "sync",
        PresentationEntity.Account => "user",
        PresentationEntity.StoragePolicy => "storage",
        PresentationEntity.AppSetting => "settings",
        PresentationEntity.Diagnostics => "shield",
        PresentationEntity.Recovery => "history",
        _ => "info"
    };

    // A real file keeps its Windows file-type icon and a cloud provider keeps its official mark,
    // so the branded mark is not offered for those two kinds.
    internal static bool UsesBrandMark(PresentationEntity entity) =>
        entity is not (PresentationEntity.File or PresentationEntity.Provider);

    internal static bool StatusColourApplies(PresentationStatus status) => status != PresentationStatus.Unknown;

    // Colour style tints the mark; symbol style keeps the normal mark; combined does both. An
    // unknown status is never tinted because that would invent a state.
    internal static bool MarkIsTinted(PresentationStatus status, string? style) =>
        StatusColourApplies(status) &&
        !string.Equals(NormalizeStatusIconStyle(style), SymbolStatusIconStyle, StringComparison.Ordinal);

    internal static bool StateSymbolShown(string? style) =>
        !string.Equals(NormalizeStatusIconStyle(style), ColourStatusIconStyle, StringComparison.Ordinal);

    // Test-facing and composition-facing description of one entity/status/style combination, so
    // every combination has one deterministic description instead of a page-local guess.
    internal static IReadOnlyList<string> EntityIconParts(PresentationEntity entity, PresentationStatus status,
        string? style)
    {
        if (!UsesBrandMark(entity)) return [];
        var parts = new List<string>
        {
            MarkIsTinted(status, style) ? "mark:tinted" : "mark:normal",
            "role:" + EntityGlyphName(entity)
        };
        if (StateSymbolShown(style)) parts.Add("state:" + StatusGlyphName(status));
        return parts;
    }

    // --- 3. One existing shark asset -----------------------------------------------------------

    internal const double StatusIconLogicalSize = 18;
    private const string SharkAssetPath = "pack://application:,,,/SyncShark;component/Assets/SyncShark-64.png";
    private static readonly Lazy<BitmapSource> SharkAsset = new(LoadSharkAsset);

    private static BitmapSource LoadSharkAsset()
    {
        var asset = new BitmapImage();
        asset.BeginInit();
        asset.UriSource = new Uri(SharkAssetPath, UriKind.Absolute);
        asset.CacheOption = BitmapCacheOption.OnLoad;
        asset.EndInit();
        asset.Freeze();
        return asset;
    }

    // The one shark source every consumer shares. Real files and providers do not use it.
    internal static ImageSource SharkMarkSource => SharkAsset.Value;

    private static FrameworkElement SharkVisual(PresentationStatus status, string? style, double size)
    {
        if (!MarkIsTinted(status, style))
            return new Image
            {
                Source = SharkAsset.Value, Width = size, Height = size, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        // The status colour shows through the asset's own alpha, so the tint introduces no second
        // artwork and no overlay that could obscure the fin.
        return new Rectangle
        {
            Width = size, Height = size, Fill = StatusBrush(status),
            OpacityMask = new ImageBrush(SharkAsset.Value) { Stretch = Stretch.Uniform },
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static CommandGlyph Symbol(string glyph, double size, PresentationStatus status, bool neutral)
    {
        var symbol = new CommandGlyph
        {
            IconName = glyph, Width = size, Height = size, Margin = new Thickness(2, 0, 0, 0),
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center
        };
        if (neutral) symbol.SetResourceReference(Control.ForegroundProperty, "Ink");
        else symbol.Foreground = StatusBrush(status);
        return symbol;
    }

    // The branded internal entity mark. Returns null for a real file or a cloud provider, whose
    // consumer keeps the Windows file-type icon or the official provider mark.
    internal static FrameworkElement? EntityIcon(PresentationEntity entity, PresentationStatus status,
        string? style, double size = StatusIconLogicalSize)
    {
        if (!UsesBrandMark(entity)) return null;
        var host = new Grid { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mark = SharkVisual(status, style, size);
        Grid.SetColumn(mark, 0);
        host.Children.Add(mark);
        var role = Symbol(EntityGlyphName(entity), size * 0.5, status, neutral: true);
        Grid.SetColumn(role, 1);
        host.Children.Add(role);
        if (StateSymbolShown(style))
        {
            var state = Symbol(StatusGlyphName(status), size * 0.56, status, neutral: false);
            Grid.SetColumn(state, 2);
            host.Children.Add(state);
        }
        return host;
    }

    // --- 4. Cloud providers keep their official mark in one square uniform host -----------------

    internal const double ProviderMarkLogicalSize = 20;

    internal static string? ProviderTemplateKey(string? provider) => provider switch
    {
        null => null,
        _ when string.Equals(provider, SyncProviders.Dropbox, StringComparison.OrdinalIgnoreCase) => "DropboxIconTemplate",
        _ when string.Equals(provider, SyncProviders.GoogleDrive, StringComparison.OrdinalIgnoreCase) => "GoogleDriveIconTemplate",
        _ when string.Equals(provider, "Dropbox", StringComparison.OrdinalIgnoreCase) => "DropboxIconTemplate",
        _ when string.Equals(provider, "Google Drive", StringComparison.OrdinalIgnoreCase) => "GoogleDriveIconTemplate",
        _ => null
    };

    // One square, uniformly scaled host. The provider mark is letterboxed inside it, so no
    // consumer has to fit a fixed 21x18 child and no mark is clipped.
    internal static ContentControl ProviderMark(string? provider, double size = ProviderMarkLogicalSize,
        FrameworkElement? scope = null)
    {
        var host = new ContentControl
        {
            Width = size, Height = size, MinWidth = size, MinHeight = size,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var key = ProviderTemplateKey(provider);
        var resolved = key is null ? null
            : scope is not null ? scope.TryFindResource(key)
            : System.Windows.Application.Current?.TryFindResource(key);
        if (resolved is DataTemplate template)
        {
            host.ContentTemplate = template;
            host.Content = new object();
        }
        else
        {
            host.Content = CommandIcons.Icon("cloud", size);
        }
        return host;
    }

    // --- 5. Account picker presentation --------------------------------------------------------

    internal const string ProviderAccountTemplateResourceKey = "NeutralAccountIconTemplate";
    private const string ProviderAccountTemplateXaml = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <!-- The rendered item itself carries the accessible provider and account identity, because a
               DataTemplate is not a DependencyObject and cannot take an attached automation property. -->
          <Grid AutomationProperties.Name="{Binding AccessibleName}">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="Auto" />
              <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <ContentControl Grid.Column="0" Width="18" Height="18" Margin="0,0,8,0"
                            VerticalAlignment="Center" Content="{Binding}">
              <ContentControl.Style>
                <Style TargetType="ContentControl">
                  <Setter Property="ContentTemplate" Value="{DynamicResource NeutralAccountIconTemplate}" />
                  <Style.Triggers>
                    <DataTrigger Binding="{Binding ProviderKey}" Value="dropbox">
                      <Setter Property="ContentTemplate" Value="{DynamicResource DropboxIconTemplate}" />
                    </DataTrigger>
                    <DataTrigger Binding="{Binding ProviderKey}" Value="google-drive">
                      <Setter Property="ContentTemplate" Value="{DynamicResource GoogleDriveIconTemplate}" />
                    </DataTrigger>
                  </Style.Triggers>
                </Style>
              </ContentControl.Style>
            </ContentControl>
            <!-- The identity binds Label so this one template also serves the workspace scope picker:
                 its ActivityPairChoice carries the same account identity as Label, and this
                 StorageAccountChoice exposes Label as the account alone. -->
            <TextBlock Grid.Column="1" Text="{Binding Label}" VerticalAlignment="Center"
                       TextTrimming="CharacterEllipsis" />
          </Grid>
        </DataTemplate>
        """;

    private static DataTemplate? _providerAccountTemplate;

    // One account-picker item: the provider mark plus the account identity alone. The provider
    // name is not repeated as visible text; it stays available to assistive technology through
    // the item's accessible name and the mark.
    internal static DataTemplate ProviderAccountTemplate() =>
        _providerAccountTemplate ??= (DataTemplate)System.Windows.Markup.XamlReader.Parse(ProviderAccountTemplateXaml);

    // --- 6. Shared feed timestamp --------------------------------------------------------------

    internal const double FeedTimestampColumnWidth = 74;

    // The timestamp is its own column, vertically centred, with the full value on hover and no
    // "At" prefix concatenated into the text.
    internal static DataGridTemplateColumn FeedTimestampColumn(double width = FeedTimestampColumnWidth) => new()
    {
        Header = "Time",
        Width = width,
        CellTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
            "<TextBlock VerticalAlignment=\"Center\" Text=\"{Binding TimeLabel}\" " +
            "ToolTip=\"{Binding FullTimestamp}\" " +
            "AutomationProperties.Name=\"{Binding TimeAccessibleLabel}\" " +
            "AutomationProperties.HelpText=\"{Binding FullTimestamp}\"/></DataTemplate>")
    };

    // --- 7. One shared details header ----------------------------------------------------------

    internal const string DetailsExpandGlyphName = "expand";
    internal const string DetailsExpandAccessibleName = "Expand Details";
    internal const string DetailsCloseGlyphName = "close";
    internal const string DetailsCloseAccessibleName = "Close Details";

    // Every consumer of the shared details model presents the same expand control and, when it
    // has one, the same close control. No page chooses its own glyph for this shared action.
    internal static DetailsHeaderControls DetailsHeader(Action expand, Action? close = null,
        string? accessibleName = null)
    {
        ArgumentNullException.ThrowIfNull(expand);
        var chrome = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var expandButton = DetailsChromeButton(DetailsExpandAccessibleName, DetailsExpandGlyphName, (_, _) => expand());
        chrome.Children.Add(expandButton);
        Button? closeButton = null;
        if (close is not null)
        {
            var closeAction = close;
            closeButton = DetailsChromeButton(DetailsCloseAccessibleName, DetailsCloseGlyphName, (_, _) => closeAction());
            chrome.Children.Add(closeButton);
        }
        if (!string.IsNullOrWhiteSpace(accessibleName)) AutomationProperties.SetName(chrome, accessibleName);
        return new DetailsHeaderControls(chrome, expandButton, closeButton);
    }

    private static Button DetailsChromeButton(string label, string glyph, RoutedEventHandler action)
    {
        var button = new Button
        {
            Content = Glyph(glyph, 18), Width = 34, Height = 34, Padding = new Thickness(7),
            Margin = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Top, Focusable = true, IsTabStop = true
        };
        button.SetResourceReference(Control.ForegroundProperty, "FocusBrush");
        button.Click += action;
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, label);
        button.ToolTip = label;
        return button;
    }

    // --- 8. Display-scaling contract -----------------------------------------------------------

    // WPF sizes are device-independent units. These helpers give the whole-pixel render size a
    // consumer needs at the supported 100/150/200 percent display scalings.
    internal static readonly uint[] SupportedDpis = [96, 144, 192];

    internal static double ScaledSize(double logicalSize, uint dpi) => logicalSize * dpi / 96.0;

    internal static int PhysicalPixels(double logicalSize, uint dpi) =>
        (int)Math.Round(ScaledSize(logicalSize, dpi), MidpointRounding.AwayFromZero);

    internal static int DpiPercent(uint dpi) => (int)Math.Round(dpi * 100.0 / 96.0, MidpointRounding.AwayFromZero);
}

// The controls one shared details header supplies: the same expand control and, when the
// consumer has one, the same close control.
internal sealed class DetailsHeaderControls
{
    internal DetailsHeaderControls(StackPanel chrome, Button expandButton, Button? closeButton)
    {
        Chrome = chrome;
        ExpandButton = expandButton;
        CloseButton = closeButton;
    }

    internal StackPanel Chrome { get; }
    internal Button ExpandButton { get; }
    internal Button? CloseButton { get; }
}

// The three-way status-icon style selector for Settings. It is deliberately separate from the
// user-approved Simple/Detailed two-icon row switch, reuses the shared segment presentation and
// shows a live preview drawn by the one shared presentation source.
internal sealed class StatusIconStyleSelector : Grid
{
    private readonly ToggleButton[] _segments;
    private readonly Grid _preview = new();
    private readonly Action<string>? _changed;
    private string _style;

    internal StatusIconStyleSelector(string accessibleName, string? current = null, Action<string>? changed = null,
        double segmentWidth = 84, double height = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessibleName);
        _changed = changed;
        _style = SharedPresentation.NormalizeStatusIconStyle(current);
        Height = height;
        VerticalAlignment = VerticalAlignment.Center;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AutomationProperties.SetName(this, accessibleName);

        var track = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        track.SetResourceReference(Border.BackgroundProperty, "ControlSurface");
        track.SetResourceReference(Border.BorderBrushProperty, "Border");
        Grid.SetColumn(track, 0);
        Children.Add(track);

        var strip = new UniformGrid
        {
            Rows = 1, Columns = SharedPresentation.StatusIconStyles.Length,
            Width = segmentWidth * SharedPresentation.StatusIconStyles.Length, Height = height,
            VerticalAlignment = VerticalAlignment.Center
        };
        _segments = new ToggleButton[SharedPresentation.StatusIconStyles.Length];
        var segmentStyle = (Style)System.Windows.Application.Current.FindResource("SegmentToggle");
        for (var index = 0; index < _segments.Length; index++)
        {
            var style = SharedPresentation.StatusIconStyles[index];
            var segment = new ToggleButton
            {
                Content = style, Style = segmentStyle, Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(2, 2, 2, 2), Focusable = true, IsTabStop = true,
                ToolTip = SharedPresentation.StatusIconStyleTooltip(style)
            };
            AutomationProperties.SetName(segment, style + " status icons");
            AutomationProperties.SetHelpText(segment, SharedPresentation.StatusIconStyleTooltip(style));
            var captured = style;
            segment.Click += (_, _) => SetStyle(captured);
            _segments[index] = segment;
            strip.Children.Add(segment);
        }
        track.Child = strip;

        _preview.Margin = new Thickness(12, 0, 0, 0);
        _preview.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_preview, 1);
        Children.Add(_preview);

        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Left or Key.Up or Key.Home: Step(-1); e.Handled = true; break;
                case Key.Right or Key.Down or Key.End: Step(1); e.Handled = true; break;
            }
        };
        SetStyle(_style, persist: false);
    }

    internal string SelectedStyle => _style;
    internal IReadOnlyList<ToggleButton> Segments => _segments;
    internal Grid Preview => _preview;

    internal void SetStyle(string? style, bool persist = true)
    {
        var normalized = SharedPresentation.NormalizeStatusIconStyle(style);
        var changed = !string.Equals(_style, normalized, StringComparison.Ordinal);
        _style = normalized;
        for (var index = 0; index < _segments.Length; index++)
        {
            var selected = string.Equals(SharedPresentation.StatusIconStyles[index], normalized, StringComparison.Ordinal);
            _segments[index].IsChecked = selected;
            _segments[index].SetResourceReference(Control.ForegroundProperty, selected ? "SelectedInk" : "MutedInk");
            AutomationProperties.SetItemStatus(_segments[index], selected ? "Selected" : "Not selected");
        }
        RefreshPreview();
        if (changed && persist) _changed?.Invoke(normalized);
    }

    private void Step(int direction)
    {
        var index = Array.IndexOf(SharedPresentation.StatusIconStyles, _style);
        if (index < 0) index = Array.IndexOf(SharedPresentation.StatusIconStyles, SharedPresentation.DefaultStatusIconStyle);
        var next = direction < 0 ? Math.Max(0, index - 1) : Math.Min(SharedPresentation.StatusIconStyles.Length - 1, index + 1);
        if (next == index) return;
        SetStyle(SharedPresentation.StatusIconStyles[next]);
        _segments[next].Focus();
    }

    private void RefreshPreview()
    {
        _preview.Children.Clear();
        foreach (var status in new[] { SharedPresentation.PresentationStatus.Attention, SharedPresentation.PresentationStatus.Finished })
        {
            var mark = SharedPresentation.EntityIcon(SharedPresentation.PresentationEntity.FolderPair, status, _style, 20);
            if (mark is not null) _preview.Children.Add(mark);
        }
    }
}
