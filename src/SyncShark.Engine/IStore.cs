namespace Massdex.SyncShark.Engine;

/// <summary>Pair-scoped durable facts and streamed Repair rows. Source boundaries advance only on settlement.</summary>
public interface IStore : IDisposable
{
    IAsyncEnumerable<PairConfig> PairsAsync(CancellationToken ct);
    Task<PairConfig?> PairAsync(string pairId, CancellationToken ct);
    /// <summary>Publishes configuration and each distinct returned footprint at its owner in one transaction, advancing the existing Repair revision even if the same scope was already owed.</summary>
    Task SavePairAsync(PairConfig pair, CancellationToken ct, IReadOnlyList<(string Owner, Scope Scope)>? returnedScopes = null);
    /// <summary>Removes an eligible pair and publishes each returned scope to its surviving owner atomically. Refusal changes neither.</summary>
    Task<bool> RemovePairAsync(string pairId, CancellationToken ct, IReadOnlyList<(string Owner, Scope Scope)>? returnedScopes = null);
    Task<SyncedEntry?> FindAsync(string pairId, Side side, string key, CancellationToken ct);
    IAsyncEnumerable<SyncedEntry> ByIdAsync(string pairId, Side side, string id, CancellationToken ct);
    IAsyncEnumerable<SyncedEntry> DescendantsAsync(string pairId, Side side, string key, CancellationToken ct);
    /// <summary>Commit the record change and spend the exact acknowledged historical deletion key atomically, even when neither side has a previous binding.</summary>
    Task CommitAsync(string pairId, SyncedEntry? before, SyncedEntry? after, JournalPosition? publication, string? spentDeletedKey, CancellationToken ct);
    IAsyncEnumerable<Notice> NoticesAsync(string pairId, CancellationToken ct);
    Task SaveNoticeAsync(string pairId, Notice notice, CancellationToken ct);
    Task CloseNoticeAsync(string pairId, Notice expected, CancellationToken ct);
    Task<JournalPosition?> PositionAsync(string volume, CancellationToken ct);
    Task SavePositionAsync(string volume, JournalPosition position, CancellationToken ct);
    Task<string?> CursorAsync(string pairId, CancellationToken ct);
    Task SaveCursorAsync(string pairId, string cursor, CancellationToken ct);
    Task<long> OweRepairAsync(string pairId, Side side, string reason, CancellationToken ct);
    /// <summary>Read the existing source revision and whole-pair reason before acquisition.</summary>
    Task<(long Revision, string? Reason)> ReadRepairAsync(string pairId, Side side, CancellationToken ct);
    /// <summary>Only the completion ending the entire two-sided Repair can publish its required summary and first_repair_done. Match both captured revisions and clear the observed reasons in the same transaction; a stale completion publishes nothing.</summary>
    Task<bool> FinishRepairAsync(string pairId, (long Local,long Dropbox) revisions, string completedRepairJson, CancellationToken ct);
    /// <summary>Retire only the matching mapped scope at the owner’s Dropbox revision. No first-run bit or full summary is published.</summary>
    Task<bool> FinishRepairAsync(string pairId, Scope scope, long revision, CancellationToken ct);
    Task SaveJobAsync(AcceptedJob job, CancellationToken ct);
    IAsyncEnumerable<AcceptedJob> JobsAsync(string pairId, CancellationToken ct);
    Task SettleJobAsync(AcceptedJob job, CancellationToken ct);
    Task ArmAsync(string pairId, RemovalSelection victim, StageProof? stage, CancellationToken ct);
    Task<(RemovalSelection Victim, StageProof? Stage)?> ArmedAsync(string pairId, CancellationToken ct);
    Task DisarmAsync(string pairId, RemovalSelection expected, StageProof? expectedStage, CancellationToken ct);
    /// <summary>Capture names only for the Dropbox history acquisition; read its terminal cursor only after the listing finishes successfully.</summary>
    Task AddScanAsync(string pairId, Guid scanId, Side side, IAsyncEnumerable<Listed> rows, Func<string>? terminalHistoryCursor, CancellationToken ct);
    IAsyncEnumerable<(Row Row, IReadOnlyList<(Notice Notice, NoticeItem? Member, bool Covered, bool Touched)> Notices)> CompareAsync(string pairId, Guid scanId, Scope? scope, Func<Side,string,(bool Covered,bool Touched)> evidence, CancellationToken ct);
    Task ClearScanAsync(string pairId, Guid scanId, CancellationToken ct);

    /// <summary>Read the persisted full-first-Repair bit and last summary without inferring them from record count.</summary>
    Task<(bool FirstRepairDone, string? LastRepairJson)> RepairStateAsync(string pairId, CancellationToken ct);

    /// <summary>Read deleted-name publication state independently of the change-feed cursor.</summary>
    Task<(string? Cursor, bool Owed)> DeletedHistoryAsync(string pairId, CancellationToken ct);

    /// <summary>Fold only this completed history acquisition and publish its own terminal cursor with it. An ordinary or incomplete scan cannot publish history.</summary>
    Task PublishDeletedHistoryAsync(string pairId, Guid scanId, CancellationToken ct);

    /// <summary>Read the unspent name from the existing delta, scratch and block homes.</summary>
    Task<bool> DeletedNameAsync(string pairId, Guid? scanId, string key, CancellationToken ct);

    /// <summary>Project both physical endpoints of a settled folder move set-wise in one transaction.</summary>
    Task ProjectSubtreeAsync(string pairId, Scope from, Scope to, CancellationToken ct);

    /// <summary>Indexed exact-path notice lookup; no whole-pair notice scan on the item path.</summary>
    IAsyncEnumerable<Notice> NoticesAtAsync(string pairId, string pathKey, CancellationToken ct);
    /// <summary>On lost journal continuity only, install the fresh volume boundary and increment the Local Repair revision/reason of every affected pair in one transaction.</summary>
    Task ResetJournalAsync(string volume, JournalPosition fresh, IReadOnlyList<string> affectedPairs, string reason, CancellationToken ct);
    /// <summary>On Dropbox reset only, install its fresh cursor and increment that pair’s Dropbox Repair revision/reason in one transaction.</summary>
    Task ResetCursorAsync(string pairId, string fresh, string reason, CancellationToken ct);
    /// <summary>Stream the existing scan’s move candidates before ordinary differences, ordered by shallowest candidate of the binding then by binding so all its observed sides are adjacent.</summary>
    IAsyncEnumerable<RenameCandidate> RenamesAsync(string pairId, Guid scanId, Scope? scope, CancellationToken ct);
}
