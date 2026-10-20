namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    internal long ProtectionBatchQueries { get; private set; }

    public IReadOnlyList<ProtectionMatch> ProtectionBatch(string rootId, ItemFlags flags, IReadOnlyList<string> paths)
    {
        if (paths.Count > 1024) throw new ArgumentOutOfRangeException(nameof(paths));
        if (paths.Count == 0) return Array.Empty<ProtectionMatch>();
        using var operation = RunOperations.Begin(RunOperationKind.Store, "store ProtectionBatch");
        // Only capture connection metadata under the shared gate. Even a negative containment
        // check over a large subtree must not monopolize the gap writer's connection/lock.
        var connectionString = InStore(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ProtectionBatchQueries++;
            return new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(_connection.ConnectionString)
            {
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Private,
                Pooling = false,
            }.ToString();
        });
        try
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
            connection.Open();
            StoreSchema.Execute(connection, null, "PRAGMA query_only=ON; PRAGMA cache_size=-2048;");
            return ReadProtectionBatch(connection, rootId, flags, paths);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception) when (StoreSchema.IsDamaged(exception))
        {
            throw new StateDamagedException($"v2 state is damaged: {exception.Message}", exception);
        }
    }

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
            "SELECT d.path,f.name FROM file_state f INDEXED BY fs_flagged JOIN dir d ON d.dir_no=f.dir_no " +
            "WHERE f.root_no=$r AND f.flags!=0 AND (f.flags & $flags)!=0 LIMIT $limit;");
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
