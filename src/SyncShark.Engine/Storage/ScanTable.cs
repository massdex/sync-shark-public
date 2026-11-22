namespace Massdex.SyncShark.Engine.Storage;

/// <summary>ScanTable operations for the storage module.</summary>
public sealed class ScanTable
{
    /// <summary>Compare operation on the supplied facts.</summary>
    public IAsyncEnumerable<Row> CompareAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Scope? scope, CancellationToken ct) => throw new NotImplementedException();
}
