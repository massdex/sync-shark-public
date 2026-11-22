using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Opens existing store images and coordinates transactional and streaming operations.</summary>
public sealed class Store : IStore
{
    private SqliteConnection Connection { get; }
    private readonly SemaphoreSlim gate = new(1);
    private readonly string connectionString;
    private readonly string scratch;
    private bool attached;
    private readonly Committer committer=new();
    private readonly PairTable pairs = new();
    private readonly RecordTable records = new();
    private readonly NoticeTable notices = new();
    private readonly ScanTable scan = new();
    private readonly EngineSettings settings;
    /// <summary>Stream configured pairs.</summary>
    public IAsyncEnumerable<PairConfig> PairsAsync(CancellationToken ct) => Stream(db => pairs.PairsAsync(db, settings.StoragePageSize, ct), false, ct);
    /// <summary>Read one pair by identifier.</summary>
    public Task<PairConfig?> PairAsync(string pairId, CancellationToken ct) => Locked(() => pairs.ReadAsync(Connection, pairId, ct), ct);
    /// <summary>Publish the pair and returned scopes atomically.</summary>
    public Task SavePairAsync(PairConfig pair, CancellationToken ct, IReadOnlyList<(string Owner, Scope Scope)>? returnedScopes = null) => Locked(async () =>
    {
        using var tx = Connection.BeginTransaction(deferred: false);
        if (await pairs.PublishAsync(tx, pair.PairId, pair, returnedScopes ?? [], ct) is {} cause)
            throw new EngineRequestException(cause);
        tx.Commit();
    }, ct);
    /// <summary>Check eligibility and publish through PairTable in one immediate write transaction.</summary>
    public Task<TextKey?> RemovePairAsync(string pairId, CancellationToken ct, IReadOnlyList<(string Owner, Scope Scope)>? returnedScopes = null) => Locked(async () =>
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        var cause=await pairs.PublishAsync(tx,pairId,null,returnedScopes??[],ct);
        if(cause is null) tx.Commit();
        return cause;
    }, ct);
    /// <summary>Read one binding by its endpoint key.</summary>
    public Task<SyncedEntry?> FindAsync(string pairId, Side side, string key, CancellationToken ct) => Locked(() => records.FindAsync(Connection, pairId, side, key, ct), ct);
    /// <summary>Stream bindings sharing an endpoint identity.</summary>
    public IAsyncEnumerable<SyncedEntry> ByIdAsync(string pairId, Side side, string id, CancellationToken ct) => Stream(db => records.ByIdAsync(db, pairId, side, id, settings.StoragePageSize, ct), false, ct);
    /// <summary>Stream recorded descendants.</summary>
    public IAsyncEnumerable<SyncedEntry> DescendantsAsync(string pairId, Side side, string key, CancellationToken ct) => Stream(db => records.DescendantsAsync(db, pairId, side, key, settings.StoragePageSize, ct), false, ct);
    /// <summary>Commit the binding and its spent deletion together. Producers await this task before submitting more work: it waits for a batch slot and completes only after settlement.</summary>
    public Task CommitAsync(string pairId, SyncedEntry? before, SyncedEntry? after, JournalPosition? publication, string? spentDeletedKey, CancellationToken ct) => committer.QueueAsync(Connection, gate, settings.StoreBatchRows, pairId, before, after, publication, spentDeletedKey, () => TouchChange(pairId, before, after, scan.Touch), ct);
    /// <summary>Stream notices for a pair.</summary>
    public IAsyncEnumerable<Notice> NoticesAsync(string pairId, CancellationToken ct) => Stream(db => notices.ListAsync(db, pairId, settings.StoragePageSize, ct), false, ct);
    /// <summary>Upsert one notice slot and its members.</summary>
    public Task SaveNoticeAsync(string pairId, Notice notice, CancellationToken ct) => Locked(async () =>
    {
        var prior = await PriorNoticeAsync(pairId, notice, ct);
        await notices.SaveNoticeAsync(Connection, pairId, notice, ct);
        TouchNotice(pairId, prior);
        TouchNotice(pairId, notice);
    }, ct);
    /// <summary>Change visibility only for the reviewed notice; publish its freshness change before releasing the writer.</summary>
    public Task<bool> UpdateNoticeVisibilityAsync(string pairId, Notice expected, bool ignored, CancellationToken ct) => Locked(async () =>
    {
        var matched = await notices.UpdateNoticeVisibilityAsync(Connection, pairId, expected, ignored, ct);
        if (matched) TouchNotice(pairId, expected);
        return matched;
    }, ct);
    /// <summary>Close only the reviewed notice.</summary>
    public Task CloseNoticeAsync(string pairId, Notice expected, CancellationToken ct) => Locked(async () =>
    {
        await notices.CloseNoticeAsync(Connection, pairId, expected, ct);
        TouchNotice(pairId, expected);
    }, ct);
    /// <summary>Read the journal boundary.</summary>
    public Task<JournalPosition?> PositionAsync(string volume, CancellationToken ct) => Locked(() => pairs.PositionAsync(Connection, volume, ct), ct);
    /// <summary>Save a settled volume boundary.</summary>
    public Task SavePositionAsync(string volume, JournalPosition position, CancellationToken ct) => Locked(() => pairs.SavePositionAsync(Connection, volume, position, ct), ct);
    /// <summary>Read the ordinary change cursor.</summary>
    public Task<string?> CursorAsync(string pairId, CancellationToken ct) => Locked(() => pairs.CursorAsync(Connection, pairId, ct), ct);
    /// <summary>Save the settled ordinary change cursor.</summary>
    public Task SaveCursorAsync(string pairId, string cursor, CancellationToken ct) => Locked(() => pairs.SaveCursorAsync(Connection, pairId, cursor, ct), ct);
    /// <summary>Advance the Repair revision with its reason.</summary>
    public Task<long> OweRepairAsync(string pairId, Side side, string reason, CancellationToken ct) => Locked(() => pairs.OweRepairAsync(Connection, pairId, side, reason, ct), ct);
    /// <summary>Read the outstanding Repair revision and reason.</summary>
    public Task<(long Revision, string? Reason)> ReadRepairAsync(string pairId, Side side, CancellationToken ct) => Locked(() => pairs.ReadRepairAsync(Connection, pairId, side, ct), ct);
    /// <summary>Only the completion ending the entire two-sided Repair can publish its required summary and first_repair_done. Match both captured revisions and clear the observed reasons in the same transaction; a stale completion publishes nothing.</summary>
    public Task<bool> FinishRepairAsync(string pairId, (long Local,long Dropbox) revisions, string completedRepairJson, CancellationToken ct) => Locked(async () =>
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        var matched=await pairs.FinishRepairAsync(tx, pairId, revisions, completedRepairJson, ct);
        if(matched) tx.Commit();
        return matched;
    }, ct);
    /// <summary>Retire only the matching mapped scope at the owner’s Dropbox revision. No first-run bit or full summary is published.</summary>
    public Task<bool> FinishRepairAsync(string pairId, Scope scope, long revision, CancellationToken ct) => Locked(async () =>
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        var matched=await pairs.FinishRepairAsync(tx, pairId, scope, revision, ct);
        if(matched) tx.Commit();
        return matched;
    }, ct);
    /// <summary>Persist the accepted job.</summary>
    public Task SaveJobAsync(AcceptedJob job, CancellationToken ct) => Locked(() => pairs.SaveJobAsync(Connection, job, ct), ct);
    /// <summary>Read unsettled jobs.</summary>
    public IAsyncEnumerable<AcceptedJob> JobsAsync(string pairId, CancellationToken ct) => Stream(db => pairs.JobsAsync(db, pairId, ct), false, ct);
    /// <summary>Retire a settled job.</summary>
    public Task SettleJobAsync(AcceptedJob job, CancellationToken ct) => Locked(() => pairs.SettleJobAsync(Connection, job, ct), ct);
    /// <summary>Persist the selected victim and stage proof.</summary>
    public Task ArmAsync(string pairId, RemovalSelection victim, StageProof? stage, CancellationToken ct) => Locked(() => pairs.ArmAsync(Connection, pairId, victim, stage, ct), ct);
    /// <summary>Read the armed victim and stage proof.</summary>
    public Task<(RemovalSelection Victim, StageProof? Stage)?> ArmedAsync(string pairId, CancellationToken ct) => Locked(() => pairs.ArmedAsync(Connection, pairId, ct), ct);
    /// <summary>Clear only the matching victim and proof.</summary>
    public Task DisarmAsync(string pairId, RemovalSelection expected, StageProof? expectedStage, CancellationToken ct) => Locked(() => pairs.DisarmAsync(Connection, pairId, expected, expectedStage, ct), ct);
    /// <summary>Read the terminal history cursor only after the Dropbox acquisition completes successfully.</summary>
    public Task AddScanAsync(string pairId, Guid scanId, Side side, IAsyncEnumerable<Listed> rows, Func<string>? terminalHistoryCursor, CancellationToken ct) => IngestAsync(pairId, scanId, side, rows, terminalHistoryCursor, ct);
    /// <summary>Stream comparison rows without holding the writer.</summary>
    public IAsyncEnumerable<(Row Row, IReadOnlyList<(Notice Notice, NoticeItem? Member, bool Covered, bool Touched)> Notices)> CompareAsync(string pairId, Guid scanId, Scope? scope, Func<Side,string,(bool Covered,bool Touched)> evidence, CancellationToken ct) => Stream(db => scan.CompareAsync(db, pairId, scanId, scope, evidence, settings.StoragePageSize, ct), true, ct);
    /// <summary>Discard the scan and its unpublished cursor.</summary>
    public Task ClearScanAsync(string pairId, Guid scanId, CancellationToken ct) => Locked(async () =>
    {
        EnsureScratch();
        await scan.ClearScanAsync(Connection, pairId, scanId, ct);
    }, ct);
    /// <summary>Close the store connection.</summary>
    public void Dispose() => Connection.Dispose();

    /// <summary>Opens the selected store image; read-only access never creates one.</summary>
    public Store(string path, bool readOnly, EngineSettings settings)
    {
        this.settings=settings;
        scratch = Path.GetFullPath(path) + ".scan";
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(path), Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite }.ToString();
        Connection = OpenConnection();
        try
        {
            using var version = StorageCommand.Create(Connection, null, "PRAGMA user_version");
            var actual = Convert.ToInt64(version.ExecuteScalar());
            if (actual != Schema.Version) throw new InvalidDataException(Text.Get(
                actual > Schema.Version ? TextKey.StateSchemaTooNew : TextKey.StateSchemaTooOld, (TextArg.Path,path)));
            if (!readOnly)
            {
                using var pragmas = StorageCommand.Create(Connection, null, Schema.Pragmas);
                pragmas.ExecuteNonQuery();
            }
        }
        catch { Connection.Dispose(); throw; }
    }


    /// <summary>Read the persisted full-first-Repair bit and last summary without inferring them from record count.</summary>
    public Task<(bool FirstRepairDone, string? LastRepairJson)> RepairStateAsync(string pairId, CancellationToken ct) => Locked(() => pairs.RepairStateAsync(Connection, pairId, ct), ct);

    /// <summary>Read deleted-name publication state independently of the change-feed cursor.</summary>
    public Task<(string? Cursor, bool Owed)> DeletedHistoryAsync(string pairId, CancellationToken ct) => Locked(() => scan.History.ReadStateAsync(Connection, pairId, ct), ct);

    /// <summary>Fold only this completed history acquisition and publish its own terminal cursor with it. An ordinary or incomplete scan cannot publish history.</summary>
    public Task PublishDeletedHistoryAsync(string pairId, Guid scanId, CancellationToken ct) => Locked(async () =>
    {
        EnsureScratch();
        await scan.History.PublishAsync(Connection, pairId, scanId, ct);
    }, ct);

    /// <summary>Read the unspent name from the existing delta, scratch and block homes.</summary>
    public Task<bool> DeletedNameAsync(string pairId, Guid? scanId, string key, CancellationToken ct) => Locked(() =>
    {
        if (scanId.HasValue) EnsureScratch();
        return scan.History.ContainsAsync(Connection, pairId, scanId, key, ct);
    }, ct);

    /// <summary>Read historical deletion evidence for the candidate kind and this acquisition only.</summary>
    public Task<bool> DeletedNameAsync(string pairId, Guid? scanId, string key, bool isFolder, CancellationToken ct) => Locked(() =>
    {
        if (scanId.HasValue) EnsureScratch();
        return scan.History.ContainsAsync(Connection, pairId, scanId, key, isFolder, ct);
    }, ct);

    /// <summary>Publish the single surviving link and its notice in one transaction.</summary>
    public Task CommitKeepBothAsync(string pairId, SyncedEntry? before, SyncedEntry first, Notice notice, CancellationToken ct) =>
        KeepBothAsync(pairId, before, first, null, notice, ct);

    /// <summary>Project both physical endpoints of a settled folder move set-wise in one transaction.</summary>
    public Task ProjectSubtreeAsync(string pairId, Scope from, Scope to, CancellationToken ct) => Locked(async () =>
    {
        await committer.ProjectSubtreeAsync(Connection, pairId, from, to, ct);
        foreach (var scope in new[] { from, to })
        {
            if (from.LocalPath!=to.LocalPath) scan.Touch(pairId, Side.Local, scope.LocalPath, true);
            if (from.DropboxPath!=to.DropboxPath) scan.Touch(pairId, Side.Dropbox, scope.DropboxPath, true);
        }
    }, ct);

    /// <summary>Indexed exact-path notice lookup; no whole-pair notice scan on the item path.</summary>
    public IAsyncEnumerable<Notice> NoticesAtAsync(string pairId, string pathKey, CancellationToken ct) => Stream(db => notices.NoticesAtAsync(db, pairId, pathKey, ct), false, ct);
    /// <summary>On lost continuity, publish the volume cursor and one Local Repair debt per durable binding selected under the same immediate transaction, independent of the reader snapshot.</summary>
    public Task ResetJournalAsync(string volume, JournalPosition fresh, IReadOnlyList<string> affectedPairs, string reason, CancellationToken ct) => Locked(async () =>
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        await pairs.ResetJournalAsync(tx, volume, fresh, [], reason, ct);
        tx.Commit();
    }, ct);
    /// <summary>On Dropbox reset only, install its fresh cursor and increment that pair’s Dropbox Repair revision/reason in one transaction.</summary>
    public Task ResetCursorAsync(string pairId, string fresh, string reason, CancellationToken ct) => Locked(async () =>
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        await pairs.ResetCursorAsync(tx, pairId, fresh, reason, ct);
        tx.Commit();
    }, ct);
    /// <summary>Stream the existing scan’s move candidates before ordinary differences, ordered by shallowest candidate of the binding then by binding so all its observed sides are adjacent.</summary>
    public IAsyncEnumerable<RenameCandidate> RenamesAsync(string pairId, Guid scanId, Scope? scope, CancellationToken ct) => Stream(db => scan.RenamesAsync(db, pairId, scanId, scope, settings.StoragePageSize, ct), true, ct);
    /// <summary>Replace the old link with both new links and their notice in one transaction.</summary>
    public Task CommitKeepBothAsync(string pairId, SyncedEntry? before, SyncedEntry first, SyncedEntry second, Notice notice, CancellationToken ct) => KeepBothAsync(pairId, before, first, second, notice, ct);
    /// <summary>In one transaction match the reviewed drive notice and publish the chosen pair; only FirstSync resets bindings and first Repair.</summary>
    public Task<bool> ResolveDriveAsync(PairConfig pair, Notice reviewed, ResolutionChoice choice, CancellationToken ct) => Locked(() => pairs.ResolveDriveAsync(Connection, pair, reviewed, choice, ct), ct);
    /// <summary>Stream notices whose members overlap the mapped footprint on their own side, including covering ancestors.</summary>
    public IAsyncEnumerable<Notice> NoticesUnderAsync(string pairId, Scope footprint, CancellationToken ct) => Stream(db => notices.NoticesUnderAsync(db, pairId, footprint, settings.StoragePageSize, ct), false, ct);
    /// <summary>Add or retire only this observed link member; close the pair-wide link notice only when no members remain.</summary>
    public Task UpdateLinkMemberAsync(string pairId, NoticeItem member, bool present, CancellationToken ct) => Locked(async () =>
    {
        await notices.UpdateLinkMemberAsync(Connection, pairId, member, present, ct);
        scan.Touch(pairId, member.Side, member.Path, false);
    }, ct);
    /// <summary>Read one indexed notice by pair and ID; the caller must still validate its reviewed token.</summary>
    public Task<Notice?> NoticeAsync(string pairId, long id, CancellationToken ct) => Locked(() => notices.NoticeAsync(Connection, pairId, id, ct), ct);
    /// <summary>Open the published image; only explicit first-add may request staged creation.</summary>
    public static async Task<Store?> OpenAsync(string productRoot, bool firstPairAdd, EngineSettings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.Combine(productRoot, ResourceNames.EngineFolder, ResourceNames.DatabaseFile);
        try { _ = File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            if (!firstPairAdd) return null;
            await new StoreMove().CreateAsync(productRoot, ct);
        }
        return new Store(path, false, settings);
    }

    /// <summary>Start tracking changes to this Repair acquisition before either listing boundary is captured.</summary>
    public void BeginScan(string pairId, Guid scanId) => scan.Begin(pairId, scanId);

    /// <summary>Report a settled change overlapping an exact path or its descendants during the acquisition.</summary>
    public bool ScanTouched(string pairId, Guid scanId, Side side, string path, bool descendants) => scan.Touched(pairId, scanId, side, path, descendants);

    private Task KeepBothAsync(string pairId, SyncedEntry? before, SyncedEntry first, SyncedEntry? second, Notice notice, CancellationToken ct) => Locked(async () =>
    {
        var prior = await PriorNoticeAsync(pairId, notice, ct);
        await committer.CommitKeepBothAsync(Connection, pairId, before, first, second, notice, ct);
        TouchChange(pairId, before, null, scan.Touch); TouchChange(pairId, null, first, scan.Touch); TouchChange(pairId, null, second, scan.Touch); TouchNotice(pairId, prior); TouchNotice(pairId, notice);
    }, ct);

    private async Task<Notice?> PriorNoticeAsync(string pairId, Notice notice, CancellationToken ct)
    {
        if (scan.HasActive(pairId))
            await foreach (var candidate in notices.NoticesAtAsync(Connection, pairId, PathNames.Key(notice.Path), ct))
                if (candidate.Kind == notice.Kind) return candidate;
        return null;
    }

    private static void TouchChange(string pairId, SyncedEntry? before, SyncedEntry? after, Action<string,Side,string,bool> touch)
    {
        bool sameFolder = before is {IsFolder:true, LocalId.Length:>0, DropboxId.Length:>0} && after is {IsFolder:true} &&
            before.LocalId==after.LocalId && before.DropboxId==after.DropboxId;
        bool localMoved=before?.LocalPath!=after?.LocalPath, remoteMoved=before?.DropboxPath!=after?.DropboxPath;
        Mark(before); Mark(after);
        void Mark(SyncedEntry? row)
        {
            if (row == null) return;
            touch(pairId, Side.Local, row.LocalPath, row.IsFolder && !(sameFolder && remoteMoved && !localMoved));
            touch(pairId, Side.Dropbox, row.DropboxPath, row.IsFolder && !(sameFolder && localMoved && !remoteMoved));
        }
    }

    private void TouchNotice(string pairId, Notice? notice)
    {
        if (notice == null) return;
        foreach (var member in notice.Subject.Items) scan.Touch(pairId, member.Side, member.Path, false);
        if (notice.Subject.Items.Count != 0) return;
        scan.Touch(pairId, Side.Local, notice.Path, false);
        scan.Touch(pairId, Side.Dropbox, notice.Path, false);
    }

    private SqliteConnection OpenConnection()
    {
        var db = new SqliteConnection(connectionString);
        try
        {
            db.Open();
            db.CreateFunction("pathkey", (string path) => PathNames.Key(path), isDeterministic: true);
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private void EnsureScratch()
    {
        if (attached) return;
        // ATTACH inherits the open-existing flags of main. Create only the disposable scan image first.
        using (var scratchDb = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = scratch, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString())) scratchDb.Open();
        AttachScratch(Connection);
        try
        {
            using var sql = StorageCommand.Create(Connection, null, "PRAGMA scan_state.journal_mode=WAL;" + Schema.CreateScan);
            sql.ExecuteNonQuery();
            attached = true;
        }
        catch
        {
            using var detach = StorageCommand.Create(Connection, null, "DETACH scan_state");
            detach.ExecuteNonQuery();
            throw;
        }
    }

    private void AttachScratch(SqliteConnection db)
    {
        using var attach = StorageCommand.Create(db, null, "ATTACH DATABASE $path AS scan_state", "$path", scratch);
        attach.ExecuteNonQuery();
    }

    private async Task IngestAsync(string pairId, Guid id, Side side, IAsyncEnumerable<Listed> rows, Func<string>? cursor, CancellationToken ct)
    {
        await Locked(() => { EnsureScratch(); return Task.CompletedTask; }, ct);
        using var db = OpenConnection();
        AttachScratch(db);
        await scan.AddScanAsync(db, pairId, id, side, rows, cursor, settings.StoreBatchRows, gate, ct);
    }

    private async Task Locked(Func<Task> run, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await run(); }
        catch (SqliteException e) when (e.SqliteErrorCode == SQLitePCL.raw.SQLITE_FULL) { throw SqliteFull.Normalize(e); }
        finally { gate.Release(); }
    }

    private async Task<T> Locked<T>(Func<Task<T>> run, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await run(); }
        catch (SqliteException e) when (e.SqliteErrorCode == SQLitePCL.raw.SQLITE_FULL) { throw SqliteFull.Normalize(e); }
        finally { gate.Release(); }
    }

    // Readers may commit while visiting a row: never lend them the writer or hold its gate across yield.
    private async IAsyncEnumerable<T> Stream<T>(Func<SqliteConnection,IAsyncEnumerable<T>> read, bool scanRows,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var db = OpenConnection();
        if (scanRows) AttachScratch(db);
        await foreach (var row in read(db).WithCancellation(ct)) yield return row;
    }
}
