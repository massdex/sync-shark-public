namespace Massdex.SyncShark.Engine;

/// <summary>The source responsibility captured by Storage before either listing of one disposable scan.</summary>
public sealed record ScanRecord(Guid Token, long Generation, bool Discovery, bool FirstRun, long ThroughWorkId,
    long LocalEpoch, long LocalRevision, long DropboxEpoch, long DropboxRevision, long? LocalGapRevision)
{
    /// <summary>Null for Repair; otherwise both listings and every scan reader are confined to this subtree.</summary>
    public string? Scope { get; init; }
}
