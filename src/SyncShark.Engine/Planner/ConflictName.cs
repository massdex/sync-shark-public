using System.Globalization;

namespace Massdex.SyncShark.Engine.Planner;

/// <summary>Renders a keep-both label from its observed source at apply time.</summary>
public static class ConflictName
{
    /// <summary>Ordinal one has no suffix; subsequent collisions are numbered before the extension.</summary>
    public static string For(string path, Side side, DateTimeOffset modified, string computerName, EngineSettings settings, int ordinal)
    {
        var file = PathNames.Name(path);
        var dot = file.LastIndexOf('.');
        var name = dot > 0 ? file[..dot] : file;
        var extension = dot > 0 ? file[dot..] : string.Empty;
        var source = side == Side.Local ? settings.ConflictLabelLocalName : settings.ConflictLabelDropboxName;
        var time = modified.ToString(settings.ConflictLabelTimeFormat, CultureInfo.InvariantCulture);
        var suffix = ordinal > 1 ? FormattableString.Invariant($" ({ordinal})") : string.Empty;
        var label = settings.ConflictLabelFormat.Replace("{name}", name).Replace("{side}", source)
            .Replace("{time}", time).Replace("{ext}", suffix + extension);
        if (!settings.ConflictLabelFormat.Contains("{ext}", StringComparison.Ordinal)) label += suffix;
        return PathNames.Child(PathNames.Parent(path), label);
    }
}
