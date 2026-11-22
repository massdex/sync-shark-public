namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Committer operations for the storage module.</summary>
public sealed class Committer
{
    /// <summary>Commit operation on the supplied facts.</summary>
    public Task CommitAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, SyncedEntry? before, SyncedEntry? after, JournalPosition? publication, CancellationToken ct) => throw new NotImplementedException();
}
