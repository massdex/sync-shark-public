using System.Globalization;
using System.Text.RegularExpressions;

namespace Massdex.SyncShark.V2;

internal static class DateProvenanceLog
{
    // A throttled progress stream proves only the paths actually printed, never intervening items.
    private static readonly Regex Applying = new(
        @"^Sync Shark: \[(?<time>[^\]]+)\] Applying: (?<path>.+) \([0-9,]+/[0-9,]+\)(?: \(\+[0-9,]+ files inside deleted folders\))?$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static IReadOnlyList<DateProvenanceSource> Sources(IReadOnlyList<string>? logs, IReadOnlyList<string>? prefixes)
    {
        logs ??= []; prefixes ??= [];
        if (prefixes.Count > 0 && prefixes.Count != 1 && prefixes.Count != logs.Count || logs.Count == 0 && prefixes.Count > 0)
            throw new SyncCliUsageException("Supply one --provenance-prefix for all logs, or one per --provenance-log in matching order. Use . for this pair's root.");
        return logs.Select((file, index) => new DateProvenanceSource(Path.GetFullPath(file),
            NormalizePrefix(prefixes.Count == 0 ? "." : prefixes[prefixes.Count == 1 ? 0 : index]))).ToArray();
    }

    private static string NormalizePrefix(string prefix) => prefix == "." ? "" : Relative(prefix).TrimEnd('/');

    private static string Relative(string path)
    {
        if (Path.IsPathRooted(path) || path.Contains(':') || path.Replace('\\', '/').Split('/').Any(p => p is "." or ".."))
            throw new SyncCliUsageException("Provenance paths and prefixes must be relative to the selected pair, without traversal.");
        return PilotPath.NormalizeRelative(path);
    }

    internal static bool TryParse(string line, string prefix, out string path, out DateTimeOffset occurred)
    {
        path = ""; occurred = default;
        var match = Applying.Match(line.TrimEnd());
        if (!match.Success) return false;
        var stamp = match.Groups["time"].Value;
        if (!Regex.IsMatch(stamp, @"(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) ||
            !DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out occurred)) return false;
        try
        {
            var relative = Relative(match.Groups["path"].Value);
            var normalizedPrefix = NormalizePrefix(prefix.Length == 0 ? "." : prefix);
            path = normalizedPrefix.Length == 0 ? relative : normalizedPrefix + "/" + relative;
            return path.Length > 0;
        }
        catch (Exception error) when (error is ArgumentException or SyncCliUsageException) { return false; }
    }
}
