using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

internal static class ColdCommandAdmissionChecks
{
    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh cold-admission fixture.");
        foreach (var mode in new[] { "success", "external-during-identity", "external-after-identity", "changed-schema",
                     "pending-recovery", "cancel-before", "cancel-after-identity", "cancel-after-profile", "profile-collision",
                     "corrupt-relationships", "corrupt-structure", "replacement", "legacy-became-pending" })
        {
            var path = Path.Combine(root, mode, "authority.db");
            var seeded = new SyncAuthorityDatabase(path);
            await seeded.InitializeAsync("cold-installation", "old-profile", "old-revision", "{}");
            var db = new SyncAuthorityDatabase(path);
            if (mode == "pending-recovery")
            {
                await seeded.CreateCommandRequestAsync("interrupted", "run_all", null, null, null, "{}");
                Require(await db.InspectColdCommandEligibilityAsync(default) is null,
                    "pending recovery refuses the shortcut before health or candidate writes");
                continue;
            }
            if (mode == "profile-collision")
                await Sql(path, "INSERT INTO capability_profile VALUES('new-profile','another-revision','{}','2026-01-01');");
            if (mode == "corrupt-relationships")
            {
                await seeded.RegisterPairAsync("pair", 1);
                await Sql(path, "PRAGMA foreign_keys=OFF; INSERT INTO root_binding VALUES('broken-root','missing-pair','local',1,'{}','fixture','blocked','2026-01-01');");
            }
            using var preparation = db.BeginColdCommandPreparation(await db.InspectColdCommandEligibilityAsync(default));
            Require(preparation is not null, mode + " has tentative preparation, not a healthy flag");
            var writeRejected = false;
            try { await db.CreateCommandRequestAsync("forbidden", "run_all", null, null, null, "{}"); }
            catch (InvalidOperationException) { writeRejected = true; }
            Require(writeRejected && await Count(path, "command_request") == 0,
                mode + " tentative scope rejects real write entry point without effects");
            if (mode == "corrupt-structure")
            {
                // Damage one otherwise-unread b-tree in this disposable fixture,
                // not its schema or admission rows. Explicit diagnostics, rather
                // than ordinary command admission, owns the full quick_check.
                await Sql(path, "PRAGMA wal_checkpoint(TRUNCATE);");
                var page = await Scalar(path, "SELECT rootpage FROM sqlite_schema WHERE name='operation_attempt';");
                var pageSize = await Scalar(path, "PRAGMA page_size;");
                Require(page > 1 && pageSize >= 512, "fixture corruption targets the exact operation-attempt b-tree");
                using var bytes = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                bytes.Position = (page - 1) * pageSize;
                bytes.WriteByte(0xff);
                bytes.Flush(flushToDisk: true);
            }
            var checks = 0;
            var structuralCheckStarted = false;
            var injected = false;
            var startupEligible = true;
            using var cancel = new CancellationTokenSource();
            if (mode == "cancel-before") cancel.Cancel();
            SyncAuthorityDatabase.HealthCheckCompletedForTest = _ => checks++;
            SyncAuthorityDatabase.ReadOnlyHealthConnectionForTest = connection =>
            {
                Require(SQLitePCL.raw.sqlite3_db_readonly(connection.Handle!, "main") == 1,
                    "health uses a native read-only handle");
            };
            SyncAuthorityDatabase.ColdCommandAdmissionBoundaryForTest = point =>
            {
                if (point == "schema-identity-checked")
                {
                    if (mode == "external-after-identity") { Sql(path, "UPDATE installation SET created_utc='external';").GetAwaiter().GetResult(); injected = true; }
                    if (mode == "changed-schema") { Sql(path, "PRAGMA user_version=999;").GetAwaiter().GetResult(); injected = true; }
                    if (mode == "cancel-after-identity") cancel.Cancel();
                    if (mode == "legacy-became-pending") startupEligible = false;
                    if (mode == "replacement")
                    {
                        try { File.Move(path, path + ".replaced"); throw new InvalidOperationException("Held database could be replaced."); }
                        catch (IOException) { injected = true; }
                        cancel.Cancel();
                    }
                }
                if (point == "profile-recorded-before-command" && mode == "cancel-after-profile") cancel.Cancel();
            };
            var admitted = false;
            try
            {
                // A cold admission carries its own single-use ticket: the strict core refuses a bare
                // identity that has no matching reservation, and the ticket keeps the published identity.
                var coldTicket = SyncAuthorityCommandReservation.Mint();
                admitted = await db.TryAdmitColdCommandAsync(coldTicket.CommandId, "run_all", null, null, "{}",
                    "new-profile", "new-revision", "{}", () => startupEligible, cancel.Token, message =>
                    {
                        if (message.Contains("running SQLite PRAGMA quick_check", StringComparison.Ordinal))
                            structuralCheckStarted = true;
                        if (mode == "external-during-identity" && !injected && message.Contains("checking current schema", StringComparison.Ordinal))
                        {
                            Sql(path, "UPDATE installation SET created_utc='during-health';").GetAwaiter().GetResult();
                            injected = true;
                        }
                    }, reservation: coldTicket);
            }
            catch (Exception error) when (mode != "success" && error is OperationCanceledException or InvalidDataException or SqliteException)
            { Console.WriteLine("REJECT " + mode + ": " + error.GetType().Name); }
            finally
            {
                SyncAuthorityDatabase.HealthCheckCompletedForTest = null;
                SyncAuthorityDatabase.ReadOnlyHealthConnectionForTest = null;
                SyncAuthorityDatabase.ColdCommandAdmissionBoundaryForTest = null;
            }
            Require(admitted == (mode is "success" or "corrupt-relationships" or "corrupt-structure"),
                mode + " admission checks only schema, identity and command rows");
            Require(await Count(path, "command_request") == (admitted ? 1 : 0), mode + " leaves no rejected command writes");
            var profileCount = await Scalar(path, "SELECT count(*) FROM capability_profile WHERE capability_profile_id='new-profile' AND source_revision='new-revision';");
            Require(profileCount == (admitted ? 1 : 0), mode + " profile/command registration is atomic");
            Require(!structuralCheckStarted && checks == 0,
                mode + " ordinary command admission never scans all retained pages or relationships");
            if (mode is "corrupt-relationships" or "corrupt-structure")
            {
                var diagnosticRejected = false;
                try { await db.VerifyHealthAsync(); }
                catch (Exception error) when (error is InvalidDataException or SqliteException)
                { diagnosticRejected = true; }
                Require(diagnosticRejected, mode + " is rejected by explicit full diagnostics");
            }
            if (mode == "success")
            {
                Require(await Scalar(path, "SELECT count(*) FROM command_request WHERE status='accepted';") == 1,
                    "accepted transition committed with profile");
            }
            if (mode is "external-during-identity" or "external-after-identity" or "replacement")
                Require(injected, mode + " exercised its decisive concurrent boundary");
        }
    }

    private static async Task Sql(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private static Task<long> Count(string path, string table) => Scalar(path, "SELECT count(*) FROM " + table + ";");
    private static async Task<long> Scalar(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
}
