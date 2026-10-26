namespace Massdex.SyncShark.V2;

/// <summary>Lazy CLI file inputs; Storage consumes each row inside its existing transaction.</summary>
internal static class DateRepairInputs
{
    internal static IEnumerable<string> Exclusions(string? file)
    {
        if (file is null) yield break;
        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (Path.IsPathRooted(line) || line.Contains(':'))
                throw new SyncCliUsageException("--exclude-list must contain relative pair paths, one per line.");
            yield return PilotPath.NormalizeRelative(line);
        }
    }

    internal static IEnumerable<(string Path, DateTimeOffset Occurred)> Provenance(IReadOnlyList<DateProvenanceSource>? sources)
    {
        foreach (var source in sources ?? [])
            foreach (var line in File.ReadLines(source.File))
                if (DateProvenanceLog.TryParse(line, source.Prefix, out var path, out var occurred))
                    yield return (path, occurred);
    }
}
