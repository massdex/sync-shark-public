namespace Massdex.SyncShark;

/// <summary>The small installation-side pointer is read once per process, never from the data folder.</summary>
public static partial class SyncDataLocation
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

    internal static void CheckMoveMarker(PilotStateAuthority authority)
    {
        RecoverMove(authority);
        var marker = Path.Combine(authority.ProductRoot, "MOVED-TO.txt");
        if (!Path.Exists(marker)) return;
        using var evidence = new MoveEvidence(marker);
        // A marker without its journal cannot prove ownership for deletion.
        throw EvidenceChanged();
    }

    public sealed record MoveResult(string DataLocation, bool RestartRequired, string Message,
        string? OldRoot = null, IReadOnlyList<string>? CopiedEntries = null);

    public static string CleanupOldCopy(MoveResult result)
    {
        if (!result.RestartRequired || result.OldRoot is null) return result.Message;
        try
        {
            if (!string.Equals(ReadProductRoot(), result.DataLocation, StringComparison.OrdinalIgnoreCase))
                throw new IOException(MoveText("data-move-stale-result"));
            using var owner = PilotInstanceLock.AcquireForMaintenance(Path.Combine(result.DataLocation, "state", "instance.lock"));
            if (!string.Equals(ReadProductRoot(), result.DataLocation, StringComparison.OrdinalIgnoreCase))
                throw new IOException(MoveText("data-move-stale-result"));
            return RecoverMove(owner.StateAuthority)
                ? MoveText("data-move-cleanup-complete", ("message", result.Message))
                : MoveText("data-move-no-pending-cleanup");
        }
        catch (Exception error) { return DescribeMoveFailure(result, error); }
    }

    /// <summary>Classifies a failed action without confusing a no-op or failed cleanup with pointer rollback.</summary>
    public static string DescribeMoveFailure(MoveResult? observed, Exception error)
    {
        if (observed is null) return MoveText("data-move-precommit-failed", ("reason", error.Message));
        if (!observed.RestartRequired) return MoveText("data-move-noop-finalization-failed", ("reason", error.Message));
        return MoveText("data-move-cleanup-incomplete", ("location", observed.DataLocation),
            ("oldRoot", observed.OldRoot ?? string.Empty), ("reason", error.Message));
    }

    // Caller must be a quiescent, exclusive lock owner (maintenance process or direct CLI).
    public static async Task<MoveResult> MoveAsync(PilotStateAuthority authority, string destination,
        CancellationToken cancellationToken = default)
    {
        authority.VerifyAuthority();
        RecoverMove(authority);
        var source = Path.GetFullPath(authority.ProductRoot);
        var target = ValidateDirectory(destination);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return new(target, false, MoveText("data-move-noop"));
        if (PilotPath.IsPathInside(source, target) || PilotPath.IsPathInside(target, source))
            throw new InvalidDataException("The old and new data folders must not contain one another.");
        // The default home also owns the launcher and pointer; returning data must preserve them.
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "product-root.txt", "product-root.txt.move.tmp", "data-move.json", "data-move.json.tmp", "MOVED-TO.txt", "active", "active.next", "active.previous", "versions", "updater", "discovery", "replacement-staging" };
        var existingTarget = Directory.Exists(target);
        if (existingTarget && (!string.Equals(target, SyncSharkRuntimePaths.InstallationUserRoot, StringComparison.OrdinalIgnoreCase) ||
                Directory.EnumerateFileSystemEntries(target).Any(path => !excluded.Contains(Path.GetFileName(path)))))
            throw new InvalidDataException("Choose a new folder, or the default home containing only installation files.");
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
        using var sourceChain = new PilotDirectoryLease.MigrationChain(source);
        using var targetParentChain = new PilotDirectoryLease.MigrationChain(targetParent);
        var files = new List<string>();
        var stage = Path.Combine(targetParent, ".syncshark-data-move-" + Guid.NewGuid().ToString("N"));
        void CopyDirectory(string from, string to)
        {
            if (!string.Equals(to, stage, StringComparison.OrdinalIgnoreCase))
            {
                if (Path.Exists(to)) throw new IOException("A destination entry appeared during the move: " + to);
                Directory.CreateDirectory(to);
            }
            using var sourceDirectory = PilotDirectoryLease.OpenMigrationDirectory(from);
            using var targetDirectory = PilotDirectoryLease.OpenMigrationDirectory(to);
            foreach (var entry in Directory.EnumerateFileSystemEntries(from))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, entry);
                if (installationRoot && excluded.Contains(relative)) continue;
                if (string.Equals(entry, authority.LockPath, StringComparison.OrdinalIgnoreCase)) continue;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Data migration cannot copy a reparse point: " + relative);
                var output = Path.Combine(stage, relative);
                if ((attributes & FileAttributes.Directory) != 0) CopyDirectory(entry, output);
                else
                {
                    var name = Path.GetFileName(entry);
                    if (name.Equals("syncshark.db-wal", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("syncshark.db-shm", StringComparison.OrdinalIgnoreCase)) continue;
                    using (var input = SyncLocalFileAuthority.OpenExistingReadGuard(entry))
                    using (var copy = SyncLocalFileAuthority.CreateNewUnprotectedFile(output, writeThrough: false))
                    {
                        input.VerifyMigrationFile(entry); copy.VerifyMigrationFile(output);
                        MoveCrashPointForTest?.Invoke("copy-open");
                        input.Stream.CopyTo(copy.Stream, 1024 * 1024); copy.Stream.Flush(true);
                    }
                    MoveCrashPointForTest?.Invoke("copy-file");
                    files.Add(relative);
                }
            }
        }
        var entries = Directory.EnumerateFileSystemEntries(source).Select(Path.GetFileName)
            .Where(name => name is not null && (!installationRoot || !excluded.Contains(name))).Cast<string>().ToArray();
        foreach (var entry in entries)
            if (Path.Exists(Path.Combine(target, entry)))
                throw new InvalidDataException("The destination contains an entry that would be replaced: " + entry);
        var marker = Path.Combine(source, "MOVED-TO.txt");
        string? temporary = null;
        var journal = CreateMoveJournal(source, target, stage, existingTarget, entries, authority.LockPath);
        SaveMoveJournal(journal);
        MoveCrashPointForTest?.Invoke("journal");
        try
        {
            Directory.CreateDirectory(stage);
            // The stage is held from creation through publication, so it cannot be renamed or replaced in that window.
            using var stageLease = PilotDirectoryLease.OpenMigrationDirectory(stage);
            var stageIdentity = stageLease.Identity;
            CopyDirectory(source, stage);
            if (Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Count() != files.Count)
                throw new IOException("The copied file list differs from the source.");
            var copiedDatabase = Path.Combine(stage, DatabaseRelativePath);
            if (await SyncAuthorityDatabase.VerifyDataLocationDatabaseAsync(copiedDatabase, cancellationToken) != identity)
                throw new InvalidDataException("The copied database installation identity differs from the source.");
            cancellationToken.ThrowIfCancellationRequested();
            authority.VerifyAuthority();
            VerifyMoveFiles(stage, journal.Files);
            journal = journal with { Publishing = true };
            SaveMoveJournal(journal);
            MoveCrashPointForTest?.Invoke("verified");
            Directory.CreateDirectory(target);
            using var targetChain = new PilotDirectoryLease.MigrationChain(target);
            foreach (var entry in entries)
            {
                stageLease.VerifyMigrationBinding(); targetChain.Verify();
                var from = Path.Combine(stage, entry);
                var to = Path.Combine(target, entry);
                if (Directory.Exists(from)) Directory.Move(from, to);
                else File.Move(from, to);
                MoveCrashPointForTest?.Invoke("publish-entry");
            }
            targetParentChain.Verify();
            stageLease.VerifyMigrationBinding();
            stageLease.Dispose();
            // Hand over to one delete-capable handle whose proven identity is the stage held above, never a path.
            using var stageDelete = PilotDirectoryLease.OpenMigrationDirectory(stage, delete: true);
            if (stageDelete.Identity != stageIdentity) throw EvidenceChanged();
            stageDelete.VerifyMigrationBinding();
            MoveCrashPointForTest?.Invoke("stage-delete");
            stageDelete.DeleteOnClose();
            // Flush before the atomic same-directory rename. After this point cancellation cannot roll back the pointer.
            Directory.CreateDirectory(SyncSharkRuntimePaths.InstallationUserRoot);
            temporary = PointerPath + ".move.tmp";
            using var installationChain = new PilotDirectoryLease.MigrationChain(SyncSharkRuntimePaths.InstallationUserRoot);
            using var installationDirectory = PilotDirectoryLease.OpenMigrationDirectory(SyncSharkRuntimePaths.InstallationUserRoot);
            using var pointer = SyncLocalFileAuthority.CreateNewUnprotectedFile(temporary);
            pointer.VerifyMigrationFile(temporary);
            pointer.Stream.Write(System.Text.Encoding.UTF8.GetBytes(target + Environment.NewLine));
            pointer.Stream.Flush(true);
            MoveCrashPointForTest?.Invoke("pointer-flushed");
            using var movedMarker = SyncLocalFileAuthority.CreateNewUnprotectedFile(marker);
            movedMarker.VerifyMigrationFile(marker);
            movedMarker.Stream.Write(System.Text.Encoding.UTF8.GetBytes(target));
            movedMarker.Stream.Flush(true);
            MoveCrashPointForTest?.Invoke("marker");
            installationChain.Verify(); sourceChain.Verify(); targetParentChain.Verify(); targetChain.Verify();
            pointer.VerifyMigrationFile(temporary); movedMarker.VerifyMigrationFile(marker);
            OrdinaryFile(PointerPath);
            pointer.RenameTo(installationDirectory, Path.GetFileName(PointerPath), replaceIfExists: true);
            MoveCrashPointForTest?.Invoke("pointer");
        }
        catch
        {
            // Recovery validates and retains exact evidence; the catch must never unlink a pathname blindly.
            try { RecoverMove(authority); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            // Recovery evidence remains durable whenever cleanup cannot safely finish.
            throw;
        }
        return new(target, true, MoveText("data-move-committed", ("location", target)), source, entries);
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
