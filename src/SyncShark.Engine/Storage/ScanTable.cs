namespace Massdex.SyncShark.Engine.Storage;

/// <summary>ScanTable operations for the storage module.</summary>
public sealed class ScanTable
{
    /// <summary>Compare operation on the supplied facts.</summary>
    public IAsyncEnumerable<(Row Row, IReadOnlyList<(Notice Notice, NoticeItem? Member, bool Covered, bool Touched)> Notices)> CompareAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Guid scanId, Scope? scope, Func<Side,string,(bool Covered,bool Touched)> evidence, CancellationToken ct) => throw new NotImplementedException();
    public Task AddScanAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Guid scanId, Side side, IAsyncEnumerable<Listed> rows, CancellationToken ct) => throw new NotImplementedException();
    public Task ClearScanAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Guid scanId, CancellationToken ct) => throw new NotImplementedException();
}
