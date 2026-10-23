using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    // This scope admits tentative reads only. It is not a health verdict, and every
    // attempted write is rejected until fresh command admission has completed.
    private readonly AsyncLocal<ColdCommandPreparation?> _coldCommandPreparation = new();
    internal sealed record ColdInstallation(string Id, string Generation);
    internal ColdInstallation? TentativeCommandInstallation =>
        _coldCommandPreparation.Value is { Active: true } scope ? scope.Installation : null;
    internal static Action<string>? ColdCommandAdmissionBoundaryForTest { get; set; }
    internal static Action<string>? CommandAdmissionCheckedForTest { get; set; }

    internal async Task<ColdInstallation?> InspectColdCommandEligibilityAsync(CancellationToken token)
    {
        if (!File.Exists(_databasePath)) return null;
        await using var connection = await OpenUnpooledInspectionConnectionAsync(_databasePath, token);
        if (await ScalarLongAsync(connection, null, "PRAGMA user_version;", token) != SyncAuthoritySchema.Version)
            return null; // Existing protected migration path owns older schemas.
        await RequireExactCurrentSchemaAsync(connection, token, verifyHealth: false);
        return await ReadColdCommandEligibilityAsync(connection, null, token);
    }

    private static async Task<ColdInstallation?> ReadColdCommandEligibilityAsync(SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken token)
    {
        // Conservative: any unpublished active reservation uses normal startup,
        // even when that startup will ultimately decide it cannot release it.
        if (await ScalarLongAsync(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM command_request WHERE status IN ('requested','accepted','executing'))
              OR EXISTS(SELECT 1 FROM sync_pair p JOIN root_binding r ON r.pair_id=p.pair_id
                WHERE p.pair_json IS NULL AND p.state<>'retired' AND r.state='active'
                  AND r.side IN ('local','dropbox'));
            """, token) != 0) return null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT provider_account_id,account_id,root_namespace_id,home_namespace_id FROM provider_account WHERE provider='dropbox';";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                if (SyncAuthorityProviderAccountIdentity.IsLegacyGoogleProviderAccount(reader.GetString(0),
                        reader.GetString(1), reader.GetString(2), reader.GetString(3))) return null;
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT installation_id,database_generation FROM installation LIMIT 2;";
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return null;
            var identity = new ColdInstallation(reader.GetString(0), reader.GetString(1));
            if (await reader.ReadAsync(token)) throw new InvalidDataException("Authority has multiple installation identities.");
            return identity;
        }
    }

    internal IDisposable? BeginColdCommandPreparation(ColdInstallation? installation)
    {
        if (installation is null || _coldCommandPreparation.Value is { Active: true }) return null;
        var scope = new ColdCommandPreparation(this, installation, _coldCommandPreparation.Value);
        _coldCommandPreparation.Value = scope;
        return scope;
    }

    private sealed class ColdCommandPreparation(SyncAuthorityDatabase owner, ColdInstallation installation,
        ColdCommandPreparation? previous) : IDisposable
    {
        internal ColdInstallation Installation { get; } = installation;
        internal bool Active { get; private set; } = true;
        public void Dispose()
        {
            Active = false;
            if (ReferenceEquals(owner._coldCommandPreparation.Value, this)) owner._coldCommandPreparation.Value = previous;
        }
    }

    internal void EndColdCommandPreparation() => _coldCommandPreparation.Value?.Dispose();

    internal async Task<SyncAuthorityCommandAdmission?> TryAdmitColdCommandAdmissionAsync(string commandId,
        string commandKind, string? pairId,
        string? targetId, string payloadJson, string profileId, string sourceRevision, string profileJson,
        Func<bool> startupStillUnneeded, CancellationToken token, Action<string>? progress,
        SyncAuthorityCommandReservation? reservation = null,
        SyncAuthorityCommandPurpose purpose = SyncAuthorityCommandPurpose.NormalEffect)
    {
        var expected = TentativeCommandInstallation;
        if (expected is null || !startupStillUnneeded()) return null;
        using var parent = PilotDirectoryLease.OpenProductDirectoryReadGuard(Path.GetDirectoryName(_databasePath)!);
        using var file = SyncLocalFileAuthority.OpenExistingMutableReadGuard(_databasePath);
        var fileIdentity = file.VerifySingleLink();
        if (!string.Equals(file.FinalPath, _databasePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Authority database path changed before command admission.");
        parent.VerifyBinding();
        await using var health = await OpenUnpooledInspectionConnectionAsync(_databasePath, token);
        await RequireExactCurrentSchemaAsync(health, token, verifyHealth: false);
        var version = await ScalarLongAsync(health, null, "PRAGMA data_version;", token);
        if (await ReadColdCommandEligibilityAsync(health, null, token) != expected) return null;
        progress?.Invoke("Admission: checking current schema and installation identity before recording this command; tentative preparation made no changes.");
        // Recheck commits on this SAME read-only connection under the short write
        // transaction below; command admission never scans the full retained database.
        ColdCommandAdmissionBoundaryForTest?.Invoke("schema-identity-checked");
        CommandAdmissionCheckedForTest?.Invoke(_databasePath);
        token.ThrowIfCancellationRequested();
        await _writer.WaitAsync(token);
        try
        {
            parent.VerifyBinding();
            file.VerifySameSingleLink(fileIdentity);
            await using var currentFile = SyncLocalFileAuthority.OpenExistingMutableReadGuard(_databasePath);
            currentFile.VerifySameSingleLink(fileIdentity);
            await using var connection = new SqliteConnection(ConnectionString(_databasePath, SqliteOpenMode.ReadWrite, false));
            await connection.OpenAsync(token);
            await RequireExactCurrentSchemaAsync(connection, token, verifyHealth: false);
            // Set connection-local safety only. Do not change a persistent journal
            // mode before proving that the inspected database is still current.
            await ExecuteAsync(connection, null,
                "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL; PRAGMA trusted_schema=OFF; PRAGMA wal_autocheckpoint=1000; PRAGMA main.journal_size_limit=33554432;", token);
            await RequireConnectionSettingsAsync(connection, token);
            // Explicit immediate transaction excludes SQLite writers only for the
            // short rebind/registration interval.
            using var transaction = connection.BeginTransaction(deferred: false);
            if (await ScalarLongAsync(health, null, "PRAGMA data_version;", token) != version ||
                await ReadColdCommandEligibilityAsync(connection, transaction, token) != expected ||
                !startupStillUnneeded()) return null;
            token.ThrowIfCancellationRequested();
            await RegisterCapabilityProfileCoreAsync(connection, transaction, profileId, sourceRevision, profileJson, token);
            ColdCommandAdmissionBoundaryForTest?.Invoke("profile-recorded-before-command");
            token.ThrowIfCancellationRequested();
            // The reservation is claimed at this actual INSERT, inside the immediate write transaction and
            // after the last eligibility re-check, so every refusal above this point leaves the caller's
            // ticket usable by the normal protected path and an ambiguous first transaction here can never
            // later create the missing row.
            var admitted = await CreateCommandRequestCoreAsync(connection, transaction, commandId, commandKind, pairId,
                targetId, null, payloadJson, purpose, token, reservation);
            await TransitionCommandRequestCoreAsync(connection, transaction, commandId, "accepted", null, token);
            token.ThrowIfCancellationRequested();
            await transaction.CommitAsync(token);
            EndColdCommandPreparation();
            progress?.Invoke("Admission: current profile and accepted command recorded atomically after fresh schema and identity checks.");
            return admitted;
        }
        finally { _writer.Release(); }
    }


    /// <summary>The bool view of the same cold admission, for callers that only need the decision.</summary>
    internal async Task<bool> TryAdmitColdCommandAsync(string commandId, string commandKind, string? pairId,
        string? targetId, string payloadJson, string profileId, string sourceRevision, string profileJson,
        Func<bool> startupStillUnneeded, CancellationToken token, Action<string>? progress,
        SyncAuthorityCommandReservation? reservation = null,
        SyncAuthorityCommandPurpose purpose = SyncAuthorityCommandPurpose.NormalEffect)
        => await TryAdmitColdCommandAdmissionAsync(commandId, commandKind, pairId, targetId, payloadJson, profileId,
            sourceRevision, profileJson, startupStillUnneeded, token, progress, reservation, purpose) is not null;

    private static async Task RegisterCapabilityProfileCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string profileId, string revision, string json, CancellationToken token)
    {
        RequireIdentifier(profileId, nameof(profileId));
        RequireJson(json, nameof(json));
        await ExecuteAsync(connection, transaction,
            "INSERT OR IGNORE INTO capability_profile(capability_profile_id,source_revision,profile_json,created_utc) VALUES($id,$revision,$json,$utc);",
            token, ("$id", profileId), ("$revision", revision), ("$json", json), ("$utc", Utc(DateTimeOffset.UtcNow)));
        if (await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM capability_profile WHERE capability_profile_id=$id AND source_revision=$revision AND profile_json=$json;",
                token, ("$id", profileId), ("$revision", revision), ("$json", json)) != 1)
            throw new InvalidDataException("The retained capability profile differs from the exact current candidate.");
    }

    private static async Task<SqliteConnection> OpenUnpooledInspectionConnectionAsync(string path, CancellationToken token)
    {
        EnsureSqliteBootstrap();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        try
        {
            await connection.OpenAsync(token);
            await ExecuteAsync(connection, null,
                "PRAGMA query_only=ON; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF;", token);
            RequireSupportedSqliteVersion(await ScalarStringAsync(connection, null, "SELECT sqlite_version();", token)
                ?? throw new InvalidDataException("SQLite did not report its version."));
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
}
