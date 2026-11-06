namespace Massdex.SyncShark.Engine;

/// <summary>One acquired change-work row as the engine replays it. WorkId is the durable receipt identity; PairId,
/// PairGeneration and SourceEpoch are the immutable acquisition applicability facts it was acquired under, never
/// completion state. Notice is the ordered change evidence and Before the saved preimage of the last synced facts
/// (null when none was saved).</summary>
public sealed record PendingChange(long WorkId, string PairId, long PairGeneration, long SourceEpoch,
    ChangeNotice Notice, SyncedEntry? Before)
{
    /// <summary>Existing issue-retained work, projected by Storage rather than persisted again.</summary>
    internal bool IsResidual { get; init; }
}
