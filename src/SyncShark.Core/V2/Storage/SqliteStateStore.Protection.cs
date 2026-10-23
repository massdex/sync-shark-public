using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal static string StorageProtectionSql(string sql, ItemFlags flags, bool storageEnabled) =>
        storageEnabled && flags == ItemFlags.OwnedPointer
            ? sql.Replace("INDEXED BY fs_flagged", "INDEXED BY fs_owned")
                .Replace("rf.flags!=0", "(rf.flags & 1024) != 0").Replace("f.flags!=0", "(f.flags & 1024) != 0")
            : storageEnabled && (flags & ItemFlags.OwnedPointer) == 0
                ? sql.Replace("INDEXED BY fs_flagged", "INDEXED BY fs_nonpointer")
                    .Replace("rf.flags!=0", "(rf.flags & ~1024) != 0").Replace("f.flags!=0", "(f.flags & ~1024) != 0")
                : sql;

    internal string StorageProtectionSql(string sql, ItemFlags flags) => StorageProtectionSql(sql, flags, _storageEnabled);

    internal long ProtectionBatchQueries { get; private set; }

    // Test seam after the first SELECT has pinned the snapshot and the shared gate is released.
    internal Action? ProtectionSnapshotReadyForTest { get; set; }

    public IReadOnlyList<ProtectionMatch> ProtectionBatch(string rootId, ItemFlags flags, IReadOnlyList<string> paths)
    {
        if (paths.Count > 1024) throw new ArgumentOutOfRangeException(nameof(paths));
        if (paths.Count == 0) return Array.Empty<ProtectionMatch>();
        using var operation = RunOperations.Begin(RunOperationKind.Store, "store ProtectionBatch");
        using (_gate.Enter())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ProtectionBatchQueries++;
        }
        try
        {
            try
            {
                var snapshot = OpenProtectionSnapshot(rootId);
                using var connection = snapshot.Connection;
                using var transaction = snapshot.Transaction;
                ProtectionSnapshotReadyForTest?.Invoke();
                return ReadProtectionBatch(connection, transaction, snapshot.Root, flags, paths, _storageEnabled);
            }
            catch (Exception exception) when (exception is not StateDamagedException and not OperationCanceledException &&
                !(exception is SqliteException sqlite && StoreSchema.IsDamaged(sqlite)))
            {
                // A transient extra-reader failure must not stop comparison. Retry exactly
                // once on the existing connection; this slower fallback retains every hold.
                var code = exception is SqliteException failure
                    ? $" sqlite={failure.SqliteErrorCode}/{failure.SqliteExtendedErrorCode}" : "";
                RunLog.Trace($"protection reader unavailable ({exception.GetType().Name}{code}): {exception.Message}; using gated store connection once");
                using (_gate.Enter())
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    using var transaction = _connection.BeginTransaction(deferred: true);
                    var root = ProtectionRootNumber(_connection, transaction, rootId);
                    return ReadProtectionBatch(_connection, transaction, root, flags, paths, _storageEnabled);
                }
            }
        }
        catch (SqliteException exception) when (StoreSchema.IsDamaged(exception))
        {
            throw new StateDamagedException($"v2 state is damaged: {exception.Message}", exception);
        }
    }

    private (SqliteConnection Connection, SqliteTransaction Transaction, long? Root) OpenProtectionSnapshot(string rootId)
    {
        using (_gate.Enter())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder(_connection.ConnectionString)
            {
                Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false,
            }.ToString());
            SqliteTransaction? transaction = null;
            try
            {
                connection.Open();
                StoreSchema.Execute(connection, null, "PRAGMA cache_size=-2048; PRAGMA temp_store=FILE; PRAGMA temp.cache_size=-2048;");
                transaction = connection.BeginTransaction(deferred: true);
                // BeginTransaction alone does not pin a deferred snapshot. The first SELECT
                // must also run under _gate, between store calls, never between commits of
                // one multi-batch delta. Only the later bounded scans run outside the gate.
                var root = ProtectionRootNumber(connection, transaction, rootId);
                return (connection, transaction, root);
            }
            catch
            {
                transaction?.Dispose();
                connection.Dispose();
                throw;
            }
        }
    }

    private static long? ProtectionRootNumber(SqliteConnection connection, SqliteTransaction transaction, string rootId) =>
        StoreSchema.Scalar(connection, transaction,
            "SELECT root_no FROM root WHERE root_id=$id;", ("$id", rootId)) is long root ? root : null;

    /// <summary>One indexed streaming query builds the bounded snapshot; no ItemState decoding.</summary>
    public PathProtectionIndex? ProtectionIndex(string rootId, ItemFlags flags,
        int pathLimit = PathProtectionIndex.DefaultPathLimit,
        int characterLimit = PathProtectionIndex.DefaultCharacterLimit) => InStore(() =>
    {
        var root = RootNo(rootId);
        if (root is null) return PathProtectionIndex.TryCreate(Array.Empty<string>(), pathLimit, characterLimit);
        // Lazy discovery protects the unvisited indexed suffix without materializing a root-sized set.
        if ((flags & ItemFlags.NeedsRehash) != 0 && HasGapProtection(root)) return null;
        using var command = StoreSchema.Command(_connection, null,
            StorageProtectionSql("SELECT d.path,f.name FROM file_state f INDEXED BY fs_flagged JOIN dir d ON d.dir_no=f.dir_no " +
            "WHERE f.root_no=$r AND f.flags!=0 AND (f.flags & $flags)!=0 LIMIT $limit;", flags));
        command.Parameters.AddWithValue("$r", root.Value);
        command.Parameters.AddWithValue("$flags", (long)flags);
        command.Parameters.AddWithValue("$limit", (long)pathLimit + 1);
        using var reader = command.ExecuteReader();
        var rows = 0;
        IEnumerable<string> Paths()
        {
            while (reader.Read())
            {
                rows++;
                yield return RowCodec.Join(reader.GetString(0), reader.GetString(1));
            }
        }
        var index = PathProtectionIndex.TryCreate(Paths(), pathLimit, characterLimit);
        // Case-clashing spellings can collapse in the index; a truncated query is still overflow.
        return rows > pathLimit ? null : index;
    });
}
