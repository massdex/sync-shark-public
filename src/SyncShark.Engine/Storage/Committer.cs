namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Committer operations for the storage module.</summary>
public sealed class Committer
{
    /// <summary>Commit operation on the supplied facts.</summary>
    public Task CommitAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, SyncedEntry? before, SyncedEntry? after, JournalPosition? publication, CancellationToken ct, string? spentDeletedKey = null) => throw new NotImplementedException();
    /// <summary>Projects both endpoint prefixes set-wise without one command per binding.</summary>
    public Task ProjectSubtreeAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Scope from, Scope to, CancellationToken ct) => throw new NotImplementedException();
}
