namespace Massdex.SyncShark.V2;

public static class BlockerNames
{
    public static string Suggest(string path)
    {
        var leaf = path[(path.LastIndexOf('/') + 1)..];
        var safe = new string(leaf.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        if (safe.Length > 120) safe = safe[..120];
        // A prefix also avoids device basenames (CON.txt, COM1, etc.) and provider-reserved names.
        return "Renamed-" + (safe.Length == 0 ? "item" : safe);
    }

    internal static bool DropboxRefuses(string path) => path.Split('/').Any(name =>
        name.Equals(".ds_store", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("icon\r", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".dropbox", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".dropbox.attr", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("~$", StringComparison.Ordinal));

    internal static string Destination(string path, string? name)
    {
        name ??= Suggest(path);
        if (name.Length is 0 or > 180 || name.Contains('/') || name.Contains('\\') || !PilotPath.IsWindowsTransferPath(name))
            throw new SyncRunRefusedException("Choose one Windows-compatible file or folder name, not a path.");
        var slash = path.LastIndexOf('/');
        var destination = (slash < 0 ? "" : path[..(slash + 1)]) + name;
        if (!PilotPath.IsWindowsTransferPath(DropboxNameMapping.Map(destination)) || PairPath.Same(path, destination))
            throw new SyncRunRefusedException("Choose a different safe name. If a parent has an invalid name, rename that parent first.");
        return destination;
    }
}
