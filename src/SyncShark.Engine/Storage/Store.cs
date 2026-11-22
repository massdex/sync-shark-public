namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Store operations for the storage module.</summary>
public sealed class Store : IStore
{
    /// <summary>Pairs operation on the supplied facts.</summary>
    public IAsyncEnumerable<PairConfig> PairsAsync(CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Pair operation on the supplied facts.</summary>
    public Task<PairConfig?> PairAsync(string pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>SavePair operation on the supplied facts.</summary>
    public Task SavePairAsync(PairConfig pair, CancellationToken ct, IReadOnlyList<(string Owner, Scope Scope)>? returnedScopes = null) => throw new NotImplementedException();
    /// <summary>RemovePair operation on the supplied facts.</summary>
    public Task<bool> RemovePairAsync(string pairId, CancellationToken ct, IReadOnlyList<(string Owner, Scope Scope)>? returnedScopes = null) => throw new NotImplementedException();
    /// <summary>Find operation on the supplied facts.</summary>
    public Task<SyncedEntry?> FindAsync(string pairId, Side side, string key, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>ById operation on the supplied facts.</summary>
    public IAsyncEnumerable<SyncedEntry> ByIdAsync(string pairId, Side side, string id, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Descendants operation on the supplied facts.</summary>
    public IAsyncEnumerable<SyncedEntry> DescendantsAsync(string pairId, Side side, string key, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Commit operation on the supplied facts.</summary>
    public Task CommitAsync(string pairId, SyncedEntry? before, SyncedEntry? after, JournalPosition? publication, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Notices operation on the supplied facts.</summary>
    public IAsyncEnumerable<Notice> NoticesAsync(string pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>SaveNotice operation on the supplied facts.</summary>
    public Task SaveNoticeAsync(string pairId, Notice notice, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>CloseNotice operation on the supplied facts.</summary>
    public Task CloseNoticeAsync(string pairId, Notice expected, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Position operation on the supplied facts.</summary>
    public Task<JournalPosition?> PositionAsync(string volume, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>SavePosition operation on the supplied facts.</summary>
    public Task SavePositionAsync(string volume, JournalPosition position, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Cursor operation on the supplied facts.</summary>
    public Task<string?> CursorAsync(string pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>SaveCursor operation on the supplied facts.</summary>
    public Task SaveCursorAsync(string pairId, string cursor, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>OweRepair operation on the supplied facts.</summary>
    public Task<long> OweRepairAsync(string pairId, Side side, string reason, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>FinishRepair operation on the supplied facts.</summary>
    public Task<(long Revision, string? Reason)> ReadRepairAsync(string pairId, Side side, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> FinishRepairAsync(string pairId, Side side, long revision, CancellationToken ct, Scope? scope = null, string? completedRepairJson = null) => throw new NotImplementedException();
    /// <summary>SaveJob operation on the supplied facts.</summary>
    public Task SaveJobAsync(AcceptedJob job, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Jobs operation on the supplied facts.</summary>
    public IAsyncEnumerable<AcceptedJob> JobsAsync(string pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>SettleJob operation on the supplied facts.</summary>
    public Task SettleJobAsync(AcceptedJob job, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Arm operation on the supplied facts.</summary>
    public Task ArmAsync(string pairId, RemovalSelection victim, StageProof? stage, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Armed operation on the supplied facts.</summary>
    public Task<(RemovalSelection Victim, StageProof? Stage)?> ArmedAsync(string pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Disarm operation on the supplied facts.</summary>
    public Task DisarmAsync(string pairId, RemovalSelection expected, StageProof? expectedStage, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>AddScan operation on the supplied facts.</summary>
    public Task AddScanAsync(string pairId, Side side, IAsyncEnumerable<Listed> rows, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Compare operation on the supplied facts.</summary>
    public IAsyncEnumerable<(Row Row, IReadOnlyList<(Notice Notice, NoticeItem? Member, bool Covered, bool Touched)> Notices)> CompareAsync(string pairId, Scope? scope, Func<Side,string,(bool Covered,bool Touched)> evidence, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>ClearScan operation on the supplied facts.</summary>
    public Task ClearScanAsync(string pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Dispose operation on the supplied facts.</summary>
    public void Dispose() => throw new NotImplementedException();

    /// <summary>Opens the selected store image; read-only access never creates one.</summary>
    public Store(string path, bool readOnly) => throw new NotImplementedException();


    /// <summary>Read the persisted full-first-Repair bit and last summary without inferring them from record count.</summary>
    public Task<(bool FirstRepairDone, string? LastRepairJson)> RepairStateAsync(string pairId, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Read deleted-name publication state independently of the change-feed cursor.</summary>
    public Task<(string? Cursor, bool Owed)> DeletedHistoryAsync(string pairId, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Publish the completed deleted-name listing and its cursor together.</summary>
    public Task PublishDeletedHistoryAsync(string pairId, string cursor, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Read the unspent name from the existing delta, scratch and block homes.</summary>
    public Task<bool> DeletedNameAsync(string pairId, string key, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Project both physical endpoints of a settled folder move set-wise in one transaction.</summary>
    public Task ProjectSubtreeAsync(string pairId, Scope from, Scope to, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Indexed exact-path notice lookup; no whole-pair notice scan on the item path.</summary>
    public IAsyncEnumerable<Notice> NoticesAtAsync(string pairId, string pathKey, CancellationToken ct) => throw new NotImplementedException();
}
