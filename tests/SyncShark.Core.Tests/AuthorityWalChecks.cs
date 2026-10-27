using System.Reflection;
using Microsoft.Data.Sqlite;
using Massdex.SyncShark;

// The input is an isolated authority database. Every durable write, connection
// admission, execution queue and savepoint decision stays in production code.
internal static class AuthorityWalChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task AssertProductionSettingsAsync(string path)
    {
        await using var connection = await OpenProductionAsync(path);
        await AssertSettingsAsync(connection);
    }

    private static async Task<SqliteConnection> OpenProductionAsync(string path)
    {
        var method = typeof(SyncAuthorityDatabase).GetMethod("OpenConnectionAtPathAsync",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return await (Task<SqliteConnection>)method.Invoke(null,
            [path, SqliteOpenMode.ReadWrite, true, CancellationToken.None, false, false])!;
    }

    private static async Task AssertSettingsAsync(SqliteConnection connection)
    {
        Require(await ScalarAsync(connection, "PRAGMA wal_autocheckpoint;") == 1000,
            "the actual production writer uses a 1000-page PASSIVE autocheckpoint");
        Require(await ScalarAsync(connection, "PRAGMA main.journal_size_limit;") == 33554432,
            "the actual production writer bounds retained WAL after reset");
        Require(await ScalarAsync(connection, "PRAGMA synchronous;") == 2 &&
            await ScalarAsync(connection, "PRAGMA foreign_keys;") == 1 &&
            await ScalarAsync(connection, "PRAGMA trusted_schema;") == 0,
            "FULL durability, foreign keys and trusted-schema rejection are unchanged");
    }

    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh bounded WAL fixture.");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "authority.db");
        var authority = new SyncAuthorityDatabase(path);
        await authority.InitializeAsync("wal-fixture", "wal-profile", "wal-policy", "{}");
        await authority.RegisterObjectAsync("wal-object", "file");
        await authority.RegisterPairAsync("wal-pair", 1);
        var identity = "{\"fixturePadding\":\"" + new string('x', 512 * 1024) + "\"}";
        var expected = 0;
        long maximumNormalWal = 0, heldWal, recoveredWal;
        await using (authority.BeginExecutionConnectionScope())
        {
            for (var i = 0; i < 100; i++)
            {
                await AddAsync();
                maximumNormalWal = Math.Max(maximumNormalWal, WalBytes(path));
            }
            var writer = HeldWriter(authority);
            await AssertSettingsAsync(writer);
            Require(maximumNormalWal > 0 && maximumNormalWal < 32 * 1024 * 1024,
                "100 real acknowledged commits recycle WAL while the same execution writer stays open");
            await using (var reader = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                await reader.OpenAsync();
                using var snapshot = reader.BeginTransaction(deferred: true);
                var before = await ScalarAsync(reader, "SELECT count(*) FROM object_version;", snapshot);
                for (var i = 0; i < 80; i++) await AddAsync();
                heldWal = WalBytes(path);
                Console.WriteLine($"Reader hold: WAL={heldWal}, snapshotBefore={before}, " +
                    $"snapshotNow={await ScalarAsync(reader, "SELECT count(*) FROM object_version;", snapshot)}, " +
                    $"writerNow={await ScalarAsync(writer, "SELECT count(*) FROM object_version;")}, expected={expected}");
                Require(heldWal > 32 * 1024 * 1024 &&
                    await ScalarAsync(reader, "SELECT count(*) FROM object_version;", snapshot) == before &&
                    await ScalarAsync(writer, "SELECT count(*) FROM object_version;") == expected,
                    "a pinned reader retains its snapshot; all newer commits survive even above the retained-size limit");
            }
            for (var i = 0; i < 24; i++) await AddAsync();
            recoveredWal = WalBytes(path);
            Require(recoveredWal <= 32 * 1024 * 1024 && recoveredWal < heldWal &&
                ReferenceEquals(writer, HeldWriter(authority)),
                "reader release permits ordinary commits to recycle WAL without closing the writer or forcing a checkpoint");
            var rejected = authority.SetPairStateAsync("missing-wal-pair", "active");
            var good = authority.SetPairStateAsync("wal-pair", "active");
            var refused = false;
            try { await rejected; } catch (InvalidOperationException) { refused = true; }
            await good;
            Require(refused && await ScalarAsync(writer,
                "SELECT count(*) FROM sync_pair WHERE pair_id='wal-pair' AND state='active';") == 1,
                "a real rejected write rolls back independently while its valid neighbor commits");
            SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = _ => throw new IOException("fixture pre-commit refusal");
            try
            {
                var failed = false;
                try { await authority.SetPairStateAsync("wal-pair", "paused"); }
                catch (IOException) { failed = true; }
                Require(failed && await ScalarAsync(writer,
                    "SELECT count(*) FROM sync_pair WHERE pair_id='wal-pair' AND state='active';") == 1,
                    "failed durable commit acknowledges no change and preserves the preceding state");
            }
            finally { SyncAuthorityDatabase.ExecutionWriteCommitBoundaryForTest = null; }
        }
        await authority.VerifyHealthAsync();
        await using (var connection = await OpenProductionAsync(path))
        {
            Require(await ScalarAsync(connection, "SELECT count(*) FROM object_version;") == expected,
                "all 204 acknowledged object versions survive writer drain and real database reopen");
            foreach (var altered in new[] { "PRAGMA wal_autocheckpoint=0;", "PRAGMA main.journal_size_limit=-1;" })
            {
                await SqlAsync(connection, altered);
                var guard = typeof(SyncAuthorityDatabase).GetMethod("RequireConnectionSettingsAsync",
                    BindingFlags.Static | BindingFlags.NonPublic)!;
                var refused = false;
                try { await (Task)guard.Invoke(null, [connection, CancellationToken.None])!; }
                catch (InvalidDataException) { refused = true; }
                Require(refused, "actual connection admission rejects disabled/unbounded WAL policy");
                await SyncAuthorityDatabase.ConfigureAuthorityConnectionAsync(connection);
            }
        }
        Console.WriteLine($"PASS production WAL: commits={expected}, normalMaxBytes={maximumNormalWal}, " +
            $"readerHeldBytes={heldWal}, readerReleasedBytes={recoveredWal}; same writer, stable reader, savepoint/commit rollback, reopen and unsafe-policy rejection");
        SqliteConnection.ClearAllPools();
        // Only this newly created bounded fixture is disposable; a refused cleanup
        // is an error, never permission to retry through another mechanism.
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("WAL fixture identity changed; cleanup refused.");
        Directory.Delete(root, recursive: true);

        async Task AddAsync()
        {
            await authority.RegisterObjectVersionAsync("wal-version-" + expected, "wal-object", "local",
                identity[..^1] + ",\"fixtureOrdinal\":" + expected + "}", null, 0);
            expected++;
        }
    }

    private static SqliteConnection HeldWriter(SyncAuthorityDatabase authority)
    {
        var local = typeof(SyncAuthorityDatabase).GetField("_executionConnectionScope", Private)!.GetValue(authority)!;
        var scope = local.GetType().GetProperty("Value")!.GetValue(local)!;
        var writes = scope.GetType().GetField("_writes", Private)!.GetValue(scope)!;
        return (SqliteConnection)writes.GetType().GetProperty("Connection", Private | BindingFlags.Public)!.GetValue(writes)!;
    }

    private static long WalBytes(string path) => File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0;
    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction; command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    private static async Task SqlAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
