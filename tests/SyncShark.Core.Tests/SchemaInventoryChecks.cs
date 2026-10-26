using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class SchemaInventoryChecks
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var db = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        await db.InitializeAsync("inventory-installation", "inventory-profile", "inventory-fixture", "{}");
        async Task Sql(string sql)
        {
            await using var connection = new SqliteConnection("Data Source=" + db.DatabasePath + ";Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        async Task Refused(Func<Task> action, string detail)
        {
            try { await action(); }
            catch (InvalidDataException error) when (error.Message.Contains("object inventory", StringComparison.Ordinal))
            { Console.WriteLine("PASS " + detail); return; }
            throw new InvalidDataException("Changed schema was accepted: " + detail);
        }
        await using (db.BeginExecutionConnectionScope())
        {
            if (await db.QuickCheckAsync() != "ok") throw new InvalidDataException("Fresh schema failed.");
            await db.RegisterPairAsync("before-change", 1);
            await db.RegisterRootBindingAsync("local-root","before-change","local",1,
                """{"volumeSerial":"00000001","fileId":"root"}""",root);
            await db.RegisterRootBindingAsync("remote-root","before-change","dropbox",1,
                """{"folderId":"id:root"}""","/Fixture");
            var admission=await db.CreateAcceptedCommandAdmissionAsync(null,"run_pair","before-change",null,
                null,"{}",SyncAuthorityCommandPurpose.NormalEffect);
            await db.TransitionCommandRequestAsync(admission.CommandRequestId,"executing");
            await db.ReserveActionAsync("first-read",admission);
            await db.ReserveActionAsync("second-read",admission);
            await db.RegisterPairAsync("warm-inventory", 1);
            await db.RegisterPairAsync("reuse-inventory", 1);
            // The held scope must check the actual schema on every checkout.
            await Sql("CREATE INDEX \"fixture-\"\"extra\" ON sync_pair(state);");
            await Refused(async () => { await db.ReserveActionAsync("changed-read",admission); },
                "window reservation reads reject a changed schema after successful reads");
        }
        await using (db.BeginConnectionReuseScope())
        {
            await Refused(() => db.RegisterPairAsync("must-not-write", 1),
                "a changed schema is refused before a write in a reused command scope");
            await Sql("DROP INDEX \"fixture-\"\"extra\";");
            if (await db.QuickCheckAsync() != "ok") throw new InvalidDataException("Restored schema failed.");
            string indexName, indexSql;
            await using (var connection = new SqliteConnection("Data Source=" + db.DatabasePath + ";Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT name,sql FROM sqlite_schema WHERE type='index' AND sql IS NOT NULL ORDER BY name LIMIT 1;";
                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) throw new InvalidDataException("No explicit index to exercise.");
                indexName = reader.GetString(0); indexSql = reader.GetString(1);
            }
            await Sql("DROP INDEX \"" + indexName.Replace("\"", "\"\"") + "\";");
            await Refused(async () => { await db.QuickCheckAsync(); },
                "a missing expected object is refused after a prior successful checkout");
            await Sql(indexSql);
            await db.RegisterPairAsync("after-restore", 1);
        }
        await db.VerifyHealthAsync();
        await using var read = new SqliteConnection("Data Source=" + db.DatabasePath + ";Mode=ReadOnly;Pooling=False");
        await read.OpenAsync();
        await using var query = read.CreateCommand();
        query.CommandText = "SELECT count(*) FROM sync_pair WHERE pair_id='must-not-write';";
        if (Convert.ToInt64(await query.ExecuteScalarAsync()) != 0)
            throw new InvalidDataException("A refused schema allowed a write.");
        Console.WriteLine("PASS restored exact schema works; refused write left no authority row");
        await query.DisposeAsync();
        await read.DisposeAsync();
        using var exclusive=File.Open(db.DatabasePath,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        Console.WriteLine("PASS drained outer scope releases database handles");
    }
}
