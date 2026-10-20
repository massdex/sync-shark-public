using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed record StorageOptInResult(bool Enabled, string? BackupPath, string? Warning);

public sealed partial class SqliteStateStore
{
    internal static string StorageRelativePath(string path)
    {
        var relative = path.Replace('\\', '/').Trim('/');
        if (relative.Split('/').Any(part => part is "." or "..") || Path.IsPathRooted(path) || path.Contains(':'))
            throw new ArgumentException("Use a relative path inside the pair, or an empty path for its root.", nameof(path));
        return relative;
    }

    // Explicit commands must see another process's opt-in immediately, not the idle-loop probe cache.
    public bool IsStorageEnabled => InStore(() =>
    {
        if (!_storageEnabled && FilesOnDemandSchema.IsEnabled(_connection))
        {
            _storageEnabled = true;
            _reads.StorageEnabled = true;
        }
        return _storageEnabled;
    });

    /// <summary>CLI opt-in: finish a WAL-aware SQLite snapshot before changing the feature schema.</summary>
    public StorageOptInResult EnableStorageWithBackup(string? backupDirectory, bool confirmWithoutBackup = false) => InStore(() =>
    {
        if (IsStorageEnabled) return new StorageOptInResult(true, null, null);
        string? backupPath = null;
        string? warning = null;
        try
        {
            if (string.IsNullOrWhiteSpace(backupDirectory) || !Path.IsPathFullyQualified(backupDirectory))
                throw new IOException("Choose an absolute backup directory, or configure the local Recovery location.");
            var directory = Path.Combine(backupDirectory, "StateBackups");
            Directory.CreateDirectory(directory);
            backupPath = Path.Combine(directory, "state.db.pre-fod-" + Guid.NewGuid().ToString("N") + ".db");
            // Reserve a fresh destination: never overwrite a backup or any existing user file.
            using (new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backupPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString()))
            {
                destination.Open();
                // File.Copy misses committed WAL pages; SQLite's online backup API snapshots them consistently.
                _connection.BackupDatabase(destination);
                StoreSchema.Execute(destination, null, "PRAGMA journal_mode=DELETE;");
                if (!string.Equals(StoreSchema.Scalar(destination, null, "PRAGMA quick_check;") as string, "ok", StringComparison.Ordinal))
                    throw new IOException("The backup integrity check did not succeed.");
            }
            using var durable = new FileStream(backupPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            durable.Flush(flushToDisk: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or ArgumentException or NotSupportedException)
        {
            warning = "Pre-storage backup failed: " + error.Message +
                (backupPath is null ? "" : " Incomplete backup candidate (not a rollback copy): " + backupPath +
                    ". You may remove this incomplete candidate after verifying a successful replacement backup; Sync Shark does not delete it.") +
                " Retry storage online-only with the same pair and path, choose another location with " +
                "--backup-directory <absolute-directory>, or explicitly accept losing this rollback snapshot " +
                "with --confirm-without-backup.";
            backupPath = null;
            if (!confirmWithoutBackup) return new StorageOptInResult(false, null, warning);
            warning += " Proceeding without a backup because --confirm-without-backup was explicitly supplied.";
        }
        EnableStorage();
        return new StorageOptInResult(true, backupPath, warning);
    });
}
