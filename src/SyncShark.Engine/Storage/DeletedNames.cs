using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

namespace Massdex.SyncShark.Engine.Storage;
/// <summary>Acquisition-local and published Dropbox deletion history, including spent-name masks.</summary>
public sealed class DeletedNames
{
    private const string Pair = "(SELECT id FROM pair WHERE pair_id=$p)";
    private readonly ConcurrentDictionary<(string, Guid), (string Cursor, string Origin)> cursors = new();
    internal void Completed(string pair, Guid scan, string cursor, string origin)
    {
        ArgumentException.ThrowIfNullOrEmpty(cursor);
        cursors[(pair, scan)] = (cursor, origin);
    }

    internal static (string Origin,string? Cursor,bool Owed) State(SqliteConnection db,string pair,SqliteTransaction? tx=null)
    {
        const string sql="SELECT json_array(id,deleted_history_cursor,deleted_history_owed,json_extract(def_json,'$.LocalRoot'),json_extract(def_json,'$.DropboxRoot'),json_extract(def_json,'$.ConnectionId'),json_extract(def_json,'$.Roots')),deleted_history_cursor,deleted_history_owed FROM pair WHERE pair_id=$p";
        using var cmd=StorageCommand.Create(db,tx,sql,"$p",pair);using var row=cmd.ExecuteReader();
        if(!row.Read())throw new InvalidOperationException(Text.Get(TextKey.PairUnknown,(TextArg.Pair,pair)));
        return(row.GetString(0),row.IsDBNull(1)?null:row.GetString(1),row.GetBoolean(2));
    }
    internal void Forget(string pair, Guid scan) => cursors.TryRemove((pair, scan), out _);
    internal static byte[] Token(string pair, Guid scan) => [..scan.ToByteArray(), ..System.Text.Encoding.UTF8.GetBytes(pair)];
    /// <summary>Read exact deletion evidence for a file, respecting its spent mask.</summary>
    public Task<bool> ContainsAsync(SqliteConnection db, string pairId, Guid? scanId, string key, CancellationToken ct) => ContainsAsync(db, pairId, scanId, key, false, ct);
    /// <summary>Check the exact file tombstone; a folder may also match an ancestor. A spent candidate is never eligible.</summary>
    public async Task<bool> ContainsAsync(SqliteConnection db, string pairId, Guid? scanId, string key, bool isFolder, CancellationToken ct)
    {
        using var tx=db.BeginTransaction(deferred:true);
        ct.ThrowIfCancellationRequested();var (origin,cursor,owed)=State(db,pairId,tx);
        if(scanId is Guid acquisition && (!cursors.TryGetValue((pairId,acquisition),out var done) || done.Origin!=origin))return false;
        if (scanId is null && (owed || cursor is null))
            return false;
        for (var candidate = key; candidate.Length != 0; candidate = PathNames.Parent(candidate))
        {
            using var delta = StorageCommand.Create(db, tx, "SELECT spent FROM discovery_deleted_delta WHERE pair_id=" + Pair + " AND path_key=$k", ("$p", pairId), ("$k", candidate));
            if (await delta.ExecuteScalarAsync(ct)is long spent)
            {
                if (spent == 0)
                    return true;
                if (candidate == key)
                    return false;
            }
            else
            {
                if (scanId is Guid id)
                {
                    using var scratch = StorageCommand.Create(db, tx, "SELECT 1 FROM scan_state.deleted_name WHERE scan_id=$s AND path_key=$k", ("$s", Token(pairId, id)), ("$k", candidate));
                    if (await scratch.ExecuteScalarAsync(ct) != null)
                        return true;
                }

                if (cursor != null)
                {
                    using var block = StorageCommand.Create(db, tx, "SELECT last_key,name_count,rowid,(SELECT max(last_key) FROM discovery_deleted_block prior WHERE prior.pair_id=b.pair_id AND prior.last_key<b.last_key) FROM discovery_deleted_block b WHERE pair_id=" + Pair + " AND last_key >= $k ORDER BY last_key LIMIT 1", ("$p", pairId), ("$k", candidate));
                    using var reader = await block.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        using var bytes = new SqliteBlob(db, "discovery_deleted_block", "names", reader.GetInt64(2), true);
                        bool found = false;
                        foreach (var name in Encoding.DecodeDeletedNames(bytes, reader.GetInt32(1), reader.GetString(0), reader.IsDBNull(3) ? null : reader.GetString(3)))
                            found |= name == candidate;
                        if (found)
                            return true;
                    }
                }
            }

            if (!isFolder)
                break;
        }

        return false;
    }

    /// <summary>Read the separate deleted-history cursor and acquisition debt.</summary>
    public Task<(string? Cursor, bool Owed)> ReadStateAsync(SqliteConnection db, string pairId, CancellationToken ct) => ReadStateAsync(db,pairId,ct,null);
    private static async Task<(string? Cursor,bool Owed)> ReadStateAsync(SqliteConnection db,string pairId,CancellationToken ct,SqliteTransaction? tx)
    {
        using var cmd = StorageCommand.Create(db, tx, "SELECT deleted_history_cursor,deleted_history_owed FROM pair WHERE pair_id=$p", "$p", pairId);
        using var row = await cmd.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct))
            throw new InvalidOperationException(Text.Get(TextKey.PairUnknown, (TextArg.Pair, pairId)));
        return (row.IsDBNull(0) ? null : row.GetString(0), row.GetBoolean(1));
    }

    /// <summary>Publish the exact terminal cursor recorded by the completed listing for this pair and scan.</summary>
    public Task PublishAsync(SqliteConnection db, string pairId, Guid scanId, CancellationToken ct) => cursors.TryGetValue((pairId, scanId), out var cursor) ? PublishAsync(db, pairId, scanId, cursor.Cursor, ct) : throw new InvalidOperationException(Text.Get(TextKey.RepairSuperseded));
    /// <summary>Fold only the completed named history acquisition and publish its terminal cursor atomically.</summary>
    public async Task PublishAsync(SqliteConnection db, string pairId, Guid scanId, string terminalCursor, CancellationToken ct)
    {
        if (!cursors.TryGetValue((pairId, scanId), out var cursor) || cursor.Cursor != terminalCursor)
            throw new InvalidOperationException(Text.Get(TextKey.RepairSuperseded));
        using var tx = db.BeginTransaction();
        ct.ThrowIfCancellationRequested();var state=State(db,pairId,tx);
        if (state.Origin != cursor.Origin)
            throw new InvalidOperationException(Text.Get(TextKey.RepairSuperseded));
        var token = Token(pairId, scanId);
        if (state.Cursor is null)
        {
            using var clear = StorageCommand.Create(db, tx, "DELETE FROM discovery_deleted_block WHERE pair_id=" + Pair, "$p", pairId);
            await clear.ExecuteNonQueryAsync(ct);
            string after = "";
            while (true)
            {
                using var cmd = StorageCommand.Create(db, tx, "SELECT path_key FROM scan_state.deleted_name WHERE scan_id=$s AND path_key>$a UNION SELECT path_key FROM discovery_deleted_delta WHERE pair_id=" + Pair + " AND spent=0 AND path_key>$a ORDER BY path_key LIMIT 5000", ("$p", pairId), ("$s", token), ("$a", after));
                var keys = new List<string>();
                using (var reader = await cmd.ExecuteReaderAsync(ct))
                    while (await reader.ReadAsync(ct))
                        keys.Add(reader.GetString(0));
                if (keys.Count == 0)
                    break;
                after = keys[^1];
                using var save = StorageCommand.Create(db, tx, "INSERT INTO discovery_deleted_block VALUES(" + Pair + ",$a,$n,$b)", ("$p", pairId), ("$a", after), ("$n", keys.Count), ("$b", Encoding.EncodeDeletedNames(keys)));
                await save.ExecuteNonQueryAsync(ct);
            }

            using var retire = StorageCommand.Create(db, tx, "DELETE FROM discovery_deleted_delta WHERE pair_id=" + Pair + " AND spent=0", "$p", pairId);
            await retire.ExecuteNonQueryAsync(ct);
        }
        else
        {
            using var merge = StorageCommand.Create(db, tx, "INSERT OR IGNORE INTO discovery_deleted_delta(pair_id,path_key) SELECT " + Pair + ",path_key FROM scan_state.deleted_name WHERE scan_id=$s", ("$p", pairId), ("$s", token));
            await merge.ExecuteNonQueryAsync(ct);
        }

        using var publish = StorageCommand.Create(db, tx, "UPDATE pair SET deleted_history_cursor=$c,deleted_history_owed=0 WHERE pair_id=$p", ("$p", pairId), ("$c", terminalCursor));
        await publish.ExecuteNonQueryAsync(ct);
        tx.Commit();
        Forget(pairId, scanId);
    }

}
