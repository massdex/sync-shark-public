namespace Massdex.SyncShark.V2;

/// <summary>Durable indexing debt; informational and independent of a sync run's outcome.</summary>
public sealed record LocalIndexStatus(string RootId, bool GapProtected, string Progress, long RehashPending)
{
    public long Remaining { get; init; }
}
