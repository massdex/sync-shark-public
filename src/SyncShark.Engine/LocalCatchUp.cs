namespace Massdex.SyncShark.Engine;

/// <summary>Committed Local source acquisition for one pair generation and source epoch.
/// It attests actual committed acquisition, never an unchanged tree or lasting permission.</summary>
public sealed record LocalCatchUp(
    string PairId,
    long PairGeneration,
    long SourceEpoch,
    NtfsUsnCheckpoint Checkpoint,
    long ThroughWorkId);
