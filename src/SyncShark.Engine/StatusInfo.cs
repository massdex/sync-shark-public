namespace Massdex.SyncShark.Engine;

/// <summary>One pair's status for `engine status` (section 8.6).</summary>
public sealed record StatusInfo(PairConfig Pair, RepairSummary? LastRepair, IReadOnlyDictionary<IssueKind, int> IssueCounts,
    bool PendingPresent, bool FirstRepairDone);
