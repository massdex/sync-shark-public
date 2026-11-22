namespace Massdex.SyncShark.Engine.Storage;

/// <summary>RecordTable operations for the storage module.</summary>
public sealed class RecordTable
{
    /// <summary>Find operation on the supplied facts.</summary>
    public Task<SyncedEntry?> FindAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Side side, string key, CancellationToken ct) => throw new NotImplementedException();
}
