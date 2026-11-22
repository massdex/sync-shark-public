namespace Massdex.SyncShark.Engine.Storage;

/// <summary>ScanTable operations for the storage module.</summary>
public sealed class ScanTable
{
    /// <summary>Project the PutBack kind independently of ordinary Failure at the same path. No compare outcome settles it.</summary>
    public IAsyncEnumerable<(Row Row, IReadOnlyList<(Notice Notice, NoticeItem? Member, bool Covered, bool Touched)> Notices)> CompareAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Guid scanId, Scope? scope, Func<Side,string,(bool Covered,bool Touched)> evidence, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Append this acquisition’s listed rows and optional completed history cursor.</summary>
    public Task AddScanAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Guid scanId, Side side, IAsyncEnumerable<Listed> rows, Func<string>? terminalHistoryCursor, int batchRows, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Release scratch belonging only to this acquisition.</summary>
    public Task ClearScanAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Guid scanId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Stream the existing scan’s move candidates before ordinary differences, ordered by shallowest candidate of the binding then by binding so all its observed sides are adjacent.</summary>
    public IAsyncEnumerable<RenameCandidate> RenamesAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Guid scanId, Scope? scope, int pageSize, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Begin tracking changes to the current acquisition.</summary>
    public void Begin(string pairId, Guid scanId) => throw new NotImplementedException();
    /// <summary>Mark an observed change during acquisition.</summary>
    public void Touch(string pairId, Side side, string path, bool subtree) => throw new NotImplementedException();
    /// <summary>Query changes overlapping the selected range.</summary>
    public bool Touched(string pairId, Guid scanId, Side side, string path, bool descendants) => throw new NotImplementedException();
    /// <summary>Check whether this pair has an active scan.</summary>
    public bool HasActive(string pairId) => throw new NotImplementedException();
    /// <summary>The history reader shared by this scan owner.</summary>
    public DeletedNames History => throw new NotImplementedException();
}
