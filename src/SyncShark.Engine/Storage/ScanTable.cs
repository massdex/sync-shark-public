namespace Massdex.SyncShark.Engine.Storage;

/// <summary>ScanTable operations for the storage module.</summary>
public sealed class ScanTable
{
    /// <summary>Compare operation on the supplied facts.</summary>
    public IAsyncEnumerable<(Row Row, IReadOnlyList<(Notice Notice, NoticeItem? Member, bool Covered, bool Touched)> Notices)> CompareAsync(Microsoft.Data.Sqlite.SqliteConnection db, string pairId, Scope? scope, Func<Side,string,(bool Covered,bool Touched)> evidence, CancellationToken ct) => throw new NotImplementedException();
}
