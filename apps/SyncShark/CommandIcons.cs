using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;

namespace Massdex.SyncShark;

/// <summary>
/// Whether one unsaved-draft action discards the draft or stays neutral. The caller states the intent
/// explicitly, so a surface never infers deletion from a label's text and a neutral choice can never
/// drift into a destructive one.
/// </summary>
internal enum UnsavedActionIntent
{
    /// <summary>Cancel, Close or Keep editing: neutral, and never styled as a delete.</summary>
    Neutral = 0,

    /// <summary>Discard Changes: the product's destructive unsaved-draft choice.</summary>
    Discard = 1
}

public sealed class CommandGlyph : System.Windows.Controls.ContentControl
{
    public static readonly System.Windows.DependencyProperty IconNameProperty = System.Windows.DependencyProperty.Register(
        nameof(IconName), typeof(string), typeof(CommandGlyph), new System.Windows.PropertyMetadata("info", (d, e) =>
        ((CommandGlyph)d).SetIcon(e.NewValue?.ToString() ?? "info")));
    public string IconName { get => (string)GetValue(IconNameProperty); set => SetValue(IconNameProperty, value); }
    public CommandGlyph()
    {
        Width = Height = 18;
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Center;
        Focusable = false;
        SetIcon("info");
    }
    private void SetIcon(string name)
    {
        var glyph = CommandIcons.Icon(name);
        glyph.Width = glyph.Height = double.NaN;
        Content = glyph;
    }
}

// Small, consistent outline command symbols. Files use the Windows association cache instead.
internal static class CommandIcons
{
    /// <summary>
    /// The product's one unsaved-draft red: the existing tray needs-action red, kept as a single
    /// definition here so no page repeats an RGB literal. It strokes the destructive glyph only; the
    /// label keeps its host's own foreground, so its contrast is unchanged and nothing has to guess a
    /// readable red for text.
    /// </summary>
    private static readonly System.Windows.Media.Brush UnsavedDiscardRed = FrozenBrush("#FFE74949");

    /// <summary>The existing bin glyph used for the one destructive unsaved-draft choice.</summary>
    private const string DiscardGlyph = "delete";

    /// <summary>The existing close glyph every neutral choice keeps.</summary>
    private const string NeutralGlyph = "close";

    /// <summary>
    /// The one description per unsaved-draft intent. The same pass that changes the label keeps the
    /// tooltip and the automation help in step, so a draft that becomes dirty never keeps the neutral
    /// Close wording and a cleaned draft never keeps the discard wording.
    /// </summary>
    private const string DiscardDescription = "Discard only these unsaved changes; saved data stays unchanged.";
    private const string CloseLabel = "Close";
    private const string CloseDescription = "Close this editor without changing saved data.";
    private const string CancelLabel = "Cancel";
    private const string CancelDescription = "Leave this draft without applying changes.";
    /// <summary>
    /// The one semantic presentation for an unsaved-draft action, shared by the Settings draft actions,
    /// the folder-pair editor, the sharing editors and the dialog's own discard choice. The explicit
    /// intent decides the glyph and the destructive red. Focus and default-button behaviour stay with
    /// the host, so a destructive choice is never made the safe default.
    /// </summary>
    internal static void SetUnsavedAction(System.Windows.Controls.Button button, string text,
        UnsavedActionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(button);
        var discard = intent == UnsavedActionIntent.Discard;
        button.Content = Label(text, discard ? DiscardGlyph : NeutralGlyph,
            discard ? UnsavedDiscardRed : null);
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        // Only the named intents own a description. Any other neutral choice keeps the presentation
        // its own host already gave it, so nothing here invents wording for it.
        var description = discard ? DiscardDescription
            : string.Equals(text, CloseLabel, StringComparison.Ordinal) ? CloseDescription
            : string.Equals(text, CancelLabel, StringComparison.Ordinal) ? CancelDescription
            : null;
        if (description is null) return;
        button.ToolTip = description;
        System.Windows.Automation.AutomationProperties.SetHelpText(button, description);
    }

    private static System.Windows.Media.Brush FrozenBrush(string value)
    {
        var brush = (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(value)!;
        brush.Freeze();
        return brush;
    }

    internal static Geometry GeometryFor(string name)
    {
        var data = name switch
        {
            "home" => "M3,10 L12,3 21,10 M5,9 V21 H10 V14 H14 V21 H19 V9",
            "mixed" => "M3,3 H10 V10 H3 Z M14,3 H21 V10 H14 Z M3,14 H10 V21 H3 Z M14,17 H21",
            "activity" => "M2,12 H6 L9,4 14,20 17,12 H22",
            "folder" => "M3,6 H9 L11,8 H21 V20 H3 Z M3,6 V4 H9 L11,6",
            "cloud" => "M6,19 H18 C24,19 24,10 18,10 C18,2 6,2 6,10 C0,10 0,19 6,19 Z",
            "cloud-check" => "M6,19 H18 C24,19 24,10 18,10 C18,2 6,2 6,10 C0,10 0,19 6,19 Z M8,12 L11,15 16,10",
            "settings" => "M9,3 H15 L16,6 19,6 22,11 20,14 21,17 16,21 12,20 9,21 4,17 5,14 2,11 5,6 8,6 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12",
            "attention" => "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M12,7 V13 M12,17 V18",
            "history" => "M3,10 A9,9 0 1 1 4,18 M3,3 V10 H10 M12,7 V12 L16,14",
            "sync" => "M3,10 A9,9 0 0 1 19,5 L21,8 M21,2 V8 H15 M21,14 A9,9 0 0 1 5,19 L3,16 M3,22 V16 H9",
            "pause" => "M6,4 H9 V20 H6 Z M15,4 H18 V20 H15 Z",
            "play" => "M7,3 L21,12 7,21 Z",
            "stop" => "M5,5 H19 V19 H5 Z",
            "check" => "M4,12 L9,17 20,6",
            "save" => "M4,3 H17 L21,7 V21 H3 V3 Z M7,3 V10 H17 V3 M7,21 V14 H17 V21",
            "close" => "M5,5 L19,19 M19,5 L5,19",
            "delete" => "M4,6 H20 M9,6 V3 H15 V6 M6,6 L7,21 H17 L18,6 M10,10 V17 M14,10 V17",
            "add" => "M12,3 V21 M3,12 H21",
            "search" => "M18,10 A7,7 0 1 1 4,10 A7,7 0 1 1 18,10 M16,16 L22,22",
            "external" => "M14,3 H21 V10 M21,3 L10,14 M10,4 H4 V20 H20 V14",
            "copy" => "M9,8 H21 V21 H9 Z M15,8 V3 H3 V16 H9",
            "link" => "M10,14 L14,10 M9,15 L7,17 A4,4 0 0 1 1,11 L6,6 A4,4 0 0 1 12,6 M15,9 L17,7 A4,4 0 0 1 23,13 L18,18 A4,4 0 0 1 12,18",
            "filter" => "M3,6 H21 M3,12 H21 M3,18 H21 M8,3 V9 M16,9 V15 M9,15 V21",
            "back" => "M15,4 L7,12 15,20",
            "right" => "M9,5 L16,12 9,19",
            "sidebar" => "M3,6 H21 M3,12 H21 M3,18 H21",
            "down" => "M5,8 L12,15 19,8",
            "up" => "M5,16 L12,9 19,16",
            "more" => "M12,4 V5 M12,11 V12 M12,18 V19",
            "file" or "document" => "M5,2 H14 L20,8 V22 H5 Z M14,2 V8 H20 M8,12 H17 M8,16 H17",
            "computer" => "M2,3 H22 V17 H2 Z M12,17 V22 M7,22 H17",
            "offline" => "M3,3 H21 V17 H3 Z M12,17 V22 M7,22 H17 M7,10 L10,13 17,6",
            "storage" => "M5,4 H19 L22,15 V21 H2 V15 Z M2,15 H22 M6,18 H7 M17,18 H18",
            "calendar" => "M3,5 H21 V22 H3 Z M3,10 H21 M7,2 V7 M17,2 V7 M7,14 H9 M13,14 H15 M7,18 H9 M13,18 H15",
            "window" => "M3,4 H21 V20 H3 Z M3,8 H21",
            "shield" => "M12,2 L21,6 V12 C21,17 17,21 12,23 C7,21 3,17 3,12 V6 Z M8,12 L11,15 17,9",
            "eye" => "M2,12 C8,2 16,2 22,12 C16,22 8,22 2,12 Z M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12",
            "user" => "M17,7 A5,5 0 1 1 7,7 A5,5 0 1 1 17,7 M3,23 V20 C3,12 21,12 21,20 V23",
            _ => "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M12,10 V18 M12,6 V7"
        };
        return Geometry.Parse(data);
    }

    internal static FrameworkElement Icon(string name, double size = 18, System.Windows.Media.Brush? stroke = null)
    {
        var path = new Path { Data = GeometryFor(name), Width = 24, Height = 24, StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.None };
        // An explicit stroke states one semantic colour; otherwise the glyph follows its host exactly
        // as every existing command glyph already does.
        if (stroke is null)
            path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(System.Windows.Controls.Control), 1) });
        else path.Stroke = stroke;
        return new Viewbox { Width = size, Height = size, Child = new System.Windows.Controls.Canvas
        { Width = 24, Height = 24, Children = { path } }, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
    }

    internal static string ForLabel(string label)
    {
        var text = label.ToLowerInvariant();
        if (text.StartsWith("cancel")) return "close";
        if (text.StartsWith("search")) return "search";
        if (text.StartsWith("go to")) return "folder";
        if (text.StartsWith("browse")) return "folder";
        if (text.StartsWith("export")) return "down";
        if (text.StartsWith("review") && text.Contains("first sync")) return "document";
        if (text.StartsWith("up one")) return "up";
        if (text is "change access") return "shield";
        if (text is "open request") return "external";
        if (text.Contains("file request")) return "document";
        if (text.Contains("revoke")) return "close";
        if (text.Contains("available offline")) return "offline";
        if (text.Contains("online only") || text.Contains("online-only")) return "cloud";
        if (text.Contains("storage")) return "storage";
        if (text is "manage" or "change default" || text.Contains("parent setting")) return "settings";
        if (text.Contains("temporary files")) return "document";
        if (text.Contains("dependencies")) return "folder";
        if (text.Contains("common rules") || text.Contains("ignore rule")) return "filter";
        if (text.Contains("pause")) return "pause";
        if (text.Contains("resume") || text.StartsWith("start")) return "play";
        if (text.Contains("copy")) return text.Contains("link") ? "link" : "copy";
        if (text.Contains("share") || text.Contains("link")) return "link";
        if (text.Contains("discard")) return "delete";
        if (text == "keep editing") return "back";
        if (text.Contains("recommended")) return "check";
        if (text.StartsWith("download")) return "down";
        if (text.StartsWith("install")) return "shield";
        if (text.StartsWith("choose")) return "folder";
        if (text.Contains("cleanup")) return "history";
        if (text.Contains("tray")) return "window";
        if (text.Contains("compare")) return "search";
        if (text.Contains("conflict")) return "attention";
        if (text.Contains("disconnect")) return "close";
        if (text.Contains("reconnect") || text.Contains("scan")) return "sync";
        if (text.Contains("removal") || text.Contains("uninstall")) return "shield";
        if (text.Contains("explorer menu")) return "folder";
        if (text.Contains("setup") || text.Contains("set up")) return "shield";
        if (text.Contains("use local")) return "computer";
        if (text.Contains("use dropbox")) return "cloud";
        if (text.Contains("keep both")) return "copy";
        if (text.Contains("select all")) return "check";
        if (text.Contains("revision")) return "history";
        if (text.Contains("refresh") || text.Contains("check") || text.Contains("sync now") || text.Contains("sync all") || text.Contains("run sync")) return "sync";
        if (text.Contains("stop")) return "stop";
        if (text.Contains("save")) return "save";
        if (text.Contains("approve") || text.Contains("apply") || text.Contains("resolve")) return "check";
        if (text.Contains("close") || text.Contains("cancel") || text.Contains("remove")) return "close";
        if (text.Contains("add") || text.Contains("new pair")) return "add";
        if (text.Contains("filter")) return "filter";
        if (text.Contains("explorer") || text.Contains("folder") || text.Contains("pair")) return "folder";
        if (text.Contains("recovery") || text.Contains("restore") || text.Contains("rewind") || text.Contains("run")) return "history";
        if (text.Contains("preview")) return "eye";
        if (text.Contains("activity") || text.Contains("log")) return "activity";
        if (text.Contains("connection")) return "cloud";
        if (text.Contains("setting")) return "settings";
        if (text.Contains("account")) return "user";
        if (text.Contains("dropbox") || text.Contains("web") || text.Contains("view all") || text.Contains("open app")) return "external";
        if (text.Contains('⋯') || text.Contains("more")) return "more";
        return "info";
    }

    internal static StackPanel Label(string text, string? icon = null,
        System.Windows.Media.Brush? iconStroke = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(Icon(icon ?? ForLabel(text), 16, iconStroke));
        var label = new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground")
        { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(System.Windows.Controls.Control), 1) });
        panel.Children.Add(label);
        return panel;
    }
}
