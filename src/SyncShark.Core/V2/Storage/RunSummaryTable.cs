using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Owns the run_summary table: bounded run history keyed by pair and run id . The sequence is a
/// unique, never-reused integer so a caller can page with a cursor even when two runs share a start millisecond.
/// </summary>
internal sealed class RunSummaryTable
{
    private const int MinimumLimit = 1;
    private const int MaximumLimit = 200;

    private readonly SqliteConnection _connection;

    public RunSummaryTable(SqliteConnection connection) => _connection = connection;

    /// <summary>Stores one summary once, then enforces the age and row caps in the same transaction.</summary>
    public void Record(RunSummary summary, long now, TimeSpan retention, int maximumRows)
    {
        if (summary.RunId.Length == 0 || summary.PairId.Length == 0)
            throw new ArgumentException("A run summary needs a run id and a pair id.", nameof(summary));
        if (summary.Outcome.Length == 0)
            throw new ArgumentException("A run summary needs an outcome.", nameof(summary));
        using var transaction = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, transaction, """
            INSERT INTO run_summary(run_id,pair_id,started,completed,outcome,done,uploaded,downloaded,deleted,moved,
              kept_both,held,failed,uncertain,skipped,deferred)
            VALUES($run,$pair,$started,$completed,$outcome,$done,$uploaded,$downloaded,$deleted,$moved,$kept,
              $held,$failed,$uncertain,$skipped,$deferred) ON CONFLICT(run_id) DO NOTHING;
            """,
            ("$run", summary.RunId), ("$pair", summary.PairId),
            ("$started", summary.StartedUtc.ToUnixTimeMilliseconds()),
            ("$completed", summary.CompletedUtc.ToUnixTimeMilliseconds()), ("$outcome", summary.Outcome),
            ("$done", summary.Done), ("$uploaded", summary.Uploaded), ("$downloaded", summary.Downloaded),
            ("$deleted", summary.Deleted), ("$moved", summary.Moved), ("$kept", summary.KeptBoth),
            ("$held", summary.Held), ("$failed", summary.Failed), ("$uncertain", summary.Uncertain),
            ("$skipped", summary.Skipped), ("$deferred", summary.Deferred));
        Prune(transaction, now, retention, maximumRows);
        transaction.Commit();
    }

    /// <summary>Newest first; rows strictly older than <paramref name="before"/> (zero means the newest row).</summary>
    public IReadOnlyList<RunSummary> Read(string? pairId, int limit, long before)
    {
        Validate(limit);
        var pair = pairId is not null;
        using var command = StoreSchema.Command(_connection, null, CursorSql(Select, pair, before != 0));
        if (pair) command.Parameters.AddWithValue("$pair", pairId!);
        if (before != 0) command.Parameters.AddWithValue("$before", before);
        command.Parameters.AddWithValue("$limit", limit);
        return ReadRows(command);
    }

    /// <summary>Oldest first; the forward cursor used by logs (zero starts at the oldest retained row).</summary>
    public IReadOnlyList<RunSummary> ReadAfter(long after, int limit)
    {
        Validate(limit);
        using var command = StoreSchema.Command(_connection, null,
            Select + "WHERE seq>$after ORDER BY seq LIMIT $limit;");
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$limit", limit);
        return ReadRows(command);
    }

    /// <summary>Newest first, positioned by an ordinal offset for the window's existing Load More paging.</summary>
    public IReadOnlyList<RunSummary> Page(string? pairId, int limit, int offset)
    {
        Validate(limit);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "An offset cannot be negative.");
        var pair = pairId is not null;
        using var command = StoreSchema.Command(_connection, null, PageSql(Select, pair));
        if (pair) command.Parameters.AddWithValue("$pair", pairId!);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        return ReadRows(command);
    }

    /// <summary>One summary by its opaque run id, or null when it was never retained or has expired.</summary>
    public RunSummary? ById(string runId)
    {
        using var command = StoreSchema.Command(_connection, null, Select + "WHERE run_id=$run;");
        command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Row(reader) : null;
    }

    /// <summary>The paging query plans of the exact reads; a probe proving the sequence order and pair index are used.</summary>
    public IReadOnlyList<string> Plans() => new[]
    {
        StoreSchema.QueryPlan(_connection, "run-history-page-pair", PageSql(Select, pair: true),
            "$pair", "$limit", "$offset"),
        StoreSchema.QueryPlan(_connection, "run-history-page-all", PageSql(Select, pair: false),
            "$limit", "$offset"),
        StoreSchema.QueryPlan(_connection, "run-history-cursor-pair", CursorSql(Select, pair: true, hasBefore: true),
            "$pair", "$before", "$limit"),
        StoreSchema.QueryPlan(_connection, "run-history-cursor-all", CursorSql(Select, pair: false, hasBefore: true),
            "$before", "$limit")
    }.SelectMany(lines => lines).ToArray();

    /// <summary>Deletes summaries past the retention window and all but the newest <paramref name="maximumRows"/>.</summary>
    public void Prune(SqliteTransaction? transaction, long now, TimeSpan retention, int maximumRows)
    {
        StoreSchema.Execute(_connection, transaction,
            "DELETE FROM run_summary WHERE completed < $cutoff;",
            ("$cutoff", now - (long)retention.TotalMilliseconds));
        long? firstToDrop = null;
        using (var command = StoreSchema.Command(_connection, transaction,
            "SELECT seq FROM run_summary ORDER BY seq DESC LIMIT 1 OFFSET $keep;"))
        {
            command.Parameters.AddWithValue("$keep", maximumRows);
            firstToDrop = command.ExecuteScalar() as long?;
        }
        if (firstToDrop is { } drop)
            StoreSchema.Execute(_connection, transaction, "DELETE FROM run_summary WHERE seq <= $drop;", ("$drop", drop));
    }

    private const string Select =
        "SELECT seq,run_id,pair_id,started,completed,outcome,done,uploaded,downloaded,deleted,moved,kept_both," +
        "held,failed,uncertain,skipped,deferred FROM run_summary ";

    // The pair filter is part of the statement rather than an "IS NULL OR" branch, so the pair page always uses the
    // (pair_id, seq) index; the all-pair page scans the primary key in seq order and needs no sort.
    private static string PageSql(string select, bool pair) =>
        select + (pair ? "WHERE pair_id=$pair " : string.Empty) +
        "ORDER BY seq DESC LIMIT $limit OFFSET $offset;";

    private static string CursorSql(string select, bool pair, bool hasBefore)
    {
        var predicates = new List<string>(2);
        if (pair) predicates.Add("pair_id=$pair");
        if (hasBefore) predicates.Add("seq<$before");
        return select + (predicates.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", predicates) + " ") +
            "ORDER BY seq DESC LIMIT $limit;";
    }

    private static IReadOnlyList<RunSummary> ReadRows(SqliteCommand command)
    {
        var rows = new List<RunSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(Row(reader));
        return rows;
    }

    private static RunSummary Row(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)), reader.GetString(5),
        reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10),
        reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15),
        reader.GetInt32(16));

    private static void Validate(int limit)
    {
        if (limit is < MinimumLimit or > MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), limit,
                $"A run history page is {MinimumLimit}..{MaximumLimit} rows.");
    }
}
