using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// com2600: one diagnostic checkpoint/restore proof, not a production backup
// command or migration. Reuses the actual product provider and readback.
internal static class CurrentStateRecoveryCheckpoint
{
    internal static async Task RunAsync(string source, string root, string settingsRoot, bool resume = false)
    {
        source = Path.GetFullPath(source);
        root = Path.GetFullPath(root);
        settingsRoot = Path.GetFullPath(settingsRoot);
        var allowed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TestData", "Projects", "sync-shark", "build") + Path.DirectorySeparatorChar;
        if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Directory.Exists(root) != resume ||
            !File.Exists(source) || !Directory.Exists(settingsRoot))
            throw new IOException("Use one new exact owner diagnostic checkpoint and existing state sources.");
        foreach (var path in new[] { source, settingsRoot, resume ? root : Path.GetDirectoryName(root)! })
            for (FileInfo? item = new FileInfo(path); item is not null; item = item.Directory is { } parent ? new FileInfo(parent.FullName) : null)
                if ((File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Recovery preservation cannot traverse a reparse target.");
        var growth = checked(new FileInfo(source).Length * (resume ? 2 : 3) + 64L * 1024 * 1024);
        if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace - growth < 10L * 1024 * 1024 * 1024)
            throw new IOException("The measured checkpoint, restore and comparison growth must retain the ten GiB floor.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        SQLitePCL.Batteries_V2.Init();
        Directory.CreateDirectory(root);
        var state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "sync-product-state", "sync-automation", "ui-preferences" })
            foreach (var slot in new[] { "a", "b" })
            {
                var file = Path.Combine(settingsRoot, name + "-" + slot + ".json");
                if (!File.Exists(file)) continue;
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse settings refused.");
                settings.Add(Path.GetFileName(file), Hash(file));
                if (!resume) File.Copy(file, Path.Combine(state, Path.GetFileName(file)), overwrite: false);
                if (!File.Exists(Path.Combine(state, Path.GetFileName(file))) ||
                    (File.GetAttributes(Path.Combine(state, Path.GetFileName(file))) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The retained non-secret settings slot is absent or replaced.");
                if (Hash(file) != settings[Path.GetFileName(file)] || Hash(Path.Combine(state, Path.GetFileName(file))) != settings[Path.GetFileName(file)])
                    throw new IOException("A non-secret settings slot changed during preservation.");
            }
        if (!settings.Keys.Any(name => name.StartsWith("sync-product-state-", StringComparison.Ordinal)) ||
            !settings.Keys.Any(name => name.StartsWith("ui-preferences-", StringComparison.Ordinal)))
            throw new IOException("Required non-secret runtime/interface state was not present.");
        var checkpoint = Path.Combine(root, "checkpoint.db");
        var restored = Path.Combine(root, "restore-proof.db");
        if (resume)
        {
            if (!File.Exists(checkpoint) || File.Exists(restored) ||
                (File.GetAttributes(checkpoint) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Resume requires the one retained checkpoint and no restore effect yet.");
            await VerifyCopyAsync(source, checkpoint, token);
        }
        else await CopyAndVerifyAsync(source, checkpoint, token);
        await CopyAndVerifyAsync(checkpoint, restored, token);
        foreach (var (name, hash) in settings)
            if (Hash(Path.Combine(settingsRoot, name)) != hash) throw new IOException("Settings changed across the database checkpoint boundary.");
        Console.WriteLine(JsonSerializer.Serialize(new { checkpoint, checkpointBytes = new FileInfo(checkpoint).Length,
            checkpointSha256 = Hash(checkpoint), restored, restoreBytes = new FileInfo(restored).Length,
            restoreSha256 = Hash(restored), settings, sourceMutated = false, coherentRestoreVerified = true }));
    }

    private static async Task CopyAndVerifyAsync(string source, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (File.Exists(destination)) throw new IOException("Recovery never overwrites an existing checkpoint.");
        string installation;
        int version;
        await using (var original = Connection(source, true))
        await using (var copy = Connection(destination, false))
        {
            await original.OpenAsync(token);
            await using var identity = original.CreateCommand();
            identity.CommandText = "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;";
            await identity.ExecuteNonQueryAsync(token);
            identity.CommandText = "SELECT installation_id FROM installation WHERE (SELECT count(*) FROM installation)=1;";
            installation = (string)(await identity.ExecuteScalarAsync(token) ?? throw new InvalidDataException("Installation identity absent."));
            identity.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(await identity.ExecuteScalarAsync(token));
            if (version != SyncAuthoritySchema.Version) throw new InvalidDataException("Only the actual current schema is supported; no migration.");
            await copy.OpenAsync(token);
            Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:O}] SQLite online backup starting; synchronous native copy, no cancellation claim.");
            original.BackupDatabase(copy);
            Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:O}] SQLite online backup returned; flushing and verifying current identity/content.");
        }
        await using (var durable = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.WriteThrough))
        { await durable.FlushAsync(token); durable.Flush(flushToDisk: true); }
        await VerifyCopyAsync(source, destination, token);
    }

    private static async Task VerifyCopyAsync(string source, string destination, CancellationToken token)
    {
        await using var original = Connection(source, true);
        await original.OpenAsync(token);
        await using var originalIdentity = original.CreateCommand();
        originalIdentity.CommandText = "SELECT installation_id FROM installation WHERE (SELECT count(*) FROM installation)=1;";
        var installation = (string)(await originalIdentity.ExecuteScalarAsync(token) ?? throw new InvalidDataException("Installation identity absent."));
        originalIdentity.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(await originalIdentity.ExecuteScalarAsync(token)) != SyncAuthoritySchema.Version)
            throw new InvalidDataException("Only the actual current schema is supported; no migration.");
        var database = new SyncAuthorityDatabase(destination);
        await database.VerifyHealthAsync(token, message => Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:O}] {message}"));
        await using (var copy = Connection(destination, true))
        {
            await copy.OpenAsync(token);
            await using var identity = copy.CreateCommand();
            identity.CommandText = "SELECT installation_id FROM installation;";
            if (!string.Equals((string?)await identity.ExecuteScalarAsync(token), installation, StringComparison.Ordinal))
                throw new InvalidDataException("The checkpoint installation identity differs.");
        }
        // Invoke only the existing read-only logical comparator, never the guarded
        // predecessor migration wrapper or any initialization/effect path.
        var compare = typeof(SyncAuthorityDatabase).GetMethod("RollbackContentsMatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("The bound product logical readback is unavailable.");
        var readback = (Task<bool>)compare.Invoke(new SyncAuthorityDatabase(source), [destination, token,
            (Action<string>)(message => Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:O}] {message}"))])!;
        if (!await readback) throw new InvalidDataException("The coherent logical readback differs; preserve both and do not terminate.");
        Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:O}] Current-schema installation and all-table content readback passed.");
    }

    private static SqliteConnection Connection(string path, bool readOnly) => new(new SqliteConnectionStringBuilder
    { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
    private static string Hash(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
}
