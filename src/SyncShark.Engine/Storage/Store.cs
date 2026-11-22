namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Store operations for the storage module.</summary>
public sealed class Store : IStore
{
    private Microsoft.Data.Sqlite.SqliteConnection Connection { get; }
    private readonly PairTable pairs = new();
    private readonly RecordTable records = new();
    private readonly NoticeTable notices = new();
    private readonly ScanTable scan = new();
    private readonly EngineSettings settings;
    /// <summary>Pairs operation on the supplied facts.</summary>
    public IAsyncEnumerable<PairConfig> PairsAsync(CancellationToken ct) => pairs.PairsAsync(Connection, settings.StoragePageSize, ct);
    /// <summary>Pair operation on the supplied facts.</summary>
    public Task<PairConfig?> PairAsync(string pairId, CancellationToken ct) => pairs.ReadAsync(Connection, pairId, ct);
    /// <summary>SavePair operation on the supplied facts.</summary>
    public Task SavePairAsync(PairConfig pair, CancellationToken ct, IReadOnlyList<(string Owner, Scope Scope)>? returnedScopes = null) => throw new NotImplementedException();
    /// <summary>Check eligibility and publish through PairTable in one immediate write transaction.</summary>
    public async Task<TextKey?> RemovePairAsync(string pairId, CancellationToken ct, IReadOnlyList<(string Owner, Scope Scope)>? returnedScopes = null)
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        var cause=await pairs.PublishAsync(tx,pairId,null,returnedScopes??[],ct);
        if(cause is null) tx.Commit();
        return cause;
    }
    /// <summary>Find operation on the supplied facts.</summary>
    public Task<SyncedEntry?> FindAsync(string pairId, Side side, string key, CancellationToken ct) => records.FindAsync(Connection, pairId, side, key, ct);
    /// <summary>ById operation on the supplied facts.</summary>
    public IAsyncEnumerable<SyncedEntry> ByIdAsync(string pairId, Side side, string id, CancellationToken ct) => records.ByIdAsync(Connection, pairId, side, id, settings.StoragePageSize, ct);
    /// <summary>Descendants operation on the supplied facts.</summary>
    public IAsyncEnumerable<SyncedEntry> DescendantsAsync(string pairId, Side side, string key, CancellationToken ct) => records.DescendantsAsync(Connection, pairId, side, key, settings.StoragePageSize, ct);
    /// <summary>Commit operation on the supplied facts.</summary>
    public Task CommitAsync(string pairId, SyncedEntry? before, SyncedEntry? after, JournalPosition? publication, string? spentDeletedKey, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Notices operation on the supplied facts.</summary>
    public IAsyncEnumerable<Notice> NoticesAsync(string pairId, CancellationToken ct) => notices.ListAsync(Connection, pairId, settings.StoragePageSize, ct);
    /// <summary>SaveNotice operation on the supplied facts.</summary>
    public Task SaveNoticeAsync(string pairId, Notice notice, CancellationToken ct) => notices.SaveNoticeAsync(Connection, pairId, notice, ct);
    /// <summary>CloseNotice operation on the supplied facts.</summary>
    public Task CloseNoticeAsync(string pairId, Notice expected, CancellationToken ct) => notices.CloseNoticeAsync(Connection, pairId, expected, ct);
    /// <summary>Position operation on the supplied facts.</summary>
    public Task<JournalPosition?> PositionAsync(string volume, CancellationToken ct) => pairs.PositionAsync(Connection, volume, ct);
    /// <summary>SavePosition operation on the supplied facts.</summary>
    public Task SavePositionAsync(string volume, JournalPosition position, CancellationToken ct) => pairs.SavePositionAsync(Connection, volume, position, ct);
    /// <summary>Cursor operation on the supplied facts.</summary>
    public Task<string?> CursorAsync(string pairId, CancellationToken ct) => pairs.CursorAsync(Connection, pairId, ct);
    /// <summary>SaveCursor operation on the supplied facts.</summary>
    public Task SaveCursorAsync(string pairId, string cursor, CancellationToken ct) => pairs.SaveCursorAsync(Connection, pairId, cursor, ct);
    /// <summary>OweRepair operation on the supplied facts.</summary>
    public Task<long> OweRepairAsync(string pairId, Side side, string reason, CancellationToken ct) => pairs.OweRepairAsync(Connection, pairId, side, reason, ct);
    /// <summary>FinishRepair operation on the supplied facts.</summary>
    public Task<(long Revision, string? Reason)> ReadRepairAsync(string pairId, Side side, CancellationToken ct) => pairs.ReadRepairAsync(Connection, pairId, side, ct);
    public async Task<bool> FinishRepairAsync(string pairId, (long Local,long Dropbox) revisions, string completedRepairJson, CancellationToken ct)
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        var matched=await pairs.FinishRepairAsync(tx, pairId, revisions, completedRepairJson, ct);
        if(matched) tx.Commit();
        return matched;
    }
    /// <summary>Retire only the matching mapped scope at the owner’s Dropbox revision. No first-run bit or full summary is published.</summary>
    public async Task<bool> FinishRepairAsync(string pairId, Scope scope, long revision, CancellationToken ct)
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        var matched=await pairs.FinishRepairAsync(tx, pairId, scope, revision, ct);
        if(matched) tx.Commit();
        return matched;
    }
    /// <summary>SaveJob operation on the supplied facts.</summary>
    public Task SaveJobAsync(AcceptedJob job, CancellationToken ct) => pairs.SaveJobAsync(Connection, job, ct);
    /// <summary>Jobs operation on the supplied facts.</summary>
    public IAsyncEnumerable<AcceptedJob> JobsAsync(string pairId, CancellationToken ct) => pairs.JobsAsync(Connection, pairId, ct);
    /// <summary>SettleJob operation on the supplied facts.</summary>
    public Task SettleJobAsync(AcceptedJob job, CancellationToken ct) => pairs.SettleJobAsync(Connection, job, ct);
    /// <summary>Arm operation on the supplied facts.</summary>
    public Task ArmAsync(string pairId, RemovalSelection victim, StageProof? stage, CancellationToken ct) => pairs.ArmAsync(Connection, pairId, victim, stage, ct);
    /// <summary>Armed operation on the supplied facts.</summary>
    public Task<(RemovalSelection Victim, StageProof? Stage)?> ArmedAsync(string pairId, CancellationToken ct) => pairs.ArmedAsync(Connection, pairId, ct);
    /// <summary>Disarm operation on the supplied facts.</summary>
    public Task DisarmAsync(string pairId, RemovalSelection expected, StageProof? expectedStage, CancellationToken ct) => pairs.DisarmAsync(Connection, pairId, expected, expectedStage, ct);
    /// <summary>AddScan operation on the supplied facts.</summary>
    public Task AddScanAsync(string pairId, Guid scanId, Side side, IAsyncEnumerable<Listed> rows, Func<string>? terminalHistoryCursor, CancellationToken ct) => scan.AddScanAsync(Connection, pairId, scanId, side, rows, terminalHistoryCursor, settings.StoreBatchRows, ct);
    /// <summary>Compare operation on the supplied facts.</summary>
    public IAsyncEnumerable<(Row Row, IReadOnlyList<(Notice Notice, NoticeItem? Member, bool Covered, bool Touched)> Notices)> CompareAsync(string pairId, Guid scanId, Scope? scope, Func<Side,string,(bool Covered,bool Touched)> evidence, CancellationToken ct) => scan.CompareAsync(Connection, pairId, scanId, scope, evidence, settings.StoragePageSize, ct);
    /// <summary>ClearScan operation on the supplied facts.</summary>
    public Task ClearScanAsync(string pairId, Guid scanId, CancellationToken ct) => scan.ClearScanAsync(Connection, pairId, scanId, ct);
    /// <summary>Dispose operation on the supplied facts.</summary>
    public void Dispose() => Connection.Dispose();

    /// <summary>Opens the selected store image; read-only access never creates one.</summary>
    public Store(string path, bool readOnly, EngineSettings settings)
    {
        this.settings=settings;
        Connection=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder {
            DataSource=path, Mode=readOnly ? Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly : Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite
        }.ToString());
    }


    /// <summary>Read the persisted full-first-Repair bit and last summary without inferring them from record count.</summary>
    public Task<(bool FirstRepairDone, string? LastRepairJson)> RepairStateAsync(string pairId, CancellationToken ct) => pairs.RepairStateAsync(Connection, pairId, ct);

    /// <summary>Read deleted-name publication state independently of the change-feed cursor.</summary>
    public Task<(string? Cursor, bool Owed)> DeletedHistoryAsync(string pairId, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Fold only this completed history acquisition and publish its own terminal cursor with it. An ordinary or incomplete scan cannot publish history.</summary>
    public Task PublishDeletedHistoryAsync(string pairId, Guid scanId, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Read the unspent name from the existing delta, scratch and block homes.</summary>
    public Task<bool> DeletedNameAsync(string pairId, Guid? scanId, string key, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Project both physical endpoints of a settled folder move set-wise in one transaction.</summary>
    public Task ProjectSubtreeAsync(string pairId, Scope from, Scope to, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Indexed exact-path notice lookup; no whole-pair notice scan on the item path.</summary>
    public IAsyncEnumerable<Notice> NoticesAtAsync(string pairId, string pathKey, CancellationToken ct) => notices.NoticesAtAsync(Connection, pairId, pathKey, ct);
    /// <summary>On lost journal continuity only, install the fresh volume boundary and increment the Local Repair revision/reason of every affected pair in one transaction.</summary>
    public async Task ResetJournalAsync(string volume, JournalPosition fresh, IReadOnlyList<string> affectedPairs, string reason, CancellationToken ct)
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        await pairs.ResetJournalAsync(tx, volume, fresh, affectedPairs, reason, ct);
        tx.Commit();
    }
    /// <summary>On Dropbox reset only, install its fresh cursor and increment that pair’s Dropbox Repair revision/reason in one transaction.</summary>
    public async Task ResetCursorAsync(string pairId, string fresh, string reason, CancellationToken ct)
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        await pairs.ResetCursorAsync(tx, pairId, fresh, reason, ct);
        tx.Commit();
    }
    /// <summary>Stream the existing scan’s move candidates before ordinary differences, ordered by shallowest candidate of the binding then by binding so all its observed sides are adjacent.</summary>
    public IAsyncEnumerable<RenameCandidate> RenamesAsync(string pairId, Guid scanId, Scope? scope, CancellationToken ct) => scan.RenamesAsync(Connection, pairId, scanId, scope, settings.StoragePageSize, ct);
}
