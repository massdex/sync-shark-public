using System.Diagnostics;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal Func<TimeSpan>? JournalElapsedForTest { get; set; }

    public IReadOnlyDictionary<string, (ItemState? Item, bool MultipleNames)> JournalObjects(
        string rootId, IReadOnlyList<string> objectIds, CancellationToken ct) => InStore(() =>
    {
        if (objectIds.Count > JournalReadBudget.Objects) throw new ArgumentOutOfRangeException(nameof(objectIds));
        var started = Stopwatch.GetTimestamp();
        var rootNo = RootNo(rootId);
        var result = new Dictionary<string, (ItemState? Item, bool MultipleNames)>(StringComparer.Ordinal);
        foreach (var id in objectIds)
        {
            ct.ThrowIfCancellationRequested();
            if (result.Count > 0 && (JournalElapsedForTest?.Invoke() ?? Stopwatch.GetElapsedTime(started)) >= JournalReadBudget.StoreTime) break;
            var item = rootNo is null ? null : _reads.ByObject(rootNo.Value, id);
            var multiple = rootNo is not null && StoreSchema.Scalar(_connection, null, JournalMultipleNamesSql,
                ("$r", rootNo.Value), ("$o", id)) is not null;
            result.Add(id, (item, multiple));
        }
        return (IReadOnlyDictionary<string, (ItemState? Item, bool MultipleNames)>)result;
    });

    // Same LIMIT-two probe as HasMultipleNames, pinned to the entity index like ByObject.
    internal const string JournalMultipleNamesSql =
        "SELECT 1 FROM file_state INDEXED BY fs_obj WHERE root_no=$r AND object_id=$o LIMIT 1 OFFSET 1;";
}
