namespace Massdex.SyncShark.Engine;

/// <summary>One duplicate-folder obligation, as its page read found it: its key, and the Failure that holds it, if
/// any (the hold commit's expected issue).</summary>
public sealed record DuplicateRepair(DuplicateRepairKey Key, long? IssueId);
