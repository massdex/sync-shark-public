namespace Massdex.SyncShark.Engine.Storage;

/// <summary>PairTable operations for the storage module.</summary>
public sealed class PairTable
{
    /// <summary>Read operation on the supplied facts.</summary>
    public Task<PairConfig?> ReadAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Publish the pair edit or removal and its returned scopes in the caller’s immediate write transaction. Check the accepted jobs, armed note and PutBack notices through that transaction, not earlier connection reads. Return the blocking cause on refusal, null on success.</summary>
    public Task<TextKey?> PublishAsync(Microsoft.Data.Sqlite.SqliteTransaction tx, string pairId, PairConfig? replacement, IReadOnlyList<(string Owner,Scope Scope)> returnedScopes, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Complete full Repair only when both captured revisions still match.</summary>
    public Task<bool> FinishRepairAsync(Microsoft.Data.Sqlite.SqliteTransaction tx, string pairId, (long Local,long Dropbox) revisions, string completedRepairJson, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Retire only the matching mapped scope at the owner’s Dropbox revision. No first-run bit or full summary is published.</summary>
    public Task<bool> FinishRepairAsync(Microsoft.Data.Sqlite.SqliteTransaction tx, string pairId, Scope scope, long revision, CancellationToken ct) => throw new NotImplementedException();


    /// <summary>Read or settle the named store operation without changing unrelated state.</summary>
    public Task<(bool FirstRepairDone, string? LastRepairJson)> RepairStateAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>On lost journal continuity only, install the fresh volume boundary and increment the Local Repair revision/reason of every affected pair in one transaction.</summary>
    public Task ResetJournalAsync(Microsoft.Data.Sqlite.SqliteTransaction tx, string volume, JournalPosition fresh, IReadOnlyList<string> affectedPairs, string reason, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>On Dropbox reset only, install its fresh cursor and increment that pair’s Dropbox Repair revision/reason in one transaction.</summary>
    public Task ResetCursorAsync(Microsoft.Data.Sqlite.SqliteTransaction tx, string pairId, string fresh, string reason, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Save the accepted job with its members in wire order.</summary>
    public Task SaveJobAsync(Microsoft.Data.Sqlite.SqliteConnection db, AcceptedJob job, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read the accepted jobs still owned by this pair.</summary>
    public IAsyncEnumerable<AcceptedJob> JobsAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Retire the matching settled job.</summary>
    public Task SettleJobAsync(Microsoft.Data.Sqlite.SqliteConnection db, AcceptedJob job, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Arm the exact PC victim and stage before removal.</summary>
    public Task ArmAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, RemovalSelection victim, StageProof? stage, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read the pair’s armed PC note.</summary>
    public Task<(RemovalSelection Victim, StageProof? Stage)?> ArmedAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Clear only the matching victim and stage.</summary>
    public Task DisarmAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, RemovalSelection expected, StageProof? expectedStage, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Read or update the existing pair or source facts.</summary>
    public IAsyncEnumerable<PairConfig> PairsAsync(Microsoft.Data.Sqlite.SqliteConnection db, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read or update the existing pair or source facts.</summary>
    public Task<JournalPosition?> PositionAsync(Microsoft.Data.Sqlite.SqliteConnection db, string volume, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read or update the existing pair or source facts.</summary>
    public Task SavePositionAsync(Microsoft.Data.Sqlite.SqliteConnection db, string volume, JournalPosition position, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read or update the existing pair or source facts.</summary>
    public Task<string?> CursorAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read or update the existing pair or source facts.</summary>
    public Task SaveCursorAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, string cursor, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read or update the existing pair or source facts.</summary>
    public Task<long> OweRepairAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Side side, string reason, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Read or update the existing pair or source facts.</summary>
    public Task<(long Revision, string? Reason)> ReadRepairAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Side side, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>In one transaction match the reviewed drive notice and publish the chosen pair; only FirstSync resets bindings and first Repair.</summary>
    public Task<bool> ResolveDriveAsync(Microsoft.Data.Sqlite.SqliteConnection db, PairConfig pair, Notice reviewed, ResolutionChoice choice, CancellationToken ct) => throw new NotImplementedException();
}
