namespace Massdex.SyncShark;

// Owns scan collision admission and the published spelling-preserving dictionary.
internal static class LocalScanEntries
{
    private const string CollisionReason = "A case- or Unicode-normalization-colliding local name is not accepted.";

    // Keep just one canonical index during the walk. Published lookups retain their original
    // spelling and OrdinalIgnoreCase contract; canonical keys never escape the scanner.
    internal static IReadOnlyDictionary<string, PilotLocalEntry> PublishEntries(
        Dictionary<string, PilotLocalEntry> entries) => entries.Values.ToDictionary(
            entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase);

    internal static void AddEntry(IDictionary<string, PilotLocalEntry> entries,
        ISet<string> blockedCanonicalPaths,
        ICollection<PilotDecision> decisions, PilotLocalEntry entry)
    {
        var key = PilotPath.CanonicalComparisonKey(entry.RelativePath);
        if (blockedCanonicalPaths.Contains(key))
        {
            decisions.Add(new PilotDecision(entry.RelativePath, PilotDecisionKind.Blocked,
                CollisionReason));
            return;
        }
        if (entries.TryGetValue(key, out var existing) &&
            !string.Equals(existing.RelativePath, entry.RelativePath, StringComparison.Ordinal))
        {
            var existingPath = existing.RelativePath;
            entries.Remove(key);
            decisions.Add(new PilotDecision(existingPath, PilotDecisionKind.Blocked,
                CollisionReason));
            decisions.Add(new PilotDecision(entry.RelativePath, PilotDecisionKind.Blocked,
                CollisionReason));
            blockedCanonicalPaths.Add(key);
            return;
        }
        entries[key] = entry;
    }

}
