using System.Text;

namespace Massdex.SyncShark.V2;

// Bounded reads of the visible open-notice catalog for `conflicts list` and the tray icon's count.
// The page is read in SQL before any materialization and the total is one capped COUNT, so neither cost
// grows with the number of open notices.
public sealed partial class SqliteStateStore
{
    internal readonly record struct ConflictNoticePage(IReadOnlyList<Notice> Items, long Total, bool TotalAtLeast);

    /// <summary>One bounded page plus its capped total; never materializes more rows than the caller asked for.</summary>
    internal ConflictNoticePage OpenConflictPage(string? pairId, string? accountId, string? search, int limit,
        int offset) => InStore(() =>
    {
        var scope = ConflictScopeOf(pairId, accountId);
        if (scope.Empty) return new ConflictNoticePage(Array.Empty<Notice>(), 0, false);
        var (filters, parameters) = ConflictFilters(scope, search);
        var total = CountConflicts(filters, parameters);
        var items = limit <= 0 ? (IReadOnlyList<Notice>)Array.Empty<Notice>()
            : ReadConflictPage(scope, filters, parameters, limit, offset);
        return new ConflictNoticePage(items, total, total >= NoticeTable.OpenPageCap);
    });

    /// <summary>The one capped count behind both the tray icon and the catalog total.</summary>
    internal int OpenConflictCount() => (int)Math.Min(int.MaxValue, CountVisibleConflicts(null, null, null));

    /// <summary>The free-space notices behind the conflicts page's grouped summary, bounded by the same cap.</summary>
    internal IReadOnlyList<Notice> OpenFreeSpaceNotices(string? pairId, string? accountId, string? search) => InStore(() =>
    {
        var scope = ConflictScopeOf(pairId, accountId);
        if (scope.Empty) return (IReadOnlyList<Notice>)Array.Empty<Notice>();
        var (filters, parameters) = ConflictFilters(scope, search);
        using var command = StoreSchema.Command(_connection, null, ConflictCatalog.FreeSpace(filters));
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.Parameters.AddWithValue("$cap", NoticeTable.OpenPageCap);
        return (IReadOnlyList<Notice>)ReadNotices(command);
    });

    private long CountVisibleConflicts(string? pairId, string? accountId, string? search) => InStore(() =>
    {
        var scope = ConflictScopeOf(pairId, accountId);
        if (scope.Empty) return 0L;
        var (filters, parameters) = ConflictFilters(scope, search);
        return CountConflicts(filters, parameters);
    });

    private long CountConflicts(string filters, IReadOnlyList<(string Name, object? Value)> parameters) =>
        Convert.ToInt64(StoreSchema.Scalar(_connection, null, ConflictCatalog.Count(filters),
            [.. parameters, ("$cap", (object?)NoticeTable.OpenPageCap)]));

    private IReadOnlyList<Notice> ReadConflictPage(ConflictScope scope, string filters,
        IReadOnlyList<(string Name, object? Value)> parameters, int limit, int offset)
    {
        // nt_open is the ordered exact-pair index; nt_page is the ordered global/account index.
        using var command = StoreSchema.Command(_connection, null,
            ConflictCatalog.Page(scope.PairNo is null ? "nt_page" : "nt_open", filters));
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        return ReadNotices(command);
    }

    private static List<Notice> ReadNotices(Microsoft.Data.Sqlite.SqliteCommand command)
    {
        var notices = new List<Notice>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) notices.Add(NoticeTable.ReadNotice(reader));
        return notices;
    }

    // An empty scope means the exact pair or account has no pair at all: the answer is an empty page, never a scan.
    private sealed record ConflictScope(bool Empty, long? PairNo, long[]? AccountPairs);

    private ConflictScope ConflictScopeOf(string? pairId, string? accountId)
    {
        long? pairNo = null;
        if (pairId is not null)
        {
            pairNo = PairNo(pairId);
            if (pairNo is null) return new ConflictScope(true, null, null);
        }
        long[]? accountPairs = null;
        if (accountId is not null)
        {
            accountPairs = Pairs()
                .Where(pair => string.Equals(pair.B.AccountId, accountId, StringComparison.Ordinal))
                .Select(pair => PairNo(pair.PairId))
                .OfType<long>()
                .ToArray();
            if (accountPairs.Length == 0) return new ConflictScope(true, null, null);
        }
        return new ConflictScope(false, pairNo, accountPairs);
    }

    private static (string Filters, IReadOnlyList<(string Name, object? Value)> Parameters) ConflictFilters(
        ConflictScope scope, string? search)
    {
        var filters = new StringBuilder(ConflictCatalog.VisibleFilter("n"));
        var parameters = new List<(string Name, object? Value)>();
        if (scope.PairNo is { } pairNo)
        {
            filters.Append(" AND n.pair_no=$p");
            parameters.Add(("$p", pairNo));
        }
        if (scope.AccountPairs is { } accountPairs)
        {
            var names = new string[accountPairs.Length];
            for (var index = 0; index < accountPairs.Length; index++)
            {
                names[index] = "$a" + index;
                parameters.Add((names[index], accountPairs[index]));
            }
            filters.Append(" AND n.pair_no IN (" + string.Join(',', names) + ")");
        }
        if (!string.IsNullOrEmpty(search))
        {
            filters.Append(" AND instr(lower(n.path), lower($search))>0");
            parameters.Add(("$search", search));
        }
        return (filters.ToString(), parameters);
    }
}
