namespace Massdex.SyncShark.V2;

/// <summary>Unknown timestamps cannot authorize newest-wins; allow only modest clock skew.</summary>
internal static class ModificationTime
{
    public static bool IsPlausible(DateTimeOffset time, DateTimeOffset observedAt) =>
        observedAt != DateTimeOffset.MinValue && observedAt != DateTimeOffset.MaxValue &&
        // Old but non-sentinel timestamps (including FAT's 1980 default) remain plausible.
        time != DateTimeOffset.MinValue && time != DateTimeOffset.MaxValue &&
        time != DateTimeOffset.UnixEpoch && time != DateTimeOffset.FromFileTime(0) &&
        time - observedAt <= TimeSpan.FromMinutes(5);
}
