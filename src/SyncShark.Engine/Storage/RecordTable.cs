namespace Massdex.SyncShark.Engine.Storage;

/// <summary>RecordTable operations for the storage module.</summary>
public sealed class RecordTable
{
    /// <summary>Find operation on the supplied facts.</summary>
    public Task<SyncedEntry?> FindAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Side side, string key, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Stream every indexed binding for this identity.</summary>
    public IAsyncEnumerable<SyncedEntry> ByIdAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Side side, string id, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Stream the indexed bindings below the selected key.</summary>
    public IAsyncEnumerable<SyncedEntry> DescendantsAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Side side, string key, int pageSize, CancellationToken ct) => throw new NotImplementedException();
}
