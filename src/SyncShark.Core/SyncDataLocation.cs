namespace Massdex.SyncShark;

/// <summary>The small installation-side pointer is read once per process, never from the data folder.</summary>
public static class SyncDataLocation
{
    private static readonly Lazy<string> EffectiveRoot = new(ReadProductRoot);
    private static readonly string DatabaseRelativePath = Path.Combine("vNext", "state", "syncshark.db");
    public static string PointerPath => Path.Combine(SyncSharkRuntimePaths.InstallationUserRoot, "product-root.txt");
    public static string ProductRoot => EffectiveRoot.Value;

    internal static string ReadProductRoot()
    {
        try
        {
            string[] lines;
            try { lines = File.ReadAllLines(PointerPath); }
            catch (FileNotFoundException) { return SyncSharkRuntimePaths.InstallationUserRoot; }
            catch (DirectoryNotFoundException) { return SyncSharkRuntimePaths.InstallationUserRoot; }
            if (lines.Length != 1 || string.IsNullOrWhiteSpace(lines[0]))
                throw new InvalidDataException("Expected exactly one absolute local folder path.");
            return ValidateDirectory(lines[0]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"Invalid Sync Shark data location in '{PointerPath}': {error.Message} Correct the pointer before starting Sync Shark; no fallback location was used.", error);
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
        string? temporary = null;
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
            // Flush before the atomic same-directory rename. After this point cancellation cannot roll back the pointer.
            Directory.CreateDirectory(SyncSharkRuntimePaths.InstallationUserRoot);
            temporary = PointerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(target + Environment.NewLine);
                stream.Write(bytes); stream.Flush(true);
            }
            using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(target)); stream.Flush(true);
            }
            File.Move(temporary, PointerPath, overwrite: true);
        }
        catch
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            }
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
