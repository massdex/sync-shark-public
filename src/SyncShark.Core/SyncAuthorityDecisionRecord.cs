namespace Massdex.SyncShark;

// Batch transport for the existing audit table; identity and payload are unchanged.
internal sealed record SyncAuthorityDecisionRecord(string DecisionId, string PairId, string ObjectId,
    string LocalScanGenerationId, string DropboxScanGenerationId, string? SynchronizationPointId,
    string Kind, string Json);

// Batch transport for accepted synchronization points; same fields as the single-point call.
internal sealed record SyncAuthorityPointRecord(string SynchronizationPointId, string PairId, string ObjectId,
    string? LocalVersionId, string? DropboxVersionId, string? LocalMembershipId, string? DropboxMembershipId);

// Batch transport for retained conflicts; same fields as the single-conflict call.
internal sealed record SyncAuthorityConflictRecord(string ConflictId, string PairId, string ObjectId,
    string Reason, string LocalObjectVersionId, string? LocalMembershipId,
    string DropboxObjectVersionId, string? DropboxMembershipId);
