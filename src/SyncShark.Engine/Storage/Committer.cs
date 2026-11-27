using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Owns atomic updates to synced bindings and their publication evidence.</summary>
public sealed class Committer
{

    /// <summary>Settle a binding and the exact spent historical name in one transaction.</summary>
    public async Task CommitAsync(SqliteConnection db, string pairId, SyncedEntry? before, SyncedEntry? after,
        JournalPosition? publication, string? spentDeletedKey, CancellationToken ct, DateTimeOffset? spentDeletedAt = null)
    {
        ct.ThrowIfCancellationRequested();
        using var tx = db.BeginTransaction(deferred: false);
        await ApplyAsync(tx, pairId, before, after, publication, spentDeletedKey, spentDeletedAt, null, ct);
        tx.Commit();
    }

    private readonly Queue<(Func<SqliteTransaction,Task<bool>> Apply, Action<bool> Published, TaskCompletionSource Done)> pending = new();
    private bool draining;
    private SemaphoreSlim? capacity;

    internal async Task QueueAsync(SqliteConnection db, SemaphoreSlim gate, int batchSize, string pairId, SyncedEntry? before,
        SyncedEntry? after, JournalPosition? publication, string? spentDeletedKey, DateTimeOffset? spentDeletedAt, (IReadOnlyList<Notice> Tokens,Func<bool> Free)? closing, Action<bool> published, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        SemaphoreSlim slots;
        lock (pending) slots = capacity ??= new SemaphoreSlim(batchSize,batchSize);
        await slots.WaitAsync(ct);
        try
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool start;
            lock (pending)
            {
                pending.Enqueue((tx => ApplyAsync(tx,pairId,before,after,publication,spentDeletedKey,spentDeletedAt,closing,ct),published,done));
                start = !draining;
                draining = true;
            }
            if (start) _ = Task.Run(() => DrainAsync(db, gate));
            await done.Task;
        }
        finally { slots.Release(); }
    }

    private async Task DrainAsync(SqliteConnection db, SemaphoreSlim gate)
    {
        while (true)
        {
            await gate.WaitAsync();
            try
            {
                List<(Func<SqliteTransaction,Task<bool>> Apply, Action<bool> Published, TaskCompletionSource Done)> group;
                lock (pending)
                {
                    if (pending.Count == 0) { draining=false; return; }
                    group = [..pending];
                    pending.Clear();
                }
                await FlushAsync(db, group);
            }
            finally { gate.Release(); }
        }
    }

    private static async Task FlushAsync(SqliteConnection db, List<(Func<SqliteTransaction,Task<bool>> Apply, Action<bool> Published, TaskCompletionSource Done)> group)
    {
        var owed = new bool[group.Count]; // this attempt's bits: a rolled-back group's are dropped, each independent retry has its own
        try
        {
            using var tx=db.BeginTransaction(deferred:false);
            try { for (var i = 0; i < group.Count; i++) owed[i] = await group[i].Apply(tx); }
            catch (Exception e) when (group.Count>1 && e is OperationCanceledException or ChangedUnderneathException or InvalidDataException or SqliteException { SqliteErrorCode: SQLitePCL.raw.SQLITE_CONSTRAINT })
            {
                // Retry independent items only after a confirmed rollback, never after COMMIT or callback failure.
                tx.Rollback();
                foreach(var item in group) await FlushAsync(db, [item]);
                return;
            }
            tx.Commit();
        }
        catch (Exception e)
        {
            foreach(var item in group) item.Done.TrySetException(SqliteFull.Normalize(e));
            return;
        }
        for (var i = 0; i < group.Count; i++)
        {
            try { group[i].Published(owed[i]); group[i].Done.TrySetResult(); }
            catch(Exception e) { group[i].Done.TrySetException(e); }
        }
    }

    private static async Task<bool> ApplyAsync(SqliteTransaction tx, string pairId, SyncedEntry? before, SyncedEntry? after,
        JournalPosition? publication, string? spentDeletedKey, DateTimeOffset? spentDeletedAt, (IReadOnlyList<Notice> Tokens,Func<bool> Free)? closing, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var owed = false;
        if (after is null) await RemoveAsync(tx, pairId, before, ct);
        else owed = await WriteAsync(tx, pairId, before, after, publication, ct, replacing:before != null);
        if (spentDeletedKey != null)
        {
            using var spend = StorageCommand.Create(tx.Connection!, tx, Sql.Get(SqlKey.CommitterApply1), ("$p",pairId), ("$key",spentDeletedKey), ("$at",spentDeletedAt?.UtcTicks));
            await spend.ExecuteNonQueryAsync(ct);
        }
        if (closing is {} c) foreach (var n in c.Tokens) await NoticeTable.Close(tx, pairId, n, ct, c.Free); // the item's own earlier failures close in its commit, each re-checked against live owners here
        return owed;
    }

    /// <summary>Replace the old binding, insert both surviving links, and publish their notice together.</summary>
    public async Task<(Notice? Saved,(long Id,string Path,NoticeKind Kind,TextKey Cause,string SubjectJson)? Prior)> CommitKeepBothAsync(SqliteConnection db, string pairId, SyncedEntry? before, SyncedEntry first,
        SyncedEntry? second, Notice notice, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var tx = db.BeginTransaction(deferred: false);
        var result=(await ReplaceAsync(tx,pairId,before,first,second,notice,ct)).Notice; tx.Commit(); return result;
    }

    internal static async Task<((Notice? Saved,(long Id,string Path,NoticeKind Kind,TextKey Cause,string SubjectJson)? Prior) Notice,bool Owed)> ReplaceAsync(SqliteTransaction tx,string pairId,SyncedEntry? before,SyncedEntry first,SyncedEntry? second,Notice notice,CancellationToken ct)
    {
        var db=tx.Connection!;
        var keep = before == null ? null : await KeepOf(db, tx, pairId, before, ct);
        await RemoveAsync(tx, pairId, before, ct);
        var detached=new List<Exclusion>();
        var owed=await Insert(first);
        if(second!=null) owed|=await Insert(second);
        if(detached.Count!=0)
        {
            using var preserve=StorageCommand.Create(db,tx,Sql.Get(SqlKey.CommitterReplace1),("$p",pairId),("$rule",null));
            foreach(var rule in detached) { preserve.Parameters["$rule"].Value=System.Text.Json.JsonSerializer.Serialize(rule);await preserve.ExecuteNonQueryAsync(ct); }
        }
        async Task<bool> Insert(SyncedEntry entry)
        {
            var legs=keep==null ? null : KeepRows.Carry(keep,before!,first,second,entry).Where(f=>
            {
                if(f.FixedPath==null) return true;
                detached.Add(new(f.Endpoint!.Value,f.FixedPath,true){RuleId=f.Id,Origin=f.Origin}); return false;
            }).ToArray();
            return await WriteAsync(tx,pairId,before,entry,entry.LocalPublished,ct,false,legs is {Length:>0} ? System.Text.Json.JsonSerializer.Serialize(legs) : null);
        }
        var result = await new NoticeTable().SaveNoticeAsync(tx, pairId, notice, ct);
        return (result,owed);
    }

    private static async Task<string?> KeepOf(SqliteConnection db, SqliteTransaction tx, string pairId, SyncedEntry before, CancellationToken ct)
    {
        using var cmd = StorageCommand.Create(db, tx,
            Sql.Get(SqlKey.CommitterKeepOf1),
            ("$p", pairId), ("$key", before.LocalKey), ("$id", Encoding.LocalId(before.LocalId)), ("$remote", before.DropboxId));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task RemoveAsync(SqliteTransaction tx, string pairId, SyncedEntry? before, CancellationToken ct)
    {
        if (before == null) return;
        using var cmd = StorageCommand.Create(tx.Connection!, tx,
            Sql.Get(SqlKey.CommitterRemove1),
            ("$p",pairId), ("$key",before.LocalKey), ("$id",Encoding.LocalId(before.LocalId)), ("$remote",before.DropboxId));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new ChangedUnderneathException(before.LocalPath);
    }

    internal static async Task<bool> WriteAsync(SqliteTransaction tx, string pairId, SyncedEntry? before, SyncedEntry entry,
        JournalPosition? publication, CancellationToken ct, bool replacing,string? keep=null)
    {
        if (PathNames.Key(entry.LocalPath) != entry.LocalKey) throw new InvalidDataException(Text.Get(TextKey.StateLocalKeyInvalid, (TextArg.Path,entry.LocalPath)));
        var boundary = entry.IsFolder ? null : publication ?? (before?.LocalId == entry.LocalId ? entry.LocalPublished : null);
        // The local path is stored as its folder row plus a name; a content-only update binds neither.
        string? parent = null, name = null;
        if (!replacing || before!.LocalPath != entry.LocalPath)
        {
            name = PathNames.Name(entry.LocalPath);
            if (!replacing || !before!.LocalPath.AsSpan(0, PathNames.LastSeparator(before.LocalPath) + 1).SequenceEqual(entry.LocalPath.AsSpan(0, PathNames.LastSeparator(entry.LocalPath) + 1)))
            {
                parent = PathNames.Folder(entry.LocalPath);
                using var intern = StorageCommand.Create(tx.Connection!, tx, Sql.Get(SqlKey.CommitterInternFolder), "$parent", parent);
                await intern.ExecuteNonQueryAsync(ct);
            }
        }
        // An update is one row write even for a move; an explicit key and an inherited key share a logical namespace.
        var sql = replacing ? Sql.Get(SqlKey.CommitterWrite1) : Sql.Get(SqlKey.CommitterWrite2);
        using var insert = StorageCommand.Create(tx.Connection!, tx, sql + Sql.Get(SqlKey.CommitterWrite3), ("$old",replacing ? before!.LocalKey : null), ("$p",pairId), ("$path",entry.LocalPath), ("$dk",entry.DropboxKey), ("$lk",entry.LocalKey), ("$dp",entry.DropboxPath),
            ("$parent",parent), ("$name",name),
            ("$folder",entry.IsFolder), ("$lid",Encoding.LocalId(entry.LocalId)), ("$size",entry.LocalSize), ("$mtime",entry.LocalModifiedTicks),
            ("$did",entry.DropboxId), ("$hash",Encoding.Hash(entry.DropboxContentHash)), ("$journal",unchecked((long?)boundary?.JournalId)),
            ("$usn",unchecked((long?)boundary?.NextUsn)));
        if(!replacing) insert.Parameters.AddWithValue("$keep",(object?)keep??DBNull.Value);
        if (replacing)
        {
            insert.Parameters.AddWithValue("$oldId", Encoding.LocalId(before!.LocalId)!);
            insert.Parameters.AddWithValue("$oldRemote",before.DropboxId);
        }
        if (await insert.ExecuteNonQueryAsync(ct) != 1) throw new ChangedUnderneathException(entry.DropboxPath);
        // One Windows file bound at a second name (renamed during its own first upload, an atomic save's temp, or a hard link)
        // owes a file scope per name in this transaction; the existing scoped Repair removes whichever name Windows no longer has.
        if (!entry.IsFolder && LocalIdentity.IsKnown(entry.LocalId) && before?.LocalId != entry.LocalId)
        {
            var names = new List<Scope>();
            await foreach (var bound in new RecordTable().ByIdAsync(tx.Connection!, pairId, Side.Local, entry.LocalId, int.MaxValue, ct, tx))
                names.Add(new(bound.LocalPath, bound.DropboxPath));
            if (names.Count > 1) await new PairTable().OweScopesAsync(tx, pairId, names, ct);
            return names.Count > 1;
        }
        if (!replacing && entry.IsFolder)
        {   // a confirmed fresh folder link proves the name lives on Dropbox again; an older timeless spend is obsolete. A dated file deletion keeps its server time.
            using var expire = StorageCommand.Create(tx.Connection!, tx, Sql.Get(SqlKey.CommitterWrite4), ("$p",pairId), ("$k",entry.DropboxKey));
            await expire.ExecuteNonQueryAsync(ct);
        }
        return false;
    }

    /// <summary>Rebase both mapped descendant ranges from the same frozen old rows.</summary>
    public async Task ProjectSubtreeAsync(SqliteConnection db, string pairId, Scope from, Scope to, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(from==to) return;
        using var tx = db.BeginTransaction(deferred: false);
        await ProjectAsync(tx,pairId,from,to,ct); tx.Commit();
    }

    internal static async Task ProjectAsync(SqliteTransaction tx,string pairId,Scope from,Scope to,CancellationToken ct)
    {
        var db=tx.Connection!;
        bool localMoved=from.LocalPath!=to.LocalPath, remoteMoved=from.DropboxPath!=to.DropboxPath;
        if(!localMoved && !remoteMoved) return;
        var localKey=localMoved ? PathNames.Key(from.LocalPath) : null;
        var remoteKey=remoteMoved ? PathNames.DropboxKey(from.DropboxPath) : null;
        var local=localKey == null ? default : PathNames.DescendantBounds(localKey);
        var remote=remoteKey == null ? default : PathNames.DescendantBounds(remoteKey);
        var destKey=remoteMoved ? PathNames.DropboxKey(to.DropboxPath) : null;
        // A NULL lower bound selects no rows and disables that endpoint's projection arms.
        using var cmd = StorageCommand.Create(db, tx, Sql.Get(SqlKey.CommitterRebaseDescendantsBoth), ("$localLow",local.Low),("$localHigh",local.High),
            ("$remoteLow",remote.Low),("$remoteHigh",remote.High), ("$p",pairId),
            ("$oldLocalPath",from.LocalPath), ("$oldLocalKey",localKey),
            ("$newLocalPath",to.LocalPath), ("$newLocalKey",localMoved ? PathNames.Key(to.LocalPath) : null),
            ("$oldDropboxPath",from.DropboxPath), ("$oldDropboxKey",remoteKey),
            ("$newDropboxPath",to.DropboxPath), ("$newDropboxKey",destKey));
        await cmd.ExecuteNonQueryAsync(ct);
        if (destKey != null)
        {
            var destination=PathNames.DescendantBounds(destKey);
            using var collision = StorageCommand.Create(db, tx, Sql.Get(SqlKey.CommitterClashInDropboxSubtree), ("$p",pairId), ("$low",destination.Low), ("$high",destination.High));
            if (Convert.ToInt64(await collision.ExecuteScalarAsync(ct)) != 0) throw new ChangedUnderneathException(to.DropboxPath);
        }
    }
}
