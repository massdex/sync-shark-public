using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Massdex.SyncShark;

// One vector source and one state vocabulary for every internal-item surface.
// Provider identities and real file association icons deliberately stay outside this renderer.
/// <summary>
/// One internal attention entity as an explicit role and state pair. An attention row carries this
/// descriptor instead of a glyph name, so an internal icon is never chosen by matching title text and
/// the two internal cases stay distinguishable from a real file conflict.
/// </summary>
internal sealed record InternalAttentionIcon(string Role, string State)
{
    /// <summary>The first-sync review row: a Sync Shark sync item that needs your decision.</summary>
    internal static readonly InternalAttentionIcon FirstSyncReview = new("sync", "!");

    /// <summary>A confirmed folder or connection problem on one pair.</summary>
    internal static readonly InternalAttentionIcon PairProblem = new("attention", "!");
}

internal static class BrandedItemImages
{
    internal const string StyleResource = "StatusIconStyle";
    private static readonly Dictionary<(string Style, string State, string Role), ImageSource> Images = new();
    private static readonly Lazy<Geometry[]> Mark = new(() =>
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri(
            "pack://application:,,,/SyncShark;component/Assets/sync-shark-mark.svg")).Stream;
        return XDocument.Load(stream).Root!.Elements().Where(element => element.Name.LocalName == "path")
            .Select(element => { var geometry = Geometry.Parse(element.Attribute("d")!.Value); geometry.Freeze(); return geometry; }).ToArray();
    });

    internal static void ApplyStyle(string style)
    {
        if (!UiPreferencesStore.IsStatusIconStyle(style)) throw new ArgumentException("Unsupported icon style.", nameof(style));
        System.Windows.Application.Current.Resources[StyleResource] = style;
    }

    internal static ImageSource For(object entity, string style) => entity switch
    {
        InternalAttentionIcon icon => Create(style, icon.State, icon.Role),
        ActivityRow row => Create(style, row.StatusGlyph, row.IsPairEvent ? "folder" : Role(row.Entity)),
        RunHistoryRow row => Create(style, row.StatusGlyph, "sync"),
        AccountSharingRow { Request: not null } => Create(style, "·", "link"),
        _ => Create(style, "·", "settings")
    };

    private static string Role(string entity) => entity.ToLowerInvariant() switch
    {
        "run" => "sync", "account" => "user", "storage policy" or "policy" => "storage",
        "diagnostics" or "diagnostic" => "activity", "recovery" => "history", _ => "settings"
    };

    internal static ImageSource Create(string style, string state, string role)
    {
        if (!UiPreferencesStore.IsStatusIconStyle(style)) style = "Combined";
        var key = (style, state, role);
        if (Images.TryGetValue(key, out var cached)) return cached;
        var colour = state switch { "!" => "#E74949", "✓" => "#79CCA5", "Ⅱ" => "#E4AC50", _ => "#0061FE" };
        var fill = (Brush)new BrushConverter().ConvertFromString(style == "Symbol" ? "#0061FE" : colour)!;
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 100, 100))));
        drawing.Children.Add(new GeometryDrawing(fill, null, Mark.Value[0]));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(180, 200, 225)), null, Mark.Value[1]));
        if (style != "Colour")
        {
            var symbol = state switch { "!" => "attention", "✓" => "check", "↻" => "sync", "Ⅱ" => "pause", _ => role };
            // Small separate corner mark: never the tray's former oversized overlay.
            drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(16, 18, 20)),
                new Pen(new SolidColorBrush(Color.FromRgb(180, 200, 225)), 1.5), new EllipseGeometry(new Point(80, 80), 18, 18)));
            var geometry = CommandIcons.GeometryFor(symbol).Clone();
            geometry.Transform = new TranslateTransform(68, 68);
            drawing.Children.Add(new GeometryDrawing(null, new Pen(Brushes.White, 2.2)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry));
        }
        var image = new DrawingImage(drawing); image.Freeze(); Images[key] = image; return image;
    }
}
