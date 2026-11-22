namespace Massdex.SyncShark.Engine.Storage;

/// <summary>NoticeTable operations for the storage module.</summary>
public sealed class NoticeTable
{
    /// <summary>List operation on the supplied facts.</summary>
    public IAsyncEnumerable<Notice> ListAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, CancellationToken ct) => throw new NotImplementedException();
}
