using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal PrepareHeader? ReadPrepare(string pairId) => InStore(() =>
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT generation, metadata_json, digest FROM prepare_header WHERE pair_id=$p;");
        command.Parameters.AddWithValue("$p", pairId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new PrepareHeader(pairId, reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    });

    private const int PrepareBatchRows = 5000;
    // Product entry points hold the exclusive process lock. This registry also protects a second
    // store opened in this process during a callback. It contains generations, never row keys.
    private static readonly object PrepareWritersGate = new();
    private static readonly HashSet<string> ActivePrepareGenerations = new(StringComparer.Ordinal);

    /// <summary>
    /// The callback runs outside the store gate. Batches are durable but unpublished until the
    /// callback returns successfully; even failure after Commit leaves the previous header intact.
    /// Writers are synchronous, single-caller sinks. Same-pair operations are serialized by callers.
    /// </summary>
    internal PrepareHeader WritePrepare(string pairId, Action<PrepareWriter> write, string? removalKey = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        var generation = Guid.NewGuid().ToString("N");
        var pairNumber = InStore(() =>
        {
            var number = RequirePair(pairId);
            lock (PrepareWritersGate) ActivePrepareGenerations.Add(generation);
            return number;
        });
        using var writer = new PrepareWriter(this, generation);
        Exception? primaryError = null;
        try
        {
            write(writer);
            var header = writer.Header(pairId);
            writer.Flush();
            InStore(() =>
            {
                using var transaction = _connection.BeginTransaction();
                // Do not resurrect a pair removed while its callback was outside the gate.
                if (StoreSchema.Scalar(_connection, transaction,
                    "SELECT pair_no FROM pair WHERE pair_id=$p;", ("$p", pairId)) is not long current || current != pairNumber)
                    throw new PreparePublicationException($"Pair {pairId} was removed during prepare.");
                writer.ValidateCounts(transaction);
                if (removalKey is null) DeletePrepare(transaction, pairId);
                else StoreSchema.Execute(_connection, transaction, "DELETE FROM removal_review WHERE entity_key=$k;", ("$k", removalKey));
                StoreSchema.Execute(_connection, transaction,
                    removalKey is null ? "INSERT INTO prepare_header(pair_id, generation, metadata_json, digest) VALUES($p,$g,$m,$d);" :
                    "INSERT INTO removal_review(pair_id,entity_key,generation,metadata_json,digest) VALUES($p,$k,$g,$m,$d);",
                    ("$k", removalKey),
                    ("$p", pairId), ("$g", generation), ("$m", header.MetadataJson), ("$d", header.Digest));
                transaction.Commit();
            });
            return header;
        }
        catch (Exception error) { primaryError = error; throw; }
        finally
        {
            // The writer has finished: releasing its in-memory registration needs no SQLite gate.
            lock (PrepareWritersGate) ActivePrepareGenerations.Remove(generation);
            // A cleanup timeout is surfaced without undoing a committed publication. Open retries
            // retained rows; no cancelled ambient run may silently strand housekeeping.
            FairStoreGate.Cleanup(() => TrySweepPrepareGenerations(), primaryError: primaryError);
        }
    }

    /// <summary>Keyset pagination; every call materializes at most 256 rows and releases its reader and lock.</summary>
    internal IReadOnlyList<PrepareRow> ReadPrepareRows(string generation, string kind, long afterOrdinal = -1,
        int limit = 256, string? digest = null) => InStore(() =>
    {
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        using var command = StoreSchema.Command(_connection, null,
            "SELECT ordinal, lookup_key, json FROM prepare_row " +
            "WHERE generation=$g AND kind=$k AND ordinal>$o " +
            "AND (EXISTS(SELECT 1 FROM prepare_header WHERE generation=$g AND digest<>'narrowing' AND ($d IS NULL OR digest=$d)) OR EXISTS(SELECT 1 FROM removal_review WHERE generation=$g AND ($d IS NULL OR digest=$d))) ORDER BY ordinal LIMIT $n;");
        command.Parameters.AddWithValue("$g", generation);
        command.Parameters.AddWithValue("$d", (object?)digest ?? DBNull.Value);
        command.Parameters.AddWithValue("$k", kind);
        command.Parameters.AddWithValue("$o", afterOrdinal);
        command.Parameters.AddWithValue("$n", limit);
        var rows = new List<PrepareRow>(limit);
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(new PrepareRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    });

    /// <summary>Indexed OrdinalIgnoreCase lookup, returning the first ordinal when a kind has duplicate keys.</summary>
    internal PrepareRow? FindPrepareRow(string generation, string kind, string key, string? digest = null) => InStore(() =>
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT ordinal, lookup_key, json FROM prepare_row " +
            "WHERE generation=$g AND kind=$k AND lookup_key=$key " +
            "AND (EXISTS(SELECT 1 FROM prepare_header WHERE generation=$g AND digest<>'narrowing' AND ($d IS NULL OR digest=$d)) OR EXISTS(SELECT 1 FROM removal_review WHERE generation=$g AND ($d IS NULL OR digest=$d))) ORDER BY ordinal LIMIT 1;");
        command.Parameters.AddWithValue("$g", generation);
        command.Parameters.AddWithValue("$d", (object?)digest ?? DBNull.Value);
        command.Parameters.AddWithValue("$k", kind);
        command.Parameters.AddWithValue("$key", PathKey.Of(key));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new PrepareRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)) : null;
    });

    internal long CountPrepareRows(string generation, string kind, string? digest = null) => InStore(() =>
        (long)StoreSchema.Scalar(_connection, null,
            "SELECT COUNT(*) FROM prepare_row WHERE generation=$g AND kind=$k " +
            "AND (EXISTS(SELECT 1 FROM prepare_header WHERE generation=$g AND digest<>'narrowing' AND ($d IS NULL OR digest=$d)) OR EXISTS(SELECT 1 FROM removal_review WHERE generation=$g AND ($d IS NULL OR digest=$d)));",
            ("$g", generation), ("$k", kind), ("$d", digest))!);

    /// <summary>A prepare cursor belongs to its root, and is not the accepted root.cursor.</summary>
    internal string? PrepareCursor(string generation, string rootId) => InStore(() =>
        StoreSchema.Scalar(_connection, null,
            "SELECT cursor FROM prepare_cursor WHERE generation=$g AND root_id=$r " +
            "AND EXISTS(SELECT 1 FROM prepare_header WHERE generation=$g AND digest<>'narrowing');",
            ("$g", generation), ("$r", rootId)) as string);

    internal string? PrepareCursorForPair(string pairId, string rootId, string? generation = null, string? digest = null) => InStore(() =>
        StoreSchema.Scalar(_connection, null,
            "SELECT c.cursor FROM prepare_header h JOIN prepare_cursor c ON c.generation=h.generation " +
            "WHERE h.pair_id=$p AND h.digest<>'narrowing' AND c.root_id=$r AND ($g IS NULL OR h.generation=$g) AND ($d IS NULL OR h.digest=$d);",
            ("$p", pairId), ("$r", rootId), ("$g", generation), ("$d", digest)) as string);

    internal string? ReadPrepareCursor(string pairId, string rootId) => PrepareCursorForPair(pairId, rootId);

    /// <summary>Rewrites the published prepare's small metadata (reviewed rule scope and digest) in place.</summary>
    internal void UpdatePrepareMetadata(string pairId, string generation, string metadataJson, string digest) => InStore(() =>
    {
        StoreSchema.Execute(_connection, null,
            "UPDATE prepare_header SET metadata_json=$m,digest=$d " +
            "WHERE pair_id=$p AND generation=$g AND digest<>$narrowing;",
            ("$m", metadataJson), ("$d", digest), ("$p", pairId), ("$g", generation),
            ("$narrowing", NarrowingDigest));
        if (StoreSchema.Changes(_connection, null) != 1)
            throw new PreparePublicationException("The retained first-sync review changed or disappeared; run prepare again.");
    });

    /// <summary>Updates the published prepare's cursor without reimporting its rows.</summary>
    internal void SetPrepareCursor(string pairId, string rootId, string? cursor, string? generation = null, string? digest = null) => InStore(() =>
    {
        ArgumentException.ThrowIfNullOrEmpty(rootId);
        StoreSchema.Execute(_connection, null,
            "INSERT INTO prepare_cursor(generation,root_id,cursor) " +
            "SELECT generation,$r,$c FROM prepare_header WHERE pair_id=$p AND digest<>'narrowing' AND ($g IS NULL OR generation=$g) AND ($d IS NULL OR digest=$d) " +
            "ON CONFLICT(generation,root_id) DO UPDATE SET cursor=excluded.cursor;",
            ("$p", pairId), ("$r", rootId), ("$c", cursor), ("$g", generation), ("$d", digest));
    });

    internal void ClearPrepare(string pairId)
    {
        InStore(() =>
        {
            using var transaction = _connection.BeginTransaction();
            DeletePrepare(transaction, pairId);
            transaction.Commit();
        });
        TrySweepPrepareGenerations();
    }

    // Retire only. In particular, pair removal calls this inside its archive transaction.
    private void DeletePrepare(SqliteTransaction transaction, string pairId) =>
        StoreSchema.Execute(_connection, transaction,
            "DELETE FROM prepare_header WHERE pair_id=$p;", ("$p", pairId));

    private void TrySweepPrepareGenerations(bool propagateDamage = false)
    {
        try
        {
            if (propagateDamage) SweepOrphanedRemovalReviews();
            SweepPrepareGenerations();
        }
        catch (SqliteException) { } // Transient housekeeping failure must not block readable state.
        // Open still fails closed on corruption. Post-write cleanup must not mask the writer's
        // original exception or make an already committed publication appear to have failed.
        catch (StateDamagedException) when (!propagateDamage) { }
        catch (ObjectDisposedException) { } // A concurrent close leaves cleanup for Open.
    }

    private void SweepPrepareGenerations()
    {
        // Keyset walk uses the generation-leading indexes and retains only one generation id.
        // No reader or store gate spans a batch boundary.
        foreach (var table in new[] { "prepare_row", "prepare_cursor" })
        {
            var after = "";
            while (true)
            {
                var generation = InStore(() => StoreSchema.Scalar(_connection, null,
                    $"SELECT generation FROM {table} WHERE generation>$after ORDER BY generation LIMIT 1;",
                    ("$after", after)) as string);
                if (generation is null) break;
                after = generation;
                while (InStore(() =>
                {
                    lock (PrepareWritersGate)
                        if (ActivePrepareGenerations.Contains(generation)) return 0;
                    using var transaction = _connection.BeginTransaction();
                    if (StoreSchema.Scalar(_connection, transaction,
                        "SELECT 1 FROM prepare_header WHERE generation=$g UNION ALL SELECT 1 FROM removal_review WHERE generation=$g;", ("$g", generation)) is not null)
                        return 0;
                    // prepare_cursor is WITHOUT ROWID; its root key supplies the bounded delete.
                    var key = table == "prepare_row" ? "rowid" : "root_id";
                    StoreSchema.Execute(_connection, transaction,
                        $"DELETE FROM {table} WHERE generation=$g AND {key} IN " +
                        $"(SELECT {key} FROM {table} WHERE generation=$g LIMIT {PrepareBatchRows});",
                        ("$g", generation));
                    var changed = StoreSchema.Changes(_connection, transaction);
                    transaction.Commit();
                    return changed;
                }) > 0) { }
            }
        }
    }

    /// <summary>Bounded streaming sink. Only a batch transaction holds the store gate.</summary>
    internal sealed class PrepareWriter : IDisposable
    {
        private readonly SqliteStateStore _store;
        private readonly string _generation;
        private readonly List<(string Kind, long Ordinal, string Key, string Json)> _pending = new(PrepareBatchRows);
        private readonly HashSet<(string Kind, string Key)> _pendingKeys = new();
        private readonly Dictionary<string, long> _writtenCounts = new(StringComparer.Ordinal);

        internal string Generation => _generation;
        private string? _metadataJson;
        private string? _digest;
        private bool _disposed;

        internal PrepareWriter(SqliteStateStore store, string generation)
        {
            _store = store;
            _generation = generation;
        }

        // Direct storage callers get exactly one JSON syntax validation.
        internal void Add(string kind, long ordinal, string key, string json) =>
            AddRow(kind, ordinal, key, json, unique: false, validated: false);

        /// <summary>First key wins across batches; skipped ordinals are fine for keyset pagination.</summary>
        internal bool AddUnique(string kind, long ordinal, string key, string json) =>
            AddRow(kind, ordinal, key, json, unique: true, validated: false);

        /// <summary>For importers/serializers that have already validated the JSON payload.</summary>
        internal void AddValidated(string kind, long ordinal, string key, string json) =>
            AddRow(kind, ordinal, key, json, unique: false, validated: true);

        internal bool AddUniqueValidated(string kind, long ordinal, string key, string json) =>
            AddRow(kind, ordinal, key, json, unique: true, validated: true);

        private bool AddRow(string kind, long ordinal, string key, string json, bool unique, bool validated)
        {
            RequireWritable();
            ArgumentException.ThrowIfNullOrEmpty(kind);
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(json);
            if (ordinal < 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
            if (!validated) { using (JsonDocument.Parse(json)) { } }
            var lookupKey = PathKey.Of(key);
            if (unique && (_pendingKeys.Contains((kind, lookupKey)) || _store.InStore(() =>
                StoreSchema.Scalar(_store._connection, null,
                    "SELECT 1 FROM prepare_row WHERE generation=$g AND kind=$k AND lookup_key=$key LIMIT 1;",
                    ("$g", _generation), ("$k", kind), ("$key", lookupKey)) is not null)))
                return false;
            _pending.Add((kind, ordinal, lookupKey, json));
            _pendingKeys.Add((kind, lookupKey));
            if (_pending.Count >= PrepareBatchRows) Flush();
            return true;
        }

        internal void Flush()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending.Count == 0) return;
            _store.InStore(() =>
            {
                using var transaction = _store._connection.BeginTransaction();
                using var command = StoreSchema.Command(_store._connection, transaction,
                    "INSERT INTO prepare_row(generation,kind,ordinal,lookup_key,json) VALUES($g,$k,$o,$key,$json);");
                command.Parameters.AddWithValue("$g", _generation);
                command.Parameters.Add("$k", SqliteType.Text);
                command.Parameters.Add("$o", SqliteType.Integer);
                command.Parameters.Add("$key", SqliteType.Text);
                command.Parameters.Add("$json", SqliteType.Text);
                command.Prepare();
                foreach (var row in _pending)
                {
                    command.Parameters["$k"].Value = row.Kind;
                    command.Parameters["$o"].Value = row.Ordinal;
                    command.Parameters["$key"].Value = row.Key;
                    command.Parameters["$json"].Value = row.Json;
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            });
            foreach (var row in _pending)
                _writtenCounts[row.Kind] = _writtenCounts.GetValueOrDefault(row.Kind) + 1;
            _pending.Clear();
            _pendingKeys.Clear();
        }

        // Compare durable rows, including unexpected kinds, under the publication transaction.
        // Counters track successful batches, not ordinals or duplicate AddUnique attempts.
        internal void ValidateCounts(SqliteTransaction transaction)
        {
            var remaining = new Dictionary<string, long>(_writtenCounts, StringComparer.Ordinal);
            using var command = StoreSchema.Command(_store._connection, transaction,
                "SELECT kind, COUNT(*) FROM prepare_row WHERE generation=$g GROUP BY kind;");
            command.Parameters.AddWithValue("$g", _generation);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var kind = reader.GetString(0);
                var actual = reader.GetInt64(1);
                remaining.Remove(kind, out var expected);
                if (actual != expected) throw CountMismatch(kind, expected, actual);
            }
            foreach (var entry in remaining)
                throw CountMismatch(entry.Key, entry.Value, 0);
        }

        private static PreparePublicationException CountMismatch(string kind, long expected, long actual) =>
            new($"Prepare row count mismatch for '{kind}': expected {expected}, found {actual}.");

        internal void SetCursor(string rootId, string? cursor)
        {
            RequireWritable();
            ArgumentException.ThrowIfNullOrEmpty(rootId);
            _store.InStore(() => StoreSchema.Execute(_store._connection, null,
                "INSERT INTO prepare_cursor(generation,root_id,cursor) VALUES($g,$r,$c) " +
                "ON CONFLICT(generation,root_id) DO UPDATE SET cursor=excluded.cursor;",
                ("$g", _generation), ("$r", rootId), ("$c", cursor)));
        }

        /// <summary>Seals the sink; does not commit until the enclosing callback has returned successfully.</summary>
        internal void Commit(string metadataJson, string digest)
        {
            RequireWritable();
            ArgumentNullException.ThrowIfNull(digest);
            using (JsonDocument.Parse(metadataJson)) { }
            _metadataJson = metadataJson;
            _digest = digest;
        }

        internal PrepareHeader Header(string pairId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_metadataJson is null || _digest is null)
                throw new InvalidOperationException("Prepare writer must call Commit before returning.");
            return new PrepareHeader(pairId, _generation, _metadataJson, _digest);
        }

        private void RequireWritable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_metadataJson is not null) throw new InvalidOperationException("Prepare writer is already sealed.");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
            _pendingKeys.Clear();
        }
    }
}
