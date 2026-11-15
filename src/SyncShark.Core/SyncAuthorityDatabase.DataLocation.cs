using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    internal static async Task<string?> VerifyDataLocationDatabaseAsync(string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await InspectReadOnlySchemaAsync(path, cancellationToken, verifyHealth: true);
        return await ReadInstallationIdAsync(path, cancellationToken)
            ?? throw new InvalidDataException("The data location database has no installation identity.");
    }

    internal static async Task<string?> PrepareDataLocationDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var installationId = await VerifyDataLocationDatabaseAsync(path, cancellationToken);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT (SELECT count(*) FROM operation WHERE status NOT IN ('completed','cancelled','superseded','blocked'))
                     + (SELECT count(*) FROM command_request WHERE status NOT IN ('completed','cancelled','rejected','stale','blocked'))
                     + (SELECT count(*) FROM operation_attempt WHERE status NOT IN ('verified_success','verified_not_applied','blocked'));
                """;
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0)
                throw new InvalidOperationException("Finish or resolve pending work first: open operations, commands or effects prevent moving the data location.");
        }
        await using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var reader = await checkpoint.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != 0)
            throw new IOException("The database is busy; its WAL could not be checkpointed. No data location was changed.");
        SqliteConnection.ClearAllPools();
        return installationId;
    }
}
