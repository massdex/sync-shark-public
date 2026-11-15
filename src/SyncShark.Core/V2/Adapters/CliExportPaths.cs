namespace Massdex.SyncShark.V2;

/// <summary>Single filesystem admission owner for isolated previews, rehearsals and CSV exports.</summary>
internal static class CliExportPaths
{
    private const int MaximumPlanPathCharacters = 32_768;
    // Every folder whose redirection could reach the live product folder, plus the files that carry state.
    private static readonly string[] IsolationFolders =
        ["state", "vNext", Path.Combine("vNext", "state"), "v2"];

    private static readonly string[] IsolationFiles =
    [
        Path.Combine("v2", "state.db"),
        Path.Combine("v2", "state.db-wal"),
        Path.Combine("v2", "state.db-shm"),
        Path.Combine("vNext", "state", "syncshark.db"),
        Path.Combine("vNext", "state", "syncshark.db-wal"),
        Path.Combine("vNext", "state", "syncshark.db-shm"),
        Path.Combine("state", "instance.lock")
    ];
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint OpenExisting = 3;

    // An isolated data root is mandatory and must be its own canonical folder. The live product folder, any
    // alias (junction, symbolic link, 8.3 short name), either folder containing the other, and a state database
    // hardlinked to the live one are all refused before the runtime opens.
    internal static string ValidateIsolatedDataRoot(string requested, string? liveProductRoot = null)
    {
        if (string.IsNullOrWhiteSpace(requested) || requested.Length > MaximumPlanPathCharacters ||
            !Path.IsPathFullyQualified(requested))
            throw new SyncCliUsageException(
                "run --plan-only requires an absolute --data-root folder; the live product folder is never defaulted.");
        if (!Directory.Exists(requested))
            throw new SyncCliUsageException(
                "--data-root must name an existing isolated product folder; it is never created or defaulted.");
        var full = TrimSeparators(Path.GetFullPath(requested));
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new SyncCliUsageException(
                "--data-root must not be a reparse point, junction or symbolic link. Use a real isolated folder.");
        string canonical;
        try
        {
            canonical = CanonicalDirectory(full);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new SyncCliUsageException("--data-root could not be verified: " + error.Message);
        }
        if (!string.Equals(TrimSeparators(full), TrimSeparators(canonical), StringComparison.OrdinalIgnoreCase))
            throw new SyncCliUsageException("--data-root must be the canonical folder path; an alias such as a " +
                "junction or an 8.3 short name is not accepted.");
        var liveRoot = SafeCanonicalPath(liveProductRoot ?? SyncSharkRuntimePaths.ProductRoot);
        if (IsSameOrInside(canonical, liveRoot) || IsSameOrInside(liveRoot, canonical))
            throw new SyncCliUsageException("--data-root must be a folder separate from the live Sync Shark " +
                "product folder. Copy the product folder to an isolated location first.");
        RejectAliasedStateFolders(canonical);
        RejectSharedStateFiles(canonical, liveRoot);
        return canonical;
    }

    // A state subfolder that is a junction or symbolic link could silently redirect the lock, the v1 authority
    // database or the v2 database into the live product folder, so every state folder must resolve inside the
    // isolated root.
    private static void RejectAliasedStateFolders(string canonicalRoot)
    {
        foreach (var relative in IsolationFolders)
        {
            var path = Path.Combine(canonicalRoot, relative);
            if (!Directory.Exists(path)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new SyncCliUsageException("--data-root must not contain a redirected " + relative +
                    " folder. Copy the product folder instead of linking its subfolders.");
            string resolved;
            try
            {
                resolved = CanonicalDirectory(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new SyncCliUsageException("--data-root could not verify its " + relative + " folder: " +
                    error.Message);
            }
            if (!IsSameOrInside(resolved, canonicalRoot))
                throw new SyncCliUsageException("--data-root's " + relative + " folder resolves outside the " +
                    "isolated folder. Copy the product folder instead of linking its subfolders.");
        }
    }

    // The state files are what isolation protects: each existing isolated file must resolve inside the isolated
    // root (a symlinked database or sidecar outside is refused), and no existing file may share one identity
    // with the live product (a hardlink or alias is refused). An existing file whose identity cannot be read
    // fails closed rather than being assumed separate.
    private static void RejectSharedStateFiles(string isolatedRoot, string liveRoot)
    {
        foreach (var relative in IsolationFiles)
        {
            var isolatedPath = Path.Combine(isolatedRoot, relative);
            if (!File.Exists(isolatedPath)) continue;
            try
            {
                var canonical = CanonicalFile(isolatedPath);
                if (!IsSameOrInside(canonical, isolatedRoot))
                    throw new SyncCliUsageException("--data-root's " + relative + " resolves outside the " +
                        "isolated folder. Copy the product folder instead of linking its files.");
                var livePath = Path.Combine(liveRoot, relative);
                if (!File.Exists(livePath)) continue;
                if (FileIdentity(isolatedPath) == FileIdentity(livePath))
                    throw new SyncCliUsageException("--data-root shares the live product state file " + relative +
                        " (hardlink). Copy the product folder instead of linking its files.");
            }
            catch (SyncCliUsageException)
            {
                throw;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new SyncCliUsageException("--data-root could not be verified against the live product " +
                    "state (" + relative + "): " + error.Message + " Close any running Sync Shark process and " +
                    "retry; the preview fails closed.");
            }
        }
    }

    private static PilotStateFileIdentity FileIdentity(string path)
    {
        using var handle = PilotDirectoryLease.CreateFile(path, FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException("The state file identity could not be opened.");
        if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var information))
            throw new IOException("The state file identity could not be read.");
        return new PilotStateFileIdentity(information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow);
    }

    private static string CanonicalFile(string path)
    {
        using var handle = PilotDirectoryLease.CreateFile(path, FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("The state file could not be opened for its canonical path.");
        return PilotDirectoryLease.FinalPath(handle);
    }

    private static string CanonicalDirectory(string path)
    {
        using var handle = PilotDirectoryLease.CreateFile(path, FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OpenExisting,
            PilotDirectoryLease.FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("The folder could not be opened for its canonical path.");
        return PilotDirectoryLease.FinalPath(handle);
    }

    // Best-effort canonicalisation for a folder that may not exist (a live folder is normally present). A
    // resolution failure keeps the nominal full path, which still lets the containment checks run.
    private static string SafeCanonicalPath(string path)
    {
        var full = TrimSeparators(Path.GetFullPath(path));
        try
        {
            return Directory.Exists(full) ? TrimSeparators(CanonicalDirectory(full)) : full;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    // The export file must be absolute, sit in an existing folder, and be outside the live and isolated
    // product folders and both paired trees. No folder is created, and no paired tree is ever written.
    internal static string ValidatePlanExportPath(string exportPath, IReadOnlyList<PairDef> pairs, string isolatedRoot, string? liveProductRoot = null)
    {
        if (string.IsNullOrWhiteSpace(exportPath) || exportPath.Length > MaximumPlanPathCharacters ||
            !Path.IsPathFullyQualified(exportPath))
            throw new SyncCliUsageException(
                "--export requires an absolute CSV file path outside both folders of the pair.");
        var full = Path.GetFullPath(exportPath);
        var directory = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new SyncCliUsageException("--export must name a file in an existing folder; no folder is created.");
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new SyncCliUsageException("--export requires a valid file name.");
        // The export never replaces anything: a new file is required, so a planted file or link at the target
        // is refused instead of being overwritten.
        if (File.Exists(full) || Directory.Exists(full) || new FileInfo(full).LinkTarget is not null)
            throw new SyncCliUsageException("--export already exists; choose a new CSV file. An export " +
                "never overwrites an existing file.");
        string canonical;
        try
        {
            canonical = Path.Combine(CanonicalDirectory(directory), name);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new SyncCliUsageException("--export could not be verified: " + error.Message);
        }
        if (IsSameOrInside(canonical, SafeCanonicalPath(isolatedRoot)) ||
            IsSameOrInside(canonical, SafeCanonicalPath(liveProductRoot ?? SyncSharkRuntimePaths.ProductRoot)))
            throw new SyncCliUsageException(
                "--export must be outside the live and isolated Sync Shark data folders.");
        foreach (var root in pairs.SelectMany(pair => new[] { pair.A, pair.B }))
        {
            if (string.IsNullOrWhiteSpace(root.RootPath) || !Path.IsPathFullyQualified(root.RootPath)) continue;
            if (IsSameOrInside(canonical, SafeCanonicalPath(root.RootPath)))
                throw new SyncCliUsageException(
                    "--export must be outside both folders of the pair; choose a file outside the paired trees.");
        }
        RejectDropboxExport(canonical);
        return canonical;
    }

    private static void RejectDropboxExport(string path)
    {
        for (var folder = Directory.GetParent(path); folder is not null; folder = folder.Parent)
            if (folder.Name.Equals("Dropbox", StringComparison.OrdinalIgnoreCase) ||
                folder.Name.StartsWith("Dropbox (", StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.Combine(folder.FullName, ".dropbox")))
                throw new SyncCliUsageException("--export must be outside Dropbox folders.");
        foreach (var location in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
        {
            var info = Path.Combine(Environment.GetFolderPath(location), "Dropbox", "info.json");
            if (!File.Exists(info)) continue;
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(info));
                foreach (var account in document.RootElement.EnumerateObject())
                    if (account.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                        throw new SyncCliUsageException("Dropbox folder locations could not be verified: each account entry must be an object.");
                    else if (account.Value.TryGetProperty("path", out var root) && root.GetString() is { } desktop &&
                        IsSameOrInside(path, SafeCanonicalPath(desktop)))
                        throw new SyncCliUsageException("--export must be outside Dropbox folders.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
            { throw new SyncCliUsageException("Dropbox folder locations could not be verified: " + error.Message); }
        }
    }

    // An isolated data folder must not sit inside a paired tree, where the engine's own state files would be
    // scanned as pair content. Both sides are checked; a remote path is not a local folder and is skipped.
    internal static void RejectDataRootInsidePair(string productRoot, PairDef pair)
    {
        var isolated = SafeCanonicalPath(productRoot);
        foreach (var endpoint in new[] { pair.A, pair.B })
        {
            if (string.IsNullOrWhiteSpace(endpoint.RootPath) ||
                !Path.IsPathFullyQualified(endpoint.RootPath)) continue;
            if (IsSameOrInside(isolated, SafeCanonicalPath(endpoint.RootPath)))
                throw new SyncCliUsageException("--data-root must be outside both folders of the pair; choose an " +
                    "isolated copy outside the paired trees.");
        }
    }

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsSameOrInside(string path, string root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var candidate = TrimSeparators(path);
        var container = TrimSeparators(root);
        if (string.Equals(candidate, container, StringComparison.OrdinalIgnoreCase)) return true;
        return candidate.StartsWith(container + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

}
