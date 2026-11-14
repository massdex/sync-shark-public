namespace Massdex.SyncShark.Engine;

/// <summary>The exact opposite-side path consumed by a bilateral observation, bounded before that observation.</summary>
public sealed record SelectionPeer(string Key, long ThroughWorkId);
