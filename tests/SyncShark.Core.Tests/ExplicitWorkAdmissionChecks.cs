using System.IO;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

// ADM proof harness for the explicit work-admission lane (schema 11).
//
// It exercises only the real production entry points of the lane: the schema-11 object and column
// inventory, the version 10 to 11 source migration and its retained rollback copy, the monotonic
// non-reused admission sequence with its exact installation generation and purpose, the ordinary closed
// command floor, the single-use pending-identity reservation, retained operation adoption, and the
// ordinary versus reconciliation-only lease dispatch binding. It creates no provider session, touches no
// original database and performs no file effect: every fixture is a disposable local SQLite file.
internal static class ExplicitWorkAdmissionChecks
{
    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh explicit-work-admission fixture.");
        Directory.CreateDirectory(root);
        var assertions = 0;
        assertions += await FreshSchemaAsync(root);
        assertions += await MigrationFromVersion10Async(root);
        assertions += await SequenceAndPurposeAsync(root);
        assertions += await ReservationClaimAsync(root);
        assertions += await ClosedFloorAsync(root);
        assertions += await RetainedOperationOwnershipAsync(root);
        assertions += await LeaseDispatchBindingAsync(root);
        assertions += await ScopeAndDispatchEnforcementAsync(root);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            metric = "explicit_work_admission_assertions",
            assertions,
            schemaVersion = SyncAuthoritySchema.Version
        }));
    }

    // ----- ADM-L01 schema 11 object and column inventory ------------------------------------------

    private static async Task<int> FreshSchemaAsync(string root)
    {
        var assertions = 0;
        var path = Path.Combine(root, "fresh", "authority.db");
        var db = new SyncAuthorityDatabase(path);
        await db.InitializeAsync("explicit-admission-install", "explicit-admission-profile", "fixture", "{}");
        Require(SyncAuthoritySchema.Version >= 11, "the current schema retains explicit admission"); assertions++;
        Require(await Scalar(path, "PRAGMA user_version;") == SyncAuthoritySchema.Version, "a fresh database records the current schema version");
        assertions++;
        Require(await Scalar(path, "SELECT count(*) FROM schema_migration WHERE schema_version=" + SyncAuthoritySchema.Version + ";") == 1,
            "the current schema receipt is retained exactly once"); assertions++;
        Require(await Scalar(path, "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name IN " +
                "('command_request_admission_sequence','operation_origin_command','operation_execution_command');") == 3,
            "the three version 11 indexes exist"); assertions++;
        Require(await Scalar(path, "SELECT count(*) FROM sqlite_schema WHERE type='trigger' AND name IN " +
                "('operation_origin_command_immutable','lease_admission_authority_immutable');") == 2,
            "the two version 11 immutability triggers exist"); assertions++;
        Require(await ColumnExists(path, "command_request", "admission_generation") &&
                await ColumnExists(path, "command_request", "admission_purpose") &&
                await ColumnExists(path, "command_request", "admission_sequence"),
            "command_request carries the three nullable admission columns"); assertions++;
        Require(await ColumnExists(path, "installation", "next_command_sequence") &&
                await ColumnExists(path, "installation", "ordinary_closed_command_floor"),
            "installation carries the two admission counters"); assertions++;
        Require(await ColumnExists(path, "operation", "origin_command_id") &&
                await ColumnExists(path, "operation", "execution_command_id"),
            "operation carries the immutable origin and the execution command references"); assertions++;
        Require(await ColumnExists(path, "lease", "command_request_id") &&
                await ColumnExists(path, "lease", "dispatch_allowed"),
            "lease carries the command reference and the checked dispatch_allowed flag"); assertions++;
        Require(await Scalar(path, "SELECT count(*) FROM installation WHERE next_command_sequence=1 " +
                "AND ordinary_closed_command_floor=0;") == 1,
            "a fresh installation starts its ordinary frontier at generation-1 sequence 1 and floor 0");
        assertions++;
        return assertions;
    }

    // ----- ADM-L01/L05 version 10 to 11 migration ------------------------------------------------

    private static async Task<int> MigrationFromVersion10Async(string root)
    {
        var assertions = 0;
        var path = Path.Combine(root, "migration", "authority.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var fixture = new SqliteConnection(new SqliteConnectionStringBuilder
                     { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        {
            await fixture.OpenAsync();
            await ExecuteAsync(fixture, SyncAuthoritySchema.Sql + "\n" + SyncAuthoritySchemaSupplement.Sql);
            foreach (var migration in new[]
                     {
                         SyncAuthoritySchemaMigration3.Sql, SyncAuthoritySchemaMigration4.Sql,
                         SyncAuthoritySchemaMigration5.Sql, SyncAuthoritySchemaMigration6.Sql,
                         SyncAuthoritySchemaMigration7.Sql, SyncAuthoritySchemaMigration8.Sql,
                         SyncAuthoritySchemaMigration9.Sql, SyncAuthoritySchemaMigration10.Sql
                     })
                await ExecuteAsync(fixture, migration);
            await ExecuteAsync(fixture,
                "INSERT INTO command_request(command_request_id,command_kind,payload_json,requested_utc,status) " +
                "VALUES('legacy-retained-command','run_all','{}','2026-01-01T00:00:00.0000000Z','completed');");
            await ExecuteAsync(fixture,
                "INSERT INTO schema_migration(schema_version,applied_utc,source_revision) " +
                "VALUES(10,'2026-01-01T00:00:00.0000000Z','fixture-v10');");
            await ExecuteAsync(fixture, "INSERT INTO installation(installation_id,database_generation,created_utc) " +
                "VALUES('explicit-admission-install','explicit-v10-generation','2026-01-01T00:00:00.0000000Z');");
            await ExecuteAsync(fixture, "PRAGMA user_version=10;");
        }
        var database = new SyncAuthorityDatabase(path);
        await database.InitializeAsync("explicit-admission-install", "explicit-admission-profile", "fixture", "{}");
        Require(await Scalar(path, "PRAGMA user_version;") == SyncAuthoritySchema.Version, "the version 10 fixture migrates to the current schema");
        assertions++;
        Require(await Scalar(path, "SELECT count(*) FROM schema_migration WHERE schema_version IN (10," + SyncAuthoritySchema.Version + ");") == 2,
            "the current receipt is added beside the retained version 10 receipt"); assertions++;
        Require(File.Exists(path + ".v10-rollback"),
            "the version 10 rollback copy is retained before the migration"); assertions++;
        Require(await Scalar(path, "SELECT count(*) FROM command_request WHERE " +
                "command_request_id='legacy-retained-command' AND admission_generation IS NULL " +
                "AND admission_purpose IS NULL AND admission_sequence IS NULL;") == 1,
            "the migrated legacy command keeps its exact bytes and gains no admission"); assertions++;
        Require(await Scalar(path, "SELECT count(*) FROM installation WHERE ordinary_closed_command_floor=0;") == 1,
            "the migrated installation keeps an open ordinary frontier"); assertions++;
        var reopened = new SyncAuthorityDatabase(path);
        await reopened.InitializeAsync("explicit-admission-install", "explicit-admission-profile", "fixture", "{}");
        Require(await Scalar(path, "SELECT count(*) FROM schema_migration WHERE schema_version=" + SyncAuthoritySchema.Version + ";") == 1,
            "reopening adds no second current receipt"); assertions++;
        return assertions;
    }

    // ----- ADM-L01 monotonic sequence, exact generation, narrow purpose ---------------------------

    private static async Task<int> SequenceAndPurposeAsync(string root)
    {
        var assertions = 0;
        var path = Path.Combine(root, "sequence", "authority.db");
        var db = new SyncAuthorityDatabase(path);
        await db.InitializeAsync("explicit-admission-install", "explicit-admission-profile", "fixture", "{}");
        var generation = await ScalarString(path, "SELECT database_generation FROM installation;");
        var first = await db.CreateAcceptedCommandAdmissionAsync(null, "run_all", null, null, null, "{}",
            SyncAuthorityCommandPurpose.NormalEffect);
        var second = await db.CreateAcceptedCommandAdmissionAsync(null, "reconcile", null, null, null, "{}",
            SyncAuthorityCommandPurpose.NormalEffect);
        var third = await db.CreateAcceptedCommandAdmissionAsync(null, "run_all", null, null, null, "{}",
            SyncAuthorityCommandPurpose.ReadOnly);
        Require(second.AdmissionSequence == first.AdmissionSequence + 1 &&
                third.AdmissionSequence == second.AdmissionSequence + 1,
            "each accepted command allocates the next exact admission sequence"); assertions++;
        Require(first.AdmissionSequence == 1 && await Scalar(path,
                "SELECT next_command_sequence FROM installation;") == 4,
            "the installation counter advances exactly once per admitted command"); assertions++;
        Require(first.DatabaseGeneration == generation && second.DatabaseGeneration == generation &&
                third.DatabaseGeneration == generation,
            "every admission is stamped with the exact current installation generation"); assertions++;
        Require(await Scalar(path, "SELECT count(*) FROM (SELECT admission_generation,admission_sequence " +
                "FROM command_request GROUP BY admission_generation,admission_sequence HAVING count(*)>1);") == 0,
            "no two accepted commands share an admission position"); assertions++;
        Require(third.Purpose == SyncAuthorityCommandPurpose.ReadOnly && await ScalarString(path,
                "SELECT admission_purpose FROM command_request WHERE command_request_id='" + third.CommandRequestId + "';")
                == "read_only",
            "the narrow purpose is bound into the retained command row, never read from request JSON");
        assertions++;
        Require(await ScalarString(path, "SELECT status FROM command_request WHERE command_request_id='" +
                first.CommandRequestId + "';") == "accepted",
            "creation and acceptance commit as one atomic transaction"); assertions++;
        var purposeRejected = false;
        try { await Sql(path, "INSERT INTO command_request(command_request_id,command_kind,payload_json," +
            "requested_utc,status,admission_purpose) VALUES('bad-purpose','run_all','{}'," +
            "'2026-01-01T00:00:00.0000000Z','accepted','bogus');"); }
        catch (SqliteException) { purposeRejected = true; }
        Require(purposeRejected, "the schema itself refuses an unsupported admission purpose"); assertions++;
        return assertions;
    }

    // ----- ADM-L01 single-use pending identity reservation ---------------------------------------

    private static async Task<int> ReservationClaimAsync(string root)
    {
        var assertions = 0;
        var path = Path.Combine(root, "reservation", "authority.db");
        var db = new SyncAuthorityDatabase(path);
        await db.InitializeAsync("explicit-admission-install", "explicit-admission-profile", "fixture", "{}");
        var ticket = SyncAuthorityCommandReservation.Mint();
        Require(ticket.CommandId.StartsWith("command-", StringComparison.Ordinal) && ticket.CommandId.Length == 40,
            "the minted pending identity keeps the established command-<Guid> display format"); assertions++;
        var admission = await db.CreateAcceptedCommandAdmissionAsync(ticket, "run_all", null, null, null, "{}",
            SyncAuthorityCommandPurpose.NormalEffect);
        Require(admission.CommandRequestId == ticket.CommandId,
            "the accepted command keeps the exact reserved pending identity"); assertions++;
        var changedPayloadRejected = false;
        try
        {
            await db.CreateAcceptedCommandAdmissionAsync(ticket, "run_all", null, null, null, "{\"different\":1}",
                SyncAuthorityCommandPurpose.NormalEffect);
        }
        catch (InvalidOperationException) { changedPayloadRejected = true; }
        Require(changedPayloadRejected,
            "a repeated use of one reservation with a different payload is refused after exact identity checks");
        assertions++;
        var identicalReuse = await db.CreateAcceptedCommandAdmissionAsync(ticket, "run_all", null, null, null, "{}",
            SyncAuthorityCommandPurpose.NormalEffect);
        Require(identicalReuse.AdmissionSequence == admission.AdmissionSequence &&
                await Scalar(path, "SELECT count(*) FROM command_request;") == 1,
            "an exact repeated use resolves the retained row instead of inserting a second command");
        assertions++;
        await db.TransitionCommandRequestAsync(ticket.CommandId, "executing");
        var executingReuse = await db.CreateAcceptedCommandAdmissionAsync(ticket, "run_all", null, null, null, "{}",
            SyncAuthorityCommandPurpose.NormalEffect);
        Require(executingReuse == admission && await ScalarString(path,
                "SELECT status FROM command_request WHERE command_request_id='" + ticket.CommandId + "';") == "executing",
            "exact replay preserves an executing command without a second acceptance"); assertions++;
        await db.TransitionCommandRequestAsync(ticket.CommandId, "completed");
        var terminalReplayRefused = false;
        try { await db.CreateAcceptedCommandAdmissionAsync(ticket, "run_all", null, null, null, "{}",
            SyncAuthorityCommandPurpose.NormalEffect); }
        catch (InvalidOperationException) { terminalReplayRefused = true; }
        Require(terminalReplayRefused && await Scalar(path, "SELECT next_command_sequence FROM installation;") == 2,
            "terminal replay neither revives the command nor consumes another admission position"); assertions++;
        await Sql(path, "DELETE FROM command_request WHERE command_request_id='" + ticket.CommandId + "';");
        var afterAbsenceRejected = false;
        try
        {
            await db.CreateAcceptedCommandAdmissionAsync(ticket, "run_all", null, null, null, "{}",
                SyncAuthorityCommandPurpose.NormalEffect);
        }
        catch (InvalidOperationException) { afterAbsenceRejected = true; }
        Require(afterAbsenceRejected && await Scalar(path, "SELECT count(*) FROM command_request;") == 0,
            "a consumed reservation never creates a missing row, so a rolled-back first attempt stays unminted");
        assertions++;
        var fresh = await db.CreateAcceptedCommandAdmissionAsync(SyncAuthorityCommandReservation.Mint(), "run_all",
            null, null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        Require(fresh.AdmissionSequence == 2,
            "deleting a committed fixture row never reuses its consumed admission sequence"); assertions++;
        return assertions;
    }

    // ----- ADM-L01 ordinary closed floor ----------------------------------------------------------

    private static async Task<int> ClosedFloorAsync(string root)
    {
        var assertions = 0;
        var path = Path.Combine(root, "floor", "authority.db");
        var db = new SyncAuthorityDatabase(path);
        await db.InitializeAsync("explicit-admission-install", "explicit-admission-profile", "fixture", "{}");
        var accepted = await db.CreateAcceptedCommandAdmissionAsync(null, "run_all", null, null, null, "{}",
            SyncAuthorityCommandPurpose.NormalEffect);
        // The floor is advanced only here, inside this disposable fixture, after the retained command is
        // terminal and no worker or lease can issue; production floor advancement stays follow-on work.
        await Sql(path, "UPDATE command_request SET status='completed',terminal_utc='2026-01-01T00:00:00.0000000Z' " +
            "WHERE command_request_id='" + accepted.CommandRequestId + "';");
        await Sql(path, "UPDATE installation SET ordinary_closed_command_floor=next_command_sequence;");
        var closedRejected = false;
        try
        {
            await db.CreateAcceptedCommandAdmissionAsync(null, "run_all", null, null, null, "{}",
                SyncAuthorityCommandPurpose.NormalEffect);
        }
        catch (InvalidOperationException) { closedRejected = true; }
        Require(closedRejected, "an ordinary closed floor refuses the next new admission"); assertions++;
        var terminalRejected = false;
        try
        {
            await db.AuthorizeRetainedOperationsAsync(accepted, new[] { "operation-not-retained" });
        }
        catch (InvalidOperationException) { terminalRejected = true; }
        Require(terminalRejected,
            "a retained terminal command with a closed position is no permission for new effect work");
        assertions++;
        return assertions;
    }

    // ----- ADM-L02 retained operation origin and execution ownership ------------------------------

    private static async Task<int> RetainedOperationOwnershipAsync(string root)
    {
        var assertions = 0;
        var path = Path.Combine(root, "ownership", "authority.db");
        var db = new SyncAuthorityDatabase(path);
        await db.InitializeAsync("explicit-admission-install", "explicit-admission-profile", "fixture", "{}");
        var operationId = await SeedRetainedOperationAsync(db, root, "ownership", SyncAuthorityOperationKind.Cleanup);
        var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "reconcile", "pair-ownership", null, null, "{}",
            SyncAuthorityCommandPurpose.NormalEffect);
        var adopted = await db.AuthorizeRetainedOperationsAsync(admission, new[] { operationId });
        Require(adopted.Count == 1 && await ScalarString(path,
                "SELECT execution_command_id FROM operation WHERE operation_id='" + operationId + "';") == admission.CommandRequestId,
            "the exact selected retained operation is adopted by the accepted command"); assertions++;
        Require(await Scalar(path, "SELECT count(*) FROM operation WHERE operation_id='" + operationId + "' " +
                "AND origin_command_id IS NULL;") == 1,
            "adoption moves the execution owner only and leaves the immutable origin unchanged");
        assertions++;
        var foreignRejected = false;
        var secondCommand = await db.CreateAcceptedCommandAdmissionAsync(null, "reconcile", "pair-ownership", null, null, "{}",
            SyncAuthorityCommandPurpose.NormalEffect);
        try { await db.AuthorizeRetainedOperationsAsync(secondCommand, new[] { operationId }); }
        catch (InvalidOperationException) { foreignRejected = true; }
        Require(foreignRejected && await ScalarString(path,
                "SELECT execution_command_id FROM operation WHERE operation_id='" + operationId + "';") == admission.CommandRequestId,
            "a second live command cannot take execution ownership of an already adopted operation");
        assertions++;
        var readOnlyRejected = false;
        var readOnly = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", "pair-ownership", null, null, "{}",
            SyncAuthorityCommandPurpose.ReadOnly);
        try { await db.AuthorizeRetainedOperationsAsync(readOnly, new[] { operationId }); }
        catch (InvalidOperationException) { readOnlyRejected = true; }
        Require(readOnlyRejected,
            "a read_only admission cannot adopt retained work or authorize any effect"); assertions++;
        var staleRejected = false;
        try
        {
            await db.AuthorizeRetainedOperationsAsync(admission with { AdmissionSequence = 999999 },
                new[] { operationId });
        }
        catch (InvalidOperationException) { staleRejected = true; }
        Require(staleRejected,
            "a presented admission whose generation or sequence does not match the retained row is refused");
        assertions++;
        var lease = await db.AcquireOperationLeaseAsync(operationId, "worker", TimeSpan.FromMinutes(5));
        var leasedRejected = false;
        try { await db.AuthorizeRetainedOperationsAsync(secondCommand, new[] { operationId }); }
        catch (InvalidOperationException) { leasedRejected = true; }
        Require(leasedRejected, "an operation holding a nonexpired active lease cannot be adopted differently");
        assertions++;
        Require(await Scalar(path, "SELECT count(*) FROM lease WHERE lease_id='" + lease.LeaseId + "' " +
                "AND dispatch_allowed=1 AND command_request_id='" + admission.CommandRequestId + "';") == 1,
            "an ordinary lease of an adopted operation is bound to its execution command with dispatch allowed");
        assertions++;
        await db.ReleaseLeaseAsync(lease, CancellationToken.None);
        var originImmutableRejected = false;
        try
        {
            await Sql(path, "UPDATE operation SET origin_command_id='" + admission.CommandRequestId + "' " +
                "WHERE operation_id='" + operationId + "';");
        }
        catch (SqliteException) { originImmutableRejected = true; }
        Require(originImmutableRejected, "the schema makes the originating command write-once"); assertions++;
        return assertions;
    }

    // ----- ADM-L03 ordinary versus reconciliation-only lease permission ---------------------------

    private static async Task<int> LeaseDispatchBindingAsync(string root)
    {
        var assertions = 0;
        var path = Path.Combine(root, "lease", "authority.db");
        var db = new SyncAuthorityDatabase(path);
        await db.InitializeAsync("explicit-admission-install", "explicit-admission-profile", "fixture", "{}");
        var operationId = await SeedRetainedOperationAsync(db, root, "lease", SyncAuthorityOperationKind.Cleanup);
        // ADM-L03: the ordinary path now REQUIRES the operation's exact current scoped admission and refuses
        // before any lease, fence or current-owner mutation instead of recording a no-dispatch lease.
        var unboundRefused = false;
        try { await db.AcquireOperationLeaseAsync(operationId, "worker", TimeSpan.FromMinutes(5)); }
        catch (InvalidOperationException) { unboundRefused = true; }
        Require(unboundRefused && await Scalar(path, "SELECT count(*) FROM lease WHERE scope_kind='operation' " +
                "AND scope_id='" + operationId + "';") == 0,
            "an operation with no execution command is refused an ordinary lease instead of receiving a no-dispatch grant");
        assertions++;
        var reconciliationLease = await db.AcquireReconciliationOperationLeaseAsync(operationId, "worker",
            TimeSpan.FromMinutes(5));
        Require(await Scalar(path, "SELECT count(*) FROM lease WHERE lease_id='" + reconciliationLease.LeaseId + "' " +
                "AND dispatch_allowed=0 AND command_request_id IS NULL;") == 1,
            "a reconciliation-only lease is recorded with no dispatch authority and no execution owner");
        assertions++;
        await db.ReleaseLeaseAsync(reconciliationLease, CancellationToken.None);
        var ineligible = await SeedRetainedOperationAsync(db, root, "lease-plain", SyncAuthorityOperationKind.CreateLocal);
        var refused = false;
        try
        {
            await db.AcquireReconciliationOperationLeaseAsync(ineligible, "worker", TimeSpan.FromMinutes(5));
        }
        catch (InvalidOperationException) { refused = true; }
        Require(refused,
            "a reconciliation-only lease is refused for an operation with no issued attempt and no cleanup or verification scope");
        assertions++;
        var immutableRefused = false;
        try { await Sql(path, "UPDATE lease SET dispatch_allowed=1 WHERE lease_id='" + reconciliationLease.LeaseId + "';"); }
        catch (SqliteException) { immutableRefused = true; }
        Require(immutableRefused,
            "the schema makes the recorded lease dispatch authority immutable, so a renewal or receipt cannot raise it");
        assertions++;
        return assertions;
    }

    // ----- ADM-L03/I11 shared scope predicate through the real lease and request_sent boundary -------

    private static async Task<int> ScopeAndDispatchEnforcementAsync(string root)
    {
        var assertions = 0;
        var path = Path.Combine(root, "scope", "authority.db");
        var db = new SyncAuthorityDatabase(path);
        await db.InitializeAsync("explicit-admission-install", "explicit-admission-profile", "fixture", "{}");
        var operationId = await SeedRetainedOperationAsync(db, root, "scope", SyncAuthorityOperationKind.CreateLocal);
        await db.RegisterPairAsync("pair-elsewhere", 1);
        var wrongPair = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", "pair-elsewhere", null,
            null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        var wrongPairRefused = false;
        try { await db.AuthorizeRetainedOperationsAsync(wrongPair, new[] { operationId }); }
        catch (InvalidOperationException) { wrongPairRefused = true; }
        Require(wrongPairRefused && await ScalarString(path, "SELECT coalesce(execution_command_id,'none') FROM operation " +
                "WHERE operation_id='" + operationId + "';") == "none",
            "a command naming a foreign pair transfers no execution ownership");
        assertions++;
        var controlForEffect = await db.CreateAcceptedCommandAdmissionAsync(null, "reconcile", "pair-scope", "root-scope",
            null, "{}", SyncAuthorityCommandPurpose.ControlAllocation);
        var controlRefused = false;
        try { await db.AuthorizeRetainedOperationsAsync(controlForEffect, new[] { operationId }); }
        catch (InvalidOperationException) { controlRefused = true; }
        Require(controlRefused, "a control-allocation admission never adopts an ordinary effect operation");
        assertions++;
        var readOnlyAdoption = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", "pair-scope", null,
            null, "{}", SyncAuthorityCommandPurpose.ReadOnly);
        var readOnlyRefused = false;
        try { await db.AuthorizeRetainedOperationsAsync(readOnlyAdoption, new[] { operationId }); }
        catch (InvalidOperationException) { readOnlyRefused = true; }
        Require(readOnlyRefused, "a read_only admission never adopts work");
        assertions++;
        var scoped = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", "pair-scope", null, null, "{}",
            SyncAuthorityCommandPurpose.NormalEffect);
        var adopted = await db.AuthorizeRetainedOperationsAsync(scoped, new[] { operationId });
        Require(adopted.Count == 1, "the exact scoped accepted command adopts its own operation");
        assertions++;
        var lease = await db.AcquireOperationLeaseAsync(operationId, "worker", TimeSpan.FromMinutes(5));
        Require(await Scalar(path, "SELECT count(*) FROM lease WHERE lease_id='" + lease.LeaseId +
                "' AND dispatch_allowed=1 AND command_request_id='" + scoped.CommandRequestId + "';") == 1,
            "an operation bound to a current scoped admission receives a dispatching lease");
        assertions++;
        var attempt = await db.StartDispatchedAttemptAsync(operationId, lease, new string('c', 64),
            "local-create-directory");
        Require(await Scalar(path, "SELECT count(*) FROM operation_attempt WHERE operation_id='" + operationId +
                "' AND attempt_id='" + attempt.AttemptId + "' AND status='request_sent';") == 1,
            "the real request_sent boundary issues exactly one attempt under the scoped admission");
        assertions++;
        await db.ReleaseLeaseAsync(lease, CancellationToken.None);
        // Revocation: the accepted command stops before the next issue attempt.
        await db.TransitionCommandRequestAsync(scoped.CommandRequestId, "cancelled");
        var revokedLeaseRefused = false;
        try { await db.AcquireOperationLeaseAsync(operationId, "worker", TimeSpan.FromMinutes(5)); }
        catch (InvalidOperationException) { revokedLeaseRefused = true; }
        Require(revokedLeaseRefused, "a revoked command refuses a new ordinary lease before any fence mutation");
        assertions++;
        Require(await Scalar(path, "SELECT count(*) FROM operation_attempt WHERE operation_id='" + operationId +
                "';") == 1,
            "the revoked admission caused no second request_sent attempt");
        assertions++;
        var reconciliation = await db.AcquireReconciliationOperationLeaseAsync(operationId, "worker",
            TimeSpan.FromMinutes(5));
        Require(await Scalar(path, "SELECT count(*) FROM lease WHERE lease_id='" + reconciliation.LeaseId +
                "' AND dispatch_allowed=0 AND command_request_id IS NULL;") == 1,
            "the interrupted attempt stays reconcilable through a no-dispatch reconciliation lease");
        assertions++;
        var issueUnderReconciliationRefused = false;
        try
        {
            await db.StartDispatchedAttemptAsync(operationId, reconciliation, new string('e', 64),
                "local-create-directory");
        }
        catch (InvalidOperationException) { issueUnderReconciliationRefused = true; }
        catch (SqliteException error) when (error.SqliteErrorCode == 19 &&
            error.Message.Contains("prior attempt must reach verified terminal reconciliation", StringComparison.Ordinal))
        { issueUnderReconciliationRefused = true; }
        Require(issueUnderReconciliationRefused && await Scalar(path,
                "SELECT count(*) FROM operation_attempt WHERE operation_id='" + operationId + "';") == 1,
            "a reconciliation-only lease can never issue a new request_sent attempt");
        assertions++;
        await db.ReleaseLeaseAsync(reconciliation, CancellationToken.None);
        return assertions;
    }

    // ----- shared fixture helpers -----------------------------------------------------------------

    private static async Task<string> SeedRetainedOperationAsync(SyncAuthorityDatabase db, string root,
        string suffix, SyncAuthorityOperationKind kind)
    {
        var pairId = "pair-" + suffix;
        var rootBindingId = "root-" + suffix;
        var objectId = "object-" + suffix;
        var operationId = "operation-" + suffix;
        await db.RegisterPairAsync(pairId, 1);
        await db.RegisterRootBindingAsync(rootBindingId, pairId, "local", 1,
            "{\"fixture\":true}", Path.Combine(root, "roots", suffix));
        await db.RegisterObjectAsync(objectId, "file");
        await db.RegisterObjectVersionAsync("version-" + suffix, objectId, "local",
            "{\"fixtureVersion\":true}", null, null);
        await db.RegisterMembershipAsync("membership-" + suffix, objectId, rootBindingId, "file.txt", 1);
        var member = new SyncAuthorityMember("member-" + suffix, pairId, objectId, "source", rootBindingId,
            "membership-" + suffix, "version-" + suffix, 1, "file.txt", "{}");
        // This is retained pre-admission input, not a new production operation. Seed its legacy
        // nullable ownership explicitly; the real adoption, lease and request_sent decisions below
        // remain unhooked. New operations must use CreateOperationAsync with their caller's admission.
        await using var connection = await OpenAsync(db.DatabasePath);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO operation(operation_id,pair_id,object_id,kind,status,priority,configuration_revision,
                capability_profile_id,idempotency_key,preconditions_json,created_utc)
            VALUES($operation,$pair,$object,$kind,'planned',50,1,'explicit-admission-profile',$key,'{}',$utc);
            INSERT INTO operation_member(operation_member_id,operation_id,pair_id,object_id,role,root_binding_id,
                membership_id,object_version_id,expected_membership_generation,expected_path_key,expected_state_json)
            VALUES($member,$operation,$pair,$object,'source',$root,$membership,$version,1,'file.txt','{}');
            UPDATE operation SET status='ready',ready_utc=$utc WHERE operation_id=$operation;
            """;
        foreach (var (name, value) in new (string, object)[]
        {
            ("$operation", operationId), ("$pair", pairId), ("$object", objectId),
            ("$kind", SyncAuthorityNames.OperationKind(kind)), ("$key", "idempotency-" + suffix),
            ("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            ("$member", member.MemberId), ("$root", rootBindingId),
            ("$membership", "membership-" + suffix), ("$version", "version-" + suffix)
        }) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return operationId;
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Explicit work admission assertion failed: " + description);
        Console.WriteLine("ok " + description);
    }

    private static async Task<bool> ColumnExists(string path, string table, string column)
        => (await QueryAsync(path, "PRAGMA table_info(" + table + ");")).Any(row =>
            string.Equals(row[1], column, StringComparison.Ordinal));

    private static async Task<SqliteConnection> OpenAsync(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;");
        return connection;
    }

    private static async Task Sql(string path, string sql)
    {
        await using var connection = await OpenAsync(path);
        await ExecuteAsync(connection, sql);
    }

    private static async Task<long> Scalar(string path, string sql)
    {
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarString(string path, string sql)
    {
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string[][]> QueryAsync(string path, string sql)
    {
        await using var connection = await OpenAsync(path);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
                values[index] = reader.IsDBNull(index)
                    ? string.Empty
                    : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty;
            rows.Add(values);
        }
        return rows.ToArray();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

