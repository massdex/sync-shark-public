namespace Massdex.SyncShark.Engine.Storage;

/// <summary>DeletedNames operations for the storage module.</summary>
public sealed class DeletedNames
{
    /// <summary>Contains operation on the supplied facts.</summary>
    public Task<bool> ContainsAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Guid? scanId, string key, CancellationToken ct) => throw new NotImplementedException();
    public Task<(string? Cursor, bool Owed)> ReadStateAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId,CancellationToken ct) => throw new NotImplementedException();
    public Task PublishAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId,Guid scanId,string terminalCursor,CancellationToken ct) => throw new NotImplementedException();
}
