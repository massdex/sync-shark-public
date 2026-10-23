using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Massdex.SyncShark;

// UIV2 shared two-endpoint pair mark.
//
// One reusable pair-row mark shows both sides of a pair at once: the left endpoint above, the right
// endpoint below, and the pair's own direction between them. The mark is presentation only. It reads
// the explicit saved endpoint document first and, only when that document is genuinely absent, falls
// back to the established legacy compatibility convention (left local, right cloud). An explicit
// record this build cannot name honestly shows the product's existing unknown mark instead of quietly
// assuming Dropbox or a local folder. Nothing here stats a path or calls a provider: the mark is
// decided from the saved record shape alone, because a runtime admission refusal is not branding.

/// <summary>Whether one side of a pair has a nameable endpoint identity or an honest unknown.</summary>
public enum PairEndpointMarkState
{
    /// <summary>The side names a real endpoint and shows its canonical existing mark.</summary>
    Marked = 0,

    /// <summary>The side has no record this build can name; it shows the unknown mark.</summary>
    Unknown = 1
}

/// <summary>One side of a pair exactly as its mark presents it.</summary>
public sealed record PairEndpointMarkSide(
    SyncPairSide Side,
    PairEndpointMarkState State,
    SyncEndpointKind? Kind,
    string IconTemplateKey,
    string AccessibleName)
{
    /// <summary>True when this side names a real endpoint.</summary>
    public bool IsKnown => State == PairEndpointMarkState.Marked;
}

/// <summary>
/// The pure pair-row presentation value: both sides plus the pair's direction. The side is a logical
/// position in the ordered pair and the kind is the endpoint kind, so a provider is never inferred
/// from display text and no consumer branches on a provider name to choose a mark.
/// </summary>
public sealed record PairEndpointMarks(PairEndpointMarkSide Left, PairEndpointMarkSide Right, SyncMode Direction)
{
    /// <summary>
    /// The registered unknown-provider resource key the endpoint value still carries. The mark no
    /// longer draws it: an unknown or absent side now renders the existing outline attention command
    /// glyph instead, so an unknown endpoint is never presented as the local folder mark while the
    /// retained artwork set registers no separate unknown image and no brand artwork is invented.
    /// </summary>
    public static string UnknownIconTemplateKey => ProviderPresentation.Unknown.IconResourceKey;

    /// <summary>The connector between the marks: down is left to right, up is right to left.</summary>
    public string ConnectorGlyph => Direction switch
    {
        SyncMode.UploadOnly => "\u2193",
        SyncMode.DownloadOnly => "\u2191",
        _ => "\u21C5"
    };

    /// <summary>The direction exactly as the pair editor already names it.</summary>
    public string DirectionLabel => SyncEndpointPresentation.DirectionLabel(Direction);

    /// <summary>The whole mark as one accessible sentence, direction included.</summary>
    public string AccessibleName =>
        Left.AccessibleName + "; " + Right.AccessibleName + "; direction " + DirectionLabel;

    /// <summary>
    /// True when a saved document is a genuinely explicit endpoint document. A legacy-implicit
    /// document that declares no explicit endpoint is the same absence as no document at all: the
    /// saved legacy members keep their original meaning, so the established convention still applies.
    /// </summary>
    public static bool IsExplicitDocument(SyncEndpointDocument? document) =>
        document is not null && (document.Version != SyncEndpointSchema.LegacyImplicit ||
                                 document.Left is not null || document.Right is not null);

    /// <summary>The pair's marks: the explicit saved document first, the legacy convention only when absent.</summary>
    public static PairEndpointMarks For(SyncPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (IsExplicitDocument(pair.EndpointDocument))
            return FromDocument(pair.EndpointDocument!, pair.Direction);
        // Only a genuinely absent document reaches here. The established compatibility projection is
        // consulted first and decides the mark whenever it can name both endpoints. Its identity
        // refusals are sync-semantics admissions rather than branding, so when it cannot form a
        // legacy identity the ordered legacy convention it defines - left local, right the pair's own
        // provider - still names the two marks instead of blanking a real legacy row.
        return SyncEndpointsCompatibility.FromSyncPair(pair).Endpoints is { } endpoints
            ? FromLegacyEndpoints(endpoints, pair.Direction)
            : FromLegacyRowFields(pair.LocalRoot, pair.Provider, pair.Direction);
    }

    /// <summary>
    /// The marks for one explicit document. A version this build does not read, a missing side, or a
    /// record whose kind and identity disagree is shown as unknown rather than guessed.
    /// </summary>
    public static PairEndpointMarks FromDocument(SyncEndpointDocument document, SyncMode direction)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version != SyncEndpointSchema.SymmetricEndpoints)
            return new PairEndpointMarks(Unknown(SyncPairSide.Left), Unknown(SyncPairSide.Right), direction);
        return new PairEndpointMarks(Side(document.Left, SyncPairSide.Left),
            Side(document.Right, SyncPairSide.Right), direction);
    }

    /// <summary>The marks for the established legacy ordered pair: the left endpoint local, the right cloud.</summary>
    public static PairEndpointMarks FromLegacyEndpoints(SyncPairEndpoints? endpoints, SyncMode direction) =>
        endpoints is null
            ? new PairEndpointMarks(Unknown(SyncPairSide.Left), Unknown(SyncPairSide.Right), direction)
            : new PairEndpointMarks(Marked(endpoints.Left.Kind, SyncPairSide.Left),
                Marked(endpoints.Right.Kind, SyncPairSide.Right), direction);

    /// <summary>
    /// The constructor-time marks for a legacy row built from its original fields. A row created from a
    /// saved pair is refreshed through <see cref="For(SyncPair)"/>, which owns the authoritative
    /// compatibility mapping; this overload exists only so existing legacy row constructors keep
    /// working without a saved document being invented for them.
    /// </summary>
    public static PairEndpointMarks FromLegacyRowFields(string? localRoot, string? provider, SyncMode direction)
    {
        var left = SyncEndpoints.ValidateLocal(localRoot).IsValid
            ? Marked(SyncEndpointKind.Local, SyncPairSide.Left)
            : Unknown(SyncPairSide.Left);
        var right = SyncEndpoints.TryMapProvider(provider, out var kind)
            ? Marked(kind, SyncPairSide.Right)
            : Unknown(SyncPairSide.Right);
        return new PairEndpointMarks(left, right, direction);
    }

    /// <summary>One explicit record as a mark. A null or self-contradictory record is unknown.</summary>
    public static PairEndpointMarkSide Side(SyncEndpointRecord? record, SyncPairSide side)
    {
        if (record is null || !Enum.IsDefined(record.Kind)) return Unknown(side);
        if (record.Kind == SyncEndpointKind.Local)
        {
            if (string.IsNullOrWhiteSpace(record.LocalPath)) return Unknown(side);
            if (!string.IsNullOrWhiteSpace(record.Provider) || !string.IsNullOrWhiteSpace(record.ConnectionId) ||
                !string.IsNullOrWhiteSpace(record.NamespaceId) || !string.IsNullOrWhiteSpace(record.FolderId) ||
                !string.IsNullOrWhiteSpace(record.FolderPath)) return Unknown(side);
            return Marked(SyncEndpointKind.Local, side);
        }
        if (!string.IsNullOrWhiteSpace(record.LocalPath) || record.LocalVolumeSerialNumber is not null ||
            record.LocalRootFileId is not null) return Unknown(side);
        if (string.IsNullOrWhiteSpace(record.ConnectionId)) return Unknown(side);
        if (string.IsNullOrWhiteSpace(record.FolderId) && string.IsNullOrWhiteSpace(record.FolderPath))
            return Unknown(side);
        return SyncEndpoints.TryMapProvider(record.Provider, out var kind) && kind == record.Kind
            ? Marked(record.Kind, side)
            : Unknown(side);
    }

    /// <summary>One nameable kind as a mark, through the central endpoint presentation mapping.</summary>
    private static PairEndpointMarkSide Marked(SyncEndpointKind kind, SyncPairSide side) =>
        new(side, PairEndpointMarkState.Marked, kind, SyncEndpointPresentation.IconTemplateKey(kind),
            SyncEndpointPresentation.SideLabel(side) + " endpoint: " + SyncEndpointPresentation.ShortLabel(kind));

    /// <summary>The honest unknown mark for one side, using the existing unknown provider mark.</summary>
    private static PairEndpointMarkSide Unknown(SyncPairSide side) =>
        new(side, PairEndpointMarkState.Unknown, null, UnknownIconTemplateKey,
            SyncEndpointPresentation.SideLabel(side) + " endpoint: unknown or not recorded");
}

/// <summary>
/// The reusable two-endpoint pair-row mark: the left endpoint above, the pair's direction in the
/// middle and the right endpoint below, in one bounded host. The hosting row control keeps its own
/// click, keyboard, tooltip and accessible expanded state; this mark never handles input and never
/// rescales or recolours the existing canonical provider artwork.
/// </summary>
public sealed class PairEndpointMark : ContentControl
{
    /// <summary>The pair presentation this mark renders.</summary>
    public static readonly DependencyProperty PresentationProperty = DependencyProperty.Register(
        nameof(Presentation), typeof(PairEndpointMarks), typeof(PairEndpointMark),
        new PropertyMetadata(null, OnPresentationChanged));

    private readonly ContentControl _left = new();
    private readonly ContentControl _right = new();
    private readonly TextBlock _connector = new();

    /// <summary>The existing outline attention command glyph every unnameable side draws.</summary>
    private const string UnknownGlyphName = "attention";

    /// <summary>The accessible name a side with no presentation at all publishes.</summary>
    private const string UnknownAccessibleName = "endpoint unknown or not recorded";

    public PairEndpointMark()
    {
        Focusable = false;
        IsTabStop = false;
        // The hosting row control owns the expand/collapse click, so the mark itself is transparent
        // to input and its own accessible text is published rather than intercepted.
        IsHitTestVisible = false;
        // The element's own HorizontalAlignment property shadows the enum name inside this class, so
        // the enum is qualified here.
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        SnapsToDevicePixels = true;
        Width = 28;
        Height = 52;

        foreach (var mark in new[] { _left, _right })
        {
            mark.Width = 24;
            mark.Height = 20;
            mark.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            mark.VerticalAlignment = VerticalAlignment.Center;
        }

        _connector.FontSize = 9;
        _connector.LineHeight = 12;
        _connector.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _connector.TextAlignment = TextAlignment.Center;
        _connector.Width = 28;
        _connector.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        _connector.SetResourceReference(ForegroundProperty, "MutedInk");

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        Grid.SetRow(_left, 0);
        Grid.SetRow(_connector, 1);
        Grid.SetRow(_right, 2);
        grid.Children.Add(_left);
        grid.Children.Add(_connector);
        grid.Children.Add(_right);
        Content = grid;

        Apply(null);
    }

    /// <summary>The pair presentation this mark renders.</summary>
    public PairEndpointMarks? Presentation
    {
        get => (PairEndpointMarks?)GetValue(PresentationProperty);
        set => SetValue(PresentationProperty, value);
    }

    private static void OnPresentationChanged(DependencyObject target, DependencyPropertyChangedEventArgs change) =>
        ((PairEndpointMark)target).Apply(change.NewValue as PairEndpointMarks);

    private void Apply(PairEndpointMarks? marks)
    {
        ApplySide(_left, marks?.Left);
        ApplySide(_right, marks?.Right);
        _connector.Text = marks?.ConnectorGlyph ?? string.Empty;
        AutomationProperties.SetName(_connector, marks?.DirectionLabel ?? string.Empty);
        AutomationProperties.SetName(this, marks?.AccessibleName ?? "Pair endpoints are not recorded yet");
    }

    private void ApplySide(ContentControl mark, PairEndpointMarkSide? side)
    {
        // The template is resolved through the shared icon resource keys, so a new endpoint kind is
        // one central mapping entry and never an edit to this control. A null state or an unknown side
        // has no nameable provider template at all: it draws the product's existing outline attention
        // glyph in muted ink, which is visibly neither the local folder mark nor any provider mark,
        // while its accessible name still states that the endpoint is unknown.
        if (side is null || side.State != PairEndpointMarkState.Marked)
        {
            mark.ContentTemplate = null;
            mark.Content = UnknownAttentionGlyph();
            AutomationProperties.SetName(mark, side?.AccessibleName ?? UnknownAccessibleName);
            return;
        }
        mark.Content = null;
        mark.SetResourceReference(ContentControl.ContentTemplateProperty, side.IconTemplateKey);
        AutomationProperties.SetName(mark, side.AccessibleName);
    }

    /// <summary>
    /// The existing outline attention command glyph in the product's muted ink, used for an unknown
    /// side and for a side with no presentation at all. It is the real shared control and the real
    /// registered outline, so no artwork is added and no provider is assumed.
    /// </summary>
    private static CommandGlyph UnknownAttentionGlyph()
    {
        var glyph = new CommandGlyph
        {
            IconName = UnknownGlyphName,
            IsHitTestVisible = false,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        glyph.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "MutedInk");
        return glyph;
    }
}
