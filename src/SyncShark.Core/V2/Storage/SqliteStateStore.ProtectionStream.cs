using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Only held paths are indexed. Scope paths stay in the caller's bounded batch.
    internal sealed class ProtectionStream : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;
        private readonly Func<IReadOnlyList<string>, IReadOnlyList<ProtectionMatch>> _fallback;
        private bool _failed, _hasHeld;
        internal Action? ExactReadForTest { get; set; }
        internal int SourcePasses { get; private set; }
        internal int ResultQueries { get; private set; }
        internal int MaximumInputBatch { get; private set; }

        internal ProtectionStream(SqliteConnection connection, SqliteTransaction transaction,
            long? a, long? b, ItemFlags flags, bool storageEnabled)
        {
            _connection = connection;
            _transaction = transaction;
            _fallback = paths => ReadProtectionBatch(connection, transaction, a, flags, paths, storageEnabled)
                .Zip(ReadProtectionBatch(connection, transaction, b, flags, paths, storageEnabled),
                    (left, right) => new ProtectionMatch(left.Covered || right.Covered, left.Contains || right.Contains)).ToArray();
            try
            {
                StoreSchema.Execute(connection, transaction,
                    "CREATE TEMP TABLE protection_held(p TEXT PRIMARY KEY) WITHOUT ROWID;");
                using var held = StoreSchema.Command(connection, transaction, StorageProtectionSql("""
                    INSERT OR IGNORE INTO protection_held(p)
                    SELECT CASE WHEN d.path_key='' THEN f.name_key ELSE d.path_key||'/'||f.name_key END
                    FROM file_state f INDEXED BY fs_flagged JOIN dir d ON d.dir_no=f.dir_no
                    WHERE f.root_no IN ($a,$b) AND f.flags!=0 AND (f.flags & $flags)!=0;
                    INSERT OR IGNORE INTO protection_held(p)
                    SELECT CASE WHEN d.path_key='' THEN f.name_key ELSE d.path_key||'/'||f.name_key END
                    FROM local_gap g CROSS JOIN file_state f INDEXED BY fs_obj
                      ON f.root_no=g.root_no AND f.object_id>g.after_object
                    JOIN dir d ON d.dir_no=f.dir_no
                    WHERE g.root_no IN ($a,$b) AND ($flags & 256)!=0 AND f.kind=0 AND g.discovery_done=0;
                    INSERT OR IGNORE INTO protection_held(p)
                    SELECT CASE WHEN d.path_key='' THEN f.name_key ELSE d.path_key||'/'||f.name_key END
                    FROM local_gap g CROSS JOIN local_gap_item gi ON gi.root_no=g.root_no
                    CROSS JOIN file_state f INDEXED BY fs_obj ON f.root_no=gi.root_no AND f.object_id=gi.object_id
                    JOIN dir d ON d.dir_no=f.dir_no
                    WHERE g.root_no IN ($a,$b) AND ($flags & 256)!=0 AND f.kind=0;
                    """, flags, storageEnabled));
                held.Parameters.AddWithValue("$a", (object?)a ?? DBNull.Value);
                held.Parameters.AddWithValue("$b", (object?)b ?? DBNull.Value);
                held.Parameters.AddWithValue("$flags", (long)flags);
                RunPreparation.Report("Preparing protection scope: indexing held entities");
                held.ExecuteNonQuery();
                SourcePasses = 3;
                _hasHeld = Convert.ToInt64(StoreSchema.Scalar(connection, transaction,
                    "SELECT EXISTS(SELECT 1 FROM protection_held);")) != 0;
                connection.CreateFunction<string, string>("protection_parent", path => RowCodec.Split(path).Parent, isDeterministic: true);
            }
            catch (Exception error) { Failed(error); }
        }

        internal IReadOnlyList<ProtectionMatch> Read(IReadOnlyList<string> paths)
        {
            if (paths.Count > 1024) throw new ArgumentOutOfRangeException(nameof(paths));
            MaximumInputBatch = Math.Max(MaximumInputBatch, paths.Count);
            if (_failed) return DamageBoundary(() => { ExactReadForTest?.Invoke(); return _fallback(paths); });
            if (!_hasHeld) return new ProtectionMatch[paths.Count];
            try
            {
                using var command = StoreSchema.Command(_connection, _transaction, """
                    SELECT
                      EXISTS(WITH RECURSIVE ancestors(p) AS (
                        SELECT q.value WHERE q.value!=''
                        UNION ALL SELECT protection_parent(p) FROM ancestors WHERE protection_parent(p)!=''
                      ) SELECT 1 FROM ancestors a JOIN protection_held h ON h.p=a.p),
                      CASE WHEN q.value='' THEN EXISTS(SELECT 1 FROM protection_held)
                      ELSE EXISTS(SELECT 1 FROM protection_held h WHERE h.p=q.value)
                        OR EXISTS(SELECT 1 FROM protection_held h WHERE h.p>=q.value||'/' AND h.p<q.value||'0') END
                    FROM json_each($paths) q ORDER BY CAST(q.key AS INTEGER);
                    """);
                command.Parameters.AddWithValue("$paths", System.Text.Json.JsonSerializer.Serialize(paths.Select(RowCodec.Key)));
                using var reader = command.ExecuteReader();
                ResultQueries++;
                var result = new ProtectionMatch[paths.Count];
                var n = 0;
                while (reader.Read()) result[n++] = new(reader.GetInt64(0) != 0, reader.GetInt64(1) != 0);
                return result;
            }
            catch (Exception error)
            {
                Failed(error);
                return DamageBoundary(() => { ExactReadForTest?.Invoke(); return _fallback(paths); });
            }
        }

        private void Failed(Exception error)
        {
            _failed = true;
            RunTrace.Emit($"Protection index unavailable; using exact legacy batch reads: {error.Message}");
        }

        public void Dispose() { }
    }
}
