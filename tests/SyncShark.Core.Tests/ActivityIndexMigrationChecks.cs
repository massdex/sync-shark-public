using System.Globalization;
using System.IO;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using IOPath = System.IO.Path;

// Focused real-SQLite check for the additive authority schema migration 6->7. It assembles a genuine v6
// predecessor from the product's own schema text and migrations 3..6, seeds retained installation/capability
// and sentinel history, then drives the REAL SyncAuthorityDatabase.InitializeAsync, proving the seven indexes,
// unchanged sentinel rows, the exact v6 rollback copy, the unexpected-index refusal and injected v7 rollback.
internal static class ActivityIndexMigrationChecks
{
    private const string Installation = "installation-migration7-431";
    private const string ProfileId = "profile-migration7-431";
    private const string SourceRevision = "migration7-431";
    private const string CapabilityJson = "{\"name\":\"migration7-431\"}";
    private const string SentinelEventId = "history-event-migration7-431";
    private const string SentinelDetail = "{\"PairId\":\"pair-migration7-431\",\"Severity\":\"warning\",\"RunId\":\"run-migration7-431\",\"OperationId\":\"operation-migration7-431\",\"EndpointContext\":{\"left\":\"a\",\"right\":\"b\"}}";

    private static readonly string[] ExpectedIndexes =
    [
        "history_event_occurred_utc",
        "history_event_operation_id",
        "history_event_endpoint_context_operation",
        "history_event_presentation_severity",
        "history_event_presentation_run",
        "reconciliation_decision_decided_utc",
        "operation_member_operation_id"
    ];

    private static int _assertions;

    internal static async Task RunAsync(string root)
    {
        _assertions = 0;
        SQLitePCL.Batteries_V2.Init();
        var directory = IOPath.GetFullPath(root);
        Directory.CreateDirectory(directory);

        await Version6MigratesToVersion7Async(IOPath.Combine(directory, "migrate"));
        await UnexpectedIndexRefusedAsync(IOPath.Combine(directory, "unexpected"));
        await InjectedV7RollsBackAsync(IOPath.Combine(directory, "rollback"));

        Console.WriteLine("PASS activity index migration: " + _assertions +
            " assertions; a genuine v6 database migrated to v7 with exactly the seven non-unique indexes and" +
            " unchanged sentinel rows, an unexpected index was refused before mutation, and an injected v7" +
            " failure rolled the transaction back leaving the v6 database intact.");
    }

    private static async Task Version6MigratesToVersion7Async(string name)
    {
        var path = PathFor(name);
        await BuildVersion6Async(path);
        var beforeSentinel = await ScalarAsync(path,
            "SELECT history_event_id || '|' || event_kind || '|' || detail_json || '|' || occurred_utc FROM history_event WHERE history_event_id='" + SentinelEventId + "';");
        var beforeCount = await NumberAsync(path, "SELECT count(*) FROM history_event;");
        Require(beforeSentinel.Length > 0, "AIM-01 the v6 predecessor retains the sentinel history row");

        var database = new SyncAuthorityDatabase(path);
        await database.InitializeAsync(Installation, ProfileId, SourceRevision, CapabilityJson);

        Require(await NumberAsync(path, "PRAGMA user_version;") == SyncAuthoritySchema.Version,
            "AIM-02 the real InitializeAsync migrated the v6 database to the current schema version");
        var indexes = await ScalarListAsync(path,
            "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%';");
        foreach (var expected in ExpectedIndexes)
            Require(indexes.Contains(expected, StringComparer.OrdinalIgnoreCase),
                "AIM-02 migration 7 created the index " + expected);
        foreach (var expected in ExpectedIndexes)
        {
            var definition = await ScalarAsync(path,
                "SELECT sql FROM sqlite_master WHERE type='index' AND name='" + expected + "';");
            Require(definition.Length > 0 &&
                    definition.IndexOf("CREATE UNIQUE", StringComparison.OrdinalIgnoreCase) < 0,
                "AIM-02 " + expected + " is a non-unique index: " + definition);
        }
        Require(await ScalarAsync(path,
                "SELECT history_event_id || '|' || event_kind || '|' || detail_json || '|' || occurred_utc FROM history_event WHERE history_event_id='" + SentinelEventId + "';") == beforeSentinel &&
            await NumberAsync(path, "SELECT count(*) FROM history_event;") == beforeCount,
            "AIM-03 the migration left the retained sentinel history row and count byte-for-byte unchanged");
        Require(await ScalarAsync(path, "PRAGMA quick_check;") == "ok",
            "AIM-03 PRAGMA quick_check reports ok after the real migration");
        Require(File.Exists(database.Version6RollbackDatabasePath) &&
                await NumberAsync(database.Version6RollbackDatabasePath, "PRAGMA user_version;") == 6 &&
                await ScalarAsync(database.Version6RollbackDatabasePath,
                    "SELECT history_event_id || '|' || event_kind || '|' || detail_json || '|' || occurred_utc FROM history_event WHERE history_event_id='" + SentinelEventId + "';") == beforeSentinel &&
                !ExpectedIndexes.Intersect(await ScalarNamesAsync(database.Version6RollbackDatabasePath)).Any(),
            "AIM-04 the exact retained v6 rollback copy exists at version 6");

        var reopened = new SyncAuthorityDatabase(path);
        await reopened.InitializeAsync(Installation, ProfileId, SourceRevision, CapabilityJson);
        var reopenedIndexes = await ScalarNamesAsync(path);
        Require(ExpectedIndexes.All(expected => reopenedIndexes.Contains(expected, StringComparer.OrdinalIgnoreCase)),
            "AIM-05 a second real initialization is idempotent and keeps all seven indexes");
    }

    private static async Task UnexpectedIndexRefusedAsync(string name)
    {
        var path = PathFor(name);
        await BuildVersion6Async(path);
        await ExecuteAsync(path, "CREATE INDEX unexpected_index_migration7 ON history_event(event_kind);");
        var beforeUserVersion = await NumberAsync(path, "PRAGMA user_version;");
        var refused = string.Empty;
        try
        {
            await new SyncAuthorityDatabase(path).InitializeAsync(Installation, ProfileId, SourceRevision,
                CapabilityJson);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        {
            refused = error.Message;
        }
        Require(refused.Length > 0,
            "AIM-06 the unexpected extra index is refused before mutation: " +
            (refused.Length == 0 ? "accepted" : refused));
        Require(await NumberAsync(path, "PRAGMA user_version;") == beforeUserVersion &&
                await NumberAsync(path, "SELECT count(*) FROM sqlite_master WHERE name='unexpected_index_migration7';") == 1,
            "AIM-06 the refusal left the v6 database and the unexpected index untouched");
    }

    private static async Task InjectedV7RollsBackAsync(string name)
    {
        var path = PathFor(name);
        await BuildVersion6Async(path);
        var before = await SnapshotAsync(path);
        SyncAuthorityDatabase.MigrationBoundaryForTest = observed =>
        {
            if (string.Equals(observed, "v7-schema-applied-before-version-commit", StringComparison.Ordinal))
                throw new IOException("injected v7 failure before the version commit");
        };
        var thrown = string.Empty;
        try
        {
            await new SyncAuthorityDatabase(path).InitializeAsync(Installation, ProfileId, SourceRevision,
                CapabilityJson);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or InvalidDataException)
        {
            thrown = error.Message;
        }
        finally
        {
            SyncAuthorityDatabase.MigrationBoundaryForTest = null;
        }
        Require(thrown.Contains("injected v7 failure", StringComparison.Ordinal),
            "AIM-07 the injected v7 boundary failure surfaced: " + (thrown.Length == 0 ? "not thrown" : thrown));
        Require(await NumberAsync(path, "PRAGMA user_version;") == 6 && await SnapshotAsync(path) == before,
            "AIM-07 the failed v7 transaction rolled back and left the retained v6 database unchanged");
        var indexes = await ScalarListAsync(path,
            "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%';");
        Require(!ExpectedIndexes.Any(expected => indexes.Contains(expected, StringComparer.OrdinalIgnoreCase)),
            "AIM-07 no migration-7 index survived the rolled-back transaction");
    }

    // A real predecessor at version 6, composed from the product's own SQL texts, with a matching
    // installation/capability receipt and one retained sentinel history row. Nothing here is produced by the
    // code that later migrates it.
    private static async Task BuildVersion6Async(string path)
    {
        Directory.CreateDirectory(IOPath.GetDirectoryName(path)!);
        var sql = SyncAuthoritySchema.Sql + SyncAuthoritySchemaSupplement.Sql +
                  SyncAuthoritySchemaMigration3.Sql + SyncAuthoritySchemaMigration4.Sql +
                  SyncAuthoritySchemaMigration5.Sql + SyncAuthoritySchemaMigration6.Sql;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = sql;
            await schema.ExecuteNonQueryAsync();
        }
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText =
                "INSERT INTO installation(installation_id,database_generation,created_utc)" +
                " VALUES('" + Installation + "','generation-migration7-431','2026-09-19T00:00:00.0000000+00:00');" +
                "INSERT INTO capability_profile(capability_profile_id,source_revision,profile_json,created_utc)" +
                " VALUES('" + ProfileId + "','" + SourceRevision + "','{}','2026-09-19T00:00:00.0000000+00:00');" +
                "INSERT INTO sync_pair(pair_id,configuration_revision,state,created_utc,pair_json,selected)" +
                " VALUES('pair-migration7-431',1,'paused','2026-09-19T00:00:00.0000000+00:00',NULL,0);" +
                "INSERT INTO root_binding(root_binding_id,pair_id,side,generation,identity_json,display_path,state,observed_utc)" +
                " VALUES('root-migration7-431','pair-migration7-431','dropbox',1,'{}','/Documents','active'," +
                "'2026-09-19T00:00:00.0000000+00:00');" +
                "INSERT INTO history_event(history_event_id,operation_id,event_kind,detail_json,occurred_utc)" +
                " VALUES('" + SentinelEventId + "',NULL,'presentation','" +
                SentinelDetail.Replace("'", "''") + "','2026-09-19T00:00:00.0000000+00:00');";
            await seed.ExecuteNonQueryAsync();
        }
        await using (var receipt = connection.CreateCommand())
        {
            receipt.CommandText =
                "INSERT INTO schema_migration(schema_version,applied_utc,source_revision)" +
                " VALUES(6,'2026-09-19T00:00:00.0000000+00:00','predecessor');" +
                "PRAGMA user_version=6;";
            await receipt.ExecuteNonQueryAsync();
        }
    }

    private static string PathFor(string name)
    {
        var directory = IOPath.Combine(IOPath.GetDirectoryName(IOPath.GetFullPath(name))!, IOPath.GetFileName(name));
        Directory.CreateDirectory(directory);
        return IOPath.Combine(directory, "authority", "syncshark.db");
    }

    private static async Task<List<string>> ScalarNamesAsync(string databasePath) =>
        await ScalarListAsync(databasePath,
            "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%';");

    private static async Task<List<string>> ScalarListAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        SqliteConnection.ClearAllPools();
        return values;
    }

    private static async Task<long> NumberAsync(string databasePath, string sql) =>
        Convert.ToInt64(await ScalarAsync(databasePath, sql), CultureInfo.InvariantCulture);

    private static async Task<string> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        SqliteConnection.ClearAllPools();
        return value is null or DBNull ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<string> SnapshotAsync(string databasePath) =>
        await ScalarAsync(databasePath,
            "SELECT group_concat(name || '|' || type || '|' || sql, '\n') FROM (SELECT name,type,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY name);") +
        "||" + await ScalarAsync(databasePath, "PRAGMA user_version;") +
        "||" + await ScalarAsync(databasePath,
            "SELECT json_group_array(json_array(history_event_id,operation_id,event_kind,detail_json,occurred_utc)) FROM (SELECT * FROM history_event ORDER BY history_event_id);") +
        "||" + await ScalarAsync(databasePath,
            "SELECT json_group_array(json_array(installation_id,database_generation,created_utc)) FROM installation;") +
        "||" + await ScalarAsync(databasePath,
            "SELECT json_group_array(json_array(schema_version,applied_utc,source_revision)) FROM (SELECT * FROM schema_migration ORDER BY schema_version);");

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
        SqliteConnection.ClearAllPools();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        _assertions++;
        Console.WriteLine("PASS " + message);
    }
}
