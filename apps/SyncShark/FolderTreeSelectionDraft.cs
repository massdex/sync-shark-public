using System.IO;

namespace Massdex.SyncShark;

internal enum FolderTreePathKind
{
    Windows,
    Dropbox
}

internal sealed record FolderTreeAbsoluteRule(string Path, bool Included, string? StableFolderIdentity = null);

internal sealed record FolderTreeSelectionPlan(string RootPath, bool DefaultIncluded,
    IReadOnlyList<SyncSelectionRule> Rules);

/// <summary>
/// Retains a small rule set over a lazily rendered full folder tree. The rules are absolute while
/// the user browses; CreatePlan converts them into the relative policy stored by a sync pair.
/// </summary>
internal sealed class FolderTreeSelectionDraft(FolderTreePathKind kind)
{
    private readonly List<FolderTreeAbsoluteRule> _rules = [];

    public IReadOnlyList<FolderTreeAbsoluteRule> Rules => _rules;
    public bool HasIncludedFolder => _rules.Any(rule => rule.Included);

    public void Clear() => _rules.Clear();

    public void Restore(IEnumerable<FolderTreeAbsoluteRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules.Clear();
        _rules.AddRange(rules.Select(rule => new FolderTreeAbsoluteRule(Normalize(rule.Path),
            rule.Included, rule.StableFolderIdentity)));
        SortRules();
    }

    public void Load(string rootPath, bool defaultIncluded, IReadOnlyList<SyncSelectionRule>? rules)
    {
        Clear();
        var root = Normalize(rootPath);
        if (defaultIncluded) _rules.Add(new FolderTreeAbsoluteRule(root, true));
        foreach (var rule in SyncSelectionPolicy.Normalize(rules))
            Set(Combine(root, rule.FolderPath), rule.Included, rule.StableFolderIdentity);
    }

    public void Set(string path, bool included, string? stableFolderIdentity = null)
    {
        var normalized = Normalize(path);
        var inherited = EffectiveState(normalized, excludeExact: true);
        _rules.RemoveAll(rule => IsSameOrDescendant(rule.Path, normalized));
        if (included != inherited)
            _rules.Add(new FolderTreeAbsoluteRule(normalized, included, stableFolderIdentity));
        SortRules();
    }

    public bool? CheckState(string path)
    {
        var normalized = Normalize(path);
        var selected = EffectiveState(normalized);
        return _rules.Any(rule => IsDescendant(rule.Path, normalized) &&
                                  EffectiveState(rule.Path) != selected)
            ? null
            : selected;
    }

    public FolderTreeSelectionPlan? CreatePlan()
    {
        var included = _rules.Where(rule => rule.Included).Select(rule => rule.Path).ToArray();
        if (included.Length == 0) return null;
        var root = CommonParent(included);
        if (root is null) return null;
        var defaultIncluded = EffectiveState(root);
        var relative = _rules
            .Where(rule => !Same(rule.Path, root) && IsDescendant(rule.Path, root))
            .Select(rule => new SyncSelectionRule(Relative(root, rule.Path), rule.Included,
                rule.StableFolderIdentity))
            .ToArray();
        return new FolderTreeSelectionPlan(root, defaultIncluded, SyncSelectionPolicy.Normalize(relative));
    }

    public static bool Equivalent(FolderTreeSelectionPlan? left, FolderTreeSelectionPlan? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.DefaultIncluded != right.DefaultIncluded) return false;
        var leftRules = left.Rules.Select(rule => (Path: PilotPath.NormalizeRelative(rule.FolderPath), rule.Included))
            .ToArray();
        var rightRules = right.Rules.Select(rule => (Path: PilotPath.NormalizeRelative(rule.FolderPath), rule.Included))
            .ToArray();
        return leftRules.SequenceEqual(rightRules);
    }

    private bool EffectiveState(string path, bool excludeExact = false)
    {
        var selected = false;
        foreach (var rule in _rules.Where(rule => IsSameOrDescendant(path, rule.Path) &&
                                                  (!excludeExact || !Same(path, rule.Path)))
                     .OrderBy(rule => Depth(rule.Path)))
            selected = rule.Included;
        return selected;
    }

    private string Normalize(string path)
    {
        if (kind == FolderTreePathKind.Dropbox) return PilotPath.NormalizeDropboxRoot(path);
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? full;
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? root
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private string Combine(string root, string relative)
    {
        var normalizedRelative = PilotPath.NormalizeRelative(relative);
        if (normalizedRelative.Length == 0) return Normalize(root);
        return kind == FolderTreePathKind.Dropbox
            ? PilotPath.NormalizeDropboxRoot(root) == "/"
                ? "/" + normalizedRelative
                : PilotPath.NormalizeDropboxRoot(root) + "/" + normalizedRelative
            : Normalize(Path.Combine(root, normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private string Relative(string root, string path)
    {
        if (kind == FolderTreePathKind.Dropbox)
        {
            var normalizedRoot = Normalize(root);
            var normalizedPath = Normalize(path);
            return normalizedRoot == "/"
                ? normalizedPath.TrimStart('/')
                : normalizedPath[(normalizedRoot.Length + 1)..];
        }
        return PilotPath.NormalizeRelative(Path.GetRelativePath(root, path));
    }

    private string? CommonParent(IReadOnlyList<string> paths)
    {
        var common = Normalize(paths[0]);
        foreach (var path in paths.Skip(1))
        {
            while (!IsSameOrDescendant(path, common))
            {
                var parent = Parent(common);
                if (parent is null) return null;
                common = parent;
            }
        }
        return common;
    }

    private string? Parent(string path)
    {
        var normalized = Normalize(path);
        if (kind == FolderTreePathKind.Dropbox)
        {
            if (normalized == "/") return null;
            var slash = normalized.LastIndexOf('/');
            return slash <= 0 ? "/" : normalized[..slash];
        }
        var root = Path.GetPathRoot(normalized);
        if (string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase)) return null;
        return Directory.GetParent(normalized)?.FullName;
    }

    private bool Same(string left, string right) => string.Equals(Normalize(left), Normalize(right),
        StringComparison.OrdinalIgnoreCase);

    private bool IsSameOrDescendant(string path, string ancestor) =>
        Same(path, ancestor) || IsDescendant(path, ancestor);

    private bool IsDescendant(string path, string ancestor)
    {
        var normalizedPath = Normalize(path);
        var normalizedAncestor = Normalize(ancestor);
        if (Same(normalizedPath, normalizedAncestor)) return false;
        var separator = kind == FolderTreePathKind.Dropbox ? '/' : Path.DirectorySeparatorChar;
        var prefix = normalizedAncestor.EndsWith(separator) ? normalizedAncestor : normalizedAncestor + separator;
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private int Depth(string path) => kind == FolderTreePathKind.Dropbox
        ? Normalize(path).Count(character => character == '/')
        : Normalize(path).Count(character => character == Path.DirectorySeparatorChar);

    private void SortRules()
    {
        var ordered = _rules.OrderBy(rule => Depth(rule.Path)).ThenBy(rule => rule.Path,
            StringComparer.OrdinalIgnoreCase).ToArray();
        _rules.Clear();
        _rules.AddRange(ordered);
    }
}
