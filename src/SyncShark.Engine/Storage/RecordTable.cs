using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Indexed reads of the two physical endpoints and the local publication boundary of each binding.</summary>
public sealed class RecordTable
{
    private static readonly int RowIdColumn, KeyColumn, LocalPathColumn;
    static RecordTable()
    {
        var columns = Sql.Get(SqlKey.RecordTableColumns).Split(',');
        RowIdColumn = Array.IndexOf(columns, "rowid");
        KeyColumn = Array.IndexOf(columns, Sql.Get(SqlKey.RecordTableLocalKey));
        LocalPathColumn = Array.IndexOf(columns, "local_path");
    }
    private static SyncedEntry Read(SqliteDataReader reader) => Encoding.ReadSynced(reader,reader.GetString(KeyColumn),LocalPathColumn);

    /// <summary>Find the exact side key, including the saved LocalPublished journal/USN (or null if unpublished).</summary>
    public async Task<SyncedEntry?> FindAsync(SqliteConnection db,string pairId,Side side,string key,CancellationToken ct)
    {
        var sql=side==Side.Local ? Sql.Get(SqlKey.RecordTableFind1) : Sql.Get(SqlKey.RecordTableFind2);
        using var cmd=StorageCommand.Create(db,null,sql,("$p",pairId),("$key",key)); using var reader=await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)?Read(reader):null;
    }
    /// <summary>Read every indexed binding for the encoded identity with its saved LocalPublished boundary.</summary>
    public async IAsyncEnumerable<SyncedEntry> ByIdAsync(SqliteConnection db,string pairId,Side side,string id,int pageSize,[EnumeratorCancellation]CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        var column=side==Side.Local?Sql.Get(SqlKey.RecordTableById1):Sql.Get(SqlKey.RecordTableById2);
        var encoded=Encoding.IdFor(side,id); long after=0; int count;
        var sql=Sql.Get(SqlKey.RecordTableSelect)+column+Sql.Get(SqlKey.RecordTableById3);
        do
        {
            count=0;
            using var cmd=StorageCommand.Create(db,null,sql,("$p",pairId),("$id",encoded),("$after",after),("$size",pageSize));
            using var reader=await cmd.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct)) { after=reader.GetInt64(RowIdColumn); count++; yield return Read(reader); }
        } while(count==pageSize);
    }
    /// <summary>Page strict descendants on the selected side, retaining each row's saved LocalPublished boundary.</summary>
    public async IAsyncEnumerable<SyncedEntry> DescendantsAsync(SqliteConnection db,string pairId,Side side,string key,int pageSize,[EnumeratorCancellation]CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        var (low,high)=PathNames.DescendantBounds(key);
        foreach(var column in side==Side.Local ? new[]{Sql.Get(SqlKey.RecordTableLocalKey)} : new[]{Sql.Get(SqlKey.RecordTableDelta),Sql.Get(SqlKey.RecordTableLocalKey)})
        {
            var inherited=side==Side.Dropbox && column==Sql.Get(SqlKey.RecordTableLocalKey) ? Sql.Get(SqlKey.RecordTableDescendants1) : "";
            var sql=Sql.Get(SqlKey.RecordTableSelect)+inherited+column+Sql.Get(SqlKey.RecordTableDescendants2)+(key.Length==0?"":Sql.Get(SqlKey.RecordTableDescendants3)+column+Sql.Get(SqlKey.RecordTableDescendants4))+Sql.Get(SqlKey.RecordTableDescendants5)+column+Sql.Get(SqlKey.RecordTableDescendants6);
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
