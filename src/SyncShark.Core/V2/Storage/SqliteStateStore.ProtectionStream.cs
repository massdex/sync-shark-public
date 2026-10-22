using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // The live tables are visited once. All later path/ancestor probes hit bounded-cache,
    // file-backed TEMP indexes, never repeat joins into file_state/local_gap_item.
    internal sealed class ProtectionStream : IDisposable
    {
        private readonly SqliteCommand? _command;
        private readonly SqliteDataReader? _reader;
        internal int SourcePasses { get; private set; }
        internal int ResultQueries { get; private set; }
        internal int MaximumInputBatch { get; private set; }
        private long _read;
        private readonly long _total;

        internal ProtectionStream(SqliteConnection connection, SqliteTransaction transaction,
            long? a, long? b, ItemFlags flags, bool storageEnabled,
            IEnumerable<IReadOnlyList<string>> batches)
        {
            StoreSchema.Execute(connection, transaction, """
                CREATE TEMP TABLE protection_held(p TEXT PRIMARY KEY) WITHOUT ROWID;
                CREATE TEMP TABLE protection_scope(n INTEGER PRIMARY KEY,p TEXT NOT NULL);
                """);
            // Separate indexed flag and gap passes avoid a whole root scan when there
            // is no gap. Gap membership is entity-owned, including aliases and older
            // epoch debt, exactly like ProtectionHeldSql. Missing roots add no rows.
            using (var held = StoreSchema.Command(connection, transaction, StorageProtectionSql("""
                INSERT OR IGNORE INTO protection_held(p)
                SELECT CASE WHEN d.path_key='' THEN f.name_key ELSE d.path_key||'/'||f.name_key END
                FROM file_state f INDEXED BY fs_flagged JOIN dir d ON d.dir_no=f.dir_no
                WHERE f.root_no IN ($a,$b) AND f.flags!=0 AND (f.flags & $flags)!=0;
                INSERT OR IGNORE INTO protection_held(p)
                SELECT CASE WHEN d.path_key='' THEN f.name_key ELSE d.path_key||'/'||f.name_key END
                FROM local_gap g CROSS JOIN file_state f INDEXED BY fs_obj ON f.root_no=g.root_no
                JOIN dir d ON d.dir_no=f.dir_no
                WHERE g.root_no IN ($a,$b) AND ($flags & 256)!=0 AND f.kind=0 AND
                  ((g.discovery_done=0 AND f.object_id>g.after_object) OR EXISTS(
                    SELECT 1 FROM local_gap_item gi WHERE gi.root_no=f.root_no AND gi.object_id=f.object_id));
                """, flags, storageEnabled)))
            {
                held.Parameters.AddWithValue("$a", (object?)a ?? DBNull.Value);
                held.Parameters.AddWithValue("$b", (object?)b ?? DBNull.Value);
                held.Parameters.AddWithValue("$flags", (long)flags);
                RunActivity.Preparation("Preparing protection scope: indexing held entities");
                held.ExecuteNonQuery();
                SourcePasses = 2;
            }
            if (Convert.ToInt64(StoreSchema.Scalar(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM protection_held);")) == 0) return;
            using (var input = StoreSchema.Command(connection, transaction,
                "INSERT INTO protection_scope(n,p) SELECT $start+CAST(key AS INTEGER),value FROM json_each($paths);"))
            {
                var start = input.Parameters.Add("$start", SqliteType.Integer);
                var paths = input.Parameters.Add("$paths", SqliteType.Text);
                foreach (var batch in batches)
                {
                    // Match RehashProtection.ForPaths' deduplication and ordering exactly.
                    var keys = batch.Distinct(StringComparer.OrdinalIgnoreCase).Select(RowCodec.Key).ToArray();
                    if (keys.Length > 1024) throw new ArgumentOutOfRangeException(nameof(batches));
                    MaximumInputBatch = Math.Max(MaximumInputBatch, keys.Length);
                    start.Value = _total;
                    paths.Value = System.Text.Json.JsonSerializer.Serialize(keys);
                    input.ExecuteNonQuery();
                    _total += keys.Length;
                    if (_total % 16384 < keys.Length)
                    {
                        RunActivity.Preparation($"Preparing protection scope: staged {_total} paths");
                        RunLog.Trace($"protection scope staged: paths={_total}");
                    }
                }
            }
            // Covered means self/ancestor; Contains means self/descendant, NOT ancestor.
            // Ancestors are generated one candidate at a time; UNION ALL uses no visited
            // set. The range seeks use '/'..'0', the same segment boundary as the old SQL.
            // Root Contains intentionally has no Unicode sentinel upper bound.
            _command = StoreSchema.Command(connection, transaction, """
                SELECT q.p,
                  EXISTS(WITH RECURSIVE ancestors(p) AS (
                    SELECT q.p WHERE q.p!=''
                    UNION ALL SELECT protection_parent(p) FROM ancestors WHERE protection_parent(p)!=''
                  ) SELECT 1 FROM ancestors a JOIN protection_held h ON h.p=a.p),
                  CASE WHEN q.p='' THEN EXISTS(SELECT 1 FROM protection_held)
                  ELSE EXISTS(SELECT 1 FROM protection_held h WHERE h.p=q.p)
                    OR EXISTS(SELECT 1 FROM protection_held h WHERE h.p>=q.p||'/' AND h.p<q.p||'0') END
                FROM protection_scope q ORDER BY q.n;
                """);
            connection.CreateFunction<string, string>("protection_parent", path => RowCodec.Split(path).Parent, isDeterministic: true);
            try { _reader = _command.ExecuteReader(); ResultQueries = 1; }
            catch { _command.Dispose(); throw; }
        }

        internal IReadOnlyList<ProtectionMatch> Read(IReadOnlyList<string> paths)
        {
            if (paths.Count > 1024) throw new ArgumentOutOfRangeException(nameof(paths));
            var result = new ProtectionMatch[paths.Count];
            if (_reader is null) return result;
            for (var n = 0; n < paths.Count; n++)
            {
                if (!_reader.Read() || _reader.GetString(0) != RowCodec.Key(paths[n]))
                    throw new InvalidOperationException("Protection scope stream and consumer order differ.");
                result[n] = new(_reader.GetInt64(1) != 0, _reader.GetInt64(2) != 0);
                _read++;
                if (_read == 1 || _read % 16384 == 0 || _read == _total)
                {
                    var message = $"Preparing protection scope {_read}/{_total}";
                    RunActivity.Preparation(message);
                    RunLog.Trace(message);
                }
            }
            return result;
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _command?.Dispose();
        }
    }
}
