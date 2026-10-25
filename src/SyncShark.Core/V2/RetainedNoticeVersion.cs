namespace Massdex.SyncShark.V2;

/// <summary>Exact per-item provenance for explicitly recovering a newest-wins loser, never a path-based guess.</summary>
public sealed record RetainedNoticeVersion(string ActionId, Side Side, ItemState Loser, ItemState Winner);
