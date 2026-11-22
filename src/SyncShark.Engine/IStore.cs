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
    Task CommitAsync(string pairId, SyncedEntry? before, SyncedEntry? after, JournalPosition? publication, CancellationToken ct);
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
    /// <summary>Call only on completed comparison; scoped completion uses the owner’s Dropbox source row. Full completion records its summary and first_repair_done atomically. Compare the original revision and retire only the matching scope, or the whole-pair reason when scope is null. A stale completion does nothing.</summary>
    Task<bool> FinishRepairAsync(string pairId, Side side, long revision, CancellationToken ct, Scope? scope = null, string? completedRepairJson = null);
    Task SaveJobAsync(AcceptedJob job, CancellationToken ct);
    IAsyncEnumerable<AcceptedJob> JobsAsync(string pairId, CancellationToken ct);
    Task SettleJobAsync(AcceptedJob job, CancellationToken ct);
    Task ArmAsync(string pairId, RemovalSelection victim, StageProof? stage, CancellationToken ct);
    Task<(RemovalSelection Victim, StageProof? Stage)?> ArmedAsync(string pairId, CancellationToken ct);
    Task DisarmAsync(string pairId, RemovalSelection expected, StageProof? expectedStage, CancellationToken ct);
    Task AddScanAsync(string pairId, Side side, IAsyncEnumerable<Listed> rows, CancellationToken ct);
    IAsyncEnumerable<(Row Row, IReadOnlyList<(Notice Notice, NoticeItem? Member, bool Covered, bool Touched)> Notices)> CompareAsync(string pairId, Scope? scope, Func<Side,string,(bool Covered,bool Touched)> evidence, CancellationToken ct);
    Task ClearScanAsync(string pairId, CancellationToken ct);

    /// <summary>Read the persisted full-first-Repair bit and last summary without inferring them from record count.</summary>
    Task<(bool FirstRepairDone, string? LastRepairJson)> RepairStateAsync(string pairId, CancellationToken ct);

    /// <summary>Read deleted-name publication state independently of the change-feed cursor.</summary>
    Task<(string? Cursor, bool Owed)> DeletedHistoryAsync(string pairId, CancellationToken ct);

    /// <summary>Publish the completed deleted-name listing and its cursor together.</summary>
    Task PublishDeletedHistoryAsync(string pairId, string cursor, CancellationToken ct);

    /// <summary>Read the unspent name from the existing delta, scratch and block homes.</summary>
    Task<bool> DeletedNameAsync(string pairId, string key, CancellationToken ct);

    /// <summary>Project both physical endpoints of a settled folder move set-wise in one transaction.</summary>
    Task ProjectSubtreeAsync(string pairId, Scope from, Scope to, CancellationToken ct);

    /// <summary>Indexed exact-path notice lookup; no whole-pair notice scan on the item path.</summary>
    IAsyncEnumerable<Notice> NoticesAtAsync(string pairId, string pathKey, CancellationToken ct);
}
