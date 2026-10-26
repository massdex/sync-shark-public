using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class RecoveryReadbackChecks
{
    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh isolated readback fixture.");
        Directory.CreateDirectory(root);
        SQLitePCL.Batteries_V2.Init();
        var source = Path.Combine(root, "source.db");
        var copy = Path.Combine(root, "copy.db");
        using (var original = Open(source))
        {
            Execute(original, """
                CREATE TABLE keyed(a TEXT NOT NULL,b INTEGER NOT NULL,data BLOB,n REAL,PRIMARY KEY(a,b)) WITHOUT ROWID;
                INSERT INTO keyed VALUES('one',1,X'000102',NULL),('two',2,NULL,1.25),('three',3,X'',0.0);
                CREATE TABLE nullable(a TEXT PRIMARY KEY,data TEXT);
                INSERT INTO nullable VALUES(NULL,'first'),(NULL,'second'),('present','third');
                CREATE TABLE rowless(a INTEGER,data TEXT);
                INSERT INTO rowless VALUES(1,'same'),(1,'same'),(2,NULL);
                CREATE TABLE collated(id INTEGER PRIMARY KEY,data TEXT COLLATE NOCASE);
                INSERT INTO collated VALUES(1,'MixedCase');
                CREATE TABLE numeric(id INTEGER PRIMARY KEY,data);
                INSERT INTO numeric VALUES(1,1);
                CREATE TABLE "quote""name"("key""column" INTEGER PRIMARY KEY,data TEXT);
                INSERT INTO "quote""name" VALUES(1,'quoted');
                """);
            using var destination = Open(copy);
            original.BackupDatabase(destination);
        }
        var sourceHash = Hash(source);
        var checks = new List<string>();
        async Task AssertMatch(bool expected, string name)
        {
            var actual = await MatchAsync(source, copy, CancellationToken.None);
            var oracle = Oracle(source, copy);
            if (actual != expected || actual != oracle)
                throw new InvalidOperationException(name + ": actual production readback/original SQL oracle differs.");
            checks.Add(name);
        }
        await AssertMatch(true, "exact-keyed-null-duplicate-quoted-baseline");
        using (var destination = Open(copy)) Execute(destination, "UPDATE keyed SET data=X'ff' WHERE a='one';");
        await AssertMatch(false, "changed-blob-rejected");
        using (var destination = Open(copy)) Execute(destination, "UPDATE keyed SET data=X'000102' WHERE a='one'; UPDATE keyed SET n=8 WHERE a='two';");
        await AssertMatch(false, "changed-number-rejected");
        using (var destination = Open(copy)) Execute(destination, "UPDATE keyed SET n=1.25 WHERE a='two'; DELETE FROM keyed WHERE a='three';");
        await AssertMatch(false, "missing-row-rejected");
        using (var destination = Open(copy)) Execute(destination, "INSERT INTO keyed VALUES('three',3,X'',0.0); INSERT INTO keyed VALUES('extra',4,NULL,NULL);");
        await AssertMatch(false, "extra-row-rejected");
        using (var destination = Open(copy)) Execute(destination, "DELETE FROM keyed WHERE a='extra'; UPDATE nullable SET data='changed' WHERE a IS NULL AND data='first';");
        await AssertMatch(false, "nullable-key-fallback-rejects-change");
        using (var destination = Open(copy)) Execute(destination, "UPDATE nullable SET data='first' WHERE data='changed'; UPDATE rowless SET data='different' WHERE a=2;");
        await AssertMatch(false, "keyless-fallback-rejects-change");
        using (var destination = Open(copy)) Execute(destination, "UPDATE rowless SET data=NULL WHERE a=2; UPDATE collated SET data='mixedcase'; UPDATE numeric SET data=1.0;");
        await AssertMatch(true, "original-collation-numeric-and-null-semantics-preserved");
        if (Hash(source) != sourceHash) throw new InvalidOperationException("Readback changed its source fixture.");
        checks.Add("source-bytes-unchanged");

        // Exercise cancellation inside the actual production comparator's native
        // EXCEPT VM, not a pre-cancelled call or a fake input/runtime.
        foreach (var path in new[] { source, copy })
        {
            using var database = Open(path);
            Execute(database, """
                CREATE TABLE z_stress(n INTEGER,data TEXT);
                WITH RECURSIVE seq(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM seq WHERE n<200000)
                INSERT INTO z_stress SELECT n,printf('retained-%08d',n) FROM seq;
                """);
        }
        using var deadline = new CancellationTokenSource();
        var observedNativeStatement = false;
        var watch = Stopwatch.StartNew();
        try
        {
            await MatchAsync(source, copy, deadline.Token, message =>
            {
                if (!message.StartsWith("Recovery readback: comparing z_stress ", StringComparison.Ordinal)) return;
                observedNativeStatement = true;
                deadline.CancelAfter(TimeSpan.FromMilliseconds(5));
            });
            throw new InvalidOperationException("The actual native readback ignored cancellation.");
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        {
            watch.Stop();
            if (!observedNativeStatement || error.InnerException is not SqliteException { SqliteErrorCode: 9 } ||
                watch.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("Cancellation did not interrupt the production SQLite VM within the bound.");
            checks.Add("native-vm-interruption-and-bounded-cancellation");
        }
        await AssertMatch(true, "fresh-real-readback-works-after-cancellation");
        Console.WriteLine(JsonSerializer.Serialize(new { passed = checks, cancellationSeconds = watch.Elapsed.TotalSeconds,
            providerEffects = 0, source, copy }));
    }

    private static Task<bool> MatchAsync(string source, string copy, CancellationToken token, Action<string>? progress = null)
    {
        var method = typeof(SyncAuthorityDatabase).GetMethod("RollbackContentsMatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("The actual production readback is absent.");
        return (Task<bool>)method.Invoke(new SyncAuthorityDatabase(source), [copy, token, progress])!;
    }

    private static bool Oracle(string source, string copy)
    {
        using var original = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        original.Open();
        using (var attach = original.CreateCommand())
        { attach.CommandText = "PRAGMA query_only=ON; ATTACH DATABASE $copy AS retained;"; attach.Parameters.AddWithValue("$copy", copy); attach.ExecuteNonQuery(); }
        var tables = new List<string>();
        using (var names = original.CreateCommand())
        {
            names.CommandText = "SELECT name FROM main.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            using var reader = names.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        foreach (var name in tables)
        {
            var quoted = "\"" + name.Replace("\"", "\"\"") + "\"";
            using var compare = original.CreateCommand();
            compare.CommandText = $"SELECT (SELECT count(*) FROM main.{quoted})=(SELECT count(*) FROM retained.{quoted}) " +
                $"AND NOT EXISTS(SELECT * FROM main.{quoted} EXCEPT SELECT * FROM retained.{quoted}) " +
                $"AND NOT EXISTS(SELECT * FROM retained.{quoted} EXCEPT SELECT * FROM main.{quoted});";
            if (Convert.ToInt64(compare.ExecuteScalar()) != 1) return false;
        }
        return true;
    }

    private static SqliteConnection Open(string path)
    { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); connection.Open(); return connection; }
    private static void Execute(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
