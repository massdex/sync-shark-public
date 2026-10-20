namespace Massdex.SyncShark.V2;

/// <summary>
/// Deterministic detection of rebuildable folders over a local inventory (docs/design/rebuildables.md): a pure
/// function of the scanned items plus a small text reader, so the same input always gives the same output and no
/// extra directory walk is done. A folder qualifies only through its tool's own marker.
/// </summary>
/// <remarks>
/// The one content marker (a CACHEDIR.TAG signature) is the only read: one small file the inventory already proved
/// is a file, read through <see cref="DiskReader"/> in production and through a dictionary in checks. <c>.git</c> is
/// never itself rebuildable, and nothing is inferred from .gitignore.
/// </remarks>
internal static class RebuildableDetector
{
    /// <summary>The CACHEDIR.TAG standard signature, at the start of the file (cachedir-tag specification).</summary>
    private const string CachedirSignature = "Signature: 8a477f597d28d172789f06886806bc55";

    /// <summary>The fixed categories with the explanation the report shows; the order is the report order.</summary>
    internal static readonly IReadOnlyList<RebuildableCategory> Categories =
    [
        new("js-packages",
            "Created by npm, pnpm or yarn install from package.json; recreate any time by running install. Usually not synced."),
        new("python-cache",
            "Python bytecode cache; recreated automatically the next time Python runs. Usually not synced."),
        new("python-venv",
            "A Python virtual environment; recreate it with venv and reinstall the packages. Usually not synced."),
        new("dotnet-build",
            ".NET build output; recreated by building the project again. Usually not synced."),
        new("cache-tagged",
            "A cache folder that marks itself with CACHEDIR.TAG; the tool that owns it can rebuild it. Usually not synced."),
        new("js-build",
            "A Next.js build output folder (.next); recreate it by running the build. Usually not synced.")
    ];

    /// <summary>Every known category id, in report order.</summary>
    internal static IReadOnlyList<string> Ids { get; } = Categories.Select(category => category.Id).ToArray();

    /// <summary>True when the id names a known category (letter case is ignored).</summary>
    internal static bool IsKnown(string? id) =>
        id is not null && Categories.Any(category => string.Equals(category.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every rebuildable folder in the inventory, ordered by category then path. A nested <c>node_modules</c> inside
    /// an already-detected one is not listed separately (the outer one covers it).
    /// </summary>
    /// <param name="readText">The content of one pair-relative file, or null when it cannot be read.</param>
    internal static IReadOnlyList<RebuildableFolder> Detect(IReadOnlyList<ItemState> items, Func<string, string?> readText)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(readText);
        var children = Children(items);
        var (fileCounts, fileBytes) = CountFilesUnder(items);
        var detected = new List<RebuildableFolder>();
        var nodeModules = new List<string>();
        // Shallowest first, so an outer node_modules is detected before any nested one.
        foreach (var folder in items.Where(item => item.Kind == ItemKind.Folder)
                     .OrderBy(item => PairPath.Depth(item.Path)).ThenBy(item => item.Path, StringComparer.Ordinal))
        {
            var category = Classify(folder.Path, children, readText, nodeModules);
            if (category is null)
                continue;
            detected.Add(new RebuildableFolder(category, folder.Path,
                fileCounts.GetValueOrDefault(folder.Path), fileBytes.GetValueOrDefault(folder.Path)));
        }
        return detected.OrderBy(folder => folder.Category, StringComparer.Ordinal)
            .ThenBy(folder => folder.Path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// The detected folders of the chosen categories, as the plan uses them: reduced to their outermost roots across
    /// all categories (a nested selection is covered by its outer folder) and with any path whose cloud counterpart is
    /// not a folder dropped (it is left to ordinary planning).
    /// </summary>
    internal static IReadOnlyList<RebuildableFolder> Chosen(IReadOnlyList<RebuildableFolder> detected,
        IReadOnlyList<ItemState> cloudItems, IReadOnlyList<string> types)
        => ChosenScoped(detected, cloudItems, types.Select(id => new RebuildableChoice(id)).ToArray());

    internal static IReadOnlyList<RebuildableFolder> ChosenScoped(IReadOnlyList<RebuildableFolder> detected,
        IReadOnlyList<ItemState> cloudItems, IReadOnlyList<RebuildableChoice> choices)
    {
        if (choices.Count == 0) return Array.Empty<RebuildableFolder>();
        var cloudFiles = FilePaths(cloudItems);
        return Collapse(detected.Where(folder => choices.Any(choice => choice.Matches(folder))))
            .Where(folder => !cloudFiles.Contains(folder.Path)).ToArray();
    }

    /// <summary>
    /// The reviewed set as it still applies, never adding anything. A frozen path is dropped when its local folder is
    /// gone, when its category's own markers no longer qualify it (revalidated with the same rules detection uses), or
    /// when its cloud counterpart is not a folder; a dropped path gets no cloud delete and no exclusion and is synced
    /// normally, with no refusal. A path the cloud does not hold at all is kept, so it keeps being ignored once the
    /// first sync completes.
    /// </summary>
    internal static IReadOnlyList<RebuildableFolder> Retained(IReadOnlyList<RebuildableFolder> frozen,
        IReadOnlyList<ItemState> localItems, string localRootPath, IReadOnlyList<ItemState> cloudItems)
    {
        if (frozen.Count == 0) return Array.Empty<RebuildableFolder>();
        var children = Children(localItems);
        var readText = DiskReader(localRootPath);
        var localFolders = new HashSet<string>(
            localItems.Where(item => item.Kind == ItemKind.Folder).Select(item => item.Path),
            StringComparer.OrdinalIgnoreCase);
        var cloudFiles = FilePaths(cloudItems);
        var kept = new List<RebuildableFolder>(frozen.Count);
        foreach (var folder in frozen)
        {
            if (!localFolders.Contains(folder.Path) || cloudFiles.Contains(folder.Path)) continue;
            if (!string.Equals(Markers(folder.Path, children, readText), folder.Category, StringComparison.OrdinalIgnoreCase))
                continue;
            kept.Add(folder);
        }
        return kept;
    }

    /// <summary>
    /// A reader of one file under a local root, or null when it is missing or cannot be read; the detector's only
    /// I/O, and never a directory walk (each requested path was already in the inventory).
    /// </summary>
    internal static Func<string, string?> DiskReader(string rootPath) => relative =>
    {
        try
        {
            return File.ReadAllText(Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    };

    // ---- the qualification rules -------------------------------------------------

    private static string? Classify(string folderPath, IReadOnlyDictionary<string, List<ItemState>> children,
        Func<string, string?> readText, List<string> nodeModules)
    {
        // A node_modules under one already detected is covered by the outer one, so it is not listed again.
        var category = Markers(folderPath, children, readText);
        return category == "js-packages" ? NodeModules(folderPath, nodeModules) : category;
    }

    /// <summary>This one folder's category from its own markers, with no nesting bookkeeping (also used to revalidate
    /// a frozen path at apply).</summary>
    private static string? Markers(string folderPath, IReadOnlyDictionary<string, List<ItemState>> children,
        Func<string, string?> readText)
    {
        var name = Name(folderPath);
        var parent = Parent(folderPath);
        var byName = name.ToLowerInvariant() switch
        {
            "node_modules" when HasFileNamed(children, parent, "package.json") => "js-packages",
            "__pycache__" when HasChildWithExtension(children, folderPath, ".pyc") => "python-cache",
            ".venv" or "venv" when HasFileNamed(children, folderPath, "pyvenv.cfg") => "python-venv",
            "bin" or "obj" when HasProjectFile(children, parent) => "dotnet-build",
            ".next" when HasFileNamed(children, parent, "package.json") && HasNextConfig(children, parent) => "js-build",
            _ => null
        };
        if (byName is not null)
            return byName;
        // Any folder may mark itself with a CACHEDIR.TAG file carrying the standard signature; the inventory must
        // already report it as a file before it is read.
        if (!HasFileNamed(children, folderPath, "CACHEDIR.TAG"))
            return null;
        var tag = folderPath.Length == 0 ? "CACHEDIR.TAG" : folderPath + "/CACHEDIR.TAG";
        return readText(tag) is { } text && text.StartsWith(CachedirSignature, StringComparison.Ordinal)
            ? "cache-tagged"
            : null;
    }

    private static string? NodeModules(string folderPath, List<string> nodeModules)
    {
        foreach (var outer in nodeModules)
            if (PairPath.IsUnder(folderPath, outer))
                return null;
        nodeModules.Add(folderPath);
        return "js-packages";
    }

    // ---- selection reduction -----------------------------------------------------

    // Outermost roots only: a folder under an already-kept one is covered by that folder's own delete.
    private static IReadOnlyList<RebuildableFolder> Collapse(IEnumerable<RebuildableFolder> folders)
    {
        var kept = new List<RebuildableFolder>();
        foreach (var folder in folders.OrderBy(folder => PairPath.Depth(folder.Path))
                     .ThenBy(folder => folder.Path, StringComparer.Ordinal))
        {
            var covered = false;
            foreach (var outer in kept)
                if (PairPath.IsUnder(folder.Path, outer.Path))
                {
                    covered = true;
                    break;
                }
            if (!covered) kept.Add(folder);
        }
        return kept;
    }

    private static HashSet<string> FilePaths(IReadOnlyList<ItemState> items) => new(
        items.Where(item => item.Kind == ItemKind.File).Select(item => item.Path),
        StringComparer.OrdinalIgnoreCase);

    // ---- indexing helpers --------------------------------------------------------

    private static Dictionary<string, List<ItemState>> Children(IReadOnlyList<ItemState> items)
    {
        var children = new Dictionary<string, List<ItemState>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var parent = Parent(item.Path);
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<ItemState>();
            list.Add(item);
        }
        return children;
    }

    // Every file charges each of its folder ancestors, so a folder's totals are read without a second walk.
    private static (Dictionary<string, int> Counts, Dictionary<string, long> Bytes) CountFilesUnder(
        IReadOnlyList<ItemState> items)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.Kind != ItemKind.File) continue;
            foreach (var folder in PairPath.Ancestors(item.Path))
            {
                counts[folder] = counts.GetValueOrDefault(folder) + 1;
                bytes[folder] = bytes.GetValueOrDefault(folder) + item.Size;
            }
        }
        return (counts, bytes);
    }

    private static bool HasFileNamed(IReadOnlyDictionary<string, List<ItemState>> children, string folder, string name) =>
        children.TryGetValue(folder, out var list) && list.Any(item =>
            item.Kind == ItemKind.File && string.Equals(Name(item.Path), name, StringComparison.OrdinalIgnoreCase));

    private static bool HasChildWithExtension(IReadOnlyDictionary<string, List<ItemState>> children, string folder,
        string extension) =>
        children.TryGetValue(folder, out var list) && list.Any(item =>
            item.Kind == ItemKind.File && Name(item.Path).EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool HasNextConfig(IReadOnlyDictionary<string, List<ItemState>> children, string folder) =>
        children.TryGetValue(folder, out var list) && list.Any(item => item.Kind == ItemKind.File &&
            Name(item.Path).StartsWith("next.config.", StringComparison.OrdinalIgnoreCase));

    // A project marker is any sibling file whose extension ends with "proj" (.csproj, .fsproj, .vbproj, .vcxproj, ...).
    private static bool HasProjectFile(IReadOnlyDictionary<string, List<ItemState>> children, string folder)
    {
        if (!children.TryGetValue(folder, out var list)) return false;
        foreach (var item in list)
        {
            if (item.Kind != ItemKind.File) continue;
            var name = Name(item.Path);
            var dot = name.LastIndexOf('.');
            if (dot > 0 && dot < name.Length - 1 &&
                name[(dot + 1)..].EndsWith("proj", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string Name(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }
}
