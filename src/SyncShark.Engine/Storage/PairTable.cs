namespace Massdex.SyncShark.Engine.Storage;

/// <summary>PairTable operations for the storage module.</summary>
public sealed class PairTable
{
    /// <summary>Read operation on the supplied facts.</summary>
    public Task<PairConfig?> ReadAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Save operation on the supplied facts.</summary>
    public Task SaveAsync(Microsoft.Data.Sqlite.SqliteConnection db, PairConfig pair, CancellationToken ct) => throw new NotImplementedException();
}
