namespace Massdex.SyncShark.Engine;

/// <summary>One in-use item's retry candidate (section 12.1): its exact retained token, pair and dispatch kind, and
/// the pending operation/generation that exact LocalRecovery must still match. It carries no due time, attempt count
/// or other timing state.</summary>
public sealed record ItemRetryCandidate(ItemRetryToken Token, string PairId,
    ItemRetryDispatch Dispatch, string? PendingOperationId, long? PendingGeneration);
