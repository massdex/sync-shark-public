using Massdex.SyncShark.V2;
using Microsoft.Win32;

namespace Massdex.SyncShark;

/// <summary>Resolves the registered data folder once per process, importing the legacy pointer when needed.</summary>
public static class SyncDataLocation
{
    private static readonly Lazy<string> EffectiveRoot = new(ReadProductRoot);
    private static readonly string DatabaseRelativePath = Path.Combine("vNext", "state", "syncshark.db");
    public const string EngineFolder = "engine";
    public const string EngineDatabaseFile = "state.db";
    public const string StoreAbsent = "absent";
    public const string StoreValid = "valid";
    public const string StoreInvalid = "invalid";
    public static string EngineDatabasePath(string root) => Path.Combine(root, EngineFolder, EngineDatabaseFile);
    private const string RegistryPath = @"Software\Massdex\SyncShark";
    private const string ValueName = "DataLocation";
    internal static string? RegistryPathForTest { get; set; }
    internal static string? DefaultRootForTest { get; set; }
    private static string DefaultRoot => DefaultRootForTest ?? SyncSharkRuntimePaths.InstallationUserRoot;
    public static string PointerPath => Path.Combine(DefaultRoot, "product-root.txt");
    // A test run's installed override always wins; only the real product root reads the registry/pointer.
    public static string ProductRoot => SyncSharkRuntimePaths.TestProductRoot ?? EffectiveRoot.Value;

    internal static string ReadProductRoot()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string? configured = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPathForTest ?? RegistryPath);
            var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            configured = value?.ToString();
            if (value is not null)
            {
                if (value is not string || key!.GetValueKind(ValueName) != RegistryValueKind.String)
                    throw new InvalidDataException(EdgeMessages.Text("data-location-value"));
                return ValidateExistingStore(configured!);
            }
            string[]? lines = null;
            try { lines = File.ReadAllLines(PointerPath); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            configured = lines is null ? DefaultRoot : string.Join(Environment.NewLine, lines);
            if (lines is not null && (lines.Length != 1 || string.IsNullOrWhiteSpace(lines[0])))
                throw new InvalidDataException(EdgeMessages.Text("data-location-value"));
            var root = ValidateExistingStore(configured);
            WriteProductRoot(root);
            return root;
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            throw new InvalidDataException(EdgeMessages.Text("data-location-invalid",
                $"{configured ?? DefaultRoot}: {error.Message}"), error);
        }
    }

    private static string ValidateExistingStore(string path)
    {
        var root = ValidateDirectory(path);
        using var store = OpenStore(root);
        if (!HasStoreHeader(store))
            throw new InvalidDataException(EdgeMessages.Text("data-location-store-missing"));
        return root;
    }

    /// <summary>Setup's one answer for store presence: absent, a valid store, or an unusable file.</summary>
    public static string StoreState(string path)
    {
        var root = ValidateDirectory(path);
        try
        {
            using var store = OpenStore(root);
            return HasStoreHeader(store) ? StoreValid : StoreInvalid;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return StoreAbsent; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return StoreInvalid; }
    }

    private static FileStream OpenStore(string root) => new(EngineDatabasePath(root), FileMode.Open,
        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static bool HasStoreHeader(FileStream store)
    {
        ReadOnlySpan<byte> signature = "SQLite format 3\0"u8;
        Span<byte> header = stackalloc byte[signature.Length];
        store.ReadExactly(header);
        return header.SequenceEqual(signature);
    }

    private static void WriteProductRoot(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPathForTest ?? RegistryPath, writable: true);
        key.SetValue(ValueName, root, RegistryValueKind.String);
    }

    /// <summary>Explicit setup-only initialization; normal startup never creates a missing store.</summary>
    public static void InitializeNewStore(string root, Action<string> initialize)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ArgumentNullException.ThrowIfNull(initialize);
        root = ValidateDirectory(root);
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPathForTest ?? RegistryPath, writable: true);
        if (key.GetValue(ValueName) is not null)
            throw new InvalidDataException(EdgeMessages.Text("data-location-already-configured"));
        string? legacy = null;
        try
        {
            var lines = File.ReadAllLines(PointerPath);
            if (lines.Length != 1) throw new InvalidDataException(EdgeMessages.Text("data-location-value"));
            legacy = ValidateExistingStore(lines[0]);
        }
        catch (FileNotFoundException) when (!File.Exists(PointerPath)) { }
        catch (DirectoryNotFoundException) when (!File.Exists(PointerPath)) { }
        catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine(EdgeMessages.Text("data-location-stale-pointer", PointerPath + ": " + error.Message));
        }
        if (legacy is not null)
        {
            WriteProductRoot(legacy);
            return;
        }
        using var authority = PilotInstanceLock.Acquire(Path.Combine(root, "state", SyncSharkRuntimePaths.InstanceLockFileName));
        var database = EngineDatabasePath(root);
        string[] files = [database, database + "-wal", database + "-shm", database + "-journal"];
        if (key.GetValue(ValueName) is not null)
            throw new InvalidDataException(EdgeMessages.Text("data-location-already-configured"));
        // A kill between CreateNew and cleanup leaves only empty files; reclaim those and never any bytes.
        var leftovers = files.Where(File.Exists).ToArray();
        if (leftovers.Any(file => new FileInfo(file).Length != 0))
            throw new InvalidDataException(EdgeMessages.Text("data-location-orphan-store",
                string.Join(", ", leftovers)));
        foreach (var file in leftovers) File.Delete(file);
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        // CreateNew establishes ownership before the initializer can write any database bytes.
        using (new FileStream(database, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        try
        {
            initialize(root);
            WriteProductRoot(ValidateExistingStore(root));
        }
        catch
        {
            foreach (var file in files)
            {
                try { File.Delete(file); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                { Console.Error.WriteLine(EdgeMessages.Text("data-location-cleanup", file + ": " + cleanup.Message)); }
            }
            throw;
        }
    }

    internal static void CheckMoveMarker(string root)
    {
        var marker = Path.Combine(root, "MOVED-TO.txt");
        if (!File.Exists(marker)) return;
        var destination = File.ReadAllText(marker).Trim();
        if (string.Equals(ReadProductRoot(), destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This data folder has moved to " + destination + ". Restart Sync Shark.");
        File.Delete(marker);
        Console.Error.WriteLine("Previous data move did not complete; continuing on " + root);
    }

    public sealed record MoveResult(string DataLocation, bool RestartRequired, string Message,
        string? OldRoot = null, IReadOnlyList<string>? CopiedEntries = null);

    public static string CleanupOldCopy(MoveResult result)
    {
        if (!result.RestartRequired || result.OldRoot is null) return result.Message;
        try
        {
            foreach (var entry in result.CopiedEntries ?? [])
            {
                var path = Path.Combine(result.OldRoot, entry);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            return result.Message + " Old runtime data removed.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return result.Message + " Old copy not removed at " + result.OldRoot + ": " + error.Message; }
    }

    // Caller must be a quiescent, exclusive lock owner (maintenance process or direct CLI).
    public static async Task<MoveResult> MoveAsync(PilotStateAuthority authority, string destination,
        CancellationToken cancellationToken = default)
    {
        authority.VerifyAuthority();
        var source = Path.GetFullPath(authority.ProductRoot);
        var target = ValidateDirectory(destination);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return new(target, false, "The data is already at this location.");
        if (PilotPath.IsPathInside(source, target) || PilotPath.IsPathInside(target, source))
            throw new InvalidDataException("The old and new data folders must not contain one another.");
        if (Directory.Exists(target))
            throw new InvalidDataException("Choose a new folder that doesn't exist yet; Sync Shark creates it.");
        var targetParent = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(targetParent) || !Directory.Exists(targetParent))
            throw new InvalidDataException("The parent folder of the new data location must already exist.");
        var product = new SyncProductStateStore(authority).Load();
        SyncProductStateStore.EnsureConfigurationMutable(product);
        var database = Path.Combine(source, DatabaseRelativePath);
        await ValidateConfiguredPairsAsync(authority, target, cancellationToken);
        var installationRoot = string.Equals(source, SyncSharkRuntimePaths.InstallationUserRoot, StringComparison.OrdinalIgnoreCase);
        var staging = Path.Combine(source, SyncSharkRuntimePaths.ReplacementStagingDirectoryName);
        if (Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging)
                .Any(path => !installationRoot || Path.GetFileName(path) != "transactions"))
            throw new InvalidOperationException("Finish or resolve pending work first: retained replacement or recovery staging prevents moving the data location.");
        var identity = await SyncAuthorityDatabase.PrepareDataLocationDatabaseAsync(database, cancellationToken);
        // Never traverse the installed launcher's junction or move installer-owned material.
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "product-root.txt", "MOVED-TO.txt", "active", "active.next", "active.previous", "versions", "updater", "discovery", "replacement-staging" };
        var files = new List<string>();
        void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var entry in Directory.EnumerateFileSystemEntries(from))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, entry);
                if (installationRoot && excluded.Contains(relative)) continue;
                if (string.Equals(entry, authority.LockPath, StringComparison.OrdinalIgnoreCase)) continue;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Data migration cannot copy a reparse point: " + relative);
                var output = Path.Combine(target, relative);
                if ((attributes & FileAttributes.Directory) != 0) CopyDirectory(entry, output);
                else
                {
                    var name = Path.GetFileName(entry);
                    if (name.Equals("syncshark.db-wal", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("syncshark.db-shm", StringComparison.OrdinalIgnoreCase)) continue;
                    using (var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var copy = new FileStream(output, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    { input.CopyTo(copy); copy.Flush(true); }
                    files.Add(relative);
                }
            }
        }
        void DeleteRecursivelyBestEffort(string path)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(path))
                        DeleteRecursivelyBestEffort(child);
                }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
                try { Directory.Delete(path); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            }
            else
            {
                try { File.Delete(path); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            }
        }
        var entries = Directory.EnumerateFileSystemEntries(source).Select(Path.GetFileName)
            .Where(name => name is not null && (!installationRoot || !excluded.Contains(name))).Cast<string>().ToArray();
        var marker = Path.Combine(source, "MOVED-TO.txt");
        var createdTarget = false;
        try
        {
            Directory.CreateDirectory(target);
            createdTarget = true;
            CopyDirectory(source, target);
            if (Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Count() != files.Count)
                throw new IOException("The copied file list differs from the source.");
            var copiedDatabase = Path.Combine(target, DatabaseRelativePath);
            if (await SyncAuthorityDatabase.VerifyDataLocationDatabaseAsync(copiedDatabase, cancellationToken) != identity)
                throw new InvalidDataException("The copied database installation identity differs from the source.");
            foreach (var relative in files)
            {
                using var original = File.OpenRead(Path.Combine(source, relative));
                using var copy = File.OpenRead(Path.Combine(target, relative));
                if (original.Length != copy.Length || !System.Security.Cryptography.SHA256.HashData(original)
                        .SequenceEqual(System.Security.Cryptography.SHA256.HashData(copy)))
                    throw new IOException("Data copy verification failed for " + relative);
            }
            cancellationToken.ThrowIfCancellationRequested();
            authority.VerifyAuthority();
            // Publish only after the verified copy and durable move marker; retain the legacy pointer.
            using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(target)); stream.Flush(true);
            }
            WriteProductRoot(target);
        }
        catch
        {
            try { if (File.Exists(marker)) File.Delete(marker); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            if (createdTarget) DeleteRecursivelyBestEffort(target);
            throw;
        }
        return new(target, true, $"Data moved to {target}. Restart required; rerun the CLI after it exits.", source, entries);
    }

    internal static async Task ValidateConfiguredPairsAsync(PilotStateAuthority authority, string target,
        CancellationToken cancellationToken = default)
    {
        var pairs = new SyncPairStore(authority).Load().Pairs.AsEnumerable();
        var database = Path.Combine(authority.ProductRoot, DatabaseRelativePath);
        if (File.Exists(database))
            pairs = pairs.Concat((await new SyncAuthorityDatabase(database).LoadPairSetAsync(cancellationToken, readOnly: true)).Pairs);
        foreach (var pair in pairs)
        {
            if (!string.IsNullOrWhiteSpace(pair.LocalRoot)) PilotPath.RequireSeparatedFromProductState(pair.LocalRoot, target);
            foreach (var endpoint in new[] { pair.LeftEndpoint, pair.RightEndpoint })
                if (endpoint?.IsLocal == true && !string.IsNullOrWhiteSpace(endpoint.LocalPath))
                    PilotPath.RequireSeparatedFromProductState(endpoint.LocalPath, target);
        }
    }

    // Validation is read-only; the existing state-authority acquisition creates missing directories.
    public static string ValidateDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' || path[2] is not ('\\' or '/'))
            throw new InvalidDataException("The data location must be an absolute local drive folder, not a relative, UNC or device path.");
        var full = PilotPath.NormalizeLocalRoot(path);
        var driveType = new DriveInfo(Path.GetPathRoot(full)!).DriveType;
        if (driveType is not (DriveType.Fixed or DriveType.Removable))
            throw new InvalidDataException("The data location must be on a local drive.");
        if (driveType == DriveType.Removable)
            Console.Error.WriteLine("Sync Shark: warning: the data location is on a removable drive. If it is disconnected, Sync Shark stops and will not start until it is reconnected.");
        for (var directory = new DirectoryInfo(full); directory is not null; directory = directory.Parent)
        {
            try
            {
                var attributes = File.GetAttributes(directory.FullName);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The data location and its ancestors must not be reparse points.");
                if ((attributes & FileAttributes.Directory) == 0)
                    throw new InvalidDataException("The data location must be a directory.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }
}
