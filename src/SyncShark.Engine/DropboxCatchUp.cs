namespace Massdex.SyncShark.Engine;

/// <summary>Committed Dropbox source acquisition for one pair generation and source epoch.
/// It attests actual committed acquisition, never an unchanged tree or lasting permission.</summary>
public sealed record DropboxCatchUp(
    string PairId,
    long PairGeneration,
    long SourceEpoch,
    string Cursor,
    long ThroughWorkId);
