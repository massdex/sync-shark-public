using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

namespace Massdex.SyncShark.Engine.Storage;
/// <summary>Acquisition-local and published Dropbox deletion history, including spent-name masks.</summary>
public sealed class DeletedNames
{
    private readonly ConcurrentDictionary<(string, Guid), (string Cursor, string Origin)> cursors = new();
    private readonly ConcurrentDictionary<(string, Guid),string> restored = new();
    internal void Completed(string pair, Guid scan, string cursor, string origin)
    {
        ArgumentException.ThrowIfNullOrEmpty(cursor);
        cursors[(pair, scan)] = (cursor, origin);
    }

    internal static (string Origin,string? Cursor,bool Owed) State(SqliteConnection db,string pair,SqliteTransaction? tx=null)
    {
        using var cmd=StorageCommand.Create(db,tx,Sql.Get(SqlKey.DeletedNamesSql),"$p",pair);using var row=cmd.ExecuteReader();
        if(!row.Read())throw new InvalidOperationException(Text.Get(TextKey.PairUnknown,(TextArg.Pair,pair)));
        int origin=row.GetOrdinal("origin"), cursor=row.GetOrdinal("cursor"), owed=row.GetOrdinal("owed");
        return(row.GetString(origin),row.IsDBNull(cursor)?null:row.GetString(cursor),row.GetBoolean(owed));
    }
    internal void Forget(string pair, Guid scan) => cursors.TryRemove((pair, scan), out _);
    internal static byte[] Token(string pair, Guid scan) => [..scan.ToByteArray(), ..System.Text.Encoding.UTF8.GetBytes(pair)];
    // The verified partition cannot alias a listing token, whose suffix is always valid UTF-8.
    internal static byte[] RestoredToken(string pair,Guid scan) => [..Token(pair,scan),0xfe];
    internal void Cleared(string pair,Guid scan){Forget(pair,scan);restored.TryRemove((pair,scan),out _);}

    // Replay the release blocks once per acquisition, one transaction per block. A partial
    // partition is never queried: the origin is admitted only after the last block verifies.
    private async Task<bool> RestoreAsync(SqliteConnection db,string pair,Guid scan,string origin,byte[] token,CancellationToken ct)
    {
        if(restored.TryGetValue((pair,scan),out var verified) && verified==origin)return true;
        restored.TryRemove((pair,scan),out _);
        string clear=Sql.Get(SqlKey.DeletedNamesClear),seekSql=Sql.Get(SqlKey.DeletedNamesRestore1),insertSql=Sql.Get(SqlKey.DeletedNamesRestore2);
        string? after=null;
        while(true)
        {
            ct.ThrowIfCancellationRequested();using var tx=db.BeginTransaction();
            if(State(db,pair,tx).Origin!=origin)return false;
            if(after==null){using var reset=StorageCommand.Create(db,tx,clear,"$s",token);reset.ExecuteNonQuery();}
            using var cmd=StorageCommand.Create(db,tx,seekSql,("$p",pair),("$after",after));
            try
            {
                using(var row=cmd.ExecuteReader())
                {
                    if(!row.Read()){row.Close();tx.Commit();restored[(pair,scan)]=origin;return true;}
                    int key=row.GetOrdinal("last_key"), count=row.GetOrdinal("name_count"), block=row.GetOrdinal("block_id");
                    var last=row.GetString(key);using var input=new SqliteBlob(db,"discovery_deleted_block","names",row.GetInt64(block),true);
                    using var put=StorageCommand.Create(db,tx,insertSql,("$s",token),("$k",""));
                    foreach(var name in DeletedNamesCodec.DecodeDeletedNames(input,row.GetInt32(count),last,after))
                    {ct.ThrowIfCancellationRequested();put.Parameters["$k"].Value=name;put.ExecuteNonQuery();}
                    after=last;
                }
            }
            catch(InvalidDataException)
            {
                await InvalidateAsync(db,tx,pair,token,ct);
                tx.Commit();return false;
            }
            tx.Commit();
        }
    }
    private static async Task InvalidateAsync(SqliteConnection db,SqliteTransaction tx,string pair,byte[]? token,CancellationToken ct)
    {
        if(token!=null){using var clear=StorageCommand.Create(db,tx,Sql.Get(SqlKey.DeletedNamesInvalidate1),"$s",token);clear.ExecuteNonQuery();}
        using var invalidate=StorageCommand.Create(db,tx,Sql.Get(SqlKey.DeletedNamesInvalidate2),"$p",pair);
        invalidate.ExecuteNonQuery();
        await new NoticeTable().SaveNoticeAsync(tx,pair,new Notice(0,NoticeKind.Info,TextKey.DeletedHistoryCorrupt,"",new([],null),DateTimeOffset.UtcNow,false),ct);
    }
    /// <summary>Read exact deletion evidence for a file, respecting its spent mask.</summary>
    public Task<bool> ContainsAsync(SqliteConnection db, string pairId, Guid? scanId, string key, CancellationToken ct) => ContainsAsync(db, pairId, scanId, key, false, ct);
    /// <summary>Check the exact file tombstone; a folder may also match an ancestor. A spent candidate is never eligible by itself; a file spend that recorded its deletion time defers to the current history.</summary>
    public async Task<bool> ContainsAsync(SqliteConnection db, string pairId, Guid? scanId, string key, bool isFolder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var (origin,cursor,owed)=State(db,pairId);
        Guid? scratchId=null;
        if(scanId is Guid acquisition && cursors.TryGetValue((pairId,acquisition),out var done))
        {
            if(done.Origin!=origin)return false;
            scratchId=acquisition;
        }
        // Publish retires the scratch completion. An ordinary or already-published scan may
        // read the current durable history, but never another acquisition's partial scratch.
        if(scratchId is null && (owed || cursor is null))return false;
        var token=scanId is Guid named ? RestoredToken(pairId,named) : null;
        bool durable=cursor!=null && !owed;
        if(durable && scanId is Guid current && !await RestoreAsync(db,pairId,current,origin,token!,ct))return false;
        using var tx=db.BeginTransaction(deferred:true);
        if(State(db,pairId,tx).Origin!=origin)return false;
        return await AnyDeletedAncestor(db, tx, pairId, scratchId, token, key, isFolder, durable, ct);
    }

    private static async Task<bool> AnyDeletedAncestor(SqliteConnection db, SqliteTransaction tx, string pairId, Guid? scratchId, byte[]? token, string key, bool isFolder, bool durable, CancellationToken ct)
    {
        string deltaSql = Sql.Get(SqlKey.DeletedNamesAnyDeletedAncestor1), scratchSql = Sql.Get(SqlKey.DeletedNamesAnyDeletedAncestor2), savedSql = Sql.Get(SqlKey.DeletedNamesAnyDeletedAncestor3);
        for (var candidate = key; candidate.Length != 0; candidate = PathNames.Parent(candidate))
        {
            // A dated file spend defers to the current history; its own deletion is refused in the planner. A file's time is never folder evidence.
            using var delta = StorageCommand.Create(db, tx, deltaSql, ("$p", pairId), ("$k", candidate), ("$folder", isFolder));
            if (await delta.ExecuteScalarAsync(ct)is long spent)
            {
                if (spent == 0)
                    return true;
                if (candidate == key)
                    return false;
            }
            else
            {
                if (scratchId is Guid id)
                {
                    using var scratch = StorageCommand.Create(db, tx, scratchSql, ("$s", Token(pairId, id)), ("$k", candidate));
                    if (await scratch.ExecuteScalarAsync(ct) != null)
                        return true;
                }

                if(durable && token!=null)
                {
                    using var saved=StorageCommand.Create(db,tx,savedSql,("$s",token),("$k",candidate));
                    if(await saved.ExecuteScalarAsync(ct)!=null)return true;
                }
                else if(durable)
                {
                    try{if(SavedBlockContains(db,tx,pairId,candidate))return true;}
                    catch(InvalidDataException){await InvalidateAsync(db,tx,pairId,null,ct);tx.Commit();return false;}
                }
            }

            if (!isFolder)
                break;
        }

        return false;
    }

    // The consumed deletion's server time, written with the spend (Committer); null for a timeless or pre-column spend.
    internal static async Task<DateTimeOffset?> ConsumedAsync(SqliteConnection db,string pairId,string key,CancellationToken ct)
    {
        using var cmd=StorageCommand.Create(db,null,Sql.Get(SqlKey.DeletedNamesConsumed1),("$p",pairId),("$k",key));
        return await cmd.ExecuteScalarAsync(ct) is long ticks ? new DateTimeOffset(ticks,TimeSpan.Zero) : null;
    }

    // Outside a scan, seek just the candidate block and validate its trailer before using a match.
    private static bool SavedBlockContains(SqliteConnection db,SqliteTransaction tx,string pair,string candidate)
    {
        using var cmd=StorageCommand.Create(db,tx,Sql.Get(SqlKey.DeletedNamesSavedBlockContains1),("$p",pair),("$k",candidate));
        using var row=cmd.ExecuteReader();if(!row.Read())return false;
        int block=row.GetOrdinal("block_id"), key=row.GetOrdinal("last_key"), count=row.GetOrdinal("name_count"), previous=row.GetOrdinal("previous_key");
        using var blob=new SqliteBlob(db,"discovery_deleted_block","names",row.GetInt64(block),true);bool found=false;
        foreach(var name in DeletedNamesCodec.DecodeDeletedNames(blob,row.GetInt32(count),row.GetString(key),row.IsDBNull(previous)?null:row.GetString(previous)))found|=name==candidate;
        return found;
    }
    /// <summary>Verify this scan's saved blocks before choosing its listing; return the post-verification deleted-history cursor and acquisition debt.</summary>
    public async Task<(string? Cursor,bool Owed)> ReadStateAsync(SqliteConnection db,string pairId,Guid scanId,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var state=State(db,pairId);
        if(state.Cursor!=null && !state.Owed && !await RestoreAsync(db,pairId,scanId,state.Origin,RestoredToken(pairId,scanId),ct))
            return await ReadStateAsync(db,pairId,ct);
        return(state.Cursor,state.Owed);
    }
    /// <summary>Read the separate deleted-history cursor and acquisition debt.</summary>
    public async Task<(string? Cursor, bool Owed)> ReadStateAsync(SqliteConnection db,string pairId,CancellationToken ct)
    {
        using var cmd = StorageCommand.Create(db, null, Sql.Get(SqlKey.DeletedNamesReadState1), "$p", pairId);
        using var row = await cmd.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct))
            throw new InvalidOperationException(Text.Get(TextKey.PairUnknown, (TextArg.Pair, pairId)));
        int cursor=row.GetOrdinal("cursor"), owed=row.GetOrdinal("owed");
        return (row.IsDBNull(cursor) ? null : row.GetString(cursor), row.GetBoolean(owed));
    }

    /// <summary>Fold only this completed history acquisition and publish its own terminal cursor with it. An ordinary or incomplete scan cannot publish history.</summary>
    public Task PublishAsync(SqliteConnection db, string pairId, Guid scanId, CancellationToken ct) => cursors.TryGetValue((pairId, scanId), out var cursor) ? PublishAsync(db, pairId, scanId, cursor.Cursor, ct) : throw new InvalidOperationException(Text.Get(TextKey.RepairSuperseded,(TextArg.Side,Text.Get(TextKey.HumanSideDropbox))));
    /// <summary>Fold only this completed history acquisition and publish its own terminal cursor with it. An ordinary or incomplete scan cannot publish history.</summary>
    public async Task PublishAsync(SqliteConnection db, string pairId, Guid scanId, string terminalCursor, CancellationToken ct)
    {
        if (!cursors.TryGetValue((pairId, scanId), out var cursor) || cursor.Cursor != terminalCursor)
            throw new InvalidOperationException(Text.Get(TextKey.RepairSuperseded,(TextArg.Side,Text.Get(TextKey.HumanSideDropbox))));
        using var tx = db.BeginTransaction();
        ct.ThrowIfCancellationRequested();var state=State(db,pairId,tx);
        if (state.Origin != cursor.Origin)
            throw new InvalidOperationException(Text.Get(TextKey.RepairSuperseded,(TextArg.Side,Text.Get(TextKey.HumanSideDropbox))));
        var token = Token(pairId, scanId);
        if (state.Cursor is null) await PublishBlocks(db, tx, pairId, token, ct);
        else await MergeDelta(db, tx, pairId, token, ct);
        using var publish = StorageCommand.Create(db, tx, Sql.Get(SqlKey.DeletedNamesPublish1), ("$p", pairId), ("$c", terminalCursor));
        await publish.ExecuteNonQueryAsync(ct);
        tx.Commit();
        Forget(pairId, scanId);
    }
    private static async Task PublishBlocks(SqliteConnection db, SqliteTransaction tx, string pairId, byte[] token, CancellationToken ct)
    {
        using var clear = StorageCommand.Create(db, tx, Sql.Get(SqlKey.DeletedNamesPublishBlocks1), "$p", pairId);
        await clear.ExecuteNonQueryAsync(ct);
        string after = "", pageSql = Sql.Get(SqlKey.DeletedNamesPublishBlocks2), saveSql = Sql.Get(SqlKey.DeletedNamesPublishBlocks3);
        while (true)
        {
            using var cmd = StorageCommand.Create(db, tx, pageSql, ("$p", pairId), ("$s", token), ("$a", after), ("$limit", DeletedNamesCodec.DeletedBlockRows));
            var keys = new List<string>();
            using (var reader = await cmd.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    keys.Add(reader.GetString(0));
            if (keys.Count == 0)
                break;
            after = keys[^1];
            using var save = StorageCommand.Create(db, tx, saveSql, ("$p", pairId), ("$a", after), ("$n", keys.Count), ("$b", DeletedNamesCodec.EncodeDeletedNames(keys)));
            await save.ExecuteNonQueryAsync(ct);
        }

        using var retire = StorageCommand.Create(db, tx, Sql.Get(SqlKey.DeletedNamesPublishBlocks4), "$p", pairId);
        await retire.ExecuteNonQueryAsync(ct);
    }

    private static async Task MergeDelta(SqliteConnection db, SqliteTransaction tx, string pairId, byte[] token, CancellationToken ct)
    {
        using var merge = StorageCommand.Create(db, tx, Sql.Get(SqlKey.DeletedNamesMergeDelta1), ("$p", pairId), ("$s", token));
        await merge.ExecuteNonQueryAsync(ct);
    }

}
