namespace Massdex.SyncShark.V2;

/// <summary>
/// Validates the 'pairs create --exclude' paths (exclusions.md, decision 58, minimal slice): each is pair-relative,
/// plain "excluded" on both sides, and follows the first-sync choice prefix rules. Every refusal is a
/// <see cref="SyncCliUsageException"/> and nothing is created.
/// </summary>
internal static class CreateExclusions
{
    private const string Suffix = " Nothing was created.";
    private const string NeedsPath = "The --exclude path cannot be empty, start with '/', contain '\\', or hold a " +
        "'.' or '..' or empty segment." + Suffix;
    private const string NeedsSelectionPath = "The --path must be a pair-relative folder: it cannot be empty, " +
        "start with '/', contain '\\', or hold a '.' or '..' or empty segment. Nothing was changed.";

    /// <summary>The validated paths without a trailing '/', in the order given, duplicates (ignoring case) dropped.</summary>
    internal static IReadOnlyList<string> Validate(IReadOnlyList<string> paths) => Validate(paths, NeedsPath);

    /// <summary>
    /// One 'folders include/exclude' path, refused with the selection verb's own wording and normalized the same
    /// way 'pairs create --exclude' is: pair-relative, '/'-separated, no dot or empty segments, no trailing '/'.
    /// </summary>
    internal static string ValidateSelection(string path) => Validate([path], NeedsSelectionPath)[0];

    private static IReadOnlyList<string> Validate(IReadOnlyList<string> paths, string refusal)
    {
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (path.Length == 0 || path[0] == '/' || path.Contains('\\')) throw new SyncCliUsageException(refusal);
            var body = path.EndsWith('/') ? path[..^1] : path;
            foreach (var segment in body.Split('/'))
                if (segment.Length == 0 || segment == "." || segment == "..") throw new SyncCliUsageException(refusal);
            if (seen.Add(body)) kept.Add(body);
        }
        return kept;
    }
}