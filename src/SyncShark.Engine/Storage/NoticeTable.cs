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
    /// <summary>Stream notices whose members overlap the mapped footprint on their own side, including covering ancestors.</summary>
    public IAsyncEnumerable<Notice> NoticesUnderAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Scope footprint, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Add or retire only this observed link member; close the pair-wide link notice only when no members remain.</summary>
    public Task UpdateLinkMemberAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, NoticeItem member, bool present, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read one indexed notice by pair and ID; the caller must still validate its reviewed token.</summary>
    public Task<Notice?> NoticeAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, long id, CancellationToken ct) => throw new NotImplementedException();
}
