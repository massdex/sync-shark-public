namespace Massdex.SyncShark.Engine.Storage;

/// <summary>DeletedNames operations for the storage module.</summary>
public sealed class DeletedNames
{
    /// <summary>Contains operation on the supplied facts.</summary>
    public Task<bool> ContainsAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, string key, CancellationToken ct) => throw new NotImplementedException();
}
