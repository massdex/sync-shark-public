namespace Massdex.SyncShark.V2;

internal static class DecisionHolds
{
    internal static bool HasRootDecisionHold(IStateStore store, string pairId) =>
        store.Pair(pairId) is not null &&
        store.SyncRetryAt(pairId, string.Empty) is { Kind: "decision" or "deletion-evidence" } or { Held: not null };
}
