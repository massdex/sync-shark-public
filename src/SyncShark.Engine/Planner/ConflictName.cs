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
        var source = side == Side.Local ? nameof(Side.Local) : settings.ConflictLabelDropboxName;
        var time = modified.UtcDateTime.ToString(settings.ConflictLabelTimeFormat, CultureInfo.InvariantCulture);
        if (side == Side.Local)
            source = settings.ConflictLabelLocalName.Replace("{computerName}", computerName);
        var suffix = ordinal > 1 ? FormattableString.Invariant($" ({ordinal})") : string.Empty;
        var label = CliCatalogResource.Fill(settings.ConflictLabelFormat,
            [("name", name), ("side", source), ("time", time), ("ext", suffix + extension)]);
        return PathNames.Child(PathNames.Parent(path), label);
    }
}
