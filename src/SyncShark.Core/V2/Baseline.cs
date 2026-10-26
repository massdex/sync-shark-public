namespace Massdex.SyncShark.V2;

/// <summary>The last-synced state of one file identity on both sides.</summary>
public sealed record Baseline(string PairId, string ObjectKey, ItemState A, ItemState B, long Gen);
