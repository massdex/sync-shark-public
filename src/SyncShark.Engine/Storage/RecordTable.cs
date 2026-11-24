using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Indexed reads of the two physical endpoints and the local publication boundary of each binding.</summary>
public sealed class RecordTable
{
    private const string LocalKey="pathkey(local_path)", Delta="dropbox_key_delta";
    private const string Select="SELECT rowid,pathkey(local_path),local_path,dropbox_key_delta,dropbox_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash,local_published_journal,local_published_usn FROM synced_entry WHERE pair_id="+PairTable.PairKey+" AND ";
    private static SyncedEntry Read(SqliteDataReader reader) => Encoding.ReadSynced(reader,reader.GetString(1),2);

    /// <summary>Find the exact side key, including the saved LocalPublished journal/USN (or null if unpublished).</summary>
    public async Task<SyncedEntry?> FindAsync(SqliteConnection db,string pairId,Side side,string key,CancellationToken ct)
    {
        var sql=side==Side.Local ? Select+LocalKey+"=$key" : Select+Delta+"=$key UNION ALL "+Select+Delta+" IS NULL AND "+LocalKey+"=$key";
        using var cmd=StorageCommand.Create(db,null,sql,("$p",pairId),("$key",key)); using var reader=await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)?Read(reader):null;
    }
    /// <summary>Read every indexed binding for the encoded identity with its saved LocalPublished boundary.</summary>
    public async IAsyncEnumerable<SyncedEntry> ByIdAsync(SqliteConnection db,string pairId,Side side,string id,int pageSize,[EnumeratorCancellation]CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        var column=side==Side.Local?"local_id":"dropbox_id";
        var encoded=Encoding.IdFor(side,id); long after=0; int count;
        do
        {
            count=0;
            using var cmd=StorageCommand.Create(db,null,Select+column+"=$id AND rowid>$after ORDER BY rowid LIMIT $size",("$p",pairId),("$id",encoded),("$after",after),("$size",pageSize));
            using var reader=await cmd.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct)) { after=reader.GetInt64(0); count++; yield return Read(reader); }
        } while(count==pageSize);
    }
    /// <summary>Page strict descendants on the selected side, retaining each row's saved LocalPublished boundary.</summary>
    public async IAsyncEnumerable<SyncedEntry> DescendantsAsync(SqliteConnection db,string pairId,Side side,string key,int pageSize,[EnumeratorCancellation]CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        var (low,high)=PathNames.DescendantBounds(key);
        foreach(var column in side==Side.Local ? new[]{LocalKey} : new[]{Delta,LocalKey})
        {
            var inherited=side==Side.Dropbox && column==LocalKey ? Delta+" IS NULL AND " : "";
            var sql=Select+inherited+column+">$after"+(key.Length==0?"":" AND "+column+"<$high")+" ORDER BY "+column+" LIMIT $size";
            var after=key.Length==0?"":low; int count;
            do
            {
                count=0;
                using var cmd=StorageCommand.Create(db,null,sql,("$p",pairId),("$after",after),("$size",pageSize));
                if(key.Length!=0) cmd.Parameters.AddWithValue("$high",high);
                using var reader=await cmd.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct)) { var row=Read(reader); after=row.KeyOn(side); count++; yield return row; }
            } while(count==pageSize);
        }
    }
}
