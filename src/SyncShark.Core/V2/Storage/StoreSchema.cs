using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Owns the v2 state database DDL, constants, pragmas and schema verification.</summary>
internal static class StoreSchema
{
    /// <summary>The schema version written to and required by <c>PRAGMA user_version</c>; integration assigns version bumps; additive preview tables are presence-guarded.</summary>
    public const int Version = 12;

    /// <summary>The state database path relative to the app data root.</summary>
    public const string DatabaseRelativePath = @"v2\state.db";

    /// <summary>SQLITE_CORRUPT.</summary>
    public const int CorruptCode = 11;

    /// <summary>SQLITE_NOTADB.</summary>
    public const int NotADatabaseCode = 26;

    private const string JournalDdl = "CREATE TABLE IF NOT EXISTS journal_volume(serial INTEGER PRIMARY KEY, volume TEXT NOT NULL, state_json TEXT NOT NULL);";

    private const string Ddl = """
        -- *_key columns hold PathKey.Of: equal exactly when equal under .NET OrdinalIgnoreCase (owner decision 50).
        CREATE TABLE dir(dir_no INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, path_key TEXT NOT NULL, mapped_key TEXT);
        CREATE INDEX dir_key ON dir(path_key);
        CREATE INDEX dir_mapped_key ON dir(mapped_key) WHERE mapped_key IS NOT NULL;
        CREATE TABLE root(root_no INTEGER PRIMARY KEY, root_id TEXT NOT NULL UNIQUE, cursor TEXT);
        CREATE TABLE pair(pair_no INTEGER PRIMARY KEY, pair_id TEXT NOT NULL UNIQUE, def_json TEXT NOT NULL,
          first_sync_done INTEGER NOT NULL);
        CREATE TABLE generation(gen INTEGER PRIMARY KEY AUTOINCREMENT, root_no INTEGER NOT NULL, kind TEXT NOT NULL,
          started INTEGER NOT NULL, finished INTEGER, complete INTEGER);
        CREATE TABLE file_state(root_no INTEGER NOT NULL, dir_no INTEGER NOT NULL, name_key TEXT NOT NULL, name TEXT NOT NULL,
          kind INTEGER NOT NULL,
          object_id TEXT NOT NULL, version_id TEXT, hash BLOB, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
          flags INTEGER NOT NULL, identity_json TEXT, PRIMARY KEY(root_no, dir_no, name_key, name)) WITHOUT ROWID;
        CREATE INDEX fs_obj ON file_state(root_no, object_id);
        CREATE TABLE tombstone(root_no INTEGER NOT NULL, object_id TEXT NOT NULL, dir_no INTEGER NOT NULL,
          name TEXT NOT NULL, deleted_at INTEGER NOT NULL, PRIMARY KEY(root_no, object_id)) WITHOUT ROWID;
        CREATE TABLE sync_point(pair_no INTEGER NOT NULL, object_key TEXT NOT NULL, gen INTEGER NOT NULL, kind INTEGER NOT NULL,
          hash BLOB, size INTEGER NOT NULL,
          a_dir INTEGER NOT NULL, a_name TEXT NOT NULL, a_name_key TEXT NOT NULL, a_object_id TEXT NOT NULL, a_version_id TEXT, a_mtime INTEGER NOT NULL,
          b_dir INTEGER, b_name TEXT, b_name_key TEXT, b_object_id TEXT NOT NULL, b_version_id TEXT, b_mtime INTEGER NOT NULL,
          PRIMARY KEY(pair_no, object_key)) WITHOUT ROWID;
        CREATE INDEX sp_a_obj ON sync_point(pair_no, a_object_id);
        CREATE INDEX sp_b_obj ON sync_point(pair_no, b_object_id);
        CREATE INDEX sp_a_dir ON sync_point(pair_no, a_dir, a_name_key);
        CREATE INDEX sp_b_dir ON sync_point(pair_no, b_dir, b_name_key) WHERE b_dir IS NOT NULL;
        -- A rowid table: an intent's JSON (~1 KB) overflows a WITHOUT ROWID page's local payload, one page per row.
        CREATE TABLE pending_effect(action_id TEXT PRIMARY KEY, pair_no INTEGER NOT NULL, action_json TEXT NOT NULL,
          state TEXT NOT NULL, attempts INTEGER NOT NULL, updated INTEGER NOT NULL);
        CREATE INDEX pe_unresolved ON pending_effect(pair_no, state, updated);
        CREATE TABLE recovery(recovery_id TEXT PRIMARY KEY, pair_no INTEGER NOT NULL, action_id TEXT NOT NULL,
          side TEXT NOT NULL, path TEXT NOT NULL, location TEXT NOT NULL, version_id TEXT, hash TEXT NOT NULL,
          size INTEGER NOT NULL, created INTEGER NOT NULL, expires INTEGER NOT NULL) WITHOUT ROWID;
        CREATE INDEX rc_pair ON recovery(pair_no, created);
        CREATE TABLE notice(notice_id TEXT PRIMARY KEY, pair_no INTEGER NOT NULL, rule INTEGER NOT NULL, kind TEXT NOT NULL,
          path TEXT NOT NULL, detail TEXT NOT NULL, reason_key TEXT NOT NULL, state TEXT NOT NULL, choice TEXT,
          created INTEGER NOT NULL) WITHOUT ROWID;
        CREATE INDEX nt_open ON notice(state, pair_no, created);
        -- Positive and negative probe results are reusable only after validating their basis cursor.
        CREATE TABLE deletion_evidence(pair_no INTEGER NOT NULL, root_no INTEGER NOT NULL, path TEXT NOT NULL,
          path_key TEXT NOT NULL, deleted_at INTEGER, probed_at INTEGER NOT NULL, basis_cursor TEXT, content_hash TEXT, failure_json TEXT,
          PRIMARY KEY(pair_no, root_no, path_key)) WITHOUT ROWID;
        """;

    // Performance-only, additive index; no persisted row format changes or version bump.
    private const string FlaggedIndexDdl =
        "CREATE INDEX IF NOT EXISTS fs_flagged ON file_state(root_no) WHERE flags != 0;" +
        "CREATE INDEX IF NOT EXISTS fs_refused ON file_state(root_no, kind) WHERE (flags & 64) != 0;";

    // Additive migrations preserve all existing rows.
    private const string DeletionEvidenceDdl = """
        CREATE TABLE IF NOT EXISTS deletion_evidence(pair_no INTEGER NOT NULL, root_no INTEGER NOT NULL,
          path TEXT NOT NULL, path_key TEXT NOT NULL, deleted_at INTEGER, probed_at INTEGER NOT NULL, basis_cursor TEXT,
          content_hash TEXT, PRIMARY KEY(pair_no, root_no, path_key)) WITHOUT ROWID;
        """;

    // Rowid payload table avoids one overflow page per moderately sized JSON row.
    private const string PrepareDdl = """
        CREATE TABLE IF NOT EXISTS prepare_header(pair_id TEXT PRIMARY KEY, generation TEXT NOT NULL UNIQUE,
          metadata_json TEXT NOT NULL, digest TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS prepare_row(generation TEXT NOT NULL, kind TEXT NOT NULL, ordinal INTEGER NOT NULL,
          lookup_key TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(generation, kind, ordinal));
        CREATE INDEX IF NOT EXISTS prepare_lookup ON prepare_row(generation, kind, lookup_key, ordinal);
        CREATE TABLE IF NOT EXISTS prepare_cursor(generation TEXT NOT NULL, root_id TEXT NOT NULL, cursor TEXT,
          PRIMARY KEY(generation, root_id)) WITHOUT ROWID;
        """;

    private const string ClassificationDdl = """
        CREATE TABLE IF NOT EXISTS deletion_classification(root_no INTEGER PRIMARY KEY,
          root_binding TEXT NOT NULL, basis_cursor TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS deletion_classification_path(root_no INTEGER NOT NULL,
          path_key TEXT NOT NULL, path TEXT NOT NULL, exact INTEGER NOT NULL,
          PRIMARY KEY(root_no,path_key)) WITHOUT ROWID;
        """;

    private const string ObservationDdl = """
        CREATE TABLE IF NOT EXISTS observation_checkpoint(root_no INTEGER PRIMARY KEY,
          root_binding TEXT NOT NULL, scope_json TEXT NOT NULL, cursor TEXT NOT NULL);
        """;

    private const string RetryDdl = """
        CREATE TABLE IF NOT EXISTS sync_retry(pair_no INTEGER NOT NULL, path_key TEXT NOT NULL,
          json TEXT NOT NULL, retry_after INTEGER NOT NULL, PRIMARY KEY(pair_no,path_key));
        CREATE INDEX IF NOT EXISTS sync_retry_due ON sync_retry(pair_no,retry_after,path_key);
        """;

    private const string RehashDdl = """
        CREATE TABLE IF NOT EXISTS local_rehash(root_no INTEGER NOT NULL, object_id TEXT NOT NULL,
          last_attempt INTEGER NOT NULL, reason TEXT NOT NULL, PRIMARY KEY(root_no, object_id)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS rehash_retry ON local_rehash(root_no, last_attempt, object_id);
        """;

    private const string TemporaryDdl = """
        CREATE TEMP TABLE IF NOT EXISTS tmp_dirs(dir_no INTEGER PRIMARY KEY);
        CREATE TEMP TABLE IF NOT EXISTS tmp_items(dir_no INTEGER NOT NULL, name_key TEXT NOT NULL,
          PRIMARY KEY(dir_no, name_key)) WITHOUT ROWID;
        """;

    /// <summary>Adds schema objects without removing rows; mapped directory keys backfill in short transactions.</summary>
    private static void Migrate(SqliteConnection connection, long fromVersion)
    {
        using (var transaction = connection.BeginTransaction())
        {
            if (fromVersion < 4) Execute(connection, transaction, DeletionEvidenceDdl);
            if (fromVersion < 5 && Scalar(connection, transaction,
                "SELECT COUNT(*) FROM pragma_table_info('deletion_evidence') WHERE name='failure_json';") is long columns && columns == 0)
                Execute(connection, transaction, "ALTER TABLE deletion_evidence ADD COLUMN failure_json TEXT;");
            Execute(connection, transaction, PrepareDdl);
            transaction.Commit();
        }
        // v7 is restartable: column first, short backfill batches, version stamp only after completion.
        if (fromVersion < 7) MigrateMappedDirectories(connection);
        if (fromVersion < 8) Execute(connection, null, FlaggedIndexDdl);
        if (fromVersion < 9) Execute(connection, null, ClassificationDdl);
        if (fromVersion < 10) Execute(connection, null, ObservationDdl);
        using var final = connection.BeginTransaction();
        Execute(connection, final, RehashDdl);
        Execute(connection, final, RetryDdl);
        Execute(connection, final, JournalDdl);
        Execute(connection, final, $"PRAGMA user_version={Version};");
        final.Commit();
    }

    private static void MigrateMappedDirectories(SqliteConnection connection)
    {
        if (Scalar(connection, null, "SELECT COUNT(*) FROM pragma_table_info('dir') WHERE name='mapped_key';") is long n && n == 0)
            Execute(connection, null, "ALTER TABLE dir ADD COLUMN mapped_key TEXT;");
        long after = -1;
        while (true)
        {
            var batch = new List<(long Id, string Path)>();
            using (var command = Command(connection, null, "SELECT dir_no,path FROM dir WHERE dir_no>$after ORDER BY dir_no LIMIT 256;"))
            {
                command.Parameters.AddWithValue("$after", after);
                using var reader = command.ExecuteReader();
                while (reader.Read()) batch.Add((reader.GetInt64(0), reader.GetString(1)));
            }
            if (batch.Count == 0) break;
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var (id, path) in batch)
                    if (DropboxNameMapping.Map(path) is var mapped && mapped != path)
                        Execute(connection, transaction, "UPDATE dir SET mapped_key=$key WHERE dir_no=$id;",
                            ("$key", RowCodec.Key(mapped)), ("$id", id));
                transaction.Commit();
            }
            after = batch[^1].Id;
        }
        Execute(connection, null, "CREATE INDEX IF NOT EXISTS dir_mapped_key ON dir(mapped_key) WHERE mapped_key IS NOT NULL;");
    }

    private static readonly string[] ExpectedNames =
    [
        "journal_volume", "dir", "root", "pair", "generation", "file_state", "tombstone", "sync_point", "pending_effect", "sync_retry", "sync_retry_due", "recovery",
        "notice", "local_rehash", "rehash_retry", "deletion_evidence", "dir_key", "dir_mapped_key", "fs_obj", "fs_flagged", "fs_refused", "sp_a_obj", "sp_b_obj", "sp_a_dir", "sp_b_dir",

        "pe_unresolved", "rc_pair", "nt_open", "prepare_header", "prepare_row", "prepare_lookup", "prepare_cursor",
        "deletion_classification", "deletion_classification_path", "observation_checkpoint",
    ];

    /// <summary>A command bound to the connection and optional transaction (Microsoft.Data.Sqlite requires it).</summary>
    public static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    /// <summary>Runs a statement with the given parameters.</summary>
    public static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] arguments)
    {
        using var command = Command(connection, transaction, sql);
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>The first column of the first row, or null.</summary>
    public static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] arguments)
    {
        using var command = Command(connection, transaction, sql);
        foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteScalar();
    }

    /// <summary>How many rows the last statement on this transaction changed.</summary>
    public static long Changes(SqliteConnection connection, SqliteTransaction? transaction) =>
        (long)Scalar(connection, transaction, "SELECT changes();")!;

    /// <summary>The EXPLAIN QUERY PLAN detail lines of a statement, each prefixed with a label (parameters bound null).</summary>
    public static IReadOnlyList<string> QueryPlan(SqliteConnection connection, string label, string sql,
        params string[] parameterNames)
    {
        using var command = Command(connection, null, "EXPLAIN QUERY PLAN " + sql);
        foreach (var name in parameterNames) command.Parameters.AddWithValue(name, DBNull.Value);
        var lines = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) lines.Add(label + ": " + reader.GetString(3));
        return lines;
    }

    /// <summary>Whether a SQLite failure means the state database is unusable (CORRUPT or NOTADB).</summary>
    public static bool IsDamaged(SqliteException exception) =>
        exception.SqliteErrorCode is CorruptCode or NotADatabaseCode;

    /// <summary>Applies the store's pragmas and requires WAL journaling; a non-WAL database is damaged.</summary>
    public static void Pragmas(SqliteConnection connection, V2Settings settings)
    {
        var journal = Scalar(connection, null, "PRAGMA journal_mode=WAL;");
        if (!string.Equals(journal as string, "wal", StringComparison.OrdinalIgnoreCase))
            throw new StateDamagedException($"v2 state journal_mode is '{journal}', expected wal.");
        // FULL: a committed intent survives power loss or an OS crash, so an effect never runs without one on disk
        // (WAL + NORMAL may lose the last commits). Group commit (B-124) amortises the extra syncs.
        Execute(connection, null,
            settings.StoreDurableCommits ? "PRAGMA synchronous=FULL;" : "PRAGMA synchronous=NORMAL;");
        // Narrowing/large subtree work sets must spill to disk rather than retain O(inventory) native
        // memory. Set this once at open: changing it later would discard the store's temp tables.
        Execute(connection, null, "PRAGMA temp_store=FILE;");
        Execute(connection, null, "PRAGMA foreign_keys=OFF;");
        Execute(connection, null, $"PRAGMA cache_size=-{settings.StoreCacheMb * 1024};");
        Execute(connection, null, $"PRAGMA busy_timeout={(long)settings.StoreBusyTimeout.TotalMilliseconds};");
    }

    /// <summary>Creates the fresh schema and stamps the version in one transaction.</summary>
    public static void Create(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, Ddl);
        Execute(connection, transaction, JournalDdl);
        Execute(connection, transaction, RehashDdl);
        Execute(connection, transaction, RetryDdl);
        Execute(connection, transaction, FlaggedIndexDdl);
        Execute(connection, transaction, PrepareDdl);
        Execute(connection, transaction, ClassificationDdl);
        Execute(connection, transaction, ObservationDdl);
        Execute(connection, transaction, $"PRAGMA user_version={Version};");
        transaction.Commit();
    }

    /// <summary>Creates the session's temporary working tables.</summary>
    public static void CreateTemporary(SqliteConnection connection) => Execute(connection, null, TemporaryDdl);

    /// <summary>Checks the version and the exact set of tables and indexes; a mismatch is damage.</summary>
    public static void Verify(SqliteConnection connection)
    {
        if (Scalar(connection, null, "PRAGMA user_version;") is not long version)
            throw new StateDamagedException("v2 state schema version is missing or not numeric.");
        // Preserve v3/v4 evidence upgrades and add v6 prepare storage in place.
        // All migrations preserve existing rows and indexes; pre-v3 stores need a fresh database.
        if (version is >= 3 and < Version)
        {
            Migrate(connection, version);
            version = Version;
        }
        if (version is >= 1 and < Version) throw new StateNeedsFreshDatabaseException(version, Version);
        if (version != Version)
            throw new StateDamagedException($"v2 state schema version {version}, expected {Version}.");
        // Additive feature storage is presence-guarded on every compatible open, not gated by a version bump.
        Execute(connection, null, RetryDdl);
        Execute(connection, null, JournalDdl); // Additive preview table: presence-guarded on every compatible open.
        var names = new HashSet<string>(StringComparer.Ordinal);
        using (var command = Command(connection, null,
            "SELECT name FROM sqlite_master WHERE type IN ('table','index') AND name NOT LIKE 'sqlite_%';"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) names.Add(reader.GetString(0));
        }
        // Existing v5 stores may lack only this additive performance index. Validate every other
        // name before changing anything; unknown/missing tables or indexes still mean damage.
        names.Add("fs_flagged");
        names.Add("fs_refused");
        if (!names.SetEquals(ExpectedNames))
            throw new StateDamagedException($"v2 state schema mismatch: {{{string.Join(", ", names.Order())}}}.");
        Execute(connection, null, FlaggedIndexDdl);
        // Unreleased v4 previews may have created the cache before cursor binding was added.
        // Legacy rows keep a null basis and are discarded before any reuse.
        if (Scalar(connection, null,
            "SELECT COUNT(*) FROM pragma_table_info('deletion_evidence') WHERE name='basis_cursor';") is long columns && columns == 0)
            Execute(connection, null, "ALTER TABLE deletion_evidence ADD COLUMN basis_cursor TEXT;");
        // Decision 69 adds the last revision's content hash as nullable evidence; an older v4 preview
        // gains the column in place, and its existing rows keep an unknown hash (never skip Recovery).
        if (Scalar(connection, null,
            "SELECT COUNT(*) FROM pragma_table_info('deletion_evidence') WHERE name='content_hash';") is long hashColumns && hashColumns == 0)
            Execute(connection, null, "ALTER TABLE deletion_evidence ADD COLUMN content_hash TEXT;");
    }
}
