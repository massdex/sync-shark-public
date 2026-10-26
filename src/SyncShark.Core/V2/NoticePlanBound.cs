namespace Massdex.SyncShark.V2;

/// <summary>The explicit cap and displayed plan size belonging to one admission notice.</summary>
public sealed record NoticePlanBound(long RequestedBound, long PlannedCount);
