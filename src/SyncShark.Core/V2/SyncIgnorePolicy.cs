namespace Massdex.SyncShark;

public static class SyncIgnorePolicy
{
    private const char Separator = ':';

    /// <summary>Parses one 'kind:value;kind:value' rule list as written by 'ignores set'; does not normalize.</summary>
    public static IReadOnlyList<SyncIgnoreRule> Parse(string spec)
    {
        var parsed = new List<SyncIgnoreRule>();
        foreach (var entry in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf(Separator, StringComparison.Ordinal);
            if (separator <= 0 || separator == entry.Length - 1)
                throw new InvalidDataException($"Ignore rule '{entry}' must be written as kind:value, for " +
                    "example FileExtension:tmp.");
            var kindText = entry[..separator].Replace("-", string.Empty, StringComparison.Ordinal);
            if (!Enum.TryParse<SyncIgnoreKind>(kindText, ignoreCase: true, out var kind))
                throw new InvalidDataException($"Ignore rule kind '{entry[..separator]}' must be one of: " +
                    string.Join(", ", Enum.GetNames<SyncIgnoreKind>()) + ".");
            parsed.Add(new SyncIgnoreRule(kind, entry[(separator + 1)..]));
        }
        return parsed;
    }

    /// <summary>Formats one rule as the canonical 'kind:value' text 'Parse' reads back.</summary>
    public static string Format(SyncIgnoreRule rule) => $"{rule.Kind}{Separator}{rule.Value}";

    /// <summary>Parses an edited rule list, where a whole value of 'none' clears it; does not normalize.</summary>
    public static IReadOnlyList<SyncIgnoreRule> ParseForEdit(string spec)
    {
        if (string.Equals(spec.Trim(), "none", StringComparison.OrdinalIgnoreCase)) return [];
        return Parse(spec);
    }

    public static IReadOnlyList<SyncIgnoreRule> Normalize(IReadOnlyList<SyncIgnoreRule>? rules)
    {
        if (rules is null || rules.Count == 0) return [];
        return rules.Select(rule =>
        {
            if (!Enum.IsDefined(rule.Kind)) throw new InvalidDataException("Choose a supported ignore rule kind.");
            var value = (rule.Value ?? "").Trim().Normalize().ToLowerInvariant();
            if (rule.Kind == SyncIgnoreKind.FileExtension)
            {
                if (value.StartsWith("*.")) value = value[1..];
                if (!value.StartsWith('.')) value = "." + value;
            }
            if (value.Length is 0 or > 255 || value is "." or ".." ||
                value.Any(ch => char.IsControl(ch) || "\\/:*?\"<>|".Contains(ch)) || value.EndsWith('.'))
                throw new InvalidDataException("Enter a file type such as .tmp, a name such as Thumbs.db or node_modules, or a name prefix. Paths and wildcards are not needed.");
            return new SyncIgnoreRule(rule.Kind, value);
        }).Distinct().OrderBy(rule => rule.Kind).ThenBy(rule => rule.Value, StringComparer.Ordinal).ToArray();
    }

    public static bool IsIgnored(string relativePath, bool isDirectory, IReadOnlyList<SyncIgnoreRule> rules)
    {
        if (relativePath.Length == 0 || rules.Count == 0) return false;
        // Rules cannot contain control characters, so such a path matches no rule; every other malformed
        // spelling is still a normalisation error.
        if (relativePath.Any(char.IsControl)) return false;
        var path = V2.PathRules.NormalizeInventoryPath(relativePath.Replace('\\', '/'));
        var components = path.Split('/');
        var name = components[^1];
        foreach (var rule in rules)
        {
            if (rule.Kind == SyncIgnoreKind.FolderName && components.Take(isDirectory ? components.Length : components.Length - 1)
                .Any(component => string.Equals(component, rule.Value, StringComparison.OrdinalIgnoreCase))) return true;
            if (isDirectory) continue;
            if (rule.Kind == SyncIgnoreKind.FileName && string.Equals(name, rule.Value, StringComparison.OrdinalIgnoreCase)) return true;
            if (rule.Kind == SyncIgnoreKind.FileNamePrefix && name.StartsWith(rule.Value, StringComparison.OrdinalIgnoreCase)) return true;
            if (rule.Kind == SyncIgnoreKind.FileExtension && name.EndsWith(rule.Value, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
