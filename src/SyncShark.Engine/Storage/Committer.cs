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
        await ApplyAsync(tx, pairId, before, after, publication, spentDeletedKey, spentDeletedAt, ct);
        tx.Commit();
    }

    private readonly Queue<(Func<SqliteTransaction,Task> Apply, Action Published, TaskCompletionSource Done)> pending = new();
    private bool draining;
    private SemaphoreSlim? capacity;

    internal async Task QueueAsync(SqliteConnection db, SemaphoreSlim gate, int batchSize, string pairId, SyncedEntry? before,
        SyncedEntry? after, JournalPosition? publication, string? spentDeletedKey, DateTimeOffset? spentDeletedAt, Action published, CancellationToken ct)
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
                pending.Enqueue((tx => ApplyAsync(tx,pairId,before,after,publication,spentDeletedKey,spentDeletedAt,ct),published,done));
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
                List<(Func<SqliteTransaction,Task> Apply, Action Published, TaskCompletionSource Done)> group;
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

    private static async Task FlushAsync(SqliteConnection db, List<(Func<SqliteTransaction,Task> Apply, Action Published, TaskCompletionSource Done)> group)
    {
        try
        {
            using var tx=db.BeginTransaction(deferred:false);
            try { foreach (var item in group) await item.Apply(tx); }
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
        foreach(var item in group)
        {
            try { item.Published(); item.Done.TrySetResult(); }
            catch(Exception e) { item.Done.TrySetException(e); }
        }
    }

    private static async Task ApplyAsync(SqliteTransaction tx, string pairId, SyncedEntry? before, SyncedEntry? after,
        JournalPosition? publication, string? spentDeletedKey, DateTimeOffset? spentDeletedAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (after is null) await RemoveAsync(tx, pairId, before, ct);
        else await WriteAsync(tx, pairId, before, after, publication, ct, replacing:before != null);
        if (spentDeletedKey != null)
        {
            using var spend = StorageCommand.Create(tx.Connection!, tx, Sql.Get(SqlKey.CommitterApply1), ("$p",pairId), ("$key",spentDeletedKey), ("$at",spentDeletedAt?.UtcTicks));
            await spend.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Replace the old binding, insert both surviving links, and publish their notice together.</summary>
    public async Task<(bool Applied,(long Id,string Path,NoticeKind Kind,TextKey Cause,string SubjectJson)? Prior)> CommitKeepBothAsync(SqliteConnection db, string pairId, SyncedEntry? before, SyncedEntry first,
        SyncedEntry? second, Notice notice, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var tx = db.BeginTransaction(deferred: false);
        var result=await ReplaceAsync(tx,pairId,before,first,second,notice,ct); tx.Commit(); return result;
    }

    internal static async Task<(bool Applied,(long Id,string Path,NoticeKind Kind,TextKey Cause,string SubjectJson)? Prior)> ReplaceAsync(SqliteTransaction tx,string pairId,SyncedEntry? before,SyncedEntry first,SyncedEntry? second,Notice notice,CancellationToken ct)
    {
        var db=tx.Connection!;
        var keep = before == null ? null : await KeepOf(db, tx, pairId, before, ct);
        await RemoveAsync(tx, pairId, before, ct);
        var detached=new List<Exclusion>();
        await Insert(first);
        if(second!=null) await Insert(second);
        if(detached.Count!=0)
        {
            using var preserve=StorageCommand.Create(db,tx,Sql.Get(SqlKey.CommitterReplace1),("$p",pairId),("$rule",null));
            foreach(var rule in detached) { preserve.Parameters["$rule"].Value=System.Text.Json.JsonSerializer.Serialize(rule);await preserve.ExecuteNonQueryAsync(ct); }
        }
        async Task Insert(SyncedEntry entry)
        {
            var legs=keep==null ? null : KeepRows.Carry(keep,before!,first,second,entry).Where(f=>
            {
                if(f.FixedPath==null) return true;
                detached.Add(new(f.Endpoint!.Value,f.FixedPath,true){RuleId=f.Id,Origin=f.Origin}); return false;
            }).ToArray();
            await WriteAsync(tx,pairId,before,entry,entry.LocalPublished,ct,false,legs is {Length:>0} ? System.Text.Json.JsonSerializer.Serialize(legs) : null);
        }
        var result = await new NoticeTable().SaveNoticeAsync(tx, pairId, notice, ct);
        return result;
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

    internal static async Task WriteAsync(SqliteTransaction tx, string pairId, SyncedEntry? before, SyncedEntry entry,
        JournalPosition? publication, CancellationToken ct, bool replacing,string? keep=null)
    {
        if (PathNames.Key(entry.LocalPath) != entry.LocalKey) throw new InvalidDataException(Text.Get(TextKey.StateLocalKeyInvalid, (TextArg.Path,entry.LocalPath)));
        var boundary = entry.IsFolder ? null : publication ?? (before?.LocalId == entry.LocalId ? entry.LocalPublished : null);
        // An update is one row write even for a move; an explicit key and an inherited key share a logical namespace.
        var sql = replacing ? Sql.Get(SqlKey.CommitterWrite1) : Sql.Get(SqlKey.CommitterWrite2);
        using var insert = StorageCommand.Create(tx.Connection!, tx, sql + Sql.Get(SqlKey.CommitterWrite3), ("$old",replacing ? before!.LocalKey : null), ("$p",pairId), ("$path",entry.LocalPath), ("$dk",entry.DropboxKey), ("$lk",entry.LocalKey), ("$dp",entry.DropboxPath),
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
        if (!replacing && entry.IsFolder)
        {   // a confirmed fresh folder link proves the name lives on Dropbox again; an older timeless spend is obsolete. A dated file deletion keeps its server time.
            using var expire = StorageCommand.Create(tx.Connection!, tx, Sql.Get(SqlKey.CommitterWrite4), ("$p",pairId), ("$k",entry.DropboxKey));
            await expire.ExecuteNonQueryAsync(ct);
        }
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
