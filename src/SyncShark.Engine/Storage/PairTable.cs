namespace Massdex.SyncShark.Engine.Storage;

/// <summary>PairTable operations for the storage module.</summary>
public sealed class PairTable
{
    /// <summary>Read operation on the supplied facts.</summary>
    public Task<PairConfig?> ReadAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Publish the pair edit or removal and the returned scopes inside the caller’s transaction.</summary>
    public Task PublishAsync(Microsoft.Data.Sqlite.SqliteTransaction tx, string pairId, PairConfig? replacement, IReadOnlyList<(string Owner,Scope Scope)> returnedScopes, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> FinishRepairAsync(Microsoft.Data.Sqlite.SqliteTransaction tx, string pairId, (long Local,long Dropbox) revisions, string completedRepairJson, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Retire only the matching mapped scope at the owner’s Dropbox revision. No first-run bit or full summary is published.</summary>
    public Task<bool> FinishRepairAsync(Microsoft.Data.Sqlite.SqliteTransaction tx, string pairId, Scope scope, long revision, CancellationToken ct) => throw new NotImplementedException();


    public Task<(bool FirstRepairDone, string? LastRepairJson)> RepairStateAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>On lost journal continuity only, install the fresh volume boundary and increment the Local Repair revision/reason of every affected pair in one transaction.</summary>
    public Task ResetJournalAsync(Microsoft.Data.Sqlite.SqliteTransaction tx, string volume, JournalPosition fresh, IReadOnlyList<string> affectedPairs, string reason, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>On Dropbox reset only, install its fresh cursor and increment that pair’s Dropbox Repair revision/reason in one transaction.</summary>
    public Task ResetCursorAsync(Microsoft.Data.Sqlite.SqliteTransaction tx, string pairId, string fresh, string reason, CancellationToken ct) => throw new NotImplementedException();
}
