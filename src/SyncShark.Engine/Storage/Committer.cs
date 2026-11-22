namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Committer operations for the storage module.</summary>
public sealed class Committer
{
    /// <summary>Commit operation on the supplied facts. Without a new publication keep the carried boundary only on a same-PC-identity relink.</summary>
    public Task CommitAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, SyncedEntry? before, SyncedEntry? after, JournalPosition? publication, string? spentDeletedKey, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Projects both endpoint prefixes set-wise without one command per binding.</summary>
    public Task ProjectSubtreeAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Scope from, Scope to, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Replace the old link with both new links and their notice in one transaction.</summary>
    public Task CommitKeepBothAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, SyncedEntry? before, SyncedEntry first, SyncedEntry second, Notice notice, CancellationToken ct) => throw new NotImplementedException();
}
