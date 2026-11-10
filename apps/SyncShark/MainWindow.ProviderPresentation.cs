using System.Windows;

namespace Massdex.SyncShark;

// One cloud-side presentation, selected from the stored provider binding - never from display
// text - so the pair editor, its folder trees, its help and accessibility names and the detailed
// cloud column all name the provider the selected account actually uses. Google Drive keeps its
// real name and mark, and the Dropbox-only web folder action stays Dropbox-only rather than
// implying parity Google Drive does not have.
public sealed record ProviderPresentation(string Key, string IconResourceKey, bool CloudWebFolderLink)
{
    public string Name => SyncProviders.DisplayName(Key);
    public string CloudPanelSubtitle => $"Paired folder in {Name}";
    public string CloudFolderPrompt => $"Folder in {Name}";
    public string CloudFolderHint => $"The {Name} folder linked to the folder on this computer beside it.";
    public string CloudRootTreeName => $"Choose folders in {Name}";
    public string CloudSelectionTreeName => $"{Name} folders selected for two-way sync";
    public string CloudRootSelectionName => $"Sync the complete {Name} folder";
    public string CloudChildSuffix => $"in {Name}";
    public string CloudBrowserName => $"Create or select a {Name} folder";
    public string CloudBrowserHint => $"Open the full {Name} folder browser to create a missing folder, or to verify and select an exact existing folder.";
    public string CloudFolderIdLabel => $"{Name} Folder ID";
    public string CloudLocationLabel => $"{Name} Location:";
    public string CloudTargetLabel => $"To {Name}";

    internal static readonly ProviderPresentation Dropbox = new(SyncProviders.Dropbox, "DropboxIconTemplate", true);
    internal static readonly ProviderPresentation GoogleDrive = new(SyncProviders.GoogleDrive, "GoogleDriveIconTemplate", false);
    // The honest unknown mark. It stays a single shared entry so a consumer that must not assume a
    // provider - such as the pair-row endpoint marks - resolves the same existing mark from here.
    internal static readonly ProviderPresentation Unknown = new("unknown", "FolderIconTemplate", false);
    internal static ProviderPresentation For(string? provider) => SyncProviders.Normalize(provider) switch
    {
        SyncProviders.Dropbox => Dropbox,
        SyncProviders.GoogleDrive => GoogleDrive,
        _ => Unknown
    };

    // UI354-ICON1: one endpoint-kind mapping and one mark helper. The kind resolves through the
    // registered endpoint providers, so no consumer hardcodes a provider per kind, and both editor
    // sides apply the authentic registered artwork through the same fixed square host.
    internal const double EndpointKindMarkSize = 28d;

    /// <summary>The presentation of the provider that owns one endpoint kind.</summary>
    internal static ProviderPresentation ForEndpointKind(SyncEndpointKind kind) =>
        For(SyncEndpointProviders.All.FirstOrDefault(item => item.Kind == kind)?.ProviderId);

    /// <summary>
    /// Applies one endpoint kind's canonical mark inside its fixed 28x28 DIP host. The registered
    /// templates already carry the authentic vector artwork through a viewbox; a template that does not
    /// is wrapped in a uniform viewbox, so an intrinsic size can never inflate the mark and its aspect
    /// ratio is never stretched. Application, taskbar and tray resources are untouched.
    /// </summary>
    internal static void ApplyEndpointKindMark(System.Windows.Controls.ContentControl host, SyncEndpointKind kind)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.Width = EndpointKindMarkSize;
        host.Height = EndpointKindMarkSize;
        host.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
        host.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetName(host,
            SyncEndpointPresentation.ShortLabel(kind) + " endpoint mark");
        // UI354-ICON1 amendment: the registered mark templates are declared in the window's own resource
        // scope, so resolve them through the host control's own lookup (which walks up to its window)
        // before falling back to the application scope. An application-only lookup missed all three
        // templates and silently drew the generic cloud for every kind.
        var templateKey = SyncEndpointPresentation.IconTemplateKey(kind);
        var template = host.TryFindResource(templateKey) as System.Windows.DataTemplate ??
                       System.Windows.Application.Current?.TryFindResource(templateKey) as System.Windows.DataTemplate;
        if (template is null)
        {
            host.ContentTemplate = null;
            host.Content = CommandIcons.Icon("cloud", (int)EndpointKindMarkSize);
            return;
        }
        if (template.LoadContent() is not System.Windows.UIElement artwork)
        {
            host.ContentTemplate = null;
            host.Content = CommandIcons.Icon("cloud", (int)EndpointKindMarkSize);
            return;
        }
        // The mark is always laid out inside one fixed square uniform host, so a template's own intrinsic
        // or viewbox size can never make the icon measure or draw wider than the 28 DIP slot.
        host.ContentTemplate = null;
        host.Content = new System.Windows.Controls.Viewbox
        {
            Stretch = System.Windows.Media.Stretch.Uniform,
            Width = EndpointKindMarkSize,
            Height = EndpointKindMarkSize,
            Child = artwork
        };
    }
}

// One resource-key mapping for every mark. Binding updates refresh existing rows
// and editors without repeating provider-specific triggers on each surface.
public static class ProviderMark
{
    public static readonly DependencyProperty ProviderProperty = DependencyProperty.RegisterAttached(
        "Provider", typeof(string), typeof(ProviderMark), new PropertyMetadata(null, Changed));
    public static void SetProvider(DependencyObject target, string? value) => target.SetValue(ProviderProperty, value);
    public static string? GetProvider(DependencyObject target) => target.GetValue(ProviderProperty) as string;
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is System.Windows.Controls.ContentControl control)
        {
            // The host owns the mark's size. Explicit smaller consumers (for example 15 px
            // tree rows) keep that size; unconstrained consumers get a bounded square host.
            if (double.IsNaN(control.Width)) control.SetCurrentValue(FrameworkElement.WidthProperty, 20d);
            if (double.IsNaN(control.Height)) control.SetCurrentValue(FrameworkElement.HeightProperty, 20d);
            control.SetResourceReference(System.Windows.Controls.ContentControl.ContentTemplateProperty,
                ProviderPresentation.For(change.NewValue as string).IconResourceKey);
        }
    }
}

public partial class MainWindow
{
    public static readonly DependencyProperty ProviderPresentationProperty = DependencyProperty.Register(
        nameof(ProviderPresentation), typeof(ProviderPresentation), typeof(MainWindow),
        new PropertyMetadata(ProviderPresentation.Dropbox));

    public ProviderPresentation ProviderPresentation
    {
        get => (ProviderPresentation)GetValue(ProviderPresentationProperty);
        set => SetValue(ProviderPresentationProperty, value);
    }

    // The provider comes from the stored provider binding of the exact connection id. Display
    // names are never parsed to decide a provider, and an inactive account keeps its provider
    // so a pair bound to it is still described truthfully.
    internal string? ProviderForConnectionId(string? connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId)) return null;
        foreach (var connection in _authorityHost.LoadProviderConnections())
            if (string.Equals(connection.ConnectionId, connectionId, StringComparison.Ordinal))
                return connection.Provider;
        return _authorityHost.LoadAccountBinding(connectionId)?.Provider;
    }

    // The pair editor is described by the account it is bound to; only a new pair without an
    // account yet falls back to the selected connection. No provider is ever inferred from text.
    internal void ApplyPairEditorProviderPresentation()
    {
        ProviderPresentation = ProviderPresentation.For(
            ProviderForConnectionId(SelectedPairConnectionId) ?? ProviderForConnectionId(_selectedConnectionId));
    }
}
