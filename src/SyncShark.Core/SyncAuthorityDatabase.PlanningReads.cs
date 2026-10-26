using System.Text;

namespace Massdex.SyncShark;

/// <summary>
/// The shared recovery-projection page bounds. The public surface keeps its existing maximum of 500
/// rows per page, and the internal projection accepts exactly that public maximum plus the one extra
/// sentinel row its own callers read to decide whether another page exists. The sentinel is the only
/// internal widening; it never becomes a public limit, and the Host's own public guard is unchanged.
/// </summary>
internal static class SyncAuthorityPlanningReadLimits
{
    /// <summary>The largest page a public caller may request.</summary>
    internal const int RecoveryPagePublicMaximum = 500;

    /// <summary>The one extra row the public paging sentinel reads beyond the public maximum.</summary>
    internal const int RecoveryPageSentinelMaximum = RecoveryPagePublicMaximum + 1;
}

public sealed partial class SyncAuthorityDatabase
{
    /// <summary>
    /// The exact number of rows one path-scoped retained-membership lookup may materialize. Two rows are
    /// read so a duplicate membership for one path is still visible as a duplicate and refused, and never
    /// quietly resolved to whichever row happened to be read first.
    /// </summary>
    internal const int LatestCompleteObservationAtPathMaximumRows = 2;

    /// <summary>
    /// Test-only observation of one exact retained-membership lookup: the pair, retention side, exact
    /// normalized path and the number of rows the lookup materialized. It is the bounded-materialization
    /// measurement for the resolver's before/after read; production installs no callback.
    /// </summary>
    internal static Action<string, string, string, int>? PlanningPathReadForTest { get; set; }

    /// <summary>
    /// The one exact retained-membership lookup for a single normalized path. It carries exactly the same
    /// latest-complete scan subquery, joins, selected columns and ordering as the whole-side load, and adds
    /// only the exact ordinal path predicate and the row bound, so a single path costs at most
    /// LatestCompleteObservationAtPathMaximumRows materialized rows instead of the complete inventory of
    /// that side. The predicate and the read run in one statement, so the rows are one SQLite snapshot of
    /// one scan generation.
    /// </summary>
    private const string LatestCompleteObservationAtPathSql = """
        SELECT m.relative_path_key,m.object_id,o.object_version_id,m.membership_id,
               m.root_binding_id,g.scan_generation_id,m.generation,
               v.content_hash,v.size_bytes,o.evidence_json,lo.object_kind
        FROM scan_generation g
        JOIN effective_observation o ON o.scan_generation_id=g.scan_generation_id
        JOIN membership m ON m.membership_id=o.membership_id
        JOIN object_version v ON v.object_version_id=o.object_version_id
        JOIN logical_object lo ON lo.object_id=m.object_id
        WHERE g.scan_generation_id=(
            SELECT scan_generation_id FROM scan_generation
            WHERE pair_id=$pair AND side=$side AND status='complete'
            ORDER BY generation DESC LIMIT 1)
          AND m.relative_path_key=$path COLLATE BINARY
        ORDER BY m.relative_path_key
        LIMIT 2;
        """;

    /// <summary>
    /// The retained memberships of exactly one normalized path in the latest complete generation of one
    /// retention side: zero entries, one entry, or two when the same path is bound to more than one
    /// membership. A caller that requires one complete single membership refuses the two-entry result.
    /// </summary>
    public async Task<IReadOnlyList<SyncAuthorityObservedEntry>> LoadLatestCompleteObservationAtPathAsync(
        string pairId, string side, string path, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = LatestCompleteObservationAtPathSql;
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$side", side);
        command.Parameters.AddWithValue("$path", path);
        var result = new List<SyncAuthorityObservedEntry>(LatestCompleteObservationAtPathMaximumRows);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new SyncAuthorityObservedEntry(reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.GetString(9),
                    reader.GetString(10)));
        }
        PlanningPathReadForTest?.Invoke(pairId, side, path, result.Count);
        return result;
    }

    /// <summary>
    /// The SQLite query plan of exactly the statement LoadLatestCompleteObservationAtPathAsync issues, for
    /// the indexed-path evidence. It reads no retained row and changes nothing; an empty result is reported
    /// as unavailable rather than interpreted.
    /// </summary>
    internal async Task<string> ExplainLatestCompleteObservationAtPathAsync(string pairId, string side,
        string path, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + LatestCompleteObservationAtPathSql;
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$side", side);
        command.Parameters.AddWithValue("$path", path);
        var plan = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.FieldCount < 4) continue;
            var detail = reader.GetValue(3)?.ToString();
            if (!string.IsNullOrEmpty(detail)) plan.AppendLine(detail);
        }
        return plan.ToString();
    }
}

