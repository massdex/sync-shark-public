using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Opens existing store images and coordinates transactional and streaming operations.</summary>
public sealed class Store : IStore
{
    private SqliteConnection Connection { get; }
    private readonly SemaphoreSlim gate = new(1);
    private readonly ScanScratch scratch;
    private readonly Committer committer=new();
    private readonly PairTable pairs = new();
    private readonly RecordTable records = new();
    private readonly NoticeTable notices = new();
    private readonly ScanTable scan = new();
    private readonly EngineSettings settings;
    private Action<PairConfig>? pairChanged;
    Action<PairConfig>? IStore.PairChanged { get => pairChanged; set => pairChanged = value; }
    /// <summary>Relink a proved kept move, including inherited departures and folder descendants, as one publication.</summary>
    public Task FollowKeepAsync(string pairId,SyncedEntry before,SyncedEntry after,Side? departure,CancellationToken ct) => Locked(async ()=>
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        if(departure is {} side)
        {
            var live=await pairs.ReadAsync(Connection,pairId,ct,tx) ?? throw new InvalidDataException();
            if(!live.Exclusions.Any(e=>e.Keep && (e.Token==null || e.Side==side) && PathNames.IsSelfOrUnder(before.PathOn(side),e.Path))) throw new ChangedUnderneathException(before.PathOn(side));
            if(!live.Exclusions.Any(e=>e.KeptBy is {Tail:""} && e.Side==side && PathNames.Same(e.Path,before.PathOn(side))))
                await KeepRows.Publish(Connection,tx,pairId,[new(side,before.PathOn(side),true)],ct);
        }
        await Committer.WriteAsync(tx,pairId,before,after,null,ct,true);
        if(before.IsFolder) await Committer.ProjectAsync(tx,pairId,new(before.LocalPath,before.DropboxPath),new(after.LocalPath,after.DropboxPath),ct);
        await PublishAsync(tx,pairId,ct); TouchChange(pairId,before,after);
    },ct);
    /// <summary>Enroll the currently stored raw rules, not the admission snapshot.</summary>
    public Task<PairConfig> EnrollKeepsAsync(PairConfig pair,CancellationToken ct) => Locked(async ()=>
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        await pairs.EnrollAsync(tx,pair,ct);
        var view=await pairs.ReadAsync(Connection,pair.PairId,ct,tx) ?? throw new InvalidDataException();
        tx.Commit(); return view;
    },ct);

    private async Task PublishAsync(SqliteTransaction tx,string id,CancellationToken ct)
    {
        var update=pairChanged;
        var view=update==null ? null : await pairs.ReadAsync(Connection,id,ct,tx) ?? throw new InvalidDataException();
        tx.Commit();
        if(view!=null) update!(view);
    }
    /// <summary>Stream configured pairs.</summary>
    public async IAsyncEnumerable<PairConfig> PairsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var malformed = new List<string>();
        await foreach (var pair in scratch.Stream(db => pairs.PairsAsync(db, settings.StoragePageSize, malformed, ct), false, ct)) yield return pair;
        foreach (var id in malformed)
            await SaveNoticeAsync(id, new(0, NoticeKind.Failure, TextKey.StateRootBindingInvalid, string.Empty, new([], null), DateTimeOffset.UtcNow, false), ct);
    }
    /// <summary>Read one pair by identifier.</summary>
    public Task<PairConfig?> PairAsync(string pairId, CancellationToken ct) => Locked(() => pairs.ReadAsync(Connection, pairId, ct), ct);
    /// <summary>Publishes configuration and each distinct returned footprint at its owner in one transaction, advancing the existing Repair revision even if the same scope was already owed.</summary>
    public Task SavePairAsync(PairConfig pair, CancellationToken ct, IReadOnlyList<(string Owner, Scope Scope)>? returnedScopes = null, Exclusion? selected = null) => Locked(async () =>
    {
        using (var tx = Connection.BeginTransaction(deferred: false))
        {
            if (await pairs.PublishAsync(tx, pair.PairId, pair, returnedScopes ?? [], ct, selected) is {} cause)
                throw new EngineRequestException(cause);
            await PublishAsync(tx,pair.PairId,ct);
        }
    }, ct);
    /// <summary>Return the exact refusal cause from the publication transaction (accepted job, armed PC note, or PutBack notice, including ignored). Null means the pair was removed and its scopes returned atomically; refusal changes neither. An unknown pair returns PairUnknown.</summary>
    public Task<TextKey?> RemovePairAsync(string pairId, CancellationToken ct, IReadOnlyList<(string Owner, Scope Scope)>? returnedScopes = null) => Locked(async () =>
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        var cause=await pairs.PublishAsync(tx,pairId,null,returnedScopes??[],ct);
        if(cause is null) tx.Commit();
        return cause;
    }, ct);
    /// <summary>Binding projections include the saved PC publication journal and boundary; only the dirty owner compares them.</summary>
    public Task<SyncedEntry?> FindAsync(string pairId, Side side, string key, CancellationToken ct) => Locked(() => records.FindAsync(Connection, pairId, side, key, ct), ct);
    /// <summary>Stream bindings sharing an endpoint identity.</summary>
    public IAsyncEnumerable<SyncedEntry> ByIdAsync(string pairId, Side side, string id, CancellationToken ct) => scratch.Stream(db => records.ByIdAsync(db, pairId, side, id, settings.StoragePageSize, ct), false, ct);
    /// <summary>Stream recorded descendants.</summary>
    public IAsyncEnumerable<SyncedEntry> DescendantsAsync(string pairId, Side side, string key, CancellationToken ct) => scratch.Stream(db => records.DescendantsAsync(db, pairId, side, key, settings.StoragePageSize, ct), false, ct);
    /// <summary>Commit the record change and spend the exact acknowledged historical deletion key atomically, even when neither side has a previous binding. Without a new publication keep the carried boundary only on a same-PC-identity relink.</summary>
    /// <remarks>Producers await admission and durable settlement before submitting more work.</remarks>
    public Task CommitAsync(string pairId, SyncedEntry? before, SyncedEntry? after, JournalPosition? publication, string? spentDeletedKey, CancellationToken ct, DateTimeOffset? spentDeletedAt = null) => committer.QueueAsync(Connection, gate, settings.StoreBatchRows, pairId, before, after, publication, spentDeletedKey, spentDeletedAt, () => TouchChange(pairId, before, after), ct);
    /// <summary>Stream notices for a pair.</summary>
    public IAsyncEnumerable<Notice> NoticesAsync(string pairId, CancellationToken ct) => scratch.Stream(db => notices.ListAsync(db, pairId, settings.StoragePageSize, ct), false, ct);
    /// <summary>Save by pair, path and kind with the members. An unrelated Failure cannot replace a PutBack notice. Within the same PutBack slot, the newest put-back failure at that path replaces its predecessor.</summary>
    /// <remarks>Store publishes only the committed write and its actual displaced footprint.</remarks>
    public Task SaveNoticeAsync(string pairId, Notice notice, CancellationToken ct) => Locked(async () =>
    {
        var result = await notices.SaveNoticeAsync(Connection, pairId, notice, ct);
        TouchNoticeWrite(pairId,notice,result);
    }, ct);
    /// <summary>Change visibility only if the stored notice still matches the reviewed subject; return false on a stale or closed slot.</summary>
    /// <remarks>Only a match publishes a freshness change.</remarks>
    public Task<bool> UpdateNoticeVisibilityAsync(string pairId, Notice expected, bool ignored, CancellationToken ct) => Locked(async () =>
    {
        var matched = await notices.UpdateNoticeVisibilityAsync(Connection, pairId, expected, ignored, ct);
        if (matched) TouchNotice(pairId, expected.Path, expected.Subject);
        return matched;
    }, ct);
    /// <summary>Close only the reviewed notice.</summary>
    public Task<bool> CloseNoticeAsync(string pairId, Notice expected, CancellationToken ct) => Locked(async () =>
    {
        var matched = await notices.CloseNoticeAsync(Connection, pairId, expected, ct);
        if (matched) TouchNotice(pairId, expected.Path, expected.Subject);
        return matched;
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
    /// <summary>Read the existing source revision and whole-pair reason before acquisition.</summary>
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
    public IAsyncEnumerable<AcceptedJob> JobsAsync(string pairId, CancellationToken ct) => scratch.Stream(db => pairs.JobsAsync(db, pairId, ct), false, ct);
    /// <summary>Retire a settled job.</summary>
    public Task SettleJobAsync(AcceptedJob job, CancellationToken ct) => Locked(() => pairs.SettleJobAsync(Connection, job, ct), ct);
    /// <summary>Persist the selected victim and stage proof.</summary>
    public Task<bool> ArmAsync(string pairId, RemovalSelection victim, StageProof? stage, CancellationToken ct) => Locked(() => pairs.ArmAsync(Connection, pairId, victim, stage, ct), ct);
    /// <summary>Read the armed victim and stage proof.</summary>
    public Task<(RemovalSelection Victim, StageProof? Stage)?> ArmedAsync(string pairId, CancellationToken ct) => Locked(() => pairs.ArmedAsync(Connection, pairId, ct), ct);
    /// <summary>Clear only the matching victim and proof.</summary>
    public Task DisarmAsync(string pairId, RemovalSelection expected, StageProof? expectedStage, CancellationToken ct) => Locked(() => pairs.DisarmAsync(Connection, pairId, expected, expectedStage, ct), ct);
    /// <summary>Capture names only for the Dropbox history acquisition; read its terminal cursor only after the listing finishes successfully.</summary>
    public Task AddScanAsync(string pairId, Guid scanId, Side side, IAsyncEnumerable<Listed> rows, Func<string>? terminalHistoryCursor, CancellationToken ct) => IngestAsync(pairId, scanId, side, rows, terminalHistoryCursor, ct);
    /// <summary>Project the notice and cause for each kind at the path. No compare outcome settles PutBack: only its own Retry or a later change of that same item can.</summary>
    /// <remarks>The stream does not hold the writer.</remarks>
    public IAsyncEnumerable<(Row Row, IReadOnlyList<(Notice Notice, NoticeItem? Member, bool Covered, bool Touched)> Notices)> CompareAsync(string pairId, Guid scanId, Scope? scope, Func<Side,string,(bool Covered,bool Touched)> evidence, CancellationToken ct) => scratch.Stream(db => scan.CompareAsync(db, pairId, scanId, scope, evidence, settings.StoragePageSize, ct), true, ct);
    /// <summary>Discard the scan and its unpublished cursor.</summary>
    public Task ClearScanAsync(string pairId, Guid scanId, CancellationToken ct) => Locked(() => scan.ClearScanAsync(scratch.Ensure(Connection), pairId, scanId, ct), ct);
    /// <summary>Close the store connection.</summary>
    public void Dispose() => scratch.Close(Connection);

    /// <summary>Opens the selected store image; read-only access never creates one.</summary>
    public Store(string path, bool readOnly, EngineSettings settings)
        : this(path, readOnly, settings, null) { }

    /// <summary>Opens the image with connection open/close notifications; read-only access never creates one.</summary>
    public Store(string path, bool readOnly, EngineSettings settings, Action<SqliteConnection, bool>? observe)
    {
        this.settings=settings;
        scratch = new ScanScratch(path, readOnly) { Observe = observe };
        Connection = scratch.OpenWriter();
    }

    /// <summary>Read the persisted full-first-Repair bit and last summary without inferring them from record count.</summary>
    public Task<(bool FirstRepairDone, string? LastRepairJson)> RepairStateAsync(string pairId, CancellationToken ct) => Locked(() => pairs.RepairStateAsync(Connection, pairId, ct), ct);

    /// <summary>Read deleted-name publication state independently of the change-feed cursor.</summary>
    public Task<(string? Cursor, bool Owed)> DeletedHistoryAsync(string pairId, CancellationToken ct) => Locked(() => scan.History.ReadStateAsync(Connection, pairId, ct), ct);

    /// <summary>Read history state for the active scan after verifying the saved history.</summary>
    public Task<(string? Cursor, bool Owed)> DeletedHistoryAsync(string pairId, Guid scanId, CancellationToken ct) => Locked(() => scan.History.ReadStateAsync(scratch.Ensure(Connection),pairId,scanId,ct), ct);

    /// <summary>Fold only this completed history acquisition and publish its own terminal cursor with it. An ordinary or incomplete scan cannot publish history.</summary>
    public Task PublishDeletedHistoryAsync(string pairId, Guid scanId, CancellationToken ct) => Locked(() => scan.History.PublishAsync(scratch.Ensure(Connection), pairId, scanId, ct), ct);

    /// <summary>A spent delta without a recorded deletion time masks the name in both the current acquisition and published history.</summary>
    public Task<bool> DeletedNameAsync(string pairId, Guid? scanId, string key, CancellationToken ct) => Locked(() =>
        scan.History.ContainsAsync(scanId.HasValue ? scratch.Ensure(Connection) : Connection, pairId, scanId, key, ct), ct);

    /// <summary>Read the Dropbox deletion time this exact name already consumed; null when its spend recorded none.</summary>
    public Task<DateTimeOffset?> ConsumedDeletionAsync(string pairId, string key, CancellationToken ct) => Locked(() =>
        DeletedNames.ConsumedAsync(Connection, pairId, key, ct), ct);

    /// <summary>Read unspent history for a file or folder candidate.</summary>
    public Task<bool> DeletedNameAsync(string pairId, Guid? scanId, string key, bool isFolder, CancellationToken ct) => Locked(() =>
        scan.History.ContainsAsync(scanId.HasValue ? scratch.Ensure(Connection) : Connection, pairId, scanId, key, isFolder, ct), ct);

    /// <summary>Publish the single receiving-side keep-both link and its notice atomically.</summary>
    public Task CommitKeepBothAsync(string pairId, SyncedEntry? before, SyncedEntry first, Notice notice, CancellationToken ct) =>
        KeepBothAsync(pairId, before, first, null, notice, ct);

    /// <summary>Project both physical endpoints of a settled folder move set-wise in one transaction.</summary>
    /// <remarks>Only endpoints whose spelling changed are projected.</remarks>
    public Task ProjectSubtreeAsync(string pairId, Scope from, Scope to, CancellationToken ct) => Locked(async () =>
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        await Committer.ProjectAsync(tx,pairId,from,to,ct);
        await PublishAsync(tx,pairId,ct);
        MarkMoved(Side.Local, from.LocalPath, to.LocalPath);
        MarkMoved(Side.Dropbox, from.DropboxPath, to.DropboxPath);
        void MarkMoved(Side side, string oldPath, string newPath)
        {
            if (oldPath == newPath) return;
            var oldKey = PathNames.KeyFor(side, oldPath);
            var newKey = PathNames.KeyFor(side, newPath);
            scan.Touch(pairId, side, oldKey, true);
            if (newKey != oldKey) scan.Touch(pairId, side, newKey, true);
        }
    }, ct);

    /// <summary>Indexed exact-path notice lookup; no whole-pair notice scan on the item path.</summary>
    public IAsyncEnumerable<Notice> NoticesAtAsync(string pairId, string pathKey, CancellationToken ct) => scratch.Stream(db => notices.NoticesAtAsync(db, pairId, pathKey, ct), false, ct);
    /// <summary>On lost journal continuity only, install the fresh volume boundary and increment the Local Repair revision/reason of every durably bound member of that volume in one transaction; select members at commit, not from the caller snapshot.</summary>
    public Task ResetJournalAsync(string volume, JournalPosition fresh, string reason, CancellationToken ct) => Locked(async () =>
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        await pairs.ResetJournalAsync(tx, volume, fresh, reason, ct);
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
    public IAsyncEnumerable<RenameCandidate> RenamesAsync(string pairId, Guid scanId, Scope? scope, CancellationToken ct) => scratch.Stream(db => scan.RenamesAsync(db, pairId, scanId, scope, settings.StoragePageSize, ct), true, ct);
    /// <summary>Replace the old link with both new links and their notice in one transaction.</summary>
    public Task CommitKeepBothAsync(string pairId, SyncedEntry? before, SyncedEntry first, SyncedEntry second, Notice notice, CancellationToken ct) => KeepBothAsync(pairId, before, first, second, notice, ct);
    /// <summary>In one transaction match the reviewed drive notice and publish the chosen pair; only FirstSync resets bindings and first Repair.</summary>
    public Task<bool> ResolveDriveAsync(PairConfig pair, Notice reviewed, ResolutionChoice choice, CancellationToken ct) => Locked(async () =>
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        var resolved = await pairs.ResolveDriveAsync(tx, pair, reviewed, choice, ct);
        // Both choices publish a committed pair; notify so the host installs the committed view, not its captured pre-reset snapshot (K04).
        if (resolved) await PublishAsync(tx,pair.PairId,ct);
        return resolved;
    }, ct);
    /// <summary>Stream notices whose members overlap the mapped footprint on their own side, including covering ancestors.</summary>
    public IAsyncEnumerable<Notice> NoticesUnderAsync(string pairId, Scope footprint, CancellationToken ct) => scratch.Stream(db => notices.NoticesUnderAsync(db, pairId, footprint, settings.StoragePageSize, ct), false, ct);
    /// <summary>Add or retire only this observed link member; close the pair-wide link notice only when no members remain.</summary>
    public Task UpdateLinkMemberAsync(string pairId, NoticeItem member, bool present, CancellationToken ct) => Locked(async () =>
    {
        if (await notices.UpdateLinkMemberAsync(Connection, pairId, member, present, ct))
            scan.Touch(pairId, member.Side, PathNames.KeyFor(member.Side, member.Path), false);
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

    /// <summary>Register the active acquisition before its boundaries are read.</summary>
    public void BeginScan(string pairId, Guid scanId) => scan.Begin(pairId, scanId);

    /// <summary>Check whether a concurrent write touched this footprint.</summary>
    public bool ScanTouched(string pairId, Guid scanId, Side side, string path, bool descendants) => scan.Touched(pairId, scanId, side, path, descendants);

    private Task KeepBothAsync(string pairId, SyncedEntry? before, SyncedEntry first, SyncedEntry? second, Notice notice, CancellationToken ct) => Locked(async () =>
    {
        using var tx=Connection.BeginTransaction(deferred:false);
        var result=await Committer.ReplaceAsync(tx,pairId,before,first,second,notice,ct);
        await PublishAsync(tx,pairId,ct);
        TouchChange(pairId, before, first, second);
        TouchNoticeWrite(pairId,notice,result);
    }, ct);

    private void TouchNoticeWrite(string pairId, Notice notice,
        (bool Applied,(long Id,string Path,NoticeKind Kind,TextKey Cause,string SubjectJson)? Prior) result)
    {
        if (!result.Applied || !scan.HasActive(pairId)) return;
        if (result.Prior is {} prior) TouchNotice(pairId,prior.Path,Encoding.DecodeSubject(prior.SubjectJson));
        TouchNotice(pairId,notice.Path,notice.Subject);
    }

    private void TouchChange(string pairId, SyncedEntry? before, SyncedEntry? after, SyncedEntry? second = null)
    {
        bool sameFolder = before is {IsFolder:true, LocalId.Length:>0, DropboxId.Length:>0} && after is {IsFolder:true} &&
            before.LocalId==after.LocalId && before.DropboxId==after.DropboxId;
        bool localMoved=before?.LocalPath!=after?.LocalPath, remoteMoved=before?.DropboxPath!=after?.DropboxPath;
        Mark(Side.Local, !(sameFolder && remoteMoved && !localMoved));
        Mark(Side.Dropbox, !(sameFolder && localMoved && !remoteMoved));
        void Mark(Side side, bool descendants)
        {
            var old = before?.KeyOn(side);
            var first = after?.KeyOn(side);
            var last = second?.KeyOn(side);
            if (old != null && old != first && old != last) scan.Touch(pairId, side, old, descendants && before!.IsFolder);
            if (first != null && first != last) scan.Touch(pairId, side, first, descendants && (after!.IsFolder || (first == old && before!.IsFolder)));
            if (last != null) scan.Touch(pairId, side, last, second!.IsFolder || (descendants && ((last == old && before!.IsFolder) || (last == first && after!.IsFolder))));
        }
    }

    private void TouchNotice(string pairId, string path, IssueSubject subject)
    {
        foreach (var member in subject.Items) scan.Touch(pairId, member.Side, PathNames.KeyFor(member.Side, member.Path), false);
        if (subject.Items.Count != 0) return;
        scan.Touch(pairId, Side.Local, PathNames.Key(path), false);
        scan.Touch(pairId, Side.Dropbox, PathNames.DropboxKey(path), false);
    }

    private async Task IngestAsync(string pairId, Guid id, Side side, IAsyncEnumerable<Listed> rows, Func<string>? cursor, CancellationToken ct)
    {
        await Locked(() => { scratch.Ensure(Connection); return Task.CompletedTask; }, ct);
        var db = scratch.OpenConnection();
        try
        {
            scratch.Attach(db);
            await scan.AddScanAsync(db, pairId, id, side, rows, cursor, settings.StoreBatchRows, gate, ct);
        }
        finally { scratch.Close(db); }
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
}
