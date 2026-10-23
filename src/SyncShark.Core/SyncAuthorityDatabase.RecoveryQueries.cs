using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

/// <summary>
/// SS-D11 exact recovery projection and pair reads, plus the bounded legacy-history import batch
/// preparation (source only).
///
/// Before these reads the recovery surfaces hydrated EVERY retained pair definition and EVERY retained
/// recovery artifact and then filtered in memory: one usage request, one catalog request and one exact
/// artifact-consuming request each deserialized and revalidated every retained pair of the database and
/// projected every recovery artifact of every pair. The exact reads below repeat the SAME predicates, the
/// SAME projection expressions, the SAME original-path fallback (affected/source/member priority, then the
/// identity JSON original path, then the unknown marker) and the SAME retained ordering as the existing
/// global readers, and add only the exact ordinal pair - and, when the caller asks for it, the exact
/// artifact and the requested side - so a caller that already knows its exact pair or artifact reads exactly
/// the rows it can use and never hydrates an unrelated pair's definition or artifact.
///
/// No schema, index, column, table, provider, credential, signing or lifecycle change is made here. The
/// existing public global readers stay available to their real all-pair consumers (pair choices and the
/// pair-set export), released rows stay readable for the exact consumers whose logic inspects the status,
/// the catalog keeps its released-row filter, and no later root, namespace, side, ownership, containment or
/// existence predicate of any consumer is weakened by this file.
///
/// The same file carries the bounded legacy-history import preparation, because both changes target the one
/// confirmed amplification: a repeated unchanged history import used to open one write transaction per
/// normalized row. Batch preparation, the pre-transaction exact identity read through one existing
/// connection, and the atomic per-batch write that replaces it live here; ImportLegacyHistoryAsync keeps its
/// exact public contract - normalized path, hashed source JSON, SHA-derived decision identity, decision
/// kind, detail JSON and the retained collision-equality refusal.
/// </summary>
public sealed partial class SyncAuthorityDatabase
{
    /// <summary>The largest number of normalized legacy-history records one import batch writes.</summary>
    internal const int MaximumLegacyHistoryBatchRecords = 200;

    /// <summary>
    /// The largest serialized legacy-history payload one import batch accumulates where the input permits it.
    /// One individual record that already exceeds this bound is retained whole in a batch of its own, so a
    /// permitted oversized record is never truncated and never dropped to satisfy the bound.
    /// </summary>
    internal const int MaximumLegacyHistoryBatchPayloadBytes = 1 << 20;

    /// <summary>The exact retained pair definition read, so its real query plan can be measured.</summary>
    internal const string PairDefinitionSql = """
        SELECT pair_id,configuration_revision,pair_json
        FROM sync_pair
        WHERE pair_id=$pair AND pair_json IS NOT NULL AND ($includeRetired=1 OR state<>'retired')
        LIMIT 1;
        """;

    /// <summary>The exact pair-and-artifact read, so its real query plan can be measured.</summary>
    internal const string ExactRecoveryArtifactSql = """
        SELECT ra.recovery_artifact_id,ra.operation_id,o.pair_id,ra.object_id,ra.side,
               coalesce((SELECT expected_path_key FROM operation_member m
                         WHERE m.operation_id=ra.operation_id AND expected_path_key IS NOT NULL
                         ORDER BY CASE role WHEN 'affected' THEN 0 WHEN 'source' THEN 1 ELSE 2 END
                         LIMIT 1),json_extract(ra.identity_json,'$.originalPath'),'<unknown>'),
               ra.identity_json,ra.content_hash,ra.size_bytes,ra.status,ra.retained_utc
        FROM recovery_artifact ra
        JOIN operation o ON o.operation_id=ra.operation_id
        WHERE o.pair_id=$pair AND ra.recovery_artifact_id=$artifact
          AND ($side IS NULL OR ra.side=$side)
        LIMIT 1;
        """;

    /// <summary>The pair-scoped retained-artifact read, so its real query plan can be measured.</summary>
    internal const string PairRecoveryArtifactSql = """
        SELECT ra.recovery_artifact_id,ra.operation_id,o.pair_id,ra.object_id,ra.side,
               coalesce((SELECT expected_path_key FROM operation_member m
                         WHERE m.operation_id=ra.operation_id AND expected_path_key IS NOT NULL
                         ORDER BY CASE role WHEN 'affected' THEN 0 WHEN 'source' THEN 1 ELSE 2 END
                         LIMIT 1),json_extract(ra.identity_json,'$.originalPath'),'<unknown>'),
               ra.identity_json,ra.content_hash,ra.size_bytes,ra.status,ra.retained_utc
        FROM recovery_artifact ra
        JOIN operation o ON o.operation_id=ra.operation_id
        WHERE o.pair_id=$pair AND ra.status<>'released'
        ORDER BY ra.retained_utc DESC,ra.recovery_artifact_id;
        """;

    /// <summary>The exact usage aggregate, so its real query plan can be measured.</summary>
    internal const string RecoveryUsageAggregateSql = """
        SELECT count(*),
               coalesce(sum(ra.size_bytes),0),
               count(ra.size_bytes),
               min(ra.retained_utc),
               coalesce(sum(CASE WHEN ra.retained_utc GLOB
                   '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]Z'
                   THEN 1 ELSE 0 END),0),
               coalesce(sum(CASE WHEN $cutoff IS NOT NULL AND ra.retained_utc <= $cutoff
                                 THEN 1 ELSE 0 END),0)
        FROM recovery_artifact ra
        JOIN operation o ON o.operation_id=ra.operation_id
        WHERE o.pair_id=$pair AND ra.status IN ('retained','verified','release_authorized');
        """;

    // ---- exact pair read -------------------------------------------------------------------------

    /// <summary>
    /// The single retained pair definition of one exact ordinal identity. It carries exactly the existing
    /// global reader's predicate (a published definition, and the same retired filter) and its exact
    /// normalization and authority checks, including the exception messages, so a caller that already knows
    /// the pair id reads one row instead of every definition. A missing definition returns null; a retained
    /// row that is empty, or that does not match its own authority identity or revision, still fails and is
    /// never returned as a usable pair.
    /// </summary>
    public async Task<SyncPair?> LoadPairDefinitionAsync(string pairId,
        CancellationToken cancellationToken = default, bool includeRetired = true)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = PairDefinitionSql;
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$includeRetired", includeRetired ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var pair = JsonSerializer.Deserialize<SyncPair>(reader.GetString(2))
                   ?? throw new InvalidDataException("A retained SQLite pair definition is empty.");
        pair = SyncPairStore.NormalizePair(pair);
        if (!string.Equals(pair.Id, reader.GetString(0), StringComparison.Ordinal) ||
            pair.PolicyRevision != reader.GetInt64(1))
            throw new InvalidDataException(
                "A retained SQLite pair definition does not match its authority identity or revision.");
        return pair;
    }

    // ---- exact recovery artifact reads ------------------------------------------------------------

    /// <summary>
    /// One exact retained recovery artifact of one exact pair, optionally bound to one exact side. Both the
    /// pair and the artifact are bound in the statement, so a zero result means exactly that this pair retains
    /// no such artifact - an artifact of another pair, a missing artifact and a requested-side disagreement are
    /// all null, and no released-row filter is applied because an exact consumer still inspects the status of
    /// the row it names (a removed copy reports itself as already removed).
    /// </summary>
    public async Task<SyncRecoveryArtifactProjection?> LoadRecoveryArtifactAsync(string pairId,
        string artifactId, string? side = null, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        RequireIdentifier(artifactId, nameof(artifactId));
        if (side is not (null or "local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ExactRecoveryArtifactSql;
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$artifact", artifactId);
        command.Parameters.AddWithValue("$side", (object?)side ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? RecoveryArtifactProjection(reader) : null;
    }

    /// <summary>
    /// Every retained recovery artifact of one exact pair, in the existing global reader's retained order and
    /// with its exact projection and its existing released-row filter for the pair catalog. It replaces the
    /// global projection followed by an in-memory pair filter; it never reads another pair's artifacts and
    /// never deserializes another pair's definition.
    /// </summary>
    public async Task<IReadOnlyList<SyncRecoveryArtifactProjection>> LoadRecoveryArtifactsForPairAsync(
        string pairId, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = PairRecoveryArtifactSql;
        command.Parameters.AddWithValue("$pair", pairId);
        var result = new List<SyncRecoveryArtifactProjection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(RecoveryArtifactProjection(reader));
        return result;
    }

    /// <summary>
    /// The ONE retained-artifact construction the exact readers share with the existing global readers: the
    /// same eleven columns, read in the same order and parsed with the same invariant timestamp rule, so an
    /// exact read and the global read of the same retained row are the same value.
    /// </summary>
    private static SyncRecoveryArtifactProjection RecoveryArtifactProjection(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetInt64(8), reader.GetString(9),
            DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture));

    // ---- exact parity usage aggregate -------------------------------------------------------------

    /// <summary>
    /// The exact retained-copy aggregate of one pair in ONE statement: the retained-copy count, the sum of
    /// their recorded sizes, the oldest retained instant and the count already eligible for cleanup at the
    /// caller's single captured instant. The status set (retained, verified, release_authorized), the size
    /// semantics and the retention comparison are exactly the ones the usage surface applied to the global
    /// projection, and no recovery artifact of another pair and no unrelated pair definition is read.
    ///
    /// Eligibility is the plain textual comparison the canonical retained spelling permits, so it stays an
    /// indexable predicate instead of a per-row julianday() conversion whose floating point result could not
    /// preserve the retained instant's parse precision. That substitution is only sound while every matching
    /// retained_utc really is the canonical fixed-width UTC spelling both writers produce, so the same
    /// statement also counts the rows whose spelling is NOT that canonical form and the rows whose size is
    /// absent; the caller refuses the whole aggregate when either count differs from the row count. An empty
    /// pair is a genuine zero count and size with no oldest instant, while invalid retained authority - a
    /// non-canonical timestamp, an absent size, or an integer overflow in the size sum - is a failure and
    /// can never degrade into a silent zero.
    /// </summary>
    public async Task<SyncRecoveryUsageAggregate> LoadRecoveryUsageAggregateAsync(string pairId,
        DateTimeOffset? expiryCutoffUtc, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RecoveryUsageAggregateSql;
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$cutoff",
            expiryCutoffUtc is { } cutoff ? Utc(cutoff) : (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("The retained recovery usage aggregate returned no row.");
        var itemCount = reader.GetInt64(0);
        var size = reader.GetInt64(1);
        var sizedCount = reader.GetInt64(2);
        var oldest = reader.IsDBNull(3) ? null : reader.GetString(3);
        var canonicalCount = reader.GetInt64(4);
        var eligibleCount = reader.GetInt64(5);
        if (sizedCount != itemCount || canonicalCount != itemCount)
            throw new InvalidDataException(
                "The retained recovery usage aggregate encountered invalid retained retention authority; no zero was substituted.");
        return new SyncRecoveryUsageAggregate(itemCount, size,
            oldest is null ? null : DateTimeOffset.Parse(oldest, CultureInfo.InvariantCulture), eligibleCount);
    }

    // ---- bounded legacy-history import preparation ------------------------------------------------

    /// <summary>
    /// One normalized legacy-history record prepared exactly as the per-row import always prepared it: the
    /// same normalized path, the same hashed source JSON, the same SHA-derived decision identity, the same
    /// decision kind, the same decided instant and the same detail JSON. PayloadBytes is the serialized size
    /// the record writes (its own identity, pair, kind and detail JSON as UTF-8), used only for batching.
    /// </summary>
    internal sealed record LegacyHistoryImportRecord(
        string DecisionId,
        string PairId,
        string DecisionKind,
        string DecisionJson,
        string DecidedUtc,
        int PayloadBytes)
    {
        /// <summary>
        /// The retained-row comparison contract used when the same prepared identity appears twice: exactly
        /// the three fields the retained receipt rechecks, compared ordinally. PayloadBytes is batching-only
        /// and is deliberately excluded, and the collision refusal is unchanged.
        /// </summary>
        internal bool Matches(LegacyHistoryImportRecord record) =>
            string.Equals(PairId, record.PairId, StringComparison.Ordinal) &&
            string.Equals(DecisionKind, record.DecisionKind, StringComparison.Ordinal) &&
            string.Equals(DecisionJson, record.DecisionJson, StringComparison.Ordinal);
    }

    /// <summary>One retained reconciliation decision read back by exact identity.</summary>
    internal sealed record RetainedLegacyDecision(string DecisionId, string PairId, string DecisionKind,
        string DecisionJson)
    {
        /// <summary>
        /// The existing exact-equality contract of a retained legacy-history row, compared ordinally in
        /// exactly the fields the retained receipt rechecks: the pair, the decision kind and the detail JSON.
        /// The decision identity is the lookup key and the decided instant is derived from the same source
        /// entry, so both are equal by construction for a matching row.
        /// </summary>
        internal bool Matches(LegacyHistoryImportRecord record) =>
            string.Equals(PairId, record.PairId, StringComparison.Ordinal) &&
            string.Equals(DecisionKind, record.DecisionKind, StringComparison.Ordinal) &&
            string.Equals(DecisionJson, record.DecisionJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// Normalizes every entry in occurrence order exactly as the per-row import did, so the same source
    /// JSON, the same hashed decision identity, the same decision kind, the same decided instant and the
    /// same detail JSON are derived for every entry of the first and of any changed input. No entry is
    /// skipped, cached or remembered from an earlier import.
    /// </summary>
    internal static List<LegacyHistoryImportRecord> PrepareLegacyHistoryImport(SyncHistory history)
    {
        var prepared = new List<LegacyHistoryImportRecord>(history.Entries.Count);
        foreach (var entry in history.Entries.OrderBy(item => item.OccurredUtc))
        {
            RequireIdentifier(entry.PairId, nameof(entry.PairId));
            var path = entry.RelativePath == "<run>"
                ? entry.RelativePath
                : PilotPath.NormalizeRelative(entry.RelativePath);
            var sourceJson = JsonSerializer.Serialize(entry with { RelativePath = path });
            var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceJson)))
                .ToLowerInvariant();
            var decisionId = "legacy-history-" + sourceHash[..20];
            var decisionKind = entry.Outcome switch
            {
                SyncDecisionKind.Baseline => "adopted",
                SyncDecisionKind.Identical => "unchanged",
                SyncDecisionKind.Skipped => "skipped",
                SyncDecisionKind.Uploaded or SyncDecisionKind.Downloaded or SyncDecisionKind.Recovered =>
                    "operation",
                SyncDecisionKind.Conflict => "conflict",
                SyncDecisionKind.Archived => "tombstone",
                _ => "blocked"
            };
            var detail = JsonSerializer.Serialize(new
            {
                path,
                reason = entry.Reason,
                legacyOutcome = entry.Outcome.ToString(),
                migrated = true
            });
            prepared.Add(new LegacyHistoryImportRecord(decisionId, entry.PairId, decisionKind, detail,
                Utc(entry.OccurredUtc),
                Encoding.UTF8.GetByteCount(decisionId) + Encoding.UTF8.GetByteCount(entry.PairId) +
                Encoding.UTF8.GetByteCount(decisionKind) + Encoding.UTF8.GetByteCount(detail)));
        }
        return prepared;
    }

    /// <summary>
    /// Groups prepared records, in occurrence order, into batches of at most 200 records and at most the
    /// bounded serialized payload where the input permits it. A record that already exceeds the payload
    /// bound on its own is placed alone in its own batch and retained whole; it is never truncated, split or
    /// dropped, and it never absorbs or is absorbed by another record.
    /// </summary>
    internal static List<List<LegacyHistoryImportRecord>> BuildLegacyHistoryImportBatches(
        List<LegacyHistoryImportRecord> prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var batches = new List<List<LegacyHistoryImportRecord>>();
        var current = new List<LegacyHistoryImportRecord>();
        var payload = 0;
        foreach (var record in prepared)
        {
            if (record.PayloadBytes > MaximumLegacyHistoryBatchPayloadBytes)
            {
                if (current.Count > 0)
                {
                    batches.Add(current);
                    current = [];
                    payload = 0;
                }
                batches.Add([record]);
                continue;
            }
            if (current.Count > 0 && (current.Count == MaximumLegacyHistoryBatchRecords ||
                                      payload + record.PayloadBytes > MaximumLegacyHistoryBatchPayloadBytes))
            {
                batches.Add(current);
                current = [];
                payload = 0;
            }
            current.Add(record);
            payload += record.PayloadBytes;
        }
        if (current.Count > 0) batches.Add(current);
        return batches;
    }

    /// <summary>
    /// Imports one prepared batch. Exactly-equal duplicate identities inside the batch collapse to the one
    /// retained row the original ordered loop would have written; two records that share an identity but
    /// differ in any existing proof field are refused with the retained collision refusal, exactly as the
    /// original second write was. The exact decision identities are then read back on ONE existing
    /// connection through one bounded parameterized IN lookup on the primary-key index: a positively exact
    /// retained row needs no write at all, and an unchanged repeated import therefore completes from reads
    /// with no writer transaction. Only the genuinely missing rows enter one write transaction, which
    /// rechecks each written row against the same exact-equality predicate inside that transaction so a race
    /// or a collision can never be hidden; a cancellation or failure rolls back only that in-flight batch and
    /// leaves every earlier committed batch retained.
    /// </summary>
    internal async Task ImportLegacyHistoryBatchAsync(List<LegacyHistoryImportRecord> batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0) return;
        var unique = new List<LegacyHistoryImportRecord>(batch.Count);
        var byIdentity = new Dictionary<string, LegacyHistoryImportRecord>(StringComparer.Ordinal);
        foreach (var record in batch)
        {
            if (byIdentity.TryGetValue(record.DecisionId, out var duplicate))
            {
                if (!duplicate.Matches(record))
                    throw new InvalidOperationException(
                        "The retained legacy-history migration row does not match.");
                continue;
            }
            byIdentity.Add(record.DecisionId, record);
            unique.Add(record);
        }
        var missing = new List<LegacyHistoryImportRecord>(unique.Count);
        {
            // The read connection is closed before the writer is taken, so a batch that is already retained
            // holds nothing at all while it proves itself complete.
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var retained = await ReadRetainedLegacyDecisionsAsync(connection, unique, cancellationToken);
            foreach (var record in unique)
            {
                if (retained.TryGetValue(record.DecisionId, out var row))
                {
                    if (!row.Matches(record))
                        throw new InvalidOperationException(
                            "The retained legacy-history migration row does not match.");
                    continue;
                }
                missing.Add(record);
            }
        }
        if (missing.Count == 0) return;
        await WriteAsync(async (connection, transaction) =>
        {
            foreach (var record in missing)
            {
                await ExecuteAsync(connection, transaction,
                    """
                    INSERT OR IGNORE INTO reconciliation_decision(
                        reconciliation_decision_id,pair_id,decision_kind,decision_json,decided_utc)
                    VALUES($id,$pair,$kind,$json,$utc);
                    """, cancellationToken, ("$id", record.DecisionId), ("$pair", record.PairId),
                    ("$kind", record.DecisionKind), ("$json", record.DecisionJson),
                    ("$utc", record.DecidedUtc));
                var exact = await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM reconciliation_decision WHERE reconciliation_decision_id=$id AND pair_id=$pair AND decision_kind=$kind AND decision_json=$json;",
                    cancellationToken, ("$id", record.DecisionId), ("$pair", record.PairId),
                    ("$kind", record.DecisionKind), ("$json", record.DecisionJson));
                if (exact != 1)
                    throw new InvalidOperationException(
                        "The retained legacy-history migration row does not match.");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// One bounded parameterized IN lookup on the retained-decision primary key for exactly the identities of
    /// one batch, on the caller's existing connection. An index lookup per identity replaces both the
    /// per-row transaction of the original import and any whole-table decision read.
    /// </summary>
    private static async Task<Dictionary<string, RetainedLegacyDecision>> ReadRetainedLegacyDecisionsAsync(
        SqliteConnection connection, List<LegacyHistoryImportRecord> records,
        CancellationToken cancellationToken)
    {
        var retained = new Dictionary<string, RetainedLegacyDecision>(records.Count, StringComparer.Ordinal);
        if (records.Count == 0) return retained;
        await using var command = connection.CreateCommand();
        var names = new string[records.Count];
        for (var index = 0; index < records.Count; index++)
        {
            names[index] = "$decision" + index.ToString(CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue(names[index], records[index].DecisionId);
        }
        command.CommandText =
            "SELECT reconciliation_decision_id,pair_id,decision_kind,decision_json " +
            "FROM reconciliation_decision WHERE reconciliation_decision_id IN (" +
            string.Join(",", names) + ");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var decisionId = reader.GetString(0);
            retained[decisionId] = new RetainedLegacyDecision(decisionId, reader.GetString(1),
                reader.GetString(2), reader.GetString(3));
        }
        return retained;
    }
}

/// <summary>
/// The exact retained-copy aggregate of one pair: the retained-copy count, the sum of their recorded sizes,
/// the oldest retained instant and the count already eligible for cleanup at one captured instant. It is the
/// one-statement projection the usage surface reads, so no other pair's recovery artifact and no unrelated
/// pair definition is hydrated to answer it.
/// </summary>
public sealed record SyncRecoveryUsageAggregate(
    long ItemCount,
    long Size,
    DateTimeOffset? OldestRetainedUtc,
    long CleanupEligibleCount);

