namespace Massdex.SyncShark.Engine.Storage;

/// <summary>NoticeTable operations for the storage module.</summary>
public sealed class NoticeTable
{
    /// <summary>List operation on the supplied facts.</summary>
    public IAsyncEnumerable<Notice> ListAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    public IAsyncEnumerable<Notice> NoticesAtAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, string pathKey, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Upsert the (pair, path, kind) slot and its members atomically; preserve a different kind’s subject and visibility.</summary>
    public Task SaveNoticeAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Notice notice, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Close only the exact expected notice and update its members in the same transaction.</summary>
    public Task CloseNoticeAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Notice expected, CancellationToken ct) => throw new NotImplementedException();
}
