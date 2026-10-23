namespace Massdex.SyncShark.V2;

/// <summary>One small observation per physical volume, independent of pair/root/run.</summary>
public sealed record JournalSizingState(ulong JournalId, long NextUsn, DateTimeOffset Observed,
    double BytesPerHour = 0, double LongestGapHours = 0, bool Never = false,
    DateTimeOffset? NotBefore = null, long Requested = 0, string? NoticePair = null, long Allocation = JournalPolicy.Delta);
