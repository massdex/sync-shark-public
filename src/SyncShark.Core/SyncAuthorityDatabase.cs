using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    private static readonly object BootstrapLock = new();
    private static bool _sqliteBootstrapped;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly string _databasePath;
    private readonly AsyncLocal<ExecutionConnectionScope?> _executionConnectionScope = new();
    // The three planning memos are pure derived values with one shared bounded policy: FIFO eviction to
    // SyncPlanningMemoBudget.MaximumEntries entries and a bounded estimated retained byte total, with an
    // item heavier than the whole byte budget never retained. They are never read as mutable authority:
    // every reservation still reads current pair state, roots, latest complete scan identities and its own
    // object's synchronization point inside its transaction, so eviction only recomputes an identical
    // historical value from exact inputs.
    private readonly SyncPlanningMemoCache<string, string> _planningContentFingerprints =
        new(SyncPlanningMemoWeights.ContentFingerprint);
    private readonly SyncPlanningMemoCache<(string Local, string Remote), IReadOnlySet<string>>
        _completeExcludedPaths = new(SyncPlanningMemoWeights.ExcludedPaths);
    private readonly SyncPlanningMemoCache<(string LocalScan, string RemoteScan, string PairId,
        long Revision, string LocalRoot, string RemoteRoot, string PairState), string>
        _planningStateFingerprints = new(SyncPlanningMemoWeights.PlanningState);
    private static readonly string[] ExpectedVersion2SchemaObjects = BuildExpectedSchemaObjects(
        SyncAuthoritySchema.Sql + "\n" + SyncAuthoritySchemaSupplement.Sql);
    private static readonly string[] ExpectedVersion3SchemaObjects = ExpectedVersion2SchemaObjects
        .Where(item => !string.Equals(item, "trigger:observation_no_update", StringComparison.Ordinal))
        .Concat(BuildExpectedSchemaObjects(SyncAuthoritySchemaMigration3.Sql))
        .OrderBy(item => item, StringComparer.Ordinal)
        .ToArray();
    private static readonly string[] ExpectedVersion4SchemaObjects = ExpectedVersion3SchemaObjects
        .Where(item => item != "index:one_active_recovery_root_per_side")
        .Concat(BuildExpectedSchemaObjects(SyncAuthoritySchemaMigration4.Sql))
        .OrderBy(item => item, StringComparer.Ordinal).ToArray();
    // Version 5 rebuilds provider_account and its sole referencing recovery table in place and
    // recreates the identical recovery indices, so its object inventory is exactly version 4's.
    private static readonly string[] ExpectedVersion5SchemaObjects = ExpectedVersion4SchemaObjects;
    // Version 6 adds one nullable column to recovery_root_binding and recreates the SAME two recovery
    // index names with one added key column, so it changes only index definitions and never the set of
    // schema object names. The current object inventory therefore stays exactly version 4's.
    private static readonly string[] ExpectedVersion6SchemaObjects = ExpectedVersion5SchemaObjects;
    // Version 7 adds key-only read indexes. Retained authority tables, receipts,
    // triggers and constraints are unchanged; the exact index inventory is required.
    private static readonly string[] ExpectedVersion7SchemaObjects = ExpectedVersion6SchemaObjects
        .Concat(BuildExpectedSchemaObjects(SyncAuthoritySchemaMigration7.Sql))
        .OrderBy(item => item, StringComparer.Ordinal).ToArray();
    // Version 8 adds the three normalized Rewind tables and their indexes. Retained
    // authority tables, receipts, triggers and constraints are unchanged; the exact
    // object inventory is required.
    private static readonly string[] ExpectedVersion8SchemaObjects = ExpectedVersion7SchemaObjects
        .Concat(BuildExpectedSchemaObjects(SyncAuthoritySchemaMigration8.Sql))
        .OrderBy(item => item, StringComparer.Ordinal).ToArray();
    // Version 9 adds the three compact observation tables, their read indexes, their two
    // append-only triggers and the read-only effective_observation view. Retained authority
    // tables, receipts, triggers and the legacy observation table are unchanged, so the
    // version 8 inventory above stays exactly the version 8 inventory for read-only
    // predecessor admission while version 9 carries its own exact inventory.
    private static readonly string[] ExpectedVersion9SchemaObjects = ExpectedVersion8SchemaObjects
        .Concat(BuildExpectedSchemaObjects(SyncAuthoritySchemaMigration9.Sql))
        .OrderBy(item => item, StringComparer.Ordinal).ToArray();
    // Version 10 adds the detached immutable activity_context table, its reverse-reference index and the
    // nullable history_event.activity_context_key foreign key. A column is not a schema object, so the exact
    // current inventory is the version 9 inventory plus those two objects; every retained table, index,
    // trigger, view and receipt stays exactly as version 9 recorded it.
    private static readonly string[] ExpectedVersion10SchemaObjects = ExpectedVersion9SchemaObjects
        .Concat(BuildExpectedSchemaObjects(SyncAuthoritySchemaMigration10.Sql))
        .OrderBy(item => item, StringComparer.Ordinal).ToArray();
    // Version 11 adds the explicit work-admission structure to the existing admission tables: three
    // nullable command_request admission columns with one unique (generation, sequence) index, two
    // installation admission counters, the nullable operation origin/execution command references with
    // their two partial indexes, the nullable lease command reference plus its checked dispatch_allowed
    // flag, and the two immutability triggers. None of those is a column-only change that alters the
    // object inventory beyond the three new indexes and two new triggers, so the exact current inventory
    // is the version 10 inventory plus those five objects and every retained table, index, trigger, view
    // and receipt stays exactly as version 10 recorded it.
    private static readonly string[] ExpectedVersion11SchemaObjects = ExpectedVersion10SchemaObjects
        .Concat(BuildExpectedSchemaObjects(SyncAuthoritySchemaMigration11.Sql))
        .OrderBy(item => item, StringComparer.Ordinal).ToArray();
    internal static Action<string>? FreshCreateBoundaryForTest { get; set; }
    // Version 12 adds the two normalized original-consent tables, their identity/read indexes, the two
    // nullable operation consent columns with their reverse-reference index, the nullable
    // command_request.consent_id with its index, and the consent enforcement triggers. Only those new
    // index and trigger names change the object inventory, so the exact current inventory is the version
    // 11 inventory plus those objects, and every retained table, column, index, trigger, view and receipt
    // stays exactly as version 11 recorded it.
    private static readonly string[] ExpectedVersion12SchemaObjects = ExpectedVersion11SchemaObjects
        .Concat(BuildExpectedSchemaObjects(SyncAuthoritySchemaMigration12.Sql))
        .OrderBy(item => item, StringComparer.Ordinal).ToArray();
    private static readonly string[] ExpectedVersion13SchemaObjects = ExpectedVersion12SchemaObjects
        .Concat(BuildExpectedSchemaObjects(SyncAuthoritySchemaMigration13.Sql))
        .Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
    private static readonly string[] ExpectedVersion14SchemaObjects = ExpectedVersion13SchemaObjects
        .Concat(BuildExpectedSchemaObjects(SyncAuthoritySchemaMigration14.Sql))
        .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static readonly string[] ExpectedVersion15SchemaObjects = ExpectedVersion14SchemaObjects
        .Where(item => item is not ("trigger:operation_member_delete_only_while_planned"
            or "trigger:operation_dependency_delete_only_while_planned"
            or "trigger:attempt_no_delete" or "trigger:receipt_no_delete"))
        .Concat(BuildExpectedSchemaObjects(SyncAuthoritySchemaMigration15.Sql))
        .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static readonly string[] ExpectedVersion16SchemaObjects = ExpectedVersion15SchemaObjects
        .Concat(BuildExpectedSchemaObjects(SyncAuthoritySchemaMigration16.Sql))
        .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static readonly string[] ExpectedVersion17SchemaObjects = ExpectedVersion16SchemaObjects
        .Concat(BuildExpectedSchemaObjects(SyncAuthoritySchemaMigration17.Sql))
        .Distinct(StringComparer.Ordinal).OrderBy(value=>value,StringComparer.Ordinal).ToArray();

    private static readonly string[] ExpectedCurrentSchemaObjects = ExpectedVersion17SchemaObjects
        .Concat(BuildExpectedSchemaObjects(SyncAuthoritySchemaMigration18.Sql))
        .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private string? _essentialSuccessorInitialReceipt;
    private string? _essentialSuccessorInitialGeneration;

    internal static Action<string>? MigrationBoundaryForTest { get; set; }
    internal static bool DisableFinalPlanningAuthorityGuardForTest { get; set; }
    internal static Action<string>? HealthCheckCompletedForTest { get; set; }
    internal static Action<SqliteConnection>? ReadOnlyHealthConnectionForTest { get; set; }
    internal static Action<SqliteConnection>? ActivityReadConnectionForTest { get; set; }
    internal static Action<string, double>? TimingForTest { get; set; }
    internal Action<string>? BootstrapReadProgressForTest { get; set; }
    internal static Action<int>? ExecutionWriteCommitBoundaryForTest { get; set; }
    // Instance-only fault input after real admission/schema checks, before any operation insert.
    internal Action<SyncAuthorityOperationPlan>? BeforeOperationRegistrationForTest { get; set; }

    // Narrow fault boundary for the published same-root edit: fires INSIDE CommitPublishedSameRootEditAsync
    // after the revision, config, root and recovery writes and BEFORE the definition write, so a test can
    // cancel or fault at exactly that point and prove the preceding writes roll back. The token is then
    // observed by the following ThrowIfCancellationRequested, so a cancelled test really aborts the commit.
    internal static Action? PublishedSameRootEditAfterWritesForTest { get; set; }

    private static long TimingStart() => TimingForTest is null ? 0 : Stopwatch.GetTimestamp();
    private static void ReportTiming(string phase, long started)
    {
        if (started != 0) TimingForTest?.Invoke(phase, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    // Pool inside an admitted command or its awaited/drained effect batch.
    // Startup, migrations and admission keep their non-pooled lifetime. Every
    // checkout still validates schema (the object inventory by the schema_version rule in
    // RequireExactSchemaAsync), receipts and connection settings; no authority/health verdict is cached.
    internal IAsyncDisposable BeginExecutionConnectionScope(bool planningReservations = false)
        => BeginConnectionScope(batchWrites: true, planningReservations);

    // Preparation and finalization can reuse parsed SQLite schema without
    // changing their individual durable transaction/acknowledgement boundaries.
    internal IAsyncDisposable BeginConnectionReuseScope()
        => BeginConnectionScope(batchWrites: false);

    private IAsyncDisposable BeginConnectionScope(bool batchWrites, bool planningReservations = false)
    {
        var scope = new ExecutionConnectionScope(this, _executionConnectionScope.Value, batchWrites, planningReservations);
        _executionConnectionScope.Value = scope;
        scope.StartWrites();
        return scope;
    }

    private sealed class ExecutionConnectionScope : IAsyncDisposable
    {
        private readonly SyncAuthorityDatabase _owner;
        private readonly ExecutionConnectionScope? _previous;
        private readonly PilotDirectoryLease _parent;
        private readonly SyncLocalFileAuthority _file;
        private readonly ExecutionWriteQueue? _writes;
        private int _disposed;
        internal bool Active => Volatile.Read(ref _disposed) == 0;
        internal bool BatchesWrites => _writes is not null;

        internal ExecutionConnectionScope(SyncAuthorityDatabase owner, ExecutionConnectionScope? previous,
            bool batchWrites, bool planningReservations)
        {
            _owner = owner;
            _previous = previous;
            _writes = batchWrites ? new ExecutionWriteQueue(owner, planningReservations) : null;
            _parent = PilotDirectoryLease.OpenProductDirectoryReadGuard(Path.GetDirectoryName(owner._databasePath)!);
            SyncLocalFileAuthority? file = null;
            try
            {
                file = SyncLocalFileAuthority.OpenExistingMutableReadGuard(owner._databasePath);
                file.VerifySingleLink();
                if (!string.Equals(file.FinalPath, owner._databasePath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The authority database path changed before its execution scope was opened.");
                _parent.VerifyBinding();
                _file = file;
            }
            catch { file?.Dispose(); _parent.Dispose(); throw; }
        }

        internal void StartWrites() => _writes?.Start();
        internal Task WriteAsync(Func<SqliteConnection, SqliteTransaction, Task> action, CancellationToken token) =>
            (_writes ?? throw new InvalidOperationException("This connection scope does not batch writes."))
            .EnqueueAsync(action, token);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            _owner._executionConnectionScope.Value = _previous;
            return new ValueTask(ReleaseAsync());
        }

        private async Task ReleaseAsync()
        {
            try
            {
                if (_writes is not null) await _writes.DrainAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    // The outer admitted command still owns the path/file guards.
                    // Only its final release retires the shared idle handles.
                    if (_previous is not { Active: true })
                    {
                        using var key = new SqliteConnection(ConnectionString(_owner._databasePath, SqliteOpenMode.ReadWrite, true));
                        SqliteConnection.ClearPool(key);
                    }
                }
                finally { _file.Dispose(); _parent.Dispose(); }
            }
        }
    }

    private static string ConnectionString(string path, SqliteOpenMode mode, bool pooling) =>
        new SqliteConnectionStringBuilder
        { DataSource = path, Mode = mode, Cache = SqliteCacheMode.Private, Pooling = pooling }.ToString();

    public SyncAuthorityDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _ = Path.GetDirectoryName(_databasePath)
            ?? throw new ArgumentException("The database path has no parent directory.", nameof(databasePath));
    }

    public string DatabasePath => _databasePath;

    public string Version2RollbackDatabasePath => _databasePath + ".v2-rollback";
    public string Version3RollbackDatabasePath => _databasePath + ".v3-rollback";
    public string Version4RollbackDatabasePath => _databasePath + ".v4-rollback";
    public string Version5RollbackDatabasePath => _databasePath + ".v5-rollback";
    public string Version6RollbackDatabasePath => _databasePath + ".v6-rollback";
    public string Version7RollbackDatabasePath => _databasePath + ".v7-rollback";
    public string Version8RollbackDatabasePath => _databasePath + ".v8-rollback";
    public string Version9RollbackDatabasePath => _databasePath + ".v9-rollback";
    public string Version10RollbackDatabasePath => _databasePath + ".v10-rollback";
    public string Version11RollbackDatabasePath => _databasePath + ".v11-rollback";
    public string Version12RollbackDatabasePath => _databasePath + ".v12-rollback";
    public string Version13RollbackDatabasePath => _databasePath + ".v13-rollback";
    public string Version14RollbackDatabasePath => _databasePath + ".v14-rollback";
    public string Version15RollbackDatabasePath => _databasePath + ".v15-rollback";
    public string Version16RollbackDatabasePath => _databasePath + ".v16-rollback";
    public string Version17RollbackDatabasePath => _databasePath + ".v17-rollback";

    public static string DefaultDatabasePath => Path.Combine(SyncSharkRuntimePaths.ProductRoot, "vNext", "state", "syncshark.db");

    public async Task InitializeAsync(string installationId, string capabilityProfileId, string sourceRevision,
        string capabilityProfileJson, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(installationId, nameof(installationId));
        RequireIdentifier(capabilityProfileId, nameof(capabilityProfileId));
        RequireJson(capabilityProfileJson, nameof(capabilityProfileJson));
        var databaseParent = Path.GetDirectoryName(_databasePath)
                             ?? throw new InvalidOperationException("The database path has no parent directory.");

        await _writer.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(_databasePath))
                throw new InvalidDataException("The authority database path is occupied by a directory; no state was changed.");

            var isFresh = !File.Exists(_databasePath);
            var migrateVersion2 = false;
            var migrateVersion3 = false;
            var migrateVersion4 = false;
            var migrateVersion5 = false;
            var migrateVersion6 = false;
            var migrateVersion7 = false;
            var migrateVersion8 = false;
            var migrateVersion9 = false;
            var migrateVersion10 = false;
            var migrateVersion11 = false;
            var migrateVersion12 = false;
            var migrateVersion13 = false;
            var migrateVersion14 = false;
            var migrateVersion15 = false;
            var migrateVersion16 = false;
            var migrateVersion17 = false;
            if (isFresh)
            {
                Directory.CreateDirectory(databaseParent);
                FreshCreateBoundaryForTest?.Invoke("parent-ready-before-file-create");
                await using (var created = new FileStream(_databasePath, FileMode.CreateNew, FileAccess.ReadWrite,
                                 FileShare.None, 1, FileOptions.WriteThrough))
                {
                    await created.FlushAsync(cancellationToken);
                    created.Flush(flushToDisk: true);
                }
                FreshCreateBoundaryForTest?.Invoke("file-created-before-sqlite-open");
            }
            else
            {
                var existingVersion = await InspectReadOnlySchemaAsync(_databasePath, cancellationToken,
                    verifyHealth: false);
                migrateVersion2 = existingVersion == SyncAuthoritySchemaMigration3.PreviousVersion;
                migrateVersion3 = existingVersion == SyncAuthoritySchemaMigration4.PreviousVersion;
                migrateVersion4 = existingVersion == SyncAuthoritySchemaMigration5.PreviousVersion;
                migrateVersion5 = existingVersion == SyncAuthoritySchemaMigration6.PreviousVersion;
                migrateVersion6 = existingVersion == SyncAuthoritySchemaMigration7.PreviousVersion;
                migrateVersion7 = existingVersion == SyncAuthoritySchemaMigration8.PreviousVersion;
                migrateVersion8 = existingVersion == SyncAuthoritySchemaMigration9.PreviousVersion;
                migrateVersion9 = existingVersion == SyncAuthoritySchemaMigration10.PreviousVersion;
                migrateVersion10 = existingVersion == SyncAuthoritySchemaMigration11.PreviousVersion;
                migrateVersion11 = existingVersion == SyncAuthoritySchemaMigration12.PreviousVersion;
                migrateVersion12 = existingVersion == SyncAuthoritySchemaMigration13.PreviousVersion;
                migrateVersion13 = existingVersion == SyncAuthoritySchemaMigration14.PreviousVersion;
                migrateVersion14 = existingVersion == SyncAuthoritySchemaMigration15.PreviousVersion;
                migrateVersion15 = existingVersion == SyncAuthoritySchemaMigration16.PreviousVersion;
                migrateVersion16 = existingVersion == SyncAuthoritySchemaMigration17.PreviousVersion;
                migrateVersion17 = existingVersion == SyncAuthoritySchemaMigration18.PreviousVersion;
                if (migrateVersion2)
                {
                    await EnsureVersion2RollbackBackupAsync(cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v2-backup-verified-before-migration");
                }
                else if (migrateVersion3)
                {
                    await EnsureRollbackBackupAsync(3, cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v3-backup-verified-before-migration");
                }
                else if (migrateVersion4)
                {
                    await EnsureRollbackBackupAsync(4, cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v4-backup-verified-before-migration");
                }
                else if (migrateVersion5)
                {
                    await EnsureRollbackBackupAsync(5, cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v5-backup-verified-before-migration");
                }
                else if (migrateVersion6)
                {
                    await EnsureRollbackBackupAsync(6, cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v6-backup-verified-before-migration");
                }
                else if (migrateVersion7)
                {
                    await EnsureRollbackBackupAsync(7, cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v7-backup-verified-before-migration");
                }
                else if (migrateVersion8)
                {
                    await EnsureRollbackBackupAsync(8, cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v8-backup-verified-before-migration");
                }
                else if (migrateVersion9)
                {
                    await EnsureRollbackBackupAsync(9, cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v9-backup-verified-before-migration");
                }
                else if (migrateVersion10)
                {
                    await EnsureRollbackBackupAsync(10, cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v10-backup-verified-before-migration");
                }
                else if (migrateVersion11)
                {
                    await EnsureRollbackBackupAsync(11, cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v11-backup-verified-before-migration");
                }
                else if (migrateVersion12)
                {
                    await EnsureRollbackBackupAsync(12, cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v12-backup-verified-before-migration");
                }
                else if (migrateVersion13)
                {
                    await EnsureRollbackBackupAsync(13, cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v13-backup-verified-before-migration");
                }
                else if (migrateVersion14)
                {
                    await EnsureRollbackBackupAsync(14, cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v14-backup-verified-before-migration");
                }
                else if (migrateVersion15)
                {
                    await EnsureRollbackBackupAsync(15, cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v15-backup-verified-before-migration");
                }
                else if (migrateVersion16)
                {
                    await EnsureRollbackBackupAsync(16, cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v16-backup-verified-before-migration");
                }
                else if (migrateVersion17)
                {
                    await EnsureRollbackBackupAsync(17, cancellationToken);
                    MigrationBoundaryForTest?.Invoke("v17-backup-verified-before-migration");
                }
            }

            await using var connection = isFresh
                ? await OpenFreshConnectionAsync(cancellationToken)
                : await OpenConnectionAtPathAsync(_databasePath, SqliteOpenMode.ReadWrite,
                    requireCurrentSchema: !migrateVersion2 && !migrateVersion3 && !migrateVersion4 &&
                                          !migrateVersion5 && !migrateVersion6 && !migrateVersion7 &&
                                          !migrateVersion8 && !migrateVersion9 && !migrateVersion10 &&
                                          !migrateVersion11 && !migrateVersion12 && !migrateVersion13 && !migrateVersion14 && !migrateVersion15 && !migrateVersion16 && !migrateVersion17,
                    cancellationToken, verifyHealth: false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            if (isFresh)
            {
                await ExecuteAsync(connection, transaction, SyncAuthoritySchema.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaSupplement.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration3.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration4.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration5.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration6.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration7.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration8.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration9.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration10.Sql, cancellationToken);
                FreshCreateBoundaryForTest?.Invoke("schema-applied-before-version-commit");
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version),
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
            }
            else if (migrateVersion2)
            {
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration3.Sql,
                    cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration4.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration5.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration6.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration7.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration8.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration9.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration10.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version),
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v3-schema-applied-before-version-commit");
            }
            else if (migrateVersion3)
            {
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration4.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration5.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration6.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration7.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration8.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration9.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration10.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version), ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v4-schema-applied-before-version-commit");
            }
            else if (migrateVersion4)
            {
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration5.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration6.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration7.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration8.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration9.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration10.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version), ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v5-schema-applied-before-version-commit");
            }
            else if (migrateVersion5)
            {
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration6.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration7.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration8.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration9.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration10.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version), ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v6-schema-applied-before-version-commit");
            }
            else if (migrateVersion6)
            {
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration7.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration8.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration9.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration10.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version), ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v7-schema-applied-before-version-commit");
            }
            else if (migrateVersion7)
            {
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration8.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration9.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration10.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version), ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v8-schema-applied-before-version-commit");
            }
            else if (migrateVersion8)
            {
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration9.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration10.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version),
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v9-schema-applied-before-version-commit");
            }
            else if (migrateVersion9)
            {
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration10.Sql, cancellationToken);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version),
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v10-schema-applied-before-version-commit");
            }
            else if (migrateVersion10)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version),
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v11-schema-applied-before-version-commit");
            }
            else if (migrateVersion11)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version),
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v12-schema-applied-before-version-commit");
            }
            else if (migrateVersion12)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version),
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v13-schema-applied-before-version-commit");
            }            else if (migrateVersion13)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version),
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v14-schema-applied-before-version-commit");
            }
            else if (migrateVersion14)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version),
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v15-schema-applied-before-version-commit");
            }
            else if (migrateVersion15)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version),
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v16-schema-applied-before-version-commit");
            }            else if (migrateVersion16)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version),
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v17-schema-applied-before-version-commit");
            }
            else if (migrateVersion17)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO schema_migration(schema_version, applied_utc, source_revision) VALUES($version,$utc,$revision);",
                    cancellationToken, ("$version", SyncAuthoritySchema.Version),
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$revision", sourceRevision));
                MigrationBoundaryForTest?.Invoke("v18-schema-applied-before-version-commit");
            }
            // Version 11 states its whole contract as additive ALTER TABLE statements on tables that every
            // predecessor path already created, so one guarded execution inside this same transaction
            // serves the fresh create and every supported predecessor migration. A database that is
            // already current executes none of the schema branches and therefore reaches this line with
            // every flag false, so the version 11 statements are never applied twice.
            if (isFresh || migrateVersion2 || migrateVersion3 || migrateVersion4 || migrateVersion5 ||
                migrateVersion6 || migrateVersion7 || migrateVersion8 || migrateVersion9 || migrateVersion10)
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration11.Sql, cancellationToken);
            // Version 12 expresses its non-table contract the same way. Its two tables, its indexes and its
            // triggers are created for the fresh create and for every supported predecessor, while its
            // additive ALTER TABLE statements on the existing operation and command_request tables must
            // apply exactly once, so this guard includes the version 11 predecessor as well. A database
            // that is already version 12 executes none of the schema branches above and therefore reaches
            // this line with every flag false, so nothing is applied twice.
            if (isFresh || migrateVersion2 || migrateVersion3 || migrateVersion4 || migrateVersion5 ||
                migrateVersion6 || migrateVersion7 || migrateVersion8 || migrateVersion9 ||
                migrateVersion10 || migrateVersion11)
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration12.Sql, cancellationToken);
            if (isFresh || migrateVersion2 || migrateVersion3 || migrateVersion4 || migrateVersion5 ||
                migrateVersion6 || migrateVersion7 || migrateVersion8 || migrateVersion9 ||
                migrateVersion10 || migrateVersion11 || migrateVersion12)
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration13.Sql, cancellationToken);
            if (isFresh || migrateVersion2 || migrateVersion3 || migrateVersion4 || migrateVersion5 ||
                migrateVersion6 || migrateVersion7 || migrateVersion8 || migrateVersion9 ||
                migrateVersion10 || migrateVersion11 || migrateVersion12 || migrateVersion13)
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration14.Sql, cancellationToken);
            if (isFresh || migrateVersion2 || migrateVersion3 || migrateVersion4 || migrateVersion5 ||
                migrateVersion6 || migrateVersion7 || migrateVersion8 || migrateVersion9 ||
                migrateVersion10 || migrateVersion11 || migrateVersion12 || migrateVersion13 || migrateVersion14)
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration15.Sql, cancellationToken);
            if (isFresh || migrateVersion2 || migrateVersion3 || migrateVersion4 || migrateVersion5 ||
                migrateVersion6 || migrateVersion7 || migrateVersion8 || migrateVersion9 ||
                migrateVersion10 || migrateVersion11 || migrateVersion12 || migrateVersion13 ||
                migrateVersion14 || migrateVersion15)
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration16.Sql, cancellationToken);
            if (isFresh || migrateVersion2 || migrateVersion3 || migrateVersion4 || migrateVersion5 ||
                migrateVersion6 || migrateVersion7 || migrateVersion8 || migrateVersion9 || migrateVersion10 ||
                migrateVersion11 || migrateVersion12 || migrateVersion13 || migrateVersion14 || migrateVersion15 || migrateVersion16)
                await ExecuteAsync(connection,transaction,SyncAuthoritySchemaMigration17.Sql,cancellationToken);
            if (isFresh || migrateVersion2 || migrateVersion3 || migrateVersion4 || migrateVersion5 ||
                migrateVersion6 || migrateVersion7 || migrateVersion8 || migrateVersion9 || migrateVersion10 ||
                migrateVersion11 || migrateVersion12 || migrateVersion13 || migrateVersion14 ||
                migrateVersion15 || migrateVersion16 || migrateVersion17)
                await ExecuteAsync(connection, transaction, SyncAuthoritySchemaMigration18.Sql, cancellationToken);
            // The rebuild keeps every recorded provider literal. Only a row whose retained id is
            // exactly the grandfathered Google derivation over its own tuple is corrected, and only
            // inside this same transaction.
            await ReclassifyLegacyGoogleProviderAccountsAsync(connection, transaction, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO installation(installation_id, database_generation, created_utc,essential_successor_json) VALUES($id,$generation,$utc,$successor);",
                cancellationToken, ("$id", installationId),
                ("$generation", _essentialSuccessorInitialGeneration ?? Guid.NewGuid().ToString("N")),
                ("$utc", Utc(DateTimeOffset.UtcNow)), ("$successor", _essentialSuccessorInitialReceipt));
            var retainedInstallations = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM installation;", cancellationToken);
            var exactInstallation = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM installation WHERE installation_id=$id;", cancellationToken,
                ("$id", installationId));
            if (retainedInstallations != 1 || exactInstallation != 1)
                throw new InvalidOperationException(
                    "The authority database is already bound to a different installation identity.");
            if (_essentialSuccessorInitialReceipt is null)
                await RegisterCapabilityProfileCoreAsync(connection, transaction, capabilityProfileId,
                    sourceRevision, capabilityProfileJson, cancellationToken);
            await ExecuteAsync(connection, transaction, $"PRAGMA user_version={SyncAuthoritySchema.Version};", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (isFresh) FreshCreateBoundaryForTest?.Invoke("current-schema-committed");
            if (migrateVersion2) MigrationBoundaryForTest?.Invoke("v3-migration-committed");
            if (migrateVersion3) MigrationBoundaryForTest?.Invoke("v4-migration-committed");
            if (migrateVersion4) MigrationBoundaryForTest?.Invoke("v5-migration-committed");
            if (migrateVersion5) MigrationBoundaryForTest?.Invoke("v6-migration-committed");
            if (migrateVersion6) MigrationBoundaryForTest?.Invoke("v7-migration-committed");
            if (migrateVersion7) MigrationBoundaryForTest?.Invoke("v8-migration-committed");
            if (migrateVersion8) MigrationBoundaryForTest?.Invoke("v9-migration-committed");
            if (migrateVersion9) MigrationBoundaryForTest?.Invoke("v10-migration-committed");
            if (migrateVersion10) MigrationBoundaryForTest?.Invoke("v11-migration-committed");
            if (migrateVersion11) MigrationBoundaryForTest?.Invoke("v12-migration-committed");
            if (migrateVersion12) MigrationBoundaryForTest?.Invoke("v13-migration-committed");
            if (migrateVersion13) MigrationBoundaryForTest?.Invoke("v14-migration-committed");
            if (migrateVersion14) MigrationBoundaryForTest?.Invoke("v15-migration-committed");
            if (migrateVersion15) MigrationBoundaryForTest?.Invoke("v16-migration-committed");
            if (migrateVersion16) MigrationBoundaryForTest?.Invoke("v17-migration-committed");
            if (migrateVersion17) MigrationBoundaryForTest?.Invoke("v18-migration-committed");
            // Ordinary startup validates schema identity without scanning retained data.
            // New schemas and migrations still require a full post-commit health check.
            await RequireExactCurrentSchemaAsync(connection, cancellationToken,
                verifyHealth: isFresh || migrateVersion2 || migrateVersion3 || migrateVersion4 || migrateVersion5 ||
                              migrateVersion6 || migrateVersion7 || migrateVersion8 || migrateVersion9 ||
                              migrateVersion10 || migrateVersion11 || migrateVersion12 || migrateVersion13 || migrateVersion14 || migrateVersion15 || migrateVersion16 || migrateVersion17,
                allowIncompleteSuccessor: _essentialSuccessorInitialReceipt is not null);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<string?> LoadInstallationIdAsync(CancellationToken cancellationToken = default,
        Action<string>? startupProgress = null)
    {
        if (!File.Exists(_databasePath)) return null;
        await InspectReadOnlySchemaAsync(_databasePath, cancellationToken, verifyHealth: false,
            startupProgress: startupProgress);
        startupProgress?.Invoke("Startup: reading the schema-validated database installation identity.");
        return await ReadInstallationIdAsync(_databasePath, cancellationToken);
    }

    public async Task<SyncPairSet> LoadPairSetAsync(CancellationToken cancellationToken = default, bool readOnly = false)
    {
        await using var connection = readOnly ? await OpenReadOnlyConnectionAsync(cancellationToken) : await OpenConnectionAsync(cancellationToken);
        return await LoadPairSetCoreAsync(connection, null, cancellationToken);
    }

    private static async Task<SyncPairSet> LoadPairSetCoreAsync(SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is null)
        {
            // Definitions and their own namespace/ancestry authorities must share one read snapshot.
            await using var snapshot = connection.BeginTransaction(deferred: true);
            return await LoadPairSetCoreAsync(connection, snapshot, cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT pair_id,configuration_revision,pair_json,selected
            FROM sync_pair
            WHERE state<>'retired' AND pair_json IS NOT NULL
            ORDER BY selected DESC,pair_id;
            """;
        var pairs = new List<SyncPair>();
        string? selected = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var pairId = reader.GetString(0);
            var revision = reader.GetInt64(1);
            var pair = JsonSerializer.Deserialize<SyncPair>(reader.GetString(2))
                       ?? throw new InvalidDataException("A retained SQLite pair definition is empty.");
            pair = SyncPairStore.NormalizePair(pair);
            if (!string.Equals(pair.Id, pairId, StringComparison.Ordinal) || pair.PolicyRevision != revision)
                throw new InvalidDataException(
                    "A retained SQLite pair definition does not match its authority identity or revision.");
            pairs.Add(pair);
            if (reader.GetInt64(3) == 1)
            {
                if (selected is not null)
                    throw new InvalidDataException("More than one SQLite pair is selected.");
                selected = pairId;
            }
        }

        // The pair reader is released BEFORE any further statement runs on this connection.
        await reader.DisposeAsync();
        await command.DisposeAsync();
        var ordered = pairs.OrderByDescending(item => item.Priority)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return await NormalizeRetainedReadbackCoreAsync(connection, transaction,
            new SyncPairSet(ordered, selected), cancellationToken);
    }

    // Runtime and Storage merge read-only migration candidates with the retained set. They must
    // preserve the same authority-backed comparison context, not revalidate legacy rows without it.
    internal async Task<SyncPairSet> NormalizeRetainedReadbackAsync(SyncPairSet set,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenReadOnlyConnectionAsync(cancellationToken);
        await using var snapshot = connection.BeginTransaction(deferred: true);
        return await NormalizeRetainedReadbackCoreAsync(connection, snapshot, set, cancellationToken);
    }

    private static async Task<SyncPairSet> NormalizeRetainedReadbackCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncPairSet set, CancellationToken cancellationToken)
    {
        var ordered = set.Pairs;
        var comparisons = new Dictionary<string, SyncPair>(StringComparer.Ordinal);
        var owned = new List<SyncPair>();
        foreach (var pair in ordered)
        {
            var retained = await LoadRetainedPairDefinitionAsync(connection, transaction, pair.Id, cancellationToken);
            if (retained is null) { comparisons[pair.Id] = pair; continue; }
            if (JsonSerializer.Serialize(SyncPairStore.NormalizePair(pair)) != JsonSerializer.Serialize(retained))
                throw new InvalidDataException("The retained pair changed during readback; reload its current definition.");
            owned.Add(pair);
            comparisons[pair.Id] = await ProjectRetainedPairForComparisonAsync(connection, transaction,
                pair, cancellationToken) ?? pair;
        }
        // D2/OV637-4: the retained pair set is validated through the SAME bound validation context the
        // publication, reservation and readback consumers use - each participating pair's own RETAINED
        // observation of its exact active root, resolved inside this reader's own coherent snapshot. The
        // context is built only when the set can contain a path-opaque correspondence, and it can only ADMIT
        // a correspondence the retained authority already verified; a pair with no usable observation keeps
        // the unchanged fail-closed refusal.
        Func<SyncPair, IReadOnlyList<SyncPairPartitionPolicy.SyncOpaqueCloudEndpointProof>?>? proofsOf = null;
        if (ordered.Count >= 2 && owned.Any(HasOpaqueCloudEndpoint))
        {
            var proofs = await BuildRetainedAliasProofsAsync(connection, transaction, owned, null,
                cancellationToken);
            proofsOf = pair => proofs.TryGetValue(pair.Id, out var value) ? value : null;
        }
        return SyncPairStore.Normalize(set, proofsOf,
            comparisons.Count == 0 ? null : pair => comparisons[pair.Id]);
    }

    /// <summary>D2: true when one of this pair's endpoints is a registered path-opaque cloud provider.</summary>
    private static bool HasOpaqueCloudEndpoint(SyncPair pair)
    {
        var endpoints = SyncCloudPairConfiguration.SavedEndpoints(pair);
        if (endpoints is null) return false;
        foreach (var endpoint in new[] { endpoints.Left, endpoints.Right })
        {
            if (endpoint.IsLocal || endpoint.Kind == SyncEndpointKind.Local) continue;
            if (!SyncEndpointProviders.TryGet(SyncEndpoints.ProviderLiteral(endpoint.Kind), out var definition) ||
                definition?.ProviderId is null) continue;
            if (!definition.PathsDescribeAncestry) return true;
        }
        return false;
    }

    public async Task<IReadOnlyList<SyncPair>> LoadAllPairDefinitionsAsync(
        CancellationToken cancellationToken = default, bool includeRetired = true)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pair_id,configuration_revision,pair_json
            FROM sync_pair
            WHERE pair_json IS NOT NULL AND ($includeRetired=1 OR state<>'retired')
            ORDER BY state='retired',pair_id;
            """;
        command.Parameters.AddWithValue("$includeRetired", includeRetired ? 1 : 0);
        var pairs = new List<SyncPair>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var pair = JsonSerializer.Deserialize<SyncPair>(reader.GetString(2))
                       ?? throw new InvalidDataException("A retained SQLite pair definition is empty.");
            pair = SyncPairStore.NormalizePair(pair);
            if (!string.Equals(pair.Id, reader.GetString(0), StringComparison.Ordinal) ||
                pair.PolicyRevision != reader.GetInt64(1))
                throw new InvalidDataException(
                    "A retained SQLite pair definition does not match its authority identity or revision.");
            pairs.Add(pair);
        }
        return pairs;
    }

    public async Task<bool> IsPairDefinitionRetainedAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarLongAsync(connection, null,
            "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND state<>'retired' AND pair_json IS NOT NULL;",
            cancellationToken, ("$id", pairId)) == 1;
    }

    /// <summary>
    /// R702: the shared ATOMIC published same-root edit commit - the ONE transaction that must carry the
    /// revision advance, BOTH own root validate-and-register guards (local and cloud) and the complete
    /// definition, so a guard refusal (for example an overlapping root) rolls the revision advance back with
    /// it. The previous call sequence committed the revision and its pair_config_revision row first and then
    /// threw, leaving a revision with no matching definition ("A retained SQLite pair definition does not
    /// match its authority identity or revision"). This is the PUBLISHED same-root edit only: it is not the
    /// runtime exact-revision registration, and it neither replaces nor bypasses the specialised new,
    /// recovered or replacement paths. Every verified fact is prepared outside the writer lock; no network
    /// call, file allocation or provider access happens inside the transaction.
    /// </summary>
    public async Task<SyncPairSet> CommitPublishedSameRootEditAsync(SyncPair pair,
        SyncAuthorityPairConfiguration configuration, SyncPair prior, long expectedPublishedRevision,
        string localRootBindingId, long localGeneration, string volumeId, string volumeSerialHex,
        string localFileIdHex, string dropboxRootBindingId, long cloudGeneration, string installationId,
        string providerAccountId, string rootNamespaceId, string folderId, bool select,
        string controlNamespaceId, string controlNamespaceAllocationId, string controlIdentityJson,
        string controlMarkerIdentityJson, string controlOwnerMarkerHash, long controlGeneration,
        string? recoveryRootBindingId = null, string? recoveryDisplayPath = null, string? recoveryFolderId = null,
        string? recoveryNamespaceId = null, long recoveryGeneration = 0,
        string? preparedIdentityJson = null, string? preparedMarkerFileId = null, string? preparedMarkerHash = null,
        string? preparedControlRootPath = null,
        SyncAuthorityLease? preparedLease = null,
        string? typedCloudRootBindingId = null, string? typedCloudCompatibilitySide = null,
        string? typedCloudProvider = null, string? typedCloudProviderAccountId = null,
        string? typedCloudNamespaceId = null, string? typedCloudFolderId = null,
        string? typedCloudDisplayPath = null,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? typedCloudHierarchies = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(configuration);
        // The configuration validation the private core does not repeat.
        if (configuration.DeletionPolicy is not ("disabled" or "recoverable"))
            throw new ArgumentOutOfRangeException(nameof(configuration));
        if (configuration.DeletionGraceSeconds < 0 || configuration.RequiredAbsenceObservations < 1 ||
            configuration.ConflictPolicy != "manual_preserve_both" ||
            configuration.MetadataPolicy is not ("content_times_basic_attrs" or "content_only") ||
            configuration.RecoveryRetentionDays < 1)
            throw new ArgumentOutOfRangeException(nameof(configuration));
        foreach (var value in new[] { localRootBindingId, dropboxRootBindingId, installationId,
                     providerAccountId, rootNamespaceId, folderId, volumeId, volumeSerialHex, localFileIdHex,
                     controlNamespaceId, controlNamespaceAllocationId, controlIdentityJson,
                     controlMarkerIdentityJson, controlOwnerMarkerHash })
            RequireIdentifier(value, nameof(localRootBindingId));
        if (localGeneration < 1 || cloudGeneration < 1) throw new ArgumentOutOfRangeException(nameof(localGeneration));
        if (controlGeneration < 1) throw new ArgumentOutOfRangeException(nameof(controlGeneration));
        if (expectedPublishedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedPublishedRevision));
        if (recoveryRootBindingId is not null)
        {
            RequireIdentifier(recoveryRootBindingId, nameof(recoveryRootBindingId));
            if (string.IsNullOrWhiteSpace(recoveryDisplayPath) || string.IsNullOrWhiteSpace(recoveryFolderId) ||
                string.IsNullOrWhiteSpace(recoveryNamespaceId) || recoveryGeneration < 1)
                throw new ArgumentOutOfRangeException(nameof(recoveryRootBindingId));
        }
        if (preparedIdentityJson is not null &&
            (string.IsNullOrWhiteSpace(preparedIdentityJson) || string.IsNullOrWhiteSpace(preparedMarkerFileId) ||
             string.IsNullOrWhiteSpace(preparedMarkerHash) || string.IsNullOrWhiteSpace(preparedControlRootPath)))
            throw new ArgumentOutOfRangeException(nameof(preparedIdentityJson));
        if (typedCloudRootBindingId is not null &&
            (string.IsNullOrWhiteSpace(typedCloudCompatibilitySide) ||
             string.IsNullOrWhiteSpace(typedCloudProvider) ||
             string.IsNullOrWhiteSpace(typedCloudProviderAccountId) ||
             string.IsNullOrWhiteSpace(typedCloudNamespaceId) ||
             string.IsNullOrWhiteSpace(typedCloudFolderId) ||
             string.IsNullOrWhiteSpace(typedCloudDisplayPath)))
            throw new ArgumentOutOfRangeException(nameof(typedCloudRootBindingId));
        var typedProviderLiteral = typedCloudRootBindingId is null ? null :
            (SyncEndpointProviders.TryGet(typedCloudProvider!, out var typedProbe) && typedProbe?.ProviderId is not null
                ? typedProbe.ProviderId
                : throw new ArgumentException(
                    "This provider is not a registered cloud provider, so no typed cloud root is written.",
                    nameof(typedCloudProvider)));
        var typedCloudPath = typedCloudRootBindingId is null ? null
            : SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(typedCloudDisplayPath!);
        var typedCloudIdentityJson = typedCloudRootBindingId is null ? null
            : SyncAuthorityCloudRootPersistence.Serialize(typedProviderLiteral!, typedCloudProviderAccountId!,
                typedCloudNamespaceId!, typedCloudFolderId!);
        var typedSupplied = (typedCloudHierarchies ?? Array.Empty<SyncAuthorityCloudHierarchySnapshot>())
            .Select(item => item ?? throw new ArgumentException("A supplied cloud hierarchy snapshot is null.",
                nameof(typedCloudHierarchies))).ToArray();

        var normalized = SyncPairStore.NormalizePair(pair);
        var normalizedPrior = SyncPairStore.NormalizePair(prior);
        // Same-root, existing-published preconditions: this boundary may only edit the definition of the SAME
        // pair on the SAME endpoints, and the supplied prior definition is the published one to CAS against.
        if (!string.Equals(normalized.Id, normalizedPrior.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("The same-root edit boundary requires one retained pair identity.");
        if (!string.Equals(normalized.LocalRoot, normalizedPrior.LocalRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(normalized.DropboxRoot, normalizedPrior.DropboxRoot, StringComparison.Ordinal) ||
            !string.Equals(normalized.DropboxFolderId, normalizedPrior.DropboxFolderId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The edited definition must keep the published pair's exact local root, cloud root and folder id.");
        if (normalized.PolicyRevision <= expectedPublishedRevision)
            throw new InvalidOperationException(
                "A published same-root edit must advance the policy revision beyond the published revision.");
        var pairJson = JsonSerializer.Serialize(normalized);
        var priorJson = JsonSerializer.Serialize(normalizedPrior);
        var localIdentityJson = JsonSerializer.Serialize(new { volumeId, volumeSerial = volumeSerialHex, fileId = localFileIdHex });
        var cloudIdentityJson = JsonSerializer.Serialize(new { providerAccountId, rootNamespaceId, folderId });
        var normalizedLocalDisplayPath = NormalizeAuthorityPath("local", normalized.LocalRoot);
        var normalizedCloudDisplayPath = NormalizeAuthorityPath("dropbox", normalized.DropboxRoot);
        var recoveryNormalizedPath = recoveryRootBindingId is null ? null
            : NormalizeAuthorityPath("dropbox", recoveryDisplayPath!);
        SyncPairSet? committed = null;
        await WriteAsync(async (connection, transaction) =>
        {
            // CAS: the published revision and the exact prior definition must still be the ones this edit
            // was prepared against, checked INSIDE the transaction that commits the advance.
            var observed = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND state<>'retired' " +
                "AND configuration_revision=$revision AND pair_json=$json;",
                cancellationToken, ("$id", normalized.Id), ("$revision", expectedPublishedRevision),
                ("$json", priorJson));
            if (observed != 1)
                throw new InvalidOperationException(
                    "The published pair definition or revision changed since this edit was prepared; reload before saving.");
            // In-transaction preimage identity match: the retained root records and the retained
            // private-control ownership must be the exact ones this edit was prepared against. Matching
            // display paths alone is not proof.
            var localPreimage = await ScalarStringAsync(connection, transaction,
                "SELECT identity_json FROM root_binding WHERE root_binding_id=$id AND pair_id=$pair AND side='local' AND generation=$generation AND state='active';",
                cancellationToken, ("$id", localRootBindingId), ("$pair", normalized.Id),
                ("$generation", localGeneration));
            if (!string.Equals(localPreimage, localIdentityJson, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The retained local root record no longer matches the exact preimage this edit was prepared against.");
            if (typedCloudRootBindingId is null)
            {
                var cloudPreimage = await ScalarStringAsync(connection, transaction,
                    "SELECT identity_json FROM root_binding WHERE root_binding_id=$id AND pair_id=$pair AND side='dropbox' AND generation=$generation AND state='active';",
                    cancellationToken, ("$id", dropboxRootBindingId), ("$pair", normalized.Id),
                    ("$generation", cloudGeneration));
                if (!string.Equals(cloudPreimage, cloudIdentityJson, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "The retained cloud root record no longer matches the exact preimage this edit was prepared against.");
            }
            else
            {
                // The typed (Google/endpoint) cloud root keeps its OWN identity shape, so it is bound to this edit
                // by its own retained preimage rather than the legacy Dropbox identity.
                var typedPreimage = await ScalarStringAsync(connection, transaction,
                    "SELECT identity_json FROM root_binding WHERE root_binding_id=$id AND pair_id=$pair AND side=$side AND generation=$generation AND state='active';",
                    cancellationToken, ("$id", typedCloudRootBindingId), ("$pair", normalized.Id),
                    ("$side", typedCloudCompatibilitySide!), ("$generation", cloudGeneration));
                if (!string.Equals(typedPreimage, typedCloudIdentityJson, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "The retained typed cloud root record no longer matches the exact preimage this edit was prepared against.");
            }
            var controlPreimage = await ScalarStringAsync(connection, transaction,
                "SELECT b.control_namespace_id || '|' || c.namespace_allocation_id || '|' || c.identity_json || '|' || c.marker_identity_json || '|' || c.owner_marker_hash || '|' || b.binding_generation " +
                "FROM pair_control_namespace b JOIN control_namespace c ON c.control_namespace_id=b.control_namespace_id " +
                "WHERE b.pair_id=$pair AND b.side='local' AND b.status='active' AND c.status='active';",
                cancellationToken, ("$pair", normalized.Id));
            var expectedControl = string.Join("|", controlNamespaceId, controlNamespaceAllocationId,
                controlIdentityJson, controlMarkerIdentityJson, controlOwnerMarkerHash,
                controlGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string? preparedResolvedAllocation = null;
            if (preparedIdentityJson is null)
            {
                if (!string.Equals(controlPreimage, expectedControl, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "The retained private-control ownership no longer matches the exact preimage this edit was prepared against.");
            }
            else
            {
                // root718/723: the pair has no ACTIVE control ownership yet; the prepared allocation is the
                // match. Require this pair's OWN committed AllocateControl operation whose verified allocation
                // carries exactly the prepared physical identity, then terminalise ONLY that operation before
                // the unchanged configuration guard runs so the guard sees a genuinely terminal own operation.
                if (controlPreimage is not null)
                    throw new InvalidOperationException(
                        "A prepared private-control allocation cannot be published over an existing active control namespace.");
                var preparedExact = await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM namespace_allocation a JOIN operation o ON o.operation_id=a.operation_id " +
                    "WHERE a.side='local' AND a.status='verified' AND a.bound_identity=$identity " +
                    "AND o.pair_id=$pair AND o.kind='allocate_control' AND o.status='committed';",
                    cancellationToken, ("$identity", preparedIdentityJson!), ("$pair", normalized.Id));
                if (preparedExact != 1)
                    throw new InvalidOperationException(
                        "The prepared private-control allocation is not this pair's own verified committed allocation.");
                preparedResolvedAllocation = await ScalarStringAsync(connection, transaction,
                    "SELECT namespace_allocation_id FROM namespace_allocation WHERE side='local' " +
                    "AND status='verified' AND bound_identity=$identity;",
                    cancellationToken, ("$identity", preparedIdentityJson!)) ?? throw new InvalidDataException(
                    "The prepared private-control allocation is not retained.");
                var preparedOperation = await ScalarStringAsync(connection, transaction,
                    "SELECT operation_id FROM namespace_allocation WHERE namespace_allocation_id=$allocation;",
                    cancellationToken, ("$allocation", preparedResolvedAllocation)) ?? throw new InvalidDataException(
                    "The prepared private-control allocation has no retained operation.");
                var preparedTokenRaw = await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM operation WHERE operation_id=$id AND last_transition_fencing_token IS NOT NULL;",
                    cancellationToken, ("$id", preparedOperation));
                var preparedToken = await ScalarLongAsync(connection, transaction,
                    "SELECT last_transition_fencing_token FROM operation WHERE operation_id=$id;", cancellationToken,
                    ("$id", preparedOperation));
                // root723: the completion runs UNDER the prepared allocation's own live fenced lease, so the
                // fencing invariant still holds and the completion rolls back with the publication.
                if (preparedLease is not null)
                    await RequireFenceAsync(connection, transaction, preparedOperation, preparedLease,
                        cancellationToken);
                var preparedLeaseId = preparedLease?.LeaseId ??
                    (await ScalarStringAsync(connection, transaction,
                        "SELECT last_transition_lease_id FROM operation WHERE operation_id=$id;", cancellationToken,
                        ("$id", preparedOperation)) ?? "");
                var preparedFencing = preparedLease?.FencingToken ??
                    (await ScalarLongAsync(connection, transaction,
                        "SELECT last_transition_fencing_token FROM operation WHERE operation_id=$id;",
                        cancellationToken, ("$id", preparedOperation)));
                await CompleteOperationCoreAsync(connection, transaction, preparedOperation, preparedLeaseId,
                    preparedFencing, JsonSerializer.Serialize(new { result = "published-prepared-control",
                        normalized.Id, allocationId = preparedResolvedAllocation }), cancellationToken);
            }
            await RegisterPairConfigurationCoreAsync(connection, transaction, normalized.Id,
                normalized.PolicyRevision, configuration, cancellationToken);
            await RegisterLocalRootBindingCoreAsync(connection, transaction, localRootBindingId, normalized.Id,
                localGeneration, installationId, volumeId, volumeSerialHex, localFileIdHex, localIdentityJson,
                normalizedLocalDisplayPath, cancellationToken,
                allowBlockedCandidatePreimage: false, partitionCandidate: normalized);
            if (typedCloudRootBindingId is null)
            {
                await RegisterAccountRootBindingCoreAsync(connection, transaction, dropboxRootBindingId, normalized.Id,
                    cloudGeneration, installationId, providerAccountId, rootNamespaceId, folderId, cloudIdentityJson,
                    normalizedCloudDisplayPath, cancellationToken,
                    allowBlockedCandidatePreimage: false, partitionCandidate: normalized);
            }
            else
            {
                // root726: the typed cloud side uses the SAME transaction-scoped guard sequence the ordinary
                // candidate registration and the recovered activation use - the typed comparator plus its
                // provider/account/namespace checks - so no provider-specific transaction is introduced.
                var pairRetained = await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM sync_pair WHERE pair_id=$pair AND state<>'retired';", cancellationToken,
                    ("$pair", normalized.Id));
                if (pairRetained != 1)
                    throw new InvalidDataException("The cloud root is not bound to a retained pair.");
                var typedAccount = await LoadCloudProviderAccountAsync(connection, transaction,
                    typedCloudProviderAccountId!, installationId, cancellationToken)
                    ?? throw new InvalidOperationException(
                        "The cloud root is not bound to an active provider account of this installation.");
                if (!string.Equals(typedAccount.Provider, typedProviderLiteral, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "The retained provider account does not belong to this provider; the cloud root is refused.");
                if (!SyncAuthorityProviderAccountIdentity.MatchesEndpointNamespace(typedProviderLiteral!,
                        typedAccount.AccountId, typedAccount.RootNamespaceId, typedAccount.HomeNamespaceId,
                        typedCloudNamespaceId!))
                    throw new InvalidOperationException(
                        "The endpoint namespace is not the retained namespace of this provider account; the cloud root is refused.");
                var typedIdentity = new SyncAuthorityCloudRootIdentity(typedProviderLiteral!,
                    typedCloudProviderAccountId!, typedCloudNamespaceId!, typedCloudFolderId!, typedCloudPath!,
                    typedCloudRootBindingId, SyncAuthorityCloudRootIdentity.PairRootAuthority,
                    typedCloudCompatibilitySide!);
                await RequireCloudRootIdentityAvailableAsync(connection, transaction, typedIdentity, typedSupplied,
                    cancellationToken, normalized.Id, normalized);
                await UpsertRootBindingCoreAsync(connection, transaction, typedCloudRootBindingId, normalized.Id,
                    typedCloudCompatibilitySide!, cloudGeneration, typedCloudIdentityJson!, typedCloudPath!,
                    cancellationToken, allowBlockedCandidatePreimage: false);
            }
            if (preparedResolvedAllocation is not null)
            {
                try
                {
                // root718/723: publish the pair's newly ACTIVE control ownership in this same transaction, using
                // the exact prepared physical identity; nothing here publishes an unproved allocation.
                await RegisterControlNamespaceCoreAsync(connection, transaction,
                    new SyncAuthorityControlNamespace(normalized.Id, preparedResolvedAllocation, "local",
                        preparedIdentityJson!, JsonSerializer.Serialize(new { fileIdentity = preparedMarkerFileId }),
                        preparedMarkerHash!, "active"), cancellationToken);
                await BindPairControlNamespaceCoreAsync(connection, transaction, normalized.Id, "local",
                    normalized.Id, 1, cancellationToken);
                await RegisterRootBindingCoreAsync(connection, transaction,
                    DerivedId("root-local-control", normalized.Id, preparedIdentityJson!), normalized.Id,
                    "local_control", 1, preparedIdentityJson!,
                    NormalizeAuthorityPath("local", preparedControlRootPath!), cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw new InvalidOperationException("prepared-publication-failed: " + error.Message, error);
                }
            }
            // The dedicated provider recovery folder, when this edit carries one, is validated and upserted
            // INSIDE this same transaction, so a recovery identity/overlap refusal rolls the whole edit back
            // instead of reporting failure after the revision and definition already committed.
            if (recoveryRootBindingId is not null)
                await RegisterDropboxRecoveryRootCoreAsync(connection, transaction, recoveryRootBindingId,
                    installationId, providerAccountId, recoveryNormalizedPath!, recoveryFolderId!,
                    recoveryNamespaceId!, recoveryGeneration, cancellationToken);
            PublishedSameRootEditAfterWritesForTest?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            await RetainPairDefinitionCoreAsync(connection, transaction, normalized, pairJson, select,
                cancellationToken);
            // Coherent readback INSIDE the same transaction, so the committed result is what the caller sees.
            committed = await LoadPairSetCoreAsync(connection, transaction, cancellationToken);
        }, cancellationToken);
        return committed ?? throw new InvalidOperationException("The same-root edit readback did not complete.");
    }

    /// <summary>
    /// root727: the cloud/cloud published policy edit. A cloud/cloud pair owns no local root, so it cannot ride
    /// the same-root root-registration publication above; on its OWN path it advances the configuration revision
    /// and writes the pair definition in ONE transaction, reusing the SAME transaction-scoped cores and the SAME
    /// published-definition CAS. An overlap/identity/interruption refusal therefore rolls the revision advance
    /// back with the definition instead of leaving the revision ahead of the definition - the observed live 3/2
    /// defect class - and no local root or Dropbox registrar is invented for a cloud/cloud pair.
    /// </summary>
    public async Task<SyncPairSet> CommitCloudPairPolicyEditAsync(SyncPair pair,
        SyncAuthorityPairConfiguration configuration, SyncPair prior, long expectedPublishedRevision, bool select,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.DeletionPolicy is not ("disabled" or "recoverable"))
            throw new ArgumentOutOfRangeException(nameof(configuration));
        if (configuration.DeletionGraceSeconds < 0 || configuration.RequiredAbsenceObservations < 1 ||
            configuration.ConflictPolicy != "manual_preserve_both" ||
            configuration.MetadataPolicy is not ("content_times_basic_attrs" or "content_only") ||
            configuration.RecoveryRetentionDays < 1)
            throw new ArgumentOutOfRangeException(nameof(configuration));
        if (expectedPublishedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedPublishedRevision));
        var normalized = SyncPairStore.NormalizePair(pair);
        var normalizedPrior = SyncPairStore.NormalizePair(prior);
        if (!string.Equals(normalized.Id, normalizedPrior.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("The cloud policy edit boundary requires one retained pair identity.");
        if (normalized.PolicyRevision <= expectedPublishedRevision)
            throw new InvalidOperationException(
                "A published cloud policy edit must advance the policy revision beyond the published revision.");
        var pairJson = JsonSerializer.Serialize(normalized);
        var priorJson = JsonSerializer.Serialize(normalizedPrior);
        SyncPairSet? committed = null;
        await WriteAsync(async (connection, transaction) =>
        {
            // CAS: the published revision and the exact prior definition must still be the ones this edit was
            // prepared against, checked INSIDE the transaction that commits the advance.
            var observed = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND state<>'retired' " +
                "AND configuration_revision=$revision AND pair_json=$json;",
                cancellationToken, ("$id", normalized.Id), ("$revision", expectedPublishedRevision),
                ("$json", priorJson));
            if (observed != 1)
                throw new InvalidOperationException(
                    "The published pair definition or revision changed since this edit was prepared; reload before saving.");
            await RegisterPairConfigurationCoreAsync(connection, transaction, normalized.Id, normalized.PolicyRevision,
                configuration, cancellationToken);
            await RetainPairDefinitionCoreAsync(connection, transaction, normalized, pairJson, select,
                cancellationToken);
            // Coherent readback INSIDE the same transaction, so the committed result is what the caller sees.
            committed = await LoadPairSetCoreAsync(connection, transaction, cancellationToken);
        }, cancellationToken);
        return committed ?? throw new InvalidOperationException("The cloud policy edit readback did not complete.");
    }

    public async Task<SyncPairSet> RetainPairDefinitionAsync(SyncPair pair, bool select,
        CancellationToken cancellationToken = default)
    {
        pair = SyncPairStore.NormalizePair(pair);
        var pairJson = JsonSerializer.Serialize(pair);
        await WriteAsync(async (connection, transaction) =>
            await RetainPairDefinitionCoreAsync(connection, transaction, pair, pairJson, select,
                cancellationToken),
            cancellationToken);
        return await LoadPairSetAsync(cancellationToken);
    }

    /// <summary>
    /// root702(c): narrowly guarded recovery of an INTERRUPTED same-root publication. A same-root edit that was
    /// interrupted between the revision advance and the definition write leaves sync_pair.configuration_revision
    /// ahead of the published pair_json revision - the observed live 3/2 defect - so the whole pair set can no
    /// longer be read. This restores the last coherent PUBLISHED revision, and only then, when the orphan
    /// revision is UNUSED (no nonterminal operation depends on the pair) and the pair's own root identities are
    /// UNCHANGED from the published definition. It rewrites no definition, no policy and no root binding; a
    /// coherent pair is a plain no-op, and the orphan config row is retained as history. No live path calls it.
    /// </summary>
    public async Task<bool> RecoverInterruptedSameRootPublicationAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        var recovered = false;
        await WriteAsync(async (connection, transaction) =>
        {
            var retainedJson = await ScalarStringAsync(connection, transaction,
                "SELECT pair_json FROM sync_pair WHERE pair_id=$id AND state<>'retired' AND pair_json IS NOT NULL;",
                cancellationToken, ("$id", pairId)) ?? throw new InvalidDataException(
                "Only a published pair has a coherent revision to restore.");
            var published = SyncPairStore.NormalizePair(
                JsonSerializer.Deserialize<SyncPair>(retainedJson) ?? throw new InvalidDataException(
                    "The retained pair definition is empty."));
            var current = await ScalarLongAsync(connection, transaction,
                "SELECT configuration_revision FROM sync_pair WHERE pair_id=$id AND state<>'retired';",
                cancellationToken, ("$id", pairId));
            if (current == published.PolicyRevision) return;
            if (current < published.PolicyRevision) throw new InvalidOperationException(
                "The retained revision is behind its published definition; this is not an interrupted publication.");
            // The orphan revision must be UNUSED: no nonterminal authority may depend on the pair.
            var pending = await ScalarLongAsync(connection, transaction,
                $"""
                SELECT count(*) FROM operation o WHERE o.pair_id=$id
                  AND o.status NOT IN ('completed','cancelled','superseded')
                  AND NOT(o.status='blocked' AND {KnownNoEffectRefusalPredicate});
                """, cancellationToken, ("$id", pairId));
            if (pending != 0) throw new InvalidOperationException(
                "The interrupted publication carries nonterminal authority; the last published revision is not restored.");
            // The pair's OWN root identities must be unchanged from the published definition.
            var localFileId = published.LocalRootFileId.ToString("x16", CultureInfo.InvariantCulture);
            var localSerial = published.LocalVolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture);
            var localExact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM root_binding WHERE pair_id=$id AND side='local' AND state='active' " +
                "AND lower(json_extract(identity_json,'$.fileId'))=lower($file) " +
                "AND lower(json_extract(identity_json,'$.volumeSerial'))=lower($serial);",
                cancellationToken, ("$id", pairId), ("$file", localFileId), ("$serial", localSerial));
            var cloudExact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM root_binding WHERE pair_id=$id AND side='dropbox' AND state='active' " +
                "AND json_extract(identity_json,'$.folderId')=$folder;",
                cancellationToken, ("$id", pairId), ("$folder", published.DropboxFolderId));
            if (localExact != 1 || cloudExact != 1) throw new InvalidOperationException(
                "The pair's own root identity changed since the interruption; the last published revision is not restored.");
            await ExecuteAsync(connection, transaction,
                "UPDATE sync_pair SET configuration_revision=$revision WHERE pair_id=$id AND configuration_revision=$current;",
                cancellationToken, ("$revision", published.PolicyRevision), ("$id", pairId), ("$current", current));
            var restored = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND configuration_revision=$revision AND pair_json=$json;",
                cancellationToken, ("$id", pairId), ("$revision", published.PolicyRevision), ("$json", retainedJson));
            if (restored != 1) throw new InvalidOperationException(
                "The interrupted publication was not restored to its last coherent revision.");
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO history_event(rowid,history_event_id,operation_id,event_kind,detail_json,occurred_utc) " +
                "VALUES((SELECT next_history_sequence FROM installation),$id,NULL,'interrupted_publication_recovered',$detail,$utc);",
                cancellationToken, ("$id", "history-recovered-" + pairId + "-" + current),
                ("$detail", JsonSerializer.Serialize(new { pairId, orphanRevision = current,
                    restoredRevision = published.PolicyRevision })), ("$utc", Utc(DateTimeOffset.UtcNow)));
            recovered = true;
        }, cancellationToken);
        return recovered;
    }

    /// <summary>
    /// B1-C6: the pair-definition core, extracted from the existing public insertion inside one explicit scope
    /// block whose braces were verified to balance. The public signature and legacy behaviour are unchanged.
    /// </summary>
    /// <summary>root727: one recoverable interrupted same-root publication, reported read-only.</summary>
    public sealed record SyncInterruptedPublicationIncident(string PairId, string Name, long OrphanRevision,
        long PublishedRevision);

    /// <summary>
    /// root727: the READ-ONLY companion to <see cref="RecoverInterruptedSameRootPublicationAsync"/>. It reports
    /// exactly the RECOVERABLE incidents a caller may safely offer to repair - a PUBLISHED pair whose
    /// configuration_revision is ahead of its own pair_json revision, with no nonterminal authority depending on
    /// it and its own root identities unchanged - so the startup surface can NAME the affected pair and decide
    /// whether to offer the guarded repair WITHOUT performing it. It writes nothing, opens no provider, takes no
    /// mutation gate, inspects no display string and accepts no caller-supplied id or path.
    /// </summary>
    public async Task<IReadOnlyList<SyncInterruptedPublicationIncident>> DetectInterruptedSameRootPublicationsAsync(
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<(string PairId, string Name, long Orphan, SyncPair Published)>();
        await using (var connection = await OpenConnectionAsync(cancellationToken))
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT pair_id,configuration_revision,pair_json FROM sync_pair " +
                    "WHERE state<>'retired' AND pair_json IS NOT NULL ORDER BY pair_id;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var published = SyncPairStore.NormalizePair(
                        JsonSerializer.Deserialize<SyncPair>(reader.GetString(2)) ?? throw new InvalidDataException(
                            "A retained SQLite pair definition is empty."));
                    var orphan = reader.GetInt64(1);
                    if (orphan > published.PolicyRevision)
                        candidates.Add((reader.GetString(0), published.Name, orphan, published));
                }
            }
            var recoverable = new List<SyncInterruptedPublicationIncident>();
            foreach (var candidate in candidates)
            {
                // The same conditions the guarded recovery re-proves: the orphan revision must be UNUSED, and
                // the pair OWN root identities must be unchanged from the published definition.
                var pending = await ScalarLongAsync(connection, null,
                    $"""
                    SELECT count(*) FROM operation o WHERE o.pair_id=$id
                      AND o.status NOT IN ('completed','cancelled','superseded')
                      AND NOT(o.status='blocked' AND {KnownNoEffectRefusalPredicate});
                    """, cancellationToken, ("$id", candidate.PairId));
                if (pending != 0) continue;
                var localFileId = candidate.Published.LocalRootFileId.ToString("x16", CultureInfo.InvariantCulture);
                var localSerial = candidate.Published.LocalVolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture);
                var localExact = await ScalarLongAsync(connection, null,
                    "SELECT count(*) FROM root_binding WHERE pair_id=$id AND side='local' AND state='active' " +
                    "AND lower(json_extract(identity_json,'$.fileId'))=lower($file) " +
                    "AND lower(json_extract(identity_json,'$.volumeSerial'))=lower($serial);",
                    cancellationToken, ("$id", candidate.PairId), ("$file", localFileId), ("$serial", localSerial));
                var cloudExact = await ScalarLongAsync(connection, null,
                    "SELECT count(*) FROM root_binding WHERE pair_id=$id AND side='dropbox' AND state='active' " +
                    "AND json_extract(identity_json,'$.folderId')=$folder;",
                    cancellationToken, ("$id", candidate.PairId), ("$folder", candidate.Published.DropboxFolderId));
                if (localExact != 1 || cloudExact != 1) continue;
                recoverable.Add(new SyncInterruptedPublicationIncident(candidate.PairId, candidate.Name,
                    candidate.Orphan, candidate.Published.PolicyRevision));
            }
            return recoverable;
        }
    }

    private static async Task RetainPairDefinitionCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncPair pair, string pairJson, bool select,
        CancellationToken cancellationToken)
    {
        {
            if (select)
                await ExecuteAsync(connection, transaction, "UPDATE sync_pair SET selected=0 WHERE selected=1;",
                    cancellationToken);
            // RC639-4.INTEGRATION-1: the pair-definition write guard discounts exactly the same one
            // authenticated recovered non-creating collision the publication and mutability guards discount.
            var definitionRecovered = await ResolveRecoveredCollisionForPairAsync(connection, transaction,
                pair.Id, cancellationToken);
            await ExecuteAsync(connection, transaction,
                $"""
                UPDATE sync_pair
                SET pair_json=$json,selected=$selected
                WHERE pair_id=$id AND configuration_revision=$revision AND state<>'retired'
                  AND (pair_json IS NULL OR pair_json=$json OR NOT EXISTS(
                      SELECT 1 FROM operation o WHERE o.pair_id=sync_pair.pair_id
                        AND o.status NOT IN ('completed','cancelled','superseded')
                        AND NOT(o.status='blocked' AND {KnownNoEffectRefusalPredicate})
                        AND ($recovered IS NULL OR o.operation_id<>$recovered)));
                """, cancellationToken, ("$json", pairJson), ("$selected", select ? 1 : 0),
                ("$id", pair.Id), ("$revision", pair.PolicyRevision),
                ("$recovered", (object?)definitionRecovered ?? DBNull.Value));
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND configuration_revision=$revision AND pair_json=$json AND state<>'retired';",
                cancellationToken, ("$id", pair.Id), ("$revision", pair.PolicyRevision), ("$json", pairJson));
            if (exact != 1)
                throw new InvalidOperationException(
                    "The pair definition changed while nonterminal authority was retained.");
        }
    }

    public async Task<SyncPairSet> SelectPairAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await WriteAsync(async (connection, transaction) =>
        {
            var retained = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND state<>'retired' AND pair_json IS NOT NULL;",
                cancellationToken, ("$id", pairId));
            if (retained != 1)
                throw new InvalidDataException("The selected SQLite sync pair is not retained.");
            await ExecuteAsync(connection, transaction, "UPDATE sync_pair SET selected=0 WHERE selected=1;",
                cancellationToken);
            await ExecuteAsync(connection, transaction, "UPDATE sync_pair SET selected=1 WHERE pair_id=$id;",
                cancellationToken, ("$id", pairId));
        }, cancellationToken);
        return await LoadPairSetAsync(cancellationToken);
    }

    // Local/account authoring uses the same unpublished-root lifecycle as cloud/cloud authoring.
    // Both current account adapters share this path; this is not a provider-pair dispatch table.
    internal sealed record AccountPairReplacementRoots(string InstallationId, string ReplacedPairId,
        long ExpectedReplacedRevision, string LocalRootBindingId, string VolumeId, string VolumeSerialHex,
        string LocalFileIdHex, string AccountRootBindingId, string ProviderAccountId, string NamespaceId);

    internal async Task PrepareAccountPairReplacementAsync(SyncPair replacement,
        SyncAuthorityPairConfiguration configuration, AccountPairReplacementRoots roots,
        CancellationToken cancellationToken = default)
    {
        replacement = RequireAccountPairReplacement(replacement, roots);
        await PreparePairReplacementCoreAsync(replacement, configuration, roots.ReplacedPairId,
            roots.ExpectedReplacedRevision, roots.LocalRootBindingId, roots.AccountRootBindingId,
            (connection, transaction) => RequireAccountPairReplacementRootsAsync(connection, transaction,
                replacement, roots, cancellationToken),
            (connection, transaction) => RegisterAccountPairReplacementRootsCoreAsync(connection, transaction,
                replacement, roots, cancellationToken), cancellationToken);
    }

    private async Task PreparePairReplacementCoreAsync(SyncPair replacement,
        SyncAuthorityPairConfiguration configuration, string replacedPairId, long expectedRevision,
        string leftRootBindingId, string rightRootBindingId,
        Func<SqliteConnection, SqliteTransaction, Task> requirePreparedRoots,
        Func<SqliteConnection, SqliteTransaction, Task> registerRoots, CancellationToken cancellationToken)
    {
        await WriteAsync(async (connection, transaction) =>
        {
            await RequirePairReplacementSourceAsync(connection, transaction, replacement, replacedPairId, expectedRevision,
                cancellationToken);
            var foreign = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$pair AND (state='retired' OR pair_json IS NOT NULL OR selected<>0 OR configuration_revision<>$revision);",
                cancellationToken, ("$pair", replacement.Id), ("$revision", replacement.PolicyRevision));
            if (foreign != 0)
                throw new InvalidOperationException("The replacement identity is already retired, published or owned at another revision.");
            var rootCount = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM root_binding WHERE pair_id=$pair AND side IN ('local','dropbox');",
                cancellationToken, ("$pair", replacement.Id));
            if (rootCount != 0)
            {
                if (rootCount != 2)
                    throw new InvalidOperationException("The replacement root preparation is incomplete; it cannot be adopted or rewritten.");
                await requirePreparedRoots(connection, transaction);
            }
            await RequireCandidateOperationsAsync(connection, transaction, replacement.Id, cancellationToken);
            await RegisterPairConfigurationCoreAsync(connection, transaction, replacement.Id,
                replacement.PolicyRevision, configuration, cancellationToken);

            // Exclude ONLY this exact, revision-checked source's active roots inside the writer
            // transaction. Reuse both ordinary duplicate/account/volume/overlap guards, then retain
            // candidates blocked and restore the source before commit. No visible ownership gap,
            // source retirement, network call or user-file effect occurs during preparation.
            var sourceRoots = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT root_binding_id FROM root_binding WHERE pair_id=$pair AND state='active';";
                command.Parameters.AddWithValue("$pair", replacedPairId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) sourceRoots.Add(reader.GetString(0));
            }
            await ExecuteAsync(connection, transaction,
                "UPDATE root_binding SET state='out_of_scope' WHERE pair_id=$pair AND state='active';",
                cancellationToken, ("$pair", replacedPairId));
            await registerRoots(connection, transaction);
            await ExecuteAsync(connection, transaction,
                "UPDATE root_binding SET state='blocked' WHERE pair_id=$pair AND root_binding_id IN ($local,$remote);",
                cancellationToken, ("$pair", replacement.Id), ("$local", leftRootBindingId),
                ("$remote", rightRootBindingId));
            foreach (var sourceRoot in sourceRoots)
                await ExecuteAsync(connection, transaction,
                    "UPDATE root_binding SET state='active' WHERE root_binding_id=$id AND pair_id=$pair AND state='out_of_scope';",
                    cancellationToken, ("$id", sourceRoot), ("$pair", replacedPairId));
        }, cancellationToken);
    }

    internal async Task<SyncPairSet> PublishAccountPairReplacementAsync(SyncPair replacement,
        AccountPairReplacementRoots roots, CancellationToken cancellationToken = default)
    {
        replacement = RequireAccountPairReplacement(replacement, roots);
        if (replacement.ControlNamespace is not { } control || control.NamespaceId != replacement.Id ||
            control.InstallationId != roots.InstallationId)
            throw new InvalidDataException("The replacement lacks its own verified private staging identity.");
        return await PublishPairReplacementCoreAsync(replacement, roots.ReplacedPairId,
            roots.ExpectedReplacedRevision, roots.LocalRootBindingId, null,
            (connection, transaction) => RequireAccountPairReplacementRootsAsync(connection, transaction,
                replacement, roots, cancellationToken),
            (connection, transaction) => RegisterAccountPairReplacementRootsCoreAsync(connection, transaction,
                replacement, roots, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// R668: the guarded recovery publication of ONE unpublished local/account candidate. It is reachable only
    /// through the private recovery publication path, and it activates this pair's OWN two roots from their
    /// exact blocked preimages inside ONE write transaction together with the pair configuration registration,
    /// after the already-authenticated retained-control recovery of this exact pair, side, root and generation
    /// has proved ownership. This is not a public blocked-root override: ordinary public root registration still
    /// refuses a blocked row, and no caller can reach this method without the exact unpublished revision, the
    /// exact own blocked preimage (pair, side, generation, identity JSON AND display path) and the retained
    /// recovery proof. A mismatch on either side refuses before any rewrite, and a second-side refusal rolls
    /// back the first side so no pair is published.
    /// </summary>
    internal sealed record RecoveredCandidateOwnRoots(string InstallationId, string PairId,
        long ExpectedUnpublishedRevision, string LocalRootBindingId, string VolumeId, string VolumeSerialHex,
        string LocalFileIdHex, string AccountRootBindingId, string ProviderAccountId, string RootNamespaceId,
        string FolderId);

    /// <summary>
    /// R672: normal authoring always retains a fresh unpublished candidate at configuration revision 1 and writes
    /// its own roots at root GENERATION 1, independently of any later policy revision. The recovered activation
    /// binds the generation to this constant instead of assuming it coincides with the policy revision, and it
    /// enforces the matching normal-authoring invariant on the candidate revision rather than inferring it.
    /// </summary>
    private const long NormalAuthoringRootGeneration = 1;

    internal async Task ActivateRecoveredCandidateOwnRootsAsync(SyncPair pair,
        RecoveredCandidateOwnRoots roots, SyncAuthorityPairConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (var value in new[]
                 {
                     roots.InstallationId, roots.PairId, roots.LocalRootBindingId, roots.VolumeId,
                     roots.VolumeSerialHex, roots.LocalFileIdHex, roots.AccountRootBindingId,
                     roots.ProviderAccountId, roots.RootNamespaceId, roots.FolderId
                 })
            RequireIdentifier(value, nameof(roots));
        if (roots.ExpectedUnpublishedRevision < 1) throw new ArgumentOutOfRangeException(nameof(roots));
        if (roots.ExpectedUnpublishedRevision != NormalAuthoringRootGeneration)
            throw new InvalidDataException(
                "A fresh unpublished local/account candidate is retained at configuration revision 1 with root " +
                "generation 1 by normal authoring; the recovered activation refuses another revision rather than " +
                "assuming its root generation.");
        if (!string.Equals(roots.PairId, pair.Id, StringComparison.Ordinal) ||
            pair.PolicyRevision != roots.ExpectedUnpublishedRevision)
            throw new InvalidDataException(
                "The recovered publication draft does not carry its own unpublished candidate identity and revision.");
        if (string.Equals(roots.LocalRootBindingId, roots.AccountRootBindingId, StringComparison.Ordinal))
            throw new InvalidDataException("A recovered publication requires two distinct own root bindings.");
        var localPath = NormalizeAuthorityPath("local", pair.LocalRoot);
        var localIdentity = JsonSerializer.Serialize(new
        {
            volumeId = roots.VolumeId,
            volumeSerial = roots.VolumeSerialHex,
            fileId = roots.LocalFileIdHex
        });
        var accountPath = NormalizeAuthorityPath("dropbox", pair.DropboxRoot);
        var accountIdentity = JsonSerializer.Serialize(new
        {
            providerAccountId = roots.ProviderAccountId,
            rootNamespaceId = roots.RootNamespaceId,
            folderId = roots.FolderId
        });
        await WriteAsync(async (connection, transaction) =>
        {
            var candidate = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$pair AND state<>'retired' AND pair_json IS NULL AND selected=0 AND configuration_revision=$revision;",
                cancellationToken, ("$pair", roots.PairId), ("$revision", roots.ExpectedUnpublishedRevision));
            if (candidate != 1)
                throw new InvalidOperationException(
                    "The unpublished candidate pair is not retained at its exact current revision; recovered activation refuses.");
            // The exact blocked own preimage is verified for BOTH sides BEFORE any rewrite. The guarded upsert
            // does not compare display_path, so this explicit check is what refuses a changed path.
            await RequireExactOwnRootPreimageAsync(connection, transaction, roots.LocalRootBindingId,
                roots.PairId, "local", NormalAuthoringRootGeneration, localIdentity, localPath,
                cancellationToken);
            await RequireExactOwnRootPreimageAsync(connection, transaction, roots.AccountRootBindingId,
                roots.PairId, "dropbox", NormalAuthoringRootGeneration, accountIdentity, accountPath,
                cancellationToken);
            // Only the already-authenticated retained-control recovery of this exact pair, side, root and
            // generation may activate its own blocked preimages; a foreign or unresolved authority refuses here.
            await RequireCompletedOrRecoveredCandidateControlAsync(connection, transaction, roots.PairId,
                roots.LocalRootBindingId, cancellationToken);
            await RegisterPairConfigurationCoreAsync(connection, transaction, roots.PairId,
                roots.ExpectedUnpublishedRevision, configuration, cancellationToken);
            await RegisterLocalRootBindingCoreAsync(connection, transaction, roots.LocalRootBindingId, roots.PairId,
                NormalAuthoringRootGeneration, roots.InstallationId, roots.VolumeId, roots.VolumeSerialHex,
                roots.LocalFileIdHex, localIdentity, localPath, cancellationToken,
                allowBlockedCandidatePreimage: true, partitionCandidate: pair);
            await RegisterAccountRootBindingCoreAsync(connection, transaction, roots.AccountRootBindingId,
                roots.PairId, NormalAuthoringRootGeneration, roots.InstallationId, roots.ProviderAccountId,
                roots.RootNamespaceId, roots.FolderId, accountIdentity, accountPath, cancellationToken,
                allowBlockedCandidatePreimage: true, partitionCandidate: pair);
        }, cancellationToken);
    }

    /// <summary>
    /// R668: the same guarded activation for the LOCAL root only, used by the typed Google-Drive local/cloud
    /// publication in either placement, whose cloud root continues to be retained by its own typed candidate
    /// path. The exact blocked own preimage and the authenticated retained-control recovery are both required.
    /// </summary>
    internal async Task ActivateRecoveredLocalRootBindingAsync(SyncPair pair, long expectedUnpublishedRevision,
        string installationId, string rootBindingId, string volumeId, string volumeSerialHex,
        string localFileIdHex, string displayPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pair);
        foreach (var value in new[] { installationId, rootBindingId, volumeId, volumeSerialHex, localFileIdHex })
            RequireIdentifier(value, nameof(rootBindingId));
        if (expectedUnpublishedRevision < 1) throw new ArgumentOutOfRangeException(nameof(expectedUnpublishedRevision));
        if (expectedUnpublishedRevision != NormalAuthoringRootGeneration)
            throw new InvalidDataException(
                "A fresh unpublished local/account candidate is retained at configuration revision 1 with root " +
                "generation 1 by normal authoring; the recovered activation refuses another revision rather than " +
                "assuming its root generation.");
        if (!string.Equals(rootBindingId, SyncAuthorityRuntimeHost.LocalRecoveryRootId(pair.Id, volumeId, localFileIdHex), StringComparison.Ordinal))
            throw new InvalidDataException("The recovered local root id is not the pair's own normal-authoring local root identity.");
        var normalizedPath = NormalizeAuthorityPath("local", displayPath);
        var identityJson = JsonSerializer.Serialize(new { volumeId, volumeSerial = volumeSerialHex, fileId = localFileIdHex });
        await WriteAsync(async (connection, transaction) =>
        {
            var candidate = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$pair AND state<>'retired' AND pair_json IS NULL AND selected=0 AND configuration_revision=$revision;",
                cancellationToken, ("$pair", pair.Id), ("$revision", expectedUnpublishedRevision));
            if (candidate != 1)
                throw new InvalidOperationException(
                    "The unpublished candidate pair is not retained at its exact current revision; recovered activation refuses.");
            await RequireExactOwnRootPreimageAsync(connection, transaction, rootBindingId, pair.Id, "local",
                NormalAuthoringRootGeneration, identityJson, normalizedPath, cancellationToken);
            await RequireCompletedOrRecoveredCandidateControlAsync(connection, transaction, pair.Id, rootBindingId,
                cancellationToken);
            await RegisterLocalRootBindingCoreAsync(connection, transaction, rootBindingId, pair.Id,
                NormalAuthoringRootGeneration, installationId, volumeId, volumeSerialHex, localFileIdHex,
                identityJson, normalizedPath, cancellationToken, allowBlockedCandidatePreimage: true,
                partitionCandidate: pair);
        }, cancellationToken);
    }

    private async Task<SyncPairSet> PublishPairReplacementCoreAsync(SyncPair replacement,
        string replacedPairId, long expectedRevision, string leftRootBindingId, string? rightControlRootBindingId,
        Func<SqliteConnection, SqliteTransaction, Task> requirePreparedRoots,
        Func<SqliteConnection, SqliteTransaction, Task> registerRoots, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(replacement);
        await WriteAsync(async (connection, transaction) =>
        {
            await RequirePairReplacementSourceAsync(connection, transaction, replacement, replacedPairId, expectedRevision,
                cancellationToken);
            var candidate = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$pair AND state<>'retired' AND pair_json IS NULL AND selected=0 AND configuration_revision=$revision;",
                cancellationToken, ("$pair", replacement.Id), ("$revision", replacement.PolicyRevision));
            if (candidate != 1)
                throw new InvalidOperationException("The unpublished replacement pair no longer matches its prepared revision.");
            await requirePreparedRoots(connection, transaction);
            await RequireCandidateOperationsAsync(connection, transaction, replacement.Id, cancellationToken);
            await RequireCompletedOrRecoveredCandidateControlAsync(connection, transaction, replacement.Id,
                leftRootBindingId, cancellationToken);
            if (rightControlRootBindingId is not null)
                await RequireCompletedCandidateControlAsync(connection, transaction, replacement.Id,
                    rightControlRootBindingId, cancellationToken, side: "dropbox");
            await ExecuteAsync(connection, transaction,
                "UPDATE root_binding SET state='out_of_scope',observed_utc=$utc WHERE pair_id=$pair AND state='active';",
                cancellationToken, ("$pair", replacedPairId), ("$utc", Utc(DateTimeOffset.UtcNow)));
            // Account/volume and all unrelated roots are checked AGAIN at publication. A race or
            // failure rolls back this transaction, leaving the old pair and selection intact.
            await registerRoots(connection, transaction);
            await ReplacePairDefinitionCoreAsync(connection, transaction, replacedPairId, replacement,
                json, cancellationToken);
            _ = await LoadPairSetCoreAsync(connection, transaction, cancellationToken);
        }, cancellationToken);
        return await LoadPairSetAsync(cancellationToken);
    }

    private static SyncPair RequireAccountPairReplacement(SyncPair replacement,
        AccountPairReplacementRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        replacement = SyncPairStore.NormalizePair(replacement);
        foreach (var id in new[] { roots.InstallationId, roots.ReplacedPairId, roots.LocalRootBindingId,
            roots.VolumeId, roots.VolumeSerialHex, roots.LocalFileIdHex, roots.AccountRootBindingId,
            roots.ProviderAccountId, roots.NamespaceId }) RequireIdentifier(id, nameof(roots));
        if (roots.ExpectedReplacedRevision < 1 || replacement.PolicyRevision != 1 ||
            replacement.Id == roots.ReplacedPairId || replacement.InitialReviewApproved != false ||
            roots.LocalRootBindingId == roots.AccountRootBindingId ||
            replacement.LocalVolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture) != roots.VolumeSerialHex ||
            replacement.LocalRootFileId.ToString("x16", CultureInfo.InvariantCulture) != roots.LocalFileIdHex)
            throw new InvalidDataException("An endpoint-changing replacement requires new exact roots, revision one and a fresh first-sync review.");
        return replacement;
    }

    private static async Task RequirePairReplacementSourceAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncPair replacement, string replacedPairId, long expectedRevision,
        CancellationToken cancellationToken)
    {
        var source = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM sync_pair WHERE pair_id=$pair AND state<>'retired' AND pair_json IS NOT NULL AND configuration_revision=$revision;",
            cancellationToken, ("$pair", replacedPairId), ("$revision", expectedRevision));
        if (source != 1)
            throw new InvalidOperationException("The pair selected for replacement changed after it was reviewed.");
        var blocking = await ScalarLongAsync(connection, transaction,
            $"""
            SELECT count(*) FROM operation o
            WHERE o.status NOT IN ('completed','cancelled','superseded')
              AND NOT(o.status='blocked' AND {KnownNoEffectRefusalPredicate})
              AND (o.pair_id IN ($old,$new) OR EXISTS(
                SELECT 1 FROM json_each(o.preconditions_json,'$.planningAuthorities') r
                WHERE json_extract(r.value,'$.PairId') IN ($old,$new)));
            """, cancellationToken, ("$old", replacedPairId), ("$new", replacement.Id));
        if (blocking != 0)
            throw new InvalidOperationException("Folder locations cannot change while queued, interrupted, uncertain, leased or cleanup work remains.");
    }

    private static async Task RequireAccountPairReplacementRootsAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncPair pair, AccountPairReplacementRoots roots,
        CancellationToken cancellationToken)
    {
        // Use the identical byte-exact blocked-root preimage check already used by cloud/cloud
        // publication. Neither a published root nor a changed path/identity may be downgraded.
            await RequireExactBlockedCandidateRootAsync(connection, transaction, roots.LocalRootBindingId,
                pair.Id, "local", 1, AccountPairLocalIdentity(roots), NormalizeAuthorityPath("local", pair.LocalRoot),
                cancellationToken);
            await RequireExactBlockedCandidateRootAsync(connection, transaction, roots.AccountRootBindingId,
                pair.Id, "dropbox", 1, AccountPairRemoteIdentity(pair, roots), NormalizeAuthorityPath("dropbox", pair.DropboxRoot),
                cancellationToken);
    }

    private static string AccountPairLocalIdentity(AccountPairReplacementRoots roots) =>
        JsonSerializer.Serialize(new { volumeId = roots.VolumeId, volumeSerial = roots.VolumeSerialHex,
            fileId = roots.LocalFileIdHex });

    private static string AccountPairRemoteIdentity(SyncPair pair, AccountPairReplacementRoots roots) =>
        JsonSerializer.Serialize(new { providerAccountId = roots.ProviderAccountId,
            rootNamespaceId = roots.NamespaceId, folderId = pair.DropboxFolderId });

    private static async Task RegisterAccountPairReplacementRootsCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncPair pair, AccountPairReplacementRoots roots,
        CancellationToken cancellationToken)
    {
        await RegisterLocalRootBindingCoreAsync(connection, transaction, roots.LocalRootBindingId, pair.Id, 1,
            roots.InstallationId, roots.VolumeId, roots.VolumeSerialHex, roots.LocalFileIdHex,
            AccountPairLocalIdentity(roots), NormalizeAuthorityPath("local", pair.LocalRoot), cancellationToken,
            allowBlockedCandidatePreimage: true);
        await RegisterAccountRootBindingCoreAsync(connection, transaction, roots.AccountRootBindingId, pair.Id, 1,
            roots.InstallationId, roots.ProviderAccountId, roots.NamespaceId, pair.DropboxFolderId,
            AccountPairRemoteIdentity(pair, roots), NormalizeAuthorityPath("dropbox", pair.DropboxRoot), cancellationToken,
            allowBlockedCandidatePreimage: true);
    }

    internal sealed record LocalPairReplacementRoots(string InstallationId, string ReplacedPairId,
        long ExpectedReplacedRevision, string LeftRootBindingId, string LeftVolumeId,
        string RightRootBindingId, string RightVolumeId);

    internal async Task PrepareLocalPairReplacementAsync(SyncPair replacement,
        SyncAuthorityPairConfiguration configuration, LocalPairReplacementRoots roots,
        CancellationToken cancellationToken = default)
    {
        replacement = RequireLocalPairReplacement(replacement, roots);
        await PreparePairReplacementCoreAsync(replacement, configuration, roots.ReplacedPairId,
            roots.ExpectedReplacedRevision, roots.LeftRootBindingId, roots.RightRootBindingId,
            (connection, transaction) => RequireLocalPairReplacementRootsAsync(connection, transaction,
                replacement, roots, cancellationToken),
            (connection, transaction) => RegisterLocalPairReplacementRootsCoreAsync(connection, transaction,
                replacement, roots, cancellationToken), cancellationToken);
    }

    internal async Task<SyncPairSet> PublishLocalPairReplacementAsync(SyncPair replacement,
        LocalPairReplacementRoots roots, CancellationToken cancellationToken = default)
    {
        replacement = RequireLocalPairReplacement(replacement, roots);
        if (replacement.ControlNamespace is not { } left || left.NamespaceId != replacement.Id ||
            left.InstallationId != roots.InstallationId || replacement.RightControlNamespace is not { } right ||
            right.NamespaceId != SyncEndpointPairRuntime.RightControlNamespaceId(replacement.Id) ||
            right.InstallationId != roots.InstallationId)
            throw new InvalidDataException("The replacement requires its own verified private staging on both local endpoints.");
        return await PublishPairReplacementCoreAsync(replacement, roots.ReplacedPairId,
            roots.ExpectedReplacedRevision, roots.LeftRootBindingId, roots.RightRootBindingId,
            (connection, transaction) => RequireLocalPairReplacementRootsAsync(connection, transaction,
                replacement, roots, cancellationToken),
            (connection, transaction) => RegisterLocalPairReplacementRootsCoreAsync(connection, transaction,
                replacement, roots, cancellationToken), cancellationToken);
    }

    private static SyncPair RequireLocalPairReplacement(SyncPair replacement, LocalPairReplacementRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        replacement = SyncPairStore.NormalizePair(replacement);
        foreach (var value in new[] { roots.InstallationId, roots.ReplacedPairId, roots.LeftRootBindingId,
            roots.LeftVolumeId, roots.RightRootBindingId, roots.RightVolumeId }) RequireIdentifier(value, nameof(roots));
        var resolution = SyncEndpointPairRuntime.Resolve(replacement, replacement.EndpointDocument);
        if (!resolution.IsLocalToLocal || SyncProviders.Normalize(replacement.Provider) != SyncProviders.Local ||
            roots.ExpectedReplacedRevision < 1 || replacement.PolicyRevision != 1 ||
            replacement.Id == roots.ReplacedPairId || replacement.InitialReviewApproved != false ||
            roots.LeftRootBindingId == roots.RightRootBindingId)
            throw new InvalidDataException("An accountless endpoint replacement requires two exact local roots, a new identity and fresh first-sync review.");
        return replacement;
    }

    private static string LocalPairLeftRootIdentity(SyncPair pair, LocalPairReplacementRoots roots) =>
        JsonSerializer.Serialize(new { volumeId = roots.LeftVolumeId,
            volumeSerial = pair.LocalVolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture),
            fileId = pair.LocalRootFileId.ToString("x16", CultureInfo.InvariantCulture) });

    private static string LocalPairRightRootIdentity(SyncPair pair)
    {
        var right = SyncEndpointPairRuntime.Resolve(pair, pair.EndpointDocument).RightLocal!;
        return JsonSerializer.Serialize(new { localRootPath = right.RootPath, localRootIdentity = right.IdentityToken });
    }

    private static async Task RequireLocalPairReplacementRootsAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncPair pair, LocalPairReplacementRoots roots,
        CancellationToken cancellationToken)
    {
        await RequireExactBlockedCandidateRootAsync(connection, transaction, roots.LeftRootBindingId,
            pair.Id, "local", 1, LocalPairLeftRootIdentity(pair, roots), NormalizeAuthorityPath("local", pair.LocalRoot),
            cancellationToken);
        await RequireExactBlockedCandidateRootAsync(connection, transaction, roots.RightRootBindingId,
            pair.Id, "dropbox", 1, LocalPairRightRootIdentity(pair), NormalizeAuthorityPath("dropbox", pair.DropboxRoot),
            cancellationToken);
    }

    private static async Task RegisterLocalPairReplacementRootsCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncPair pair, LocalPairReplacementRoots roots,
        CancellationToken cancellationToken)
    {
        await RegisterLocalRootBindingCoreAsync(connection, transaction, roots.LeftRootBindingId, pair.Id, 1,
            roots.InstallationId, roots.LeftVolumeId,
            pair.LocalVolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture),
            pair.LocalRootFileId.ToString("x16", CultureInfo.InvariantCulture), LocalPairLeftRootIdentity(pair, roots),
            NormalizeAuthorityPath("local", pair.LocalRoot), cancellationToken, allowBlockedCandidatePreimage: true);
        var right = SyncEndpointPairRuntime.Resolve(pair, pair.EndpointDocument).RightLocal!;
        await RequireActiveLocalVolumeCoreAsync(connection, transaction, roots.InstallationId, roots.RightVolumeId,
            right.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture), cancellationToken);
        await RegisterRootBindingCoreAsync(connection, transaction, roots.RightRootBindingId, pair.Id, "dropbox", 1,
            LocalPairRightRootIdentity(pair), NormalizeAuthorityPath("dropbox", right.RootPath), cancellationToken,
            allowBlockedCandidatePreimage: true);
    }

    public async Task<SyncPairSet> ReplacePairDefinitionAsync(string replacedPairId, SyncPair replacement,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(replacedPairId, nameof(replacedPairId));
        replacement = SyncPairStore.NormalizePair(replacement);
        if (string.Equals(replacedPairId, replacement.Id, StringComparison.Ordinal))
            return await RetainPairDefinitionAsync(replacement, select: true, cancellationToken);
        var replacementJson = JsonSerializer.Serialize(replacement);
        await WriteAsync(async (connection, transaction) =>
            await ReplacePairDefinitionCoreAsync(connection, transaction, replacedPairId, replacement,
                replacementJson, cancellationToken),
            cancellationToken);
        return await LoadPairSetAsync(cancellationToken);
    }

    /// <summary>
    /// B1-C6: the replacement core, extracted from the existing public replacement inside one explicit scope
    /// block whose braces were verified to balance. Publication calls this same body inside its own transaction,
    /// and the public signature and every legacy behaviour are unchanged.
    /// </summary>
    private async Task ReplacePairDefinitionCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string replacedPairId, SyncPair replacement, string replacementJson,
        CancellationToken cancellationToken)
    {
        {
            var source = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND state<>'retired' AND pair_json IS NOT NULL;",
                cancellationToken, ("$id", replacedPairId));
            if (source != 1)
                throw new InvalidDataException("The sync pair selected for folder replacement is not retained.");
            var nonterminal = await ScalarLongAsync(connection, transaction,
                $"""
                SELECT count(*) FROM operation o
                WHERE o.pair_id IN ($source,$replacement) AND o.status NOT IN ('completed','cancelled','superseded')
                  AND NOT(o.status='blocked' AND {KnownNoEffectRefusalPredicate});
                """,
                cancellationToken, ("$source", replacedPairId), ("$replacement", replacement.Id));
            if (nonterminal != 0)
                throw new InvalidOperationException(
                    "Folder locations cannot change while queued, interrupted, blocked, or cleanup work remains.");
            var target = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND configuration_revision=$revision AND state<>'retired';",
                cancellationToken, ("$id", replacement.Id), ("$revision", replacement.PolicyRevision));
            if (target != 1)
                throw new InvalidOperationException("The replacement pair authority was not prepared exactly.");
            await ExecuteAsync(connection, transaction, "UPDATE sync_pair SET selected=0 WHERE selected=1;",
                cancellationToken);
            await ExecuteAsync(connection, transaction,
                "UPDATE sync_pair SET pair_json=$json,selected=1 WHERE pair_id=$id AND configuration_revision=$revision AND state<>'retired';",
                cancellationToken, ("$json", replacementJson), ("$id", replacement.Id),
                ("$revision", replacement.PolicyRevision));
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND configuration_revision=$revision AND pair_json=$json AND selected=1 AND state<>'retired';",
                cancellationToken, ("$id", replacement.Id), ("$revision", replacement.PolicyRevision),
                ("$json", replacementJson));
            if (exact != 1)
                throw new InvalidOperationException("The replacement pair definition was not retained exactly.");
            await ExecuteAsync(connection, transaction,
                "UPDATE sync_pair SET state='retired',selected=0 WHERE pair_id=$id AND state<>'retired';",
                cancellationToken, ("$id", replacedPairId));
            await ExecuteAsync(connection, transaction,
                "UPDATE root_binding SET state='out_of_scope',observed_utc=$utc WHERE pair_id=$id AND state='active';",
                cancellationToken, ("$id", replacedPairId), ("$utc", Utc(DateTimeOffset.UtcNow)));
        }
    }


    // ----- BIND1-1/BIND1-2: unpublished candidate roots and the one guarded atomic publication --------

    /// <summary>One retained candidate root row: the exact blocked preimage preparation wrote.</summary>
    private sealed record CloudPairCandidateRoot(string RootBindingId, string PairId, string Side, long Generation,
        string IdentityJson, string DisplayPath);

    /// <summary>
    /// B1-C3: retains one candidate endpoint root through the one guarded insertion path in the existing blocked
    /// state. The blocked preimage is admitted only here, so an ordinary caller can never activate a blocked root
    /// and blocked-to-blocked replay stays exact.
    /// </summary>
    private static async Task UpsertBlockedCandidateRootCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string rootBindingId, string pairId, string side, long generation,
        string identityJson, string normalizedDisplayPath, CancellationToken cancellationToken) =>
        await UpsertRootBindingCoreAsync(connection, transaction, rootBindingId, pairId, side, generation,
            identityJson, normalizedDisplayPath, cancellationToken, "blocked",
            allowBlockedCandidatePreimage: true);

    /// <summary>B1-C3: the exact blocked-preimage check. Any drift refuses without a write.</summary>
    private static async Task RequireExactBlockedCandidateRootAsync(SqliteConnection connection,
        SqliteTransaction transaction, string rootBindingId, string pairId, string side, long generation,
        string identityJson, string normalizedDisplayPath, CancellationToken cancellationToken)
    {
        var exact = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM root_binding WHERE root_binding_id=$id AND pair_id=$pair AND side=$side AND generation=$generation AND identity_json=$identity AND display_path=$path AND state='blocked';",
            cancellationToken, ("$id", rootBindingId), ("$pair", pairId), ("$side", side),
            ("$generation", generation), ("$identity", identityJson), ("$path", normalizedDisplayPath));
        if (exact != 1)
            throw new InvalidOperationException(
                "The retained candidate root does not carry the exact blocked identity, side, generation, path and state that preparation wrote.");
    }

    /// <summary>
    /// R668: the exact OWN preimage check for the guarded recovered publication. A row that already exists for
    /// this deterministic own root binding id must carry EXACTLY this pair, side, generation, identity JSON and
    /// display path, and it must still be in the ordinary 'active' state or the unpublished 'blocked' state.
    /// The guarded upsert does not compare display_path, so this check is what refuses a changed path before any
    /// rewrite. An absent row is a fresh own root and is left to the ordinary guarded insertion.
    /// </summary>
    private static async Task RequireExactOwnRootPreimageAsync(SqliteConnection connection,
        SqliteTransaction transaction, string rootBindingId, string pairId, string side, long generation,
        string identityJson, string normalizedDisplayPath, CancellationToken cancellationToken)
    {
        var existing = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM root_binding WHERE root_binding_id=$id;",
            cancellationToken, ("$id", rootBindingId));
        if (existing == 0) return;
        var exact = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM root_binding WHERE root_binding_id=$id AND pair_id=$pair AND side=$side AND generation=$generation AND identity_json=$identity AND display_path=$path AND state IN ('active','blocked');",
            cancellationToken, ("$id", rootBindingId), ("$pair", pairId), ("$side", side),
            ("$generation", generation), ("$identity", identityJson), ("$path", normalizedDisplayPath));
        if (exact != 1)
            throw new InvalidOperationException(
                "The retained own root does not carry the exact identity, side, generation, path and state the recovered publication requires.");
    }

    /// <summary>Reads one retained blocked candidate root on one side of one pair.</summary>
    private static async Task<CloudPairCandidateRoot> LoadCandidateRootAsync(SqliteConnection connection,
        SqliteTransaction transaction, string pairId, string side, long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT root_binding_id,generation,identity_json,display_path,state FROM root_binding WHERE pair_id=$pair AND side=$side;";
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$side", side);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException(
                "The prepared cloud-pair candidate carries no retained root on this side.");
        var root = new CloudPairCandidateRoot(reader.GetString(0), pairId, side, reader.GetInt64(1),
            reader.GetString(2), reader.GetString(3));
        if (!string.Equals(reader.GetString(4), "blocked", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The prepared cloud-pair candidate root is not in the blocked state; publication is refused.");
        if (root.Generation != expectedRevision)
            throw new InvalidDataException(
                "The prepared cloud-pair candidate root belongs to another configuration revision.");
        return root;
    }

    /// <summary>
    /// B1-C1: publication requires the EXACT existing single pair-private staging allocation owned and locked by
    /// the candidate logical-left root - the left pair_control_namespace bound to an active left control
    /// namespace whose namespace allocation is verified and physically bound, whose owning operation is a
    /// completed AllocateControl of this candidate pair AND whose lock operation-member names the exact left
    /// candidate root. There is no right-side namespace and no manual SQL substitute in the fixture.
    /// </summary>
    private static async Task RequireCompletedCandidateControlAsync(SqliteConnection connection,
        SqliteTransaction transaction, string pairId, string leftRootBindingId,
        CancellationToken cancellationToken, string side = "local")
    {
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        var complete = await ScalarLongAsync(connection, transaction,
            """
            SELECT count(*) FROM pair_control_namespace b
            JOIN control_namespace c ON c.control_namespace_id=b.control_namespace_id
            JOIN namespace_allocation a ON a.namespace_allocation_id=c.namespace_allocation_id
            JOIN operation o ON o.operation_id=a.operation_id
            JOIN operation_member m ON m.operation_id=o.operation_id AND m.role='lock'
            WHERE b.pair_id=$pair AND b.side=$side AND b.status='active'
              AND c.side=$side AND c.status='active'
              AND a.side=$side AND a.status='verified' AND a.bound_identity IS NOT NULL
              AND o.kind='allocate_control' AND o.status='completed' AND o.pair_id=$pair
              AND m.root_binding_id=$root;
            """,
            cancellationToken, ("$pair", pairId), ("$root", leftRootBindingId), ("$side", side));
        if (complete != 1)
            throw new InvalidOperationException(
                "Publication requires the exact completed private staging allocation of this candidate, bound to its logical-left root; a missing, foreign or unresolved staging refuses.");
    }

    /// <summary>
    /// B1-C2/B1-C5: the exact nonterminal and planning-reference work check of the candidate and the replaced
    /// pair, using the existing planning-authority JSON shape rather than a loose text match.
    /// </summary>
    private static async Task RequireNoPublishBlockingWorkAsync(SqliteConnection connection,
        SqliteTransaction transaction, string candidatePairId, string? replacedPairId,
        CancellationToken cancellationToken)
    {
        // RC639-4.INTEGRATION-1: only the exact authenticated retained-control recovery non-creating
        // collision operation of THIS candidate pair is treated as resolved. Every other queued,
        // interrupted, blocked, cleanup or planning-referenced record keeps refusing unchanged, the
        // immutable blocked records stay byte-identical, and no global KnownConflict waiver is added.
        var recoveryInstallation = await ScalarStringAsync(connection, transaction,
            "SELECT installation_id FROM installation ORDER BY installation_id LIMIT 1;", cancellationToken);
        var recoveredCollision = recoveryInstallation is null ? null
            : await ResolveRecoveredNonCreatingCollisionOperationIdAsync(connection, transaction,
                candidatePairId, recoveryInstallation, cancellationToken);
        var blocking = await ScalarLongAsync(connection, transaction,
            """
            SELECT count(*) FROM operation o
            WHERE o.status NOT IN ('completed','cancelled','superseded')
              AND (o.pair_id=$candidate OR o.pair_id=$replaced OR EXISTS(
                    SELECT 1 FROM json_each(o.preconditions_json,'$.planningAuthorities') retained
                    WHERE json_extract(retained.value,'$.PairId') IN ($candidate,$replaced)))
              AND ($recovered IS NULL OR o.operation_id<>$recovered);
            """,
            cancellationToken, ("$candidate", candidatePairId), ("$replaced", replacedPairId ?? "<none>"),
            ("$recovered", (object?)recoveredCollision ?? DBNull.Value));
        if (blocking != 0)
            throw new InvalidOperationException(
                "Cloud-pair authoring cannot publish while queued, interrupted, blocked, cleanup or planning-referenced work remains.");
    }

    /// <summary>
    /// B1-C2/B1-C5: revalidates BOTH exact candidate accounts inside the publication transaction, before any
    /// activation, from the prepared roots rather than from a caller claim.
    /// </summary>
    private static async Task RequireCandidateAccountsAsync(SqliteConnection connection,
        SqliteTransaction transaction, string installationId, CloudPairCandidateRoot left,
        CloudPairCandidateRoot right, CancellationToken cancellationToken)
    {
        var leftIdentity = RequireReadCandidateIdentity(left.IdentityJson);
        var rightIdentity = RequireReadCandidateIdentity(right.IdentityJson);
        await RequireActiveCandidateAccountAsync(connection, transaction, installationId, leftIdentity.Provider,
            leftIdentity.ProviderAccountId, leftIdentity.NamespaceId, cancellationToken);
        await RequireActiveCandidateAccountAsync(connection, transaction, installationId, rightIdentity.Provider,
            rightIdentity.ProviderAccountId, rightIdentity.NamespaceId, cancellationToken);
    }

    /// <summary>B1-C6: the shared typed reader of one retained cloud root identity.</summary>
    private static (string Provider, string ProviderAccountId, string NamespaceId, string FolderId)
        RequireReadCandidateIdentity(string identityJson) =>
        SyncAuthorityCloudRootPersistence.TryRead(identityJson, out var provider, out var providerAccountId,
            out var namespaceId, out var folderId)
            ? (provider!, providerAccountId!, namespaceId!, folderId!)
            : throw new InvalidDataException(
                "The retained candidate root identity is not a readable typed cloud root identity.");

    /// <summary>
    /// B1-C1/B1-C2/B1-C3/B1-C5: the ONE guarded atomic publication and rebind of an unpublished cloud-pair
    /// candidate. Every recheck, account validation, activation and pair write happens inside one SQLite
    /// transaction; the replaced pair's own active roots leave the guard only inside it; the second candidate is
    /// compared with the first through the unchanged guarded validator; and no network, provider or filesystem
    /// call is held while it runs.
    /// </summary>
    internal async Task<SyncPairSet> PublishCloudPairCandidateAsync(string candidatePairId, SyncPair replacement,
        string? replacedPairId, long? expectedReplacedRevision, string installationId,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> leftHierarchies,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> rightHierarchies,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(candidatePairId, nameof(candidatePairId));
        RequireIdentifier(installationId, nameof(installationId));
        ArgumentNullException.ThrowIfNull(replacement);
        if (!string.Equals(candidatePairId, replacement.Id, StringComparison.Ordinal))
            throw new InvalidDataException("The published pair is not the prepared candidate itself.");
        replacement = SyncPairStore.NormalizePair(replacement);
        RequireDisabledUnapprovedCandidate(replacement);
        var replacementJson = JsonSerializer.Serialize(replacement);
        ArgumentNullException.ThrowIfNull(leftHierarchies);
        ArgumentNullException.ThrowIfNull(rightHierarchies);
        var projection = SyncCloudPairConfiguration.RequireProjection(replacement);
        if (replacedPairId is not null)
        {
            RequireIdentifier(replacedPairId, nameof(replacedPairId));
            if (string.Equals(replacedPairId, candidatePairId, StringComparison.Ordinal))
                throw new InvalidDataException("A cloud-pair rebind replaces a different retained pair.");
            if (expectedReplacedRevision is null)
                throw new InvalidDataException(
                    "An endpoint-changing rebind requires the exact expected retained pair revision.");
        }
        await WriteAsync(async (connection, transaction) =>
        {
            var candidate = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND state<>'retired' AND pair_json IS NULL AND selected=0 AND configuration_revision=$revision;",
                cancellationToken, ("$id", candidatePairId), ("$revision", replacement.PolicyRevision));
            if (candidate != 1)
                throw new InvalidDataException(
                    "The cloud-pair candidate is not retained exactly as prepared; publication is refused.");
            var left = await LoadCandidateRootAsync(connection, transaction, candidatePairId,
                SyncAuthorityCloudSides.LogicalLeft, replacement.PolicyRevision, cancellationToken);
            var right = await LoadCandidateRootAsync(connection, transaction, candidatePairId,
                SyncAuthorityCloudSides.LogicalRight, replacement.PolicyRevision, cancellationToken);
            RequirePreparedRootAgreement(projection.Left, left, SyncAuthorityCloudSides.LogicalLeft);
            RequirePreparedRootAgreement(projection.Right, right, SyncAuthorityCloudSides.LogicalRight);
            await RegisterPairConfigurationCoreAsync(connection, transaction, candidatePairId,
                replacement.PolicyRevision, SyncAuthorityPairConfiguration.FromPair(replacement), cancellationToken);
            await RequireCandidateAccountsAsync(connection, transaction, installationId, left, right,
                cancellationToken);
            await RequireNoPublishBlockingWorkAsync(connection, transaction, candidatePairId, replacedPairId,
                cancellationToken);
            await RequireCandidateOperationsAsync(connection, transaction, candidatePairId, cancellationToken);
            if (replacedPairId is not null)
            {
                var old = await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND state<>'retired' AND pair_json IS NOT NULL AND configuration_revision=$revision;",
                    cancellationToken, ("$id", replacedPairId), ("$revision", expectedReplacedRevision!.Value));
                if (old != 1)
                    throw new InvalidOperationException(
                        "The retained pair selected for replacement changed after it was reviewed; publication is refused.");
            }
            await RequireCompletedOrRecoveredCandidateControlAsync(connection, transaction, candidatePairId,
                left.RootBindingId, cancellationToken);
            // Both prepared candidates are legitimate inputs, although neither is active yet. Validate
            // the entire supplied set before changing the old pair, then pass only the authorities
            // relevant to each activation to the unchanged overlap guard inside this transaction.
            var preparedAuthorities = new[] { CandidateAuthority(left), CandidateAuthority(right) };
            var publicationAuthorities = (await LoadActiveCloudAuthoritiesAsync(connection, transaction,
                candidatePairId, cancellationToken)).Concat(preparedAuthorities);
            // D2: when a caller supplies no fresh hierarchy evidence for a side, the ONE bound
            // validation context is built from that candidate root's RETAINED observation, resolved
            // inside this same publication transaction. The retained observation is last-observed
            // configuration evidence only; it must still agree with the candidate root id, generation
            // and current active account, and every effect path elsewhere keeps its fresh context.
            if (leftHierarchies.Count == 0)
                leftHierarchies = await RetainedHierarchyForCandidateAsync(connection, transaction,
                    installationId, candidatePairId, SyncAuthorityCloudSides.LogicalLeft, left,
                    cancellationToken);
            if (rightHierarchies.Count == 0)
                rightHierarchies = await RetainedHierarchyForCandidateAsync(connection, transaction,
                    installationId, candidatePairId, SyncAuthorityCloudSides.LogicalRight, right,
                    cancellationToken);
            foreach (var snapshot in leftHierarchies.Concat(rightHierarchies))
                if (!publicationAuthorities.Any(snapshot.BindsTo))
                    throw new InvalidDataException("A publication hierarchy is not bound to either candidate or a retained cloud authority.");
            // D3: an absent bound context is refused PER SIDE, and only where another retained
            // non-retired pair actually reaches that candidate root's provider, account and namespace.
            // A genuinely unrelated side keeps publishing with one-sided or absent evidence.
            await RequireRelatedSideBoundContextAsync(connection, transaction, candidatePairId, replacedPairId,
                preparedAuthorities[0], "left", leftHierarchies, cancellationToken);
            await RequireRelatedSideBoundContextAsync(connection, transaction, candidatePairId, replacedPairId,
                preparedAuthorities[1], "right", rightHierarchies, cancellationToken);
            if (replacedPairId is not null)
                await ExecuteAsync(connection, transaction,
                    "UPDATE root_binding SET state='out_of_scope',observed_utc=$utc WHERE pair_id=$id AND state='active';",
                    cancellationToken, ("$id", replacedPairId), ("$utc", Utc(DateTimeOffset.UtcNow)));
            await ActivateCandidateRootAsync(connection, transaction, left, leftHierarchies, cancellationToken,
                replacement, preparedAuthorities);
            await ActivateCandidateRootAsync(connection, transaction, right, rightHierarchies, cancellationToken,
                replacement, preparedAuthorities);
            if (replacedPairId is null)
                await RetainPairDefinitionCoreAsync(connection, transaction, replacement, replacementJson,
                    select: true, cancellationToken);
            else
                await ReplacePairDefinitionCoreAsync(connection, transaction, replacedPairId, replacement,
                    replacementJson, cancellationToken);
            // Pair-set identity validation belongs inside the publication transaction. A separately
            // retained definition can still conflict even after its live root left the active set.
            _ = await LoadPairSetCoreAsync(connection, transaction, cancellationToken);
        }, cancellationToken);
        return await LoadPairSetAsync(cancellationToken);
    }

    /// <summary>
    /// B1-C5: the replacement projection must name the exact prepared candidate roots on provider, namespace,
    /// folder and display path; a swapped or foreign root refuses inside the transaction.
    /// </summary>
    private static void RequirePreparedRootAgreement(SyncEndpoint endpoint, CloudPairCandidateRoot root,
        string side)
    {
        var identity = RequireReadCandidateIdentity(root.IdentityJson);
        var label = string.Equals(side, SyncAuthorityCloudSides.LogicalLeft, StringComparison.Ordinal)
            ? "left"
            : "right";
        var providerLiteral = SyncEndpoints.ProviderLiteral(endpoint.Kind);
        if (endpoint.IsLocal || string.IsNullOrWhiteSpace(providerLiteral) ||
            !string.Equals(providerLiteral, identity.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(endpoint.NamespaceId, identity.NamespaceId, StringComparison.Ordinal) ||
            !string.Equals(endpoint.FolderId, identity.FolderId, StringComparison.Ordinal) ||
            !string.Equals(SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(endpoint.FolderPath),
                root.DisplayPath, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The published " + label +
                " endpoint does not agree with the exact prepared candidate root; publication is refused.");
    }

    /// <summary>
    /// B1-C3: activates ONE prepared blocked candidate root through the unchanged guarded validator and the one
    /// guarded insertion path, with the blocked preimage admitted only for this guarded activation.
    /// </summary>
    private static async Task ActivateCandidateRootAsync(SqliteConnection connection,
        SqliteTransaction transaction, CloudPairCandidateRoot root,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> hierarchies, CancellationToken cancellationToken,
        SyncPair? partitionCandidate = null,
        IReadOnlyList<SyncAuthorityCloudRootIdentity>? preparedAuthorities = null)
    {
        var candidate = CandidateAuthority(root);
        var active = await LoadActiveCloudAuthoritiesAsync(connection, transaction, root.RootBindingId, cancellationToken);
        var applicable = hierarchies.Where(snapshot => snapshot.BindsTo(candidate) || active.Any(snapshot.BindsTo) ||
            preparedAuthorities?.Any(snapshot.BindsTo) == true).ToArray();
        await RequireCloudRootIdentityAvailableAsync(connection, transaction, candidate, applicable,
            cancellationToken, root.PairId, partitionCandidate, preparedAuthorities);
        await UpsertRootBindingCoreAsync(connection, transaction, root.RootBindingId, root.PairId, root.Side,
            root.Generation, root.IdentityJson, root.DisplayPath, cancellationToken, "active",
            allowBlockedCandidatePreimage: true);
    }

    /// <summary>
    /// D2: the retained observation of one candidate root as the bound validation context, or empty. It is
    /// accepted only for the exact candidate root id and generation, inside the caller's transaction.
    /// </summary>
    private static async Task<IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>>
        RetainedHierarchyForCandidateAsync(SqliteConnection connection, SqliteTransaction transaction,
            string installationId, string pairId, string side, CloudPairCandidateRoot root,
            CancellationToken cancellationToken)
    {
        var retained = await ResolveRetainedOpaqueHierarchyCoreAsync(connection, transaction, installationId,
            pairId, side, cancellationToken, allowBlockedCandidateRoot: true);
        if (retained is null || !string.Equals(retained.RootBindingId, root.RootBindingId, StringComparison.Ordinal) ||
            retained.RootGeneration != root.Generation)
            return Array.Empty<SyncAuthorityCloudHierarchySnapshot>();
        return new[] { retained.Snapshot };
    }

    /// <summary>
    /// D3: the per-side required-alias-evidence refusal. A side is refused only when it carries no bound
    /// provider hierarchy context AND another retained non-retired pair really reaches that candidate root's
    /// provider, account and namespace, so an absent context is never passed off as an independent root. A
    /// definite Same/Nested relation, or one the evidence-free comparison cannot verify, is left to the
    /// unchanged typed guard so its exact precise reason is preserved; a genuinely unrelated side keeps
    /// publishing, exactly as a one-sided or absent-evidence publication always has.
    /// </summary>
    private static async Task RequireRelatedSideBoundContextAsync(SqliteConnection connection,
        SqliteTransaction transaction, string candidatePairId, string? replacedPairId,
        SyncAuthorityCloudRootIdentity candidate, string sideLabel,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> hierarchies, CancellationToken cancellationToken)
    {
        foreach (var snapshot in hierarchies)
            if (snapshot.BindsTo(candidate)) return;
        var related = await LoadRelatedRetainedCloudRootsAsync(connection, transaction, candidatePairId,
            replacedPairId, candidate, cancellationToken);
        if (related.Count == 0) return;
        foreach (var root in related)
            if (SyncAuthorityCloudRootComparison.Compare(candidate, null, root, null) !=
                SyncAuthorityCloudRootRelation.Distinct) return;
        throw new InvalidOperationException(
            "The " + sideLabel + " cloud root shares its provider, account and namespace with another retained " +
            "pair root but carries no bound provider hierarchy context; publication is refused rather than " +
            "treating the absent context as an independent root.");
    }

    /// <summary>
    /// D3: every ACTIVE cloud root of another retained non-retired pair whose provider, account and namespace
    /// exactly match the candidate root's own, read inside the caller's publication transaction. The pair
    /// selected for replacement is this pair's own predecessor, never a foreign peer. Recovery boundaries and
    /// blocked candidate preimages are excluded: D3 only closes the path-descriptive alias gap between live
    /// pairs.
    /// </summary>
    private static async Task<IReadOnlyList<SyncAuthorityCloudRootIdentity>> LoadRelatedRetainedCloudRootsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string candidatePairId, string? replacedPairId,
        SyncAuthorityCloudRootIdentity candidate, CancellationToken cancellationToken)
    {
        var result = new List<SyncAuthorityCloudRootIdentity>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.root_binding_id,r.side,r.display_path,r.identity_json,
                   p.provider,p.provider_account_id,p.account_id,p.root_namespace_id,p.home_namespace_id
            FROM root_binding r
            JOIN sync_pair s ON s.pair_id=r.pair_id AND s.state<>'retired'
            LEFT JOIN provider_account p
              ON p.provider_account_id=json_extract(r.identity_json,'$.providerAccountId')
             AND p.status='active'
            WHERE r.state='active' AND r.pair_id<>$pair
              AND ($replaced IS NULL OR r.pair_id<>$replaced)
              AND json_extract(r.identity_json,'$.provider')=$provider COLLATE NOCASE
              AND json_extract(r.identity_json,'$.providerAccountId')=$account COLLATE BINARY
              AND json_extract(r.identity_json,'$.namespaceId')=$namespace COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$pair", candidatePairId);
        command.Parameters.AddWithValue("$replaced", (object?)replacedPairId ?? DBNull.Value);
        command.Parameters.AddWithValue("$provider", candidate.Provider);
        command.Parameters.AddWithValue("$account", (object?)candidate.ProviderAccountId ?? DBNull.Value);
        command.Parameters.AddWithValue("$namespace", (object?)candidate.NamespaceId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var identity = BuildRetainedCloudRootIdentity(reader.GetString(0),
                SyncAuthorityCloudRootIdentity.PairRootAuthority, reader.GetString(1), reader.GetString(2),
                reader.GetString(3), ReadCloudProviderAccountRow(reader, 4));
            if (identity is not null) result.Add(identity);
        }
        return result;
    }

    private static SyncAuthorityCloudRootIdentity CandidateAuthority(CloudPairCandidateRoot root)
    {
        var identity = RequireReadCandidateIdentity(root.IdentityJson);
        if (!SyncEndpointProviders.TryGet(identity.Provider, out var provider) || provider!.ProviderId is null)
            throw new InvalidDataException("The candidate root does not name a registered cloud provider.");
        return new SyncAuthorityCloudRootIdentity(provider.ProviderId, identity.ProviderAccountId,
            identity.NamespaceId, identity.FolderId, root.DisplayPath, root.RootBindingId,
            SyncAuthorityCloudRootIdentity.PairRootAuthority, root.Side);
    }
    /// <summary>
    /// Start a new authoring candidate only after proving that the deterministic candidate
    /// belongs to this installation and never issued a control-directory creation. Keep its
    /// blocked roots, operation and receipts as evidence; their immutable IDs must not be replayed.
    /// </summary>
    internal async Task<SyncPair> RetryFailedUnpublishedAccountPairAsync(SyncPair pair,
        string installationId, string providerAccountId, string rootNamespaceId,
        CancellationToken cancellationToken = default)
    {
        var retry = false;
        await WriteAsync(async (connection, transaction) =>
        {
            var eligible = await ScalarLongAsync(connection, transaction,
                $"""
                SELECT count(*) FROM sync_pair p
                WHERE p.pair_id=$pair AND p.configuration_revision=1
                  AND {NonCreatingUnpublishedControlFailurePredicate}
                  AND (SELECT count(*) FROM root_binding r WHERE r.pair_id=p.pair_id)=2
                  AND EXISTS(SELECT 1 FROM root_binding r JOIN volume v
                    ON v.volume_id=json_extract(r.identity_json,'$.volumeId')
                    WHERE r.pair_id=p.pair_id AND r.side='local' AND r.generation=1 AND r.state='blocked'
                      AND r.display_path=$local AND v.installation_id=$installation AND v.status='online'
                      AND lower(v.volume_serial_hex)=$serial
                      AND lower(json_extract(r.identity_json,'$.volumeSerial'))=$serial
                      AND lower(json_extract(r.identity_json,'$.fileId'))=$file)
                  AND EXISTS(SELECT 1 FROM root_binding r JOIN provider_account a
                    ON a.provider_account_id=json_extract(r.identity_json,'$.providerAccountId')
                    WHERE r.pair_id=p.pair_id AND r.side='dropbox' AND r.generation=1 AND r.state='blocked'
                      AND r.display_path=$remote AND a.installation_id=$installation AND a.status='active'
                      AND a.provider_account_id=$account AND a.root_namespace_id=$namespace
                      AND json_extract(r.identity_json,'$.rootNamespaceId')=$namespace
                      AND json_extract(r.identity_json,'$.folderId')=$folder)
                  AND NOT EXISTS(SELECT 1 FROM pair_control_namespace c WHERE c.pair_id=p.pair_id)
                  AND NOT EXISTS(SELECT 1 FROM namespace_allocation n JOIN operation o
                    ON o.operation_id=n.operation_id WHERE o.pair_id=p.pair_id
                      AND (n.side<>'local' OR n.owner_nonce_hash<>$owner))
                ;
                """, cancellationToken, ("$pair", pair.Id), ("$installation", installationId),
                ("$local", NormalizeAuthorityPath("local", pair.LocalRoot)),
                ("$serial", pair.LocalVolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture)),
                ("$file", pair.LocalRootFileId.ToString("x16", CultureInfo.InvariantCulture)),
                ("$remote", NormalizeAuthorityPath("dropbox", pair.DropboxRoot)),
                ("$account", providerAccountId), ("$namespace", rootNamespaceId),
                ("$folder", pair.DropboxFolderId),
                ("$owner", DropboxContentHash.Compute(SyncControlNamespace.MarkerBytes(installationId, pair.Id))));
            retry = eligible == 1;
        }, cancellationToken);
        // A new namespace avoids both the immutable blocked operation and its unique allocation
        // name. Normal registration repeats all overlap guards and the allocator still refuses
        // foreign sibling markers. No failed authority or filesystem object is adopted or erased.
        return retry ? pair with { Id = Guid.NewGuid().ToString("N")[..20] } : pair;
    }

    private const string NonCreatingUnpublishedControlFailurePredicate = """
        p.pair_json IS NULL AND p.selected=0 AND p.state<>'retired'
        AND EXISTS(SELECT 1 FROM operation o WHERE o.pair_id=p.pair_id)
        AND NOT EXISTS(
          SELECT 1 FROM operation o WHERE o.pair_id=p.pair_id AND (
            o.kind<>'allocate_control' OR o.status<>'blocked'
            OR NOT EXISTS(SELECT 1 FROM namespace_allocation n
              WHERE n.operation_id=o.operation_id AND n.status='blocked' AND n.bound_identity IS NULL)
            OR EXISTS(SELECT 1 FROM namespace_allocation n
              WHERE n.operation_id=o.operation_id AND (n.status<>'blocked' OR n.bound_identity IS NOT NULL))
            OR NOT EXISTS(SELECT 1 FROM operation_attempt a JOIN effect_receipt e ON e.attempt_id=a.attempt_id
              WHERE a.operation_id=o.operation_id AND a.status='blocked' AND e.certainty='known_conflict')
            OR EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id
              AND (a.status<>'blocked' OR a.effect_kind<>'local-control-directory-precondition'))
            OR EXISTS(SELECT 1 FROM operation_attempt a JOIN effect_receipt e ON e.attempt_id=a.attempt_id
              WHERE a.operation_id=o.operation_id AND e.certainty<>'known_conflict')
          ))
        AND NOT EXISTS(SELECT 1 FROM lease l WHERE l.status='active' AND (
          l.scope_id=p.pair_id
          OR l.scope_id IN (SELECT operation_id FROM operation WHERE pair_id=p.pair_id)
          OR l.scope_id IN (SELECT root_binding_id FROM root_binding WHERE pair_id=p.pair_id)))
        """;

    /// <summary>
    /// Releases only data-root reservations from a failed, never-published pair whose
    /// control allocation is positively retained as a non-creating collision. This
    /// does not adopt, delete, retry or rewrite the private namespace or its operation.
    /// Published pairs, any issued/unknown/effectful work and live leases stay held.
    /// </summary>
    internal async Task<int> ReleaseFailedUnpublishedPairRootsAsync(
        CancellationToken cancellationToken = default)
    {
        var released = 0;
        await WriteAsync(async (connection, transaction) =>
        {
            var candidates = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    SELECT p.pair_id FROM sync_pair p
                    WHERE {NonCreatingUnpublishedControlFailurePredicate}
                      AND EXISTS(SELECT 1 FROM root_binding r WHERE r.pair_id=p.pair_id
                        AND r.side IN ('local','dropbox') AND r.state='active')
                    ORDER BY p.pair_id LIMIT 128;
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) candidates.Add(reader.GetString(0));
            }
            foreach (var pairId in candidates)
            {
                var count = await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM root_binding WHERE pair_id=$pair AND side IN ('local','dropbox') AND state='active';",
                    cancellationToken, ("$pair", pairId));
                await ExecuteAsync(connection, transaction,
                    "UPDATE root_binding SET state='blocked',observed_utc=$utc WHERE pair_id=$pair AND side IN ('local','dropbox') AND state='active';",
                    cancellationToken, ("$pair", pairId), ("$utc", Utc(DateTimeOffset.UtcNow)));
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO history_event(retention_class,rowid,history_event_id,operation_id,event_kind,detail_json,occurred_utc) VALUES('safety',(SELECT next_history_sequence FROM installation),$id,NULL,'pair_authoring_reservations_released',$detail,$utc);",
                    cancellationToken, ("$id", "history-" + Guid.NewGuid().ToString("N")),
                    ("$detail", JsonSerializer.Serialize(new
                    {
                        pairId, rootCount = count,
                        reason = "Unpublished pair refused by a non-creating private-control collision; only data-root reservations released.",
                        privateNamespaceUnchanged = true
                    })), ("$utc", Utc(DateTimeOffset.UtcNow)));
                released = checked(released + (int)count);
            }
        }, cancellationToken);
        return released;
    }

    public async Task<SyncPairSet> RetirePairDefinitionAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await WriteAsync(async (connection, transaction) =>
        {
            var active = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND state<>'retired' AND pair_json IS NOT NULL;",
                cancellationToken, ("$id", pairId));
            if (active != 1)
                throw new InvalidDataException("The sync pair selected for deletion is not retained.");
            var nonterminal = await ScalarLongAsync(connection, transaction,
                $"""
                SELECT count(*) FROM operation o
                WHERE o.pair_id=$id AND o.status NOT IN ('completed','cancelled','superseded')
                  AND NOT(o.status='blocked' AND {KnownNoEffectRefusalPredicate});
                """,
                cancellationToken, ("$id", pairId));
            if (nonterminal != 0)
                throw new InvalidOperationException(
                    "The pair cannot be deleted while queued, interrupted, blocked, or cleanup work remains.");
            await ExecuteAsync(connection, transaction,
                "UPDATE sync_pair SET state='retired',selected=0 WHERE pair_id=$id AND state<>'retired';",
                cancellationToken, ("$id", pairId));
            await ExecuteAsync(connection, transaction,
                "UPDATE root_binding SET state='out_of_scope',observed_utc=$utc WHERE pair_id=$id AND state='active';",
                cancellationToken, ("$id", pairId), ("$utc", Utc(DateTimeOffset.UtcNow)));
            var hasSelection = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE selected=1 AND state<>'retired';", cancellationToken);
            if (hasSelection == 0)
                await ExecuteAsync(connection, transaction,
                    "UPDATE sync_pair SET selected=1 WHERE pair_id=(SELECT pair_id FROM sync_pair WHERE state<>'retired' AND pair_json IS NOT NULL ORDER BY pair_id LIMIT 1);",
                    cancellationToken);
        }, cancellationToken);
        return await LoadPairSetAsync(cancellationToken);
    }

    public async Task RecordAcceptedLegacyPairImportAsync(SyncPair sourcePair, SyncPair importedPair,
        SyncPairRuntimeState? runtimeState = null, CancellationToken cancellationToken = default)
    {
        sourcePair = SyncPairStore.NormalizePair(sourcePair);
        importedPair = SyncPairStore.NormalizePair(importedPair);
        runtimeState = runtimeState is null ? null : SyncRuntimeStateStore.NormalizePair(runtimeState);
        var sourceJson = JsonSerializer.Serialize(new { Pair = sourcePair, Runtime = runtimeState });
        var importedJson = JsonSerializer.Serialize(new
        {
            Pair = importedPair,
            LegacyRuntime = runtimeState,
            RequiresRecursiveBootstrap = true,
            PendingLegacyRecovery = runtimeState?.PendingTransfer is not null
        });
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceJson))).ToLowerInvariant();
        var migrationId = "legacy-state-" + sourceHash[..20];
        // SS-D11: the exact accepted receipt of this exact source identity, hash and imported payload is read
        // FIRST on one existing connection. Only one positively exact accepted row short-circuits the write;
        // an absent or non-matching row keeps the unchanged transactional path and its collision rejection,
        // and the receipt is still created by the same INSERT OR IGNORE plus exact inside-transaction recheck.
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var accepted = await ScalarLongAsync(connection, null,
                """
                SELECT count(*) FROM migration_import
                WHERE migration_import_id=$id AND source_kind='legacy_0_1_1_pairs'
                  AND source_identity=$source AND source_sha256=$sha AND imported_json=$json AND status='accepted';
                """, cancellationToken, ("$id", migrationId), ("$source", sourcePair.Id),
                ("$sha", sourceHash), ("$json", importedJson));
            if (accepted == 1) return;
        }
        await WriteAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction,
                """
                INSERT OR IGNORE INTO migration_import(migration_import_id,source_kind,source_identity,source_sha256,
                    imported_json,imported_utc,status)
                VALUES($id,'legacy_0_1_1_pairs',$source,$sha,$json,$utc,'accepted');
                """, cancellationToken, ("$id", migrationId), ("$source", sourcePair.Id),
                ("$sha", sourceHash), ("$json", importedJson), ("$utc", Utc(DateTimeOffset.UtcNow)));
            var exact = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM migration_import
                WHERE migration_import_id=$id AND source_kind='legacy_0_1_1_pairs'
                  AND source_identity=$source AND source_sha256=$sha AND imported_json=$json AND status='accepted';
                """, cancellationToken, ("$id", migrationId), ("$source", sourcePair.Id),
                ("$sha", sourceHash), ("$json", importedJson));
            if (exact != 1)
                throw new InvalidOperationException("The retained legacy-pair migration receipt does not match.");
        }, cancellationToken);
    }

    /// <summary>
    /// Imports a legacy history into the retained decision authority. SS-D11 keeps the exact legacy contract -
    /// occurrence order, the normalized path, the hashed source JSON, the SHA-derived decision identity, the
    /// decision kind, the detail JSON and the retained collision refusal - and replaces the per-row write
    /// transaction with prepared, bounded, atomic batches. Every entry of the first and of any changed input is
    /// still normalized and validated; an unchanged repeated input completes from exact retained rows alone,
    /// with no writer transaction at all. The preparation, the batch bounds, the pre-transaction exact identity
    /// read and the per-batch write are in SyncAuthorityDatabase.RecoveryQueries.cs.
    /// </summary>
    public async Task ImportLegacyHistoryAsync(SyncHistory history,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        var prepared = PrepareLegacyHistoryImport(history);
        foreach (var batch in BuildLegacyHistoryImportBatches(prepared))
            await ImportLegacyHistoryBatchAsync(batch, cancellationToken);
    }

    public async Task CreateCommandRequestAsync(string commandRequestId, string commandKind, string? pairId,
        string? targetId, long? expectedGeneration, string payloadJson,
        CancellationToken cancellationToken = default)
        => await WriteAsync(async (connection, transaction) =>
        {
            await CreateCommandRequestCoreAsync(connection, transaction, commandRequestId, commandKind, pairId,
                targetId, expectedGeneration, payloadJson, SyncAuthorityCommandPurpose.NormalEffect,
                cancellationToken);
        }, cancellationToken);

    // The one command-creation core. The exact admission the new row carries is allocated HERE, inside the
    // caller's creating transaction, from the retained installation generation and its monotonic
    // next_command_sequence; no caller supplies and no code infers a sequence. This legacy entry keeps the
    // established public command-request contract and therefore records the ordinary normal_effect purpose;
    // the admitted production paths use CreateAcceptedCommandAdmissionAsync with their exact purpose and
    // reserved identity.
    private static async Task<SyncAuthorityCommandAdmission> CreateCommandRequestCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string commandRequestId, string commandKind, string? pairId, string? targetId,
        long? expectedGeneration, string payloadJson, SyncAuthorityCommandPurpose purpose,
        CancellationToken cancellationToken, SyncAuthorityCommandReservation? reservation = null)
    {
        RequireIdentifier(commandRequestId, nameof(commandRequestId));
        if (pairId is not null) RequireIdentifier(pairId, nameof(pairId));
        if (expectedGeneration is <= 0) throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        if (commandKind is not ("run_pair" or "run_all" or "pause" or "resume" or "stop" or
            "resolve_conflict" or "cancel_unstarted" or "reconcile" or "backup" or "integrity_check"))
            throw new ArgumentOutOfRangeException(nameof(commandKind));
        RequireJson(payloadJson, nameof(payloadJson));
            using var payload = JsonDocument.Parse(payloadJson);
            if (targetId is not null && payload.RootElement.ValueKind == JsonValueKind.Object &&
                payload.RootElement.TryGetProperty("action", out var action) &&
                action.ValueKind == JsonValueKind.String && action.GetString() == "restore_recovery")
                await RequireRecoverySourceAvailableAsync(connection, transaction, targetId, cancellationToken);
            return await CreateAdmittedCommandRequestCoreAsync(connection, transaction, commandRequestId,
                reservation, commandKind, pairId, targetId, expectedGeneration, payloadJson, purpose,
                cancellationToken);
    }

    public async Task TransitionCommandRequestAsync(string commandRequestId, string status,
        string? resultJson = null, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(commandRequestId, nameof(commandRequestId));
        if (resultJson is not null) RequireJson(resultJson, nameof(resultJson));
        if (status is not ("accepted" or "executing" or "completed" or "rejected" or "stale" or
            "blocked" or "cancelled"))
            throw new ArgumentOutOfRangeException(nameof(status));
        await WriteAsync((connection, transaction) => TransitionCommandRequestCoreAsync(connection, transaction,
            commandRequestId, status, resultJson, cancellationToken), cancellationToken);
    }

    private static async Task TransitionCommandRequestCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string commandRequestId, string status, string? resultJson,
        CancellationToken cancellationToken)
    {
            var current = await ScalarStringAsync(connection, transaction,
                "SELECT status FROM command_request WHERE command_request_id=$id;", cancellationToken,
                ("$id", commandRequestId))
                          ?? throw new InvalidDataException("The command request is not retained.");
            var allowed = (current, status) switch
            {
                ("requested", "accepted" or "rejected" or "blocked" or "cancelled") => true,
                ("accepted", "executing" or "blocked" or "cancelled" or "stale") => true,
                ("executing", "completed" or "blocked" or "stale") => true,
                _ => false
            };
            if (!allowed)
                throw new InvalidOperationException($"Command transition {current} -> {status} is not allowed.");
            var terminal = status is "completed" or "rejected" or "stale" or "blocked" or "cancelled";
            await ExecuteAsync(connection, transaction,
                """
                UPDATE command_request
                SET status=$status,result_json=CASE
                    WHEN $status='blocked' AND json_type(result_json,'$.manualLocalRestore')='object'
                    THEN json_set(CASE WHEN json_type($result)='object' THEN $result
                                       ELSE json_object('failureResult',json($result)) END,'$.manualLocalRestore',
                                  json_extract(result_json,'$.manualLocalRestore'))
                    ELSE $result END,
                    terminal_utc=CASE WHEN $terminal=1 THEN $utc ELSE NULL END
                WHERE command_request_id=$id AND status=$current;
                """, cancellationToken, ("$status", status), ("$result", resultJson),
                ("$terminal", terminal ? 1 : 0), ("$utc", Utc(DateTimeOffset.UtcNow)),
                ("$id", commandRequestId), ("$current", current));
            // Retain safe command stages alongside sync stages. The payload is built solely
            // from typed command identity/status, never raw request/result JSON or exceptions.
            if (status is "executing" or "completed" or "blocked" or "cancelled" or "stale")
                await ExecuteAsync(connection, transaction,
                    """
                    INSERT INTO history_event(retention_class,rowid,history_event_id,operation_id,event_kind,detail_json,occurred_utc)
                    SELECT 'safety',(SELECT next_history_sequence FROM installation),$event,NULL,'presentation',json_object(
                        'EventId',$event,'OccurredUtc',$utc,
                        'Categories',json_array(CASE
                            WHEN command_kind IN ('run_pair','run_all','reconcile','resolve_conflict') THEN 'Sync log'
                            WHEN command_kind LIKE '%connection%' OR command_kind IN ('bind_pair','retire_pair') THEN 'Connections'
                            WHEN command_kind LIKE '%settings%' OR command_kind LIKE '%automation%' OR command_kind LIKE '%transfer%' THEN 'App settings'
                            ELSE 'Diagnostics' END),
                        'Severity',CASE WHEN $status='completed' THEN 'Success' WHEN $status='executing' THEN 'Information' ELSE 'Warning' END,
                        'Entity',CASE WHEN command_kind IN ('run_pair','run_all','reconcile','resolve_conflict') THEN 'Run'
                            WHEN pair_id IS NOT NULL THEN 'Folder pair' ELSE 'Application' END,
                        'Action',replace(command_kind,'_',' ') || ' ' || $status,
                        'Message','Command ' || $status || '.',
                        'PairId',pair_id,'RunId',CASE WHEN command_kind IN ('run_pair','run_all','reconcile','resolve_conflict') THEN command_request_id END,
                        'RelativePath',''),$utc
                    FROM command_request WHERE command_request_id=$id;
                    """, cancellationToken, ("$event", Guid.NewGuid().ToString("N")),
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$status", status), ("$id", commandRequestId));
    }

    internal async Task CheckpointExecutingCommandResultAsync(string commandRequestId, string resultJson,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(commandRequestId, nameof(commandRequestId));
        RequireJson(resultJson, nameof(resultJson));
        await WriteAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE command_request SET result_json=$result WHERE command_request_id=$id AND status='executing';",
                cancellationToken, ("$id", commandRequestId), ("$result", resultJson));
            if (await ScalarLongAsync(connection, transaction, "SELECT changes();", cancellationToken) != 1)
                throw new InvalidOperationException("Only the exact executing command can retain a progress checkpoint.");
        }, cancellationToken);
    }

    // Access repair is a checkpoint, never a completed restore or a blocked-to-completed transition.
    internal async Task RecordManualLocalRestoreAccessAsync(string commandRequestId, string verificationJson,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(commandRequestId, nameof(commandRequestId));
        RequireJson(verificationJson, nameof(verificationJson));
        await WriteAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction,
                """
                UPDATE command_request
                SET result_json=json_set(result_json,'$.manualLocalRestoreAccess',json($verification))
                WHERE command_request_id=$id AND status='blocked'
                  AND command_kind='reconcile'
                  AND json_extract(payload_json,'$.action')='restore_recovery'
                  AND json_extract(payload_json,'$.source')='LocalArchive'
                  AND json_type(result_json,'$.manualLocalRestore')='object';
                """, cancellationToken, ("$id", commandRequestId), ("$verification", verificationJson));
            if (await ScalarLongAsync(connection, transaction, "SELECT changes();", cancellationToken) != 1)
                throw new InvalidOperationException("Only the exact unresolved manual restore can retain access verification.");
        }, cancellationToken);
    }

    public async Task RecoverInterruptedCommandRequestsAsync(CancellationToken cancellationToken = default)
    {
        var result = JsonSerializer.Serialize(new
        {
            reason = "process_interrupted",
            recoveryRequired = true
        });
        await WriteAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction,
                """
                UPDATE command_request
                SET status='blocked',terminal_utc=$utc,
                    result_json=CASE WHEN result_json IS NULL THEN $result
                        ELSE json_set(result_json,'$.reason','process_interrupted','$.recoveryRequired',json('true')) END
                WHERE status IN ('requested','accepted','executing');
                """, cancellationToken, ("$utc", Utc(DateTimeOffset.UtcNow)), ("$result", result));
        }, cancellationToken);
    }

    public async Task<DateTimeOffset?> LoadLastSuccessfulSyncUtcAsync(
        CancellationToken cancellationToken = default, IReadOnlyCollection<string>? allowedPairIds = null)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var value = await ScalarStringAsync(connection, null, """
            SELECT max(terminal_utc)
            FROM command_request
            WHERE command_kind IN ('run_pair','run_all') AND status='completed'
              AND coalesce(json_extract(result_json,'$.failedPairs'),0)=0
              AND coalesce(json_extract(result_json,'$.Stopped'),json_extract(result_json,'$.stopped'),0)=0
              AND (coalesce(json_extract(result_json,'$.completedPairs'),0)>0
                   OR json_extract(result_json,'$.firstSyncComplete')=1)
              AND coalesce(json_extract(result_json,'$.Blocked'),json_extract(result_json,'$.blocked'),0)=0
              AND coalesce(json_extract(result_json,'$.Conflicts'),json_extract(result_json,'$.conflicts'),0)=0
              AND coalesce(json_extract(result_json,'$.reviewedWorkComplete'),0)=0
              AND ($pairs IS NULL OR EXISTS (
                SELECT 1 FROM json_each(payload_json,'$.pairIds') run_pair
                JOIN json_each($pairs) allowed ON allowed.value=run_pair.value));
            """, cancellationToken, ("$pairs", allowedPairIds is null ? null : JsonSerializer.Serialize(allowedPairIds)));
        return value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<SyncAuthorityRunHistory>> LoadRunHistoryAsync(int limit = 50, int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200 || offset < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT command_request_id,command_kind,requested_utc,terminal_utc,status,payload_json,result_json
            FROM command_request WHERE command_kind IN ({RetainedRunCommandKindsSql})
            ORDER BY requested_utc DESC,command_request_id DESC LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", limit); command.Parameters.AddWithValue("$offset", offset);
        var result = new List<SyncAuthorityRunHistory>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return result;
    }

    // R474-2: the retained first-sync terminal rows, read directly from the existing command results and filtered
    // to first-sync outcomes, so unrelated run volume can never evict an unresolved problem from the projection.
    // The caller decides which row is the current problem of each pair; every earlier row stays retained.
    public async Task<IReadOnlyList<SyncAuthorityRunHistory>> LoadFirstSyncOutcomeRowsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH valid_outcomes AS (
                SELECT *, CASE WHEN json_valid(result_json) THEN result_json ELSE '{}' END AS safe_result
                FROM command_request WHERE command_kind='run_pair' AND terminal_utc IS NOT NULL
            ), ranked AS (
                SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY pair_id,
                        CASE WHEN json_type(safe_result,'$.firstSyncScanComplete') IN ('true','false')
                             THEN 'scan' ELSE 'apply' END
                    ORDER BY requested_utc DESC,command_request_id DESC
                ) AS pair_rank
                FROM valid_outcomes
                WHERE pair_id IS NOT NULL AND
                    (json_type(safe_result,'$.firstSyncProblem')='object'
                     OR json_type(safe_result,'$.firstSyncComplete') IN ('true','false')
                     OR json_type(safe_result,'$.firstSyncScanComplete') IN ('true','false'))
            )
            SELECT command_request_id,command_kind,requested_utc,terminal_utc,status,payload_json,result_json
            FROM ranked WHERE pair_rank=1
            ORDER BY requested_utc DESC,command_request_id DESC;
            """;
        var result = new List<SyncAuthorityRunHistory>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return result;
    }


    // Current command problems are derived from immutable retained outcomes, not
    // the paged Runs list. Unrelated command volume cannot evict a failure. Only
    // a later ordinary, successful run of the exact target can resolve it;
    // stopped/unknown/recovery-only results are not evidence of that success.
    public async Task<IReadOnlyList<(string PairId, SyncAuthorityRunHistory Run)>> LoadCommandProblemOutcomeRowsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH terminal_outcomes AS MATERIALIZED (
                SELECT *,
                    CASE WHEN json_valid(payload_json) THEN payload_json ELSE '{}' END AS safe_request,
                    CASE WHEN json_valid(result_json) THEN result_json ELSE '{}' END AS safe_result
                FROM command_request
                WHERE command_kind IN ('run_pair','run_all','reconcile') AND status IN ('blocked','completed')
                  AND terminal_utc IS NOT NULL
            ), pair_results AS MATERIALIZED (
                SELECT command_request_id,
                    CASE WHEN outcome.type='object' THEN outcome.value ELSE '{}' END AS safe_pair
                FROM terminal_outcomes
                JOIN json_each(CASE WHEN json_type(safe_result,'$.pairs')='array'
                    THEN json_extract(safe_result,'$.pairs') ELSE '[]' END) outcome
            ), failed_outcomes AS (
                SELECT * FROM terminal_outcomes terminal
                WHERE json_type(safe_result,'$.commandFailure')='object' OR EXISTS (
                    SELECT 1 FROM pair_results failure
                    WHERE failure.command_request_id=terminal.command_request_id
                      AND json_type(safe_pair,'$.failureMessage')='text'
                      AND length(json_extract(safe_pair,'$.failureMessage'))>0
                )
            ), valid_outcomes AS (
                SELECT * FROM failed_outcomes
                UNION ALL
                SELECT *,
                    CASE WHEN json_valid(payload_json) THEN payload_json ELSE '{}' END,
                    CASE WHEN json_valid(result_json) THEN result_json ELSE '{}' END
                FROM command_request later
                WHERE command_kind IN ('run_pair','run_all') AND status='completed' AND terminal_utc IS NOT NULL
                  AND EXISTS(SELECT 1 FROM failed_outcomes earlier WHERE earlier.requested_utc < later.requested_utc)
            ), relevant_targets AS (
                SELECT DISTINCT outcome.*, target.value AS target_pair_id
                FROM valid_outcomes outcome
                JOIN json_each(CASE WHEN json_type(safe_request,'$.pairIds')='array'
                    THEN json_extract(safe_request,'$.pairIds') ELSE '[]' END) target
                WHERE target.type='text' AND length(target.value)>0 AND (
                    json_type(safe_result,'$.commandFailure')='object' OR EXISTS (
                        SELECT 1 FROM pair_results failure
                        WHERE failure.command_request_id=outcome.command_request_id
                          AND json_extract(safe_pair,'$.pairId')=target.value
                          AND json_type(safe_pair,'$.failureMessage')='text'
                          AND length(json_extract(safe_pair,'$.failureMessage'))>0
                    ) OR
                    (status='completed' AND command_kind IN ('run_pair','run_all')
                     AND json_type(safe_request,'$.recoveryOnly')='false'
                     AND EXISTS (
                        SELECT 1 FROM pair_results successful
                        WHERE successful.command_request_id=outcome.command_request_id
                          AND json_extract(safe_pair,'$.pairId')=target.value
                          AND json_type(safe_pair,'$.stopped')='false'
                          AND json_type(safe_pair,'$.failureType')='null'
                          AND json_extract(safe_pair,'$.failureMessage')=''
                          AND json_type(safe_pair,'$.failed')='integer'
                          AND json_extract(safe_pair,'$.failed')=0
                          AND json_type(safe_pair,'$.blocked')='integer'
                          AND json_extract(safe_pair,'$.blocked')=0
                          AND json_type(safe_pair,'$.conflicts')='integer'
                          AND json_extract(safe_pair,'$.conflicts')=0
                          AND json_type(safe_pair,'$.needsVerification')='integer'
                          AND json_extract(safe_pair,'$.needsVerification')=0
                          AND json_type(safe_pair,'$.notAttempted')='integer'
                          AND json_extract(safe_pair,'$.notAttempted')=0
                     ))
                )
            ), ranked AS (
                SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY target_pair_id ORDER BY requested_utc DESC,command_request_id DESC
                ) AS pair_rank FROM relevant_targets
            )
            SELECT target_pair_id,command_request_id,command_kind,requested_utc,terminal_utc,status,payload_json,result_json
            FROM ranked WHERE pair_rank=1 ORDER BY requested_utc DESC,command_request_id DESC;
            """;
        var result = new List<(string PairId, SyncAuthorityRunHistory Run)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add((reader.GetString(0), new(reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7))));
        return result;
    }

    public async Task RegisterPairAsync(string pairId, long configurationRevision,
        CancellationToken cancellationToken = default)
    {
        await RegisterPairAsync(pairId, configurationRevision,
            new SyncAuthorityPairConfiguration("disabled", 300, 2, "manual_preserve_both", "content_only",
                RecursiveMode: false, AutomaticPruning: false, RecoveryRetentionDays: 30),
            cancellationToken);
    }

    public async Task RegisterPairAsync(string pairId, long configurationRevision,
        SyncAuthorityPairConfiguration configuration, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (configurationRevision < 1) throw new ArgumentOutOfRangeException(nameof(configurationRevision));
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.DeletionPolicy is not ("disabled" or "recoverable"))
            throw new ArgumentOutOfRangeException(nameof(configuration));
        if (configuration.DeletionGraceSeconds < 0 || configuration.RequiredAbsenceObservations < 1 ||
            configuration.ConflictPolicy != "manual_preserve_both" ||
            configuration.MetadataPolicy is not ("content_times_basic_attrs" or "content_only") ||
            configuration.RecoveryRetentionDays < 1)
            throw new ArgumentOutOfRangeException(nameof(configuration));
        await WriteAsync(async (connection, transaction) =>
            await RegisterPairConfigurationCoreAsync(connection, transaction, pairId, configurationRevision,
                configuration, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// B1-C4: the one transaction-scoped configuration insertion core, extracted verbatim from the existing
    /// public RegisterPairAsync so candidate preparation reuses exactly the sync_pair and pair_config_revision
    /// policy instead of inventing a row or dropping the configuration. The extracted body is carried inside one
    /// explicit scope block whose braces were verified to balance; the public validation, signature and every
    /// legacy behaviour are unchanged.
    /// </summary>
    private static async Task RegisterPairConfigurationCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string pairId, long configurationRevision,
        SyncAuthorityPairConfiguration configuration, CancellationToken cancellationToken)
    {
        {
            // Re-registering the exact live revision is an idempotent read, not a
            // configuration change. Keep it in the same writer transaction and
            // still validate every immutable policy field below. Only an actual
            // insertion/revision change/retirement revival needs the retained-
            // operation guard, which can otherwise scan the whole operation log.
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND configuration_revision=$revision AND state<>'retired';",
                cancellationToken, ("$id", pairId), ("$revision", configurationRevision));
            if (exact != 1)
            {
                var configurationRecovered = await ResolveRecoveredCollisionForPairAsync(connection, transaction,
                    pairId, cancellationToken);
                await ExecuteAsync(connection, transaction,
                $"""
                INSERT INTO sync_pair(pair_id, configuration_revision, state, created_utc)
                VALUES($id,$revision,'discovery_required',$utc)
                ON CONFLICT(pair_id) DO UPDATE SET
                    configuration_revision=excluded.configuration_revision,
                    state=CASE WHEN sync_pair.state='retired' THEN 'discovery_required' ELSE sync_pair.state END
                WHERE NOT EXISTS(
                    SELECT 1 FROM operation o WHERE o.pair_id=excluded.pair_id
                      AND o.status NOT IN ('completed','cancelled','superseded')
                      AND NOT(o.status='blocked' AND {KnownNoEffectRefusalPredicate})
                      AND ($recovered IS NULL OR o.operation_id<>$recovered));
                """, cancellationToken, ("$id", pairId), ("$revision", configurationRevision),
                ("$utc", Utc(DateTimeOffset.UtcNow)),
                ("$recovered", (object?)configurationRecovered ?? DBNull.Value));
                exact = await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND configuration_revision=$revision AND state<>'retired';",
                    cancellationToken, ("$id", pairId), ("$revision", configurationRevision));
            }
            if (exact != 1)
                throw new InvalidOperationException("Pair configuration changed while nonterminal authority was retained.");
            await ExecuteAsync(connection, transaction,
                """
                INSERT OR IGNORE INTO pair_config_revision(pair_id,revision,created_utc,created_by,deletion_policy,
                    deletion_grace_seconds,required_absence_observations,conflict_policy,metadata_policy,
                    recursive_mode,automatic_pruning,recovery_retention_days)
                VALUES($pair,$revision,$utc,'local_product',$deletion,$grace,$observations,$conflict,$metadata,
                    $recursive,$pruning,$retention);
                """, cancellationToken, ("$pair", pairId), ("$revision", configurationRevision),
                ("$utc", Utc(DateTimeOffset.UtcNow)), ("$deletion", configuration.DeletionPolicy),
                ("$grace", configuration.DeletionGraceSeconds),
                ("$observations", configuration.RequiredAbsenceObservations),
                ("$conflict", configuration.ConflictPolicy), ("$metadata", configuration.MetadataPolicy),
                ("$recursive", configuration.RecursiveMode ? 1 : 0),
                ("$pruning", configuration.AutomaticPruning ? 1 : 0),
                ("$retention", configuration.RecoveryRetentionDays));
            var exactConfiguration = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM pair_config_revision
                WHERE pair_id=$pair AND revision=$revision AND deletion_policy=$deletion
                  AND deletion_grace_seconds=$grace AND required_absence_observations=$observations
                  AND conflict_policy=$conflict AND metadata_policy=$metadata
                  AND recursive_mode=$recursive AND automatic_pruning=$pruning
                  AND recovery_retention_days=$retention;
                """, cancellationToken, ("$pair", pairId), ("$revision", configurationRevision),
                ("$deletion", configuration.DeletionPolicy), ("$grace", configuration.DeletionGraceSeconds),
                ("$observations", configuration.RequiredAbsenceObservations),
                ("$conflict", configuration.ConflictPolicy), ("$metadata", configuration.MetadataPolicy),
                ("$recursive", configuration.RecursiveMode ? 1 : 0),
                ("$pruning", configuration.AutomaticPruning ? 1 : 0),
                ("$retention", configuration.RecoveryRetentionDays));
            if (exactConfiguration != 1)
                throw new InvalidOperationException("The pair policy changed without a new configuration revision.");
        }
    }

    public async Task EnsurePairConfigurationMutableAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        // RC639-4.INTEGRATION-1: the authoring-mutability projection discounts exactly the SAME one record
        // the publication guards discount - the exact authenticated retained-control recovery non-creating
        // collision bound to this pair's current candidate root. Every other queued, interrupted, blocked,
        // cleanup or planning-referenced record keeps refusing unchanged, and no status is rewritten.
        var mutableInstallation = await ScalarStringAsync(connection, null,
            "SELECT installation_id FROM installation ORDER BY installation_id LIMIT 1;", cancellationToken);
        var mutableRecovered = mutableInstallation is null ? null
            : await ResolveRecoveredNonCreatingCollisionOperationIdAsync(connection, null, pairId,
                mutableInstallation, cancellationToken);
        var retained = await ScalarLongAsync(connection, null,
            $"""
            SELECT count(*) FROM operation o
            WHERE o.status NOT IN ('completed','cancelled','superseded')
              AND (o.pair_id=$pair OR EXISTS(SELECT 1 FROM json_each(o.preconditions_json,'$.planningAuthorities') retained
                    WHERE json_extract(retained.value,'$.PairId')=$pair))
              AND NOT(o.status='blocked' AND {KnownNoEffectRefusalPredicate})
              AND ($recovered IS NULL OR o.operation_id<>$recovered);
            """, cancellationToken, ("$pair", pairId),
            ("$recovered", (object?)mutableRecovered ?? DBNull.Value));
        if (retained != 0)
            throw new InvalidOperationException(
                "Finish or recover this pair's pending work before saving changes. Its current settings stay in effect until then; your draft is kept.");
    }

    public async Task SetPairStateAsync(string pairId, string state,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (state is not ("discovery_required" or "active" or "paused" or "blocked"))
            throw new ArgumentOutOfRangeException(nameof(state));
        await WriteAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE sync_pair SET state=$state WHERE pair_id=$id AND state<>'retired';",
                cancellationToken, ("$state", state), ("$id", pairId));
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM sync_pair WHERE pair_id=$id AND state=$state;", cancellationToken,
                ("$id", pairId), ("$state", state));
            if (exact != 1) throw new InvalidOperationException("The retained pair state was not updated.");
        }, cancellationToken);
    }

    public async Task AuthorizeNamespaceAllocationAsync(SyncAuthorityNamespaceAllocation allocation,
        SyncAuthorityLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        foreach (var value in new[] { allocation.NamespaceAllocationId, allocation.OperationId })
            RequireIdentifier(value, nameof(allocation));
        if (allocation.OperationId != lease.OperationId)
            throw new InvalidOperationException("The namespace allocation lease belongs to another operation.");
        if (allocation.Side is not ("local" or "dropbox"))
            throw new ArgumentOutOfRangeException(nameof(allocation));
        if (allocation.Status != SyncAuthorityAllocationStatus.Authorized ||
            allocation.BoundIdentity is not null)
            throw new InvalidOperationException("A new namespace allocation must begin authorized and unbound.");
        ArgumentException.ThrowIfNullOrWhiteSpace(allocation.ParentIdentity);
        var deterministicName = PilotPath.NormalizeTopLevelName(allocation.DeterministicName);
        RequireHash(allocation.OwnerNonceHash, nameof(allocation));
        await WriteAsync(async (connection, transaction) =>
        {
            await RequireFenceAsync(connection, transaction, allocation.OperationId, lease, cancellationToken);
            await ExecuteAsync(connection, transaction,
                """
                INSERT OR IGNORE INTO namespace_allocation(namespace_allocation_id,operation_id,side,
                    parent_identity,deterministic_name,owner_nonce_hash,status,transition_lease_id,
                    transition_fencing_token,created_utc)
                VALUES($id,$operation,$side,$parent,$name,$nonce,'authorized',$lease,$token,$utc);
                """, cancellationToken, ("$id", allocation.NamespaceAllocationId),
                ("$operation", allocation.OperationId), ("$side", allocation.Side),
                ("$parent", allocation.ParentIdentity), ("$name", deterministicName),
                ("$nonce", allocation.OwnerNonceHash), ("$lease", lease.LeaseId),
                ("$token", lease.FencingToken), ("$utc", Utc(DateTimeOffset.UtcNow)));
            var exact = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM namespace_allocation
                WHERE namespace_allocation_id=$id AND operation_id=$operation AND side=$side
                  AND parent_identity=$parent AND deterministic_name=$name AND owner_nonce_hash=$nonce
                  AND status='authorized' AND bound_identity IS NULL;
                """, cancellationToken, ("$id", allocation.NamespaceAllocationId),
                ("$operation", allocation.OperationId), ("$side", allocation.Side),
                ("$parent", allocation.ParentIdentity), ("$name", deterministicName),
                ("$nonce", allocation.OwnerNonceHash));
            if (exact != 1)
                throw new InvalidOperationException(
                    "The deterministic namespace allocation collides with different retained authority.");
        }, cancellationToken);
    }

    public async Task TransitionNamespaceAllocationAsync(string namespaceAllocationId, SyncAuthorityLease lease,
        SyncAuthorityAllocationStatus status, string? boundIdentity = null,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(namespaceAllocationId, nameof(namespaceAllocationId));
        if (status is SyncAuthorityAllocationStatus.Authorized)
            throw new ArgumentOutOfRangeException(nameof(status));
        await WriteAsync((connection, transaction) => TransitionNamespaceAllocationCoreAsync(connection,
            transaction, namespaceAllocationId, lease, status, boundIdentity, cancellationToken), cancellationToken);
    }

    /// <summary>root718: the transaction-scoped namespace-allocation transition, extracted so the atomic published
    /// same-root edit can drive the newly created allocation to its verified binding inside its own transaction.
    /// The fence requirement is unchanged: the same RequireFenceAsync runs against the caller's lease.</summary>
    private static async Task TransitionNamespaceAllocationCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string namespaceAllocationId, SyncAuthorityLease lease,
        SyncAuthorityAllocationStatus status, string? boundIdentity, CancellationToken cancellationToken)
    {
            var operationId = await ScalarStringAsync(connection, transaction,
                "SELECT operation_id FROM namespace_allocation WHERE namespace_allocation_id=$id;",
                cancellationToken, ("$id", namespaceAllocationId)) ??
                              throw new InvalidDataException("The namespace allocation is not retained.");
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            await ExecuteAsync(connection, transaction,
                """
                UPDATE namespace_allocation
                SET status=$status,
                    transition_lease_id=$lease,
                    transition_fencing_token=$token,
                    bound_identity=coalesce($identity,bound_identity)
                WHERE namespace_allocation_id=$id;
                """, cancellationToken, ("$status", SyncAuthorityNames.AllocationStatus(status)),
                ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$identity", boundIdentity), ("$id", namespaceAllocationId));
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM namespace_allocation WHERE namespace_allocation_id=$id AND status=$status;",
                cancellationToken, ("$id", namespaceAllocationId),
                ("$status", SyncAuthorityNames.AllocationStatus(status)));
            if (exact != 1)
                throw new InvalidOperationException("The namespace allocation transition was not retained.");
            if (status is SyncAuthorityAllocationStatus.Bound or SyncAuthorityAllocationStatus.Verified)
            {
                var bound = await ScalarStringAsync(connection, transaction,
                    "SELECT bound_identity FROM namespace_allocation WHERE namespace_allocation_id=$id;",
                    cancellationToken, ("$id", namespaceAllocationId));
                if (string.IsNullOrWhiteSpace(bound))
                    throw new InvalidOperationException("A bound namespace allocation requires exact object identity.");
            }
    }

    public async Task<SyncAuthorityNamespaceAllocation> LoadNamespaceAllocationAsync(string namespaceAllocationId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(namespaceAllocationId, nameof(namespaceAllocationId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation_id,side,parent_identity,deterministic_name,owner_nonce_hash,status,bound_identity FROM namespace_allocation WHERE namespace_allocation_id=$id;";
        command.Parameters.AddWithValue("$id", namespaceAllocationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("The namespace allocation is not retained.");
        return new SyncAuthorityNamespaceAllocation(namespaceAllocationId, reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), AllocationStatus(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public async Task RegisterControlNamespaceAsync(SyncAuthorityControlNamespace controlNamespace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlNamespace);
        foreach (var value in new[] { controlNamespace.ControlNamespaceId, controlNamespace.NamespaceAllocationId })
            RequireIdentifier(value, nameof(controlNamespace));
        if (controlNamespace.Side is not ("local" or "dropbox") || controlNamespace.Status != "active")
            throw new ArgumentOutOfRangeException(nameof(controlNamespace));
        RequireJson(controlNamespace.IdentityJson, nameof(controlNamespace));
        RequireJson(controlNamespace.MarkerIdentityJson, nameof(controlNamespace));
        RequireHash(controlNamespace.OwnerMarkerHash, nameof(controlNamespace));
        await WriteAsync((connection, transaction) => RegisterControlNamespaceCoreAsync(connection, transaction,
            controlNamespace, cancellationToken), cancellationToken);
    }

    /// <summary>root718: the transaction-scoped control-namespace publication, so a caller that must publish the
    /// newly active namespace in the SAME transaction as its revision/roots/definition can reuse this exact body
    /// instead of the public wrapper's own transaction.</summary>
    private static async Task RegisterControlNamespaceCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncAuthorityControlNamespace controlNamespace,
        CancellationToken cancellationToken)
    {
            var verified = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM namespace_allocation WHERE namespace_allocation_id=$id AND side=$side AND status='verified';",
                cancellationToken, ("$id", controlNamespace.NamespaceAllocationId),
                ("$side", controlNamespace.Side));
            if (verified != 1)
                throw new InvalidOperationException(
                    "A control namespace requires an exact verified allocation before binding.");
            await ExecuteAsync(connection, transaction,
                """
                INSERT OR IGNORE INTO control_namespace(control_namespace_id,namespace_allocation_id,side,
                    identity_json,marker_identity_json,owner_marker_hash,status,verified_utc)
                VALUES($id,$allocation,$side,$identity,$marker,$hash,'active',$utc);
                """, cancellationToken, ("$id", controlNamespace.ControlNamespaceId),
                ("$allocation", controlNamespace.NamespaceAllocationId), ("$side", controlNamespace.Side),
                ("$identity", controlNamespace.IdentityJson), ("$marker", controlNamespace.MarkerIdentityJson),
                ("$hash", controlNamespace.OwnerMarkerHash), ("$utc", Utc(DateTimeOffset.UtcNow)));
            var exact = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM control_namespace
                WHERE control_namespace_id=$id AND namespace_allocation_id=$allocation AND side=$side
                  AND identity_json=$identity AND marker_identity_json=$marker
                  AND owner_marker_hash=$hash AND status='active';
                """, cancellationToken, ("$id", controlNamespace.ControlNamespaceId),
                ("$allocation", controlNamespace.NamespaceAllocationId), ("$side", controlNamespace.Side),
                ("$identity", controlNamespace.IdentityJson), ("$marker", controlNamespace.MarkerIdentityJson),
                ("$hash", controlNamespace.OwnerMarkerHash));
            if (exact != 1)
                throw new InvalidOperationException(
                    "The deterministic control namespace collides with different retained authority.");
    }

    public async Task BindPairControlNamespaceAsync(string pairId, string side, string controlNamespaceId,
        long bindingGeneration, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        RequireIdentifier(controlNamespaceId, nameof(controlNamespaceId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        if (bindingGeneration < 1) throw new ArgumentOutOfRangeException(nameof(bindingGeneration));
        await WriteAsync((connection, transaction) => BindPairControlNamespaceCoreAsync(connection, transaction,
            pairId, side, controlNamespaceId, bindingGeneration, cancellationToken), cancellationToken);
    }

    /// <summary>root718: the transaction-scoped pair/control-namespace binding, extracted so the atomic published
    /// same-root edit can bind the newly created ownership inside its own transaction.</summary>
    private static async Task BindPairControlNamespaceCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string pairId, string side, string controlNamespaceId,
        long bindingGeneration, CancellationToken cancellationToken)
    {
            var active = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM control_namespace WHERE control_namespace_id=$id AND side=$side AND status='active';",
                cancellationToken, ("$id", controlNamespaceId), ("$side", side));
            if (active != 1)
                throw new InvalidOperationException("The pair control namespace is not active on the required side.");
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO pair_control_namespace(pair_id,side,control_namespace_id,binding_generation,status) VALUES($pair,$side,$control,$generation,'active');",
                cancellationToken, ("$pair", pairId), ("$side", side), ("$control", controlNamespaceId),
                ("$generation", bindingGeneration));
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM pair_control_namespace WHERE pair_id=$pair AND side=$side AND control_namespace_id=$control AND binding_generation=$generation AND status='active';",
                cancellationToken, ("$pair", pairId), ("$side", side), ("$control", controlNamespaceId),
                ("$generation", bindingGeneration));
            if (exact != 1)
                throw new InvalidOperationException(
                    "The pair control-namespace binding collides with different retained authority.");
    }

    /// <summary>
    /// The exact retained private-control preimage of a pair's active control namespace. It is read so a
    /// caller preparing a published same-root edit can bind the edit to the control ownership it prepared
    /// against; <see cref="CommitPublishedSameRootEditAsync"/> re-proves every value inside its own
    /// transaction, so a drift between this read and the commit still fails closed.
    /// </summary>
    public sealed record SyncAuthorityControlNamespacePreimage(string NamespaceId, string AllocationId,
        string IdentityJson, string MarkerIdentityJson, string OwnerMarkerHash, long BindingGeneration);

    public async Task<SyncAuthorityControlNamespacePreimage?> LoadPairControlNamespacePreimageAsync(string pairId,
        string side, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT b.control_namespace_id, c.namespace_allocation_id, c.identity_json, c.marker_identity_json, " +
            "c.owner_marker_hash, b.binding_generation FROM pair_control_namespace b " +
            "JOIN control_namespace c ON c.control_namespace_id=b.control_namespace_id " +
            "WHERE b.pair_id=$pair AND b.side=$side AND b.status='active' AND c.status='active';";
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$side", side);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SyncAuthorityControlNamespacePreimage(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5));
    }

    /// <summary>root718/723: the deterministic role-derived id the allocator uses for its own bindings, so the
    /// atomic published edit can publish the same local_control root id the allocator would have used.</summary>
    private static string DerivedId(string role, params string[] parts) => role + "-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant()[..32];

    public async Task<string> LoadPairStateAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarStringAsync(connection, null,
                   "SELECT state FROM sync_pair WHERE pair_id=$id;", cancellationToken, ("$id", pairId))
               ?? throw new InvalidDataException("The authority database does not retain this pair.");
    }

    public async Task RegisterProviderAccountAsync(string providerAccountId, string installationId,
        string accountId, string rootNamespaceId, string homeNamespaceId, string displayLabel,
        long bindingRevision, CancellationToken cancellationToken = default,
        string provider = SyncProviders.Dropbox)
    {
        foreach (var value in new[] { providerAccountId, installationId, accountId, rootNamespaceId, homeNamespaceId })
            RequireIdentifier(value, nameof(providerAccountId));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayLabel);
        if (bindingRevision < 1) throw new ArgumentOutOfRangeException(nameof(bindingRevision));
        // The exact registered cloud provider decides the stored literal. An unregistered provider is
        // refused before any write and is never routed through the legacy Dropbox default.
        if (!SyncEndpointProviders.TryGet(provider, out var providerDefinition) ||
            providerDefinition is null || providerDefinition.ProviderId is null)
            throw new ArgumentException(
                "This provider is not a registered cloud provider, so no provider account is written.",
                nameof(provider));
        var providerLiteral = providerDefinition.ProviderId;
        await WriteAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO provider_account(provider_account_id,installation_id,provider,account_id,root_namespace_id,
                    home_namespace_id,display_label,binding_revision,verified_utc,status)
                VALUES($id,$installation,$provider,$account,$root,$home,$label,$revision,$utc,'active')
                ON CONFLICT(provider_account_id) DO UPDATE SET
                    display_label=excluded.display_label,binding_revision=excluded.binding_revision,
                    verified_utc=excluded.verified_utc,status='active'
                WHERE provider_account.provider=excluded.provider
                  AND provider_account.installation_id=excluded.installation_id
                  AND provider_account.account_id=excluded.account_id
                  AND provider_account.root_namespace_id=excluded.root_namespace_id
                  AND provider_account.home_namespace_id=excluded.home_namespace_id;
                """, cancellationToken, ("$id", providerAccountId), ("$installation", installationId),
                ("$provider", providerLiteral), ("$account", accountId), ("$root", rootNamespaceId),
                ("$home", homeNamespaceId),
                ("$label", displayLabel), ("$revision", bindingRevision), ("$utc", Utc(DateTimeOffset.UtcNow)));
            var retained = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM provider_account WHERE provider_account_id=$id AND installation_id=$installation AND provider=$provider AND account_id=$account AND root_namespace_id=$root AND home_namespace_id=$home AND status='active';",
                cancellationToken, ("$id", providerAccountId), ("$installation", installationId),
                ("$provider", providerLiteral), ("$account", accountId), ("$root", rootNamespaceId),
                ("$home", homeNamespaceId));
            if (retained != 1) throw new InvalidOperationException("A provider-account identity change requires explicit rebind authority.");
        }, cancellationToken);
    }

    public async Task RegisterVolumeAsync(string volumeId, string installationId, string volumeSerialHex,
        string fileSystem, string? volumeGuidPath, bool supportsFileId128, bool supportsUsn,
        bool supportsAtomicRename, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(volumeId, nameof(volumeId));
        RequireIdentifier(installationId, nameof(installationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeSerialHex);
        if (!string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cloud Pro local authority currently requires NTFS.");
        await WriteAsync(async (connection, transaction) =>
        {
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO volume(volume_id,installation_id,volume_serial_hex,filesystem,volume_guid_path,
                    first_seen_utc,last_seen_utc,supports_file_id_128,supports_usn,supports_atomic_rename,status)
                VALUES($id,$installation,$serial,'NTFS',$guid,$utc,$utc,$fileid,$usn,$rename,'online')
                ON CONFLICT(volume_id) DO UPDATE SET last_seen_utc=excluded.last_seen_utc,status='online'
                WHERE volume.installation_id=excluded.installation_id
                  AND volume.volume_serial_hex=excluded.volume_serial_hex
                  AND upper(volume.filesystem)='NTFS';
                """, cancellationToken, ("$id", volumeId), ("$installation", installationId),
                ("$serial", volumeSerialHex), ("$guid", volumeGuidPath), ("$utc", now),
                ("$fileid", supportsFileId128 ? 1 : 0), ("$usn", supportsUsn ? 1 : 0),
                ("$rename", supportsAtomicRename ? 1 : 0));
            var retained = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM volume WHERE volume_id=$id AND installation_id=$installation AND volume_serial_hex=$serial AND upper(filesystem)='NTFS' AND status='online';",
                cancellationToken, ("$id", volumeId), ("$installation", installationId), ("$serial", volumeSerialHex));
            if (retained != 1) throw new InvalidOperationException("A volume identity change requires explicit rebind authority.");
        }, cancellationToken);
    }

    // F15/V1: the ONE shared volume resolver. Every production caller that knows a local NTFS volume by its
    // installation and serial asks this single method for that volume's id instead of deriving one locally,
    // so two callers holding the same natural key can never race two different volume_id values into the
    // same UNIQUE(installation_id, volume_serial_hex) row. A retained natural key keeps its exact existing
    // volume_id - including a grandfathered caller-local derivation - and a genuinely absent key is derived
    // once with the existing Host convention: role volume, installation, invariant lowercase x8 serial,
    // SHA256 first 32 hex characters. This adds no identity namespace, migrates, re-keys, deletes or
    // rewrites nothing, does not weaken the unique constraint and leaves the strict explicit-id
    // RegisterVolumeAsync above unchanged for callers that request an exact identity.
    public async Task<string> ResolveVolumeIdAsync(string installationId, string volumeSerialHex,
        string fileSystem, string? volumeGuidPath, bool supportsFileId128, bool supportsUsn,
        bool supportsAtomicRename, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(installationId, nameof(installationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeSerialHex);
        if (!string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cloud Pro local authority currently requires NTFS.");
        var serial = NormalizeVolumeSerialHex(volumeSerialHex);
        var derived = DeriveVolumeId(installationId, serial);
        var resolved = derived;
        await WriteAsync(async (connection, transaction) =>
        {
            var retainedCount = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM volume WHERE installation_id=$installation AND upper(volume_serial_hex)=upper($serial);",
                cancellationToken, ("$installation", installationId), ("$serial", serial));
            if (retainedCount > 1)
                throw new InvalidOperationException(
                    "The installation retains more than one volume row for this natural key; no volume identity was chosen.");
            if (retainedCount == 1)
            {
                var existing = await ScalarStringAsync(connection, transaction,
                    "SELECT volume_id FROM volume WHERE installation_id=$installation AND upper(volume_serial_hex)=upper($serial) ORDER BY volume_id LIMIT 1;",
                    cancellationToken, ("$installation", installationId), ("$serial", serial))
                    ?? throw new InvalidDataException("The retained volume row has no identity.");
                await ExecuteAsync(connection, transaction,
                    """
                    UPDATE volume SET last_seen_utc=$utc,status='online',volume_guid_path=$guid,
                        supports_file_id_128=$fileid,supports_usn=$usn,supports_atomic_rename=$rename
                    WHERE volume_id=$id AND installation_id=$installation
                      AND upper(volume_serial_hex)=upper($serial) AND upper(filesystem)='NTFS';
                    """, cancellationToken, ("$id", existing), ("$installation", installationId),
                    ("$serial", serial), ("$guid", volumeGuidPath), ("$utc", Utc(DateTimeOffset.UtcNow)),
                    ("$fileid", supportsFileId128 ? 1 : 0), ("$usn", supportsUsn ? 1 : 0),
                    ("$rename", supportsAtomicRename ? 1 : 0));
                var confirmed = await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM volume WHERE volume_id=$id AND installation_id=$installation AND upper(volume_serial_hex)=upper($serial) AND upper(filesystem)='NTFS' AND status='online';",
                    cancellationToken, ("$id", existing), ("$installation", installationId), ("$serial", serial));
                if (confirmed != 1)
                    throw new InvalidOperationException("The retained volume identity could not be reverified.");
                resolved = existing;
                return;
            }
            var occupied = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM volume WHERE volume_id=$id AND NOT (installation_id=$installation AND upper(volume_serial_hex)=upper($serial));",
                cancellationToken, ("$id", derived), ("$installation", installationId), ("$serial", serial));
            if (occupied != 0)
                throw new InvalidOperationException(
                    "The derived volume identity is already occupied by a different volume; no volume was registered.");
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO volume(volume_id,installation_id,volume_serial_hex,filesystem,volume_guid_path,
                    first_seen_utc,last_seen_utc,supports_file_id_128,supports_usn,supports_atomic_rename,status)
                VALUES($id,$installation,$serial,'NTFS',$guid,$utc,$utc,$fileid,$usn,$rename,'online');
                """, cancellationToken, ("$id", derived), ("$installation", installationId), ("$serial", serial),
                ("$guid", volumeGuidPath), ("$utc", Utc(DateTimeOffset.UtcNow)),
                ("$fileid", supportsFileId128 ? 1 : 0), ("$usn", supportsUsn ? 1 : 0),
                ("$rename", supportsAtomicRename ? 1 : 0));
            var inserted = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM volume WHERE volume_id=$id AND installation_id=$installation AND upper(volume_serial_hex)=upper($serial) AND upper(filesystem)='NTFS' AND status='online';",
                cancellationToken, ("$id", derived), ("$installation", installationId), ("$serial", serial));
            if (inserted != 1)
                throw new InvalidOperationException("A volume identity change requires explicit rebind authority.");
            resolved = derived;
        }, cancellationToken);
        return resolved;
    }

    // The natural key is the installation plus the invariant lowercase hexadecimal serial, so the exact
    // same physical volume reached through the decimal caller and the hexadecimal caller resolves once.
    private static string NormalizeVolumeSerialHex(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || !trimmed.All(Uri.IsHexDigit))
            throw new ArgumentException("A volume serial must be hexadecimal.", nameof(value));
        return trimmed.ToLowerInvariant();
    }

    // The existing Host convention, unchanged: the same role, installation and lowercase serial material
    // the Host already hashes for a newly seen volume. No new identity namespace is introduced.
    private static string DeriveVolumeId(string installationId, string normalizedSerial) =>
        "volume-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "volume\n" + installationId + "\n" + normalizedSerial))).ToLowerInvariant()[..32];

    public async Task RegisterRootBindingAsync(string rootBindingId, string pairId, string side, long generation,
        string identityJson, string displayPath, CancellationToken cancellationToken = default,
        SyncPair? partitionCandidate = null)
    {
        RequireIdentifier(rootBindingId, nameof(rootBindingId));
        RequireIdentifier(pairId, nameof(pairId));
        RequireJson(identityJson, nameof(identityJson));
        if (side is not ("local" or "dropbox" or "local_control" or "dropbox_control"))
            throw new ArgumentOutOfRangeException(nameof(side));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        var normalizedDisplayPath = NormalizeAuthorityPath(side, displayPath);
        await WriteAsync((connection, transaction) => RegisterRootBindingCoreAsync(connection, transaction,
            rootBindingId, pairId, side, generation, identityJson, normalizedDisplayPath, cancellationToken,
            partitionCandidate: partitionCandidate),
            cancellationToken);
    }

    public async Task RegisterLocalRootBindingAsync(string rootBindingId, string pairId, long generation,
        string installationId, string volumeId, string volumeSerialHex, string localFileIdHex,
        string displayPath, CancellationToken cancellationToken = default, SyncPair? partitionCandidate = null)
    {
        foreach (var value in new[]
                 {
                     rootBindingId, pairId, installationId, volumeId, volumeSerialHex, localFileIdHex
                 })
            RequireIdentifier(value, nameof(rootBindingId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        var normalizedDisplayPath = NormalizeAuthorityPath("local", displayPath);
        var identityJson = JsonSerializer.Serialize(new { volumeId, volumeSerial = volumeSerialHex, fileId = localFileIdHex });
        await WriteAsync((connection, transaction) => RegisterLocalRootBindingCoreAsync(connection, transaction,
            rootBindingId, pairId, generation, installationId, volumeId, volumeSerialHex, localFileIdHex,
            identityJson, normalizedDisplayPath, cancellationToken,
            partitionCandidate: partitionCandidate), cancellationToken);
    }

    private static async Task RegisterLocalRootBindingCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string rootBindingId, string pairId, long generation,
        string installationId, string volumeId, string volumeSerialHex, string localFileIdHex,
        string identityJson, string normalizedDisplayPath, CancellationToken cancellationToken,
        bool allowBlockedCandidatePreimage = false, SyncPair? partitionCandidate = null)
    {
            await RequireActiveLocalVolumeCoreAsync(connection, transaction, installationId, volumeId,
                volumeSerialHex, cancellationToken);
            var duplicate = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM root_binding
                WHERE side='local' AND state='active' AND root_binding_id<>$excluded
                  AND json_extract(identity_json,'$.volumeId')=$volume
                  AND lower(json_extract(identity_json,'$.fileId'))=lower($file);
                """, cancellationToken, ("$excluded", rootBindingId), ("$volume", volumeId),
                ("$file", localFileIdHex));
            if (duplicate != 0)
                throw new InvalidOperationException(
                    "The physical local directory identity is already bound to another active pair root.");
            await RegisterRootBindingCoreAsync(connection, transaction, rootBindingId, pairId, "local",
                generation, identityJson, normalizedDisplayPath, cancellationToken,
                allowBlockedCandidatePreimage: allowBlockedCandidatePreimage, partitionCandidate: partitionCandidate);
    }

    private static async Task RequireActiveLocalVolumeCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string installationId, string volumeId, string volumeSerialHex,
        CancellationToken cancellationToken)
    {
        var volume = await ScalarLongAsync(connection, transaction,
            """
            SELECT count(*) FROM volume
            WHERE volume_id=$volume AND installation_id=$installation
              AND upper(volume_serial_hex)=upper($serial) AND status='online';
            """, cancellationToken, ("$volume", volumeId), ("$installation", installationId),
            ("$serial", volumeSerialHex));
        if (volume != 1)
            throw new InvalidOperationException("The local root is not bound to the active installation and NTFS volume identity.");
    }

    public async Task RegisterDropboxRootBindingAsync(string rootBindingId, string pairId, long generation,
        string installationId, string providerAccountId, string rootNamespaceId, string folderId,
        string displayPath, CancellationToken cancellationToken = default, SyncPair? partitionCandidate = null)
    {
        foreach (var value in new[]
                 {
                     rootBindingId, pairId, installationId, providerAccountId, rootNamespaceId, folderId
                 })
            RequireIdentifier(value, nameof(rootBindingId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        var normalizedDisplayPath = NormalizeAuthorityPath("dropbox", displayPath);
        var identityJson = JsonSerializer.Serialize(new { providerAccountId, rootNamespaceId, folderId });
        await WriteAsync((connection, transaction) => RegisterAccountRootBindingCoreAsync(connection, transaction,
            rootBindingId, pairId, generation, installationId, providerAccountId, rootNamespaceId, folderId,
            identityJson, normalizedDisplayPath, cancellationToken, partitionCandidate: partitionCandidate),
            cancellationToken);
    }

    private static async Task RegisterAccountRootBindingCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string rootBindingId, string pairId, long generation,
        string installationId, string providerAccountId, string rootNamespaceId, string folderId,
        string identityJson, string normalizedDisplayPath, CancellationToken cancellationToken,
        bool allowBlockedCandidatePreimage = false, SyncPair? partitionCandidate = null)
    {
            var provider = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM provider_account
                WHERE provider_account_id=$provider AND installation_id=$installation
                  AND root_namespace_id=$root AND status='active';
                """, cancellationToken, ("$provider", providerAccountId), ("$installation", installationId),
                ("$root", rootNamespaceId));
            if (provider != 1)
                throw new InvalidOperationException(
                    "The Dropbox root is not bound to the active installation and account namespace.");
            var duplicate = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM root_binding
                WHERE side='dropbox' AND state='active' AND root_binding_id<>$excluded
                  AND json_extract(identity_json,'$.providerAccountId')=$provider
                  AND json_extract(identity_json,'$.rootNamespaceId')=$root
                  AND json_extract(identity_json,'$.folderId')=$folder;
                """, cancellationToken, ("$excluded", rootBindingId), ("$provider", providerAccountId),
                ("$root", rootNamespaceId), ("$folder", folderId));
            if (duplicate != 0)
                throw new InvalidOperationException(
                    "The Dropbox folder identity is already bound to another active pair root.");
            await RegisterRootBindingCoreAsync(connection, transaction, rootBindingId, pairId, "dropbox",
                generation, identityJson, normalizedDisplayPath, cancellationToken, rootNamespaceId,
                allowBlockedCandidatePreimage, partitionCandidate);
    }

    private static async Task RegisterRootBindingCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string rootBindingId, string pairId, string side, long generation,
        string identityJson, string normalizedDisplayPath, CancellationToken cancellationToken,
        string? validatedDropboxNamespace = null, bool allowBlockedCandidatePreimage = false,
        SyncPair? partitionCandidate = null)
    {
        if (side == "dropbox")
        {
            using var identity = JsonDocument.Parse(identityJson);
            if (identity.RootElement.TryGetProperty("localRootPath", out var path))
            {
                if (path.ValueKind != JsonValueKind.String ||
                    !identity.RootElement.TryGetProperty("localRootIdentity", out var nativeIdentity) ||
                    nativeIdentity.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nativeIdentity.GetString()))
                    throw new InvalidDataException("The right local endpoint lacks its exact native identity.");
                var physicalPath = PilotPath.NormalizeLocalRoot(path.GetString()!);
                if (NormalizeAuthorityPath("dropbox", physicalPath) != normalizedDisplayPath ||
                    await HasOverlappingAuthorityPathAsync(connection, transaction, "local", physicalPath,
                        rootBindingId, cancellationToken, partitionCandidate: partitionCandidate))
                    throw new InvalidOperationException("The physical right local endpoint overlaps another active root or recovery authority.");
            }
        }
        if (await HasOverlappingAuthorityPathAsync(connection, transaction, side, normalizedDisplayPath,
                rootBindingId, cancellationToken, validatedDropboxNamespace, partitionCandidate))
            throw new InvalidOperationException("The root overlaps another active pair or recovery authority boundary.");
        await UpsertRootBindingCoreAsync(connection, transaction, rootBindingId, pairId, side, generation,
            identityJson, normalizedDisplayPath, cancellationToken,
            allowBlockedCandidatePreimage: allowBlockedCandidatePreimage);
    }

    // The single unguarded root-binding insertion shared by every guarded method: the legacy physical local
    // root, the legacy Dropbox cloud root and the typed cloud root all insert through this one helper. It is
    // private, so no caller can reach an insertion without passing its own class of overlap guard first, and
    // no public boolean can bypass a guard.
    private static async Task UpsertRootBindingCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string rootBindingId, string pairId, string side, long generation,
        string identityJson, string normalizedDisplayPath, CancellationToken cancellationToken,
        string state = "active", bool allowBlockedCandidatePreimage = false)
    {
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO root_binding(root_binding_id,pair_id,side,generation,identity_json,display_path,state,observed_utc)
            VALUES($id,$pair,$side,$generation,$identity,$path,$state,$utc)
            ON CONFLICT(root_binding_id) DO UPDATE SET display_path=excluded.display_path,
                state=excluded.state,observed_utc=excluded.observed_utc
            WHERE root_binding.pair_id=excluded.pair_id AND root_binding.side=excluded.side
              AND root_binding.generation=excluded.generation
              AND root_binding.identity_json=excluded.identity_json
              AND (root_binding.state IN ('active','out_of_scope')
                   OR ($allowBlocked = 1 AND root_binding.state='blocked'));
            """,
            cancellationToken, ("$id", rootBindingId), ("$pair", pairId), ("$side", side),
            ("$generation", generation), ("$identity", identityJson), ("$path", normalizedDisplayPath),
            ("$utc", Utc(DateTimeOffset.UtcNow)), ("$state", state),
            ("$allowBlocked", allowBlockedCandidatePreimage ? 1 : 0));
        var exact = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM root_binding WHERE root_binding_id=$id AND pair_id=$pair AND side=$side AND generation=$generation AND identity_json=$identity AND display_path=$path AND state=$state;",
            cancellationToken, ("$id", rootBindingId), ("$pair", pairId), ("$side", side),
            ("$generation", generation), ("$identity", identityJson), ("$path", normalizedDisplayPath),
            ("$state", state));
        if (exact != 1)
            throw new InvalidOperationException("The deterministic root binding collides with different authority.");
    }

    public async Task RegisterDropboxRecoveryRootAsync(string recoveryRootBindingId, string installationId,
        string providerAccountId, string displayPath, string folderId, string rootNamespaceId, long generation,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(recoveryRootBindingId, nameof(recoveryRootBindingId));
        RequireIdentifier(installationId, nameof(installationId));
        RequireIdentifier(providerAccountId, nameof(providerAccountId));
        RequireIdentifier(folderId, nameof(folderId));
        RequireIdentifier(rootNamespaceId, nameof(rootNamespaceId));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        var normalizedPath = NormalizeAuthorityPath("dropbox", displayPath);
        await WriteAsync((connection, transaction) => RegisterDropboxRecoveryRootCoreAsync(connection, transaction,
            recoveryRootBindingId, installationId, providerAccountId, normalizedPath, folderId, rootNamespaceId,
            generation, cancellationToken), cancellationToken);
    }

    private static async Task RegisterDropboxRecoveryRootCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string recoveryRootBindingId, string installationId,
        string providerAccountId, string normalizedPath, string folderId, string rootNamespaceId, long generation,
        CancellationToken cancellationToken)
    {
        var identityJson = JsonSerializer.Serialize(new { folderId, rootNamespaceId });
            var provider = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM provider_account WHERE provider_account_id=$id AND installation_id=$installation AND root_namespace_id=$root AND status='active';",
                cancellationToken, ("$id", providerAccountId), ("$installation", installationId),
                ("$root", rootNamespaceId));
            if (provider != 1) throw new InvalidOperationException("The recovery root is not bound to the active Dropbox account namespace.");
            if (await HasOverlappingAuthorityPathAsync(connection, transaction, "dropbox", normalizedPath,
                    recoveryRootBindingId, cancellationToken, rootNamespaceId))
                throw new InvalidOperationException("The dedicated Dropbox recovery root overlaps an active pair root.");
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO recovery_root_binding(recovery_root_binding_id,installation_id,provider_account_id,side,
                    display_path,normalized_path_key,identity_json,generation,status,verified_utc)
                VALUES($id,$installation,$provider,'dropbox',$path,$path,$identity,$generation,'active',$utc)
                ON CONFLICT(recovery_root_binding_id) DO UPDATE SET display_path=excluded.display_path,
                    normalized_path_key=excluded.normalized_path_key,verified_utc=excluded.verified_utc,
                    provider_corpus_id=recovery_root_binding.provider_corpus_id
                WHERE recovery_root_binding.installation_id=excluded.installation_id
                  AND recovery_root_binding.provider_account_id=excluded.provider_account_id
                  AND recovery_root_binding.side=excluded.side
                  AND recovery_root_binding.identity_json=excluded.identity_json
                  AND recovery_root_binding.generation=excluded.generation
                  AND recovery_root_binding.status='active';
                """,
                cancellationToken, ("$id", recoveryRootBindingId), ("$installation", installationId),
                ("$provider", providerAccountId), ("$path", normalizedPath), ("$identity", identityJson),
                ("$generation", generation), ("$utc", Utc(DateTimeOffset.UtcNow)));
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id=$id AND installation_id=$installation AND provider_account_id=$provider AND normalized_path_key=$path AND identity_json=$identity AND generation=$generation AND status='active';",
                cancellationToken, ("$id", recoveryRootBindingId), ("$installation", installationId),
                ("$provider", providerAccountId), ("$path", normalizedPath), ("$identity", identityJson),
                ("$generation", generation));
            if (exact != 1)
                throw new InvalidOperationException("The deterministic recovery-root binding collides with different authority.");
    }

    public async Task RegisterObjectAsync(string objectId, string objectKind,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(objectId, nameof(objectId));
        if (objectKind is not ("file" or "directory")) throw new ArgumentOutOfRangeException(nameof(objectKind));
        await WriteAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO logical_object(object_id,object_kind,created_utc) VALUES($id,$kind,$utc);",
                cancellationToken, ("$id", objectId), ("$kind", objectKind), ("$utc", Utc(DateTimeOffset.UtcNow)));
        }, cancellationToken);
    }

    public async Task RegisterObjectVersionAsync(string objectVersionId, string objectId, string side,
        string identityJson, string? contentHash, long? size, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(objectVersionId, nameof(objectVersionId));
        RequireIdentifier(objectId, nameof(objectId));
        RequireJson(identityJson, nameof(identityJson));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        if (contentHash is not null) RequireHash(contentHash, nameof(contentHash));
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        await WriteAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO object_version(object_version_id,object_id,side,identity_json,content_hash,size_bytes,observed_utc) VALUES($id,$object,$side,$identity,$hash,$size,$utc);",
                cancellationToken, ("$id", objectVersionId), ("$object", objectId), ("$side", side),
                ("$identity", identityJson), ("$hash", contentHash), ("$size", size),
                ("$utc", Utc(DateTimeOffset.UtcNow)));
        }, cancellationToken);
    }

    public async Task<string?> ResolveObjectIdForPhysicalIdentityAsync(string pairId, string side,
        string physicalIdentityJson, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        RequireJson(physicalIdentityJson, nameof(physicalIdentityJson));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ResolveObjectIdForPhysicalIdentityCoreAsync(connection, null, pairId, side,
            physicalIdentityJson, cancellationToken);
    }

    private static async Task<string?> ResolveObjectIdForPhysicalIdentityCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string pairId, string side,
        string physicalIdentityJson, CancellationToken cancellationToken)
    {
        using var identity = JsonDocument.Parse(physicalIdentityJson);
        var fileId = identity.RootElement.GetProperty("fileId").GetString();
        var volumeSerial = side == "local"
            ? identity.RootElement.GetProperty("volumeSerial").GetString()
            : null;
        if (string.IsNullOrWhiteSpace(fileId) || side == "local" && string.IsNullOrWhiteSpace(volumeSerial))
            throw new InvalidDataException("The physical identity binding is incomplete.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = side == "local"
            ? """
              WITH candidate(object_id,identity_json) AS (
                  SELECT v.object_id,v.identity_json
                  FROM object_version v
                  JOIN effective_observation o ON o.object_version_id=v.object_version_id
                  JOIN scan_generation s ON s.scan_generation_id=o.scan_generation_id
                  WHERE s.scan_generation_id=(
                        SELECT latest.scan_generation_id FROM scan_generation latest
                        WHERE latest.pair_id=$pair AND latest.side='local' AND latest.status='complete'
                        ORDER BY latest.generation DESC LIMIT 1)
                    AND v.side='local'
                  UNION
                  SELECT v.object_id,v.identity_json
                  FROM synchronization_point p
                  JOIN object_version v ON v.object_version_id=p.local_object_version_id
                  WHERE p.pair_id=$pair AND p.authority_generation=(
                        SELECT max(latest.authority_generation) FROM synchronization_point latest
                        WHERE latest.pair_id=p.pair_id AND latest.object_id=p.object_id)
              )
              SELECT DISTINCT object_id FROM candidate
              WHERE json_extract(identity_json,'$.volumeSerial')=$volume
                AND json_extract(identity_json,'$.fileId')=$file;
              """
            : """
              WITH candidate(object_id,identity_json) AS (
                  SELECT v.object_id,v.identity_json
                  FROM object_version v
                  JOIN effective_observation o ON o.object_version_id=v.object_version_id
                  JOIN scan_generation s ON s.scan_generation_id=o.scan_generation_id
                  WHERE s.scan_generation_id=(
                        SELECT latest.scan_generation_id FROM scan_generation latest
                        WHERE latest.pair_id=$pair AND latest.side='dropbox' AND latest.status='complete'
                        ORDER BY latest.generation DESC LIMIT 1)
                    AND v.side='dropbox'
                  UNION
                  SELECT v.object_id,v.identity_json
                  FROM synchronization_point p
                  JOIN object_version v ON v.object_version_id=p.dropbox_object_version_id
                  WHERE p.pair_id=$pair AND p.authority_generation=(
                        SELECT max(latest.authority_generation) FROM synchronization_point latest
                        WHERE latest.pair_id=p.pair_id AND latest.object_id=p.object_id)
              )
              SELECT DISTINCT object_id FROM candidate
              WHERE json_extract(identity_json,'$.fileId')=$file;
              """;
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$file", fileId);
        if (side == "local") command.Parameters.AddWithValue("$volume", volumeSerial!);
        var matches = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) matches.Add(reader.GetString(0));
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException("One physical identity is bound to multiple logical objects.")
        };
    }

    private static string AuthorityDeterministicId(string role, params string[] parts)
    {
        var material = role + "\n" + string.Join("\n", parts);
        return role + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant()[..32];
    }

    public async Task RegisterMembershipAsync(string membershipId, string objectId, string rootBindingId,
        string relativePathKey, long generation, string state = "active", CancellationToken cancellationToken = default)
    {
        RequireIdentifier(membershipId, nameof(membershipId));
        RequireIdentifier(objectId, nameof(objectId));
        RequireIdentifier(rootBindingId, nameof(rootBindingId));
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePathKey);
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        if (state is not ("active" or "provisional" or "retired" or "out_of_scope" or "ambiguous"))
            throw new ArgumentOutOfRangeException(nameof(state));
        await WriteAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO membership(membership_id,object_id,root_binding_id,relative_path_key,generation,state,observed_utc) VALUES($id,$object,$root,$path,$generation,$state,$utc);",
                cancellationToken, ("$id", membershipId), ("$object", objectId), ("$root", rootBindingId),
                ("$path", relativePathKey), ("$generation", generation), ("$state", state),
                ("$utc", Utc(DateTimeOffset.UtcNow)));
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM membership WHERE membership_id=$id AND object_id=$object AND root_binding_id=$root AND relative_path_key=$path AND generation=$generation AND state=$state;",
                cancellationToken, ("$id", membershipId), ("$object", objectId), ("$root", rootBindingId),
                ("$path", relativePathKey), ("$generation", generation), ("$state", state));
            if (exact != 1)
                throw new InvalidOperationException("The deterministic membership identity collides with different retained authority.");
        }, cancellationToken);
    }

    public async Task<long?> LoadMembershipGenerationAsync(string membershipId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(membershipId, nameof(membershipId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT generation FROM membership WHERE membership_id=$id;";
        command.Parameters.AddWithValue("$id", membershipId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null || result is DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async Task<long> NextMembershipGenerationAsync(string rootBindingId, string relativePath,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(rootBindingId, nameof(rootBindingId));
        var path = PilotPath.NormalizeRelative(relativePath);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarLongAsync(connection, null,
            "SELECT coalesce(max(generation),0)+1 FROM membership WHERE root_binding_id=$root AND relative_path_key=$path;",
            cancellationToken, ("$root", rootBindingId), ("$path", path));
    }

    public async Task<SyncAuthorityObjectVersion> LoadObjectVersionAsync(string objectVersionId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(objectVersionId, nameof(objectVersionId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT object_id,side,identity_json,content_hash,size_bytes FROM object_version WHERE object_version_id=$id;";
        command.Parameters.AddWithValue("$id", objectVersionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidDataException("The object version is not retained.");
        return new SyncAuthorityObjectVersion(objectVersionId, reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4));
    }

    /// <summary>
    /// The moving side's version in the synchronization point a native move was planned from. Read only by
    /// the move executors and reconcilers when they commit: the stationary side still holds exactly that
    /// baseline content, so pairing it with this version is true even when the moved file was also edited,
    /// and the next comparison then plans the ordinary update for that edit.
    /// </summary>
    public async Task<string> LoadMoveBaselineVersionAsync(SyncAuthorityOperationSnapshot operation,
        string movingSide, CancellationToken cancellationToken = default)
    {
        if (movingSide is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(movingSide));
        using var preconditions = JsonDocument.Parse(operation.PreconditionsJson);
        var pointId = preconditions.RootElement.TryGetProperty("synchronizationPointId", out var value) &&
                      value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(pointId) || operation.ObjectId is null)
            throw new InvalidDataException("The move has no retained baseline synchronization point.");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarStringAsync(connection, null, movingSide == "local"
                ? "SELECT local_object_version_id FROM synchronization_point WHERE synchronization_point_id=$id AND pair_id=$pair AND object_id=$object;"
                : "SELECT dropbox_object_version_id FROM synchronization_point WHERE synchronization_point_id=$id AND pair_id=$pair AND object_id=$object;",
                cancellationToken, ("$id", pointId), ("$pair", operation.PairId), ("$object", operation.ObjectId))
            ?? throw new InvalidDataException("The move's baseline synchronization point is not retained.");
    }

    public async Task<string?> LoadLatestObjectVersionEvidenceAsync(string objectVersionId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(objectVersionId, nameof(objectVersionId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarStringAsync(connection, null,
            """
            SELECT evidence_json
            FROM effective_observation
            WHERE object_version_id=$id AND complete_generation=1
            ORDER BY observed_utc DESC, observation_id DESC
            LIMIT 1;
            """, cancellationToken, ("$id", objectVersionId));
    }

    public async Task<string?> LoadCachedLocalContentHashAsync(PilotLocalFileFingerprint fingerprint,
        CancellationToken cancellationToken = default)
        => await LoadCachedLocalContentHashAsync(fingerprint, null, cancellationToken);

    // The retained hash is only reused when the row was produced with the same content-identity
    // algorithm the current pair uses. A row with no recorded algorithm is a legacy Dropbox identity
    // and defaults to the Dropbox scheme, so existing Dropbox state keeps reusing its cache while a
    // Drive pair can never reuse a Dropbox-scheme hash.
    public async Task<string?> LoadCachedLocalContentHashAsync(PilotLocalFileFingerprint fingerprint,
        string? requiredAlgorithm, CancellationToken cancellationToken = default)
    {
        var volume = fingerprint.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture);
        var fileId = fingerprint.FileId.ToString("x16", CultureInfo.InvariantCulture);
        var lastWrite = fingerprint.LastWriteUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT content_hash
            FROM object_version
            WHERE side='local' AND content_hash IS NOT NULL AND size_bytes=$size
              AND lower(json_extract(identity_json,'$.volumeSerial'))=lower($volume)
              AND lower(json_extract(identity_json,'$.fileId'))=lower($file)
              AND json_extract(identity_json,'$.lastWriteUtc')=$write
              AND coalesce(json_extract(identity_json,'$.contentAlgorithm'),$legacyAlgorithm)=$algorithm
            ORDER BY observed_utc DESC
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$size", fingerprint.Size);
        command.Parameters.AddWithValue("$volume", volume);
        command.Parameters.AddWithValue("$file", fileId);
        command.Parameters.AddWithValue("$write", lastWrite);
        command.Parameters.AddWithValue("$legacyAlgorithm", SyncContentIdentity.DropboxBlockSha256);
        command.Parameters.AddWithValue("$algorithm",
            requiredAlgorithm ?? SyncContentIdentity.DropboxBlockSha256);
        var hashes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) hashes.Add(reader.GetString(0));
        return hashes.Count switch
        {
            0 => null,
            1 => hashes[0],
            _ => throw new InvalidDataException(
                "One unchanged local physical identity has conflicting retained content hashes.")
        };
    }

    public async Task CreateOperationAsync(SyncAuthorityOperationPlan plan,
        SyncAuthorityCommandAdmission admission,
        CancellationToken cancellationToken = default,
        IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? cloudHierarchyEvidence = null,
        SyncAuthorityActionReservation? reservation = null)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ValidatePlan(plan);
        var persistedPreconditions = PersistPlanningAuthorities(plan.PreconditionsJson,
            plan.PlanningAuthorities ?? []);
        persistedPreconditions = PersistSubtreeReservations(persistedPreconditions,
            plan.SubtreeReservations ?? []);
        await WriteAsync(async (connection, transaction) =>
        {
            await RequireCurrentCommandAdmissionCoreAsync(connection, transaction, admission,
                [SyncAuthorityCommandPurpose.NormalEffect, SyncAuthorityCommandPurpose.ControlAllocation],
                cancellationToken);
            if (await IsExactRetainedActionCoreAsync(connection, transaction, plan, persistedPreconditions,
                    cancellationToken)) return;
            var compactCopy = SyncAuthorityCompletedCopyMove.Parse(plan.PreconditionsJson);
            if (compactCopy is not null &&
                !plan.Dependencies.Any(d => d.DependsOnOperationId == compactCopy.CopyOperationId && d.Kind == "must_complete"))
            {
                if (plan.Kind != SyncAuthorityOperationKind.DeleteLocal || plan.PairId is null ||
                    plan.ObjectId != compactCopy.ObjectId ||
                    !(await ReadCurrentCopyMovesCoreAsync(connection, transaction, plan.PairId, compactCopy,
                        cancellationToken)).Contains(compactCopy))
                    throw new InvalidDataException("A missing completed-copy prerequisite requires this exact current normalized proof.");
            }
            var planningStage = TimingStart();
            await RequireNestedPartitionDisjointEffectAsync(connection, transaction, plan, cancellationToken);
            using var recoveryPreconditions = JsonDocument.Parse(plan.PreconditionsJson);
            if (recoveryPreconditions.RootElement.TryGetProperty("action", out var restoreAction) &&
                restoreAction.ValueKind == JsonValueKind.String && restoreAction.GetString() == "restore_recovery" &&
                recoveryPreconditions.RootElement.TryGetProperty("recoveryArtifactId", out var sourceArtifact) &&
                sourceArtifact.ValueKind == JsonValueKind.String)
                await RequireRecoverySourceAvailableAsync(connection, transaction, sourceArtifact.GetString()!,
                    cancellationToken);
            if (recoveryPreconditions.RootElement.ValueKind == JsonValueKind.Object &&
                recoveryPreconditions.RootElement.TryGetProperty("action", out var recoveryAction) &&
                recoveryAction.ValueKind == JsonValueKind.String && recoveryAction.GetString() == "restore_recovery")
                foreach (var member in plan.Members)
                {
                    using var expected = JsonDocument.Parse(member.ExpectedStateJson);
                    if (expected.RootElement.ValueKind == JsonValueKind.Object &&
                        expected.RootElement.TryGetProperty("sourceRecoveryArtifactId", out var source) &&
                        source.ValueKind == JsonValueKind.String)
                        await RequireRecoverySourceAvailableAsync(connection, transaction, source.GetString()!,
                            cancellationToken);
                }
            foreach (var planningAuthority in plan.PlanningAuthorities ?? [])
                if (!await IsPlanningAuthorityCurrentAsync(connection, transaction, planningAuthority,
                        cancellationToken))
                    throw new InvalidOperationException(
                        "The exact observation-set authority changed before the operation reservation was committed.");
            if (plan.PairId is not null)
            {
                var configuration = await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM pair_config_revision WHERE pair_id=$pair AND revision=$revision;",
                    cancellationToken, ("$pair", plan.PairId), ("$revision", plan.ConfigurationRevision));
                if (configuration != 1)
                    throw new InvalidOperationException("The operation has no exact retained pair configuration revision.");
                using var preconditions = JsonDocument.Parse(plan.PreconditionsJson);
                if (preconditions.RootElement.TryGetProperty("conflictId", out _) &&
                    await ScalarLongAsync(connection, transaction,
                        "SELECT count(*) FROM sync_pair WHERE pair_id=$pair AND configuration_revision=$revision AND state<>'retired';",
                        cancellationToken, ("$pair", plan.PairId), ("$revision", plan.ConfigurationRevision)) != 1)
                    throw new InvalidOperationException("The pair policy changed before this exact conflict operation could be reserved.");
            }
            BeforeOperationRegistrationForTest?.Invoke(plan);
            ReportTiming("plan-authority", planningStage);
            planningStage = TimingStart();
            await RequireFreshActionTargetCoreAsync(connection, transaction, plan, admission, cancellationToken);
            await RequireWorkWindowSlotAsync(connection, transaction, admission, cancellationToken);
            var registration = await ClaimActionRegistrationCoreAsync(connection, transaction, plan,
                admission, reservation, cancellationToken);
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO operation(operation_id,pair_id,object_id,kind,status,priority,configuration_revision,
                    capability_profile_id,idempotency_key,preconditions_json,created_utc,operation_group_id,
                    origin_command_id,execution_command_id,registration_generation,registration_sequence,registration_intent_sha256)
                VALUES($id,$pair,$object,$kind,'planned',$priority,$revision,$profile,$key,$preconditions,$utc,$group,
                    $origin,$execution,$generation,$sequence,$intent);
                """, cancellationToken, ("$id", plan.OperationId), ("$pair", plan.PairId),
                ("$object", plan.ObjectId), ("$kind", SyncAuthorityNames.OperationKind(plan.Kind)),
                ("$priority", plan.Priority), ("$revision", plan.ConfigurationRevision),
                ("$profile", plan.CapabilityProfileId), ("$key", plan.IdempotencyKey),
                ("$preconditions", persistedPreconditions), ("$utc", now),
                ("$group", plan.OperationGroupId),
                ("$origin", admission.CommandRequestId), ("$execution", admission.CommandRequestId),
                ("$generation", registration.Generation), ("$sequence", registration.Sequence),
                ("$intent", ActionIntentFingerprint(plan, persistedPreconditions)));

            foreach (var member in plan.Members)
            {
                await ExecuteAsync(connection, transaction,
                    """
                    INSERT INTO operation_member(operation_member_id,operation_id,pair_id,object_id,role,root_binding_id,
                        membership_id,object_version_id,expected_membership_generation,expected_path_key,expected_state_json)
                    VALUES($id,$operation,$pair,$object,$role,$root,$membership,$version,$generation,$path,$state);
                    """, cancellationToken, ("$id", member.MemberId), ("$operation", plan.OperationId),
                    ("$pair", member.PairId), ("$object", member.ObjectId), ("$role", member.Role),
                    ("$root", member.RootBindingId), ("$membership", member.MembershipId),
                    ("$version", member.ObjectVersionId), ("$generation", member.ExpectedMembershipGeneration),
                    ("$path", member.ExpectedPathKey), ("$state", member.ExpectedStateJson));
                // The sole production member-insertion path keeps an already-loaded
                // same-transaction candidate index complete before the next guard.
                IncludeExecutionReservationMember(transaction, member, plan.Kind);
            }

            if (!await HasCurrentOperationCommandScopeAsync(connection, transaction, plan.OperationId,
                    SyncAuthorityCommandPurposeNames.Wire(admission.Purpose), cancellationToken))
                throw new InvalidOperationException("The accepted command does not cover this operation's exact kind and scope.");

            // I8: the reviewed-route consent binding is stamped inside THIS same creating transaction, after
            // every operation_member insert (including the execution-reservation member index) and before
            // dependencies, allocations and the commit, so a reviewed operation can never be retained
            // unbound. The stamp writes only while both consent columns are still NULL, so an exact
            // idempotent replay and an already-retained legacy row are never restamped or revived.
            await StampOperationConsentAuthorityAsync(connection, transaction, plan, persistedPreconditions,
                cancellationToken);

            foreach (var dependency in plan.Dependencies)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO operation_dependency(operation_id,depends_on_operation_id,dependency_kind) VALUES($operation,$dependency,$kind);",
                    cancellationToken, ("$operation", plan.OperationId),
                    ("$dependency", dependency.DependsOnOperationId), ("$kind", dependency.Kind));
            }

            foreach (var allocation in plan.Allocations)
            {
                await ExecuteAsync(connection, transaction,
                    """
                    INSERT INTO resource_allocation(allocation_id,operation_id,control_namespace_id,resource_kind,
                        deterministic_name,intended_parent_identity,owner_nonce_hash,status,created_utc)
                    VALUES($id,$operation,$namespace,$kind,$name,$parent,$nonce,'authorized',$utc);
                    """, cancellationToken, ("$id", allocation.AllocationId), ("$operation", plan.OperationId),
                    ("$namespace", allocation.ControlNamespaceId),
                    ("$kind", SyncAuthorityNames.AllocationKind(allocation.Kind)),
                    ("$name", allocation.DeterministicName), ("$parent", allocation.IntendedParentIdentity),
                    ("$nonce", allocation.OwnerNonceHash), ("$utc", now));
            }

            ReportTiming("plan-members", planningStage);
            planningStage = TimingStart();
            var currentPaths = plan.Members
                .Where(item => RequiresPathReservation(plan.Kind, item.Role))
                .Where(item => !string.IsNullOrWhiteSpace(item.ExpectedPathKey))
                .Select(item => (item.RootBindingId,
                    Path: PilotPath.NormalizeRelative(item.ExpectedPathKey!)))
                .Distinct(new RootPathComparer())
                .ToArray();
            var currentSubtrees = (plan.SubtreeReservations ?? [])
                .Select(item => item with { RelativePath = PilotPath.NormalizeRelative(item.RelativePath) })
                .ToArray();
            // A corresponding INCLUDED pair reaches one physical object through two different root bindings,
            // so equal-root comparison alone cannot see the alias. The projection is derived in THIS
            // transaction and expands the compared roots AND the translated path candidates BEFORE the
            // SQL/candidate filter, so an aliased competing reservation is never dropped. The original
            // ordinal checks below remain authoritative.
            var aliasProjection = await LoadReservationAliasProjectionAsync(connection, transaction,
                cloudHierarchyEvidence, plan, cancellationToken);
            ReportTiming("plan-alias-projection", planningStage);
            planningStage = TimingStart();
            var relevantRoots = currentPaths.Select(item => item.RootBindingId)
                .Concat(currentSubtrees.Select(item => item.RootBindingId))
                .ToHashSet(StringComparer.Ordinal);
            var queryRoots = aliasProjection.ExpandRoots(relevantRoots);
            var queryPaths = currentPaths
                .SelectMany(item => aliasProjection.EnumerateEquivalent(item.RootBindingId, item.Path))
                .Select(item => item.Path)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var canonicalCurrentPaths = currentPaths
                .Select(item => aliasProjection.Canonicalize(item.RootBindingId, item.Path))
                .Distinct().ToArray();
            var canonicalCurrentSubtrees = currentSubtrees
                .Select(item => aliasProjection.Canonicalize(item.RootBindingId, item.RelativePath))
                .Distinct().ToArray();
            // Alias roots AND their equivalent paths were expanded above. They
            // therefore use the same conservative candidate lookup as a single
            // pair; merely having a nested pair must not read every sibling's
            // complete retained plan for every file. Subtrees keep the full-root
            // fallback, and final canonical overlap checks stay intact.
            var allPaths = currentSubtrees.Length > 0 ||
                           currentPaths.Any(item => item.Path.Any(character => character > 127));
            // The in-memory index uses the SAME normalized ordinal path equality
            // as the final guard, including Unicode. SQLite NOCASE is only ASCII:
            // keep its original conservative fallback when no index is available,
            // but do not expand every Unicode file to the entire retained root.
            var candidateIds = currentSubtrees.Length > 0
                ? null : await LoadExecutionReservationCandidateIdsAsync(connection, transaction,
                    queryRoots, queryPaths, cancellationToken);
            if (candidateIds is not null) allPaths = true;
            var candidateSource = candidateIds is null
                ? "FROM operation_member m"
                : "FROM json_each($candidateMembers) candidate CROSS JOIN operation_member m ON m.operation_member_id=candidate.value";
            var competingReservations = new List<CompetingPathReservation>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    $"""
                    SELECT o.operation_id,m.root_binding_id,m.expected_path_key,m.role,
                           o.kind,o.status,o.preconditions_json
                    {candidateSource}
                    JOIN operation o ON o.operation_id=m.operation_id
                    WHERE m.operation_id<>$operation
                      AND m.expected_path_key IS NOT NULL
                      AND (o.status NOT IN ('completed','cancelled','superseded','blocked')
                           OR (o.status='blocked' AND (o.kind='cross_pair_move'
                               OR json_extract(o.preconditions_json,'$.action')='restore_recovery')))
                      AND m.root_binding_id IN (SELECT value FROM json_each($roots))
                      AND ($allPaths=1 OR o.kind='cross_pair_move'
                           OR m.expected_path_key COLLATE NOCASE IN (SELECT value FROM json_each($paths))
                           OR m.expected_path_key GLOB '*[^ -~]*')
                    ORDER BY o.operation_id,m.operation_member_id;
                    """;
                command.Parameters.AddWithValue("$operation", plan.OperationId);
                // A normal one-file reservation only competes with the same path or a
                // cross-pair subtree. Do not deserialize every sibling's retained plan
                // for every file (several GB for the captured Bridge plan). The existing
                // ordinal checks below remain authoritative. Without a candidate index,
                // SQLite NOCASE needs the full Unicode/root fallback. With the index,
                // all selected IDs reach those checks without another path filter.
                command.Parameters.AddWithValue("$roots", JsonSerializer.Serialize(queryRoots));
                command.Parameters.AddWithValue("$paths", JsonSerializer.Serialize(queryPaths));
                command.Parameters.AddWithValue("$allPaths", allPaths ? 1 : 0);
                if (candidateIds is not null)
                    command.Parameters.AddWithValue("$candidateMembers", JsonSerializer.Serialize(candidateIds));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var rootBindingId = reader.GetString(1);
                    var kind = SyncAuthorityNames.OperationKind(reader.GetString(4));
                    if (!queryRoots.Contains(rootBindingId) ||
                        !RequiresPathReservation(kind, reader.GetString(3)))
                        continue;
                    competingReservations.Add(new CompetingPathReservation(reader.GetString(0),
                        rootBindingId, PilotPath.NormalizeRelative(reader.GetString(2)), kind,
                        reader.GetString(5), reader.GetString(6)));
                }
            }

            ReportTiming("plan-reservation-query", planningStage);
            planningStage = TimingStart();
            foreach (var competingOperation in competingReservations
                         .GroupBy(item => item.OperationId, StringComparer.Ordinal))
            {
                var first = competingOperation.First();
                var competingPaths = competingOperation
                    .Select(item => aliasProjection.Canonicalize(item.RootBindingId, item.Path)).ToArray();
                var competingSubtrees = first.Kind == SyncAuthorityOperationKind.CrossPairMove
                    ? LoadSubtreeReservations(first.PreconditionsJson)
                        .Select(item => aliasProjection.Canonicalize(item.RootBindingId,
                            PilotPath.NormalizeRelative(item.RelativePath))).ToArray()
                    : [];
                var overlaps = canonicalCurrentPaths.Any(current => competingPaths.Any(competing =>
                                   SameRootAndPath(current.Root, current.Path,
                                       competing.Root, competing.Path)) ||
                               competingSubtrees.Any(competing => SameRootAndDescendant(
                                   current.Root, current.Path,
                                   competing.Root, competing.Path))) ||
                               canonicalCurrentSubtrees.Any(current => competingPaths.Any(competing =>
                                   SameRootAndDescendant(competing.Root, competing.Path,
                                       current.Root, current.Path)) ||
                               competingSubtrees.Any(competing => SameRootAndOverlappingSubtree(
                                   current.Root, current.Path,
                                   competing.Root, competing.Path)));
                if (!overlaps) continue;

                var safelySuperseded = false;
                if (first.Status == "ready" &&
                    first.Kind != SyncAuthorityOperationKind.CrossPairMove)
                {
                    var planningCurrent = true;
                    foreach (var retainedAuthority in LoadPlanningAuthorities(first.PreconditionsJson))
                        planningCurrent &= await IsPlanningAuthorityCurrentAsync(connection, transaction,
                            retainedAuthority, cancellationToken);
                    if (!planningCurrent)
                    {
                        var eligible = await ScalarLongAsync(connection, transaction,
                            """
                            SELECT count(*) FROM operation o
                            WHERE o.operation_id=$id AND o.status='ready' AND o.current_lease_id IS NULL
                              AND NOT EXISTS(SELECT 1 FROM operation_attempt a
                                             WHERE a.operation_id=o.operation_id)
                              AND NOT EXISTS(SELECT 1 FROM resource_allocation r
                                             WHERE r.operation_id=o.operation_id
                                               AND r.status<>'authorized');
                            """, cancellationToken, ("$id", first.OperationId));
                        if (eligible == 1)
                            safelySuperseded = await ExecuteCountAsync(connection, transaction,
                                "UPDATE operation SET status='superseded',terminal_utc=$utc WHERE operation_id=$id AND status='ready';",
                                cancellationToken, ("$id", first.OperationId),
                                ("$utc", Utc(DateTimeOffset.UtcNow))) == 1;
                    }
                }
                if (safelySuperseded)
                    continue;
                throw new InvalidOperationException(
                    "A prior nonterminal operation reservation overlaps this exact root/path or retained subtree prefix.");
            }

            ReportTiming("plan-reservation-guard", planningStage);
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status='ready',ready_utc=$utc WHERE operation_id=$id AND status='planned';",
                cancellationToken, ("$utc", now), ("$id", plan.OperationId));
        }, cancellationToken);
    }

    public async Task<SyncAuthorityScanGeneration> BeginScanGenerationAsync(string pairId, string rootBindingId,
        string side, string triggerKind, string scopeJson, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        RequireIdentifier(rootBindingId, nameof(rootBindingId));
        RequireJson(scopeJson, nameof(scopeJson));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        if (triggerKind is not ("initial" or "monitor" or "periodic" or "recovery" or "manual" or "cursor_reset" or "overflow"))
            throw new ArgumentOutOfRangeException(nameof(triggerKind));
        SyncAuthorityScanGeneration? result = null;
        await WriteAsync(async (connection, transaction) =>
        {
            var bindingCount = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM root_binding WHERE root_binding_id=$root AND pair_id=$pair AND side=$side AND state='active';",
                cancellationToken, ("$root", rootBindingId), ("$pair", pairId), ("$side", side));
            if (bindingCount != 1) throw new InvalidOperationException("The scan root is not the current identity-bound pair root.");
            var generation = await ScalarLongAsync(connection, transaction,
                "SELECT coalesce(max(generation),0)+1 FROM scan_generation WHERE root_binding_id=$root;",
                cancellationToken, ("$root", rootBindingId));
            var id = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            await ExecuteAsync(connection, transaction,
                "INSERT INTO scan_generation(scan_generation_id,pair_id,root_binding_id,side,generation,trigger_kind,scope_json,started_utc,status) VALUES($id,$pair,$root,$side,$generation,$trigger,$scope,$utc,'incomplete');",
                cancellationToken, ("$id", id), ("$pair", pairId), ("$root", rootBindingId), ("$side", side),
                ("$generation", generation), ("$trigger", triggerKind), ("$scope", scopeJson), ("$utc", Utc(now)));
            result = new SyncAuthorityScanGeneration(id, pairId, rootBindingId, side, generation, triggerKind, now);
        }, cancellationToken);
        return result!;
    }

    public async Task AppendObservationAsync(SyncAuthorityScanGeneration scan, string observationId,
        string? objectVersionId, string? membershipId, string evidenceJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        RequireIdentifier(observationId, nameof(observationId));
        RequireJson(evidenceJson, nameof(evidenceJson));
        await WriteAsync(async (connection, transaction) =>
        {
            var current = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM scan_generation WHERE scan_generation_id=$id AND root_binding_id=$root AND generation=$generation AND status='incomplete';",
                cancellationToken, ("$id", scan.ScanGenerationId), ("$root", scan.RootBindingId),
                ("$generation", scan.Generation));
            if (current != 1) throw new InvalidOperationException("Observations may be appended only to the current incomplete scan generation.");
            await ExecuteAsync(connection, transaction,
                "INSERT INTO observation(observation_id,scan_generation_id,root_binding_id,scan_generation,complete_generation,object_version_id,membership_id,evidence_json,observed_utc) VALUES($id,$scan,$root,$generation,0,$version,$membership,$evidence,$utc);",
                cancellationToken, ("$id", observationId), ("$scan", scan.ScanGenerationId),
                ("$root", scan.RootBindingId), ("$generation", scan.Generation), ("$version", objectVersionId),
                ("$membership", membershipId), ("$evidence", evidenceJson), ("$utc", Utc(DateTimeOffset.UtcNow)));
        }, cancellationToken);
    }

    public async Task CompleteScanGenerationAsync(SyncAuthorityScanGeneration scan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        await WriteAsync((connection, transaction) => CompleteScanGenerationCoreAsync(connection, transaction,
            scan, cancellationToken), cancellationToken);
    }

    private static async Task CompleteScanGenerationCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncAuthorityScanGeneration scan, CancellationToken cancellationToken)
    {
            // A compact scan generation owns its rows through the compact cut and interval representation:
            // its candidate must be sealed, its captured parent must still be the latest complete scan and
            // its sealed complete input count must still equal its own resolved row count. A legacy scan
            // generation has no cut at all, so the exact legacy publication below stays in charge for it.
            if (await TryPublishCompactCutAsync(connection, transaction, scan, cancellationToken))
            {
                await RetireAbsentCopyPredecessorsCoreAsync(connection, transaction, scan, cancellationToken);
                return;
            }
            var duplicate = await ScalarStringAsync(connection, transaction, """
                SELECT min(m.relative_path_key) FROM effective_observation o
                JOIN object_version v ON v.object_version_id=o.object_version_id
                JOIN membership m ON m.membership_id=o.membership_id
                WHERE o.scan_generation_id=$scan GROUP BY v.object_id
                HAVING count(DISTINCT m.relative_path_key)>1 LIMIT 1;
                """, cancellationToken, ("$scan", scan.ScanGenerationId));
            if (duplicate is not null)
                throw new InvalidDataException($"The inventory contains a conflicting file identity at {duplicate}. The incomplete snapshot was not published and no action may be planned from it.");
            var count = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM observation WHERE scan_generation_id=$id;", cancellationToken,
                ("$id", scan.ScanGenerationId));
            await ExecuteAsync(connection, transaction,
                "UPDATE scan_generation SET status='complete',completed_utc=$utc,entry_count=$count WHERE scan_generation_id=$id AND status='incomplete';",
                cancellationToken, ("$utc", Utc(DateTimeOffset.UtcNow)), ("$count", count),
                ("$id", scan.ScanGenerationId));
            await ExecuteAsync(connection, transaction,
                "UPDATE observation SET complete_generation=1 WHERE scan_generation_id=$id AND complete_generation=0;",
                cancellationToken, ("$id", scan.ScanGenerationId));
            await RetireAbsentCopyPredecessorsCoreAsync(connection, transaction, scan, cancellationToken);
    }

    public async Task<bool> IsPlanningAuthorityCurrentAsync(SyncAuthorityPlanningAuthority planningAuthority,
        CancellationToken cancellationToken = default)
    {
        ValidatePlanningAuthority(planningAuthority);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await IsPlanningAuthorityCurrentAsync(connection, transaction, planningAuthority,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return current;
    }

    internal async Task<int> CountOperationsOfKindForTestAsync(SyncAuthorityOperationKind kind,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return checked((int)await ScalarLongAsync(connection, null,
            "SELECT count(*) FROM operation WHERE kind=$kind;", cancellationToken,
            ("$kind", SyncAuthorityNames.OperationKind(kind))));
    }

    internal async Task<int> CountOperationsWithStatusForTestAsync(SyncAuthorityOperationKind kind,
        SyncAuthorityOperationStatus status, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return checked((int)await ScalarLongAsync(connection, null,
            "SELECT count(*) FROM operation WHERE kind=$kind AND status=$status;", cancellationToken,
            ("$kind", SyncAuthorityNames.OperationKind(kind)),
            ("$status", SyncAuthorityNames.OperationStatus(status))));
    }

    internal async Task<bool> HasVerifiedCrossPairRemovalAsync(
        SyncAuthoritySynchronizationPoint point, string localRoot, string remoteRoot,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarLongAsync(connection, null,
            """
            SELECT EXISTS(
                SELECT 1 FROM operation o
                WHERE o.kind='cross_pair_move' AND o.status='completed'
                  AND EXISTS(SELECT 1 FROM operation_member m WHERE m.operation_id=o.operation_id
                    AND m.role='source' AND m.pair_id=$pair AND m.object_id=$object
                    AND m.root_binding_id=$localRoot AND m.membership_id=$localMembership
                    AND m.object_version_id=$localVersion AND m.expected_path_key=$localPath)
                  AND EXISTS(SELECT 1 FROM operation_member m WHERE m.operation_id=o.operation_id
                    AND m.role='source' AND m.pair_id=$pair AND m.object_id=$object
                    AND m.root_binding_id=$remoteRoot AND m.membership_id=$remoteMembership
                    AND m.object_version_id=$remoteVersion AND m.expected_path_key=$remotePath)
                  AND EXISTS(SELECT 1 FROM operation_attempt a JOIN effect_receipt r ON r.attempt_id=a.attempt_id
                    WHERE a.operation_id=o.operation_id AND a.status='verified_success'
                      AND r.certainty='known_success'));
            """, cancellationToken, ("$pair", point.PairId), ("$object", point.ObjectId),
            ("$localRoot", localRoot), ("$remoteRoot", remoteRoot),
            ("$localMembership", point.LocalMembershipId), ("$remoteMembership", point.DropboxMembershipId),
            ("$localVersion", point.LocalObjectVersionId), ("$remoteVersion", point.DropboxObjectVersionId),
            ("$localPath", point.LocalRelativePath), ("$remotePath", point.DropboxRelativePath)) == 1;
    }

    internal async Task<int> SupersedeStaleUnissuedCrossPairReservationsAsync(
        CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        await using (var connection = await OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            // Fresh unissued groups are intentionally absent from the recovery queue.
            // They still reserve paths, so a new observation must retire stale ones
            // before reserving their successors. This never dispatches an old choice.
            command.CommandText = "SELECT operation_id FROM operation " +
                "WHERE kind='cross_pair_move' AND status='ready' ORDER BY operation_id;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetString(0));
        }
        var count = 0;
        foreach (var operation in await LoadOperationsAsync(ids, cancellationToken: cancellationToken))
            if (operation.PlanningAuthorities.Count > 0 &&
                await SupersedeReadyOperationIfPlanningAuthorityChangedAsync(operation.OperationId,
                    operation.PlanningAuthorities, cancellationToken))
                count++;
        return count;
    }

    public async Task<bool> SupersedeReadyOperationIfPlanningAuthorityChangedAsync(string operationId,
        IReadOnlyList<SyncAuthorityPlanningAuthority> planningAuthorities,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(operationId, nameof(operationId));
        ArgumentNullException.ThrowIfNull(planningAuthorities);
        foreach (var item in planningAuthorities) ValidatePlanningAuthority(item);
        var superseded = false;
        await WriteAsync(async (connection, transaction) =>
        {
            var current = true;
            foreach (var item in planningAuthorities)
                current &= await IsPlanningAuthorityCurrentAsync(connection, transaction, item,
                    cancellationToken);
            if (current) return;
            var eligible = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM operation o
                WHERE o.operation_id=$id AND o.status='ready' AND o.current_lease_id IS NULL
                  AND NOT EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id)
                  AND NOT EXISTS(SELECT 1 FROM resource_allocation r
                                 WHERE r.operation_id=o.operation_id AND r.status<>'authorized');
                """, cancellationToken, ("$id", operationId));
            if (eligible != 1) return;
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status='superseded',terminal_utc=$utc WHERE operation_id=$id AND status='ready';",
                cancellationToken, ("$id", operationId), ("$utc", Utc(DateTimeOffset.UtcNow)));
            superseded = true;
        }, cancellationToken);
        return superseded;
    }

    private async Task<bool> IsPlanningAuthorityCurrentAsync(SqliteConnection connection,
        SqliteTransaction? transaction, SyncAuthorityPlanningAuthority planningAuthority,
        CancellationToken cancellationToken)
    {
        ValidatePlanningAuthority(planningAuthority);
        if (!await IsPlanningAuthorityBaseCurrentAsync(connection, transaction, planningAuthority,
                cancellationToken))
            return false;
        var pairState = await LoadPairPlanningStateAsync(connection, transaction, planningAuthority.PairId,
            cancellationToken);
        var excluded = await LoadCompleteExcludedPathsAsync(connection, transaction,
            planningAuthority.LocalScanGenerationId, planningAuthority.DropboxScanGenerationId,
            cancellationToken);
        if (!string.Equals(BuildPlanningStateFingerprint(planningAuthority.PairId,
                planningAuthority.ConfigurationRevision, planningAuthority.LocalRootBindingId,
                planningAuthority.DropboxRootBindingId, pairState, excluded,
                planningAuthority.LocalScanGenerationId, planningAuthority.DropboxScanGenerationId),
                planningAuthority.PlanningStateFingerprint, StringComparison.Ordinal))
            return false;
        var connectionId = await LoadPairPlanningConnectionIdAsync(connection, transaction,
            planningAuthority.PairId, cancellationToken);
        if (!string.Equals(connectionId, planningAuthority.ConnectionId, StringComparison.Ordinal))
            return false;
        if (!string.Equals(await LoadRootAuthorityFingerprintAsync(connection, transaction,
                    planningAuthority.PairId, planningAuthority.LocalRootBindingId,
                    planningAuthority.DropboxRootBindingId, cancellationToken),
                planningAuthority.RootAuthorityFingerprint, StringComparison.Ordinal))
            return false;
        // Participant-wide authorities protect correlation/deletion decisions across the complete set. An
        // object-scoped operation instead binds its own point so a completed sibling does not invalidate the
        // remaining operations from the same immutable observation cut.
        if (planningAuthority.ObjectId is null)
        {
            var synchronizationPoints = await LoadLatestSynchronizationPointsAsync(connection, transaction,
                planningAuthority.PairId, cancellationToken);
            var synchronizationPointSetFingerprint = BuildSynchronizationPointSetFingerprint(synchronizationPoints);
            if (!string.Equals(synchronizationPointSetFingerprint,
                    planningAuthority.SynchronizationPointSetFingerprint, StringComparison.Ordinal) ||
                planningAuthority.SynchronizationPointId is not null)
                return false;
        }
        else
        {
            // The existing UNIQUE(pair_id,object_id,authority_generation) index
            // answers this exact authority read without loading/hash-sorting every
            // sibling for every file. Do not cache mutable synchronization points.
            var synchronizationPointId = await ScalarStringAsync(connection, transaction,
                """
                SELECT synchronization_point_id FROM current_synchronization_point
                WHERE pair_id=$pair AND object_id=$object
                ORDER BY authority_generation DESC LIMIT 1;
                """, cancellationToken, ("$pair", planningAuthority.PairId),
                ("$object", planningAuthority.ObjectId));
            if (!string.Equals(synchronizationPointId, planningAuthority.SynchronizationPointId,
                    StringComparison.Ordinal))
                return false;
        }
        var contentCacheKey = PlanningContentCacheKey(planningAuthority.PairId,
            planningAuthority.ConnectionId, planningAuthority.LocalRootBindingId,
            planningAuthority.DropboxRootBindingId, planningAuthority.LocalScanGenerationId,
            planningAuthority.DropboxScanGenerationId);
        if (!_planningContentFingerprints.TryGetValue(contentCacheKey, out var observationContentFingerprint))
        {
            var local = await LoadCompleteObservationAsync(connection, transaction, planningAuthority.PairId,
                "local", planningAuthority.LocalScanGenerationId, cancellationToken);
            var dropbox = await LoadCompleteObservationAsync(connection, transaction, planningAuthority.PairId,
                "dropbox", planningAuthority.DropboxScanGenerationId, cancellationToken);
            var localUncertain = await LoadCompleteUncertainPathsAsync(connection, transaction,
                planningAuthority.PairId, "local", planningAuthority.LocalScanGenerationId, cancellationToken);
            var dropboxUncertain = await LoadCompleteUncertainPathsAsync(connection, transaction,
                planningAuthority.PairId, "dropbox", planningAuthority.DropboxScanGenerationId,
                cancellationToken);
            observationContentFingerprint = BuildObservationContentFingerprint(planningAuthority.PairId,
                planningAuthority.ConfigurationRevision, planningAuthority.ConnectionId,
                planningAuthority.LocalRootBindingId, planningAuthority.DropboxRootBindingId, local, dropbox,
                localUncertain, dropboxUncertain, excluded);
            _planningContentFingerprints.TryAdd(contentCacheKey, observationContentFingerprint);
        }
        if (!string.Equals(observationContentFingerprint, planningAuthority.ObservationContentFingerprint,
                StringComparison.Ordinal))
            return false;
        return true;
    }

    private static async Task<bool> IsPlanningAuthorityBaseCurrentAsync(SqliteConnection connection,
        SqliteTransaction? transaction, SyncAuthorityPlanningAuthority planningAuthority,
        CancellationToken cancellationToken)
    {
        var exact = await ScalarLongAsync(connection, transaction,
            """
            SELECT CASE WHEN
              EXISTS(SELECT 1 FROM sync_pair
                     WHERE pair_id=$pair AND configuration_revision=$revision AND state<>'retired')
              AND EXISTS(SELECT 1 FROM root_binding
                         WHERE root_binding_id=$localRoot AND pair_id=$pair AND side='local' AND state='active')
              AND EXISTS(SELECT 1 FROM root_binding
                         WHERE root_binding_id=$dropboxRoot AND pair_id=$pair AND side='dropbox' AND state='active')
              AND $localScan=(SELECT scan_generation_id FROM scan_generation
                              WHERE pair_id=$pair AND side='local' AND status='complete'
                              ORDER BY generation DESC LIMIT 1)
              AND $dropboxScan=(SELECT scan_generation_id FROM scan_generation
                                WHERE pair_id=$pair AND side='dropbox' AND status='complete'
                                ORDER BY generation DESC LIMIT 1)
              AND EXISTS(SELECT 1 FROM scan_generation
                         WHERE scan_generation_id=$localScan AND root_binding_id=$localRoot
                           AND pair_id=$pair AND side='local' AND status='complete')
              AND EXISTS(SELECT 1 FROM scan_generation
                         WHERE scan_generation_id=$dropboxScan AND root_binding_id=$dropboxRoot
                           AND pair_id=$pair AND side='dropbox' AND status='complete')
              THEN 1 ELSE 0 END;
            """, cancellationToken, ("$pair", planningAuthority.PairId),
            ("$revision", planningAuthority.ConfigurationRevision),
            ("$localRoot", planningAuthority.LocalRootBindingId),
            ("$dropboxRoot", planningAuthority.DropboxRootBindingId),
            ("$localScan", planningAuthority.LocalScanGenerationId),
            ("$dropboxScan", planningAuthority.DropboxScanGenerationId));
        return exact == 1;
    }

    public async Task AppendObservationEntriesAsync(SyncAuthorityScanGeneration scan,
        IReadOnlyList<SyncAuthorityObservationInput> entries,
        CancellationToken cancellationToken = default,
        Func<SyncComparisonProgress, Task>? onProgress = null, string endpointLabel = "selected endpoint") =>
        await AppendObservationEntriesCoreAsync(scan, entries, null, cancellationToken, onProgress, endpointLabel, false);

    // Only the existing Host-backed observer opts into synchronous ordered
    // admission followed by awaited delivery. Public arbitrary sinks stay serial.
    internal Task AppendObservationEntriesAsync(SyncAuthorityScanGeneration scan,
        IReadOnlyList<SyncAuthorityObservationInput> entries, CancellationToken cancellationToken,
        Func<SyncComparisonProgress, Task>? onProgress, string endpointLabel,
        bool concurrentProgressRetention) =>
        AppendObservationEntriesCoreAsync(scan, entries, null, cancellationToken, onProgress,
            endpointLabel, concurrentProgressRetention);

    // Only the explicit retained-copy recovery command uses this route. Its
    // complete fresh inventory does not grant another transfer or rewrite a
    // synchronization point; the transaction verifies each historical proof.
    internal Task AppendIdentityRecoveryObservationEntriesAsync(SyncAuthorityScanGeneration scan,
        IReadOnlyList<SyncAuthorityObservationInput> entries,
        IReadOnlyList<SyncCompletedCopyMove> recovery, CancellationToken cancellationToken,
        Func<SyncComparisonProgress, Task>? onProgress, string endpointLabel,
        bool concurrentProgressRetention) =>
        AppendObservationEntriesCoreAsync(scan, entries, null, cancellationToken, onProgress,
            endpointLabel, concurrentProgressRetention, recovery);

    internal async Task AppendBoundObservationEntriesAsync(SyncAuthorityScanGeneration scan,
        IReadOnlyList<SyncAuthorityObservationInput> entries,
        SyncAuthorityObservationBindingAuthority bindingAuthority,
        CancellationToken cancellationToken = default,
        Func<SyncComparisonProgress, Task>? onProgress = null, string endpointLabel = "selected endpoint",
        bool concurrentProgressRetention = false) =>
        await AppendObservationEntriesCoreAsync(scan, entries, bindingAuthority, cancellationToken, onProgress,
            endpointLabel, concurrentProgressRetention);

    private async Task AppendObservationEntriesCoreAsync(SyncAuthorityScanGeneration scan,
        IReadOnlyList<SyncAuthorityObservationInput> entries,
        SyncAuthorityObservationBindingAuthority? bindingAuthority,
        CancellationToken cancellationToken, Func<SyncComparisonProgress, Task>? onProgress, string endpointLabel,
        bool concurrentProgressRetention, IReadOnlyList<SyncCompletedCopyMove>? identityRecovery = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry.RelativePath);
            if (entry.ObjectKind is not ("file" or "directory"))
                throw new ArgumentOutOfRangeException(nameof(entries));
            RequireJson(entry.IdentityJson, nameof(entries));
            RequireJson(entry.EvidenceJson, nameof(entries));
            if (entry.ContentHash is not null) RequireHash(entry.ContentHash, nameof(entries));
            if (entry.Size < 0) throw new ArgumentOutOfRangeException(nameof(entries));
            if (entry.BoundObjectId is not null) RequireIdentifier(entry.BoundObjectId, nameof(entries));
        }
        if (bindingAuthority is null && entries.Any(item => item.BoundObjectId is not null))
            throw new InvalidOperationException("A logical-object observation binding requires fenced operation authority.");
        var started = DateTimeOffset.UtcNow;
        Dictionary<string, string> identities = null!;
        if (onProgress is not null)
            await onProgress(new SyncComparisonProgress("Resolving File Identities", scan.PairId,
                0, entries.Count, started, started,
                $"Loading the retained {endpointLabel} identity map and checking {entries.Count:N0} observed files/folders before saving their metadata.", "records resolved"));
        var generations = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await WriteAsync(async (connection, transaction) =>
        {
            if (bindingAuthority is not null)
                await ValidateBoundObservationAuthorityAsync(connection, transaction, scan, entries,
                    bindingAuthority, cancellationToken);
            using var commands = new ObservationCommands(connection, transaction);
            var current = await commands.ScalarLongAsync(
                "SELECT count(*) FROM scan_generation WHERE scan_generation_id=$id AND root_binding_id=$root AND generation=$generation AND status='incomplete';",
                cancellationToken, ("$id", scan.ScanGenerationId), ("$root", scan.RootBindingId),
                ("$generation", scan.Generation));
            if (current != 1)
                throw new InvalidOperationException("Observation entries require the current incomplete scan generation.");
            // The identity kind the scan root itself retains decides the resolution. A retained cloud root
            // resolves explicit cloud identities only, by provider, account, namespace and file identity, and
            // a legacy root keeps its exact existing physical/cloud resolution. A missing, mixed or spoofed
            // identity is refused inside the transaction, so nothing is written for it.
            var cloudScanRoot = await LoadCloudScanRootAsync(connection, transaction, scan.RootBindingId,
                scan.PairId, scan.Side, cancellationToken);
            identities = await ResolveObservationBatchAsync(connection, transaction, scan, entries,
                cloudScanRoot, cancellationToken, identityRecovery);
            await using var generationCommand = connection.CreateCommand();
            generationCommand.Transaction = transaction;
            generationCommand.CommandText = "SELECT relative_path_key,max(generation) FROM membership WHERE root_binding_id=$root GROUP BY relative_path_key;";
            generationCommand.Parameters.AddWithValue("$root", scan.RootBindingId);
            await using var reader = await generationCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                generations[reader.GetString(0)] = reader.GetInt64(1);
        }, cancellationToken);

        var saved = 0;
        // Trusted Host publication can amortize checkout, schema validation and
        // FULL commits over a larger bounded metadata prefix. This does not
        // enlarge the retained-log delivery window or publish an incomplete scan.
        // Arbitrary callbacks retain their original 128-row transaction boundary.
        // Fenced observations keep the existing revalidation interval. No-callback
        // callers also retain their original boundaries; this correction targets
        // measured Host publication with its lossless progress sink only.
        var metadataBatchSize = concurrentProgressRetention && bindingAuthority is null && onProgress is not null ? 1024 : 128;
        foreach (var batch in entries.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).Chunk(metadataBatchSize))
        {
            if (onProgress is not null)
                await onProgress(new SyncComparisonProgress("Saving Folder Inventories", batch[0].RelativePath,
                    saved, entries.Count, started, DateTimeOffset.UtcNow,
                    $"Writing the next {batch.Length:N0} {endpointLabel} metadata records; {saved:N0} records are already committed. No file transfer is running.", "records saved"));
            var databaseStarted = Stopwatch.GetTimestamp();
            await WriteAsync(async (connection, transaction) =>
            {
                if (bindingAuthority is not null)
                    await RequireFenceAsync(connection, transaction, bindingAuthority.OperationId,
                        bindingAuthority.Lease, cancellationToken);
                using var commands = new ObservationCommands(connection, transaction);
                if (await commands.ScalarLongAsync("SELECT count(*) FROM scan_generation WHERE scan_generation_id=$id AND status='incomplete';",
                        cancellationToken, ("$id", scan.ScanGenerationId)) != 1)
                    throw new InvalidOperationException("The inventory is no longer open for metadata writes.");
                foreach (var entry in batch)
            {
                var path = PilotPath.NormalizeRelative(entry.RelativePath);
                var objectId = identities[path];
                var versionId = AuthorityDeterministicId("version-" + scan.Side, objectId,
                    entry.IdentityJson, entry.ContentHash ?? "none",
                    (entry.Size ?? 0).ToString(CultureInfo.InvariantCulture));
                var membershipId = AuthorityDeterministicId("membership-" + scan.Side,
                    scan.ScanGenerationId, path, objectId);
                var generation = checked(generations.GetValueOrDefault(path) + 1);
                generations[path] = generation;
                var now = Utc(DateTimeOffset.UtcNow);
                var insertedObject = await commands.ExecuteAsync(
                    "INSERT OR IGNORE INTO logical_object(object_id,object_kind,created_utc) VALUES($id,$kind,$utc);",
                    cancellationToken, ("$id", objectId), ("$kind", entry.ObjectKind), ("$utc", now));
                if (insertedObject != 1)
                {
                    var collisionStarted = TimingStart();
                    if (await commands.ScalarLongAsync(
                            "SELECT count(*) FROM logical_object WHERE object_id=$id AND object_kind=$kind;",
                            cancellationToken, ("$id", objectId), ("$kind", entry.ObjectKind)) != 1)
                        throw new InvalidOperationException("The deterministic observed object collides with a different kind.");
                    ReportTiming("observation-object-collision-check", collisionStarted);
                }
                var insertedVersion = await commands.ExecuteAsync(
                    "INSERT OR IGNORE INTO object_version(object_version_id,object_id,side,identity_json,content_hash,size_bytes,observed_utc) VALUES($id,$object,$side,$identity,$hash,$size,$utc);",
                    cancellationToken, ("$id", versionId), ("$object", objectId), ("$side", scan.Side),
                    ("$identity", entry.IdentityJson), ("$hash", entry.ContentHash), ("$size", entry.Size),
                    ("$utc", now));
                if (insertedVersion != 1)
                {
                    // An ignored insert can be the secondary unique identity key,
                    // not only this deterministic ID. Preserve every evidence field.
                    var collisionStarted = TimingStart();
                    if (await commands.ScalarLongAsync(
                            "SELECT count(*) FROM object_version WHERE object_version_id=$id AND object_id=$object AND side=$side AND identity_json=$identity AND content_hash IS $hash AND size_bytes IS $size;",
                            cancellationToken, ("$id", versionId), ("$object", objectId), ("$side", scan.Side),
                            ("$identity", entry.IdentityJson), ("$hash", entry.ContentHash),
                            ("$size", entry.Size)) != 1)
                        throw new InvalidOperationException("The deterministic observed version collides with different evidence.");
                    ReportTiming("observation-version-collision-check", collisionStarted);
                }
                await commands.ExecuteAsync(
                    "INSERT INTO membership(membership_id,object_id,root_binding_id,relative_path_key,generation,state,observed_utc) VALUES($id,$object,$root,$path,$generation,'active',$utc);",
                    cancellationToken, ("$id", membershipId), ("$object", objectId),
                    ("$root", scan.RootBindingId), ("$path", path), ("$generation", generation),
                    ("$utc", now));
                await commands.ExecuteAsync(
                    "INSERT INTO observation(observation_id,scan_generation_id,root_binding_id,scan_generation,complete_generation,object_version_id,membership_id,evidence_json,observed_utc) VALUES($id,$scan,$root,$generation,0,$version,$membership,$evidence,$utc);",
                    cancellationToken,
                    ("$id", AuthorityDeterministicId("observation", scan.ScanGenerationId, membershipId)),
                    ("$scan", scan.ScanGenerationId), ("$root", scan.RootBindingId),
                    ("$generation", scan.Generation), ("$version", versionId),
                    ("$membership", membershipId), ("$evidence", entry.EvidenceJson), ("$utc", now));
            }
            }, cancellationToken);
            var databaseElapsed = Stopwatch.GetElapsedTime(databaseStarted);
            // Callbacks may themselves retain activity in this database. They run AFTER the
            // batch commit and writer release, never from inside a transaction or DB lock.
            // A trusted Host admits this bounded prefix in order, then every
            // delivery is drained before the next transaction or publication.
            var feedbackStarted = Stopwatch.GetTimestamp();
            foreach (var feedbackBatch in batch.Chunk(128))
            {
                List<Task>? deliveries = concurrentProgressRetention && onProgress is not null ? [] : null;
                Exception? feedbackFailure = null;
                try
                {
                    foreach (var entry in feedbackBatch)
                    {
                        saved++;
                        if (onProgress is null) continue;
                        var delivery = onProgress(new SyncComparisonProgress("Saving Folder Inventories", entry.RelativePath,
                            saved, entries.Count, started, DateTimeOffset.UtcNow,
                            $"Saved {endpointLabel} metadata for this {entry.ObjectKind}. The snapshot is not published until the entire inventory validates.", "records saved"));
                        if (deliveries is not null) deliveries.Add(delivery);
                        else await delivery.ConfigureAwait(false);
                    }
                }
                catch (Exception error) { feedbackFailure = error; }
                if (deliveries is not null)
                    try { await Task.WhenAll(deliveries).ConfigureAwait(false); }
                    catch (Exception error) { feedbackFailure ??= error; }
                if (feedbackFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(feedbackFailure).Throw();
            }
            var feedbackElapsed = Stopwatch.GetElapsedTime(feedbackStarted);
            if (onProgress is not null)
                await onProgress(new SyncComparisonProgress("Saving Folder Inventories", batch[^1].RelativePath,
                    saved, entries.Count, started, DateTimeOffset.UtcNow,
                    $"Committed {batch.Length:N0} {endpointLabel} records in {databaseElapsed.TotalSeconds:F3}s including database admission and durable commit; retained and delivered every row's log in {feedbackElapsed.TotalSeconds:F3}s. The complete inventory is not yet published.", "records saved"));
        }
    }

    // Reuse the same prepared statements for every entry in this one transaction. Evidence,
    // generation and collision checks are unchanged; no per-file SQL parsing or command allocation.
    private sealed class ObservationCommands(SqliteConnection connection, SqliteTransaction transaction) : IDisposable
    {
        private readonly Dictionary<string, SqliteCommand> _commands = new(StringComparer.Ordinal);
        private SqliteCommand Bind(string sql, (string Name, object? Value)[] values)
        {
            if (!_commands.TryGetValue(sql, out var command))
            {
                command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
                foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
                command.Prepare(); _commands.Add(sql, command);
            }
            else foreach (var value in values) command.Parameters[value.Name].Value = value.Value ?? DBNull.Value;
            return command;
        }
        internal Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] values)
            => Bind(sql, values).ExecuteNonQueryAsync(cancellationToken);
        internal async Task<long> ScalarLongAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] values)
            => Convert.ToInt64(await Bind(sql, values).ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        internal async Task<string?> ScalarStringAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] values)
        {
            var value = await Bind(sql, values).ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        public void Dispose() { foreach (var command in _commands.Values) command.Dispose(); }
    }

    private static async Task ValidateBoundObservationAuthorityAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncAuthorityScanGeneration scan,
        IReadOnlyList<SyncAuthorityObservationInput> entries,
        SyncAuthorityObservationBindingAuthority bindingAuthority, CancellationToken cancellationToken)
    {
        RequireIdentifier(bindingAuthority.OperationId, nameof(bindingAuthority.OperationId));
        ArgumentNullException.ThrowIfNull(bindingAuthority.Lease);
        ArgumentNullException.ThrowIfNull(bindingAuthority.Bindings);
        await RequireFenceAsync(connection, transaction, bindingAuthority.OperationId,
            bindingAuthority.Lease, cancellationToken);

        await using var operationCommand = connection.CreateCommand();
        operationCommand.Transaction = transaction;
        operationCommand.CommandText =
            "SELECT kind,status,preconditions_json FROM operation WHERE operation_id=$id;";
        operationCommand.Parameters.AddWithValue("$id", bindingAuthority.OperationId);
        await using var operationReader = await operationCommand.ExecuteReaderAsync(cancellationToken);
        if (!await operationReader.ReadAsync(cancellationToken))
            throw new InvalidDataException("The binding operation is not retained.");
        var operationKind = operationReader.GetString(0);
        var operationStatus = operationReader.GetString(1);
        var preconditionsJson = operationReader.GetString(2);
        await operationReader.DisposeAsync();
        if (operationKind != "cross_pair_move" || operationStatus is not ("effect_issuing" or
                "effect_unknown" or "effect_observed" or "reconciling" or "verifying"))
            throw new InvalidOperationException(
                "Logical-object observation binding is limited to an active cross-pair effect or recovery.");

        using var preconditions = JsonDocument.Parse(preconditionsJson);
        var root = preconditions.RootElement;
        var sourcePairId = RequiredJsonString(root, "sourcePairId");
        var destinationPairId = RequiredJsonString(root, "destinationPairId");
        var sourcePath = PilotPath.NormalizeRelative(RequiredJsonString(root, "sourcePath"));
        var destinationPath = PilotPath.NormalizeRelative(RequiredJsonString(root, "destinationPath"));
        if (!string.Equals(scan.PairId, destinationPairId, StringComparison.Ordinal))
            throw new InvalidOperationException("The bound observation is not for the retained destination pair.");
        var destinationAuthority = LoadPlanningAuthorities(preconditionsJson).SingleOrDefault(item =>
            string.Equals(item.PairId, destinationPairId, StringComparison.Ordinal))
            ?? throw new InvalidDataException("The cross-pair operation has no destination planning authority.");
        var expectedRoot = scan.Side == "local"
            ? destinationAuthority.LocalRootBindingId
            : destinationAuthority.DropboxRootBindingId;
        if (!string.Equals(scan.RootBindingId, expectedRoot, StringComparison.Ordinal))
            throw new InvalidOperationException("The bound observation uses a different destination root authority.");

        var sideBindings = bindingAuthority.Bindings.Where(item => item.Side == scan.Side).ToArray();
        if (sideBindings.Length == 0 || sideBindings.Select(item => PilotPath.NormalizeRelative(item.RelativePath))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != sideBindings.Length)
            throw new InvalidDataException("The bound observation must contain one unique binding per moved path.");
        var boundSubtreePaths = sideBindings.Select(item => PilotPath.NormalizeRelative(item.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observedSubtreePaths = entries.Select(item => PilotPath.NormalizeRelative(item.RelativePath))
            .Where(path => IsSameOrDescendantPath(path, destinationPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!boundSubtreePaths.SetEquals(observedSubtreePaths))
            throw new IOException(
                "The complete moved destination subtree contains an unbound or missing path; no synchronization point was accepted.");
        var entriesByPath = entries.ToDictionary(item => PilotPath.NormalizeRelative(item.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        foreach (var binding in sideBindings)
        {
            RequireIdentifier(binding.ObjectId, nameof(binding.ObjectId));
            RequireIdentifier(binding.SourceObjectVersionId, nameof(binding.SourceObjectVersionId));
            RequireJson(binding.SourceIdentityJson, nameof(binding.SourceIdentityJson));
            if (binding.ObjectKind is not ("file" or "directory"))
                throw new InvalidDataException("A bound observation has an unsupported object kind.");
            if (binding.ContentHash is not null) RequireHash(binding.ContentHash, nameof(binding.ContentHash));
            if (binding.Size < 0) throw new InvalidDataException("A bound observation has a negative size.");
            var normalizedSource = PilotPath.NormalizeRelative(binding.SourceRelativePath);
            var normalizedDestination = PilotPath.NormalizeRelative(binding.RelativePath);
            if (!string.Equals(RebaseCrossPairPath(normalizedSource, sourcePath, destinationPath),
                    normalizedDestination, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A bound destination path is not the retained source-path successor.");
            if (!entriesByPath.TryGetValue(normalizedDestination, out var observed) ||
                !string.Equals(observed.BoundObjectId, binding.ObjectId, StringComparison.Ordinal) ||
                observed.ObjectKind != binding.ObjectKind || observed.Size != binding.Size ||
                !string.Equals(observed.ContentHash, binding.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The bound destination observation differs from its retained source object.");
            if (binding.RequirePhysicalIdentity &&
                !SamePhysicalIdentity(scan.Side, binding.SourceIdentityJson, observed.IdentityJson))
                throw new IOException("The moved destination did not preserve its required physical identity.");

            var exactVersion = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM object_version v
                JOIN logical_object o ON o.object_id=v.object_id
                WHERE v.object_version_id=$version AND v.object_id=$object AND v.side=$side
                  AND v.identity_json=$identity AND v.content_hash IS $hash AND v.size_bytes IS $size
                  AND o.object_kind=$kind;
                """, cancellationToken, ("$version", binding.SourceObjectVersionId),
                ("$object", binding.ObjectId), ("$side", binding.Side),
                ("$identity", binding.SourceIdentityJson), ("$hash", binding.ContentHash),
                ("$size", binding.Size), ("$kind", binding.ObjectKind));
            if (exactVersion != 1)
                throw new InvalidDataException("A bound observation source version is not retained exactly.");
            var currentSourcePoint = await ScalarLongAsync(connection, transaction,
                binding.Side == "local"
                    ? """
                      SELECT count(*) FROM current_synchronization_point p
                      JOIN membership m ON m.membership_id=p.local_membership_id
                      WHERE p.pair_id=$pair AND p.object_id=$object
                        AND p.authority_generation=(SELECT max(x.authority_generation)
                            FROM current_synchronization_point x WHERE x.pair_id=p.pair_id AND x.object_id=p.object_id)
                        AND p.local_object_version_id=$version AND m.relative_path_key=$path;
                      """
                    : """
                      SELECT count(*) FROM current_synchronization_point p
                      JOIN membership m ON m.membership_id=p.dropbox_membership_id
                      WHERE p.pair_id=$pair AND p.object_id=$object
                        AND p.authority_generation=(SELECT max(x.authority_generation)
                            FROM current_synchronization_point x WHERE x.pair_id=p.pair_id AND x.object_id=p.object_id)
                        AND p.dropbox_object_version_id=$version AND m.relative_path_key=$path;
                      """, cancellationToken, ("$pair", sourcePairId), ("$object", binding.ObjectId),
                ("$version", binding.SourceObjectVersionId), ("$path", normalizedSource));
            if (currentSourcePoint != 1)
                throw new InvalidDataException("A bound observation is not backed by the latest source synchronization point.");
        }
        if (entries.Any(item => item.BoundObjectId is not null && sideBindings.All(binding =>
                !string.Equals(PilotPath.NormalizeRelative(binding.RelativePath),
                    PilotPath.NormalizeRelative(item.RelativePath), StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("An observed logical-object binding is outside the retained move set.");
    }

    private static string RequiredJsonString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new InvalidDataException($"The retained operation has no {name} authority.");

    private static string RebaseCrossPairPath(string path, string sourceRoot, string destinationRoot)
    {
        if (string.Equals(path, sourceRoot, StringComparison.OrdinalIgnoreCase)) return destinationRoot;
        var prefix = sourceRoot + "/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A bound source path is outside the retained moved subtree.");
        return destinationRoot + "/" + path[prefix.Length..];
    }

    private static bool SamePhysicalIdentity(string side, string expectedJson, string actualJson)
    {
        using var expected = JsonDocument.Parse(expectedJson);
        using var actual = JsonDocument.Parse(actualJson);
        var expectedFile = RequiredJsonString(expected.RootElement, "fileId");
        var actualFile = RequiredJsonString(actual.RootElement, "fileId");
        if (!string.Equals(expectedFile, actualFile, StringComparison.OrdinalIgnoreCase)) return false;
        if (side == "dropbox") return true;
        return string.Equals(RequiredJsonString(expected.RootElement, "volumeSerial"),
            RequiredJsonString(actual.RootElement, "volumeSerial"), StringComparison.OrdinalIgnoreCase);
    }

    public async Task AppendObservationEvidenceBatchAsync(SyncAuthorityScanGeneration scan,
        IReadOnlyList<(string ObservationId, string EvidenceJson)> evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(evidence);
        foreach (var item in evidence)
        {
            RequireIdentifier(item.ObservationId, nameof(evidence));
            RequireJson(item.EvidenceJson, nameof(evidence));
        }
        await WriteAsync(async (connection, transaction) =>
        {
            if (await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM scan_generation WHERE scan_generation_id=$id AND root_binding_id=$root AND generation=$generation AND status='incomplete';",
                    cancellationToken, ("$id", scan.ScanGenerationId), ("$root", scan.RootBindingId),
                    ("$generation", scan.Generation)) != 1)
                throw new InvalidOperationException("Observation evidence requires the current incomplete scan generation.");
            foreach (var item in evidence)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO observation(observation_id,scan_generation_id,root_binding_id,scan_generation,complete_generation,object_version_id,membership_id,evidence_json,observed_utc) VALUES($id,$scan,$root,$generation,0,NULL,NULL,$evidence,$utc);",
                    cancellationToken, ("$id", item.ObservationId), ("$scan", scan.ScanGenerationId),
                    ("$root", scan.RootBindingId), ("$generation", scan.Generation),
                    ("$evidence", item.EvidenceJson), ("$utc", Utc(DateTimeOffset.UtcNow)));
                using var document = JsonDocument.Parse(item.EvidenceJson);
                if (!document.RootElement.TryGetProperty("state", out var state) || state.GetString() != "cloud_native" ||
                    !document.RootElement.TryGetProperty("fileId", out var id) || id.ValueKind != JsonValueKind.String ||
                    string.IsNullOrEmpty(id.GetString())) continue;
                // A known cloud object may have been renamed to an unrepresentable name. Retain
                // uncertainty for its earlier paths too: never interpret that rename as deletion.
                var priorPaths = new List<string>();
                await using (var prior = connection.CreateCommand())
                {
                    prior.Transaction = transaction;
                    prior.CommandText = """
                        SELECT DISTINCT m.relative_path_key FROM effective_observation o
                        JOIN membership m ON m.membership_id=o.membership_id
                        JOIN object_version v ON v.object_version_id=o.object_version_id
                        WHERE o.root_binding_id=$root AND o.complete_generation=1
                          AND json_extract(v.identity_json,'$.fileId')=$file;
                        """;
                    prior.Parameters.AddWithValue("$root", scan.RootBindingId);
                    prior.Parameters.AddWithValue("$file", id.GetString()!);
                    await using var reader = await prior.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken)) priorPaths.Add(reader.GetString(0));
                }
                foreach (var path in priorPaths)
                {
                    var aliasId = "observation-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        item.ObservationId + "\n" + path))).ToLowerInvariant();
                    await ExecuteAsync(connection, transaction,
                        """
                        INSERT INTO observation(observation_id,scan_generation_id,root_binding_id,scan_generation,
                            complete_generation,object_version_id,membership_id,evidence_json,observed_utc)
                        VALUES($id,$scan,$root,$generation,0,NULL,NULL,
                            json_set($evidence,'$.blockedCloudPath',json_extract($evidence,'$.path'),'$.path',$path),$utc);
                        """, cancellationToken, ("$id", aliasId), ("$scan", scan.ScanGenerationId),
                        ("$root", scan.RootBindingId), ("$generation", scan.Generation),
                        ("$evidence", item.EvidenceJson), ("$path", path), ("$utc", Utc(DateTimeOffset.UtcNow)));
                }
            }
        }, cancellationToken);
    }

    public async Task<bool> HasTwoStableCompleteObservationsAsync(string pairId, string side,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var generationIds = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT scan_generation_id FROM scan_generation WHERE pair_id=$pair AND side=$side AND status='complete' ORDER BY generation DESC LIMIT 2;";
            command.Parameters.AddWithValue("$pair", pairId);
            command.Parameters.AddWithValue("$side", side);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) generationIds.Add(reader.GetString(0));
        }
        if (generationIds.Count != 2) return false;
        var first = await ObservationDigestAsync(connection, generationIds[0], cancellationToken);
        var second = await ObservationDigestAsync(connection, generationIds[1], cancellationToken);
        return CryptographicOperations.FixedTimeEquals(first, second);
    }

    public async Task<bool> HasCompleteObservationAsync(string pairId, string side,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarLongAsync(connection, null,
            "SELECT count(*) FROM scan_generation WHERE pair_id=$pair AND side=$side AND status='complete';",
            cancellationToken, ("$pair", pairId), ("$side", side)) > 0;
    }

    public async Task<IReadOnlyList<SyncAuthorityObservedEntry>> LoadLatestCompleteObservationAsync(
        string pairId, string side, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.relative_path_key,m.object_id,o.object_version_id,m.membership_id,
                   m.root_binding_id,g.scan_generation_id,m.generation,
                   v.content_hash,v.size_bytes,o.evidence_json,lo.object_kind
            FROM scan_generation g
            JOIN effective_observation o ON o.scan_generation_id=g.scan_generation_id
            JOIN membership m ON m.membership_id=o.membership_id
            JOIN object_version v ON v.object_version_id=o.object_version_id
            JOIN logical_object lo ON lo.object_id=m.object_id
            WHERE g.scan_generation_id=(
                SELECT scan_generation_id FROM scan_generation
                WHERE pair_id=$pair AND side=$side AND status='complete'
                ORDER BY generation DESC LIMIT 1)
            ORDER BY m.relative_path_key;
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$side", side);
        var result = new List<SyncAuthorityObservedEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SyncAuthorityObservedEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.GetString(9), reader.GetString(10)));
        }
        return result;
    }

    public async Task<IReadOnlySet<string>> LoadLatestCompleteUncertainPathsAsync(
        string pairId, string side, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json_extract(o.evidence_json,'$.path')
            FROM scan_generation g
            JOIN effective_observation o ON o.scan_generation_id=g.scan_generation_id
            WHERE g.scan_generation_id=(
                SELECT scan_generation_id FROM scan_generation
                WHERE pair_id=$pair AND side=$side AND status='complete'
                ORDER BY generation DESC LIMIT 1)
              AND o.object_version_id IS NULL
              AND o.membership_id IS NULL
              AND json_type(o.evidence_json,'$.path')='text'
              AND coalesce(json_extract(o.evidence_json,'$.state'),'unavailable')<>'selective_sync_excluded'
            ORDER BY lower(json_extract(o.evidence_json,'$.path'));
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$side", side);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            paths.Add(PilotPath.NormalizeInventoryPath(reader.GetString(0)));
        return paths;
    }

    public async Task<IReadOnlySet<string>> LoadCompleteUncertainPathsAsync(
        string pairId, string side, string scanGenerationId, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        RequireIdentifier(scanGenerationId, nameof(scanGenerationId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await LoadCompleteUncertainPathsAsync(connection, null, pairId, side, scanGenerationId,
            cancellationToken);
    }

    private static async Task<IReadOnlySet<string>> LoadCompleteUncertainPathsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string pairId, string side,
        string scanGenerationId, CancellationToken cancellationToken)
    {
        var exact = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM scan_generation WHERE scan_generation_id=$scan AND pair_id=$pair AND side=$side AND status='complete';",
            cancellationToken, ("$scan", scanGenerationId), ("$pair", pairId), ("$side", side));
        if (exact != 1) throw new InvalidDataException("The requested complete uncertainty checkpoint is unavailable.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT json_extract(evidence_json,'$.path')
            FROM effective_observation
            WHERE scan_generation_id=$scan
              AND object_version_id IS NULL
              AND membership_id IS NULL
              AND json_type(evidence_json,'$.path')='text'
              AND coalesce(json_extract(evidence_json,'$.state'),'unavailable')<>'selective_sync_excluded'
            ORDER BY lower(json_extract(evidence_json,'$.path'));
            """;
        command.Parameters.AddWithValue("$scan", scanGenerationId);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            paths.Add(PilotPath.NormalizeInventoryPath(reader.GetString(0)));
        return paths;
    }

    private async Task<IReadOnlySet<string>> LoadCompleteExcludedPathsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string localScanGenerationId,
        string dropboxScanGenerationId, CancellationToken cancellationToken)
    {
        var key = (localScanGenerationId, dropboxScanGenerationId);
        if (_completeExcludedPaths.TryGetValue(key, out var retained)) return retained;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT json_extract(evidence_json,'$.path')
            FROM effective_observation
            WHERE scan_generation_id IN ($local,$dropbox)
              AND object_version_id IS NULL
              AND membership_id IS NULL
              AND json_type(evidence_json,'$.path')='text'
              AND json_extract(evidence_json,'$.state')='selective_sync_excluded'
            ORDER BY lower(json_extract(evidence_json,'$.path'));
            """;
        command.Parameters.AddWithValue("$local", localScanGenerationId);
        command.Parameters.AddWithValue("$dropbox", dropboxScanGenerationId);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            paths.Add(PilotPath.NormalizeInventoryPath(reader.GetString(0)));
        await reader.DisposeAsync();
        // Complete observation generations are immutable. Cache ONLY their exclusion
        // evidence, not pair configuration, roots, pauses or synchronization points:
        // those mutable authorities must still be read inside every reservation.
        // A new scan has a new key, so changed selective-sync choices invalidate it.
        if (await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM scan_generation WHERE scan_generation_id IN ($local,$remote) AND status='complete';",
                cancellationToken, ("$local", localScanGenerationId), ("$remote", dropboxScanGenerationId)) == 2)
        {
            // The memo itself evicts its oldest entries to its own bounded entry and byte limits, so an
            // insertion never discards the whole retained set and never grows without a bound.
            _completeExcludedPaths.TryAdd(key, paths);
        }
        return paths;
    }

    public async Task<SyncAuthorityPairObservationSet> LoadPlanningObservationSetAsync(string pairId,
        long configurationRevision, string localRootBindingId, string dropboxRootBindingId,
        string localScanGenerationId, string dropboxScanGenerationId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        RequireIdentifier(localRootBindingId, nameof(localRootBindingId));
        RequireIdentifier(dropboxRootBindingId, nameof(dropboxRootBindingId));
        RequireIdentifier(localScanGenerationId, nameof(localScanGenerationId));
        RequireIdentifier(dropboxScanGenerationId, nameof(dropboxScanGenerationId));
        if (configurationRevision < 1) throw new ArgumentOutOfRangeException(nameof(configurationRevision));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var checkpoint = new SyncAuthorityPlanningAuthority("observation-set-load", pairId,
            configurationRevision, localRootBindingId, dropboxRootBindingId, localScanGenerationId,
            dropboxScanGenerationId, new string('0', 64));
        if (!await IsPlanningAuthorityBaseCurrentAsync(connection, transaction, checkpoint, cancellationToken))
            throw new InvalidOperationException("The requested planning observation set is no longer current.");

        var local = await LoadCompleteObservationAsync(connection, transaction, pairId, "local",
            localScanGenerationId, cancellationToken);
        var dropbox = await LoadCompleteObservationAsync(connection, transaction, pairId, "dropbox",
            dropboxScanGenerationId, cancellationToken);
        var localUncertain = await LoadCompleteUncertainPathsAsync(connection, transaction, pairId, "local",
            localScanGenerationId, cancellationToken);
        var dropboxUncertain = await LoadCompleteUncertainPathsAsync(connection, transaction, pairId, "dropbox",
            dropboxScanGenerationId, cancellationToken);
        var excluded = await LoadCompleteExcludedPathsAsync(connection, transaction,
            localScanGenerationId, dropboxScanGenerationId, cancellationToken);
        var points = await LoadLatestSynchronizationPointsAsync(connection, transaction, pairId,
            cancellationToken);
        var pairState = await LoadPairPlanningStateAsync(connection, transaction, pairId, cancellationToken);
        var connectionId = await LoadPairPlanningConnectionIdAsync(connection, transaction, pairId,
            cancellationToken);
        var fingerprint = BuildObservationContentFingerprint(pairId, configurationRevision, connectionId,
            localRootBindingId, dropboxRootBindingId, local, dropbox, localUncertain, dropboxUncertain, excluded);
        var planningStateFingerprint = BuildPlanningStateFingerprint(pairId, configurationRevision,
            localRootBindingId, dropboxRootBindingId, pairState, excluded,
            localScanGenerationId, dropboxScanGenerationId);
        var synchronizationPointSetFingerprint = BuildSynchronizationPointSetFingerprint(points);
        var rootAuthorityFingerprint = await LoadRootAuthorityFingerprintAsync(connection, transaction, pairId,
            localRootBindingId, dropboxRootBindingId, cancellationToken);
        _planningContentFingerprints[PlanningContentCacheKey(pairId, connectionId, localRootBindingId,
            dropboxRootBindingId, localScanGenerationId, dropboxScanGenerationId)] = fingerprint;
        await transaction.CommitAsync(cancellationToken);
        return new SyncAuthorityPairObservationSet(pairId, connectionId, configurationRevision, localRootBindingId,
            dropboxRootBindingId, localScanGenerationId, dropboxScanGenerationId, local, dropbox,
            localUncertain, dropboxUncertain, excluded, points, fingerprint, planningStateFingerprint,
            synchronizationPointSetFingerprint, rootAuthorityFingerprint);
    }

    // A local-only pair has no account scope. Keep that absence identical when creating and
    // revalidating planning authority; an empty persisted compatibility field is not an account ID.
    // Nonempty identities are preserved exactly and still pass the normal authority validation.
    private static async Task<string?> LoadPairPlanningConnectionIdAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string pairId, CancellationToken cancellationToken)
    {
        var id = await ScalarStringAsync(connection, transaction,
            "SELECT coalesce(json_extract(pair_json,'$.ConnectionId'),json_extract(pair_json,'$.connectionId')) FROM sync_pair WHERE pair_id=$pair;",
            cancellationToken, ("$pair", pairId));
        return string.IsNullOrEmpty(id) ? null : id;
    }

    private static void AppendObservationFingerprint(StringBuilder material, string side,
        IEnumerable<SyncAuthorityObservedEntry> entries)
    {
        foreach (var entry in entries.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.ObjectId, StringComparer.Ordinal))
            material.Append(side).Append('\t').Append(entry.RelativePath).Append('\t')
                .Append(entry.ObjectId).Append('\t').Append(entry.ObjectVersionId).Append('\t')
                .Append(entry.ObjectKind).Append('\t').Append(entry.ContentHash).Append('\t')
                .AppendLine(entry.Size?.ToString(CultureInfo.InvariantCulture));
    }

    private static string BuildObservationContentFingerprint(string pairId, long configurationRevision,
        string? connectionId, string localRootBindingId, string dropboxRootBindingId,
        IReadOnlyList<SyncAuthorityObservedEntry> local, IReadOnlyList<SyncAuthorityObservedEntry> dropbox,
        IReadOnlySet<string> localUncertain, IReadOnlySet<string> dropboxUncertain,
        IReadOnlySet<string> excluded)
    {
        var material = new StringBuilder();
        material.AppendLine(pairId).AppendLine(configurationRevision.ToString(CultureInfo.InvariantCulture))
            .AppendLine(connectionId ?? "<unbound-connection>")
            .AppendLine(localRootBindingId).AppendLine(dropboxRootBindingId);
        AppendObservationFingerprint(material, "local", local);
        AppendObservationFingerprint(material, "dropbox", dropbox);
        foreach (var path in localUncertain.Order(StringComparer.OrdinalIgnoreCase))
            material.Append("local-uncertain\t").AppendLine(path);
        foreach (var path in dropboxUncertain.Order(StringComparer.OrdinalIgnoreCase))
            material.Append("dropbox-uncertain\t").AppendLine(path);
        foreach (var path in excluded.Order(StringComparer.OrdinalIgnoreCase))
            material.Append("excluded\t").AppendLine(path);
        // Synchronization authority is fingerprinted separately. Keeping this digest observation-only lets
        // exact object-scoped operations survive authorized synchronization-point changes to unrelated objects.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())))
            .ToLowerInvariant();
    }

    private static string PlanningContentCacheKey(string pairId, string? connectionId,
        string localRootBindingId, string dropboxRootBindingId, string localScanGenerationId,
        string dropboxScanGenerationId) =>
        string.Join("\n", pairId, connectionId ?? "<unbound-connection>", localRootBindingId,
            dropboxRootBindingId, localScanGenerationId, dropboxScanGenerationId);

    public async Task<IReadOnlySet<string>> LoadExcludedPathIndexPathsAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await LoadExcludedPathIndexPathsAsync(connection, null, pairId, cancellationToken);
    }

    private static async Task<IReadOnlySet<string>> LoadExcludedPathIndexPathsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string pairId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT relative_path_key FROM sync_path_index WHERE pair_id=$pair AND included=0 ORDER BY lower(relative_path_key);";
        command.Parameters.AddWithValue("$pair", pairId);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            paths.Add(PilotPath.NormalizeRelative(reader.GetString(0)));
        return paths;
    }

    private static async Task<string> LoadPairPlanningStateAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string pairId, CancellationToken cancellationToken)
    {
        return await ScalarStringAsync(connection, transaction,
                   "SELECT state || char(10) || coalesce(pair_json,'<null>') FROM sync_pair WHERE pair_id=$pair;",
                   cancellationToken, ("$pair", pairId))
               ?? throw new InvalidOperationException("The planning pair is no longer retained.");
    }

    private string BuildPlanningStateFingerprint(string pairId, long configurationRevision,
        string localRootBindingId, string dropboxRootBindingId, string pairState,
        IEnumerable<string> excludedPaths, string localScanGenerationId, string dropboxScanGenerationId)
    {
        // Memoize only this pure calculation over exact inputs, not authority.
        // Every reservation still reads current pair state, roots, latest complete
        // scan identities and its object's synchronization point in its transaction.
        // Completed generations bind immutable exclusions. Any changed input has
        // a different key; eviction only recomputes the identical historical digest.
        var key = (localScanGenerationId, dropboxScanGenerationId, pairId,
            configurationRevision, localRootBindingId, dropboxRootBindingId, pairState);
        if (_planningStateFingerprints.TryGetValue(key, out var retained)) return retained;
        var started = TimingStart();
        var material = new StringBuilder();
        material.Append(pairId).Append('\n').Append(configurationRevision.ToString(CultureInfo.InvariantCulture))
            .Append('\n').Append(localRootBindingId).Append('\n').Append(dropboxRootBindingId).Append('\n')
            .Append(pairState).Append('\n');
        foreach (var path in excludedPaths.Order(StringComparer.OrdinalIgnoreCase))
            material.Append("excluded\t").AppendLine(path);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())))
            .ToLowerInvariant();
        _planningStateFingerprints.TryAdd(key, fingerprint);
        ReportTiming("planning-state-fingerprint", started);
        return fingerprint;
    }

    private static string BuildSynchronizationPointSetFingerprint(
        IEnumerable<SyncAuthoritySynchronizationPoint> points)
    {
        var material = new StringBuilder();
        foreach (var point in points.OrderBy(item => item.ObjectId, StringComparer.Ordinal)
                     .ThenBy(item => item.AuthorityGeneration))
            material.Append(point.SynchronizationPointId).Append('\t').Append(point.PairId).Append('\t')
                .Append(point.ObjectId).Append('\t').Append(point.AuthorityGeneration).Append('\t')
                .Append(point.LocalObjectVersionId).Append('\t').Append(point.DropboxObjectVersionId).Append('\t')
                .Append(point.LocalMembershipId).Append('\t').Append(point.DropboxMembershipId).Append('\t')
                .Append(point.LocalRelativePath).Append('\t').AppendLine(point.DropboxRelativePath);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())))
            .ToLowerInvariant();
    }

    private static async Task<string> LoadRootAuthorityFingerprintAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string pairId, string localRootBindingId,
        string dropboxRootBindingId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT root_binding_id,pair_id,side,generation,identity_json,display_path,state
            FROM root_binding
            WHERE pair_id=$pair AND root_binding_id IN ($local,$dropbox)
            ORDER BY side,root_binding_id;
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$local", localRootBindingId);
        command.Parameters.AddWithValue("$dropbox", dropboxRootBindingId);
        var material = new StringBuilder();
        var count = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            count++;
            for (var index = 0; index < reader.FieldCount; index++)
            {
                if (index > 0) material.Append('\t');
                material.Append(reader.GetValue(index));
            }
            material.AppendLine();
        }
        if (count != 2)
            throw new InvalidDataException("The exact local and Dropbox root authorities are not retained.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())))
            .ToLowerInvariant();
    }

    public Task<SyncAuthorityBootstrapEvidencePage?> LoadBootstrapEvidencePageAsync(string pairId,
        int limit = 250, int offset = 0, string? search = null, string? group = null,
        string? itemKind = null, string? fileType = null,
        CancellationToken cancellationToken = default, string? reviewedLocalScanId = null,
        string? reviewedRemoteScanId = null)
        => LoadBootstrapEvidenceCoreAsync(pairId, limit, offset, search, group, itemKind, fileType,
            cancellationToken, reviewedLocalScanId, reviewedRemoteScanId, readAll: false);

    // Execution already needs every reviewed row. Read the identical classification once,
    // rather than recomputing its full joins/counts for each 500-row presentation page.
    // The public paged surface keeps its existing bound; this is not a wider UI page size.
    internal Task<SyncAuthorityBootstrapEvidencePage?> LoadBootstrapEvidenceAsync(string pairId,
        string? search = null, string? group = null, string? itemKind = null, string? fileType = null,
        CancellationToken cancellationToken = default, string? reviewedLocalScanId = null,
        string? reviewedRemoteScanId = null)
        => LoadBootstrapEvidenceCoreAsync(pairId, -1, 0, search, group, itemKind, fileType,
            cancellationToken, reviewedLocalScanId, reviewedRemoteScanId, readAll: true);

    private async Task<SyncAuthorityBootstrapEvidencePage?> LoadBootstrapEvidenceCoreAsync(string pairId,
        int limit, int offset, string? search, string? group, string? itemKind, string? fileType,
        CancellationToken cancellationToken, string? reviewedLocalScanId,
        string? reviewedRemoteScanId, bool readAll)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (!readAll && (limit is < 1 or > 500)) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var query = (search ?? string.Empty).Trim();
        if (query.Length > 256) throw new ArgumentOutOfRangeException(nameof(search));
        var selectedGroup = string.IsNullOrWhiteSpace(group) ? "All groups" : group.Trim();
        string[] acceptedGroups =
        [
            "All groups", "Local newer", "Dropbox newer", "Approximately equal time",
            "Unreliable or missing time", "Identical", "Local only", "Dropbox only",
            "Unreliable or missing", SyncBootstrapActions.LocalUpdatedGroup,
            SyncBootstrapActions.RemoteUpdatedGroup
        ];
        if (!acceptedGroups.Contains(selectedGroup, StringComparer.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(group));
        var selectedItemKind = string.IsNullOrWhiteSpace(itemKind) ? "All items" : itemKind.Trim();
        if (selectedItemKind is not ("All items" or "Folders" or "Files"))
            throw new ArgumentOutOfRangeException(nameof(itemKind));
        var selectedFileType = string.IsNullOrWhiteSpace(fileType) ? "All types" : fileType.Trim();
        if (selectedFileType.Length > 32 || selectedFileType.Any(character =>
                !char.IsLetterOrDigit(character) && character is not ' ' and not '-' and not '_'))
            throw new ArgumentOutOfRangeException(nameof(fileType));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        // An empty path index is a valid completed observation of two empty
        // folders. Scan authority comes from complete scan records, not rows.
        if (reviewedLocalScanId is null && await ScalarLongAsync(connection, null,
                "SELECT count(DISTINCT side) FROM scan_generation WHERE pair_id=$pair AND side IN ('local','dropbox') AND status='complete';",
                cancellationToken, ("$pair", pairId)) != 2)
            return null;
        var localScanGenerationId = await ScalarStringAsync(connection, null,
            "SELECT scan_generation_id FROM scan_generation WHERE pair_id=$pair AND side='local' AND status='complete' AND ($scan IS NULL OR scan_generation_id=$scan) AND root_binding_id=(SELECT root_binding_id FROM scan_generation WHERE pair_id=$pair AND side='local' AND status='complete' ORDER BY generation DESC LIMIT 1) ORDER BY generation DESC LIMIT 1;",
            cancellationToken, ("$pair", pairId), ("$scan", reviewedLocalScanId)) ?? throw new InvalidDataException("The reviewed left inventory is unavailable or its selected root changed. Scan again.");
        var dropboxScanGenerationId = await ScalarStringAsync(connection, null,
            "SELECT scan_generation_id FROM scan_generation WHERE pair_id=$pair AND side='dropbox' AND status='complete' AND ($scan IS NULL OR scan_generation_id=$scan) AND root_binding_id=(SELECT root_binding_id FROM scan_generation WHERE pair_id=$pair AND side='dropbox' AND status='complete' ORDER BY generation DESC LIMIT 1) ORDER BY generation DESC LIMIT 1;",
            cancellationToken, ("$pair", pairId), ("$scan", reviewedRemoteScanId)) ?? throw new InvalidDataException("The reviewed right inventory is unavailable or its selected root changed. Scan again.");

        // Materialize only fields consumed by this comparison, not full retained evidence JSON.
        // Excluded-path observations have neither a version nor a membership and were already
        // rejected by every downstream branch. Filter them once before temporary-table creation.
        // The original observation/history and exact selected scan identities remain unchanged.
        const string classifiedSql = """
            WITH scoped_observations AS MATERIALIZED (
                SELECT scan_generation_id,object_version_id,membership_id,
                       CASE WHEN scan_generation_id=$local_scan THEN
                           coalesce(json_extract(evidence_json,'$.lastWriteUtc'),
                               json_extract(json_extract(evidence_json,'$.identity'),'$.lastWriteUtc')) END AS local_modified,
                       CASE WHEN scan_generation_id=$remote_scan THEN
                           json_extract(evidence_json,'$.modifiedUtc') END AS remote_modified,
                       CASE WHEN object_version_id IS NULL AND membership_id IS NULL THEN
                           json_extract(evidence_json,'$.path') END AS uncertain_path,
                       CASE WHEN object_version_id IS NULL AND membership_id IS NULL THEN
                           json_type(evidence_json,'$.path') END AS uncertain_path_type,
                       CASE WHEN object_version_id IS NULL AND membership_id IS NULL THEN
                           json_extract(evidence_json,'$.reason') END AS uncertain_reason
                FROM effective_observation
                WHERE (scan_generation_id=$local_scan OR scan_generation_id=$remote_scan)
                  AND NOT (object_version_id IS NULL AND membership_id IS NULL
                      AND coalesce(json_extract(evidence_json,'$.state'),'')='selective_sync_excluded')
            ), latest_local(scan_id) AS (
                SELECT $local_scan
            ), latest_dropbox(scan_id) AS (
                SELECT $remote_scan
            ), local_entry AS MATERIALIZED (
                SELECT m.relative_path_key AS path,lo.object_kind AS kind,v.content_hash AS hash,
                       v.size_bytes AS size,v.object_id,v.object_version_id,m.root_binding_id,
                       o.local_modified AS modified
                FROM scoped_observations o
                JOIN latest_local g ON g.scan_id=o.scan_generation_id
                JOIN membership m ON m.membership_id=o.membership_id
                JOIN object_version v ON v.object_version_id=o.object_version_id
                JOIN logical_object lo ON lo.object_id=v.object_id
            ), dropbox_entry AS MATERIALIZED (
                SELECT m.relative_path_key AS path,lo.object_kind AS kind,v.content_hash AS hash,
                       v.size_bytes AS size,v.object_id,v.object_version_id,m.root_binding_id,
                       o.remote_modified AS modified
                FROM scoped_observations o
                JOIN latest_dropbox g ON g.scan_id=o.scan_generation_id
                JOIN membership m ON m.membership_id=o.membership_id
                JOIN object_version v ON v.object_version_id=o.object_version_id
                JOIN logical_object lo ON lo.object_id=v.object_id
            ), uncertain AS (
                SELECT o.uncertain_path AS path
                FROM scoped_observations o JOIN latest_local g ON g.scan_id=o.scan_generation_id
                WHERE o.object_version_id IS NULL AND o.membership_id IS NULL
                  AND o.uncertain_path_type='text'
                UNION
                SELECT o.uncertain_path AS path
                FROM scoped_observations o JOIN latest_dropbox g ON g.scan_id=o.scan_generation_id
                WHERE o.object_version_id IS NULL AND o.membership_id IS NULL
                  AND o.uncertain_path_type='text'
            ), uncertainty_reasons AS (
                SELECT uncertain_path AS path,
                       group_concat(DISTINCT uncertain_reason) AS reason
                FROM scoped_observations
                WHERE object_version_id IS NULL AND membership_id IS NULL
                GROUP BY uncertain_path
            ), reviewed_paths AS (
                SELECT path FROM local_entry UNION SELECT path FROM dropbox_entry
                UNION SELECT path FROM uncertain
            ), evidence AS (
                SELECT p.path,l.kind AS local_kind,d.kind AS dropbox_kind,
                       l.hash AS local_hash,d.hash AS dropbox_hash,l.size AS local_size,
                       d.size AS dropbox_size,l.modified AS local_modified,d.modified AS dropbox_modified,
                       CASE WHEN b.object_id=d.object_id
                           AND bl.relative_path_key=p.path AND bd.relative_path_key=p.path
                           AND bl.root_binding_id=l.root_binding_id AND bd.root_binding_id=d.root_binding_id
                           AND b.local_object_version_id IS NOT NULL AND b.dropbox_object_version_id IS NOT NULL
                           THEN b.object_id END AS baseline_object,
                       l.object_version_id=b.local_object_version_id AS local_matches_baseline,
                       d.object_version_id=b.dropbox_object_version_id AS dropbox_matches_baseline,
                       CASE WHEN u.path IS NULL THEN 0 ELSE 1 END AS uncertain,
                       ur.reason AS blocked_reason,l.object_id AS local_object_id,
                       l.object_version_id AS local_version_id,b.object_id IS NOT NULL AS has_baseline
                FROM reviewed_paths p
                LEFT JOIN local_entry l ON l.path=p.path
                LEFT JOIN dropbox_entry d ON d.path=p.path
                LEFT JOIN uncertain u ON u.path=p.path
                LEFT JOIN uncertainty_reasons ur ON ur.path=p.path
                LEFT JOIN current_synchronization_point b ON b.pair_id=$pair AND b.object_id=l.object_id
                    AND b.authority_generation=(SELECT max(n.authority_generation)
                        FROM current_synchronization_point n WHERE n.pair_id=$pair AND n.object_id=l.object_id)
                LEFT JOIN membership bl ON bl.membership_id=b.local_membership_id
                LEFT JOIN membership bd ON bd.membership_id=b.dropbox_membership_id
            ), classified AS MATERIALIZED (
                SELECT *,CASE
                    WHEN uncertain=1 THEN 'Unreliable or missing'
                    WHEN local_kind IS NULL THEN 'Dropbox only'
                    WHEN dropbox_kind IS NULL THEN 'Local only'
                    WHEN local_kind=dropbox_kind AND local_hash IS dropbox_hash THEN 'Identical'
                    WHEN baseline_object IS NOT NULL AND local_matches_baseline=1
                        AND dropbox_matches_baseline=1 THEN 'Identical'
                    WHEN local_kind='file' AND dropbox_kind='file' AND local_hash IS NOT NULL
                        AND dropbox_hash IS NOT NULL AND baseline_object IS NOT NULL
                        AND local_matches_baseline=0 AND dropbox_matches_baseline=1
                        THEN 'Local changed since sync'
                    WHEN local_kind='file' AND dropbox_kind='file' AND local_hash IS NOT NULL
                        AND dropbox_hash IS NOT NULL AND baseline_object IS NOT NULL
                        AND local_matches_baseline=1 AND dropbox_matches_baseline=0
                        THEN 'Dropbox changed since sync'
                    WHEN julianday(local_modified) IS NULL OR julianday(dropbox_modified) IS NULL
                        THEN 'Unreliable or missing time'
                    WHEN abs((julianday(local_modified)-julianday(dropbox_modified))*86400.0)<=120.0
                        THEN 'Approximately equal time'
                    WHEN julianday(local_modified)>julianday(dropbox_modified) THEN 'Local newer'
                    ELSE 'Dropbox newer' END AS group_name
                FROM evidence
            )
            """;
        int? totalMatching = null;
        int[]? totals = null;
        var items = new List<SyncAuthorityBootstrapEvidence>();
        var queryStarted = TimingStart();
        await using (var command = connection.CreateCommand())
        {
            // Each retained entry join and classification is evaluated once. Stacked
            // whole-result window counts caused gigabytes of temporary IO for a real
            // 309k-path review. Scalar totals over the same materialized rows preserve
            // global/filtered counts and paging without repeating those window spools.
            command.CommandText = classifiedSql + """
                , classified_totals AS (
                    SELECT count(*) AS total_items,
                           count(CASE WHEN group_name='Local only' THEN 1 END) AS local_only_items,
                           count(CASE WHEN group_name='Dropbox only' THEN 1 END) AS dropbox_only_items,
                           count(CASE WHEN group_name IN ('Local newer','Dropbox newer',
                               'Approximately equal time','Unreliable or missing time') THEN 1 END)
                               AS conflict_items,
                           count(CASE WHEN group_name='Local changed since sync' THEN 1 END)
                               AS local_updated_items,
                           count(CASE WHEN group_name='Dropbox changed since sync' THEN 1 END)
                               AS dropbox_updated_items
                    FROM classified
                ), matching AS MATERIALIZED (
                    SELECT * FROM classified
                     WHERE ($search='' OR instr(lower(path),lower($search))>0)
                       AND ($group='All groups' OR group_name=$group)
                       AND ($item_kind='All items'
                            OR ($item_kind='Folders' AND coalesce(local_kind,dropbox_kind,'unknown')='directory')
                            OR ($item_kind='Files' AND coalesce(local_kind,dropbox_kind,'unknown')<>'directory'))
                       AND ($file_type='All types'
                            OR ($file_type='Folder' AND coalesce(local_kind,dropbox_kind,'unknown')='directory')
                            OR ($file_type='File' AND coalesce(local_kind,dropbox_kind,'unknown')<>'directory')
                            OR (coalesce(local_kind,dropbox_kind,'unknown')<>'directory'
                                AND lower(path) LIKE '%.' || lower($file_type)))
                ), matching_totals AS (
                    SELECT count(*) AS total_matching_items FROM matching
                )
                SELECT path,group_name,coalesce(local_kind,dropbox_kind,'unknown'),
                       local_size,dropbox_size,local_modified,dropbox_modified,
                       total_matching_items,total_items,local_only_items,dropbox_only_items,conflict_items,
                       local_updated_items,dropbox_updated_items,blocked_reason,
                       local_object_id,local_version_id,has_baseline
                FROM matching CROSS JOIN classified_totals CROSS JOIN matching_totals
                ORDER BY group_name,path COLLATE NOCASE
                LIMIT $limit OFFSET $offset;
                """;
            AddBootstrapParameters(command, pairId, query, selectedGroup, selectedItemKind, selectedFileType);
            command.Parameters.AddWithValue("$local_scan", localScanGenerationId);
            command.Parameters.AddWithValue("$remote_scan", dropboxScanGenerationId);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            await ReadWithNativeCancellationAsync(connection, async () =>
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    totalMatching ??= checked((int)reader.GetInt64(7));
                    totals ??=
                    [
                        checked((int)reader.GetInt64(8)), checked((int)reader.GetInt64(9)),
                        checked((int)reader.GetInt64(10)), checked((int)reader.GetInt64(11)),
                        checked((int)reader.GetInt64(12)), checked((int)reader.GetInt64(13))
                    ];
                    items.Add(new SyncAuthorityBootstrapEvidence(reader.GetString(0), reader.GetString(1),
                        reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4),
                        ParseNullableUtc(reader, 5), ParseNullableUtc(reader, 6),
                        BlockedReason: reader.IsDBNull(14) ? null : reader.GetString(14),
                        LocalObjectId: reader.IsDBNull(15) ? null : reader.GetString(15),
                        LocalObjectVersionId: reader.IsDBNull(16) ? null : reader.GetString(16),
                        HasBaseline: reader.GetInt64(17) != 0));
                }
                return true;
            }, cancellationToken, BootstrapReadProgressForTest is null ? null :
                () => BootstrapReadProgressForTest?.Invoke("rows"));
        }

        if (totalMatching is null || totals is null)
        {
            await using var summary = connection.CreateCommand();
            summary.CommandText = classifiedSql + """
                SELECT count(CASE WHEN ($search='' OR instr(lower(path),lower($search))>0)
                                       AND ($group='All groups' OR group_name=$group)
                                       AND ($item_kind='All items'
                                            OR ($item_kind='Folders' AND coalesce(local_kind,dropbox_kind,'unknown')='directory')
                                            OR ($item_kind='Files' AND coalesce(local_kind,dropbox_kind,'unknown')<>'directory'))
                                       AND ($file_type='All types'
                                            OR ($file_type='Folder' AND coalesce(local_kind,dropbox_kind,'unknown')='directory')
                                            OR ($file_type='File' AND coalesce(local_kind,dropbox_kind,'unknown')<>'directory')
                                            OR (coalesce(local_kind,dropbox_kind,'unknown')<>'directory'
                                                AND lower(path) LIKE '%.' || lower($file_type))) THEN 1 END),
                       count(*),
                       count(CASE WHEN group_name='Local only' THEN 1 END),
                       count(CASE WHEN group_name='Dropbox only' THEN 1 END),
                       count(CASE WHEN group_name IN ('Local newer','Dropbox newer',
                           'Approximately equal time','Unreliable or missing time') THEN 1 END),
                       count(CASE WHEN group_name='Local changed since sync' THEN 1 END),
                       count(CASE WHEN group_name='Dropbox changed since sync' THEN 1 END)
                FROM classified;
                """;
            AddBootstrapParameters(summary, pairId, query, selectedGroup, selectedItemKind, selectedFileType);
            summary.Parameters.AddWithValue("$local_scan", localScanGenerationId);
            summary.Parameters.AddWithValue("$remote_scan", dropboxScanGenerationId);
            await ReadWithNativeCancellationAsync(connection, async () =>
            {
                await using var reader = await summary.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                totalMatching = checked((int)reader.GetInt64(0));
                totals = Enumerable.Range(1, 6).Select(index => checked((int)reader.GetInt64(index))).ToArray();
                return true;
            }, cancellationToken, BootstrapReadProgressForTest is null ? null :
                () => BootstrapReadProgressForTest?.Invoke("summary"));
        }
        if (totalMatching is null || totals is null)
            throw new InvalidDataException("The completed inventory comparison did not return its totals.");
        ReportTiming("bootstrap-evidence-query", queryStarted);
        await AddBootstrapMoveReviewsAsync(connection, pairId, localScanGenerationId,
            dropboxScanGenerationId, items, cancellationToken, BootstrapReadProgressForTest is null ? null :
                () => BootstrapReadProgressForTest?.Invoke("moves"));
        return new SyncAuthorityBootstrapEvidencePage(items, offset, totalMatching.Value, totals[0], totals[1],
            totals[2], totals[3], offset + items.Count < totalMatching.Value, localScanGenerationId,
            dropboxScanGenerationId, totals[4], totals[5]);
    }

    private static void AddBootstrapParameters(SqliteCommand command, string pairId, string search,
        string group, string itemKind, string fileType)
    {
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$search", search);
        command.Parameters.AddWithValue("$group", group);
        command.Parameters.AddWithValue("$item_kind", itemKind);
        command.Parameters.AddWithValue("$file_type", fileType);
    }

    private static DateTimeOffset? ParseNullableUtc(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public async Task<string> LoadLatestCompleteScanIdAsync(string pairId, string side,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarStringAsync(connection, null,
                   "SELECT scan_generation_id FROM scan_generation WHERE pair_id=$pair AND side=$side AND status='complete' ORDER BY generation DESC LIMIT 1;",
                   cancellationToken, ("$pair", pairId), ("$side", side))
               ?? throw new InvalidOperationException("The pair has no complete observation for the requested side.");
    }

    public async Task<string> LoadActiveRootBindingIdAsync(string pairId, string side,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarStringAsync(connection, null,
                   "SELECT root_binding_id FROM root_binding WHERE pair_id=$pair AND side=$side AND state='active';",
                   cancellationToken, ("$pair", pairId), ("$side", side))
               ?? throw new InvalidOperationException("The pair has no active identity-bound root for the requested side.");
    }

    internal async Task<(long Generation, string IdentityJson, string DisplayPath)>
        LoadRootBindingAuthorityForTestAsync(string rootBindingId,
            CancellationToken cancellationToken = default)
    {
        RequireIdentifier(rootBindingId, nameof(rootBindingId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT generation,identity_json,display_path FROM root_binding WHERE root_binding_id=$id AND state='active';";
        command.Parameters.AddWithValue("$id", rootBindingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidDataException("The active test root authority is not retained.");
        return (reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
    }

    public async Task<IReadOnlyList<SyncAuthoritySynchronizationPoint>> LoadLatestSynchronizationPointsAsync(
        string pairId, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await LoadLatestSynchronizationPointsAsync(connection, null, pairId, cancellationToken);
    }

    private static async Task<IReadOnlyList<SyncAuthoritySynchronizationPoint>>
        LoadLatestSynchronizationPointsAsync(SqliteConnection connection, SqliteTransaction? transaction,
            string pairId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.synchronization_point_id,s.pair_id,s.object_id,s.authority_generation,
                   s.local_object_version_id,s.dropbox_object_version_id,
                   s.local_membership_id,s.dropbox_membership_id,
                   lm.relative_path_key,dm.relative_path_key
            FROM current_synchronization_point s
            LEFT JOIN membership lm ON lm.membership_id=s.local_membership_id
            LEFT JOIN membership dm ON dm.membership_id=s.dropbox_membership_id
            WHERE s.pair_id=$pair AND s.authority_generation=(
                SELECT max(newer.authority_generation) FROM current_synchronization_point newer
                WHERE newer.pair_id=s.pair_id AND newer.object_id=s.object_id)
            ORDER BY coalesce(lm.relative_path_key,dm.relative_path_key),s.object_id;
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        var result = new List<SyncAuthoritySynchronizationPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SyncAuthoritySynchronizationPoint(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return result;
    }

    public async Task RecordReconciliationDecisionAsync(string decisionId, string pairId, string objectId,
        string localScanGenerationId, string dropboxScanGenerationId, string? synchronizationPointId,
        string decisionKind, string decisionJson, CancellationToken cancellationToken = default)
        => await RecordReconciliationDecisionsAsync([new(decisionId, pairId, objectId,
            localScanGenerationId, dropboxScanGenerationId, synchronizationPointId, decisionKind, decisionJson)],
            cancellationToken);

    internal async Task RecordReconciliationDecisionsAsync(
        IReadOnlyList<SyncAuthorityDecisionRecord> records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) return;
        foreach (var record in records)
        {
            foreach (var value in new[] { record.DecisionId, record.PairId, record.ObjectId,
                         record.LocalScanGenerationId, record.DropboxScanGenerationId })
                RequireIdentifier(value, nameof(records));
            if (record.Kind is not ("unchanged" or "initial_observation" or "adopted" or "operation" or
                "conflict" or "tombstone" or "move" or "blocked" or "skipped"))
                throw new ArgumentOutOfRangeException(nameof(records));
            RequireJson(record.Json, nameof(records));
        }
        await WriteAsync(async (connection, transaction) =>
        {
            foreach (var record in records)
                await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO reconciliation_decision(reconciliation_decision_id,pair_id,object_id,local_scan_generation_id,dropbox_scan_generation_id,synchronization_point_id,decision_kind,decision_json,decided_utc) VALUES($id,$pair,$object,$local,$dropbox,$point,$kind,$json,$utc);",
                cancellationToken, ("$id", record.DecisionId), ("$pair", record.PairId), ("$object", record.ObjectId),
                ("$local", record.LocalScanGenerationId), ("$dropbox", record.DropboxScanGenerationId),
                ("$point", record.SynchronizationPointId), ("$kind", record.Kind), ("$json", record.Json),
                ("$utc", Utc(DateTimeOffset.UtcNow)));
        }, cancellationToken);
    }

    public async Task<string> RecordConflictAsync(string conflictId, string pairId, string objectId,
        string reason, string localObjectVersionId, string? localMembershipId,
        string dropboxObjectVersionId, string? dropboxMembershipId,
        CancellationToken cancellationToken = default)
    {
        await RecordConflictsAsync([new(conflictId, pairId, objectId, reason, localObjectVersionId,
            localMembershipId, dropboxObjectVersionId, dropboxMembershipId)], cancellationToken);
        return conflictId;
    }

    // Many conflicts in one transaction: each row keeps the exact single-conflict writes.
    internal async Task RecordConflictsAsync(IReadOnlyList<SyncAuthorityConflictRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) return;
        foreach (var record in records)
        {
            foreach (var value in new[] { record.ConflictId, record.PairId, record.ObjectId })
                RequireIdentifier(value, nameof(records));
            ArgumentException.ThrowIfNullOrWhiteSpace(record.Reason);
        }
        await WriteAsync(async (connection, transaction) =>
        {
            using var commands = new ObservationCommands(connection, transaction);
            foreach (var record in records)
            {
                var generation = await commands.ScalarLongAsync(
                    "SELECT coalesce(max(conflict_generation),0)+1 FROM conflict WHERE pair_id=$pair AND object_id=$object;",
                    cancellationToken, ("$pair", record.PairId), ("$object", record.ObjectId));
                await commands.ExecuteAsync(
                    "INSERT OR IGNORE INTO conflict(conflict_id,pair_id,object_id,conflict_generation,reason,status,created_utc) VALUES($id,$pair,$object,$generation,$reason,'open',$utc);",
                    cancellationToken, ("$id", record.ConflictId), ("$pair", record.PairId), ("$object", record.ObjectId),
                    ("$generation", generation), ("$reason", record.Reason), ("$utc", Utc(DateTimeOffset.UtcNow)));
                await commands.ExecuteAsync(
                    "INSERT OR IGNORE INTO conflict_version(conflict_id,side,object_version_id,membership_id) VALUES($id,'local',$version,$membership);",
                    cancellationToken, ("$id", record.ConflictId), ("$version", record.LocalObjectVersionId),
                    ("$membership", record.LocalMembershipId));
                await commands.ExecuteAsync(
                    "INSERT OR IGNORE INTO conflict_version(conflict_id,side,object_version_id,membership_id) VALUES($id,'dropbox',$version,$membership);",
                    cancellationToken, ("$id", record.ConflictId), ("$version", record.DropboxObjectVersionId),
                    ("$membership", record.DropboxMembershipId));
            }
        }, cancellationToken);
    }

    public async Task<SyncAuthorityConflictSnapshot> LoadConflictAsync(string conflictId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(conflictId, nameof(conflictId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.pair_id,c.object_id,c.conflict_generation,c.status,
                   lv.object_version_id,dv.object_version_id,
                   lv.membership_id,dv.membership_id,
                   lm.root_binding_id,dm.root_binding_id,
                   lm.generation,dm.generation,
                   lm.relative_path_key,dm.relative_path_key,c.created_utc
            FROM conflict c
            JOIN conflict_version lv ON lv.conflict_id=c.conflict_id AND lv.side='local'
            JOIN conflict_version dv ON dv.conflict_id=c.conflict_id AND dv.side='dropbox'
            LEFT JOIN membership lm ON lm.membership_id=lv.membership_id
            LEFT JOIN membership dm ON dm.membership_id=dv.membership_id
            WHERE c.conflict_id=$id;
            """;
        command.Parameters.AddWithValue("$id", conflictId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidDataException("The conflict is not retained.");
        if (reader.IsDBNull(8) || reader.IsDBNull(9) || reader.IsDBNull(10) || reader.IsDBNull(11) ||
            reader.IsDBNull(12) || reader.IsDBNull(13))
            throw new InvalidDataException("The conflict has no complete current membership authority.");
        return new SyncAuthorityConflictSnapshot(conflictId, reader.GetString(0), reader.GetString(1),
            reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetInt64(10), reader.GetInt64(11),
            reader.GetString(12), reader.GetString(13),
            DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture));
    }

    public async Task<SyncAuthorityConflictSnapshot?> LoadCurrentConflictForPathAsync(string pairId,
        string relativePath, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        var path = PilotPath.NormalizeRelative(relativePath);
        object? scalar;
        await using (var connection = await OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT c.conflict_id
                FROM conflict c
                JOIN conflict_version lv ON lv.conflict_id=c.conflict_id AND lv.side='local'
                JOIN conflict_version dv ON dv.conflict_id=c.conflict_id AND dv.side='dropbox'
                JOIN membership lm ON lm.membership_id=lv.membership_id
                JOIN membership dm ON dm.membership_id=dv.membership_id
                WHERE c.pair_id=$pair AND c.status IN ('open','resolution_requested','resolving')
                  AND lm.relative_path_key=$path AND dm.relative_path_key=$path
                ORDER BY c.conflict_generation DESC,c.created_utc DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$pair", pairId);
            command.Parameters.AddWithValue("$path", path);
            scalar = await command.ExecuteScalarAsync(cancellationToken);
        }
        // A no-row, SQL NULL or DBNull result is the documented "no open conflict for this path" answer: it
        // returns null here, BEFORE any identifier conversion or conflict load, so a missing conflict can
        // never become an empty identifier or an unrelated load. A real identifier still loads normally.
        if (scalar is null || scalar is DBNull) return null;
        var conflictId = Convert.ToString(scalar, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(conflictId) ? null : await LoadConflictAsync(conflictId, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> LoadOpenConflictIdsAsync(string? pairId = null,
        CancellationToken cancellationToken = default)
    {
        if (pairId is not null) RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT conflict_id
            FROM conflict
            WHERE status IN ('open','resolution_requested','resolving')
              AND ($pair IS NULL OR pair_id=$pair)
            ORDER BY created_utc,conflict_id;
            """;
        command.Parameters.AddWithValue("$pair", pairId is null ? DBNull.Value : pairId);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<IReadOnlyList<SyncAuthorityLocalCloudRepresentation>>
        LoadLatestLocalCloudRepresentationsAsync(string pairId, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT lm.relative_path_key,
                   lv.identity_json,lv.content_hash,lv.size_bytes,
                   dv.identity_json,dv.content_hash,dv.size_bytes
            FROM current_synchronization_point s
            JOIN membership lm ON lm.membership_id=s.local_membership_id
            JOIN object_version lv ON lv.object_version_id=s.local_object_version_id
            JOIN object_version dv ON dv.object_version_id=s.dropbox_object_version_id
            WHERE s.pair_id=$pair AND s.authority_generation=(
                SELECT max(newer.authority_generation) FROM current_synchronization_point newer
                WHERE newer.pair_id=s.pair_id AND newer.object_id=s.object_id)
            ORDER BY lm.relative_path_key,s.object_id;
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        var result = new List<SyncAuthorityLocalCloudRepresentation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SyncAuthorityLocalCloudRepresentation(reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6)));
        }
        return result;
    }

    public async Task<IReadOnlyList<string>> LoadOpenConflictIdsPageAsync(int limit, int offset = 0,
        string? search = null, string? pairId = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pairId is not null) RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.conflict_id
            FROM conflict c
            WHERE c.status IN ('open','resolution_requested','resolving')
              AND ($pair IS NULL OR c.pair_id=$pair)
              AND ($search='' OR c.pair_id LIKE $search OR EXISTS(
                    SELECT 1
                    FROM conflict_version cv
                    JOIN membership m ON m.membership_id=cv.membership_id
                    WHERE cv.conflict_id=c.conflict_id AND m.relative_path_key LIKE $search))
            ORDER BY c.created_utc,c.conflict_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$pair", pairId is null ? DBNull.Value : pairId);
        command.Parameters.AddWithValue("$search", string.IsNullOrWhiteSpace(search)
            ? string.Empty : "%" + search.Trim() + "%");
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<IReadOnlyList<SyncAuthorityConflictProjection>> LoadOpenConflictProjectionPageAsync(
        int limit, int offset = 0, string? search = null, string? pairId = null,
        CancellationToken cancellationToken = default, IReadOnlyList<string>? allowedPairIds = null)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pairId is not null) RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.conflict_id,c.pair_id,lm.relative_path_key,c.conflict_generation,
                   lov.size_bytes,dov.size_bytes,lov.content_hash,dov.content_hash,
                   lov.identity_json,
                   (SELECT o.evidence_json
                    FROM effective_observation o
                    WHERE o.object_version_id=dov.object_version_id
                    ORDER BY o.observed_utc DESC,o.observation_id DESC
                    LIMIT 1),
                   c.status
            FROM conflict c
            JOIN conflict_version lv ON lv.conflict_id=c.conflict_id AND lv.side='local'
            JOIN conflict_version dv ON dv.conflict_id=c.conflict_id AND dv.side='dropbox'
            JOIN object_version lov ON lov.object_version_id=lv.object_version_id
            JOIN object_version dov ON dov.object_version_id=dv.object_version_id
            JOIN membership lm ON lm.membership_id=lv.membership_id
            JOIN membership dm ON dm.membership_id=dv.membership_id
            WHERE c.status IN ('open','resolution_requested','resolving')
              AND ($pair IS NULL OR c.pair_id=$pair)
              AND ($pairs IS NULL OR c.pair_id IN (SELECT value FROM json_each($pairs)))
              AND ($search='' OR c.pair_id LIKE $search OR lm.relative_path_key LIKE $search OR
                   dm.relative_path_key LIKE $search)
            ORDER BY c.created_utc,c.conflict_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$pair", pairId is null ? DBNull.Value : pairId);
        command.Parameters.AddWithValue("$pairs", allowedPairIds is null ? DBNull.Value : JsonSerializer.Serialize(allowedPairIds));
        command.Parameters.AddWithValue("$search", string.IsNullOrWhiteSpace(search)
            ? string.Empty : "%" + search.Trim() + "%");
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var result = new List<SyncAuthorityConflictProjection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SyncAuthorityConflictProjection(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetString(10)));
        }
        return result;
    }

    public async Task<IReadOnlyList<SyncAuthorityObservedEntry>> LoadCompleteObservationAsync(
        string pairId, string side, string scanGenerationId, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        RequireIdentifier(scanGenerationId, nameof(scanGenerationId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await LoadCompleteObservationAsync(connection, null, pairId, side, scanGenerationId,
            cancellationToken);
    }

    private static async Task<IReadOnlyList<SyncAuthorityObservedEntry>> LoadCompleteObservationAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string pairId, string side,
        string scanGenerationId, CancellationToken cancellationToken)
    {
        var exact = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM scan_generation WHERE scan_generation_id=$scan AND pair_id=$pair AND side=$side AND status='complete';",
            cancellationToken, ("$scan", scanGenerationId), ("$pair", pairId), ("$side", side));
        if (exact != 1) throw new InvalidDataException("The requested complete observation checkpoint is unavailable.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT m.relative_path_key,m.object_id,o.object_version_id,m.membership_id,
                   m.root_binding_id,g.scan_generation_id,m.generation,
                   v.content_hash,v.size_bytes,o.evidence_json,lo.object_kind
            FROM scan_generation g
            JOIN effective_observation o ON o.scan_generation_id=g.scan_generation_id
            JOIN membership m ON m.membership_id=o.membership_id
            JOIN object_version v ON v.object_version_id=o.object_version_id
            JOIN logical_object lo ON lo.object_id=m.object_id
            WHERE g.scan_generation_id=$scan
            ORDER BY m.relative_path_key;
            """;
        command.Parameters.AddWithValue("$scan", scanGenerationId);
        var result = new List<SyncAuthorityObservedEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new SyncAuthorityObservedEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.GetString(9), reader.GetString(10)));
        return result;
    }

    public async Task CompleteEligibleConflictResolutionsAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        var eligible = new List<(string ConflictId, string GroupId)>();
        await using (var connection = await OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT cr.conflict_id,cr.operation_group_id
                FROM conflict_resolution cr
                JOIN conflict c ON c.conflict_id=cr.conflict_id
                WHERE c.pair_id=$pair AND cr.status IN ('planned','executing')
                  AND EXISTS(SELECT 1 FROM operation o WHERE o.operation_group_id=cr.operation_group_id)
                  AND NOT EXISTS(
                      SELECT 1 FROM operation o
                      WHERE o.operation_group_id=cr.operation_group_id AND o.status<>'completed');
                """;
            command.Parameters.AddWithValue("$pair", pairId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                eligible.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var item in eligible)
            await CompleteConflictResolutionAsync(item.ConflictId, item.GroupId, cancellationToken);
    }

    // Newest wins planned this object's replacement: its open (unchosen) conflicts are no longer the user's to decide.
    internal Task SupersedeOpenConflictsAsync(string pairId, string objectId, CancellationToken cancellationToken) =>
        WriteAsync((connection, transaction) => ExecuteAsync(connection, transaction,
            "UPDATE conflict SET status='superseded' WHERE pair_id=$pair AND object_id=$object AND status='open';",
            cancellationToken, ("$pair", pairId), ("$object", objectId)), cancellationToken);

    internal async Task<bool> MarkStaleUnissuedConflictResolutionAsync(string conflictId,
        long expectedGeneration, string operationGroupId, CancellationToken cancellationToken)
    {
        RequireIdentifier(conflictId, nameof(conflictId));
        RequireIdentifier(operationGroupId, nameof(operationGroupId));
        if (expectedGeneration < 1) throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        var retired = false;
        await WriteAsync(async (connection, transaction) =>
        {
            var safe = await ScalarLongAsync(connection, transaction, """
                SELECT count(*) FROM conflict c JOIN conflict_resolution r ON r.conflict_id=c.conflict_id
                JOIN operation_group g ON g.operation_group_id=r.operation_group_id
                WHERE c.conflict_id=$conflict AND c.conflict_generation=$generation
                  AND c.status IN ('resolution_requested','resolving')
                  AND r.requested_generation=$generation AND r.operation_group_id=$group
                  AND r.status IN ('planned','executing') AND g.kind='conflict_resolution'
                  AND g.status IN ('planned','active')
                  AND EXISTS(SELECT 1 FROM operation o WHERE o.operation_group_id=$group)
                  AND NOT EXISTS(SELECT 1 FROM operation o WHERE o.operation_group_id=$group
                                 AND o.status NOT IN ('superseded','cancelled'))
                  AND NOT EXISTS(SELECT 1 FROM operation o JOIN operation_attempt a
                                 ON a.operation_id=o.operation_id WHERE o.operation_group_id=$group)
                  AND NOT EXISTS(SELECT 1 FROM operation o JOIN resource_allocation a
                                 ON a.operation_id=o.operation_id WHERE o.operation_group_id=$group
                                 AND a.status NOT IN ('authorized','released'));
                """, cancellationToken, ("$conflict", conflictId), ("$generation", expectedGeneration),
                ("$group", operationGroupId));
            if (safe != 1) return;
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                "UPDATE conflict SET status='superseded' WHERE conflict_id=$conflict AND conflict_generation=$generation;",
                cancellationToken, ("$conflict", conflictId), ("$generation", expectedGeneration));
            await ExecuteAsync(connection, transaction,
                "UPDATE conflict_resolution SET status='stale',terminal_utc=$utc WHERE conflict_id=$conflict AND requested_generation=$generation AND operation_group_id=$group;",
                cancellationToken, ("$conflict", conflictId), ("$generation", expectedGeneration),
                ("$group", operationGroupId), ("$utc", now));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_group SET status='cancelled',terminal_utc=$utc WHERE operation_group_id=$group;",
                cancellationToken, ("$group", operationGroupId), ("$utc", now));
            retired = true;
        }, cancellationToken);
        return retired;
    }

    public async Task RegisterConflictResolutionAsync(string resolutionId, string conflictId,
        long expectedGeneration, SyncAuthorityConflictResolutionKind kind, string operationGroupId,
        CancellationToken cancellationToken = default)
    {
        foreach (var value in new[] { resolutionId, conflictId, operationGroupId })
            RequireIdentifier(value, nameof(resolutionId));
        if (expectedGeneration < 1) throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        var kindName = kind switch
        {
            SyncAuthorityConflictResolutionKind.KeepLocal => "keep_local",
            SyncAuthorityConflictResolutionKind.KeepDropbox => "keep_dropbox",
            SyncAuthorityConflictResolutionKind.PreserveBoth => "preserve_both",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        await WriteAsync(async (connection, transaction) =>
        {
            var current = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM conflict c
                WHERE c.conflict_id=$id AND c.conflict_generation=$generation AND
                  (c.status IN ('open','resolution_requested') OR
                   (c.status='resolving' AND EXISTS (
                     SELECT 1 FROM conflict_resolution r JOIN operation_group g
                       ON g.operation_group_id=r.operation_group_id
                     WHERE r.conflict_resolution_id=$resolution AND r.conflict_id=c.conflict_id
                       AND r.requested_generation=c.conflict_generation AND r.resolution_kind=$kind
                       AND r.operation_group_id=$group AND r.status='executing'
                       AND g.kind='conflict_resolution' AND g.status='active')));
                """,
                cancellationToken, ("$id", conflictId), ("$generation", expectedGeneration),
                ("$resolution", resolutionId), ("$kind", kindName), ("$group", operationGroupId));
            if (current != 1) throw new InvalidOperationException("The requested conflict generation is stale or not open.");
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO operation_group(operation_group_id,kind,status,created_utc,detail_json) VALUES($id,'conflict_resolution','planned',$utc,$detail);",
                cancellationToken, ("$id", operationGroupId), ("$utc", Utc(DateTimeOffset.UtcNow)),
                ("$detail", JsonSerializer.Serialize(new { conflictId, expectedGeneration, kind = kindName })));
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO conflict_resolution(conflict_resolution_id,conflict_id,requested_generation,resolution_kind,operation_group_id,status,requested_utc) VALUES($id,$conflict,$generation,$kind,$group,'planned',$utc);",
                cancellationToken, ("$id", resolutionId), ("$conflict", conflictId),
                ("$generation", expectedGeneration), ("$kind", kindName), ("$group", operationGroupId),
                ("$utc", Utc(DateTimeOffset.UtcNow)));
            await ExecuteAsync(connection, transaction,
                "UPDATE conflict SET status='resolution_requested' WHERE conflict_id=$id AND status='open';",
                cancellationToken, ("$id", conflictId));
        }, cancellationToken);
    }

    public async Task RegisterOperationGroupAsync(string operationGroupId, string kind, string detailJson,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(operationGroupId, nameof(operationGroupId));
        if (kind is not ("compound" or "cross_pair_move" or "recovery" or "maintenance"))
            throw new ArgumentOutOfRangeException(nameof(kind));
        RequireJson(detailJson, nameof(detailJson));
        await WriteAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO operation_group(operation_group_id,kind,status,created_utc,detail_json) VALUES($id,$kind,'planned',$utc,$detail);",
                cancellationToken, ("$id", operationGroupId), ("$kind", kind),
                ("$utc", Utc(DateTimeOffset.UtcNow)), ("$detail", detailJson));
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM operation_group WHERE operation_group_id=$id AND kind=$kind AND detail_json=$detail;",
                cancellationToken, ("$id", operationGroupId), ("$kind", kind), ("$detail", detailJson));
            if (exact != 1)
                throw new InvalidOperationException("The deterministic operation-group identity collides with different authority.");
        }, cancellationToken);
    }

    public async Task MarkOperationGroupActiveAsync(string operationGroupId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(operationGroupId, nameof(operationGroupId));
        await WriteAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_group SET status='active' WHERE operation_group_id=$id AND status='planned';",
                cancellationToken, ("$id", operationGroupId));
        }, cancellationToken);
    }

    public async Task CompleteOperationGroupAsync(string operationGroupId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(operationGroupId, nameof(operationGroupId));
        await WriteAsync(async (connection, transaction) =>
        {
            var incomplete = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM operation WHERE operation_group_id=$id AND status<>'completed';",
                cancellationToken, ("$id", operationGroupId));
            var members = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM operation WHERE operation_group_id=$id;", cancellationToken,
                ("$id", operationGroupId));
            if (members < 1 || incomplete != 0)
                throw new InvalidOperationException("The operation group cannot complete before every member operation.");
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_group SET status='completed',terminal_utc=$utc WHERE operation_group_id=$id AND status IN ('planned','active','committed');",
                cancellationToken, ("$id", operationGroupId), ("$utc", Utc(DateTimeOffset.UtcNow)));
        }, cancellationToken);
    }

    public async Task CompleteConflictResolutionAsync(string conflictId, string operationGroupId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(conflictId, nameof(conflictId));
        RequireIdentifier(operationGroupId, nameof(operationGroupId));
        await WriteAsync(async (connection, transaction) =>
        {
            var incomplete = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM operation WHERE operation_group_id=$group AND status<>'completed';",
                cancellationToken, ("$group", operationGroupId));
            if (incomplete != 0) throw new InvalidOperationException("Conflict resolution operations are not complete.");
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_group SET status='completed',terminal_utc=$utc WHERE operation_group_id=$group AND status IN ('planned','active','committed');",
                cancellationToken, ("$utc", now), ("$group", operationGroupId));
            await ExecuteAsync(connection, transaction,
                "UPDATE conflict_resolution SET status='completed',terminal_utc=$utc WHERE conflict_id=$conflict AND operation_group_id=$group AND status IN ('planned','executing');",
                cancellationToken, ("$utc", now), ("$conflict", conflictId), ("$group", operationGroupId));
            await ExecuteAsync(connection, transaction,
                "UPDATE conflict SET status='resolved',resolved_utc=$utc WHERE conflict_id=$conflict AND status IN ('resolution_requested','resolving');",
                cancellationToken, ("$utc", now), ("$conflict", conflictId));
        }, cancellationToken);
    }

    public async Task MarkConflictResolutionExecutingAsync(string conflictId, string operationGroupId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(conflictId, nameof(conflictId));
        RequireIdentifier(operationGroupId, nameof(operationGroupId));
        await WriteAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_group SET status='active' WHERE operation_group_id=$group AND status='planned';",
                cancellationToken, ("$group", operationGroupId));
            await ExecuteAsync(connection, transaction,
                "UPDATE conflict_resolution SET status='executing' WHERE conflict_id=$conflict AND operation_group_id=$group AND status='planned';",
                cancellationToken, ("$conflict", conflictId), ("$group", operationGroupId));
            await ExecuteAsync(connection, transaction,
                "UPDATE conflict SET status='resolving' WHERE conflict_id=$conflict AND status='resolution_requested';",
                cancellationToken, ("$conflict", conflictId));
        }, cancellationToken);
    }

    public async Task<string> RecordTombstoneAsync(string tombstoneId, string pairId, string objectId,
        string absentSide, string? priorMembershipId, string? priorObjectVersionId,
        int completeObservationCount, CancellationToken cancellationToken = default)
    {
        foreach (var value in new[] { tombstoneId, pairId, objectId }) RequireIdentifier(value, nameof(tombstoneId));
        if (absentSide is not ("local" or "dropbox" or "both")) throw new ArgumentOutOfRangeException(nameof(absentSide));
        if (completeObservationCount < 1) throw new ArgumentOutOfRangeException(nameof(completeObservationCount));
        await WriteAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO tombstone(tombstone_id,pair_id,object_id,absent_side,prior_membership_id,prior_object_version_id,observed_absent_utc,complete_observation_count,status) VALUES($id,$pair,$object,$side,$membership,$version,$utc,$count,'observed');",
                cancellationToken, ("$id", tombstoneId), ("$pair", pairId), ("$object", objectId),
                ("$side", absentSide), ("$membership", priorMembershipId), ("$version", priorObjectVersionId),
                ("$utc", Utc(DateTimeOffset.UtcNow)), ("$count", completeObservationCount));
        }, cancellationToken);
        return tombstoneId;
    }

    public async Task<SyncAuthorityTombstoneObservation> RecordTombstoneObservationAsync(string tombstoneId,
        string pairId, string objectId, string absentSide, string? priorMembershipId,
        string? priorObjectVersionId, string localScanGenerationId, string dropboxScanGenerationId,
        CancellationToken cancellationToken = default)
    {
        foreach (var value in new[]
                 {
                     tombstoneId, pairId, objectId, localScanGenerationId, dropboxScanGenerationId
                 })
            RequireIdentifier(value, nameof(tombstoneId));
        if (absentSide is not ("local" or "dropbox"))
            throw new ArgumentOutOfRangeException(nameof(absentSide));
        SyncAuthorityTombstoneObservation? result = null;
        await WriteAsync(async (connection, transaction) =>
        {
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO tombstone(tombstone_id,pair_id,object_id,absent_side,prior_membership_id,prior_object_version_id,observed_absent_utc,complete_observation_count,status) VALUES($id,$pair,$object,$side,$membership,$version,$utc,1,'observed');",
                cancellationToken, ("$id", tombstoneId), ("$pair", pairId), ("$object", objectId),
                ("$side", absentSide), ("$membership", priorMembershipId), ("$version", priorObjectVersionId),
                ("$utc", now));
            var retained = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM tombstone WHERE tombstone_id=$id AND pair_id=$pair AND object_id=$object AND absent_side=$side AND prior_membership_id IS $membership AND prior_object_version_id IS $version AND status IN ('observed','grace');",
                cancellationToken, ("$id", tombstoneId), ("$pair", pairId), ("$object", objectId),
                ("$side", absentSide), ("$membership", priorMembershipId), ("$version", priorObjectVersionId));
            if (retained != 1)
                throw new InvalidOperationException("The tombstone identity is not one matching unresolved absence generation.");
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO tombstone_observation(tombstone_id,local_scan_generation_id,dropbox_scan_generation_id,observed_utc) VALUES($id,$local,$dropbox,$utc);",
                cancellationToken, ("$id", tombstoneId), ("$local", localScanGenerationId),
                ("$dropbox", dropboxScanGenerationId), ("$utc", now));
            var count = checked((int)await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM tombstone_observation WHERE tombstone_id=$id;",
                cancellationToken, ("$id", tombstoneId)));
            await ExecuteAsync(connection, transaction,
                "UPDATE tombstone SET complete_observation_count=$count,status=CASE WHEN $count>1 THEN 'grace' ELSE 'observed' END WHERE tombstone_id=$id AND status IN ('observed','grace');",
                cancellationToken, ("$count", count), ("$id", tombstoneId));
            var first = await ScalarStringAsync(connection, transaction,
                "SELECT min(observed_utc) FROM tombstone_observation WHERE tombstone_id=$id;",
                cancellationToken, ("$id", tombstoneId))
                        ?? throw new InvalidDataException("The first tombstone observation was not retained.");
            var last = await ScalarStringAsync(connection, transaction,
                "SELECT max(observed_utc) FROM tombstone_observation WHERE tombstone_id=$id;",
                cancellationToken, ("$id", tombstoneId))
                       ?? throw new InvalidDataException("The last tombstone observation was not retained.");
            var status = await ScalarStringAsync(connection, transaction,
                "SELECT status FROM tombstone WHERE tombstone_id=$id;", cancellationToken, ("$id", tombstoneId))
                         ?? throw new InvalidDataException("The tombstone status was not retained.");
            result = new SyncAuthorityTombstoneObservation(tombstoneId, count,
                DateTimeOffset.Parse(first, CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(last, CultureInfo.InvariantCulture), status);
        }, cancellationToken);
        return result ?? throw new InvalidDataException("The tombstone observation transaction produced no result.");
    }

    public async Task AcceptSynchronizationPointAsync(string synchronizationPointId, string pairId,
        string objectId, string? localVersionId, string? dropboxVersionId, string? localMembershipId,
        string? dropboxMembershipId, CancellationToken cancellationToken = default)
        => await AcceptSynchronizationPointsAsync([new(synchronizationPointId, pairId, objectId, localVersionId,
            dropboxVersionId, localMembershipId, dropboxMembershipId)], cancellationToken);

    // Many points in one transaction: each row keeps the exact single-point proof and writes.
    internal async Task AcceptSynchronizationPointsAsync(IReadOnlyList<SyncAuthorityPointRecord> points,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0) return;
        foreach (var point in points)
        {
            RequireIdentifier(point.SynchronizationPointId, nameof(point.SynchronizationPointId));
            RequireIdentifier(point.PairId, nameof(point.PairId));
            RequireIdentifier(point.ObjectId, nameof(point.ObjectId));
            if (point.LocalVersionId is null && point.DropboxVersionId is null)
                throw new InvalidOperationException("A synchronization point requires at least one observed side.");
        }
        await WriteAsync(async (connection, transaction) =>
        {
            using var commands = new ObservationCommands(connection, transaction);
            foreach (var point in points)
            {
                var generation = await commands.ScalarLongAsync(
                    "SELECT coalesce(max(authority_generation),0)+1 FROM synchronization_point WHERE pair_id=$pair AND object_id=$object;",
                    cancellationToken, ("$pair", point.PairId), ("$object", point.ObjectId));
                await PrepareCurrentCopyPointAsync(commands, point.SynchronizationPointId, point.PairId,
                    point.ObjectId, point.LocalVersionId, point.DropboxVersionId, point.LocalMembershipId,
                    point.DropboxMembershipId, generation, cancellationToken);
                await commands.ExecuteAsync(
                    "INSERT OR IGNORE INTO synchronization_point(synchronization_point_id,pair_id,object_id,local_object_version_id,dropbox_object_version_id,local_membership_id,dropbox_membership_id,accepted_utc,authority_generation) VALUES($id,$pair,$object,$localVersion,$dropboxVersion,$localMembership,$dropboxMembership,$utc,$generation);",
                    cancellationToken, ("$id", point.SynchronizationPointId), ("$pair", point.PairId),
                    ("$object", point.ObjectId), ("$localVersion", point.LocalVersionId),
                    ("$dropboxVersion", point.DropboxVersionId), ("$localMembership", point.LocalMembershipId),
                    ("$dropboxMembership", point.DropboxMembershipId), ("$utc", Utc(DateTimeOffset.UtcNow)),
                    ("$generation", generation));
            }
        }, cancellationToken);
    }

    public async Task RegisterMonitorCursorAsync(string monitorCursorId, string pairId, string rootBindingId,
        string kind, long generation, string sourceIdentityJson, string? cursorValue,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(monitorCursorId, nameof(monitorCursorId));
        RequireIdentifier(pairId, nameof(pairId));
        RequireIdentifier(rootBindingId, nameof(rootBindingId));
        RequireJson(sourceIdentityJson, nameof(sourceIdentityJson));
        if (kind is not ("directory_changes" or "usn" or "dropbox_cursor" or "periodic"))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        await WriteAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO monitor_cursor(monitor_cursor_id,pair_id,root_binding_id,kind,generation,cursor_value,source_identity_json,status,updated_utc)
                VALUES($id,$pair,$root,$kind,$generation,$cursor,$identity,'active',$utc)
                ON CONFLICT(monitor_cursor_id) DO UPDATE SET status='active',updated_utc=excluded.updated_utc
                WHERE monitor_cursor.pair_id=excluded.pair_id
                  AND monitor_cursor.root_binding_id=excluded.root_binding_id
                  AND monitor_cursor.kind=excluded.kind
                  AND monitor_cursor.generation=excluded.generation
                  AND monitor_cursor.source_identity_json=excluded.source_identity_json
                  AND monitor_cursor.status='unavailable';
                """,
                cancellationToken, ("$id", monitorCursorId), ("$pair", pairId), ("$root", rootBindingId),
                ("$kind", kind), ("$generation", generation), ("$cursor", cursorValue),
                ("$identity", sourceIdentityJson), ("$utc", Utc(DateTimeOffset.UtcNow)));
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM monitor_cursor WHERE monitor_cursor_id=$id AND pair_id=$pair AND root_binding_id=$root AND kind=$kind AND generation=$generation AND source_identity_json=$identity AND status='active';",
                cancellationToken, ("$id", monitorCursorId), ("$pair", pairId), ("$root", rootBindingId),
                ("$kind", kind), ("$generation", generation), ("$identity", sourceIdentityJson));
            if (exact != 1)
                throw new InvalidOperationException("The monitor cursor identity collides with different authority.");
        }, cancellationToken);
    }

    public async Task SetMonitorCursorStatusAsync(string monitorCursorId, string pairId, string status,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(monitorCursorId, nameof(monitorCursorId));
        RequireIdentifier(pairId, nameof(pairId));
        if (status is not ("active" or "reset_required" or "unavailable" or "retired"))
            throw new ArgumentOutOfRangeException(nameof(status));
        await WriteAsync(async (connection, transaction) =>
        {
            var allowed = status switch
            {
                "active" => "('active','unavailable','reset_required')",
                "reset_required" => "('active','reset_required')",
                "unavailable" => "('active','unavailable')",
                _ => "('active','unavailable','reset_required','retired')"
            };
            await ExecuteAsync(connection, transaction,
                $"UPDATE monitor_cursor SET status=$status,updated_utc=$utc WHERE monitor_cursor_id=$id AND pair_id=$pair AND status IN {allowed};",
                cancellationToken, ("$status", status), ("$utc", Utc(DateTimeOffset.UtcNow)),
                ("$id", monitorCursorId), ("$pair", pairId));
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM monitor_cursor WHERE monitor_cursor_id=$id AND pair_id=$pair AND status=$status;",
                cancellationToken, ("$id", monitorCursorId), ("$pair", pairId), ("$status", status));
            if (exact != 1) throw new InvalidOperationException("The monitor status transition was not accepted.");
        }, cancellationToken);
    }

    public async Task<SyncAuthorityMonitorCursorSnapshot?> LoadMonitorCursorAsync(string monitorCursorId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(monitorCursorId, nameof(monitorCursorId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pair_id,root_binding_id,kind,generation,cursor_value,source_identity_json,status FROM monitor_cursor WHERE monitor_cursor_id=$id AND status IN ('active','unavailable');";
        command.Parameters.AddWithValue("$id", monitorCursorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SyncAuthorityMonitorCursorSnapshot(monitorCursorId, reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5), reader.GetString(6));
    }

    public async Task<IReadOnlyList<SyncAuthorityMonitorProjection>> LoadMonitorProjectionAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.pair_id,c.kind,c.status,c.updated_utc,
                   (SELECT count(*) FROM dirty_item d WHERE d.pair_id=c.pair_id AND d.status<>'closed'),
                   EXISTS(SELECT 1 FROM monitor_event e WHERE e.monitor_cursor_id=c.monitor_cursor_id
                       AND e.lifetime_class='legacy' AND e.consumed_utc IS NULL)
            FROM monitor_cursor c
            WHERE c.status<>'retired'
            GROUP BY c.monitor_cursor_id,c.pair_id,c.kind,c.status,c.updated_utc
            ORDER BY c.pair_id,c.kind;
            """;
        var result = new List<SyncAuthorityMonitorProjection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new SyncAuthorityMonitorProjection(reader.GetString(0), reader.GetString(1),
                ProjectionMonitorState(reader.GetString(2))+
                    (reader.GetInt64(5)!=0?"; legacy change evidence retained":""),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.GetInt32(4)));
        return result;
    }


    public async Task<SyncAuthorityLease> AcquireOperationLeaseAsync(string operationId, string workerId,
        TimeSpan duration, CancellationToken cancellationToken = default)
        => await AcquireOperationLeaseCoreAsync(operationId, workerId, duration, reconciliationOnly: false,
            cancellationToken);

    /// <summary>
    /// The explicit reconciliation-only lease path. It is usable only for an exact retained issued or
    /// unresolved attempt, or for a retained cleanup or verification operation, after the same identity and
    /// fence checks an ordinary lease passes. It never carries dispatch authority (dispatch_allowed stays 0)
    /// and never claims an execution owner, so it may read, reconcile and record an exact existing receipt or
    /// finish retained cleanup under the existing guards, while an actual request_sent boundary always
    /// rejects it. An expired or missing command is never turned into a fresh normal grant by this path.
    /// </summary>
    public async Task<SyncAuthorityLease> AcquireReconciliationOperationLeaseAsync(string operationId,
        string workerId, TimeSpan duration, CancellationToken cancellationToken = default)
        => await AcquireOperationLeaseCoreAsync(operationId, workerId, duration, reconciliationOnly: true,
            cancellationToken);

    private async Task<SyncAuthorityLease> AcquireOperationLeaseCoreAsync(string operationId, string workerId,
        TimeSpan duration, bool reconciliationOnly, CancellationToken cancellationToken)
    {
        RequireIdentifier(operationId, nameof(operationId));
        RequireIdentifier(workerId, nameof(workerId));
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(duration));

        SyncAuthorityLease? result = null;
        await WriteAsync(async (connection, transaction) =>
        {
            var now = DateTimeOffset.UtcNow;
            await ExecuteAsync(connection, transaction,
                "UPDATE lease SET status='expired' WHERE scope_kind='operation' AND scope_id=$id AND status='active' AND julianday(expires_utc)<=julianday($now);",
                cancellationToken, ("$id", operationId), ("$now", Utc(now)));
            var active = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM lease WHERE scope_kind='operation' AND scope_id=$id AND status='active';",
                cancellationToken, ("$id", operationId));
            if (active != 0) throw new InvalidOperationException("The operation already has an active lease.");
            // Dispatch authority is decided HERE, once, from the operation's own retained execution command.
            // A reconciliation-only lease never carries it. The ordinary path records the exact command the
            // lease is bound to and raises dispatch_allowed only when that command is a current accepted or
            // executing admission of this installation generation; otherwise the lease is recorded with no
            // dispatch authority, which is the retained meaning of "no permission". Lease renewal and every
            // receipt path are structurally unable to change either value afterwards.
            string? boundCommandId = null;
            var dispatchAllowed = 0;
            if (reconciliationOnly)
            {
                var reconciliationEligible = await ScalarLongAsync(connection, transaction,
                    """
                    SELECT count(*) FROM operation o
                    WHERE o.operation_id=$id
                      AND (o.kind IN ('cleanup','reconcile')
                           OR EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id));
                    """, cancellationToken, ("$id", operationId));
                if (reconciliationEligible != 1)
                    throw new InvalidOperationException(
                        "A reconciliation-only lease requires an exact retained issued attempt or a retained cleanup or verification operation.");
            }
            else
            {
                boundCommandId = await ScalarStringAsync(connection, transaction,
                    "SELECT execution_command_id FROM operation WHERE operation_id=$id;", cancellationToken,
                    ("$id", operationId));
                if (boundCommandId is null)
                    throw new InvalidOperationException(
                        "An ordinary lease requires the operation's exact current accepted execution command; an unbound operation is never given a no-dispatch lease as an implicit refusal.");
                var leaseAdmissionPurpose = await ScalarStringAsync(connection, transaction,
                    "SELECT admission_purpose FROM command_request WHERE command_request_id=$id;", cancellationToken,
                    ("$id", boundCommandId));
                // The ordinary path requires the SAME purpose/kind/scope predicate as registration, adoption
                // and the request_sent boundary; a missing, terminal, stale, read-only or foreign-scope
                // admission throws here, before any lease, fence or current-owner mutation.
                if (leaseAdmissionPurpose is null ||
                    !SyncAuthorityCommandPurposeNames.IsWireValue(leaseAdmissionPurpose) ||
                    !await HasCurrentCommandAdmissionAsync(connection, transaction, boundCommandId, cancellationToken) ||
                    !await HasCurrentOperationCommandScopeAsync(connection, transaction, operationId,
                        leaseAdmissionPurpose, cancellationToken))
                    throw new InvalidOperationException(
                        "The operation's execution command is not a current accepted admission covering this operation's kind and pair scope; no ordinary lease can be issued.");
                dispatchAllowed = 1;
            }

            var status = await ScalarStringAsync(connection, transaction,
                "SELECT status FROM operation WHERE operation_id=$id;", cancellationToken, ("$id", operationId));
            if (status is null) throw new InvalidDataException("The operation is not retained.");
            if (await ScalarLongAsync(connection, transaction,
                    "SELECT closure_utc IS NOT NULL FROM operation WHERE operation_id=$id;",
                    cancellationToken, ("$id", operationId)) == 1)
                throw new InvalidOperationException("An exactly closed operation cannot be leased.");
            if (status is "completed" or "cancelled" or "superseded")
                throw new InvalidOperationException("A terminal operation cannot be leased.");
            await RequireCurrentUnissuedConflictPolicyAsync(connection, transaction, operationId, cancellationToken);
            if (status is "ready")
            {
                await RequireSealedOperationGroupAsync(connection, transaction, operationId, cancellationToken);
                var unsatisfiedDependencies = await ScalarLongAsync(connection, transaction,
                    """
                    SELECT count(*) FROM operation_dependency_state d
                    WHERE d.operation_id=$id
                      AND ((d.dependency_kind='must_complete' AND d.prerequisite_status<>'completed')
                        OR (d.dependency_kind='must_commit' AND d.prerequisite_status NOT IN ('committed','cleanup_pending','completed'))
                        OR (d.dependency_kind='must_resolve' AND d.prerequisite_status NOT IN ('committed','cleanup_pending','completed','blocked','cancelled','superseded')));
                    """, cancellationToken, ("$id", operationId));
                if (unsatisfiedDependencies != 0)
                    throw new InvalidOperationException("The operation has unresolved immutable dependencies.");
            }

            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO lease_fence(scope_kind,scope_id,last_token) VALUES('operation',$id,1)
                ON CONFLICT(scope_kind,scope_id) DO UPDATE SET last_token=last_token+1;
                """, cancellationToken, ("$id", operationId));
            var token = await ScalarLongAsync(connection, transaction,
                "SELECT last_token FROM lease_fence WHERE scope_kind='operation' AND scope_id=$id;",
                cancellationToken, ("$id", operationId));
            var leaseId = Guid.NewGuid().ToString("N");
            var expires = now.Add(duration);
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO lease(lease_id,scope_kind,scope_id,worker_id,fencing_token,acquired_utc,expires_utc,
                    status,command_request_id,dispatch_allowed)
                VALUES($lease,'operation',$operation,$worker,$token,$now,$expires,'active',$command,$dispatch);
                """,
                cancellationToken, ("$lease", leaseId), ("$operation", operationId), ("$worker", workerId),
                ("$token", token), ("$now", Utc(now)), ("$expires", Utc(expires)),
                ("$command", boundCommandId), ("$dispatch", dispatchAllowed));
            await ExecuteAsync(connection, transaction,
                """
                UPDATE operation SET current_lease_id=$lease,current_fencing_token=$token,
                    status=CASE WHEN status='ready' THEN 'leased' ELSE status END
                WHERE operation_id=$operation;
                """, cancellationToken, ("$lease", leaseId), ("$token", token), ("$operation", operationId));
            result = new SyncAuthorityLease(leaseId, operationId, workerId, token, now, expires);
        }, cancellationToken);
        return result!;
    }

    public async Task<SyncAuthorityAttempt> StartDispatchedAttemptAsync(string operationId,
        SyncAuthorityLease lease, string requestFingerprint, string effectKind,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(operationId, nameof(operationId));
        RequireHash(requestFingerprint, nameof(requestFingerprint));
        ArgumentException.ThrowIfNullOrWhiteSpace(effectKind);
        SyncAuthorityAttempt? result = null;
        var planningInvalidated = false;
        await WriteAsync(async (connection, transaction) =>
        {
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            if (!await IsCurrentDispatchAuthorityAsync(connection, transaction, operationId, lease, cancellationToken))
                throw new InvalidOperationException("The current lease has no current command dispatch authority.");
            var externallyProgressedAttempts = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM operation_attempt WHERE operation_id=$id AND status<>'verified_not_applied';",
                cancellationToken,
                ("$id", operationId));
            if (externallyProgressedAttempts == 0 && !DisableFinalPlanningAuthorityGuardForTest)
            {
                await RequireSealedOperationGroupAsync(connection, transaction, operationId, cancellationToken);
                var preconditions = await ScalarStringAsync(connection, transaction,
                    "SELECT preconditions_json FROM operation WHERE operation_id=$id;", cancellationToken,
                    ("$id", operationId)) ?? throw new InvalidDataException("The operation preconditions are absent.");
                var planningAuthorities = LoadPlanningAuthorities(preconditions);
                var planningCurrent = true;
                foreach (var item in planningAuthorities)
                    planningCurrent &= await IsPlanningAuthorityCurrentAsync(connection, transaction, item,
                        cancellationToken);
                if (!planningCurrent)
                {
                    var safeToSupersede = await ScalarLongAsync(connection, transaction,
                        """
                        SELECT count(*) FROM operation o
                        WHERE o.operation_id=$id AND o.status='leased'
                          AND NOT EXISTS(SELECT 1 FROM resource_allocation r
                                         WHERE r.operation_id=o.operation_id AND r.status<>'authorized');
                        """, cancellationToken, ("$id", operationId));
                    if (safeToSupersede != 1)
                        throw new InvalidOperationException(
                            "Planning authority changed after an allocation advanced; the operation made no new effect and requires recovery review.");
                    await ExecuteAsync(connection, transaction,
                        """
                        UPDATE operation
                        SET status='superseded',terminal_utc=$utc,last_transition_lease_id=$lease,
                            last_transition_fencing_token=$token
                        WHERE operation_id=$id AND status='leased'
                          AND current_lease_id=$lease AND current_fencing_token=$token;
                        """, cancellationToken, ("$utc", Utc(DateTimeOffset.UtcNow)),
                        ("$lease", lease.LeaseId), ("$token", lease.FencingToken), ("$id", operationId));
                    planningInvalidated = true;
                    return;
                }
            }
            var number = checked((int)(await ScalarLongAsync(connection, transaction,
                "SELECT coalesce(max(attempt_no),0)+1 FROM operation_attempt WHERE operation_id=$id;",
                cancellationToken, ("$id", operationId))));
            var attemptId = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO operation_attempt(attempt_id,operation_id,attempt_no,lease_id,fencing_token,
                    transition_lease_id,transition_fencing_token,request_fingerprint,effect_kind,status,started_utc)
                VALUES($attempt,$operation,$number,$lease,$token,$lease,$token,$fingerprint,$kind,'request_sent',$utc);
                """, cancellationToken, ("$attempt", attemptId), ("$operation", operationId),
                ("$number", number), ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$fingerprint", requestFingerprint), ("$kind", effectKind), ("$utc", Utc(now)));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status='effect_issuing',last_transition_lease_id=$lease,last_transition_fencing_token=$token WHERE operation_id=$id AND status IN ('ready','leased','validating','allocation_authorized','staged');",
                cancellationToken, ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$id", operationId));
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM operation WHERE operation_id=$id AND status='effect_issuing' AND current_lease_id=$lease AND current_fencing_token=$token;",
                cancellationToken, ("$id", operationId), ("$lease", lease.LeaseId),
                ("$token", lease.FencingToken));
            if (exact != 1)
                throw new InvalidOperationException("The dispatched attempt was not bound to the current fenced operation.");
            result = new SyncAuthorityAttempt(attemptId, operationId, number, lease.LeaseId,
                lease.FencingToken, requestFingerprint, effectKind, SyncAuthorityAttemptStatus.RequestSent, now,
                null);
        }, cancellationToken);
        if (planningInvalidated) throw new SyncAuthorityPlanningInvalidatedException(operationId);
        return result!;
    }

    public async Task RecordReceiptAsync(string attemptId, SyncAuthorityLease lease,
        SyncAuthorityReceiptCertainty certainty, string sourceIdentityJson, string destinationIdentityJson,
        string providerMetadataJson, string evidenceDigest, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(attemptId, nameof(attemptId));
        RequireJson(sourceIdentityJson, nameof(sourceIdentityJson));
        RequireJson(destinationIdentityJson, nameof(destinationIdentityJson));
        RequireJson(providerMetadataJson, nameof(providerMetadataJson));
        RequireHash(evidenceDigest, nameof(evidenceDigest));
        await WriteAsync(async (connection, transaction) =>
        {
            var operationId = await ScalarStringAsync(connection, transaction,
                "SELECT operation_id FROM operation_attempt WHERE attempt_id=$id;",
                cancellationToken, ("$id", attemptId)) ?? throw new InvalidDataException("The attempt is not retained.");
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            var receiptId = Guid.NewGuid().ToString("N");
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO effect_receipt(effect_receipt_id,attempt_id,recorded_lease_id,recorded_fencing_token,
                    certainty,source_identity_json,destination_identity_json,provider_metadata_json,evidence_digest,observed_utc)
                VALUES($receipt,$attempt,$lease,$token,$certainty,$source,$destination,$metadata,$digest,$utc);
                """, cancellationToken, ("$receipt", receiptId), ("$attempt", attemptId),
                ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$certainty", SyncAuthorityNames.ReceiptCertainty(certainty)), ("$source", sourceIdentityJson),
                ("$destination", destinationIdentityJson), ("$metadata", providerMetadataJson),
                ("$digest", evidenceDigest), ("$utc", now));
            var attemptStatus = certainty == SyncAuthorityReceiptCertainty.Unknown ? "effect_unknown" : "response_received";
            var operationStatus = certainty == SyncAuthorityReceiptCertainty.Unknown ? "effect_unknown" : "effect_observed";
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_attempt SET status=$status,transition_lease_id=$lease,transition_fencing_token=$token WHERE attempt_id=$id AND status='request_sent';",
                cancellationToken, ("$status", attemptStatus), ("$lease", lease.LeaseId),
                ("$token", lease.FencingToken), ("$id", attemptId));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status=$status,last_transition_lease_id=$lease,last_transition_fencing_token=$token WHERE operation_id=$id AND status='effect_issuing';",
                cancellationToken, ("$status", operationStatus), ("$lease", lease.LeaseId),
                ("$token", lease.FencingToken), ("$id", operationId));
        }, cancellationToken);
    }

    internal async Task<IReadOnlyList<string>> LoadSuccessfulAttemptDestinationsAsync(string attemptId,
        CancellationToken cancellationToken)
    {
        RequireIdentifier(attemptId, nameof(attemptId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT destination_identity_json FROM effect_receipt WHERE attempt_id=$id AND certainty='known_success' ORDER BY observed_utc,effect_receipt_id;";
        command.Parameters.AddWithValue("$id", attemptId);
        var destinations = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) destinations.Add(reader.GetString(0));
        return destinations;
    }

    // The caller has acquired the run lock and re-read retry eligibility. Recheck
    // the exact run membership and durable no-effect guards in the SAME transaction
    // that retires selected plans. A path match alone never conveys ownership.
    internal async Task<int> SupersedeRetryReadyOperationsAsync(string originRunId, string retryCommandId,
        string pairId, long policyRevision, IReadOnlyCollection<(string Path, string OperationId)> targets,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(originRunId, nameof(originRunId));
        RequireIdentifier(retryCommandId, nameof(retryCommandId));
        RequireIdentifier(pairId, nameof(pairId));
        var superseded = 0;
        await WriteAsync(async (connection, transaction) =>
        {
            if (await ScalarLongAsync(connection, transaction,
                    """
                    SELECT count(*) FROM command_request WHERE command_request_id=$command
                      AND status='executing' AND pair_id=$pair
                      AND json_extract(payload_json,'$.retryOfRunId')=$origin
                      AND json_extract(payload_json,'$.policyRevision')=$revision;
                    """, cancellationToken, ("$command", retryCommandId), ("$pair", pairId),
                    ("$origin", originRunId), ("$revision", policyRevision)) != 1)
                throw new InvalidOperationException("Retry handoff requires its executing, revision-bound command.");
            var lineageJson = await ScalarStringAsync(connection, transaction,
                """
                WITH RECURSIVE ancestry(id,payload) AS (
                    SELECT command_request_id,payload_json FROM command_request WHERE command_request_id=$origin
                    UNION SELECT parent.command_request_id,parent.payload_json FROM command_request parent JOIN ancestry child
                      ON json_valid(child.payload) AND parent.command_request_id=json_extract(child.payload,'$.retryOfRunId') LIMIT 101
                ), lineage(id) AS (
                    SELECT id FROM ancestry UNION SELECT child.command_request_id FROM command_request child JOIN lineage parent
                      ON json_valid(child.payload_json) AND json_extract(child.payload_json,'$.retryOfRunId')=parent.id LIMIT 101
                ) SELECT json_group_array(id) FROM lineage;
                """, cancellationToken, ("$origin", originRunId));
            var lineageIds = JsonSerializer.Deserialize<string[]>(lineageJson ?? "[]") ?? [];
            if (lineageIds.Length >= 101)
                throw new InvalidOperationException("Retry lineage is incomplete; open the latest retained run.");
            // Read run membership once in this same write transaction. Planning
            // can stop before the first presentation event, so use the shared
            // durable group binding as well as retained event links.
            var ownedIds = (await LoadRunOperationHistoryCoreAsync(connection, transaction,
                lineageIds, pairId, cancellationToken)).Select(item => item.OperationId).ToHashSet(StringComparer.Ordinal);
            var exactTargets = targets.Distinct().ToArray();
            foreach (var target in exactTargets)
            {
                RequireIdentifier(target.OperationId, nameof(targets));
                if (!ownedIds.Contains(target.OperationId))
                    throw new InvalidOperationException("Retry handoff refuses an operation outside the retained run lineage.");
            }
            var eligible = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var eligibility = connection.CreateCommand())
            {
                eligibility.Transaction = transaction;
                // Same predicates as the individual handoff, evaluated once for
                // this exact selection. Statement-local indexes avoid rescanning
                // all allocation/lease history per file. The write transaction
                // remains held through validation and every supersession.
                eligibility.CommandText = $"""
                    WITH resource_allocation AS MATERIALIZED (SELECT * FROM main.resource_allocation),
                         lease AS MATERIALIZED (SELECT * FROM main.lease)
                    SELECT o.operation_id,o.status FROM json_each($targets) target JOIN operation o
                      ON o.operation_id=json_extract(target.value,'$.OperationId')
                    WHERE o.pair_id=$pair AND o.configuration_revision=$revision
                      AND o.current_lease_id IS NULL
                      AND o.kind IN ('create_local','create_remote','update_local','update_remote',
                                     'delete_local','delete_remote','move_local','move_remote')
                      AND {KnownNoEffectRefusalPredicate}
                      AND EXISTS(SELECT 1 FROM operation_member m WHERE m.operation_id=o.operation_id
                          AND m.role IN ('source','destination','lock')
                          AND m.expected_path_key=json_extract(target.value,'$.Path'))
                      AND NOT EXISTS(SELECT 1 FROM operation_dependency d
                          WHERE d.operation_id=o.operation_id OR d.depends_on_operation_id=o.operation_id);
                    """;
                eligibility.Parameters.AddWithValue("$targets", JsonSerializer.Serialize(exactTargets.Select(item =>
                    new { Path = PilotPath.NormalizeRelative(item.Path), item.OperationId })));
                eligibility.Parameters.AddWithValue("$pair", pairId);
                eligibility.Parameters.AddWithValue("$revision", policyRevision);
                await using var reader = await eligibility.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) eligible.Add(reader.GetString(0), reader.GetString(1));
            }
            // Reject the entire handoff before changing even one reservation.
            if (eligible.Count != exactTargets.Length)
                throw new InvalidOperationException("Retry handoff no longer has exact run-owned, known-no-effect authority for every selected operation. Verify retained outcomes again.");
            foreach (var target in exactTargets)
            {
                if (!eligible.TryGetValue(target.OperationId, out var state))
                    throw new InvalidOperationException("Retry handoff no longer has exact run-owned, known-no-effect authority: " +
                        target.OperationId + " (" + target.Path + "). Verify the retained outcome again.");
                // A blocked/superseded known refusal already holds no ordinary reservation.
                if (state != "ready") continue;
                var now = Utc(DateTimeOffset.UtcNow);
                superseded += await ExecuteCountAsync(connection, transaction,
                    "UPDATE operation SET status='superseded',terminal_utc=$utc WHERE operation_id=$id AND status='ready';",
                    cancellationToken, ("$id", target.OperationId), ("$utc", now));
                await AppendOperationLifecycleAsync(connection, transaction,
                target.OperationId, "retry_owned_supersede", JsonSerializer.Serialize(new { originRunId, retryCommandId,
                        path = target.Path, target.OperationId, policyRevision }),
                now, Guid.NewGuid().ToString("N"), cancellationToken);
            }
        }, cancellationToken);
        return superseded;
    }

    public async Task ReconcileAttemptAsync(string attemptId, SyncAuthorityLease lease,
        SyncAuthorityAttemptStatus terminalAttemptStatus, string detailJson,
        CancellationToken cancellationToken = default)
    {
        if (terminalAttemptStatus is not (SyncAuthorityAttemptStatus.VerifiedSuccess or
            SyncAuthorityAttemptStatus.VerifiedNotApplied or SyncAuthorityAttemptStatus.Blocked))
            throw new ArgumentOutOfRangeException(nameof(terminalAttemptStatus));
        RequireJson(detailJson, nameof(detailJson));
        await WriteAsync(async (connection, transaction) =>
        {
            var operationId = await ScalarStringAsync(connection, transaction,
                "SELECT operation_id FROM operation_attempt WHERE attempt_id=$id;",
                cancellationToken, ("$id", attemptId)) ?? throw new InvalidDataException("The attempt is not retained.");
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            var receipts = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM effect_receipt WHERE attempt_id=$id;", cancellationToken, ("$id", attemptId));
            if (receipts == 0) throw new InvalidOperationException("A dispatched attempt cannot reconcile without a durable receipt.");
            if (terminalAttemptStatus == SyncAuthorityAttemptStatus.VerifiedNotApplied &&
                (await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM effect_receipt WHERE attempt_id=$id AND certainty='known_not_applied';",
                    cancellationToken, ("$id", attemptId)) == 0 ||
                 await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM effect_receipt WHERE attempt_id=$id AND certainty='known_success';",
                    cancellationToken, ("$id", attemptId)) != 0))
                throw new InvalidOperationException("Verified absence requires positive no-effect evidence and cannot override a retained successful effect.");
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                // A request_sent attempt is exactly the interrupted-upload case this reconciler exists for: the
                // request was recorded but no receipt proves an effect, so a proven-absent destination must be
                // able to record its terminal attempt status here.
                "UPDATE operation_attempt SET status=$status,ended_utc=$utc,transition_lease_id=$lease,transition_fencing_token=$token WHERE attempt_id=$id AND status IN ('started','request_sent','response_received','effect_unknown');",
                cancellationToken, ("$status", SyncAuthorityNames.AttemptStatus(terminalAttemptStatus)),
                ("$utc", now), ("$lease", lease.LeaseId), ("$token", lease.FencingToken), ("$id", attemptId));
            var operationStatus = terminalAttemptStatus switch
            {
                SyncAuthorityAttemptStatus.VerifiedSuccess => "committed",
                SyncAuthorityAttemptStatus.VerifiedNotApplied => "ready",
                SyncAuthorityAttemptStatus.Blocked => "blocked",
                _ => throw new ArgumentOutOfRangeException(nameof(terminalAttemptStatus))
            };
            await ExecuteAsync(connection, transaction,
                // effect_issuing is the status an interrupted effect actually carries; without it here the
                // proven-absent disposition never reaches the operation row and its path reservation never clears.
                "UPDATE operation SET status=$status,last_transition_lease_id=$lease,last_transition_fencing_token=$token,block_detail=CASE WHEN $status='blocked' THEN $detail ELSE block_detail END WHERE operation_id=$id AND status IN ('effect_issuing','effect_observed','effect_unknown','reconciling','verifying');",
                cancellationToken, ("$status", operationStatus), ("$lease", lease.LeaseId),
                ("$token", lease.FencingToken), ("$detail", detailJson), ("$id", operationId));
            await AppendOperationLifecycleAsync(connection, transaction,
                operationId, "attempt_reconciled", detailJson,
                now, Guid.NewGuid().ToString("N"), cancellationToken);
        }, cancellationToken);
    }

    public async Task CommitSuccessfulAttemptAsync(string attemptId, SyncAuthorityLease lease,
        string synchronizationPointId, string pairId, string objectId,
        string? localVersionId, string? dropboxVersionId, string? localMembershipId,
        string? dropboxMembershipId, string detailJson, CancellationToken cancellationToken = default)
    {
        foreach (var value in new[] { attemptId, synchronizationPointId, pairId, objectId })
            RequireIdentifier(value, nameof(attemptId));
        if (localVersionId is null && dropboxVersionId is null)
            throw new InvalidOperationException("A successful effect must commit at least one exact successor version.");
        RequireJson(detailJson, nameof(detailJson));
        await WriteAsync(async (connection, transaction) =>
        {
            var operationId = await ScalarStringAsync(connection, transaction,
                "SELECT operation_id FROM operation_attempt WHERE attempt_id=$id;",
                cancellationToken, ("$id", attemptId)) ?? throw new InvalidDataException("The attempt is not retained.");
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            var knownSuccess = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM effect_receipt WHERE attempt_id=$id AND certainty='known_success';",
                cancellationToken, ("$id", attemptId));
            if (knownSuccess < 1) throw new InvalidOperationException("A successful commit requires a durable known-success receipt.");
            var generation = await ScalarLongAsync(connection, transaction,
                "SELECT coalesce(max(authority_generation),0)+1 FROM synchronization_point WHERE pair_id=$pair AND object_id=$object;",
                cancellationToken, ("$pair", pairId), ("$object", objectId));
            var now = Utc(DateTimeOffset.UtcNow);
            await PrepareCurrentCopyPointAsync(connection, transaction, synchronizationPointId, pairId, objectId,
                localVersionId, dropboxVersionId, localMembershipId, dropboxMembershipId, generation, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO synchronization_point(synchronization_point_id,pair_id,object_id,local_object_version_id,dropbox_object_version_id,local_membership_id,dropbox_membership_id,accepted_utc,authority_generation) VALUES($id,$pair,$object,$localVersion,$dropboxVersion,$localMembership,$dropboxMembership,$utc,$generation);",
                cancellationToken, ("$id", synchronizationPointId), ("$pair", pairId), ("$object", objectId),
                ("$localVersion", localVersionId), ("$dropboxVersion", dropboxVersionId),
                ("$localMembership", localMembershipId), ("$dropboxMembership", dropboxMembershipId),
                ("$utc", now), ("$generation", generation));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_attempt SET status='verified_success',ended_utc=$utc,transition_lease_id=$lease,transition_fencing_token=$token WHERE attempt_id=$id AND status IN ('response_received','effect_unknown');",
                cancellationToken, ("$utc", now), ("$lease", lease.LeaseId),
                ("$token", lease.FencingToken), ("$id", attemptId));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status='committed',last_transition_lease_id=$lease,last_transition_fencing_token=$token WHERE operation_id=$id AND status IN ('effect_observed','effect_unknown','reconciling','verifying');",
                cancellationToken, ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$id", operationId));
            await BindCommittedPointCoreAsync(connection, transaction, operationId, synchronizationPointId,
                pairId, objectId, localVersionId, dropboxVersionId, localMembershipId, dropboxMembershipId,
                generation, cancellationToken);
            await AppendOperationLifecycleAsync(connection, transaction,
                operationId, "effect_committed", detailJson,
                now, Guid.NewGuid().ToString("N"), cancellationToken);
        }, cancellationToken);
    }

    public async Task CommitSuccessfulAuxiliaryAttemptAsync(string attemptId, SyncAuthorityLease lease,
        string detailJson, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(attemptId, nameof(attemptId));
        RequireJson(detailJson, nameof(detailJson));
        await WriteAsync(async (connection, transaction) =>
        {
            var operationId = await ScalarStringAsync(connection, transaction,
                "SELECT operation_id FROM operation_attempt WHERE attempt_id=$id;",
                cancellationToken, ("$id", attemptId)) ?? throw new InvalidDataException("The attempt is not retained.");
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            var valid = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM operation o
                JOIN effect_receipt r ON r.attempt_id=$attempt
                WHERE o.operation_id=$operation AND (o.kind IN ('resolve_conflict','cross_pair_move')
                    OR (o.kind='delete_local' AND json_type(o.preconditions_json,'$.completedCopyMove')='object'))
                  AND r.certainty='known_success';
                """, cancellationToken, ("$attempt", attemptId), ("$operation", operationId));
            if (valid < 1)
                throw new InvalidOperationException("An auxiliary effect commit requires a known-success receipt.");
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_attempt SET status='verified_success',ended_utc=$utc,transition_lease_id=$lease,transition_fencing_token=$token WHERE attempt_id=$id AND status IN ('response_received','effect_unknown');",
                cancellationToken, ("$utc", now), ("$lease", lease.LeaseId),
                ("$token", lease.FencingToken), ("$id", attemptId));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status='committed',last_transition_lease_id=$lease,last_transition_fencing_token=$token WHERE operation_id=$id AND status IN ('effect_observed','effect_unknown','reconciling','verifying');",
                cancellationToken, ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$id", operationId));
            await AppendOperationLifecycleAsync(connection, transaction,
                operationId, "auxiliary_effect_committed", detailJson,
                now, Guid.NewGuid().ToString("N"), cancellationToken);
        }, cancellationToken);
    }

    public async Task CommitSuccessfulDeletionAttemptAsync(string attemptId, SyncAuthorityLease lease,
        string tombstoneId, string pairId, string objectId, string detailJson,
        CancellationToken cancellationToken = default)
    {
        foreach (var value in new[] { attemptId, tombstoneId, pairId, objectId })
            RequireIdentifier(value, nameof(attemptId));
        RequireJson(detailJson, nameof(detailJson));
        await WriteAsync(async (connection, transaction) =>
        {
            var operationId = await ScalarStringAsync(connection, transaction,
                "SELECT operation_id FROM operation_attempt WHERE attempt_id=$id;",
                cancellationToken, ("$id", attemptId)) ?? throw new InvalidDataException("The attempt is not retained.");
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            var knownSuccess = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM effect_receipt WHERE attempt_id=$id AND certainty='known_success';",
                cancellationToken, ("$id", attemptId));
            if (knownSuccess < 1)
                throw new InvalidOperationException("A deletion commit requires a durable known-success receipt.");
            var retainedTombstone = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM tombstone WHERE tombstone_id=$tombstone AND pair_id=$pair AND object_id=$object AND status IN ('observed','grace','propagation_planned','retained');",
                cancellationToken, ("$tombstone", tombstoneId), ("$pair", pairId), ("$object", objectId));
            if (retainedTombstone != 1)
                throw new InvalidOperationException("The deletion is not bound to one unresolved retained tombstone.");
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                "UPDATE tombstone SET status='resolved' WHERE tombstone_id=$tombstone AND status IN ('observed','grace','propagation_planned','retained');",
                cancellationToken, ("$tombstone", tombstoneId));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_attempt SET status='verified_success',ended_utc=$utc,transition_lease_id=$lease,transition_fencing_token=$token WHERE attempt_id=$id AND status IN ('response_received','effect_unknown');",
                cancellationToken, ("$utc", now), ("$lease", lease.LeaseId),
                ("$token", lease.FencingToken), ("$id", attemptId));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status='committed',last_transition_lease_id=$lease,last_transition_fencing_token=$token WHERE operation_id=$id AND status IN ('effect_observed','effect_unknown','reconciling','verifying');",
                cancellationToken, ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$id", operationId));
            await BindCommittedDeletionCoreAsync(connection, transaction, operationId, tombstoneId,
                pairId, objectId, cancellationToken);
            await AppendOperationLifecycleAsync(connection, transaction,
                operationId, "deletion_committed", detailJson,
                now, Guid.NewGuid().ToString("N"), cancellationToken);
        }, cancellationToken);
    }

    public async Task CommitReconciledIntermediateAttemptAsync(string attemptId, SyncAuthorityLease lease,
        string detailJson, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(attemptId, nameof(attemptId));
        RequireJson(detailJson, nameof(detailJson));
        await WriteAsync(async (connection, transaction) =>
        {
            var operationId = await ScalarStringAsync(connection, transaction,
                "SELECT operation_id FROM operation_attempt WHERE attempt_id=$id;",
                cancellationToken, ("$id", attemptId)) ?? throw new InvalidDataException("The attempt is not retained.");
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            var receipts = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM effect_receipt WHERE attempt_id=$id;", cancellationToken, ("$id", attemptId));
            if (receipts < 1)
                throw new InvalidOperationException("A reconciled intermediate effect requires a durable receipt boundary.");
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_attempt SET status='verified_success',ended_utc=$utc,transition_lease_id=$lease,transition_fencing_token=$token WHERE attempt_id=$id AND status IN ('response_received','effect_unknown');",
                cancellationToken, ("$utc", now), ("$lease", lease.LeaseId),
                ("$token", lease.FencingToken), ("$id", attemptId));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status=CASE WHEN status='effect_unknown' THEN 'ready' ELSE 'validating' END,last_transition_lease_id=$lease,last_transition_fencing_token=$token WHERE operation_id=$id AND status IN ('effect_observed','effect_unknown');",
                cancellationToken, ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$id", operationId));
            await AppendOperationLifecycleAsync(connection, transaction,
                operationId, "intermediate_effect_reconciled", detailJson,
                now, Guid.NewGuid().ToString("N"), cancellationToken);
        }, cancellationToken);
    }

    public async Task CommitReconciledDeletionAttemptAsync(string attemptId, SyncAuthorityLease lease,
        string tombstoneId, string pairId, string objectId, string detailJson,
        CancellationToken cancellationToken = default)
    {
        foreach (var value in new[] { attemptId, tombstoneId, pairId, objectId })
            RequireIdentifier(value, nameof(attemptId));
        RequireJson(detailJson, nameof(detailJson));
        await WriteAsync(async (connection, transaction) =>
        {
            var operationId = await ScalarStringAsync(connection, transaction,
                "SELECT operation_id FROM operation_attempt WHERE attempt_id=$id;",
                cancellationToken, ("$id", attemptId)) ?? throw new InvalidDataException("The attempt is not retained.");
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            var receipts = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM effect_receipt WHERE attempt_id=$id;", cancellationToken, ("$id", attemptId));
            if (receipts < 1)
                throw new InvalidOperationException("A reconciled deletion requires a durable receipt boundary.");
            var retainedTombstone = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM tombstone WHERE tombstone_id=$tombstone AND pair_id=$pair AND object_id=$object AND status IN ('observed','grace','propagation_planned','retained');",
                cancellationToken, ("$tombstone", tombstoneId), ("$pair", pairId), ("$object", objectId));
            if (retainedTombstone != 1)
                throw new InvalidOperationException("The reconciled deletion is not bound to one unresolved tombstone.");
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                "UPDATE tombstone SET status='resolved' WHERE tombstone_id=$tombstone AND status IN ('observed','grace','propagation_planned','retained');",
                cancellationToken, ("$tombstone", tombstoneId));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_attempt SET status='verified_success',ended_utc=$utc,transition_lease_id=$lease,transition_fencing_token=$token WHERE attempt_id=$id AND status IN ('response_received','effect_unknown');",
                cancellationToken, ("$utc", now), ("$lease", lease.LeaseId),
                ("$token", lease.FencingToken), ("$id", attemptId));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status='committed',last_transition_lease_id=$lease,last_transition_fencing_token=$token WHERE operation_id=$id AND status IN ('effect_observed','effect_unknown','reconciling','verifying');",
                cancellationToken, ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$id", operationId));
            await BindCommittedDeletionCoreAsync(connection, transaction, operationId, tombstoneId,
                pairId, objectId, cancellationToken);
            await AppendOperationLifecycleAsync(connection, transaction,
                operationId, "deletion_reconciled", detailJson,
                now, Guid.NewGuid().ToString("N"), cancellationToken);
        }, cancellationToken);
    }

    public async Task CommitReconciledSuccessfulAttemptAsync(string attemptId, SyncAuthorityLease lease,
        string synchronizationPointId, string pairId, string objectId,
        string? localVersionId, string? dropboxVersionId, string? localMembershipId,
        string? dropboxMembershipId, string detailJson, CancellationToken cancellationToken = default)
    {
        foreach (var value in new[] { attemptId, synchronizationPointId, pairId, objectId })
            RequireIdentifier(value, nameof(attemptId));
        if (localVersionId is null && dropboxVersionId is null)
            throw new InvalidOperationException("A reconciled effect must commit at least one exact successor version.");
        RequireJson(detailJson, nameof(detailJson));
        await WriteAsync(async (connection, transaction) =>
        {
            var operationId = await ScalarStringAsync(connection, transaction,
                "SELECT operation_id FROM operation_attempt WHERE attempt_id=$id;",
                cancellationToken, ("$id", attemptId)) ?? throw new InvalidDataException("The attempt is not retained.");
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            var receipts = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM effect_receipt WHERE attempt_id=$id;", cancellationToken, ("$id", attemptId));
            if (receipts < 1)
                throw new InvalidOperationException("A reconciled successful effect requires a durable receipt boundary.");
            var generation = await ScalarLongAsync(connection, transaction,
                "SELECT coalesce(max(authority_generation),0)+1 FROM synchronization_point WHERE pair_id=$pair AND object_id=$object;",
                cancellationToken, ("$pair", pairId), ("$object", objectId));
            var now = Utc(DateTimeOffset.UtcNow);
            await PrepareCurrentCopyPointAsync(connection, transaction, synchronizationPointId, pairId, objectId,
                localVersionId, dropboxVersionId, localMembershipId, dropboxMembershipId, generation, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO synchronization_point(synchronization_point_id,pair_id,object_id,local_object_version_id,dropbox_object_version_id,local_membership_id,dropbox_membership_id,accepted_utc,authority_generation) VALUES($id,$pair,$object,$localVersion,$dropboxVersion,$localMembership,$dropboxMembership,$utc,$generation);",
                cancellationToken, ("$id", synchronizationPointId), ("$pair", pairId), ("$object", objectId),
                ("$localVersion", localVersionId), ("$dropboxVersion", dropboxVersionId),
                ("$localMembership", localMembershipId), ("$dropboxMembership", dropboxMembershipId),
                ("$utc", now), ("$generation", generation));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_attempt SET status='verified_success',ended_utc=$utc,transition_lease_id=$lease,transition_fencing_token=$token WHERE attempt_id=$id AND status IN ('response_received','effect_unknown');",
                cancellationToken, ("$utc", now), ("$lease", lease.LeaseId),
                ("$token", lease.FencingToken), ("$id", attemptId));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status='committed',last_transition_lease_id=$lease,last_transition_fencing_token=$token WHERE operation_id=$id AND status IN ('effect_observed','effect_unknown','reconciling','verifying');",
                cancellationToken, ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$id", operationId));
            await BindCommittedPointCoreAsync(connection, transaction, operationId, synchronizationPointId,
                pairId, objectId, localVersionId, dropboxVersionId, localMembershipId, dropboxMembershipId,
                generation, cancellationToken);
            await AppendOperationLifecycleAsync(connection, transaction,
                operationId, "effect_reconciled_and_committed", detailJson,
                now, Guid.NewGuid().ToString("N"), cancellationToken);
        }, cancellationToken);
    }

    public async Task CompleteIntermediateAttemptAsync(string attemptId, SyncAuthorityLease lease,
        string detailJson, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(attemptId, nameof(attemptId));
        RequireJson(detailJson, nameof(detailJson));
        await WriteAsync(async (connection, transaction) =>
        {
            var operationId = await ScalarStringAsync(connection, transaction,
                "SELECT operation_id FROM operation_attempt WHERE attempt_id=$id;",
                cancellationToken, ("$id", attemptId)) ?? throw new InvalidDataException("The attempt is not retained.");
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            var knownSuccess = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM effect_receipt WHERE attempt_id=$id AND certainty='known_success';",
                cancellationToken, ("$id", attemptId));
            if (knownSuccess < 1) throw new InvalidOperationException("An intermediate effect requires a known-success receipt.");
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_attempt SET status='verified_success',ended_utc=$utc,transition_lease_id=$lease,transition_fencing_token=$token WHERE attempt_id=$id AND status='response_received';",
                cancellationToken, ("$utc", now), ("$lease", lease.LeaseId),
                ("$token", lease.FencingToken), ("$id", attemptId));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status='validating',last_transition_lease_id=$lease,last_transition_fencing_token=$token WHERE operation_id=$id AND status='effect_observed';",
                cancellationToken, ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$id", operationId));
            await AppendOperationLifecycleAsync(connection, transaction,
                operationId, "intermediate_effect_verified", detailJson,
                now, Guid.NewGuid().ToString("N"), cancellationToken);
        }, cancellationToken);
    }

    public async Task ReconcileIntermediateNotAppliedAttemptAsync(string attemptId,
        SyncAuthorityLease lease, string detailJson, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(attemptId, nameof(attemptId));
        RequireJson(detailJson, nameof(detailJson));
        await WriteAsync(async (connection, transaction) =>
        {
            var operationId = await ScalarStringAsync(connection, transaction,
                "SELECT operation_id FROM operation_attempt WHERE attempt_id=$id;",
                cancellationToken, ("$id", attemptId)) ??
                throw new InvalidDataException("The attempt is not retained.");
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            var knownNotApplied = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM effect_receipt WHERE attempt_id=$id AND certainty='known_not_applied';",
                cancellationToken, ("$id", attemptId));
            if (knownNotApplied < 1)
                throw new InvalidOperationException(
                    "An intermediate retry requires a durable known-not-applied receipt.");
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_attempt SET status='verified_not_applied',ended_utc=$utc,transition_lease_id=$lease,transition_fencing_token=$token WHERE attempt_id=$id AND status='response_received';",
                cancellationToken, ("$utc", now), ("$lease", lease.LeaseId),
                ("$token", lease.FencingToken), ("$id", attemptId));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status='validating',last_transition_lease_id=$lease,last_transition_fencing_token=$token WHERE operation_id=$id AND status='effect_observed';",
                cancellationToken, ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$id", operationId));
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM operation o JOIN operation_attempt a ON a.operation_id=o.operation_id WHERE a.attempt_id=$attempt AND a.status='verified_not_applied' AND o.operation_id=$operation AND o.status='validating' AND o.current_lease_id=$lease AND o.current_fencing_token=$token;",
                cancellationToken, ("$attempt", attemptId), ("$operation", operationId),
                ("$lease", lease.LeaseId), ("$token", lease.FencingToken));
            if (exact != 1)
                throw new InvalidOperationException(
                    "The intermediate known-not-applied attempt did not retain its exact fenced validating state.");
            await AppendOperationLifecycleAsync(connection, transaction,
                operationId, "intermediate_effect_verified_not_applied", detailJson,
                now, Guid.NewGuid().ToString("N"), cancellationToken);
        }, cancellationToken);
    }

    public async Task RegisterRecoveryArtifactAsync(string recoveryArtifactId, string operationId,
        string objectId, string? allocationId, string side, string identityJson, string contentHash, long size,
        CancellationToken cancellationToken = default)
    {
        foreach (var value in new[] { recoveryArtifactId, operationId, objectId })
            RequireIdentifier(value, nameof(recoveryArtifactId));
        if (allocationId is not null) RequireIdentifier(allocationId, nameof(allocationId));
        if (side is not ("local" or "dropbox")) throw new ArgumentOutOfRangeException(nameof(side));
        RequireJson(identityJson, nameof(identityJson));
        RequireHash(contentHash, nameof(contentHash));
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        await WriteAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO recovery_artifact(recovery_artifact_id,operation_id,object_id,allocation_id,side,identity_json,content_hash,size_bytes,status,retained_utc) VALUES($id,$operation,$object,$allocation,$side,$identity,$hash,$size,'retained',$utc);",
                cancellationToken, ("$id", recoveryArtifactId), ("$operation", operationId),
                ("$object", objectId), ("$allocation", (object?)allocationId ?? DBNull.Value), ("$side", side),
                ("$identity", identityJson), ("$hash", contentHash), ("$size", size),
                ("$utc", Utc(DateTimeOffset.UtcNow)));
        }, cancellationToken);
    }

    // Records an already verified manual-restore preimage before the destination can be replaced.
    // This is evidence of the completed retention copy, not queued permission to replay a restore.
    internal async Task RegisterManualRecoveryCopyAsync(string id, string commandId, string pairId,
        long policyRevision, string profileId, string relativePath, SyncArchiveResult copy,
        CancellationToken cancellationToken)
    {
        foreach (var value in new[] { id, commandId, pairId, profileId }) RequireIdentifier(value, nameof(id));
        RequireHash(copy.ContentHash, nameof(copy));
        if (copy.Size < 0 || string.IsNullOrWhiteSpace(copy.FileId) || string.IsNullOrWhiteSpace(copy.Revision))
            throw new InvalidDataException("The retained preimage is missing verified identity metadata.");
        var path = PilotPath.NormalizeRelative(relativePath);
        var identity = JsonSerializer.Serialize(new { copy.FileId, copy.Revision, copy.RecoveryPath, copy.Size,
            copy.ContentHash, originalPath = path, provenance = "manual_restore_preimage", commandId });
        await WriteAsync(async (connection, transaction) =>
        {
            if (await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM command_request WHERE command_request_id=$command AND pair_id=$pair AND command_kind='reconcile' AND status='executing';",
                    cancellationToken, ("$command", commandId), ("$pair", pairId)) != 1 ||
                await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM sync_pair WHERE pair_id=$pair AND configuration_revision=$revision;",
                    cancellationToken, ("$pair", pairId), ("$revision", policyRevision)) != 1)
                throw new InvalidOperationException("The manual restore command or pair policy changed before retention was recorded.");
            var utc = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO logical_object(object_id,object_kind,created_utc) VALUES($id,'file',$utc);",
                cancellationToken, ("$id", id), ("$utc", utc));
            await ExecuteAsync(connection, transaction,
                """
                INSERT OR IGNORE INTO operation(operation_id,pair_id,object_id,kind,status,priority,
                    configuration_revision,capability_profile_id,idempotency_key,preconditions_json,created_utc,terminal_utc)
                VALUES($id,$pair,$id,'reconcile','completed',0,$revision,$profile,$id,$identity,$utc,$utc);
                INSERT OR IGNORE INTO recovery_artifact(recovery_artifact_id,operation_id,object_id,side,
                    identity_json,content_hash,size_bytes,status,retained_utc)
                VALUES($id,$id,$id,'dropbox',$identity,$hash,$size,'retained',$utc);
                """, cancellationToken, ("$id", id), ("$pair", pairId), ("$revision", policyRevision),
                ("$profile", profileId), ("$identity", identity), ("$hash", copy.ContentHash), ("$size", copy.Size), ("$utc", utc));
            if (await ScalarLongAsync(connection, transaction,
                    """
                    SELECT count(*) FROM recovery_artifact ra
                    JOIN operation o ON o.operation_id=ra.operation_id
                    JOIN logical_object lo ON lo.object_id=o.object_id
                    WHERE ra.recovery_artifact_id=$id AND ra.operation_id=$id AND ra.object_id=$id
                      AND ra.side='dropbox' AND ra.status='retained' AND ra.allocation_id IS NULL
                      AND o.pair_id=$pair AND o.object_id=$id AND o.kind='reconcile' AND o.status='completed'
                      AND o.configuration_revision=$revision AND o.capability_profile_id=$profile
                      AND o.idempotency_key=$id AND o.preconditions_json=$identity AND o.terminal_utc IS NOT NULL
                      AND lo.object_kind='file' AND ra.identity_json=$identity
                      AND ra.content_hash=$hash AND ra.size_bytes=$size;
                    """,
                    cancellationToken, ("$id", id), ("$pair", pairId), ("$identity", identity),
                    ("$hash", copy.ContentHash), ("$size", copy.Size),
                    ("$revision", policyRevision), ("$profile", profileId)) != 1)
                throw new InvalidOperationException("The manual recovery record collides with different retained evidence.");
        }, cancellationToken);
    }

    public async Task CompleteOperationAsync(SyncAuthorityLease lease, string detailJson,
        CancellationToken cancellationToken = default)
    {
        RequireJson(detailJson, nameof(detailJson));
        await WriteAsync(async (connection, transaction) =>
        {
            await RequireFenceAsync(connection, transaction, lease.OperationId, lease, cancellationToken);
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status='completed',terminal_utc=$utc,last_transition_lease_id=$lease,last_transition_fencing_token=$token WHERE operation_id=$id AND status IN ('committed','cleanup_pending');",
                cancellationToken, ("$utc", now), ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$id", lease.OperationId));
            await CaptureCurrentCopyCompletionAsync(connection, transaction, lease.OperationId, cancellationToken);
            await RetireCompletedCopyPredecessorAsync(connection, transaction, lease.OperationId, cancellationToken);
            await AppendOperationLifecycleAsync(connection, transaction,
                lease.OperationId, "operation_completed", detailJson,
                now, Guid.NewGuid().ToString("N"), cancellationToken);
        }, cancellationToken);
    }

    /// <summary>root718/723: the committed/cleanup_pending -&gt; completed transition, extracted so the atomic
    /// published edit can terminalise ONLY the pair's own proved prepared allocation inside its own transaction.
    /// The caller supplies the exact operation id and the retained lease identity it validated; the core still
    /// requires the operation to be genuinely committed, so 'completed' is never set without its prerequisite.</summary>
    private async Task CompleteOperationCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string operationId, string leaseId, long fencingToken, string detailJson,
        CancellationToken cancellationToken)
    {
        var now = Utc(DateTimeOffset.UtcNow);
        if (string.IsNullOrEmpty(leaseId))
        {
            // A prepared allocation's own lease was released (and its row removed) before the atomic
            // publication, so rewriting last_transition_lease_id would break its FK to lease. Complete the
            // operation without touching that column; its retained value stays exactly as prepared.
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status='completed',terminal_utc=$utc WHERE operation_id=$id AND status IN ('committed','cleanup_pending');",
                cancellationToken, ("$utc", now), ("$id", operationId));
        }
        else
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status='completed',terminal_utc=$utc,last_transition_lease_id=$lease,last_transition_fencing_token=$token WHERE operation_id=$id AND status IN ('committed','cleanup_pending');",
                cancellationToken, ("$utc", now), ("$lease", leaseId), ("$token", fencingToken),
                ("$id", operationId));
        }
        var completed = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM operation WHERE operation_id=$id AND status='completed';", cancellationToken,
            ("$id", operationId));
        if (completed != 1) throw new InvalidOperationException(
            "The prepared control allocation was not completed by its own committed transition.");
        await AppendOperationLifecycleAsync(connection, transaction,
                operationId, "operation_completed", detailJson,
                now, Guid.NewGuid().ToString("N"), cancellationToken);
    }

    public async Task MarkCleanupPendingAsync(SyncAuthorityLease lease, string detailJson,
        CancellationToken cancellationToken = default)
    {
        RequireJson(detailJson, nameof(detailJson));
        await WriteAsync(async (connection, transaction) =>
        {
            await RequireFenceAsync(connection, transaction, lease.OperationId, lease, cancellationToken);
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET status='cleanup_pending',last_transition_lease_id=$lease,last_transition_fencing_token=$token WHERE operation_id=$id AND status='committed';",
                cancellationToken, ("$lease", lease.LeaseId), ("$token", lease.FencingToken),
                ("$id", lease.OperationId));
            await AppendOperationLifecycleAsync(connection, transaction,
                lease.OperationId, "cleanup_started", detailJson,
                now, Guid.NewGuid().ToString("N"), cancellationToken);
        }, cancellationToken);
    }

    public async Task TransitionAllocationAsync(string allocationId, SyncAuthorityLease lease,
        SyncAuthorityAllocationStatus status, string? boundIdentity = null, string? contentHash = null,
        long? size = null, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(allocationId, nameof(allocationId));
        if (contentHash is not null) RequireHash(contentHash, nameof(contentHash));
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        await WriteAsync(async (connection, transaction) =>
        {
            var operationId = await ScalarStringAsync(connection, transaction,
                "SELECT operation_id FROM resource_allocation WHERE allocation_id=$id;",
                cancellationToken, ("$id", allocationId)) ?? throw new InvalidDataException("The allocation is not retained.");
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            await ExecuteAsync(connection, transaction,
                """
                UPDATE resource_allocation
                SET status=$status,
                    transition_lease_id=$lease,
                    transition_fencing_token=$token,
                    bound_identity=coalesce($identity,bound_identity),
                    content_hash=coalesce($hash,content_hash),
                    size_bytes=coalesce($size,size_bytes)
                WHERE allocation_id=$id;
                """, cancellationToken, ("$status", SyncAuthorityNames.AllocationStatus(status)),
                ("$lease", lease.LeaseId), ("$token", lease.FencingToken), ("$identity", boundIdentity),
                ("$hash", contentHash), ("$size", size), ("$id", allocationId));
        }, cancellationToken);
    }

    // Admission and release run under the same SQLite writer transaction: whichever
    // wins first prevents the other from issuing a provider effect against this source.
    // Revision catalog keys are not retained artifacts and keep their separate route.
    private static async Task RequireRecoverySourceAvailableAsync(SqliteConnection connection,
        SqliteTransaction transaction, string recoveryArtifactId, CancellationToken cancellationToken)
    {
        var unavailable = await ScalarLongAsync(connection, transaction,
            "SELECT count(*) FROM recovery_artifact WHERE recovery_artifact_id=$id AND status NOT IN ('retained','verified');",
            cancellationToken, ("$id", recoveryArtifactId));
        if (unavailable != 0)
            throw new InvalidOperationException(
                "This recovery copy has been authorized for removal or removed; select another retained copy.");
    }

    private static async Task RequireNoRecoverySourceConsumersAsync(SqliteConnection connection,
        SqliteTransaction transaction, string recoveryArtifactId, CancellationToken cancellationToken)
    {
        var consumers = await ScalarLongAsync(connection, transaction,
            """
            WITH sources(source_id) AS (
              SELECT $id
              UNION
              SELECT json_extract(identity_json,'$.sourceRecoveryArtifactId')
              FROM recovery_artifact WHERE recovery_artifact_id=$id
                AND json_type(identity_json,'$.sourceRecoveryArtifactId')='text'
            )
            SELECT
              (SELECT count(*) FROM command_request c
               WHERE c.target_id IN (SELECT source_id FROM sources)
                 AND json_extract(c.payload_json,'$.action')='restore_recovery'
                 AND (c.status IN ('requested','accepted','executing')
                      OR c.status='blocked' AND NOT EXISTS(
                        SELECT 1 FROM operation terminal
                        WHERE json_extract(terminal.preconditions_json,'$.restoreCommandId')=c.command_request_id
                          AND terminal.status IN ('completed','cancelled','superseded')
                          AND NOT EXISTS(SELECT 1 FROM operation_attempt a
                            WHERE a.operation_id=terminal.operation_id
                              AND a.status IN ('started','request_sent','response_received','effect_unknown')))))
              + (SELECT count(*) FROM operation o
                 WHERE (o.status NOT IN ('completed','cancelled','superseded')
                        OR EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id
                                  AND a.status IN ('started','request_sent','response_received','effect_unknown')))
                   AND (json_extract(o.preconditions_json,'$.recoveryArtifactId') IN (SELECT source_id FROM sources)
                        OR EXISTS(SELECT 1 FROM operation_member m WHERE m.operation_id=o.operation_id
                                  AND (json_extract(m.expected_state_json,'$.sourceRecoveryArtifactId') IN (SELECT source_id FROM sources)
                                       OR json_extract(m.expected_state_json,'$.recoveryArtifactId') IN (SELECT source_id FROM sources)))))
              + (SELECT count(*) FROM operation_attempt a JOIN recovery_artifact r ON r.operation_id=a.operation_id
                 WHERE r.recovery_artifact_id=$id
                   AND a.status IN ('started','request_sent','response_received','effect_unknown'));
            """, cancellationToken, ("$id", recoveryArtifactId));
        if (consumers != 0)
            throw new InvalidOperationException(
                "This recovery copy is still required by a pending or unresolved restore. Resolve that operation before removing it.");
    }

    public async Task AuthorizeRecoveryArtifactReleaseAsync(string recoveryArtifactId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(recoveryArtifactId, nameof(recoveryArtifactId));
        await WriteAsync(async (connection, transaction) =>
        {
            await RequireNoRecoverySourceConsumersAsync(connection, transaction, recoveryArtifactId,
                cancellationToken);
            await ExecuteAsync(connection, transaction,
                """
                UPDATE recovery_artifact
                SET status='release_authorized'
                WHERE recovery_artifact_id=$id AND status IN ('retained','verified')
                  AND EXISTS(SELECT 1 FROM operation o WHERE o.operation_id=recovery_artifact.operation_id
                             AND o.status IN ('completed','cancelled','superseded'));
                """, cancellationToken, ("$id", recoveryArtifactId));
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM recovery_artifact WHERE recovery_artifact_id=$id AND status='release_authorized';",
                cancellationToken, ("$id", recoveryArtifactId));
            if (exact != 1)
                throw new InvalidOperationException(
                    "The recovery copy is not eligible for a manual release from a completed operation.");
        }, cancellationToken);
    }

    public async Task MarkRecoveryArtifactReleasedAsync(string recoveryArtifactId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(recoveryArtifactId, nameof(recoveryArtifactId));
        await WriteAsync(async (connection, transaction) =>
        {
            await RequireNoRecoverySourceConsumersAsync(connection, transaction, recoveryArtifactId,
                cancellationToken);
            await ExecuteAsync(connection, transaction,
                "UPDATE recovery_artifact SET status='released',released_utc=$utc WHERE recovery_artifact_id=$id AND status='release_authorized';",
                cancellationToken, ("$utc", Utc(DateTimeOffset.UtcNow)), ("$id", recoveryArtifactId));
            var exact = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM recovery_artifact WHERE recovery_artifact_id=$id AND status='released';",
                cancellationToken, ("$id", recoveryArtifactId));
            if (exact != 1)
                throw new InvalidOperationException("The recovery release did not reach its durable terminal state.");
        }, cancellationToken);
    }

    public async Task<string> AdoptLocalCloudRepresentationAsync(string pairId, string relativePath,
        string expectedLocalIdentityJson, string replacementLocalIdentityJson, string dropboxId,
        string dropboxRevision, string contentHash, long size,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        var path = PilotPath.NormalizeRelative(relativePath);
        RequireJson(expectedLocalIdentityJson, nameof(expectedLocalIdentityJson));
        RequireJson(replacementLocalIdentityJson, nameof(replacementLocalIdentityJson));
        ArgumentException.ThrowIfNullOrWhiteSpace(dropboxId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dropboxRevision);
        RequireHash(contentHash, nameof(contentHash));
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        ValidateCloudRepresentationIdentity(expectedLocalIdentityJson, replacementLocalIdentityJson,
            dropboxId, dropboxRevision);

        string? adoptedVersionId = null;
        await WriteAsync(async (connection, transaction) =>
        {
            string? objectId = null;
            string? currentLocalVersionId = null;
            string? dropboxVersionId = null;
            string? localMembershipId = null;
            string? dropboxMembershipId = null;
            string? currentLocalIdentity = null;
            string? currentLocalHash = null;
            long? currentLocalSize = null;
            string? currentDropboxIdentity = null;
            string? currentDropboxHash = null;
            long? currentDropboxSize = null;
            var count = 0;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT p.object_id,p.local_object_version_id,p.dropbox_object_version_id,
                           p.local_membership_id,p.dropbox_membership_id,
                           lv.identity_json,lv.content_hash,lv.size_bytes,
                           dv.identity_json,dv.content_hash,dv.size_bytes
                    FROM current_synchronization_point p
                    JOIN logical_object o ON o.object_id=p.object_id AND o.object_kind='file'
                    JOIN object_version lv ON lv.object_version_id=p.local_object_version_id
                                          AND lv.object_id=p.object_id AND lv.side='local'
                    JOIN object_version dv ON dv.object_version_id=p.dropbox_object_version_id
                                          AND dv.object_id=p.object_id AND dv.side='dropbox'
                    JOIN membership lm ON lm.membership_id=p.local_membership_id
                                      AND lm.object_id=p.object_id
                    JOIN membership dm ON dm.membership_id=p.dropbox_membership_id
                                      AND dm.object_id=p.object_id
                    WHERE p.pair_id=$pair
                      AND p.authority_generation=(
                          SELECT max(latest.authority_generation)
                          FROM current_synchronization_point latest
                          WHERE latest.pair_id=p.pair_id AND latest.object_id=p.object_id)
                      AND lower(lm.relative_path_key)=lower($path)
                      AND lower(dm.relative_path_key)=lower($path);
                    """;
                command.Parameters.AddWithValue("$pair", pairId);
                command.Parameters.AddWithValue("$path", path);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    count++;
                    objectId = reader.GetString(0);
                    currentLocalVersionId = reader.GetString(1);
                    dropboxVersionId = reader.GetString(2);
                    localMembershipId = reader.GetString(3);
                    dropboxMembershipId = reader.GetString(4);
                    currentLocalIdentity = reader.GetString(5);
                    currentLocalHash = reader.IsDBNull(6) ? null : reader.GetString(6);
                    currentLocalSize = reader.IsDBNull(7) ? null : reader.GetInt64(7);
                    currentDropboxIdentity = reader.GetString(8);
                    currentDropboxHash = reader.IsDBNull(9) ? null : reader.GetString(9);
                    currentDropboxSize = reader.IsDBNull(10) ? null : reader.GetInt64(10);
                }
            }
            if (count != 1 || objectId is null || currentLocalVersionId is null ||
                dropboxVersionId is null || localMembershipId is null || dropboxMembershipId is null ||
                currentLocalIdentity is null || currentDropboxIdentity is null)
                throw new InvalidOperationException(
                    "Cloud representation adoption requires one exact two-sided synchronization point at the path.");
            if (!string.Equals(currentLocalHash, contentHash, StringComparison.OrdinalIgnoreCase) ||
                currentLocalSize != size ||
                !string.Equals(currentDropboxHash, contentHash, StringComparison.OrdinalIgnoreCase) ||
                currentDropboxSize != size)
                throw new InvalidOperationException(
                    "Cloud representation adoption requires unchanged bytes on both synchronized sides.");
            using (var remote = JsonDocument.Parse(currentDropboxIdentity))
            {
                if (!string.Equals(RequiredJsonString(remote.RootElement, "fileId"), dropboxId,
                        StringComparison.Ordinal) ||
                    !string.Equals(RequiredJsonString(remote.RootElement, "revision"), dropboxRevision,
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Cloud representation adoption requires the exact synchronized Dropbox revision.");
            }

            if (SyncAuthorityLocalVersionIdentity.EquivalentWithKnownContent(currentLocalIdentity,
                    replacementLocalIdentityJson, contentHash, size))
            {
                adoptedVersionId = currentLocalVersionId;
                return;
            }
            if (!SyncAuthorityLocalVersionIdentity.EquivalentWithKnownContent(currentLocalIdentity,
                    expectedLocalIdentityJson, contentHash, size))
                throw new InvalidOperationException(
                    "Cloud representation adoption requires the exact pre-conversion local identity.");

            var versionId = AuthorityDeterministicId("version-local", objectId,
                replacementLocalIdentityJson, contentHash, size.ToString(CultureInfo.InvariantCulture));
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO object_version(object_version_id,object_id,side,identity_json,content_hash,size_bytes,observed_utc) VALUES($id,$object,'local',$identity,$hash,$size,$utc);",
                cancellationToken, ("$id", versionId), ("$object", objectId),
                ("$identity", replacementLocalIdentityJson), ("$hash", contentHash), ("$size", size),
                ("$utc", now));
            if (await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM object_version WHERE object_version_id=$id AND object_id=$object AND side='local' AND identity_json=$identity AND content_hash=$hash AND size_bytes=$size;",
                    cancellationToken, ("$id", versionId), ("$object", objectId),
                    ("$identity", replacementLocalIdentityJson), ("$hash", contentHash),
                    ("$size", size)) != 1)
                throw new InvalidOperationException(
                    "The deterministic Cloud representation version collides with different authority.");

            var generation = await ScalarLongAsync(connection, transaction,
                "SELECT coalesce(max(authority_generation),0)+1 FROM current_synchronization_point WHERE pair_id=$pair AND object_id=$object;",
                cancellationToken, ("$pair", pairId), ("$object", objectId));
            var pointId = AuthorityDeterministicId("syncpoint-local-cloud-representation", pairId,
                objectId, versionId, dropboxVersionId, localMembershipId, dropboxMembershipId);
            await PrepareCurrentCopyPointAsync(connection, transaction, pointId, pairId, objectId,
                versionId, dropboxVersionId, localMembershipId, dropboxMembershipId, generation, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT OR IGNORE INTO synchronization_point(synchronization_point_id,pair_id,object_id,local_object_version_id,dropbox_object_version_id,local_membership_id,dropbox_membership_id,accepted_utc,authority_generation) VALUES($id,$pair,$object,$localVersion,$dropboxVersion,$localMembership,$dropboxMembership,$utc,$generation);",
                cancellationToken, ("$id", pointId), ("$pair", pairId), ("$object", objectId),
                ("$localVersion", versionId), ("$dropboxVersion", dropboxVersionId),
                ("$localMembership", localMembershipId), ("$dropboxMembership", dropboxMembershipId),
                ("$utc", now), ("$generation", generation));
            if (await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM current_synchronization_point WHERE synchronization_point_id=$id AND pair_id=$pair AND object_id=$object AND local_object_version_id=$localVersion AND dropbox_object_version_id=$dropboxVersion AND local_membership_id=$localMembership AND dropbox_membership_id=$dropboxMembership AND authority_generation=$generation;",
                    cancellationToken, ("$id", pointId), ("$pair", pairId), ("$object", objectId),
                    ("$localVersion", versionId), ("$dropboxVersion", dropboxVersionId),
                    ("$localMembership", localMembershipId),
                    ("$dropboxMembership", dropboxMembershipId), ("$generation", generation)) != 1)
                throw new InvalidOperationException(
                    "The deterministic Cloud representation synchronization point collides with different authority.");
            adoptedVersionId = versionId;
        }, cancellationToken);
        return adoptedVersionId ??
               throw new InvalidDataException("Cloud representation adoption produced no retained version.");
    }

    private static void ValidateCloudRepresentationIdentity(string expectedIdentityJson,
        string replacementIdentityJson, string dropboxId, string dropboxRevision)
    {
        using var expected = JsonDocument.Parse(expectedIdentityJson);
        using var replacement = JsonDocument.Parse(replacementIdentityJson);
        var expectedAlgorithm = expected.RootElement.TryGetProperty("contentAlgorithm", out var originalAlgorithm) ? originalAlgorithm.GetString() : null;
        var replacementAlgorithm = replacement.RootElement.TryGetProperty("contentAlgorithm", out var newAlgorithm) ? newAlgorithm.GetString() : null;
        if (!string.Equals(expectedAlgorithm, replacementAlgorithm, StringComparison.Ordinal))
            throw new InvalidOperationException("Cloud representation adoption cannot change the content identity algorithm.");
        foreach (var property in new[] { "volumeSerial", "fileId", "lastWriteUtc" })
            if (!string.Equals(RequiredJsonString(expected.RootElement, property),
                    RequiredJsonString(replacement.RootElement, property), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Cloud representation adoption cannot change local physical or timestamp identity.");
        if (!replacement.RootElement.TryGetProperty("cloudFiles", out var cloud) ||
            cloud.ValueKind != JsonValueKind.Object ||
            !string.Equals(RequiredJsonString(cloud, "marker"), WindowsCloudFileIdentity.CurrentMarker,
                StringComparison.Ordinal) ||
            !string.Equals(RequiredJsonString(cloud, "DropboxId"), dropboxId,
                StringComparison.Ordinal) ||
            !string.Equals(RequiredJsonString(cloud, "DropboxRevision"), dropboxRevision,
                StringComparison.Ordinal) || cloud.TryGetProperty("availability", out _))
            throw new InvalidOperationException(
                "The replacement local identity is not the exact availability-free Cloud Files authority.");
    }

    public async Task CancelEmptyOperationGroupAsync(string operationGroupId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(operationGroupId, nameof(operationGroupId));
        await WriteAsync(async (connection, transaction) =>
        {
            var members = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM operation WHERE operation_group_id=$id;", cancellationToken,
                ("$id", operationGroupId));
            if (members != 0)
                throw new InvalidOperationException("A non-empty operation group cannot be cancelled as effect-free.");
            var present = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM operation_group WHERE operation_group_id=$id;", cancellationToken,
                ("$id", operationGroupId));
            if (present != 1)
                throw new InvalidDataException("The effect-free operation group is not retained.");
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_group SET status='cancelled',terminal_utc=$utc WHERE operation_group_id=$id AND status IN ('planned','active');",
                cancellationToken, ("$id", operationGroupId), ("$utc", Utc(DateTimeOffset.UtcNow)));
        }, cancellationToken);
    }

    public async Task ReplacePathIndexAsync(string pairId, string localScanGenerationId,
        string dropboxScanGenerationId, IReadOnlyList<SyncPathIndexEntry> entries,
        SyncSelectionIndex selection, CancellationToken cancellationToken = default,
        Func<SyncComparisonProgress, Task>? onProgress = null)
    {
        RequireIdentifier(pairId, nameof(pairId));
        RequireIdentifier(localScanGenerationId, nameof(localScanGenerationId));
        RequireIdentifier(dropboxScanGenerationId, nameof(dropboxScanGenerationId));
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(selection);
        var orderedEntries = entries.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var started = DateTimeOffset.UtcNow;
        // Inventory rows already have their exact per-file retained events. Walking them again solely to
        // report "prepared" performs no work and makes logging dominate small-file synchronization. Report
        // the actual SQLite transaction instead; neither a prepared row nor an uncommitted write is completion.
        cancellationToken.ThrowIfCancellationRequested();
        if (onProgress is not null)
            await onProgress(new SyncComparisonProgress("Committing Folder Index", pairId,
                0, orderedEntries.Length, started, DateTimeOffset.UtcNow,
                $"Executing the atomic SQLite index replacement and folder rollups for {orderedEntries.Length:N0} records. Prepared statements are reused; the old index stays authoritative until commit.", "records committed"));
        await WriteAsync(async (connection, transaction) =>
        {
            using var commands = new ObservationCommands(connection, transaction);
            var validScans = await commands.ScalarLongAsync(
                "SELECT count(*) FROM scan_generation WHERE pair_id=$pair AND status='complete' AND scan_generation_id IN ($local,$dropbox);",
                cancellationToken, ("$pair", pairId), ("$local", localScanGenerationId),
                ("$dropbox", dropboxScanGenerationId));
            if (validScans != 2)
                throw new InvalidOperationException("A path index requires the exact complete local and Dropbox inventories.");

            var retainedPaths = new List<(string Path, bool Directory)>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT relative_path_key,object_kind FROM sync_path_index WHERE pair_id=$pair;";
                command.Parameters.AddWithValue("$pair", pairId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) retainedPaths.Add((reader.GetString(0), reader.GetString(1) == "directory"));
            }
            foreach (var retained in retainedPaths)
            {
                var path = retained.Path;
                if (selection.IsIncluded(path, retained.Directory))
                {
                    await commands.ExecuteAsync(
                        "DELETE FROM sync_path_index WHERE pair_id=$pair AND relative_path_key=$path;",
                        cancellationToken, ("$pair", pairId), ("$path", path));
                }
                else
                {
                    await commands.ExecuteAsync(
                        """
                        UPDATE sync_path_index
                        SET dropbox_present=0,dropbox_stable_identity=NULL,included=0,
                            online_only=CASE WHEN local_present=1 THEN online_only ELSE 0 END,
                            dropbox_scan_generation_id=$dropbox,updated_utc=$utc
                        WHERE pair_id=$pair AND relative_path_key=$path;
                        """, cancellationToken, ("$dropbox", dropboxScanGenerationId),
                        ("$utc", Utc(DateTimeOffset.UtcNow)), ("$pair", pairId), ("$path", path));
                }
            }

            foreach (var entry in orderedEntries)
            {
                var path = PilotPath.NormalizeRelative(entry.RelativePath);
                if (path.Length == 0) continue;
                await commands.ExecuteAsync(
                    """
                    INSERT INTO sync_path_index(pair_id,relative_path_key,object_kind,local_present,
                        dropbox_present,online_only,local_stable_identity,dropbox_stable_identity,included,
                        local_scan_generation_id,dropbox_scan_generation_id,updated_utc)
                    VALUES($pair,$path,$kind,$localPresent,$dropboxPresent,$onlineOnly,$localIdentity,
                        $dropboxIdentity,$included,$localScan,$dropboxScan,$utc)
                    ON CONFLICT(pair_id,relative_path_key) DO UPDATE SET
                        object_kind=excluded.object_kind,
                        local_present=CASE WHEN $localObserved=1 THEN excluded.local_present
                                           ELSE sync_path_index.local_present END,
                        online_only=CASE WHEN $localObserved=1 THEN excluded.online_only
                                         ELSE sync_path_index.online_only END,
                        local_stable_identity=CASE WHEN $localObserved=1 THEN excluded.local_stable_identity
                                                   ELSE sync_path_index.local_stable_identity END,
                        dropbox_present=excluded.dropbox_present,
                        dropbox_stable_identity=excluded.dropbox_stable_identity,
                        included=excluded.included,
                        local_scan_generation_id=CASE WHEN $localObserved=1
                            THEN excluded.local_scan_generation_id ELSE sync_path_index.local_scan_generation_id END,
                        dropbox_scan_generation_id=excluded.dropbox_scan_generation_id,
                        updated_utc=excluded.updated_utc;
                    """, cancellationToken, ("$pair", pairId), ("$path", path),
                    ("$kind", entry.Kind == PilotEntryKind.Directory ? "directory" : "file"),
                    ("$localPresent", entry.LocalPresent ? 1 : 0),
                    ("$dropboxPresent", entry.DropboxPresent ? 1 : 0),
                    ("$onlineOnly", entry.OnlineOnly ? 1 : 0),
                    ("$localIdentity", entry.LocalStableIdentity),
                    ("$dropboxIdentity", entry.DropboxStableIdentity),
                    ("$included", entry.Included ? 1 : 0),
                    ("$localScan", localScanGenerationId), ("$dropboxScan", dropboxScanGenerationId),
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$localObserved", entry.LocalObserved ? 1 : 0));
            }
            await RecomputePathIndexRollupsAsync(connection, transaction, pairId, cancellationToken);
        }, cancellationToken);
        if (onProgress is not null)
            await onProgress(new SyncComparisonProgress("Folder Index Committed", pairId,
                orderedEntries.Length, orderedEntries.Length, started, DateTimeOffset.UtcNow,
                "The complete verified index and folder rollups were committed atomically. No file transfer occurred in this stage.", "records committed"));
    }

    public async Task<IReadOnlyList<SyncStorageDirectoryIdentity>> LoadStorageDirectoryIdentitiesAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT relative_path_key,local_stable_identity,dropbox_stable_identity FROM sync_path_index WHERE pair_id=$pair AND object_kind='directory';";
        command.Parameters.AddWithValue("$pair", pairId);
        var result = new List<SyncStorageDirectoryIdentity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var local = reader.IsDBNull(1) ? null : reader.GetString(1);
            var parts = local?.Split(':'); ulong? localId = null;
            if (parts is { Length: 3 } && ulong.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed)) localId = parsed;
            result.Add(new SyncStorageDirectoryIdentity(reader.GetString(0), localId,
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return result;
    }

    public async Task<SyncFolderSelectionPage?> LoadFolderSelectionPageAsync(string pairId,
        int limit = 250, int offset = 0, string? search = null, string? filter = null,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        var query = (search ?? string.Empty).Trim();
        if (query.Length > 256) throw new ArgumentOutOfRangeException(nameof(search));
        var selectedFilter = filter ?? "All folders";
        var acceptedFilters = new[] { "All folders", "This PC only", "Dropbox only", "Online only", "Mixed", "Excluded", "Pending", "Errors" };
        if (!acceptedFilters.Contains(selectedFilter, StringComparer.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(filter));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (await ScalarLongAsync(connection, null,
                "SELECT count(*) FROM sync_path_index WHERE pair_id=$pair;", cancellationToken,
                ("$pair", pairId)) == 0)
            return null;

        const string folderBase = """
            WITH folders AS (
                SELECT f.*,
                       f.subtree_folder_count AS folder_count,
                       f.subtree_included_folder_count AS included_folder_count,
                       f.subtree_local_items AS local_items,
                       f.subtree_online_only_items AS online_only_items,
                       f.subtree_mixed_items AS mixed_items,
                       f.subtree_excluded_items AS excluded_items,
                       (SELECT count(DISTINCT m.expected_path_key) FROM operation_member m
                        JOIN operation op ON op.operation_id=m.operation_id
                        WHERE m.pair_id=f.pair_id AND m.expected_path_key IS NOT NULL
                          AND (m.expected_path_key=f.relative_path_key OR
                               (m.expected_path_key>=f.relative_path_key||'/' AND
                                m.expected_path_key<f.relative_path_key||'0'))
                          AND op.status NOT IN ('completed','cancelled','superseded','blocked')) AS pending_items,
                       (SELECT count(DISTINCT m.expected_path_key) FROM operation_member m
                        JOIN operation op ON op.operation_id=m.operation_id
                        WHERE m.pair_id=f.pair_id AND m.expected_path_key IS NOT NULL
                          AND (m.expected_path_key=f.relative_path_key OR
                               (m.expected_path_key>=f.relative_path_key||'/' AND
                                m.expected_path_key<f.relative_path_key||'0'))
                          AND op.status IN ('blocked','effect_unknown')) AS error_items
                FROM sync_path_index f
                WHERE f.pair_id=$pair AND f.object_kind='directory'
            ), filtered AS (
                SELECT * FROM folders
                WHERE ($search='' OR instr(lower(relative_path_key),lower($search))>0)
                  AND ($filter='All folders'
                    OR ($filter='This PC only' AND (local_present=1 AND dropbox_present=0 OR local_items>0))
                    OR ($filter='Dropbox only' AND dropbox_present=1 AND local_present=0 AND online_only=0)
                    OR ($filter='Online only' AND (online_only=1 OR dropbox_present=1 AND local_present=0 OR online_only_items>0))
                    OR ($filter='Mixed' AND (local_present=1 AND dropbox_present=1 OR mixed_items>0))
                    OR ($filter='Excluded' AND (included=0 OR excluded_items>0))
                    OR ($filter='Pending' AND pending_items>0)
                    OR ($filter='Errors' AND error_items>0))
            )
            """;
        long totalMatching;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = folderBase + "SELECT count(*) FROM filtered;";
            AddFolderPageParameters(count, pairId, query, selectedFilter);
            totalMatching = (long)(await count.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        var nodes = new List<SyncFolderSelectionNode>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = folderBase + """
                SELECT f.relative_path_key,f.local_present,f.dropbox_present,f.online_only,
                       local_stable_identity,dropbox_stable_identity,folder_count,included_folder_count,
                       local_items,online_only_items,mixed_items,excluded_items,pending_items,error_items,
                       CASE WHEN f.local_present=0 AND f.included=0 AND EXISTS (
                           SELECT 1 FROM sync_path_index a
                           WHERE a.pair_id=f.pair_id AND a.object_kind='directory'
                             AND a.local_present=1 AND a.included=0
                             AND f.relative_path_key>a.relative_path_key||'/'
                             AND f.relative_path_key<a.relative_path_key||'0'
                       ) THEN 1 ELSE 0 END AS local_unloaded
                FROM filtered f
                ORDER BY length(relative_path_key)-length(replace(relative_path_key,'/','')),
                         relative_path_key COLLATE NOCASE
                LIMIT $limit OFFSET $offset;
                """;
            AddFolderPageParameters(command, pairId, query, selectedFilter);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var path = reader.GetString(0);
                var localPresent = reader.GetInt32(1) == 1;
                var dropboxPresent = reader.GetInt32(2) == 1;
                var onlineOnly = reader.GetInt32(3) == 1;
                var localUnloaded = reader.GetInt32(14) == 1;
                var folderCount = reader.GetInt32(6);
                var includedCount = reader.GetInt32(7);
                bool? checkState = includedCount == 0 ? false : includedCount == folderCount ? true : null;
                var presence = localUnloaded ? SyncFolderPresence.LocalUnloaded
                    : onlineOnly ? SyncFolderPresence.OnlineOnly
                    : localPresent && dropboxPresent ? SyncFolderPresence.Mixed
                    : dropboxPresent ? SyncFolderPresence.CloudOnly
                    : localPresent ? SyncFolderPresence.LocalOnly
                    : SyncFolderPresence.Excluded;
                var stableIdentity = reader.IsDBNull(5) ? reader.IsDBNull(4) ? null : reader.GetString(4)
                    : "dropbox:" + reader.GetString(5);
                var localIdentity = reader.IsDBNull(4) ? null : reader.GetString(4);
                var dropboxIdentity = reader.IsDBNull(5) ? null : reader.GetString(5);
                nodes.Add(new SyncFolderSelectionNode(path,
                    Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar)),
                    path.Count(character => character == '/') + 1, checkState, presence,
                    Math.Max(0, folderCount - 1), stableIdentity,
                    new SyncFolderRollup(reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10),
                        reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13)),
                    localIdentity, dropboxIdentity));
            }
        }

        await using var summary = connection.CreateCommand();
        summary.CommandText = """
            SELECT count(CASE WHEN object_kind='directory' THEN 1 END),
                    count(CASE WHEN object_kind='directory' AND dropbox_present=1 AND local_present=0
                        AND NOT (included=0 AND EXISTS (
                            SELECT 1 FROM sync_path_index a
                            WHERE a.pair_id=sync_path_index.pair_id AND a.object_kind='directory'
                              AND a.local_present=1 AND a.included=0
                              AND sync_path_index.relative_path_key>a.relative_path_key||'/'
                              AND sync_path_index.relative_path_key<a.relative_path_key||'0'
                        )) THEN 1 END),
                   count(CASE WHEN object_kind='directory' AND local_present=1 AND dropbox_present=0 THEN 1 END),
                   count(CASE WHEN object_kind='directory' AND local_present=1 AND dropbox_present=1 AND online_only=0 THEN 1 END),
                   count(CASE WHEN object_kind='directory' AND online_only=1 THEN 1 END),
                   count(CASE WHEN local_present=1 AND dropbox_present=0 AND online_only=0 THEN 1 END),
                   count(CASE WHEN online_only=1 THEN 1 END),
                   count(CASE WHEN local_present=1 AND dropbox_present=1 AND online_only=0 THEN 1 END),
                   count(CASE WHEN included=0 THEN 1 END)
            FROM sync_path_index WHERE pair_id=$pair;
            """;
        summary.Parameters.AddWithValue("$pair", pairId);
        int[] summaryValues;
        await using (var summaryReader = await summary.ExecuteReaderAsync(cancellationToken))
        {
            await summaryReader.ReadAsync(cancellationToken);
            summaryValues = Enumerable.Range(0, 9).Select(summaryReader.GetInt32).ToArray();
        }
        var totalFolders = summaryValues[0];
        var pending = await CountPairOperationsAsync(connection, pairId, false, cancellationToken);
        var errors = await CountPairOperationsAsync(connection, pairId, true, cancellationToken);
        return new SyncFolderSelectionPage(pairId, nodes, offset, checked((int)totalMatching), totalFolders,
            offset + nodes.Count < totalMatching, summaryValues[1], summaryValues[2],
            summaryValues[3], summaryValues[4],
            new SyncFolderRollup(summaryValues[5], summaryValues[6], summaryValues[7], summaryValues[8],
                pending, errors));
    }

    public async Task UpdatePathIndexSelectionAsync(string pairId, SyncSelectionIndex selection,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        ArgumentNullException.ThrowIfNull(selection);
        await WriteAsync(async (connection, transaction) =>
        {
            var paths = new List<(string Path, bool Directory)>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT relative_path_key,object_kind FROM sync_path_index WHERE pair_id=$pair;";
                command.Parameters.AddWithValue("$pair", pairId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) paths.Add((reader.GetString(0), reader.GetString(1) == "directory"));
            }
            foreach (var path in paths)
                await ExecuteAsync(connection, transaction,
                    "UPDATE sync_path_index SET included=$included,updated_utc=$utc WHERE pair_id=$pair AND relative_path_key=$path;",
                    cancellationToken, ("$included", selection.IsIncluded(path.Path, path.Directory) ? 1 : 0),
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$pair", pairId), ("$path", path.Path));
            await RecomputePathIndexRollupsAsync(connection, transaction, pairId, cancellationToken);
        }, cancellationToken);
    }

    private static async Task RecomputePathIndexRollupsAsync(SqliteConnection connection,
        SqliteTransaction transaction, string pairId, CancellationToken cancellationToken)
    {
        var items = new List<(string Path, bool Directory, bool Local, bool Dropbox, bool OnlineOnly,
            bool Included)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT relative_path_key,object_kind,local_present,dropbox_present,online_only,included FROM sync_path_index WHERE pair_id=$pair ORDER BY relative_path_key;";
            command.Parameters.AddWithValue("$pair", pairId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                items.Add((reader.GetString(0), reader.GetString(1) == "directory", reader.GetInt32(2) == 1,
                    reader.GetInt32(3) == 1, reader.GetInt32(4) == 1, reader.GetInt32(5) == 1));
        }
        var folders = items.Where(item => item.Directory).ToDictionary(item => item.Path,
            _ => new FolderIndexRollupBuilder(), StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var candidate = item.Path;
            while (candidate.Length != 0)
            {
                if (folders.TryGetValue(candidate, out var rollup))
                {
                    if (item.Directory)
                    {
                        rollup.FolderCount++;
                        if (item.Included) rollup.IncludedFolderCount++;
                    }
                    if (item.Local && !item.Dropbox && !item.OnlineOnly) rollup.LocalItems++;
                    if (item.OnlineOnly) rollup.OnlineOnlyItems++;
                    if (item.Local && item.Dropbox && !item.OnlineOnly) rollup.MixedItems++;
                    if (!item.Included) rollup.ExcludedItems++;
                }
                var separator = candidate.LastIndexOf('/');
                if (separator < 0) break;
                candidate = candidate[..separator];
            }
        }
        using var commands = new ObservationCommands(connection, transaction);
        foreach (var (path, rollup) in folders)
            await commands.ExecuteAsync(
                """
                UPDATE sync_path_index SET subtree_folder_count=$folders,
                    subtree_included_folder_count=$included,subtree_local_items=$local,
                    subtree_online_only_items=$online,subtree_mixed_items=$mixed,
                    subtree_excluded_items=$excluded
                WHERE pair_id=$pair AND relative_path_key=$path AND object_kind='directory';
                """, cancellationToken, ("$folders", rollup.FolderCount),
                ("$included", rollup.IncludedFolderCount), ("$local", rollup.LocalItems),
                ("$online", rollup.OnlineOnlyItems), ("$mixed", rollup.MixedItems),
                ("$excluded", rollup.ExcludedItems), ("$pair", pairId), ("$path", path));
    }

    private sealed class FolderIndexRollupBuilder
    {
        public int FolderCount { get; set; }
        public int IncludedFolderCount { get; set; }
        public int LocalItems { get; set; }
        public int OnlineOnlyItems { get; set; }
        public int MixedItems { get; set; }
        public int ExcludedItems { get; set; }
    }

    private static void AddFolderPageParameters(SqliteCommand command, string pairId, string search,
        string filter)
    {
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$search", search);
        command.Parameters.AddWithValue("$filter", filter);
    }

    private static async Task<int> CountPairOperationsAsync(SqliteConnection connection, string pairId,
        bool errorsOnly, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = errorsOnly
            ? "SELECT count(*) FROM operation WHERE pair_id=$pair AND status IN ('blocked','effect_unknown');"
            : "SELECT count(*) FROM operation WHERE pair_id=$pair AND status NOT IN ('completed','cancelled','superseded','blocked');";
        command.Parameters.AddWithValue("$pair", pairId);
        return checked((int)(long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L));
    }

    public async Task<SyncAuthorityUploadSessionCheckpoint?> LoadUploadSessionCheckpointAsync(
        string allocationId, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(allocationId, nameof(allocationId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT allocation_id,operation_id,session_id,confirmed_offset_bytes,source_object_version_id,
                   source_size_bytes,source_content_hash,status,updated_utc
            FROM upload_session_checkpoint WHERE allocation_id=$id;
            """;
        command.Parameters.AddWithValue("$id", allocationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SyncAuthorityUploadSessionCheckpoint(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetInt64(5),
            reader.GetString(6), reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture));
    }

    public async Task SaveUploadSessionCheckpointAsync(string allocationId,
        SyncAuthorityLease lease, string sessionId, long confirmedOffset, string sourceObjectVersionId,
        long sourceSize, string sourceContentHash, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(allocationId, nameof(allocationId));
        RequireIdentifier(sourceObjectVersionId, nameof(sourceObjectVersionId));
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 2048)
            throw new InvalidDataException("The Dropbox upload-session identity is absent or oversized.");
        if (sourceSize <= 0 || confirmedOffset < 0 || confirmedOffset > sourceSize)
            throw new InvalidDataException("The Dropbox upload-session offset is outside the retained source.");
        RequireHash(sourceContentHash, nameof(sourceContentHash));
        await WriteAsync(async (connection, transaction) =>
        {
            await RequireFenceAsync(connection, transaction, lease.OperationId, lease, cancellationToken);
            var now = Utc(DateTimeOffset.UtcNow);
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO upload_session_checkpoint(
                    allocation_id,operation_id,session_id,confirmed_offset_bytes,source_object_version_id,
                    source_size_bytes,source_content_hash,status,transition_lease_id,
                    transition_fencing_token,updated_utc)
                VALUES($allocation,$operation,$session,$offset,$version,$size,$hash,'active',$lease,$token,$utc)
                ON CONFLICT(allocation_id) DO UPDATE SET
                    session_id=excluded.session_id,
                    confirmed_offset_bytes=excluded.confirmed_offset_bytes,
                    status='active',
                    transition_lease_id=excluded.transition_lease_id,
                    transition_fencing_token=excluded.transition_fencing_token,
                    updated_utc=excluded.updated_utc
                WHERE upload_session_checkpoint.operation_id=excluded.operation_id
                  AND upload_session_checkpoint.source_object_version_id=excluded.source_object_version_id
                  AND upload_session_checkpoint.source_size_bytes=excluded.source_size_bytes
                  AND upload_session_checkpoint.source_content_hash=excluded.source_content_hash;
                """, cancellationToken, ("$allocation", allocationId), ("$operation", lease.OperationId),
                ("$session", sessionId), ("$offset", confirmedOffset), ("$version", sourceObjectVersionId),
                ("$size", sourceSize), ("$hash", sourceContentHash.ToLowerInvariant()),
                ("$lease", lease.LeaseId), ("$token", lease.FencingToken), ("$utc", now));
            var exact = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM upload_session_checkpoint
                WHERE allocation_id=$allocation AND operation_id=$operation AND session_id=$session
                  AND confirmed_offset_bytes=$offset AND source_object_version_id=$version
                  AND source_size_bytes=$size AND source_content_hash=$hash AND status='active';
                """, cancellationToken, ("$allocation", allocationId), ("$operation", lease.OperationId),
                ("$session", sessionId), ("$offset", confirmedOffset), ("$version", sourceObjectVersionId),
                ("$size", sourceSize), ("$hash", sourceContentHash.ToLowerInvariant()));
            if (exact != 1)
                throw new InvalidOperationException(
                    "The upload-session checkpoint conflicts with its immutable source authority.");
        }, cancellationToken);
    }

    public async Task CompleteUploadSessionCheckpointAsync(string allocationId,
        SyncAuthorityLease lease, string sessionId, long sourceSize,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(allocationId, nameof(allocationId));
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new InvalidDataException("The completed upload-session identity is absent.");
        if (sourceSize <= 0) throw new ArgumentOutOfRangeException(nameof(sourceSize));
        await WriteAsync(async (connection, transaction) =>
        {
            await RequireFenceAsync(connection, transaction, lease.OperationId, lease, cancellationToken);
            await ExecuteAsync(connection, transaction,
                """
                UPDATE upload_session_checkpoint
                SET confirmed_offset_bytes=$size,status='completed',transition_lease_id=$lease,
                    transition_fencing_token=$token,updated_utc=$utc
                WHERE allocation_id=$allocation AND operation_id=$operation AND session_id=$session
                  AND source_size_bytes=$size AND status='active';
                """, cancellationToken, ("$size", sourceSize), ("$lease", lease.LeaseId),
                ("$token", lease.FencingToken), ("$utc", Utc(DateTimeOffset.UtcNow)),
                ("$allocation", allocationId), ("$operation", lease.OperationId), ("$session", sessionId));
            var completed = await ScalarLongAsync(connection, transaction,
                """
                SELECT count(*) FROM upload_session_checkpoint
                WHERE allocation_id=$allocation AND operation_id=$operation AND session_id=$session
                  AND confirmed_offset_bytes=$size AND source_size_bytes=$size AND status='completed';
                """, cancellationToken, ("$allocation", allocationId), ("$operation", lease.OperationId),
                ("$session", sessionId), ("$size", sourceSize));
            if (completed != 1)
                throw new InvalidOperationException("The exact upload-session checkpoint could not be completed.");
        }, cancellationToken);
    }

    public async Task ReleaseLeaseAsync(SyncAuthorityLease lease, CancellationToken cancellationToken = default)
    {
        await WriteAsync(async (connection, transaction) =>
        {
            await RequireFenceAsync(connection, transaction, lease.OperationId, lease, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "UPDATE lease SET status='released' WHERE lease_id=$lease AND status='active';",
                cancellationToken, ("$lease", lease.LeaseId));
            await ExecuteAsync(connection, transaction,
                "UPDATE operation SET current_lease_id=NULL,current_fencing_token=NULL WHERE operation_id=$operation AND current_lease_id=$lease AND current_fencing_token=$token;",
                cancellationToken, ("$operation", lease.OperationId), ("$lease", lease.LeaseId),
                ("$token", lease.FencingToken));
        }, cancellationToken);
    }

    public async Task<SyncAuthorityOperationSnapshot> LoadOperationAsync(string operationId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(operationId, nameof(operationId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await LoadOperationCoreAsync(connection, operationId, cancellationToken);
    }

    // A reviewed batch has already acquired its run and database-identity guards.
    // Read its snapshots through one checked connection instead of repeating the
    // schema/settings checkout for every file. Executors still independently reload
    // and fence each current operation before an effect; this is not an authority cache.
    internal async Task<IReadOnlyList<SyncAuthorityOperationSnapshot>> LoadOperationsAsync(
        IReadOnlyList<string> operationIds, CancellationToken cancellationToken = default,
        Func<int, string, Task>? onRead = null)
    {
        ArgumentNullException.ThrowIfNull(operationIds);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var operationId in operationIds) RequireIdentifier(operationId, nameof(operationIds));
        if (operationIds.Count == 0) return [];
        await using var connection = await OpenConnectionAsync(cancellationToken);
        // One read per table for the whole batch; each uses its operation_id index.
        var members = new Dictionary<string, List<SyncAuthorityMember>>(StringComparer.Ordinal);
        var allocations = new Dictionary<string, List<SyncAuthorityAllocation>>(StringComparer.Ordinal);
        var idsJson = JsonSerializer.Serialize(operationIds);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT operation_id,operation_member_id,pair_id,object_id,role,root_binding_id,membership_id,object_version_id,expected_membership_generation,expected_path_key,expected_state_json FROM operation_member WHERE operation_id IN (SELECT value FROM json_each($ids)) ORDER BY operation_id,operation_member_id;";
            command.Parameters.AddWithValue("$ids", idsJson);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                if (!members.TryGetValue(id, out var rows)) members[id] = rows = [];
                rows.Add(ReadMember(reader, 1));
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT operation_id,allocation_id,control_namespace_id,resource_kind,deterministic_name,intended_parent_identity,owner_nonce_hash,status,bound_identity,content_hash,size_bytes FROM resource_allocation WHERE operation_id IN (SELECT value FROM json_each($ids)) ORDER BY operation_id,allocation_id;";
            command.Parameters.AddWithValue("$ids", idsJson);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                if (!allocations.TryGetValue(id, out var rows)) allocations[id] = rows = [];
                rows.Add(ReadAllocation(reader, 1));
            }
        }
        var snapshots = new List<SyncAuthorityOperationSnapshot>(operationIds.Count);
        for (var index = 0; index < operationIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (onRead is not null) await onRead(index, operationIds[index]);
            snapshots.Add(await LoadOperationCoreAsync(connection, operationIds[index], cancellationToken,
                members.GetValueOrDefault(operationIds[index]) ?? [],
                allocations.GetValueOrDefault(operationIds[index]) ?? []));
        }
        return snapshots;
    }

    private static async Task<SyncAuthorityOperationSnapshot> LoadOperationCoreAsync(
        SqliteConnection connection, string operationId, CancellationToken cancellationToken,
        IReadOnlyList<SyncAuthorityMember>? loadedMembers = null,
        IReadOnlyList<SyncAuthorityAllocation>? loadedAllocations = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind,status,pair_id,object_id,configuration_revision,capability_profile_id,idempotency_key,preconditions_json,current_lease_id,current_fencing_token FROM operation WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidDataException("The operation is not retained.");
        var kind = SyncAuthorityNames.OperationKind(reader.GetString(0));
        var status = SyncAuthorityNames.OperationStatus(reader.GetString(1));
        var pairId = reader.IsDBNull(2) ? null : reader.GetString(2);
        var objectId = reader.IsDBNull(3) ? null : reader.GetString(3);
        var configurationRevision = reader.GetInt64(4);
        var profile = reader.GetString(5);
        var key = reader.GetString(6);
        var preconditions = reader.GetString(7);
        var leaseId = reader.IsDBNull(8) ? null : reader.GetString(8);
        long? fencingToken = reader.IsDBNull(9) ? null : reader.GetInt64(9);
        await reader.DisposeAsync();

        var members = loadedMembers ?? await LoadMembersAsync(connection, operationId, cancellationToken);
        var dependencies = await LoadDependenciesAsync(connection, operationId, cancellationToken);
        var allocations = loadedAllocations ?? await LoadAllocationsAsync(connection, operationId, cancellationToken);
        var attempts = await LoadAttemptsAsync(connection, operationId, cancellationToken);
        var planningAuthorities = LoadPlanningAuthorities(preconditions);
        var subtreeReservations = kind == SyncAuthorityOperationKind.CrossPairMove
            ? LoadSubtreeReservations(preconditions)
            : [];
        return new SyncAuthorityOperationSnapshot(operationId, kind, status, pairId, objectId,
            configurationRevision, profile, key, preconditions, leaseId, fencingToken,
            members, dependencies, allocations, attempts, planningAuthorities, subtreeReservations);
    }

    public async Task<IReadOnlyList<SyncAuthorityOperationSnapshot>> ListOperationsForGroupAsync(
        string operationGroupId, CancellationToken cancellationToken = default,
        Func<int, int, string, Task>? onRead = null)
    {
        RequireIdentifier(operationGroupId, nameof(operationGroupId));
        var operationIds = new List<string>();
        await using (var connection = await OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT operation_id FROM operation WHERE operation_group_id=$group ORDER BY created_utc,operation_id;";
            command.Parameters.AddWithValue("$group", operationGroupId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) operationIds.Add(reader.GetString(0));
        }

        // Reuse the complete shared loader: two member/allocation scans for the
        // group, not two full retained-history scans for every completed file.
        // Statuses, dependencies, attempts and authority are still loaded by
        // the original core reader; completion keeps its fresh atomic guard.
        if (onRead is not null) await onRead(0, operationIds.Count, string.Empty);
        var operations = await LoadOperationsAsync(operationIds, cancellationToken,
            onRead is null ? null : (index, id) => onRead(index, operationIds.Count, id));
        if (onRead is not null) await onRead(operationIds.Count, operationIds.Count, string.Empty);
        return operations;
    }

    public async Task<IReadOnlyList<SyncAuthorityQueueItem>> ListRunnableOperationsAsync(int limit = 100,
        CancellationToken cancellationToken = default)
    {
        return await ListRunnableOperationsAsync(null, limit, cancellationToken);
    }

    public async Task<IReadOnlyList<SyncAuthorityRetainedSubtreeReservation>>
        ListRetainedSubtreeReservationsAsync(CancellationToken cancellationToken = default)
    {
        var retained = new List<(string OperationId, SyncAuthorityOperationStatus Status,
            string PreconditionsJson)>();
        await using (var connection = await OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT operation_id,status,preconditions_json
                FROM operation
                WHERE kind='cross_pair_move'
                  AND status NOT IN ('completed','cancelled','superseded')
                ORDER BY created_utc,operation_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                retained.Add((reader.GetString(0), SyncAuthorityNames.OperationStatus(reader.GetString(1)),
                    reader.GetString(2)));
        }

        var result = new List<SyncAuthorityRetainedSubtreeReservation>();
        foreach (var operation in retained)
        foreach (var reservation in LoadSubtreeReservations(operation.PreconditionsJson))
            result.Add(new SyncAuthorityRetainedSubtreeReservation(operation.OperationId, operation.Status,
                reservation.PairId, reservation.RootBindingId, reservation.RelativePath));
        return result;
    }

    public async Task<IReadOnlyList<SyncAuthorityQueueItem>> ListRunnableOperationsForPairAsync(string pairId,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pairId)) throw new ArgumentException("Pair ID is required.", nameof(pairId));
        return await ListRunnableOperationsAsync(pairId, limit, cancellationToken);
    }

    /// <summary>Recovery resumes retained effects, never a queued but unissued first-sync choice.</summary>
    public Task<IReadOnlyList<SyncAuthorityQueueItem>> ListRecoveryOperationsForPairAsync(string pairId,
        int limit = 100, CancellationToken cancellationToken = default, bool includePreparedLocalCreates = false)
    {
        RequireIdentifier(pairId, nameof(pairId));
        return ListRunnableOperationsAsync(pairId, limit, cancellationToken, recoveryOnly: true,
            includePreparedLocalCreates: includePreparedLocalCreates);
    }

    private async Task<IReadOnlyList<SyncAuthorityQueueItem>> ListRunnableOperationsAsync(string? pairId, int limit,
        CancellationToken cancellationToken, bool recoveryOnly = false, bool includePreparedLocalCreates = false)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH prepared_local_creates AS MATERIALIZED (
                SELECT DISTINCT o.operation_id FROM operation o
                JOIN resource_allocation tx ON tx.operation_id=o.operation_id AND tx.resource_kind='transaction_directory'
                JOIN resource_allocation stage ON stage.operation_id=o.operation_id AND stage.resource_kind='stage_file'
                WHERE $prepared_local=1 AND o.pair_id=$pair_id AND o.kind='create_local' AND o.status='leased'
                  AND tx.status IN ('created','bound','verified','released') AND stage.status='authorized'
                  AND NOT EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id)
            ), verified_cloud_recoveries AS MATERIALIZED (
                SELECT DISTINCT o.operation_id FROM operation o
                JOIN operation_attempt a ON a.operation_id=o.operation_id
                    AND a.effect_kind='cloud-recovery-copy' AND a.status='verified_success'
                JOIN effect_receipt r ON r.attempt_id=a.attempt_id AND r.certainty='known_success'
                JOIN recovery_artifact ra ON ra.operation_id=o.operation_id AND ra.status IN ('retained','verified')
                JOIN resource_allocation allocation ON allocation.allocation_id=ra.allocation_id
                    AND allocation.operation_id=o.operation_id AND allocation.resource_kind='remote_recovery_file'
                    AND allocation.status='verified'
                WHERE o.status='validating' AND o.kind IN ('update_local','update_remote','delete_local','delete_remote')
                    AND ($pair_id IS NULL OR o.pair_id=$pair_id)
            )
            SELECT o.operation_id,o.kind,o.status,o.pair_id,o.object_id,o.priority,o.created_utc,
                   CASE WHEN o.status IN ('effect_issuing','effect_unknown','effect_observed','reconciling','verifying','committed','cleanup_pending')
                             OR EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id
                                       AND a.status IN ('started','request_sent','response_received','effect_unknown'))
                             OR o.operation_id IN (SELECT operation_id FROM prepared_local_creates)
                             OR o.operation_id IN (SELECT operation_id FROM verified_cloud_recoveries)
                        THEN 1 ELSE 0 END AS recovery_required
            FROM operation o
            WHERE o.status IN ('ready','leased','validating','allocation_authorized','staged','effect_issuing',
                               'effect_unknown','effect_observed','reconciling','verifying','committed','cleanup_pending')
              AND ($pair_id IS NULL OR o.pair_id=$pair_id)
              AND (o.status<>'ready' OR o.operation_group_id IS NULL OR EXISTS(
                    SELECT 1 FROM operation_group g WHERE g.operation_group_id=o.operation_group_id AND g.status IN ('active','committed')))
              AND ($recovery_only=0 OR o.status IN ('effect_issuing','effect_unknown','effect_observed',
                    'reconciling','verifying','committed','cleanup_pending')
                   OR EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id
                        AND a.status IN ('started','request_sent','response_received','effect_unknown'))
                   OR o.operation_id IN (SELECT operation_id FROM prepared_local_creates)
                   OR o.operation_id IN (SELECT operation_id FROM verified_cloud_recoveries))
              AND NOT EXISTS(
                  SELECT 1 FROM lease l
                  WHERE l.scope_kind='operation' AND l.scope_id=o.operation_id AND l.status='active'
                    AND julianday(l.expires_utc)>julianday('now'))
              AND (o.operation_id IN (SELECT operation_id FROM prepared_local_creates) OR NOT EXISTS(
                  SELECT 1 FROM operation_dependency_state d
                  WHERE d.operation_id=o.operation_id
                    AND ((d.dependency_kind='must_complete' AND d.prerequisite_status<>'completed')
                      OR (d.dependency_kind='must_commit' AND d.prerequisite_status NOT IN ('committed','cleanup_pending','completed'))
                      OR (d.dependency_kind='must_resolve' AND d.prerequisite_status NOT IN ('committed','cleanup_pending','completed','blocked','cancelled','superseded')))))
            ORDER BY recovery_required DESC,
                     CASE o.status
                         WHEN 'effect_unknown' THEN 0 WHEN 'effect_issuing' THEN 1 WHEN 'effect_observed' THEN 2
                         WHEN 'reconciling' THEN 3 WHEN 'verifying' THEN 4 WHEN 'committed' THEN 5
                         WHEN 'cleanup_pending' THEN 6 ELSE 7 END,
                     o.priority DESC,o.created_utc,o.operation_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pair_id", pairId is null ? DBNull.Value : pairId);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$recovery_only", recoveryOnly ? 1 : 0);
        command.Parameters.AddWithValue("$prepared_local", includePreparedLocalCreates ? 1 : 0);
        var result = new List<SyncAuthorityQueueItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SyncAuthorityQueueItem(reader.GetString(0),
                SyncAuthorityNames.OperationKind(reader.GetString(1)),
                SyncAuthorityNames.OperationStatus(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                reader.GetInt32(7) == 1));
        }
        return result;
    }

    public async Task<IReadOnlyList<SyncAuthorityActivityProjection>> LoadActivityProjectionAsync(int limit = 250,
        CancellationToken cancellationToken = default) =>
        await LoadActivityProjectionPageAsync(limit, 0, null, "All", cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<SyncAuthorityActivityProjection>> LoadActivityProjectionPageAsync(
        int limit = 250, int offset = 0, string? search = null, string? filter = null,
        string? itemType = null, string? pairId = null, DateTimeOffset? sinceUtc = null,
        CancellationToken cancellationToken = default, IReadOnlyList<string>? allowedPairIds = null,
        SyncActivityQuery? query = null, string? connectionId = null)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (query?.Categories?.Any(category => !SyncActivityCategories.All.Contains(category)) == true)
            throw new ArgumentException("Unsupported activity category.", nameof(query));
        if (query?.AfterSequence < 0 || query?.ThroughSequence < 0) throw new ArgumentOutOfRangeException(nameof(query));
        // History reads can touch a large retained log. Reuse the health reader's bounded
        // IO resources without leaving a mapping/cache in the transfer connection pool.
        // The diagnostic log must remain readable while startup/command integrity admission is running or
        // has failed. Use the existing exact-schema, query-only reader: this cannot mutate the database or
        // admit any command/effect, whose full health checks remain on OpenConnectionAsync.
        await using var connection = await OpenReadOnlyConnectionAsync(cancellationToken);
        await ConfigureInspectionReadResourcesAsync(connection, cancellationToken);
        ActivityReadConnectionForTest?.Invoke(connection);
        // EPS2: which recorded account a row may be attributed to is decided by its retained endpoint
        // context. That rule lives in exactly one place, the EPS1 append-side validator, so the page
        // query reads it back through one bounded shared lookup instead of restating it in SQL.
        RegisterRetainedEndpointContextLookup(connection);
        // Presentation JSON uses explicit enum names; authority rows use their existing persisted codes.
        connection.CreateFunction<object?, string?>("syncshark_activity_kind",
            SyncEndpointActivityPresentation.OperationCodeForJsonName, isDeterministic: true);
        await using var command = connection.CreateCommand();
        // Materialize the read-only member lookup once. The current authority schema
        // has no operation-id member index; correlated projections otherwise rescan every
        // member for each history row, before LIMIT can bound the displayed page. SQLite
        // can index this statement-local relation without changing any retained authority.
        // Direction is an established query option, never caller-supplied SQL.
        // A CASE-wrapped sequence makes SQLite sort the entire retained log even
        // when its rowid can directly seek and stop at the requested page.
        var timeOrder = query?.OldestFirst == true
            ? "occurred_utc ASC,sequence ASC,sort_id"
            : "occurred_utc DESC,sequence DESC,sort_id";
        var pageOrder = query?.DetailedLog == true
            ? "ORDER BY sequence " + (query.NewestLogFirst ? "DESC," : "ASC,") + timeOrder
            : "ORDER BY " + timeOrder;
        var normalizedFilter = string.IsNullOrWhiteSpace(filter) ? "All" : filter.Trim();
        // Every detailed row is one history_event with a unique durable rowid.
        // On an unconstrained account/search/type surface, exact run, category,
        // severity and cursor predicates can be applied to that native ordered
        // stream before any member/context/display enrichment. Account scopes
        // retain the complete attribution path, including ambiguous legacy rows.
        var directLogPage = query?.DetailedLog == true && string.IsNullOrWhiteSpace(search) &&
            string.IsNullOrEmpty(pairId) && sinceUtc is null && allowedPairIds is null &&
            string.IsNullOrEmpty(connectionId) && string.IsNullOrEmpty(query.Entity) &&
            (string.IsNullOrWhiteSpace(itemType) || itemType.Trim() == "All types") &&
            normalizedFilter is "All" or "All actions" or "Changes";
        // Unconstrained All-Accounts browse/log pages can select exact identities
        // and durable cursors before display enrichment. Constrained predicates
        // still run before pagination; there is no history or authority cache.
        var boundedBrowse = directLogPage || (query?.DetailedLog == true ||
            query?.AfterSequence is null && query?.ThroughSequence is null) && string.IsNullOrWhiteSpace(search) &&
            string.IsNullOrEmpty(pairId) && sinceUtc is null && allowedPairIds is null &&
            string.IsNullOrEmpty(connectionId) &&
            string.IsNullOrEmpty(query?.Severity) && string.IsNullOrEmpty(query?.Entity) &&
            string.IsNullOrEmpty(query?.RunId) &&
            (string.IsNullOrWhiteSpace(itemType) || itemType.Trim() == "All types") &&
            normalizedFilter is "All" or "All actions" or "Changes";
        // Only presentation rows have a retained run_id in this projection.
        // A nonempty exact run filter therefore cannot match a decision or
        // lifecycle row, or require another row's linked endpoint context.
        var runScoped = !string.IsNullOrEmpty(query?.RunId);
        // These exact category rules are shared by candidate selection and full
        // projection. A category page must filter before LIMIT, not fill its page
        // from recent unrelated diagnostics or recreate the legacy classification.
        const string decisionCategories = "CASE WHEN o.kind='allocate_control' THEN '[\"Folder pairs\"]' WHEN o.kind IN ('cleanup','reconcile') THEN '[\"Diagnostics\"]' ELSE '[\"File activity\"]' END";
        const string lifecycleCategories = "CASE WHEN h.event_kind IN ('unissued_lease_recovered','attempt_reconciled') THEN '[\"Sync log\",\"Diagnostics\"]' WHEN o.kind='allocate_control' THEN '[\"Folder pairs\"]' WHEN o.kind IN ('cleanup','reconcile') THEN '[\"Diagnostics\"]' ELSE '[\"File activity\"]' END";
        const string presentationCategories = "CASE WHEN lower(json_extract(h.detail_json,'$.Action')) LIKE 'reconcile%' THEN '[\"Diagnostics\"]' ELSE json_extract(h.detail_json,'$.Categories') END";
        var presentationSeverityOnly = !string.IsNullOrEmpty(query?.Severity) && query.Severity != "Information";
        var logKindPredicate = runScoped
            ? "h.event_kind='presentation' AND coalesce(json_extract(h.detail_json,'$.RunId'),json_extract(h.detail_json,'$.RelatedRunId'))=$run_id"
            : presentationSeverityOnly ? "h.event_kind='presentation'"
            : "(h.event_kind='presentation' OR (h.event_kind IN ('unissued_lease_recovered','attempt_reconciled') AND o.pair_id IS NOT NULL))";
        var logSeverityPredicate = string.IsNullOrEmpty(query?.Severity) ? "" :
            presentationSeverityOnly ? "AND json_extract(h.detail_json,'$.Severity')=$severity" :
                "AND CASE WHEN h.event_kind='presentation' THEN json_extract(h.detail_json,'$.Severity') ELSE 'Information' END=$severity";
        var logCategoryPredicate = query?.Categories is not { Count: > 0 } ? "" : $"""
            AND EXISTS (SELECT 1 FROM json_each(CASE WHEN h.event_kind='presentation'
                THEN {presentationCategories} ELSE {lifecycleCategories} END) tag
                JOIN json_each($categories) selected ON selected.value=tag.value)
            """;
        // The global chronological merge needs at most offset+limit matches from
        // each branch. Filtering precedes each branch's bound; anything below
        // that branch's top K cannot enter the merged top K. Key-only indexes
        // make the branches ordered reads instead of one full retained-history
        // sort. Ties retain the exact existing time/sequence/id ordering.
        var candidateCategoryPredicate = query?.Categories is not { Count: > 0 } ? "" :
            "AND EXISTS (SELECT 1 FROM json_each({0}) tag JOIN json_each($categories) selected ON selected.value=tag.value)";
        string CandidateCategory(string expression) => string.Format(CultureInfo.InvariantCulture,
            candidateCategoryPredicate, expression);
        var browsePage = directLogPage ? $"""
            browse_candidates AS MATERIALIZED (
                SELECT CASE WHEN h.event_kind='presentation' THEN 'presentation' ELSE 'lifecycle' END AS branch,
                       h.history_event_id AS sort_id,h.occurred_utc,h.rowid AS sequence,
                       o.pair_id,o.operation_id
                FROM history_event h
                LEFT JOIN operation o ON h.event_kind<>'presentation' AND o.operation_id=h.operation_id
                WHERE {logKindPredicate}
                  AND ($after_sequence IS NULL OR h.rowid>$after_sequence)
                  AND ($through_sequence IS NULL OR h.rowid<=$through_sequence)
                  {logSeverityPredicate}
                  {logCategoryPredicate}
                ORDER BY h.rowid {(query!.NewestLogFirst ? "DESC" : "ASC")}
                LIMIT $limit OFFSET $offset
            ),
            browse_page AS MATERIALIZED (
                SELECT b.branch,b.sort_id,b.occurred_utc,b.sequence,
                       CASE WHEN b.branch='presentation' THEN coalesce(json_extract(h.detail_json,'$.PairId'),'')
                            ELSE b.pair_id END AS pair_id,
                       CASE WHEN b.branch='presentation' THEN json_extract(h.detail_json,'$.OperationId')
                            ELSE b.operation_id END AS operation_id
                FROM browse_candidates b
                LEFT JOIN history_event h ON b.branch='presentation' AND h.history_event_id=b.sort_id
            ),
            """ : boundedBrowse ? $"""
            browse_decisions AS MATERIALIZED (
                    SELECT 'decision' AS branch,d.reconciliation_decision_id AS sort_id,
                           d.decided_utc AS occurred_utc,NULL AS sequence,d.pair_id,
                           json_extract(d.decision_json,'$.operationId') AS operation_id,
                           {decisionCategories} AS categories
                    FROM reconciliation_decision d INDEXED BY reconciliation_decision_decided_utc
                    LEFT JOIN operation o ON o.operation_id=json_extract(d.decision_json,'$.operationId')
                    WHERE $detailed_log=0 AND ($filter<>'Changes' OR d.decision_kind NOT IN ('unchanged','initial_observation','adopted'))
                      AND ($after_sequence IS NULL OR NULL>$after_sequence)
                      AND ($through_sequence IS NULL OR NULL<=$through_sequence)
                      {CandidateCategory(decisionCategories)}
                    {pageOrder} LIMIT $candidate_limit
            ),
            browse_lifecycle AS MATERIALIZED (
                    SELECT 'lifecycle' AS branch,h.history_event_id AS sort_id,h.occurred_utc,h.rowid AS sequence,
                           o.pair_id,o.operation_id,{lifecycleCategories} AS categories
                    FROM history_event h INDEXED BY history_event_occurred_utc
                    CROSS JOIN operation o ON o.operation_id=h.operation_id
                    WHERE o.pair_id IS NOT NULL AND ($detailed_log=0 OR
                        h.event_kind IN ('unissued_lease_recovered','attempt_reconciled'))
                      AND ($after_sequence IS NULL OR h.rowid>$after_sequence)
                      AND ($through_sequence IS NULL OR h.rowid<=$through_sequence)
                      {CandidateCategory(lifecycleCategories)}
                    {pageOrder} LIMIT $candidate_limit
            ),
            browse_presentations AS MATERIALIZED (
                    SELECT 'presentation' AS branch,h.history_event_id AS sort_id,h.occurred_utc,h.rowid AS sequence,
                           NULL AS pair_id,NULL AS operation_id,{presentationCategories} AS categories
                    FROM history_event h INDEXED BY history_event_occurred_utc
                    WHERE h.event_kind='presentation'
                      AND ($after_sequence IS NULL OR h.rowid>$after_sequence)
                      AND ($through_sequence IS NULL OR h.rowid<=$through_sequence)
                      {CandidateCategory(presentationCategories)}
                    {pageOrder} LIMIT $candidate_limit
            ),
            browse_candidates AS MATERIALIZED (
                SELECT branch,sort_id,occurred_utc,sequence,pair_id,operation_id FROM (
                    SELECT * FROM browse_decisions UNION ALL
                    SELECT * FROM browse_lifecycle UNION ALL
                    SELECT * FROM browse_presentations
                )
                {pageOrder}
                LIMIT $limit OFFSET $offset
            ),
            browse_page AS MATERIALIZED (
                SELECT b.branch,b.sort_id,b.occurred_utc,b.sequence,
                       CASE WHEN b.branch='presentation' THEN coalesce(json_extract(h.detail_json,'$.PairId'),'')
                            ELSE b.pair_id END AS pair_id,
                       CASE WHEN b.branch='presentation' THEN json_extract(h.detail_json,'$.OperationId')
                            ELSE b.operation_id END AS operation_id
                FROM browse_candidates b
                LEFT JOIN history_event h ON b.branch='presentation' AND h.history_event_id=b.sort_id
            ),
            """ : "";
        var decisionPageJoin = boundedBrowse
            ? "JOIN browse_page b ON b.branch='decision' AND b.sort_id=d.reconciliation_decision_id" : "";
        var lifecyclePageJoin = boundedBrowse
            ? "JOIN browse_page b ON b.branch='lifecycle' AND b.sort_id=h.history_event_id" : "";
        var presentationPageJoin = boundedBrowse
            ? "JOIN browse_page b ON b.branch='presentation' AND b.sort_id=h.history_event_id" : "";
        var memberPageScope = boundedBrowse
            ? "WHERE operation_id IN (SELECT operation_id FROM browse_page)"
            : runScoped
                ? "WHERE operation_id IN (SELECT json_extract(h.detail_json,'$.OperationId') FROM history_event h WHERE h.event_kind='presentation' AND coalesce(json_extract(h.detail_json,'$.RunId'),json_extract(h.detail_json,'$.RelatedRunId'))=$run_id)"
                : "";
        var contextDemand = boundedBrowse ? """
            context_demand AS MATERIALIZED (
                SELECT DISTINCT pair_id,operation_id FROM projected
                WHERE branch<>'presentation' AND own_context IS NULL
                  AND coalesce(operation_id,'')<>''
            ),
            """ : "";
        var contextReadGate = runScoped ? "AND 0" : boundedBrowse
            ? "AND EXISTS (SELECT 1 FROM context_demand)" : "";
        // Drive an exact indexed lookup from the bounded demand. An empty demand
        // performs no history scan, and every matching conflicting context still
        // participates in the existing DISTINCT ambiguity refusal.
        var contextFrom = boundedBrowse && !runScoped
            ? "context_demand demand CROSS JOIN history_event h INDEXED BY history_event_endpoint_context_operation"
            : "history_event h";
        var contextPageScope = boundedBrowse && !runScoped ? """
            AND json_extract(h.detail_json,'$.PairId')=demand.pair_id
            AND json_extract(h.detail_json,'$.OperationId')=demand.operation_id
            """ : "";
        var finalPageLimit = boundedBrowse ? "" : "LIMIT $limit OFFSET $offset";
        // CCS3/CCS5/S10: ONE shared reference-resolution expression, used for a row's own context and for the
        // exact pair-and-operation linked context. A row's effective endpoint context is its own retained
        // inline context, or one internal reference in EITHER stored form. A schema 10 row names a detached
        // immutable activity_context snapshot by its exact primary key; a legacy row names the already-inserted
        // full-context anchor event by its exact history_event primary key. Both are exact primary-key reads
        // (no whole-history scan, no chain, and no new index for either form).
        //
        // The detached form is accepted only when the retained snapshot itself agrees with the event: the
        // snapshot's own pair and optional run must equal the event's, and the snapshot context must name that
        // same pair. The legacy form keeps its unchanged one-hop conditions: same pair, same optional run and
        // an anchor whose own context is a real inline snapshot. Any missing, foreign, malformed, inconsistent
        // or recursive reference yields no context at all, so an unusable reference stays visibly unknown
        // instead of becoming a current pair, a legacy connection or a neighbouring operation's account.
        //
        // The existing partial index history_event_endpoint_context_operation already covers the new form: its
        // predicate tests json_extract(detail_json,'$.EndpointContext') IS NOT NULL, which is true for the
        // reference object itself, so no index or filter change is required for it.
        string ResolveEndpointContext(string detailJson) => $$"""
            (SELECT CASE
                    WHEN json_type({{detailJson}},'$.EndpointContext.EndpointContextKey') IS NOT NULL
                      AND json_type({{detailJson}},'$.EndpointContext.EndpointContextEventId') IS NULL
                         THEN (SELECT CASE
                                          WHEN snapshot.pair_id=coalesce(json_extract({{detailJson}},'$.PairId'),'')
                                            AND snapshot.run_id IS json_extract({{detailJson}},'$.RunId')
                                            AND json_extract(snapshot.context_json,'$.PairId')=snapshot.pair_id
                                          THEN snapshot.context_json END
                               FROM activity_context snapshot
                               WHERE snapshot.context_key=json_extract({{detailJson}},'$.EndpointContext.EndpointContextKey'))
                    WHEN json_type({{detailJson}},'$.EndpointContext.EndpointContextEventId') IS NULL
                      AND json_type({{detailJson}},'$.EndpointContext.EndpointContextKey') IS NULL
                         THEN json_extract({{detailJson}},'$.EndpointContext')
                    WHEN json_type({{detailJson}},'$.EndpointContext.EndpointContextEventId')='text'
                      AND json_extract({{detailJson}},'$.EndpointContext.EndpointContextEventId')<>''
                         THEN (SELECT json_extract(anchor.detail_json,'$.EndpointContext')
                               FROM history_event anchor
                               WHERE anchor.history_event_id=json_extract({{detailJson}},'$.EndpointContext.EndpointContextEventId')
                                 AND anchor.event_kind='presentation'
                                 AND json_extract(anchor.detail_json,'$.EventId')=anchor.history_event_id
                                 AND json_extract(anchor.detail_json,'$.EndpointContext') IS NOT NULL
                                 AND json_type(anchor.detail_json,'$.EndpointContext.EndpointContextEventId') IS NULL
                                 AND json_extract(anchor.detail_json,'$.PairId')=json_extract({{detailJson}},'$.PairId')
                                 AND json_extract(anchor.detail_json,'$.RunId') IS json_extract({{detailJson}},'$.RunId'))
                         ELSE NULL
                    END)
            """;
        // CCS5/S10: a row that carries an explicit context reference in EITHER stored form is context-bearing.
        // When its reference cannot be resolved the row stays unknown; it is never attributed through its
        // retained legacy ConnectionId.
        var explicitContextReference =
            "CASE WHEN json_type(h.detail_json,'$.EndpointContext.EndpointContextKey') IS NOT NULL " +
            "OR json_type(h.detail_json,'$.EndpointContext.EndpointContextEventId') IS NOT NULL THEN 1 ELSE 0 END";
        command.CommandText = $"""
            WITH {browsePage}
            activity_members AS MATERIALIZED (
                SELECT operation_id,expected_path_key,role,object_id,object_version_id,root_binding_id
                FROM operation_member {memberPageScope}
            ),
            projected AS (
                SELECT d.pair_id,
                       coalesce(json_extract(d.decision_json,'$.path'),'<item>') AS path,
                       d.decision_kind,
                       coalesce(json_extract(d.decision_json,'$.reason'),'No retained explanation.') AS reason,
                       json_extract(d.decision_json,'$.operationId') AS operation_id,
                       o.kind AS operation_kind,o.status AS operation_status,
                       NULL AS event_kind,d.decided_utc AS occurred_utc,
                       d.reconciliation_decision_id AS sort_id,
                       coalesce((SELECT lo.object_kind FROM logical_object lo WHERE lo.object_id=d.object_id),
                                'unknown') AS item_kind,
                       'decision:' || d.reconciliation_decision_id AS event_id,
                       NULL AS sequence,{decisionCategories} AS categories,
                       CASE WHEN d.decision_kind IN ('blocked','conflict','tombstone') THEN 'Warning' ELSE 'Information' END AS severity,
                       CASE WHEN o.kind='allocate_control' THEN 'Folder pair' ELSE NULL END AS entity,
                       NULL AS account_id, NULL AS run_id, NULL AS action,
                       'decision' AS branch,json_extract(d.decision_json,'$.endpointContext') AS own_context,NULL AS legacy_connection_id,
                       NULL AS retained_phase,0 AS explicit_reference
                FROM reconciliation_decision d
                {decisionPageJoin}
                LEFT JOIN operation o ON o.operation_id=json_extract(d.decision_json,'$.operationId')
                WHERE $run_id=''
                UNION ALL
                SELECT o.pair_id,
                       coalesce((SELECT expected_path_key FROM activity_members m
                                 WHERE m.operation_id=o.operation_id AND expected_path_key IS NOT NULL
                                 ORDER BY CASE role WHEN 'affected' THEN 0 WHEN 'source' THEN 1 ELSE 2 END
                                 LIMIT 1),'<operation>') AS path,
                       'lifecycle' AS decision_kind,h.event_kind AS reason,o.operation_id,
                       CASE WHEN h.event_kind='attempt_reconciled' THEN NULL ELSE o.kind END AS operation_kind,o.status AS operation_status,
                       h.event_kind,h.occurred_utc,h.history_event_id AS sort_id,
                       coalesce((SELECT lo.object_kind FROM activity_members m
                                 JOIN logical_object lo ON lo.object_id=m.object_id
                                 WHERE m.operation_id=o.operation_id
                                 ORDER BY CASE role WHEN 'affected' THEN 0 WHEN 'source' THEN 1 ELSE 2 END
                                 LIMIT 1),'unknown') AS item_kind,
                       'history:' || h.history_event_id,h.rowid,
                       {lifecycleCategories},
                       CASE WHEN h.event_kind='operation_completed' THEN 'Success' ELSE 'Information' END,
                       CASE WHEN h.event_kind='attempt_reconciled' THEN 'Application'
                            WHEN o.kind='allocate_control' THEN 'Folder pair' ELSE NULL END,NULL,NULL,NULL,
                       'lifecycle',NULL,NULL,NULL,0
                FROM history_event h
                {lifecyclePageJoin}
                JOIN operation o ON o.operation_id=h.operation_id
                WHERE o.pair_id IS NOT NULL AND $run_id=''
                UNION ALL
                SELECT coalesce(json_extract(h.detail_json,'$.PairId'),''),
                       coalesce(json_extract(h.detail_json,'$.RelativePath'),''),
                       'presentation',json_extract(h.detail_json,'$.Message'),
                       json_extract(h.detail_json,'$.OperationId'),
                       syncshark_activity_kind(json_extract(h.detail_json,'$.OperationKind')),NULL,NULL,
                       h.occurred_utc,h.history_event_id,
                       CASE json_extract(h.detail_json,'$.Entity') WHEN 'File' THEN 'file' WHEN 'Folder' THEN 'directory' ELSE 'unknown' END,
                       'history:' || h.history_event_id,h.rowid,
                       {presentationCategories},
                       json_extract(h.detail_json,'$.Severity'),json_extract(h.detail_json,'$.Entity'),
                       json_extract(h.detail_json,'$.ConnectionId'),coalesce(json_extract(h.detail_json,'$.RunId'),json_extract(h.detail_json,'$.RelatedRunId')),
                       json_extract(h.detail_json,'$.Action'),
                       'presentation',{ResolveEndpointContext("h.detail_json")},
                       json_extract(h.detail_json,'$.ConnectionId'),json_extract(h.detail_json,'$.Phase'),
                       {explicitContextReference} AS explicit_reference
                FROM history_event h {presentationPageJoin} WHERE h.event_kind='presentation'
                    AND ($run_id='' OR coalesce(json_extract(h.detail_json,'$.RunId'),json_extract(h.detail_json,'$.RelatedRunId'))=$run_id)
            ),
            {contextDemand}
            -- EPS2: the one unambiguous retained endpoint context for an operation inside its own pair.
            -- Only an exact pair id AND operation id match counts, and matching records that disagree
            -- contribute no context at all rather than an arbitrary, latest-by-pair or current one.
            linked_context AS (
                SELECT pair_id,operation_id,min(context_json) AS context_json
                FROM (
                    SELECT json_extract(h.detail_json,'$.PairId') AS pair_id,
                           json_extract(h.detail_json,'$.OperationId') AS operation_id,
                           {ResolveEndpointContext("h.detail_json")} AS context_json
                    FROM {contextFrom}
                    WHERE h.event_kind='presentation'
                      {contextReadGate}
                      AND json_extract(h.detail_json,'$.EndpointContext') IS NOT NULL
                      AND coalesce(json_extract(h.detail_json,'$.OperationId'),'')<>''
                      {contextPageScope}
                )
                GROUP BY pair_id,operation_id
                HAVING count(DISTINCT context_json)=1
            ),
            -- Prefer a row's own retained context. Legacy decisions and lifecycle rows may reuse only
            -- exact pair-and-operation-linked context; never replace a present but invalid own context.
            resolved AS (
                SELECT p.*,CASE WHEN p.branch='presentation' THEN p.own_context WHEN p.own_context IS NOT NULL THEN p.own_context ELSE l.context_json END AS context_json
                FROM projected p
                LEFT JOIN linked_context l ON p.branch<>'presentation' AND l.pair_id=p.pair_id
                                          AND l.operation_id=p.operation_id
            ),
            -- The shared validation boundary: the recorded cloud accounts a retained context may be
            -- attributed to, or no answer at all when that context is missing, malformed, foreign,
            -- ambiguous or names an endpoint kind or provider this build does not register.
            checked AS (
                SELECT r.*,syncshark_endpoint_context_accounts(r.context_json,r.pair_id) AS context_account_ids
                FROM resolved r
            ),
            -- The accounts a row may be attributed to: the exact recorded cloud connections of its valid
            -- context, or its explicitly retained legacy single connection when no usable context exists.
            -- A local/local context, an unknown context and an unknown account attribute nothing, so the
            -- row stays visible under All Accounts instead of whichever account owns the pair now. A row that
            -- carries an explicit context reference is context-bearing: an unresolved reference stays unknown
            -- and is never re-attributed through the legacy connection it happens to retain (CCS5).
            scoped AS (
                SELECT c.*,
                       CASE WHEN c.context_account_ids IS NOT NULL THEN c.context_account_ids
                            WHEN c.explicit_reference=1 THEN json_array()
                            WHEN c.legacy_connection_id IS NOT NULL THEN json_array(c.legacy_connection_id)
                            ELSE json_array() END AS account_ids
                FROM checked c
            ),
            -- Bound the fully filtered and ordered page before display enrichment.
            -- Filters still see all retained rows and the same account validator.
            page AS MATERIALIZED (
                SELECT * FROM scoped
            WHERE ($detailed_log=0 OR branch='presentation' OR event_kind IN ('unissued_lease_recovered','attempt_reconciled'))
              AND ($after_sequence IS NULL OR sequence>$after_sequence)
              AND ($search='' OR path LIKE $search OR reason LIKE $search OR pair_id LIKE $search OR
                   decision_kind LIKE $search OR coalesce(operation_id,'') LIKE $search OR
                   coalesce(operation_kind,'') LIKE $search OR
                   coalesce(operation_status,'') LIKE $search OR coalesce(event_kind,'') LIKE $search OR
                   coalesce(action,'') LIKE $search)
              AND ($filter IN ('All','All actions')
                   OR ($filter='Changes' AND decision_kind NOT IN ('unchanged','initial_observation','adopted'))
                   OR ($filter='Transfers' AND operation_kind IN
                       ('create_remote','update_remote','create_local','update_local'))
                   OR ($filter='Moves' AND operation_kind IN ('move_local','move_remote','cross_pair_move'))
                   OR ($filter='Conflicts' AND
                       (decision_kind='conflict' OR operation_kind='resolve_conflict'))
                   OR ($filter='Recovery / deletions' AND
                       (operation_kind IN ('delete_local','delete_remote') OR decision_kind='tombstone'))
                   OR ($filter='Needs attention' AND
                       (decision_kind IN ('blocked','conflict','tombstone') OR
                        operation_status IN ('blocked','effect_unknown') OR
                        -- ENG4: a retained warning or error presentation event is a failure the operator must be
                        -- able to reach through the same failure filter, for example a reviewed first sync that
                        -- stopped or that still has items waiting.
                        severity IN ('Warning','Error'))))
              AND ($pair_id='' OR pair_id=$pair_id)
              AND ($allowed_pairs='' OR pair_id IN (SELECT value FROM json_each($allowed_pairs)) OR
                   (pair_id='' AND $account_id<>'' AND
                    EXISTS(SELECT 1 FROM json_each(account_ids) WHERE value=$account_id)))
              AND ($account_id='' OR EXISTS(SELECT 1 FROM json_each(account_ids) WHERE value=$account_id))
              AND ($categories='' OR EXISTS (SELECT 1 FROM json_each(categories) tag
                   JOIN json_each($categories) selected ON selected.value=tag.value))
              AND ($severity='' OR severity=$severity)
              AND ($entity='' OR coalesce(entity,CASE item_kind WHEN 'file' THEN 'File' WHEN 'directory' THEN 'Folder' ELSE 'Unknown' END)=$entity)
              AND ($run_id='' OR run_id=$run_id)
              AND ($through_sequence IS NULL OR sequence <= $through_sequence)
              AND ($since_utc='' OR occurred_utc >= $since_utc)
              AND ($item_type IN ('','All types')
                   OR ($item_type='Folders' AND item_kind='directory')
                   OR ($item_type='Files' AND item_kind='file')
                   OR (item_kind='file' AND lower(path) LIKE '%.' || lower(ltrim($item_type,'.'))))
            {pageOrder}
            {finalPageLimit}
            )
            SELECT occurred_utc,pair_id,path,decision_kind,reason,operation_id,operation_kind,operation_status,
                   event_kind,item_kind,
                   CASE WHEN branch='presentation' AND EXISTS(SELECT 1 FROM history_event h
                        WHERE h.history_event_id=scoped.sort_id
                          AND json_type(h.detail_json,'$.OperationSnapshot')='object')
                   THEN (SELECT json_extract(h.detail_json,'$.OperationSnapshot.SizeBytes')
                         FROM history_event h WHERE h.history_event_id=scoped.sort_id)
                   ELSE (SELECT v.size_bytes FROM activity_members m JOIN object_version v
                    ON v.object_version_id=m.object_version_id WHERE m.operation_id=scoped.operation_id
                    ORDER BY CASE m.role WHEN 'source' THEN 0 WHEN 'affected' THEN 1 ELSE 2 END LIMIT 1) END,
                   CASE WHEN branch='presentation' AND EXISTS(SELECT 1 FROM history_event h
                        WHERE h.history_event_id=scoped.sort_id
                          AND json_type(h.detail_json,'$.OperationSnapshot')='object')
                   THEN (SELECT json_extract(h.detail_json,'$.OperationSnapshot.LocalRoot')
                         FROM history_event h WHERE h.history_event_id=scoped.sort_id)
                   ELSE (SELECT CASE WHEN count(DISTINCT r.display_path)=1 THEN min(r.display_path) END
                    FROM activity_members m JOIN root_binding r ON r.root_binding_id=m.root_binding_id
                    WHERE m.operation_id=scoped.operation_id AND r.side='local') END,
                   CASE WHEN branch='presentation' AND EXISTS(SELECT 1 FROM history_event h
                        WHERE h.history_event_id=scoped.sort_id
                          AND json_type(h.detail_json,'$.OperationSnapshot')='object')
                   THEN (SELECT json_extract(h.detail_json,'$.OperationSnapshot.RemoteRoot')
                         FROM history_event h WHERE h.history_event_id=scoped.sort_id)
                   ELSE (SELECT CASE WHEN count(DISTINCT r.display_path)=1 THEN min(r.display_path) END
                    FROM activity_members m JOIN root_binding r ON r.root_binding_id=m.root_binding_id
                    WHERE m.operation_id=scoped.operation_id AND r.side='dropbox') END,
                   event_id,sequence,categories,severity,entity,account_id,run_id,action,
                   CASE WHEN context_account_ids IS NOT NULL THEN context_json END,retained_phase,
                   CASE WHEN branch='presentation' THEN (SELECT json_extract(h.detail_json,'$.Progress')
                        FROM history_event h WHERE h.history_event_id=scoped.sort_id) END
            FROM page scoped
            {pageOrder};
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$search", string.IsNullOrWhiteSpace(search)
            ? string.Empty : "%" + search.Trim() + "%");
        command.Parameters.AddWithValue("$filter", normalizedFilter);
        command.Parameters.AddWithValue("$item_type", string.IsNullOrWhiteSpace(itemType) ? "All types" : itemType.Trim());
        command.Parameters.AddWithValue("$pair_id", string.IsNullOrWhiteSpace(pairId) ? string.Empty : pairId);
        command.Parameters.AddWithValue("$allowed_pairs", allowedPairIds is null ? "" : JsonSerializer.Serialize(allowedPairIds));
        command.Parameters.AddWithValue("$since_utc", sinceUtc?.ToUniversalTime().ToString("O") ?? string.Empty);
        command.Parameters.AddWithValue("$categories", query?.Categories is { Count: > 0 } tags ? JsonSerializer.Serialize(tags) : "");
        command.Parameters.AddWithValue("$severity", query?.Severity ?? "");
        command.Parameters.AddWithValue("$entity", query?.Entity ?? "");
        command.Parameters.AddWithValue("$run_id", query?.RunId ?? "");
        command.Parameters.AddWithValue("$account_id", connectionId ?? "");
        command.Parameters.AddWithValue("$oldest", query?.OldestFirst == true ? 1 : 0);
        command.Parameters.AddWithValue("$detailed_log", query?.DetailedLog == true ? 1 : 0);
        command.Parameters.AddWithValue("$newest_log", query?.NewestLogFirst == true ? 1 : 0);
        command.Parameters.AddWithValue("$after_sequence", (object?)query?.AfterSequence ?? DBNull.Value);
        command.Parameters.AddWithValue("$through_sequence", (object?)query?.ThroughSequence ?? DBNull.Value);
        command.Parameters.AddWithValue("$candidate_limit", (long)offset + limit);
        return await ReadWithNativeCancellationAsync(connection, async () =>
        {
        var result = new List<SyncAuthorityActivityProjection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var operationId = reader.IsDBNull(5) ? null : reader.GetString(5);
            var operationKind = reader.IsDBNull(6) ? null : reader.GetString(6);
            var operationStatus = reader.IsDBNull(7) ? null : reader.GetString(7);
            var eventKind = reader.IsDBNull(8) ? null : reader.GetString(8);
            var itemKind = reader.IsDBNull(9) ? "unknown" : reader.GetString(9);
            var reason = reader.GetString(4);
            if (eventKind is not null)
                reason = ProjectionHistoryEvent(eventKind, operationKind);
            if (operationStatus is not null && eventKind is null)
                reason += " Current durable state: " + ProjectionOperationState(operationStatus) + ".";
            result.Add(new SyncAuthorityActivityProjection(
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetString(1),
                reader.GetString(2), reader.GetString(3) == "presentation" ? reader.GetString(20) : eventKind is null
                    ? ProjectionActivityOutcome(reader.GetString(3), operationKind, operationStatus)
                    : ProjectionHistoryOutcome(eventKind, operationKind),
                reason, operationId, ActivityItemKind(itemKind), ActivityFileType(reader.GetString(2), itemKind),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetInt64(14),
                JsonSerializer.Deserialize<string[]>(reader.GetString(15)), reader.GetString(16),
                reader.IsDBNull(17) ? ActivityItemKind(itemKind) : reader.GetString(17),
                reader.IsDBNull(18) ? null : reader.GetString(18), reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.IsDBNull(21) ? null : JsonSerializer.Deserialize<SyncEndpointContext>(reader.GetString(21)),
                SyncEndpointActivityPresentation.KindForRetainedCode(operationKind),
                reader.GetString(3) == "presentation"
                    ? SyncEndpointActivityPresentation.PhaseForJsonName(reader.IsDBNull(22) ? null : reader.GetString(22))
                    : eventKind is not null ? SyncEndpointActivityPresentation.PhaseForHistoryEvent(eventKind)
                    : SyncEndpointActivityPresentation.PhaseForDecision(reader.GetString(3)),
                reader.GetString(3) is "presentation" or "lifecycle" ? null : reader.GetString(3),
                reader.IsDBNull(23) ? null : JsonSerializer.Deserialize<SyncActivityProgress>(reader.GetString(23))));
        }
        return result;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SyncAuthorityQueueProjection>> LoadQueueProjectionAsync(int limit = 250,
        CancellationToken cancellationToken = default) =>
        await LoadQueueProjectionPageAsync(limit, 0, null, "All", cancellationToken);

    // Live projections share one retained-pair boundary. Retiring a pair preserves
    // its failure journal; it must not leave that history in the current queue.
    private const string CurrentPairProjectionPredicate =
        "pair_id IN (SELECT pair_id FROM sync_pair WHERE state<>'retired')";

    public async Task<int> LoadPendingQueueCountAsync(CancellationToken cancellationToken = default,
        IReadOnlyList<string>? allowedPairIds = null)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return checked((int)await ScalarLongAsync(connection, null,
            $"SELECT count(*) FROM operation WHERE pair_id IS NOT NULL AND {CurrentPairProjectionPredicate} AND status NOT IN ('completed','cancelled','superseded') AND ($pairs IS NULL OR pair_id IN (SELECT value FROM json_each($pairs)));",
            cancellationToken, ("$pairs", allowedPairIds is null ? null : System.Text.Json.JsonSerializer.Serialize(allowedPairIds))));
    }

    public async Task<IReadOnlySet<string>> LoadPairIdsNeedingAttentionAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT DISTINCT pair_id
            FROM (
                SELECT pair_id FROM operation
                WHERE pair_id IS NOT NULL AND {CurrentPairProjectionPredicate}
                  AND status IN ('blocked','effect_unknown','cleanup_pending')
                UNION ALL
                SELECT pair_id FROM conflict
                WHERE {CurrentPairProjectionPredicate}
                  AND status IN ('open','resolution_requested','resolving')
            )
            ORDER BY pair_id;
            """;
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    // EPS2: the activity page reads the account a retained record may be attributed to from the record's
    // own endpoint context. Validating that context in SQL would restate the EPS1 contract rules in a
    // second place, where they could drift; the page query therefore asks this one connection-local
    // lookup, which runs the same shared validator the append path runs. It is a scalar, deterministic,
    // side-effect-free function over the bounded row value, not a schema object, service or extra query
    // per result, and it never reaches a provider, credential or the currently selected account.
    private static void RegisterRetainedEndpointContextLookup(SqliteConnection connection)
    {
        // One statement-local memo of this pure validation, including rejected values.
        // The exact pair AND raw JSON are the key; current account/pair state is never used.
        // Overflow recomputes normally instead of changing attribution or retaining a global cache.
        var memo = new Dictionary<(string Pair, string Json), string?>();
        long retainedBytes = 0;
        connection.CreateFunction<object?, object?, string?>("syncshark_endpoint_context_accounts",
            (contextJson, pairId) =>
            {
                if (contextJson is not string json || pairId is not string pair)
                    return RetainedEndpointContextAccounts(contextJson, pairId);
                if (memo.TryGetValue((pair, json), out var retained)) return retained;
                var accounts = RetainedEndpointContextAccounts(json, pair);
                var bytes = 2L * (json.Length + (long)pair.Length + (accounts?.Length ?? 0)) + 128;
                if (memo.Count < 256 && bytes <= 1048576 - retainedBytes)
                {
                    memo.Add((pair, json), accounts);
                    retainedBytes += bytes;
                }
                return accounts;
            }, isDeterministic: true);
    }

    /// <summary>
    /// The recorded cloud accounts a retained endpoint context may be attributed to, as a JSON array, or
    /// null when that context is missing, malformed, foreign, ambiguous or names an endpoint kind or
    /// provider this build does not register. An unknown context stays unknown: it never becomes the
    /// currently selected, primary or first available account, and it never fails the whole page.
    /// </summary>
    private static string? RetainedEndpointContextAccounts(object? contextJson, object? pairId)
    {
        if (contextJson is not string json || pairId is not string pair) return null;
        try
        {
            var context = JsonSerializer.Deserialize<SyncEndpointContext>(json);
            if (context is null || SyncEndpointContext.Validate(context, pair).Count != 0) return null;
            // A local/local context is valid and carries no account at all, so it attributes nothing.
            return JsonSerializer.Serialize(new[] { context.Left.ConnectionId, context.Right.ConnectionId }
                .Where(connectionId => connectionId is not null).ToArray());
        }
        catch (JsonException)
        {
            // A retained value this build cannot read is unknown presentation state, not a page failure.
            return null;
        }
    }

    private static string ActivityItemKind(string itemKind) =>
        string.Equals(itemKind, "directory", StringComparison.OrdinalIgnoreCase) ? "Folder" :
        string.Equals(itemKind, "file", StringComparison.OrdinalIgnoreCase) ? "File" : "Unknown";

    private static string ActivityFileType(string path, string itemKind)
    {
        if (string.Equals(itemKind, "directory", StringComparison.OrdinalIgnoreCase)) return "Folder";
        if (!string.Equals(itemKind, "file", StringComparison.OrdinalIgnoreCase)) return "Unknown";
        var extension = Path.GetExtension(path);
        return string.IsNullOrWhiteSpace(extension) ? "File" : extension.TrimStart('.').ToUpperInvariant();
    }

    public async Task<IReadOnlyList<SyncAuthorityQueueProjection>> LoadQueueProjectionPageAsync(
        int limit = 250, int offset = 0, string? search = null, string? filter = null,
        CancellationToken cancellationToken = default, string? pairId = null, string? itemType = null,
        DateTimeOffset? sinceUtc = null, IReadOnlyList<string>? allowedPairIds = null)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH queue_members AS MATERIALIZED (SELECT * FROM operation_member),
            projected AS (
                SELECT o.operation_id,o.pair_id,
                       coalesce((SELECT m.expected_path_key FROM queue_members m
                                 WHERE m.operation_id=o.operation_id AND m.expected_path_key IS NOT NULL
                                 ORDER BY CASE m.role WHEN 'destination' THEN 0 WHEN 'affected' THEN 1
                                          WHEN 'source' THEN 2 ELSE 3 END,m.operation_member_id LIMIT 1),
                                '<operation>') AS path,
                       o.created_utc,o.priority,o.kind,o.status,
                       CASE WHEN o.status IN ('effect_issuing','effect_unknown','effect_observed','reconciling','verifying','committed','cleanup_pending')
                                 OR EXISTS(SELECT 1 FROM operation_attempt a WHERE a.operation_id=o.operation_id
                                           AND a.status IN ('started','request_sent','response_received','effect_unknown'))
                            THEN 1 ELSE 0 END AS recovery_required
                FROM operation o
                WHERE o.pair_id IS NOT NULL AND o.status NOT IN ('completed','cancelled','superseded')
                  AND {CurrentPairProjectionPredicate}
            )
            SELECT operation_id,pair_id,path,created_utc,priority,kind,status,recovery_required,
                   (SELECT lo.object_kind FROM queue_members m JOIN logical_object lo ON lo.object_id=m.object_id
                    WHERE m.operation_id=projected.operation_id
                    ORDER BY CASE m.role WHEN 'source' THEN 0 WHEN 'affected' THEN 1 ELSE 2 END LIMIT 1) AS item_kind
            FROM projected
            WHERE ($search='' OR path LIKE $search OR pair_id LIKE $search OR kind LIKE $search OR
                   status LIKE $search)
              AND ($filter='All'
                   OR ($filter='Needs recovery' AND recovery_required=1)
                   OR ($filter='Blocked' AND status IN ('blocked','effect_unknown'))
                   OR ($filter='Transfers' AND kind IN
                       ('create_remote','update_remote','create_local','update_local'))
                   OR ($filter='Moves' AND kind IN ('move_local','move_remote','cross_pair_move')))
              AND ($pair='' OR pair_id=$pair)
              AND ($allowed_pairs='' OR pair_id IN (SELECT value FROM json_each($allowed_pairs)))
              AND ($since='' OR created_utc >= $since)
              AND ($type IN ('','All types') OR ($type='Folders' AND item_kind='directory')
                   OR ($type='Files' AND item_kind='file')
                   OR (item_kind='file' AND lower(path) LIKE '%.' || lower(ltrim($type,'.'))))
            ORDER BY recovery_required DESC,priority DESC,created_utc,operation_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$search", string.IsNullOrWhiteSpace(search)
            ? string.Empty : "%" + search.Trim() + "%");
        command.Parameters.AddWithValue("$filter", string.IsNullOrWhiteSpace(filter) ? "All" : filter.Trim());
        command.Parameters.AddWithValue("$pair", pairId ?? string.Empty);
        command.Parameters.AddWithValue("$allowed_pairs", allowedPairIds is null ? "" : JsonSerializer.Serialize(allowedPairIds));
        command.Parameters.AddWithValue("$type", itemType ?? string.Empty);
        command.Parameters.AddWithValue("$since", sinceUtc is { } since ? Utc(since) : string.Empty);
        var result = new List<SyncAuthorityQueueProjection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SyncAuthorityQueueProjection(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.GetInt32(4),
                ProjectionOperationName(reader.GetString(5)), ProjectionOperationState(reader.GetString(6)),
                reader.GetInt32(7) == 1,
                ActivityItemKind(reader.IsDBNull(8) ? "unknown" : reader.GetString(8)),
                ActivityFileType(reader.GetString(2), reader.IsDBNull(8) ? "unknown" : reader.GetString(8))));
        }
        return result;
    }

    internal async Task<bool> HasPendingPairDirtyHintForTestAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarLongAsync(connection, null,
            "SELECT count(*) FROM dirty_item WHERE pair_id=$pair AND normalized_path_key IS NULL AND reconciliation_level='pair' AND status='dirty';",
            cancellationToken, ("$pair", pairId)) > 0;
    }

    public async Task<IReadOnlyList<SyncAuthorityPathState>> LoadPairPathStatesAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH path_state(relative_path,pending,error) AS (
                SELECT coalesce((SELECT m.expected_path_key FROM operation_member m
                                 WHERE m.operation_id=o.operation_id AND m.expected_path_key IS NOT NULL
                                 ORDER BY CASE m.role WHEN 'destination' THEN 0 WHEN 'affected' THEN 1
                                          WHEN 'source' THEN 2 ELSE 3 END,m.operation_member_id LIMIT 1),
                                '<operation>'),
                       CASE WHEN o.status='blocked' THEN 0 ELSE 1 END,
                       CASE WHEN o.status IN ('blocked','effect_unknown') THEN 1 ELSE 0 END
                FROM operation o
                WHERE o.pair_id=$pair AND o.status NOT IN ('completed','cancelled','superseded')
                UNION ALL
                SELECT coalesce(lm.relative_path_key,dm.relative_path_key),0,1
                FROM conflict c
                LEFT JOIN conflict_version lv ON lv.conflict_id=c.conflict_id AND lv.side='local'
                LEFT JOIN conflict_version dv ON dv.conflict_id=c.conflict_id AND dv.side='dropbox'
                LEFT JOIN membership lm ON lm.membership_id=lv.membership_id
                LEFT JOIN membership dm ON dm.membership_id=dv.membership_id
                WHERE c.pair_id=$pair AND c.status IN ('open','resolution_requested','resolving')
                  AND coalesce(lm.relative_path_key,dm.relative_path_key) IS NOT NULL
            )
            SELECT relative_path,max(pending),max(error)
            FROM path_state
            WHERE relative_path<>'<operation>'
            GROUP BY relative_path COLLATE NOCASE
            ORDER BY relative_path COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        var result = new List<SyncAuthorityPathState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new SyncAuthorityPathState(reader.GetString(0), reader.GetInt32(1) == 1,
                reader.GetInt32(2) == 1));
        return result;
    }

    public async Task<IReadOnlyList<SyncRecoveryArtifactProjection>> LoadRecoveryArtifactsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ra.recovery_artifact_id,ra.operation_id,o.pair_id,ra.object_id,ra.side,
                   coalesce((SELECT expected_path_key FROM operation_member m
                             WHERE m.operation_id=ra.operation_id AND expected_path_key IS NOT NULL
                             ORDER BY CASE role WHEN 'affected' THEN 0 WHEN 'source' THEN 1 ELSE 2 END
                             LIMIT 1),json_extract(ra.identity_json,'$.originalPath'),'<unknown>'),
                   ra.identity_json,ra.content_hash,ra.size_bytes,ra.status,ra.retained_utc
            FROM recovery_artifact ra
            JOIN operation o ON o.operation_id=ra.operation_id
            WHERE o.pair_id IS NOT NULL
            ORDER BY ra.retained_utc DESC,ra.recovery_artifact_id;
            """;
        var result = new List<SyncRecoveryArtifactProjection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new SyncRecoveryArtifactProjection(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetInt64(8), reader.GetString(9),
                DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture)));
        return result;
    }

    public async Task<IReadOnlyList<SyncRecoveryArtifactProjection>> LoadRecoveryArtifactsPageAsync(
        string pairId, int limit, int offset = 0, string? search = null,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(pairId, nameof(pairId));
        // The public maximum stays SyncAuthorityPlanningReadLimits.RecoveryPagePublicMaximum (500). The
        // projection itself also accepts exactly one more row, which is only ever the paging sentinel the
        // public caller reads to decide whether another page exists, so a public page of 500 rows and a
        // 500/501/0 boundary proof remain truthful. The public guard itself is unchanged.
        if (limit is < 1 or > SyncAuthorityPlanningReadLimits.RecoveryPageSentinelMaximum)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH projected AS (
                SELECT ra.recovery_artifact_id,ra.operation_id,o.pair_id,ra.object_id,ra.side,
                       coalesce((SELECT expected_path_key FROM operation_member m
                                 WHERE m.operation_id=ra.operation_id AND expected_path_key IS NOT NULL
                                 ORDER BY CASE role WHEN 'affected' THEN 0 WHEN 'source' THEN 1 ELSE 2 END
                                 LIMIT 1),json_extract(ra.identity_json,'$.originalPath'),'<unknown>') AS original_path,
                       ra.identity_json,ra.content_hash,ra.size_bytes,ra.status,ra.retained_utc
                FROM recovery_artifact ra
                JOIN operation o ON o.operation_id=ra.operation_id
                WHERE o.pair_id=$pair AND ra.status<>'released'
            )
            SELECT recovery_artifact_id,operation_id,pair_id,object_id,side,original_path,identity_json,
                   content_hash,size_bytes,status,retained_utc
            FROM projected
            WHERE $search='' OR original_path LIKE $search OR side LIKE $search OR identity_json LIKE $search
            ORDER BY retained_utc DESC,recovery_artifact_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$pair", pairId);
        command.Parameters.AddWithValue("$search", string.IsNullOrWhiteSpace(search)
            ? string.Empty : "%" + search.Trim() + "%");
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var result = new List<SyncRecoveryArtifactProjection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new SyncRecoveryArtifactProjection(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetInt64(8), reader.GetString(9),
                DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture)));
        return result;
    }

    private static string ProjectionActivityOutcome(string decisionKind, string? operationKind,
        string? operationStatus)
    {
        if (operationKind is null || operationStatus is null)
            return decisionKind switch
            {
                "unchanged" => "Identical",
                "initial_observation" or "adopted" => "Baseline",
                "conflict" => "Conflict",
                "tombstone" or "blocked" => "Blocked",
                _ => char.ToUpperInvariant(decisionKind[0]) + decisionKind[1..]
            };
        if (operationStatus != "completed") return ProjectionOperationState(operationStatus);
        return operationKind switch
        {
            "create_remote" or "update_remote" => "Uploaded",
            "create_local" or "update_local" => "Downloaded",
            "delete_local" or "delete_remote" => "Archived",
            _ => "Recovered"
        };
    }

    private static string ProjectionOperationName(string operationKind) => operationKind switch
    {
        "create_remote" => "Upload new",
        "update_remote" => "Upload update",
        "create_local" => "Download new",
        "update_local" => "Download update",
        "delete_local" => "Archive local",
        "delete_remote" => "Archive Dropbox",
        "move_local" => "Move local",
        "move_remote" => "Move Dropbox",
        "resolve_conflict" => "Resolve conflict",
        "cross_pair_move" => "Cross-pair move",
        "cleanup" => "Cleanup",
        "reconcile" => "Reconcile",
        _ => operationKind
    };

    private static string ProjectionOperationState(string operationStatus) => operationStatus switch
    {
        "planned" or "ready" => "Queued",
        "leased" => "Leased",
        "validating" => "Planning",
        "allocation_authorized" or "staged" => "Staging",
        "effect_issuing" => "Effect issuing",
        "effect_unknown" => "Effect unknown",
        "effect_observed" or "verifying" => "Verifying",
        "reconciling" => "Reconciling",
        "committed" => "Committed",
        "cleanup_pending" => "Cleanup pending",
        "completed" => "Complete",
        "blocked" => "Blocked",
        "cancelled" => "Cancelled",
        "superseded" => "Superseded",
        _ => operationStatus
    };

    private static string ProjectionMonitorState(string monitorStatus) => monitorStatus switch
    {
        "active" => "Monitoring",
        "reset_required" => "Full reconciliation required",
        "unavailable" => "Monitor unavailable",
        "retired" => "Stopped",
        _ => monitorStatus
    };

    public async Task<string> GetSqliteVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarStringAsync(connection, null, "SELECT sqlite_version();", cancellationToken)
               ?? throw new InvalidDataException("SQLite did not report a runtime version.");
    }

    public async Task<string> QuickCheckAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarStringAsync(connection, null, "PRAGMA quick_check;", cancellationToken)
               ?? throw new InvalidDataException("SQLite quick_check returned no result.");
    }

    public async Task<string> IntegrityCheckAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ScalarStringAsync(connection, null, "PRAGMA integrity_check;", cancellationToken)
               ?? throw new InvalidDataException("SQLite integrity_check returned no result.");
    }

    private async Task TransitionAttemptAsync(string attemptId, SyncAuthorityLease lease,
        SyncAuthorityAttemptStatus status, bool terminal, CancellationToken cancellationToken)
    {
        RequireIdentifier(attemptId, nameof(attemptId));
        await WriteAsync(async (connection, transaction) =>
        {
            var operationId = await ScalarStringAsync(connection, transaction,
                "SELECT operation_id FROM operation_attempt WHERE attempt_id=$id;",
                cancellationToken, ("$id", attemptId)) ?? throw new InvalidDataException("The attempt is not retained.");
            await RequireFenceAsync(connection, transaction, operationId, lease, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "UPDATE operation_attempt SET status=$status,ended_utc=CASE WHEN $terminal=1 THEN $utc ELSE ended_utc END,transition_lease_id=$lease,transition_fencing_token=$token WHERE attempt_id=$id;",
                cancellationToken, ("$status", SyncAuthorityNames.AttemptStatus(status)),
                ("$terminal", terminal ? 1 : 0), ("$utc", Utc(DateTimeOffset.UtcNow)),
                ("$lease", lease.LeaseId), ("$token", lease.FencingToken), ("$id", attemptId));
        }, cancellationToken);
    }

    private async Task WriteAsync(Func<SqliteConnection, SqliteTransaction, Task> action,
        CancellationToken cancellationToken)
    {
        if (TentativeCommandInstallation is not null)
            throw new InvalidOperationException("Tentative command preparation cannot write authority state before fresh admission.");
        if (_executionConnectionScope.Value is { Active: true, BatchesWrites: true } scope)
        {
            await scope.WriteAsync(action, cancellationToken).ConfigureAwait(false);
            return;
        }
        var started = TimingStart();
        await _writer.WaitAsync(cancellationToken);
        ReportTiming("writer-wait", started);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            started = TimingStart();
            await action(connection, transaction);
            ReportTiming("write-body", started);
            started = TimingStart();
            await transaction.CommitAsync(cancellationToken);
            ReportTiming("commit", started);
        }
        finally
        {
            EndExecutionReservationTransaction();
            _writer.Release();
        }
    }

    private async Task<SqliteConnection> OpenReadOnlyConnectionAsync(CancellationToken cancellationToken)
    {
        EnsureSqliteBootstrap();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            ConfigureActionRetirementFunction(connection);
            await ExecuteAsync(connection, null, "PRAGMA query_only=ON; PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF;", cancellationToken);
            await RequireExactCurrentSchemaAsync(connection, cancellationToken, verifyHealth: false);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken,
        Action<string>? startupProgress = null, bool reuseConnection = true)
    {
        if (TentativeCommandInstallation is not null)
            return await OpenReadOnlyConnectionAsync(cancellationToken);
        // Full retained-data scans are explicit diagnostics or schema-creation/migration
        // checks, never implicit admission for an ordinary read or effect command.
        // Exact schema, completed-successor and connection guards still apply on every open.
        return await OpenConnectionAtPathAsync(_databasePath, SqliteOpenMode.ReadWrite,
            requireCurrentSchema: true, cancellationToken, verifyHealth: false,
            pooling: reuseConnection && _executionConnectionScope.Value is { Active: true });
    }

    public async Task VerifyHealthAsync(CancellationToken cancellationToken = default,
        Action<string>? startupProgress = null)
    {
        // Explicit full diagnostics use a bounded private read-only cache and native
        // cancellation, not a default-cache read/write connection. No verdict is cached.
        var version = await InspectReadOnlySchemaAsync(_databasePath, cancellationToken,
            startupProgress: startupProgress);
        if (version != SyncAuthoritySchema.Version)
            throw new InvalidDataException(
                $"Authority schema version {version} is not the required current version {SyncAuthoritySchema.Version}. No schema write was attempted.");
        await using var readiness = await OpenUnpooledInspectionConnectionAsync(_databasePath, cancellationToken);
        await RequireCompletedEssentialSuccessorAsync(readiness, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<SqliteConnection> OpenFreshConnectionAsync(CancellationToken cancellationToken)
        => await OpenConnectionAtPathAsync(_databasePath, SqliteOpenMode.ReadWrite, requireCurrentSchema: false,
            cancellationToken, freshDatabase: true);

    private static async Task<SqliteConnection> OpenConnectionAtPathAsync(string databasePath, SqliteOpenMode mode,
        bool requireCurrentSchema, CancellationToken cancellationToken, bool verifyHealth = true, bool pooling = false,
        bool freshDatabase = false)
    {
        EnsureSqliteBootstrap();
        var connection = new SqliteConnection(ConnectionString(databasePath, mode, pooling));
        try
        {
            var started = TimingStart();
            await connection.OpenAsync(cancellationToken);
            // The actual pinned SQLite runtime freezes NONE when WAL is selected first, even before
            // the first table. Set the fresh database's pointer-map mode before journal initialization.
            // Existing databases never enter this branch and are never VACUUMed here.
            if (freshDatabase)
                await ExecuteAsync(connection, null, "PRAGMA auto_vacuum=INCREMENTAL;", cancellationToken);
            ReportTiming("connection-open", started);
            started = TimingStart();
            if (requireCurrentSchema)
                await RequireExactCurrentSchemaAsync(connection, cancellationToken, verifyHealth);
            ReportTiming("schema-check", started);
            started = TimingStart();
            ConfigureHistoryRetirementFunctions(connection);
            ConfigureActionRetirementFunction(connection);
            await ConfigureAuthorityConnectionAsync(connection, cancellationToken);
            ReportTiming("connection-settings", started);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static string ProjectionHistoryOutcome(string eventKind, string? operationKind) => eventKind switch
    {
        "effect_committed" or "auxiliary_effect_committed" or "deletion_committed" or
            "effect_reconciled_and_committed" => operationKind switch
            {
                "create_remote" or "update_remote" => "Uploaded",
                "create_local" or "update_local" => "Downloaded",
                "delete_local" or "delete_remote" => "Recovery copy saved",
                "move_local" or "move_remote" or "cross_pair_move" => "Moved",
                "resolve_conflict" => "Conflict resolved",
                _ => "Change saved"
            },
        "operation_completed" => "Completed",
        "unissued_lease_recovered" => "Unissued plan discarded",
        "attempt_reconciled" => "Previous outcome checked",
        "cleanup_started" => "Cleanup",
        "intermediate_effect_reconciled" or "deletion_reconciled" => "Recovered",
        "intermediate_effect_verified_not_applied" => "No change",
        _ => "Verified"
    };

    private static string ProjectionHistoryEvent(string eventKind, string? operationKind)
    {
        var action = operationKind is null ? "sync operation" : ProjectionOperationName(operationKind).ToLowerInvariant();
        return eventKind switch
        {
            "effect_committed" => $"The {action} effect was verified and committed.",
            "auxiliary_effect_committed" => $"The {action} effect was verified and committed.",
            "deletion_committed" => "A recoverable deletion was verified after its Recovery copy was saved.",
            "effect_reconciled_and_committed" => $"Sync Shark recovered and committed the interrupted {action}.",
            "intermediate_effect_reconciled" => $"An intermediate {action} effect was reconciled safely.",
            "deletion_reconciled" => "An interrupted recoverable deletion was reconciled safely.",
            "intermediate_effect_verified" => $"An intermediate {action} effect was verified.",
            "intermediate_effect_verified_not_applied" => $"The attempted {action} did not apply; no destination change was accepted.",
            "operation_completed" => $"The {action} operation completed.",
            "unissued_lease_recovered" => "Discarded an interrupted unissued plan. No file was transferred or changed. A fresh planner and current approval must authorize any replacement.",
            "attempt_reconciled" => "Checked the retained outcome of an earlier attempt. This recovery check is not a new completed file transfer.",
            "cleanup_started" => $"Final cleanup started after the {action} was committed.",
            _ => eventKind.Replace('_', ' ')
        };
    }

    // The version-4 to version-5 rebuild copies every recorded provider literal unchanged. This is the
    // only place that corrects one, and it corrects a row only when that row's retained
    // provider-account id is exactly the grandfathered Google derivation over the same row's account
    // and root with home equal to root. A display label, e-mail, path, substring or guess never
    // classifies a row. The correction runs inside the caller's migration transaction, so a failure
    // here rolls the whole migration back.
    private static async Task ReclassifyLegacyGoogleProviderAccountsAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, string Account, string Root, string Home)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT provider_account_id,account_id,root_namespace_id,home_namespace_id FROM provider_account WHERE provider='dropbox' ORDER BY provider_account_id;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        foreach (var row in rows)
        {
            if (!SyncAuthorityProviderAccountIdentity.IsLegacyGoogleProviderAccount(row.Id, row.Account,
                    row.Root, row.Home))
                continue;
            await ExecuteAsync(connection, transaction,
                "UPDATE provider_account SET provider=$provider WHERE provider_account_id=$id AND provider='dropbox' AND account_id=$account AND root_namespace_id=$root AND home_namespace_id=$home;",
                cancellationToken, ("$provider", SyncProviders.GoogleDrive), ("$id", row.Id),
                ("$account", row.Account), ("$root", row.Root), ("$home", row.Home));
        }
    }

    private static async Task<int> InspectReadOnlySchemaAsync(string databasePath,
        CancellationToken cancellationToken, bool verifyHealth = true, Action<string>? startupProgress = null)
    {
        EnsureSqliteBootstrap();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        try
        {
            startupProgress?.Invoke("Startup: opening the private read-only database connection.");
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(connection, null,
                "PRAGMA query_only=ON; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF;",
                cancellationToken);
            // Full health checks are read-only and precede effect admission. A bounded
            // mapping lets SQLite read OS-cached pages without a second private copy;
            // this is address space, not a request to preload/allocate 2 GiB of RAM.
            // Keep this connection-local and unpooled so disposal releases the mapping
            // before migration, checkpoint, replacement or ordinary transfer writes.
            // Unsupported mapping falls back to SQLite's existing buffered reads.
            if (verifyHealth)
            {
                await ConfigureInspectionReadResourcesAsync(connection, cancellationToken);
                ReadOnlyHealthConnectionForTest?.Invoke(connection);
            }
            var sqliteVersion = await ScalarStringAsync(connection, null, "SELECT sqlite_version();",
                                    cancellationToken)
                                ?? throw new InvalidDataException("SQLite did not report a runtime version.");
            RequireSupportedSqliteVersion(sqliteVersion);
            var version = checked((int)await ScalarLongAsync(connection, null, "PRAGMA user_version;",
                cancellationToken));
            if (version == SyncAuthoritySchema.Version)
                await RequireExactSchemaAsync(connection, version, ExpectedCurrentSchemaObjects,
                    cancellationToken, verifyHealth, startupProgress);
            else if (version == SyncAuthoritySchemaMigration3.PreviousVersion)
                await RequireExactSchemaAsync(connection, version, ExpectedVersion2SchemaObjects,
                    cancellationToken, verifyHealth, startupProgress);
            else if (version == SyncAuthoritySchemaMigration4.PreviousVersion)
                await RequireExactSchemaAsync(connection, version, ExpectedVersion3SchemaObjects, cancellationToken,
                    verifyHealth, startupProgress);
            else if (version == SyncAuthoritySchemaMigration5.PreviousVersion)
                await RequireExactSchemaAsync(connection, version, ExpectedVersion4SchemaObjects, cancellationToken,
                    verifyHealth, startupProgress);
            else if (version == SyncAuthoritySchemaMigration6.PreviousVersion)
                await RequireExactSchemaAsync(connection, version, ExpectedVersion5SchemaObjects, cancellationToken,
                    verifyHealth, startupProgress);
            else if (version == SyncAuthoritySchemaMigration7.PreviousVersion)
                await RequireExactSchemaAsync(connection, version, ExpectedVersion6SchemaObjects, cancellationToken,
                    verifyHealth, startupProgress);
            else if (version == SyncAuthoritySchemaMigration8.PreviousVersion)
                await RequireExactSchemaAsync(connection, version, ExpectedVersion7SchemaObjects, cancellationToken,
                    verifyHealth, startupProgress);
            else if (version == SyncAuthoritySchemaMigration9.PreviousVersion)
                await RequireExactSchemaAsync(connection, version, ExpectedVersion8SchemaObjects, cancellationToken,
                    verifyHealth, startupProgress);
            else if (version == SyncAuthoritySchemaMigration10.PreviousVersion)
                await RequireExactSchemaAsync(connection, version, ExpectedVersion9SchemaObjects, cancellationToken,
                    verifyHealth, startupProgress);
            else if (version == SyncAuthoritySchemaMigration11.PreviousVersion)
                await RequireExactSchemaAsync(connection, version, ExpectedVersion10SchemaObjects, cancellationToken,
                    verifyHealth, startupProgress);
            else if (version == SyncAuthoritySchemaMigration12.PreviousVersion)
                await RequireExactSchemaAsync(connection, version, ExpectedVersion11SchemaObjects, cancellationToken,
                    verifyHealth, startupProgress);
            else if (version == SyncAuthoritySchemaMigration13.PreviousVersion)
                await RequireExactSchemaAsync(connection, version, ExpectedVersion12SchemaObjects, cancellationToken,
                    verifyHealth, startupProgress);
            else if (version == SyncAuthoritySchemaMigration18.PreviousVersion)
                await RequireExactSchemaAsync(connection,version,ExpectedVersion17SchemaObjects,cancellationToken,
                    verifyHealth,startupProgress);
            else if (version == SyncAuthoritySchemaMigration17.PreviousVersion)
                await RequireExactSchemaAsync(connection,version,ExpectedVersion16SchemaObjects,cancellationToken,
                    verifyHealth,startupProgress);
            else if (version == SyncAuthoritySchemaMigration16.PreviousVersion)
                await RequireExactSchemaAsync(connection, version, ExpectedVersion15SchemaObjects, cancellationToken,
                    verifyHealth, startupProgress);
            else if (version == SyncAuthoritySchemaMigration15.PreviousVersion)
                await RequireExactSchemaAsync(connection, version, ExpectedVersion14SchemaObjects, cancellationToken,
                    verifyHealth, startupProgress);
            else if (version == SyncAuthoritySchemaMigration14.PreviousVersion)
                await RequireExactSchemaAsync(connection, version, ExpectedVersion13SchemaObjects, cancellationToken,
                    verifyHealth, startupProgress);
            else
            {
                var direction = version < SyncAuthoritySchema.Version ? "older or unknown" : "newer";
                throw new InvalidDataException(
                    $"Authority schema version {version} is {direction}; only exact version {SyncAuthoritySchema.Version} or its supported version 2/3/4/5/6/7/8/9/10/11/12/13/14/15/16/17 predecessors are accepted. No schema write was attempted.");
            }
            return version;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new InvalidDataException(
                "The existing authority database is malformed or unreadable. Automatic repair is disabled; restore an exact supported copy before retrying. No database write was attempted.",
                exception);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    private Task EnsureVersion2RollbackBackupAsync(CancellationToken cancellationToken) => EnsureRollbackBackupAsync(2, cancellationToken);

    private static async Task ConfigureInspectionReadResourcesAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, null, "PRAGMA cache_size=-65536;", cancellationToken);
        if (Environment.Is64BitProcess)
            await ExecuteAsync(connection, null, "PRAGMA mmap_size=2147483648;", cancellationToken);
    }

    private async Task EnsureRollbackBackupAsync(int predecessorVersion, CancellationToken cancellationToken)
    {
        var backupPath = predecessorVersion == 2 ? Version2RollbackDatabasePath
            : predecessorVersion == 3 ? Version3RollbackDatabasePath
            : predecessorVersion == 4 ? Version4RollbackDatabasePath
            : predecessorVersion == 5 ? Version5RollbackDatabasePath
            : predecessorVersion == 6 ? Version6RollbackDatabasePath
            : predecessorVersion == 7 ? Version7RollbackDatabasePath
            : predecessorVersion == 8 ? Version8RollbackDatabasePath
            : predecessorVersion == 9 ? Version9RollbackDatabasePath
            : predecessorVersion == 10 ? Version10RollbackDatabasePath
            : predecessorVersion == 11 ? Version11RollbackDatabasePath
            : predecessorVersion == 12 ? Version12RollbackDatabasePath
            : predecessorVersion == 13 ? Version13RollbackDatabasePath
            : predecessorVersion == 14 ? Version14RollbackDatabasePath
            : predecessorVersion == 17 ? Version17RollbackDatabasePath
            : predecessorVersion == 16 ? Version16RollbackDatabasePath
            : predecessorVersion == 15 ? Version15RollbackDatabasePath
            : throw new ArgumentOutOfRangeException(nameof(predecessorVersion));
        var stagePath = backupPath + ".staging";
        var sourceInstallation = await ReadInstallationIdAsync(_databasePath, cancellationToken);
        if (File.Exists(backupPath))
        {
            if (await InspectReadOnlySchemaAsync(backupPath, cancellationToken) !=
                predecessorVersion ||
                !string.Equals(await ReadInstallationIdAsync(backupPath, cancellationToken), sourceInstallation,
                    StringComparison.Ordinal) ||
                !await RollbackContentsMatchAsync(backupPath, cancellationToken))
                throw new InvalidDataException(
                    $"The retained v{predecessorVersion} rollback database does not match this installation's current contents; both copies were preserved and migration was not attempted.");
            return;
        }

        if (File.Exists(stagePath))
        {
            if (await InspectReadOnlySchemaAsync(stagePath, cancellationToken) !=
                predecessorVersion ||
                !string.Equals(await ReadInstallationIdAsync(stagePath, cancellationToken), sourceInstallation,
                    StringComparison.Ordinal) ||
                !await RollbackContentsMatchAsync(stagePath, cancellationToken))
                throw new InvalidDataException(
                    $"The interrupted v{predecessorVersion} rollback staging database is not an exact copy; migration was not attempted.");
            File.Move(stagePath, backupPath, overwrite: false);
            return;
        }

        EnsureSqliteBootstrap();
        await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                     {
                         DataSource = _databasePath,
                         Mode = SqliteOpenMode.ReadOnly,
                         Cache = SqliteCacheMode.Private,
                         Pooling = false
                     }.ToString()))
        await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                     {
                         DataSource = stagePath,
                         Mode = SqliteOpenMode.ReadWriteCreate,
                         Cache = SqliteCacheMode.Private,
                         Pooling = false
                     }.ToString()))
        {
            await source.OpenAsync(cancellationToken);
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
        }
        await using (var durable = new FileStream(stagePath, FileMode.Open, FileAccess.ReadWrite,
                         FileShare.Read, 1, FileOptions.WriteThrough))
        {
            await durable.FlushAsync(cancellationToken);
            durable.Flush(flushToDisk: true);
        }
        if (await InspectReadOnlySchemaAsync(stagePath, cancellationToken) !=
            predecessorVersion ||
            !string.Equals(await ReadInstallationIdAsync(stagePath, cancellationToken), sourceInstallation,
                StringComparison.Ordinal) || !await RollbackContentsMatchAsync(stagePath, cancellationToken))
            throw new InvalidDataException($"The v{predecessorVersion} rollback staging copy failed exact readback.");
        File.Move(stagePath, backupPath, overwrite: false);
        if (await InspectReadOnlySchemaAsync(backupPath, cancellationToken) !=
            predecessorVersion)
            throw new InvalidDataException($"The promoted v{predecessorVersion} rollback database failed exact readback.");
    }

    private async Task<bool> RollbackContentsMatchAsync(string backupPath, CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        // SQLite's online backup includes WAL state. Compare logical rows rather than main-file
        // hashes, and never silently reuse a same-installation backup after new user activity.
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using (var attach = connection.CreateCommand())
        {
            // The bundled provider opens normal Windows paths, not URI filenames. An attached
            // database inherits the connection's read-only mode; verify that at the native boundary.
            attach.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA query_only=ON; ATTACH DATABASE $backup AS retained; BEGIN;";
            attach.Parameters.AddWithValue("$backup", Path.GetFullPath(backupPath));
            await attach.ExecuteNonQueryAsync(cancellationToken);
        }
        if (SQLitePCL.raw.sqlite3_db_readonly(connection.Handle, "main") != 1 ||
            SQLitePCL.raw.sqlite3_db_readonly(connection.Handle, "retained") != 1)
            throw new InvalidDataException("Rollback comparison requires two read-only databases.");
        var tables = new List<string>();
        await using (var names = connection.CreateCommand())
        {
            names.CommandText = "SELECT name FROM main.sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            await using var reader = await names.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        }
        foreach (var name in tables)
        {
            var identifier = '"' + name.Replace("\"", "\"\"") + '"';
            var columns = new List<(string Name, int Key, bool NonNull)>();
            await using (var metadata = connection.CreateCommand())
            {
                metadata.CommandText = $"PRAGMA main.table_xinfo({identifier});";
                await using var reader = await metadata.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    if (reader.GetInt32(6) != 1) // SELECT * includes generated, but not hidden virtual columns.
                        columns.Add((reader.GetString(1), reader.GetInt32(5), reader.GetInt32(3) != 0));
            }
            var key = columns.Where(column => column.Key > 0).OrderBy(column => column.Key).ToArray();
            var indexed = key.Length > 0;
            var nullableKey = key.Where(column => !column.NonNull).ToArray();
            if (indexed && nullableKey.Length > 0)
            {
                // Ordinary SQLite composite/TEXT primary keys may contain NULL and
                // duplicates. In that case retain the original EXCEPT semantics.
                var nullPredicate = string.Join(" OR ", nullableKey.Select(column =>
                    '"' + column.Name.Replace("\"", "\"\"") + "\" IS NULL"));
                await using var nulls = connection.CreateCommand();
                nulls.CommandText = $"SELECT EXISTS(SELECT 1 FROM main.{identifier} WHERE {nullPredicate}) " +
                    $"OR EXISTS(SELECT 1 FROM retained.{identifier} WHERE {nullPredicate});";
                indexed = Convert.ToInt64(await ReadWithNativeCancellationAsync(connection,
                    () => nulls.ExecuteScalarAsync(cancellationToken), cancellationToken)) == 0;
            }
            var started = Stopwatch.GetTimestamp();
            progress?.Invoke($"Recovery readback: comparing {name} using " +
                (indexed ? "indexed primary-key row matching." : "full set comparison; no unique non-null key."));
            await using var compare = connection.CreateCommand();
            if (indexed)
            {
                static string Column(string alias, string name) => alias + ".\"" + name.Replace("\"", "\"\"") + "\"";
                var keysMatch = string.Join(" AND ", key.Select(column =>
                    Column("current", column.Name) + " IS " + Column("copy", column.Name)));
                var valuesMatch = string.Join(" AND ", columns.Select(column =>
                    Column("current", column.Name) + " IS " + Column("copy", column.Name)));
                // Equal counts plus a unique non-null key make this a bijection.
                // SQLite IS retains EXCEPT's NULL/numeric/blob comparison semantics.
                // Unlike EXCEPT it does not sort/materialize the entire retained DB.
                compare.CommandText = $"""
                    SELECT (SELECT count(*) FROM main.{identifier})=(SELECT count(*) FROM retained.{identifier})
                        AND NOT EXISTS(SELECT 1 FROM main.{identifier} AS current
                            WHERE NOT EXISTS(SELECT 1 FROM retained.{identifier} AS copy
                                WHERE {keysMatch} AND {valuesMatch}));
                    """;
            }
            else compare.CommandText = $"""
                SELECT (SELECT count(*) FROM main.{identifier})=(SELECT count(*) FROM retained.{identifier})
                    AND NOT EXISTS(SELECT * FROM main.{identifier} EXCEPT SELECT * FROM retained.{identifier})
                    AND NOT EXISTS(SELECT * FROM retained.{identifier} EXCEPT SELECT * FROM main.{identifier});
                """;
            var equal = Convert.ToInt64(await ReadWithNativeCancellationAsync(connection,
                () => compare.ExecuteScalarAsync(cancellationToken), cancellationToken)) == 1;
            progress?.Invoke($"Recovery readback: {name} " + (equal ? "matched" : "differs") +
                $" in {Stopwatch.GetElapsedTime(started).TotalSeconds:F3}s.");
            if (!equal) return false;
        }
        return true;
    }

    private static async Task<string> ReadInstallationIdAsync(string databasePath,
        CancellationToken cancellationToken)
    {
        EnsureSqliteBootstrap();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var count = await ScalarLongAsync(connection, null, "SELECT count(*) FROM installation;",
            cancellationToken);
        if (count != 1) throw new InvalidDataException("The authority database has no unique installation identity.");
        return await ScalarStringAsync(connection, null, "SELECT installation_id FROM installation;",
                   cancellationToken) ??
               throw new InvalidDataException("The authority database installation identity is absent.");
    }

    private static void EnsureSqliteBootstrap()
    {
        lock (BootstrapLock)
        {
            if (_sqliteBootstrapped) return;
            SQLitePCL.Batteries_V2.Init();
            _sqliteBootstrapped = true;
        }
    }

    internal static async Task ConfigureAuthorityConnectionAsync(SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        // PASSIVE checkpoints reuse committed WAL pages without evicting readers.
        // The retained-size limit applies after reset, not while a reader pins history.
        // FULL durability and each request's commit/acknowledgement boundary remain unchanged.
        await ExecuteAsync(connection, null,
            "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA trusted_schema=OFF; PRAGMA wal_autocheckpoint=1000; PRAGMA main.journal_size_limit=33554432;",
            cancellationToken);
        // The 2 MB default page cache cannot hold the upper index levels of a large inventory, so every
        // random-key insert re-read them and batch time grew with the database. 64 MB per connection.
        await ExecuteAsync(connection, null, "PRAGMA cache_size=-65536;", cancellationToken);
        await RequireConnectionSettingsAsync(connection, cancellationToken);
    }

    private static async Task RequireConnectionSettingsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var foreignKeys = await ScalarLongAsync(connection, null, "PRAGMA foreign_keys;", cancellationToken);
        var synchronous = await ScalarLongAsync(connection, null, "PRAGMA synchronous;", cancellationToken);
        var journalMode = await ScalarStringAsync(connection, null, "PRAGMA journal_mode;", cancellationToken);
        var trustedSchema = await ScalarLongAsync(connection, null, "PRAGMA trusted_schema;", cancellationToken);
        var walAutocheckpoint = await ScalarLongAsync(connection, null, "PRAGMA wal_autocheckpoint;", cancellationToken);
        var retainedJournalLimit = await ScalarLongAsync(connection, null, "PRAGMA main.journal_size_limit;", cancellationToken);
        var version = await ScalarStringAsync(connection, null, "SELECT sqlite_version();", cancellationToken)
                      ?? throw new InvalidDataException("SQLite did not report a runtime version.");
        if (foreignKeys != 1 || synchronous != 2 || trustedSchema != 0 || walAutocheckpoint != 1000 ||
            retainedJournalLimit != 33554432 ||
            !string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SQLite authority connection settings are not WAL/FULL/foreign-key/trusted-schema/checkpoint safe.");
        RequireSupportedSqliteVersion(version);
    }

    private static void RequireSupportedSqliteVersion(string value)
    {
        if (!Version.TryParse(value, out var version))
            throw new InvalidDataException("SQLite reported an invalid runtime version.");
        var supported = version >= new Version(3, 51, 3) || version == new Version(3, 50, 7) || version == new Version(3, 44, 6);
        if (!supported)
            throw new InvalidDataException($"SQLite {version} is blocked because its WAL reset behavior is not accepted.");
    }

    private static async Task RequireHealthyAsync(SqliteConnection connection, CancellationToken cancellationToken,
        Action<string>? startupProgress = null)
    {
        await ReadWithNativeCancellationAsync(connection, async () =>
        {
            var stage = Stopwatch.StartNew();
            // Validate structure before following foreign-key references. The
            // required structural pass also reads the b-trees before the more
            // scattered relationship lookups; no extra warm-up read, persisted
            // verdict, or reduced integrity predicate is used.
            using var snapshot = connection.BeginTransaction(deferred: true);
            startupProgress?.Invoke("Startup: running SQLite PRAGMA quick_check on retained state; no folder scan or file transfer is running.");
            var quickCheck = await ScalarStringAsync(connection, snapshot, "PRAGMA quick_check;", cancellationToken);
            if (!string.Equals(quickCheck, "ok", StringComparison.Ordinal))
                throw new InvalidDataException("SQLite quick_check did not return ok.");
            startupProgress?.Invoke($"Startup: SQLite quick_check completed in {stage.Elapsed.TotalSeconds:F3}s.");
            stage.Restart();
            startupProgress?.Invoke("Startup: running SQLite PRAGMA foreign_key_check on retained state; no folder scan or file transfer is running.");
            // Keep this same read snapshot for every table. The native predicates
            // include all history, not an index-only or selected-table substitute.
            var tables = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = snapshot;
                command.CommandText = "SELECT name FROM main.sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
            }
            for (var index = 0; index < tables.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var table = tables[index];
                var quotedTable = "\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                var statement = $"PRAGMA main.foreign_key_check({quotedTable});";
                startupProgress?.Invoke($"Startup: checking saved relationships in {table} ({index + 1}/{tables.Count}); {statement}");
                var tableStage = Stopwatch.StartNew();
                var foreignKeyCheck = await ScalarStringAsync(connection, snapshot, statement, cancellationToken);
                if (foreignKeyCheck is not null)
                    throw new InvalidDataException($"SQLite foreign_key_check reported an error in table {table}.");
                startupProgress?.Invoke($"Startup: checked saved relationships in {table} ({index + 1}/{tables.Count}) in {tableStage.Elapsed.TotalSeconds:F3}s.");
            }
            startupProgress?.Invoke($"Startup: SQLite foreign_key_check completed in {stage.Elapsed.TotalSeconds:F3}s.");
            return true;
        }, cancellationToken);
        HealthCheckCompletedForTest?.Invoke(connection.DataSource);
    }

    // Microsoft.Data.Sqlite executes these reads synchronously: passing a token to
    // ExecuteScalarAsync alone cannot interrupt an in-flight SQLite VM. The callback
    // only observes cancellation; it never queries/mutates the connection. Restrict
    // this scope to an exclusively owned read connection, never an effect transaction.
    internal static async Task<T> ReadWithNativeCancellationAsync<T>(SqliteConnection connection,
        Func<Task<T>> read, CancellationToken cancellationToken, Action? nativeProgressForTest = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!cancellationToken.CanBeCanceled) return await read();
        var handle = connection.Handle ?? throw new InvalidOperationException("The SQLite read connection is closed.");
        SQLitePCL.raw.sqlite3_progress_handler(handle, 1000,
            static state =>
            {
                var observation = ((CancellationToken Token, Action? Progress))state!;
                // Test-only observation of actual VM execution. It must never
                // query or mutate this exclusively owned connection.
                observation.Progress?.Invoke();
                return observation.Token.IsCancellationRequested ? 1 : 0;
            }, (cancellationToken, nativeProgressForTest));
        try
        {
            var result = await read();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (SqliteException error) when (error.SqliteErrorCode == SQLitePCL.raw.SQLITE_INTERRUPT &&
                                            cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The SQLite read was cancelled before it finished.",
                error, cancellationToken);
        }
        finally
        {
            // A cancelled callback must not leak into reuse, disposal or later reads.
            SQLitePCL.raw.sqlite3_progress_handler(handle, 0, null, null);
        }
    }

    private static async Task RequireExactCurrentSchemaAsync(SqliteConnection connection,
        CancellationToken cancellationToken, bool verifyHealth = true, bool allowIncompleteSuccessor = false)
    {
        var started = TimingStart();
        var version = await ScalarLongAsync(connection, null, "PRAGMA user_version;", cancellationToken);
        ReportTiming("schema-version", started);
        if (version != SyncAuthoritySchema.Version)
        {
            var direction = version < SyncAuthoritySchema.Version ? "older or unknown" : "newer";
            throw new InvalidDataException(
                $"Authority schema version {version} is {direction}; exact version {SyncAuthoritySchema.Version} is required. Automatic migration is disabled, so restore the authority database with a matching Sync Shark version before retrying. No schema write was attempted.");
        }

        await RequireExactSchemaAsync(connection, checked((int)version), ExpectedCurrentSchemaObjects,
            cancellationToken, verifyHealth);
        if (!allowIncompleteSuccessor)
            await RequireCompletedEssentialSuccessorAsync(connection, cancellationToken);
    }

    private static async Task RequireExactSchemaAsync(SqliteConnection connection, int version,
        IReadOnlyList<string> expectedObjects, CancellationToken cancellationToken, bool verifyHealth = true,
        Action<string>? startupProgress = null)
    {
        startupProgress?.Invoke("Startup: validating the exact database object inventory and schema-migration receipt.");
        var started = TimingStart();
        // The exact object inventory is read the first time a (pooled) SQLite connection is checked
        // and again whenever its PRAGMA schema_version differs from the value recorded at that
        // connection's last full verification. SQLite increments schema_version on every DDL
        // statement (CREATE/DROP/ALTER of any table, index, trigger or view), so an unchanged value
        // means the inventory is still exactly the one already compared. The version, receipt and
        // successor checks below still run on every checkout.
        var schemaCookie = await ScalarLongAsync(connection, null, "PRAGMA schema_version;", cancellationToken);
        if (!VerifiedInventories.TryGetValue(connection.Handle!, out var verified) ||
            verified.SchemaVersion != schemaCookie || !ReferenceEquals(verified.Expected, expectedObjects))
        {
            var actualObjects = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT lower(type) || ':' || name
                    FROM sqlite_schema
                    WHERE type IN ('table','index','trigger','view') AND name NOT LIKE 'sqlite_%'
                    ORDER BY lower(type), name;
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) actualObjects.Add(reader.GetString(0));
            }
            if (!actualObjects.SequenceEqual(expectedObjects, StringComparer.Ordinal))
                throw new InvalidDataException(
                    $"The authority database object inventory is not the exact schema version {version}. Automatic repair is disabled; restore an exact supported copy before retrying. No schema write was attempted.");
            VerifiedInventories.AddOrUpdate(connection.Handle!, new(schemaCookie, expectedObjects));
        }
        ReportTiming("schema-inventory", started);

        started = TimingStart();
        var migrationReceipt = await ScalarLongAsync(connection, null,
            "SELECT count(*) FROM schema_migration WHERE schema_version=$version;", cancellationToken,
            ("$version", version));
        ReportTiming("schema-receipt", started);
        if (migrationReceipt != 1)
            throw new InvalidDataException(
                $"The authority database does not retain the exact schema version {version} receipt. Automatic repair is disabled; no schema write was attempted.");
        // A staged version-12 database built before the digest column was bound is incompatible.
        // Refuse it read-only; never silently repair/reset an existing current-generation identity.
        if (version >= 12 && await ScalarLongAsync(connection, null,
                "SELECT count(*) FROM pragma_table_info('observation_cut') WHERE name='sealed_input_digest' AND upper(type)='TEXT';",
                cancellationToken) != 1)
            throw new InvalidDataException("The current compact-observation schema lacks its sealed input digest. No schema write was attempted.");
        if (verifyHealth) await RequireHealthyAsync(connection, cancellationToken, startupProgress);
    }

    private sealed record VerifiedInventory(long SchemaVersion, IReadOnlyList<string> Expected);
    // Keyed by the native handle, which a pooled connection keeps across checkouts.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SQLitePCL.sqlite3, VerifiedInventory>
        VerifiedInventories = new();

    private static string[] BuildExpectedSchemaObjects(string definitions)
    {
        // Views are part of the exact inventory this list is compared against, because the checkout
        // validation enumerates sqlite_schema with type IN ('table','index','trigger','view'). The
        // read-only effective_observation view of version 9 is therefore expected by name, and the same
        // expression captures it.
        var matches = Regex.Matches(definitions,
            @"CREATE\s+(?:UNIQUE\s+)?(?<type>TABLE|INDEX|TRIGGER|VIEW)\s+IF\s+NOT\s+EXISTS\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var objects = matches.Select(match =>
                $"{match.Groups["type"].Value.ToLowerInvariant()}:{match.Groups["name"].Value}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (objects.Length == 0 || objects.Distinct(StringComparer.Ordinal).Count() != objects.Length)
            throw new InvalidOperationException("The compiled current-schema object inventory is empty or duplicated.");
        return objects;
    }

    private static async Task RequireFenceAsync(SqliteConnection connection, SqliteTransaction transaction,
        string operationId, SyncAuthorityLease lease, CancellationToken cancellationToken)
    {
        if (!string.Equals(operationId, lease.OperationId, StringComparison.Ordinal))
            throw new InvalidOperationException("The lease is not bound to this operation.");
        var count = await ScalarLongAsync(connection, transaction,
            """
            SELECT count(*) FROM lease l JOIN operation o ON o.operation_id=l.scope_id
            WHERE l.lease_id=$lease AND l.scope_kind='operation' AND l.scope_id=$operation
              AND l.status='active' AND l.fencing_token=$token
              AND julianday(l.expires_utc)>julianday('now')
              AND o.current_lease_id=l.lease_id AND o.current_fencing_token=l.fencing_token;
            """, cancellationToken, ("$lease", lease.LeaseId), ("$operation", operationId),
            ("$token", lease.FencingToken));
        if (count != 1) throw new InvalidOperationException("The worker does not hold the current fenced operation lease.");
        await RequireCurrentUnissuedConflictPolicyAsync(connection, transaction, operationId, cancellationToken);
    }

    private static async Task RequireCurrentUnissuedConflictPolicyAsync(SqliteConnection connection,
        SqliteTransaction transaction, string operationId, CancellationToken cancellationToken)
    {
        var stale = await ScalarLongAsync(connection, transaction,
            """
            SELECT count(*) FROM operation o
            WHERE o.operation_id=$id AND o.status IN ('ready','leased')
              AND json_type(o.preconditions_json,'$.conflictId') IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM sync_pair p WHERE p.pair_id=o.pair_id
                AND p.state<>'retired' AND p.configuration_revision=o.configuration_revision);
            """, cancellationToken, ("$id", operationId));
        if (stale != 0)
            throw new InvalidOperationException("The exact conflict pair policy changed before its file effect could be issued.");
        // Once an effect is issued, its old authority remains evidence: recovery/commit must retain
        // the truthful outcome even if settings changed meanwhile. Never block receipt finalization.
    }

    private static async Task<bool> HasOverlappingAuthorityPathAsync(SqliteConnection connection,
        SqliteTransaction transaction, string side, string candidate, string excludedId,
        CancellationToken cancellationToken, string? validatedDropboxNamespace = null,
        SyncPair? partitionCandidate = null)
    {
        var family = side is "local" or "local_control" ? "local" : "dropbox";
        // Each competing root keeps its owning pair id and its own side so a proven nested partition can be
        // evaluated against the exact candidate and retained pair definitions and the exact row identity
        // instead of by path alone. A private control row is never granted by a pair-level relation.
        var paths = new List<(string Path, string? PairId, string Side)>();
        await using (var roots = connection.CreateCommand())
        {
            roots.Transaction = transaction;
            // A root written with the typed cloud identity carries its real provider, account, namespace and
            // folder identity, so it is decided by the one typed cloud guard and never by a path-only
            // comparison. A legacy row has no cloud identity member and keeps its exact previous comparison.
            roots.CommandText = family == "local"
                ? """
                    SELECT pair_id, side, CASE WHEN side IN ('local','local_control') THEN display_path
                           ELSE json_extract(identity_json,'$.localRootPath') END
                    FROM root_binding WHERE state='active' AND root_binding_id<>$excluded
                      AND (side IN ('local','local_control') OR
                           (side='dropbox' AND json_type(identity_json,'$.localRootPath')='text'
                            AND json_type(identity_json,'$.localRootIdentity')='text'))
                      AND json_extract(identity_json,'$.identityKind') IS NOT 'cloud';
                    """
                : """
                    SELECT r.pair_id, r.side, r.display_path FROM root_binding r
                    LEFT JOIN provider_account p ON p.provider_account_id=json_extract(r.identity_json,'$.providerAccountId')
                      AND p.root_namespace_id=json_extract(r.identity_json,'$.rootNamespaceId') AND p.status='active'
                    WHERE r.side IN ('dropbox','dropbox_control') AND r.state='active' AND r.root_binding_id<>$excluded
                      AND json_extract(r.identity_json,'$.identityKind') IS NOT 'cloud'
                      AND ($namespace IS NULL OR p.provider_account_id IS NULL OR p.root_namespace_id=$namespace);
                    """;
            roots.Parameters.AddWithValue("$excluded", excludedId);
            if (family == "dropbox") roots.Parameters.AddWithValue("$namespace", (object?)validatedDropboxNamespace ?? DBNull.Value);
            await using var reader = await roots.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                paths.Add((reader.GetString(2), reader.GetString(0), reader.GetString(1)));
        }
        await using (var recovery = connection.CreateCommand())
        {
            recovery.Transaction = transaction;
            // Only a namespace verified through RegisterDropboxRootBinding/RecoveryRoot may
            // separate equal cloud paths. Legacy/unbound roots stay conservative, and the
            // same shared namespace still overlaps even when accessed by different accounts.
            recovery.CommandText = """
                SELECT NULL, r.side, r.display_path FROM recovery_root_binding r
                LEFT JOIN provider_account p ON p.provider_account_id=r.provider_account_id
                  AND p.root_namespace_id=json_extract(r.identity_json,'$.rootNamespaceId') AND p.status='active'
                WHERE r.side=$side AND r.status='active' AND r.recovery_root_binding_id<>$excluded
                  AND json_extract(r.identity_json,'$.identityKind') IS NOT 'cloud'
                  AND ($namespace IS NULL OR p.provider_account_id IS NULL OR p.root_namespace_id=$namespace);
                """;
            recovery.Parameters.AddWithValue("$side", family);
            recovery.Parameters.AddWithValue("$excluded", excludedId);
            recovery.Parameters.AddWithValue("$namespace", family == "dropbox" ? (object?)validatedDropboxNamespace ?? DBNull.Value : DBNull.Value);
            await using var reader = await recovery.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) paths.Add((reader.GetString(2), null, reader.GetString(1)));
        }
        var separator = family == "local" ? '\\' : '/';
        foreach (var (existing, existingPairId, existingSide) in paths)
        {
            if (!AuthorityPathsOverlap(existing, candidate, separator)) continue;
            // A proven nested partition is not an overlap: the enclosing pair excludes the whole adjacent
            // subtree or the specific ownership path. Reserved control/recovery rows have no pair and stay
            // strict, and a stale or unrepresented row whose exact path is not excluded stays an overlap.
            if (partitionCandidate is not null && existingPairId is not null &&
                await AllowsNestedOverlapRowAsync(connection, transaction, partitionCandidate, existingPairId,
                    existingSide, existing, candidate, cancellationToken)) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Reads the exact retained pair definition of a competing root and asks the one shared partition
    /// policy whether this exact existing path is an allowed nested-partition row. A missing or
    /// unpublished retained definition proves nothing and stays a strict overlap.
    /// </summary>
    private static async Task<bool> AllowsNestedOverlapRowAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncPair candidatePair, string retainedPairId, string rowSide,
        string existingPath, string candidatePath, CancellationToken cancellationToken)
    {
        var retained = await LoadRetainedPairDefinitionAsync(connection, transaction, retainedPairId,
            cancellationToken);
        if (retained is null) return false;
        // The comparison copy supplies each document-less retained side's OWN already-verified namespace, read
        // inside this same transaction, so a nested registration is decided against the exact facts the
        // enclosing pair was published with. The raw retained definition keeps owning every revision,
        // definition and identity check, and the copy never enters persistence.
        var comparison = await ProjectRetainedPairForComparisonAsync(connection, transaction, retained,
            cancellationToken);
        if (comparison is null) return false;
        // A published legacy candidate needs the SAME own-authority projection as its peer.
        // Preserve the requested policy; bind the namespace only to this candidate's retained
        // account/folder/root tuple inside this transaction, never to the competing path.
        var candidateComparison = candidatePair;
        if (candidatePair.EndpointDocument is null &&
            await LoadRetainedPairDefinitionAsync(connection, transaction, candidatePair.Id,
                cancellationToken) is not null)
        {
            candidateComparison = await ProjectRetainedPairForComparisonAsync(connection, transaction,
                candidatePair, cancellationToken);
            if (candidateComparison is null) return false;
        }
        // An excluded nested partition and a corresponding INCLUDED nesting are two distinct admitted
        // relations; a row that qualifies for neither keeps the strict overlap refusal.
        if (SyncPairPartitionPolicy.AllowsNestedOverlapPath(candidateComparison, comparison, existingPath,
                candidatePath, retainedPairId, rowSide)) return true;
        return SyncPairPartitionPolicy.AllowsCorrespondingIncludedPath(candidateComparison, comparison, existingPath,
            candidatePath, retainedPairId, rowSide);
    }

    private static async Task<SyncPair?> LoadRetainedPairDefinitionAsync(SqliteConnection connection,
        SqliteTransaction transaction, string pairId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT pair_json FROM sync_pair WHERE pair_id=$id AND state<>'retired' AND pair_json IS NOT NULL;";
        command.Parameters.AddWithValue("$id", pairId);
        var json = await command.ExecuteScalarAsync(cancellationToken);
        return json is string text && !string.IsNullOrWhiteSpace(text) ? ReadRetainedPair(text, pairId) : null;
    }

    private static SyncPair? ReadRetainedPair(string json, string expectedPairId)
    {
        try
        {
            var pair = JsonSerializer.Deserialize<SyncPair>(json);
            if (pair is null) return null;
            pair = SyncPairStore.NormalizePair(pair);
            return string.Equals(pair.Id, expectedPairId, StringComparison.Ordinal) ? pair : null;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException) { return null; }
    }

    /// <summary>
    /// The real recursive directory effect fence for a retained nested partition. A directory delete, move or
    /// cross-pair move whose member path is the excluded nested child subtree itself or any ancestor of it is
    /// refused at admission, before any effect is reserved, so a parent run can never enter, move or
    /// recursively delete the child's or a reserved private subtree. Ordinary sibling and deeper paths and
    /// pairs without a retained nested partition are unaffected, and an already-issued effect is never
    /// re-opened or replayed by this fence.
    /// </summary>
    private static async Task RequireNestedPartitionDisjointEffectAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncAuthorityOperationPlan plan, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.PairId)) return;
        if (plan.Kind is not (SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.DeleteRemote or
            SyncAuthorityOperationKind.MoveLocal or SyncAuthorityOperationKind.MoveRemote or
            SyncAuthorityOperationKind.CrossPairMove)) return;
        if (!IsDirectoryEffect(plan)) return;
        var pair = await LoadRetainedPairDefinitionAsync(connection, transaction, plan.PairId, cancellationToken);
        if (pair is null) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT pair_id, pair_json FROM sync_pair WHERE state<>'retired' AND pair_json IS NOT NULL;";
        var others = new List<SyncPair>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var other = ReadRetainedPair(reader.GetString(1), reader.GetString(0));
                if (other is not null && !string.Equals(other.Id, pair.Id, StringComparison.Ordinal))
                    others.Add(other);
            }
        if (others.Count == 0) return;
        var known = new List<SyncPair>(others.Count + 1) { pair };
        known.AddRange(others);
        foreach (var other in others)
        {
            var subtree = SyncPairPartitionPolicy.NestedSubtreeRelative(pair, other);
            if (subtree is null || !SyncPairPartitionPolicy.ExcludesWholeSubtree(pair, subtree)) continue;
            foreach (var member in plan.Members)
            {
                if (member.Role is not ("source" or "destination" or "affected")) continue;
                if (string.IsNullOrWhiteSpace(member.ExpectedPathKey)) continue;
                if (SyncPairPartitionPolicy.RefusesAncestorDirectoryEffect(pair, known, member.ExpectedPathKey!,
                        isDirectory: true))
                    throw new InvalidOperationException(
                        "The directory effect at '" + member.ExpectedPathKey +
                        "' is an ancestor of an excluded nested child subtree and is refused by the retained nested-pair partition.");
            }
        }
    }

    private static bool IsDirectoryEffect(SyncAuthorityOperationPlan plan)
    {
        try
        {
            using var preconditions = JsonDocument.Parse(plan.PreconditionsJson);
            if (preconditions.RootElement.ValueKind == JsonValueKind.Object &&
                preconditions.RootElement.TryGetProperty("entryKind", out var entryKind) &&
                entryKind.ValueKind == JsonValueKind.String &&
                string.Equals(entryKind.GetString(), "Directory", StringComparison.Ordinal)) return true;
        }
        catch (JsonException) { }
        foreach (var member in plan.Members)
        {
            if (member.Role is not ("source" or "destination")) continue;
            try
            {
                using var expected = JsonDocument.Parse(member.ExpectedStateJson);
                if (expected.RootElement.ValueKind != JsonValueKind.Object) continue;
                if ((expected.RootElement.TryGetProperty("ObjectKind", out var kind) ||
                     expected.RootElement.TryGetProperty("objectKind", out kind)) &&
                    kind.ValueKind == JsonValueKind.String &&
                    string.Equals(kind.GetString(), "directory", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (JsonException) { }
        }
        return false;
    }

    private static string NormalizeAuthorityPath(string side, string path) => side switch
    {
        "local" or "local_control" => PilotPath.NormalizeLocalRoot(path),
        "dropbox" or "dropbox_control" => PilotPath.NormalizeDropboxRoot(path),
        _ => throw new ArgumentOutOfRangeException(nameof(side))
    };

    private static bool AuthorityPathsOverlap(string left, string right, char separator)
    {
        var first = left.TrimEnd(separator);
        var second = right.TrimEnd(separator);
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
               first.StartsWith(second + separator, StringComparison.OrdinalIgnoreCase) ||
               second.StartsWith(first + separator, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<SyncAuthorityMember>> LoadMembersAsync(SqliteConnection connection,
        string operationId, CancellationToken cancellationToken)
    {
        var result = new List<SyncAuthorityMember>();
        await using var command = connection.CreateCommand();
        // operation_member_operation_id filters the few rows, which are then sorted.
        command.CommandText = "SELECT operation_member_id,pair_id,object_id,role,root_binding_id,membership_id,object_version_id,expected_membership_generation,expected_path_key,expected_state_json FROM operation_member WHERE operation_id=$id ORDER BY operation_member_id;";
        command.Parameters.AddWithValue("$id", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadMember(reader));
        }
        return result;
    }

    private static SyncAuthorityMember ReadMember(SqliteDataReader reader, int offset = 0) =>
        new(reader.GetString(offset), reader.GetString(offset + 1), reader.GetString(offset + 2),
            reader.GetString(offset + 3), reader.GetString(offset + 4),
            reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5),
            reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6),
            reader.IsDBNull(offset + 7) ? null : reader.GetInt64(offset + 7),
            reader.IsDBNull(offset + 8) ? null : reader.GetString(offset + 8), reader.GetString(offset + 9));

    private static async Task<IReadOnlyList<SyncAuthorityDependency>> LoadDependenciesAsync(SqliteConnection connection,
        string operationId, CancellationToken cancellationToken)
    {
        var result = new List<SyncAuthorityDependency>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT depends_on_operation_id,dependency_kind FROM operation_dependency_state WHERE operation_id=$id ORDER BY depends_on_operation_id;";
        command.Parameters.AddWithValue("$id", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new SyncAuthorityDependency(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static async Task<IReadOnlyList<SyncAuthorityAllocation>> LoadAllocationsAsync(SqliteConnection connection,
        string operationId, CancellationToken cancellationToken)
    {
        var result = new List<SyncAuthorityAllocation>();
        await using var command = connection.CreateCommand();
        // action_resource_owner filters the few rows, which are then sorted.
        command.CommandText = "SELECT allocation_id,control_namespace_id,resource_kind,deterministic_name,intended_parent_identity,owner_nonce_hash,status,bound_identity,content_hash,size_bytes FROM resource_allocation WHERE operation_id=$id ORDER BY allocation_id;";
        command.Parameters.AddWithValue("$id", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadAllocation(reader));
        }
        return result;
    }

    private static SyncAuthorityAllocation ReadAllocation(SqliteDataReader reader, int offset = 0) =>
        new(reader.GetString(offset), reader.GetString(offset + 1),
            SyncAuthorityNames.AllocationKind(reader.GetString(offset + 2)), reader.GetString(offset + 3),
            reader.GetString(offset + 4), reader.GetString(offset + 5), AllocationStatus(reader.GetString(offset + 6)),
            reader.IsDBNull(offset + 7) ? null : reader.GetString(offset + 7),
            reader.IsDBNull(offset + 8) ? null : reader.GetString(offset + 8),
            reader.IsDBNull(offset + 9) ? null : reader.GetInt64(offset + 9));

    private static SyncAuthorityAllocationStatus AllocationStatus(string value) => value switch
    {
        "authorized" => SyncAuthorityAllocationStatus.Authorized,
        "creating" => SyncAuthorityAllocationStatus.Creating,
        "created" => SyncAuthorityAllocationStatus.Created,
        "bound" => SyncAuthorityAllocationStatus.Bound,
        "verified" => SyncAuthorityAllocationStatus.Verified,
        "released" => SyncAuthorityAllocationStatus.Released,
        "blocked" => SyncAuthorityAllocationStatus.Blocked,
        _ => throw new InvalidDataException("The retained resource-allocation status is not supported.")
    };

    private static async Task<IReadOnlyList<SyncAuthorityAttempt>> LoadAttemptsAsync(SqliteConnection connection,
        string operationId, CancellationToken cancellationToken)
    {
        var result = new List<SyncAuthorityAttempt>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT attempt_id,attempt_no,lease_id,fencing_token,request_fingerprint,effect_kind,status,started_utc,ended_utc FROM operation_attempt WHERE operation_id=$id ORDER BY attempt_no;";
        command.Parameters.AddWithValue("$id", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SyncAuthorityAttempt(reader.GetString(0), operationId, reader.GetInt32(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetString(4), reader.GetString(5),
                SyncAuthorityNames.AttemptStatus(reader.GetString(6)), DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture)));
        }
        return result;
    }

    private static async Task<byte[]> ObservationDigestAsync(SqliteConnection connection, string scanGenerationId,
        CancellationToken cancellationToken)
    {
        var canonical = new StringBuilder();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT evidence_json FROM effective_observation WHERE scan_generation_id=$id ORDER BY evidence_json;";
        command.Parameters.AddWithValue("$id", scanGenerationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = reader.GetString(0);
            canonical.Append(value.Length).Append(':').Append(value).Append('|');
            canonical.Append('\n');
        }
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ExecuteCountAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarStringAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static void ValidatePlan(SyncAuthorityOperationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RequireIdentifier(plan.OperationId, nameof(plan.OperationId));
        RequireIdentifier(plan.IdempotencyKey, nameof(plan.IdempotencyKey));
        RequireIdentifier(plan.CapabilityProfileId, nameof(plan.CapabilityProfileId));
        if (plan.PairId is not null) RequireIdentifier(plan.PairId, nameof(plan.PairId));
        if (plan.ObjectId is not null) RequireIdentifier(plan.ObjectId, nameof(plan.ObjectId));
        if (plan.OperationGroupId is not null) RequireIdentifier(plan.OperationGroupId, nameof(plan.OperationGroupId));
        if (plan.ConfigurationRevision < 1) throw new ArgumentOutOfRangeException(nameof(plan.ConfigurationRevision));
        RequireJson(plan.PreconditionsJson, nameof(plan.PreconditionsJson));
        if (plan.Members.Count == 0) throw new InvalidDataException("An effectful operation requires at least one immutable member.");
        if (plan.Members.Select(item => item.MemberId).Distinct(StringComparer.Ordinal).Count() != plan.Members.Count)
            throw new InvalidDataException("Operation member IDs must be unique.");
        foreach (var member in plan.Members)
        {
            RequireIdentifier(member.MemberId, nameof(member.MemberId));
            RequireIdentifier(member.PairId, nameof(member.PairId));
            RequireIdentifier(member.ObjectId, nameof(member.ObjectId));
            RequireIdentifier(member.RootBindingId, nameof(member.RootBindingId));
            if (member.Role is not ("source" or "destination" or "affected" or "lock" or "recovery"))
                throw new InvalidDataException("The operation member role is not supported.");
            RequireJson(member.ExpectedStateJson, nameof(member.ExpectedStateJson));
        }
        foreach (var dependency in plan.Dependencies)
        {
            RequireIdentifier(dependency.DependsOnOperationId, nameof(dependency.DependsOnOperationId));
            if (dependency.Kind is not ("must_complete" or "must_commit" or "must_resolve"))
                throw new InvalidDataException("The operation dependency kind is not supported.");
        }
        foreach (var allocation in plan.Allocations)
        {
            RequireIdentifier(allocation.AllocationId, nameof(allocation.AllocationId));
            RequireIdentifier(allocation.ControlNamespaceId, nameof(allocation.ControlNamespaceId));
            ArgumentException.ThrowIfNullOrWhiteSpace(allocation.DeterministicName);
            ArgumentException.ThrowIfNullOrWhiteSpace(allocation.IntendedParentIdentity);
            RequireHash(allocation.OwnerNonceHash, nameof(allocation.OwnerNonceHash));
        }
        var planningAuthorities = plan.PlanningAuthorities ?? [];
        if (planningAuthorities.Select(item => item.PairId).Distinct(StringComparer.Ordinal).Count() !=
            planningAuthorities.Count)
            throw new InvalidDataException("An operation cannot bind two planning authorities for one pair.");
        if (planningAuthorities.Select(item => item.ObservationSetId).Distinct(StringComparer.Ordinal).Count() > 1)
            throw new InvalidDataException("An operation's planning authorities must belong to one observation set.");
        foreach (var planningAuthority in planningAuthorities) ValidatePlanningAuthority(planningAuthority);
        var subtreeReservations = plan.SubtreeReservations ?? [];
        if (subtreeReservations.Count > 0 && plan.Kind != SyncAuthorityOperationKind.CrossPairMove)
            throw new InvalidDataException(
                "Subtree-prefix authority is limited to one coordinated cross-pair move.");
        if (subtreeReservations.Select(item => (item.RootBindingId,
                    Path: PilotPath.NormalizeRelative(item.RelativePath)))
                .Distinct(new RootPathComparer()).Count() != subtreeReservations.Count)
            throw new InvalidDataException("Operation subtree-prefix reservations must be unique.");
        foreach (var reservation in subtreeReservations)
        {
            RequireIdentifier(reservation.PairId, nameof(reservation.PairId));
            RequireIdentifier(reservation.RootBindingId, nameof(reservation.RootBindingId));
            _ = PilotPath.NormalizeRelative(reservation.RelativePath);
            if (!plan.Members.Any(member =>
                    string.Equals(member.PairId, reservation.PairId, StringComparison.Ordinal) &&
                    string.Equals(member.RootBindingId, reservation.RootBindingId, StringComparison.Ordinal)))
                throw new InvalidDataException(
                    "A subtree-prefix reservation is not bound to an immutable operation participant root.");
        }
        using (var preconditions = JsonDocument.Parse(plan.PreconditionsJson))
        {
            var isDirectoryMove = plan.Kind == SyncAuthorityOperationKind.CrossPairMove &&
                                  preconditions.RootElement.TryGetProperty("entryKind", out var entryKind) &&
                                  entryKind.ValueKind == JsonValueKind.String &&
                                  string.Equals(entryKind.GetString(), "Directory", StringComparison.Ordinal);
            if (isDirectoryMove && subtreeReservations.Count != 4)
                throw new InvalidDataException(
                    "A directory cross-pair move requires four immutable source/destination subtree-prefix reservations.");
            if (!isDirectoryMove && subtreeReservations.Count != 0)
                throw new InvalidDataException(
                    "Subtree-prefix reservations require a directory cross-pair move.");
        }
    }

    private static void ValidatePlanningAuthority(SyncAuthorityPlanningAuthority planningAuthority)
    {
        ArgumentNullException.ThrowIfNull(planningAuthority);
        RequireIdentifier(planningAuthority.ObservationSetId, nameof(planningAuthority.ObservationSetId));
        RequireIdentifier(planningAuthority.PairId, nameof(planningAuthority.PairId));
        RequireIdentifier(planningAuthority.LocalRootBindingId, nameof(planningAuthority.LocalRootBindingId));
        RequireIdentifier(planningAuthority.DropboxRootBindingId, nameof(planningAuthority.DropboxRootBindingId));
        RequireIdentifier(planningAuthority.LocalScanGenerationId, nameof(planningAuthority.LocalScanGenerationId));
        RequireIdentifier(planningAuthority.DropboxScanGenerationId, nameof(planningAuthority.DropboxScanGenerationId));
        RequireHash(planningAuthority.PlanningStateFingerprint,
            nameof(planningAuthority.PlanningStateFingerprint));
        RequireHash(planningAuthority.ObservationContentFingerprint ?? string.Empty,
            nameof(planningAuthority.ObservationContentFingerprint));
        RequireHash(planningAuthority.SynchronizationPointSetFingerprint ?? string.Empty,
            nameof(planningAuthority.SynchronizationPointSetFingerprint));
        RequireHash(planningAuthority.RootAuthorityFingerprint ?? string.Empty,
            nameof(planningAuthority.RootAuthorityFingerprint));
        if (planningAuthority.ConnectionId is not null)
            RequireIdentifier(planningAuthority.ConnectionId, nameof(planningAuthority.ConnectionId));
        if (planningAuthority.ObjectId is not null)
            RequireIdentifier(planningAuthority.ObjectId, nameof(planningAuthority.ObjectId));
        if (planningAuthority.SynchronizationPointId is not null)
            RequireIdentifier(planningAuthority.SynchronizationPointId,
                nameof(planningAuthority.SynchronizationPointId));
        if (planningAuthority.ObjectId is null && planningAuthority.SynchronizationPointId is not null)
            throw new InvalidDataException("A synchronization-point authority requires its logical object.");
        if (planningAuthority.ConfigurationRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(planningAuthority.ConfigurationRevision));
    }

    private static string PersistPlanningAuthorities(string preconditionsJson,
        IReadOnlyList<SyncAuthorityPlanningAuthority> planningAuthorities)
    {
        if (planningAuthorities.Count == 0) return preconditionsJson;
        using var document = JsonDocument.Parse(preconditionsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Planning authority requires object-shaped operation preconditions.");
        if (document.RootElement.TryGetProperty("planningAuthorities", out _))
            throw new InvalidDataException("Operation preconditions cannot supply their own planning authority.");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject()) property.WriteTo(writer);
            writer.WritePropertyName("planningAuthorities");
            JsonSerializer.Serialize(writer, planningAuthorities.OrderBy(item => item.PairId,
                StringComparer.Ordinal).ToArray());
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string PersistSubtreeReservations(string preconditionsJson,
        IReadOnlyList<SyncAuthoritySubtreeReservation> subtreeReservations)
    {
        if (subtreeReservations.Count == 0) return preconditionsJson;
        using var document = JsonDocument.Parse(preconditionsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Subtree-prefix authority requires object-shaped operation preconditions.");
        if (document.RootElement.TryGetProperty("subtreeReservations", out _))
            throw new InvalidDataException("Operation preconditions cannot supply their own subtree-prefix authority.");
        var normalized = subtreeReservations
            .Select(item => item with { RelativePath = PilotPath.NormalizeRelative(item.RelativePath) })
            .OrderBy(item => item.PairId, StringComparer.Ordinal)
            .ThenBy(item => item.RootBindingId, StringComparer.Ordinal)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject()) property.WriteTo(writer);
            writer.WritePropertyName("subtreeReservations");
            JsonSerializer.Serialize(writer, normalized);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IReadOnlyList<SyncAuthorityPlanningAuthority> LoadPlanningAuthorities(
        string preconditionsJson)
    {
        using var document = JsonDocument.Parse(preconditionsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("planningAuthorities", out var retained))
            return [];
        if (retained.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The retained operation planning authority is malformed.");
        SyncAuthorityPlanningAuthority[] authorities;
        try
        {
            authorities = JsonSerializer.Deserialize<SyncAuthorityPlanningAuthority[]>(retained.GetRawText()) ??
                          throw new InvalidDataException("The retained operation planning authority is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The retained operation planning authority is malformed.", exception);
        }
        if (authorities.Select(item => item.PairId).Distinct(StringComparer.Ordinal).Count() != authorities.Length ||
            authorities.Select(item => item.ObservationSetId).Distinct(StringComparer.Ordinal).Count() > 1)
            throw new InvalidDataException("The retained operation planning authority is inconsistent.");
        foreach (var authority in authorities) ValidatePlanningAuthority(authority);
        return authorities;
    }

    private static IReadOnlyList<SyncAuthoritySubtreeReservation> LoadSubtreeReservations(
        string preconditionsJson)
    {
        using var document = JsonDocument.Parse(preconditionsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The retained operation preconditions are not object-shaped.");
        if (document.RootElement.TryGetProperty("subtreeReservations", out var retained))
        {
            if (retained.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The retained subtree-prefix authority is malformed.");
            SyncAuthoritySubtreeReservation[] reservations;
            try
            {
                reservations = JsonSerializer.Deserialize<SyncAuthoritySubtreeReservation[]>(
                                   retained.GetRawText()) ??
                               throw new InvalidDataException("The retained subtree-prefix authority is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The retained subtree-prefix authority is malformed.", exception);
            }
            return NormalizeRetainedSubtreeReservations(reservations);
        }

        // Version-3 operations created before explicit prefix serialization still retain
        // enough immutable authority to reconstruct the four directory prefixes exactly.
        if (!document.RootElement.TryGetProperty("entryKind", out var entryKind) ||
            entryKind.ValueKind != JsonValueKind.String ||
            !string.Equals(entryKind.GetString(), "Directory", StringComparison.Ordinal))
            return [];
        var sourcePairId = RequiredJsonString(document.RootElement, "sourcePairId");
        var destinationPairId = RequiredJsonString(document.RootElement, "destinationPairId");
        var sourcePath = PilotPath.NormalizeRelative(RequiredJsonString(document.RootElement, "sourcePath"));
        var destinationPath = PilotPath.NormalizeRelative(
            RequiredJsonString(document.RootElement, "destinationPath"));
        var authorities = LoadPlanningAuthorities(preconditionsJson);
        var source = authorities.SingleOrDefault(item =>
                         string.Equals(item.PairId, sourcePairId, StringComparison.Ordinal)) ??
                     throw new InvalidDataException(
                         "The retained directory move has no source root authority for prefix reconstruction.");
        var destination = authorities.SingleOrDefault(item =>
                              string.Equals(item.PairId, destinationPairId, StringComparison.Ordinal)) ??
                          throw new InvalidDataException(
                              "The retained directory move has no destination root authority for prefix reconstruction.");
        return NormalizeRetainedSubtreeReservations(
        [
            new(sourcePairId, source.LocalRootBindingId, sourcePath),
            new(sourcePairId, source.DropboxRootBindingId, sourcePath),
            new(destinationPairId, destination.LocalRootBindingId, destinationPath),
            new(destinationPairId, destination.DropboxRootBindingId, destinationPath)
        ]);
    }

    private static IReadOnlyList<SyncAuthoritySubtreeReservation> NormalizeRetainedSubtreeReservations(
        IReadOnlyList<SyncAuthoritySubtreeReservation> reservations)
    {
        var normalized = reservations.Select(item =>
        {
            RequireIdentifier(item.PairId, nameof(item.PairId));
            RequireIdentifier(item.RootBindingId, nameof(item.RootBindingId));
            return item with { RelativePath = PilotPath.NormalizeRelative(item.RelativePath) };
        }).OrderBy(item => item.PairId, StringComparer.Ordinal)
            .ThenBy(item => item.RootBindingId, StringComparer.Ordinal)
            .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length != 4 || normalized
                .Select(item => (item.RootBindingId, Path: item.RelativePath))
                .Distinct(new RootPathComparer()).Count() != normalized.Length)
            throw new InvalidDataException(
                "The retained directory move does not have four unique source/destination subtree-prefix authorities.");
        return normalized;
    }

    private static void RequireIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 160 || value.Any(character => char.IsControl(character)))
            throw new ArgumentException("The authority identifier is not accepted.", parameterName);
    }

    private static void RequireJson(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        try { using var _ = JsonDocument.Parse(value); }
        catch (JsonException exception) { throw new ArgumentException("Valid JSON is required.", parameterName, exception); }
    }

    private static void RequireHash(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A lowercase or uppercase SHA-256 hex value is required.", parameterName);
    }

    private static bool RequiresPathReservation(SyncAuthorityOperationKind kind, string role) => kind switch
    {
        SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.CreateRemote or
            SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.UpdateRemote or
            SyncAuthorityOperationKind.ResolveConflict => role is "destination" or "affected" or "lock",
        SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.DeleteRemote =>
            role is "source" or "affected" or "lock",
        SyncAuthorityOperationKind.MoveLocal or SyncAuthorityOperationKind.MoveRemote or
            SyncAuthorityOperationKind.CrossPairMove => role is "destination" or "affected" or "lock",
        _ => role is "affected" or "lock"
    };

    private static bool SameRootAndPath(string leftRoot, string leftPath, string rightRoot,
        string rightPath) => string.Equals(leftRoot, rightRoot, StringComparison.Ordinal) &&
                             string.Equals(PilotPath.NormalizeRelative(leftPath),
                                 PilotPath.NormalizeRelative(rightPath), StringComparison.OrdinalIgnoreCase);

    private static bool SameRootAndDescendant(string pathRoot, string path, string prefixRoot,
        string prefix) => string.Equals(pathRoot, prefixRoot, StringComparison.Ordinal) &&
                          IsSameOrDescendantPath(path, prefix);

    private static bool SameRootAndOverlappingSubtree(string leftRoot, string leftPrefix,
        string rightRoot, string rightPrefix) => string.Equals(leftRoot, rightRoot, StringComparison.Ordinal) &&
                                                 (IsSameOrDescendantPath(leftPrefix, rightPrefix) ||
                                                  IsSameOrDescendantPath(rightPrefix, leftPrefix));

    private static bool IsSameOrDescendantPath(string path, string prefix)
    {
        var normalizedPath = PilotPath.NormalizeRelative(path);
        var normalizedPrefix = PilotPath.NormalizeRelative(prefix);
        return string.Equals(normalizedPath, normalizedPrefix, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedPrefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CompetingPathReservation(
        string OperationId,
        string RootBindingId,
        string Path,
        SyncAuthorityOperationKind Kind,
        string Status,
        string PreconditionsJson);

    private sealed class RootPathComparer : IEqualityComparer<(string RootBindingId, string Path)>
    {
        public bool Equals((string RootBindingId, string Path) x, (string RootBindingId, string Path) y) =>
            string.Equals(x.RootBindingId, y.RootBindingId, StringComparison.Ordinal) &&
            string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string RootBindingId, string Path) value) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.RootBindingId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path));
    }

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
