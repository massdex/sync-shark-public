namespace Massdex.SyncShark.Engine.Storage;

/// <summary>DeletedNames operations for the storage module.</summary>
public sealed class DeletedNames
{
    /// <summary>Contains operation on the supplied facts.</summary>
    public Task<bool> ContainsAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Guid? scanId, string key, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read the separate deleted-history cursor and debt.</summary>
    public Task<(string? Cursor, bool Owed)> ReadStateAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId,CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Publish the completed named acquisition and its terminal history cursor together.</summary>
    public Task PublishAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId,Guid scanId,string terminalCursor,CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Check retained history for the specified candidate kind.</summary>
    public Task<bool> ContainsAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Guid? scanId, string key, bool isFolder, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Publish a completed acquisition at its captured cursor.</summary>
    public Task PublishAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Guid scanId, CancellationToken ct) => throw new NotImplementedException();
}
