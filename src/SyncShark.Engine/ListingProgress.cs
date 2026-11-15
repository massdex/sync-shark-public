namespace Massdex.SyncShark.Engine;

/// <summary>Consumed listing observations, not unique paths: unreadable local entries may be observed again.</summary>
public readonly record struct ListingProgress(Side Side, string Path, long Units, long Observations, long Baseline);
