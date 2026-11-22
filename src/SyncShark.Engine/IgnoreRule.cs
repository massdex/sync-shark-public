namespace Massdex.SyncShark.Engine;

/// <summary>One normalized ignore rule, shared by configuration edits and path filtering.</summary>
public sealed record IgnoreRule(IgnoreRule.KindName Kind, string Value)
{
    /// <summary>Matches a file extension, file name, folder component or file prefix.</summary>
    public enum KindName { FileExtension, FileName, FolderName, FileNamePrefix }

    private const char Separator = ':';

    /// <summary>Parses one 'kind:value;kind:value' rule list as written by 'ignores set'; does not normalize.</summary>
    public static IReadOnlyList<IgnoreRule> Parse(string spec)
    {
        var parsed = new List<IgnoreRule>();
        foreach (var entry in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf(Separator, StringComparison.Ordinal);
            if (separator <= 0 || separator == entry.Length - 1)
                throw new InvalidDataException(Text.Get(TextKey.InvalidIgnoreFormat, (TextArg.Value, entry)));
            var kindText = entry[..separator].Replace("-", string.Empty, StringComparison.Ordinal);
            if (!Enum.TryParse<KindName>(kindText, ignoreCase: true, out var kind))
                throw new InvalidDataException(Text.Get(TextKey.InvalidIgnoreKind, (TextArg.Value, entry[..separator]), (TextArg.Kind, string.Join(',', Enum.GetNames<KindName>()))));
            parsed.Add(new IgnoreRule(kind, entry[(separator + 1)..]));
        }
        return parsed;
    }

    /// <summary>Formats one rule as the canonical 'kind:value' text 'Parse' reads back.</summary>
    public static string Format(IgnoreRule rule) => $"{rule.Kind}{Separator}{rule.Value}";

    /// <summary>Parses an edited rule list, where a whole value of 'none' clears it; does not normalize.</summary>
    public static IReadOnlyList<IgnoreRule> ParseForEdit(string spec)
    {
        if (string.Equals(spec.Trim(), "none", StringComparison.OrdinalIgnoreCase)) return [];
        return Parse(spec);
    }

    public static IReadOnlyList<IgnoreRule> Normalize(IReadOnlyList<IgnoreRule>? rules)
    {
        if (rules is null || rules.Count == 0) return [];
        return rules.Select(rule =>
        {
            if (!Enum.IsDefined(rule.Kind)) throw new InvalidDataException(Text.Get(TextKey.UnsupportedIgnoreKind));
            var value = (rule.Value ?? "").Trim().Normalize().ToLowerInvariant();
            if (rule.Kind == KindName.FileExtension)
            {
                if (value.StartsWith("*.")) value = value[1..];
                if (!value.StartsWith('.')) value = "." + value;
            }
            if (value.Length is 0 or > 255 || value is "." or ".." ||
                value.Any(ch => char.IsControl(ch) || "\\/:*?\"<>|".Contains(ch)) || value.EndsWith('.'))
                throw new InvalidDataException(Text.Get(TextKey.InvalidIgnoreValue));
            return new IgnoreRule(rule.Kind, value);
        }).Distinct().OrderBy(rule => rule.Kind).ThenBy(rule => rule.Value, StringComparer.Ordinal).ToArray();
    }

    public static bool IsIgnored(string relativePath, bool isDirectory, IReadOnlyList<IgnoreRule> rules)
    {
        if (relativePath.Length == 0 || rules.Count == 0) return false;
        // Rules cannot contain control characters, so such a path matches no rule; every other malformed
        // spelling is still a normalisation error.
        if (relativePath.Any(char.IsControl)) return false;
        var path = PathNames.NormalizeInventoryPath(relativePath.Replace('\\', '/'));
        var components = path.Split('/');
        var name = components[^1];
        foreach (var rule in rules)
        {
            if (rule.Kind == KindName.FolderName && components.Take(isDirectory ? components.Length : components.Length - 1)
                .Any(component => string.Equals(component, rule.Value, StringComparison.OrdinalIgnoreCase))) return true;
            if (isDirectory) continue;
            if (rule.Kind == KindName.FileName && string.Equals(name, rule.Value, StringComparison.OrdinalIgnoreCase)) return true;
            if (rule.Kind == KindName.FileNamePrefix && name.StartsWith(rule.Value, StringComparison.OrdinalIgnoreCase)) return true;
            if (rule.Kind == KindName.FileExtension && name.EndsWith(rule.Value, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
