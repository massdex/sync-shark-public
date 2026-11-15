using System.Collections;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // These are observations, not deletion authority. A published header means every row is complete
    // at this cursor. Invalidate it before bounded batches; a crash never publishes partial absence.
    public DeletionClassification? ReadDeletionClassification(RootDef root) => InStore(() =>
    {
        var number = RootNo(root.RootId);
        if (number is null) return null;
        var cursor = StoreSchema.Scalar(_connection, null,
            "SELECT basis_cursor FROM deletion_classification WHERE root_no=$r AND root_binding=$binding;",
            ("$r", number.Value), ("$binding", JsonSerializer.Serialize(root))) as string;
        return cursor is null ? null : new DeletionClassification(cursor,
            new ClassificationPaths(this, number.Value, cursor, true),
            new ClassificationPaths(this, number.Value, cursor, false));
    });

    public void ReplaceDeletionClassification(RootDef root, string cursor, IReadOnlySet<string> exact,
        IReadOnlySet<string> ancestors)
    {
        var number = BeginClassificationWrite(root);
        // Bounded deletes also keep the store gate and SQLite transaction short at 1M paths.
        while (InStore(() =>
        {
            StoreSchema.Execute(_connection, null,
                "DELETE FROM deletion_classification_path WHERE root_no=$r AND path_key IN " +
                "(SELECT path_key FROM deletion_classification_path WHERE root_no=$r LIMIT 256);", ("$r", number));
            return StoreSchema.Changes(_connection, null!);
        }) != 0) { }
        WriteClassificationRows(number, ancestors.Select(path => (Path: path, Deleted: exact.Contains(path))), replacing: true);
        PublishClassification(root, number, cursor);
    }

    public void ReplayDeletionClassification(RootDef root, string cursor,
        IEnumerable<(string Path, bool Deleted)> changes)
    {
        var number = BeginClassificationWrite(root);
        WriteClassificationRows(number, changes, replacing: false);
        PublishClassification(root, number, cursor);
    }

    private long BeginClassificationWrite(RootDef root) => InStore(() =>
    {
        var number = RootNo(root.RootId) ?? throw new InvalidOperationException("Unknown classification root.");
        StoreSchema.Execute(_connection, null, "DELETE FROM deletion_classification WHERE root_no=$r;", ("$r", number));
        return number;
    });

    private void WriteClassificationRows(long number, IEnumerable<(string Path, bool Deleted)> rows, bool replacing)
    {
        foreach (var batch in rows.Chunk(256)) InStore(() =>
        {
            using var transaction = _connection.BeginTransaction();
            using var put = StoreSchema.Command(_connection, transaction,
                "INSERT INTO deletion_classification_path(root_no,path_key,path,exact) VALUES($r,$key,$path,$exact) " +
                "ON CONFLICT(root_no,path_key) DO UPDATE SET path=excluded.path,exact=excluded.exact;");
            put.Parameters.AddWithValue("$r", number);
            var key = put.Parameters.AddWithValue("$key", "");
            var path = put.Parameters.AddWithValue("$path", "");
            var exact = put.Parameters.AddWithValue("$exact", 0);
            using var live = StoreSchema.Command(_connection, transaction,
                "UPDATE deletion_classification_path SET exact=0 WHERE root_no=$r AND path_key=$key;");
            live.Parameters.AddWithValue("$r", number);
            var liveKey = live.Parameters.AddWithValue("$key", "");
            foreach (var row in batch)
            {
                if (replacing || row.Deleted)
                {
                    key.Value = RowCodec.Key(row.Path); path.Value = row.Path; exact.Value = row.Deleted ? 1 : 0;
                    put.ExecuteNonQuery();
                }
                else { liveKey.Value = RowCodec.Key(row.Path); live.ExecuteNonQuery(); }
            }
            transaction.Commit();
        });
    }

    private void PublishClassification(RootDef root, long number, string cursor) => InStore(() =>
        StoreSchema.Execute(_connection, null,
            "INSERT INTO deletion_classification(root_no,root_binding,basis_cursor) VALUES($r,$binding,$cursor);",
            ("$r", number), ("$binding", JsonSerializer.Serialize(root)), ("$cursor", cursor)));

    // A query-backed set: no persisted tombstone array, hash set or per-candidate Task on cursor resume.
    private sealed class ClassificationPaths(SqliteStateStore store, long root, string cursor, bool exact) : IReadOnlySet<string>
    {
        private readonly string _filter = exact ? " AND exact=1" : "";
        private int? _count;
        public int Count => _count ??= store.InStore(() =>
        {
            EnsureCurrent();
            return checked((int)(long)StoreSchema.Scalar(store._connection, null,
                "SELECT COUNT(*) FROM deletion_classification_path WHERE root_no=$r" + _filter, ("$r", root))!);
        });
        private void EnsureCurrent()
        {
            var current = StoreSchema.Scalar(store._connection, null,
                "SELECT basis_cursor FROM deletion_classification WHERE root_no=$r;", ("$r", root)) as string;
            if (current != cursor) throw new IOException("Deletion classification changed during its read; retry observation.");
        }
        public bool Contains(string item)
        {
            if (Count == 0) return false;
            return store.InStore(() =>
            {
                EnsureCurrent();
                return (long)StoreSchema.Scalar(store._connection, null,
                    "SELECT EXISTS(SELECT 1 FROM deletion_classification_path WHERE root_no=$r AND path_key=$key" + _filter + ");",
                    ("$r", root), ("$key", RowCodec.Key(item)))! != 0;
            });
        }
        public IEnumerator<string> GetEnumerator()
        {
            var after = "";
            while (true)
            {
                var batch = store.InStore(() =>
                {
                    EnsureCurrent();
                    var rows = new List<(string Key, string Path)>();
                    using var command = StoreSchema.Command(store._connection, null,
                        "SELECT path_key,path FROM deletion_classification_path WHERE root_no=$r AND path_key>$after" +
                        _filter + " ORDER BY path_key LIMIT 256;");
                    command.Parameters.AddWithValue("$r", root); command.Parameters.AddWithValue("$after", after);
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
                    return rows;
                });
                if (batch.Count == 0) yield break;
                foreach (var row in batch) yield return row.Path;
                after = batch[^1].Key;
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsSupersetOf(IEnumerable<string> other) => other.All(Contains);
        public bool Overlaps(IEnumerable<string> other) => other.Any(Contains);
        public bool IsSubsetOf(IEnumerable<string> other) => new HashSet<string>(other, StringComparer.OrdinalIgnoreCase).IsSupersetOf(this);
        public bool IsProperSubsetOf(IEnumerable<string> other) => new HashSet<string>(other, StringComparer.OrdinalIgnoreCase).IsProperSupersetOf(this);
        public bool IsProperSupersetOf(IEnumerable<string> other) => new HashSet<string>(other, StringComparer.OrdinalIgnoreCase).IsProperSubsetOf(this);
        public bool SetEquals(IEnumerable<string> other) => new HashSet<string>(other, StringComparer.OrdinalIgnoreCase).SetEquals(this);
    }
}
