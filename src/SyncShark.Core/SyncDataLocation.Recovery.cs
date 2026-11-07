using System.Security.Cryptography;
using System.Text.Json;

namespace Massdex.SyncShark;

// One durable move journal: the move's manifest, its saved record and the recovery and deletion steps all go through the one journal; each step takes its own scoped holds and revalidates the objects it preserves or deletes against that manifest.
public static partial class SyncDataLocation
{
    internal static Action<string>? MoveCrashPointForTest;
    private static string MoveJournalPath => Path.Combine(SyncSharkRuntimePaths.InstallationUserRoot, "data-move.json");
    private sealed record MoveFile(string Path, string Sha256);
    private sealed record MoveJournal(string Source, string Target, string Stage, bool ExistingTarget,
        string[] Entries, string[] Directories, MoveFile[] Files, bool Publishing = false);

    internal static string MoveText(string key, params (string Name, string Value)[] values) =>
        CliCatalogResource.Fill(V2.MessageCatalog.Default.Message(key), values);

    private static IOException EvidenceChanged() => new(MoveText("data-move-evidence-changed"));

    private static MoveJournal CreateMoveJournal(string source, string target, string stage, bool existingTarget,
        string[] entries, string lockPath)
    {
        var files = new List<MoveFile>();
        var directories = new List<string>();
        using var sourceChain = new PilotDirectoryLease.MigrationChain(source);
        void Visit(string path)
        {
            if (string.Equals(path, lockPath, StringComparison.OrdinalIgnoreCase)) return;
            var relative = Path.GetRelativePath(source, path);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw EvidenceChanged();
            if ((attributes & FileAttributes.Directory) != 0)
            {
                using var directory = PilotDirectoryLease.OpenMigrationDirectory(path);
                directories.Add(relative);
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) Visit(child);
            }
            else if (!Path.GetFileName(path).Equals("syncshark.db-wal", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFileName(path).Equals("syncshark.db-shm", StringComparison.OrdinalIgnoreCase))
            {
                using var input = SyncLocalFileAuthority.OpenExistingReadGuard(path);
                input.VerifyMigrationFile(path);
                files.Add(new(relative, Hash(input)));
            }
        }
        foreach (var entry in entries) Visit(Path.Combine(source, entry));
        return new(source, target, stage, existingTarget, entries, directories.ToArray(), files.ToArray());
    }

    private static string Hash(SyncLocalFileAuthority file)
    {
        file.Stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(file.Stream));
    }

    private static void OrdinaryFile(string path)
    {
        if (!Path.Exists(path)) return;
        using var parents = new PilotDirectoryLease.MigrationChain(Path.GetDirectoryName(path)!);
        using var file = SyncLocalFileAuthority.OpenExistingReadGuard(path);
        file.VerifyMigrationFile(path);
    }

    private static void SaveMoveJournal(MoveJournal journal)
    {
        Directory.CreateDirectory(SyncSharkRuntimePaths.InstallationUserRoot);
        using var parents = new PilotDirectoryLease.MigrationChain(SyncSharkRuntimePaths.InstallationUserRoot);
        // The publishing journal is a second durable preimage, not an overwrite of recovery evidence.
        using var directory = PilotDirectoryLease.OpenMigrationDirectory(SyncSharkRuntimePaths.InstallationUserRoot);
        using var file = SyncLocalFileAuthority.CreateNewUnprotectedFile(MoveJournalPath + ".tmp");
        file.VerifyMigrationFile(MoveJournalPath + ".tmp");
        JsonSerializer.Serialize(file.Stream, journal);
        file.Stream.Flush(true);
        MoveCrashPointForTest?.Invoke(journal.Publishing ? "publish-journal-flushed" : "journal-flushed");
        parents.Verify();
        file.VerifyMigrationFile(MoveJournalPath + ".tmp");
        if (!journal.Publishing) file.RenameTo(directory, Path.GetFileName(MoveJournalPath));
    }

    // Keeps the read preimage immutable until its exact object is consumed, including at test gates.
    private sealed class MoveEvidence : IDisposable
    {
        private readonly string _path;
        private readonly PilotDirectoryLease.MigrationChain _parents;
        private readonly SyncLocalFileAuthority _file;
        private readonly PilotDirectoryIdentity _identity;
        private readonly string _hash;

        internal MoveEvidence(string path)
        {
            _path = path;
            _parents = new(Path.GetDirectoryName(path)!);
            try
            {
                _file = SyncLocalFileAuthority.OpenExistingExclusive(path);
                try
                {
                    _identity = _file.VerifyMigrationFile(path);
                    _hash = Hash(_file);
                }
                catch { _file.Dispose(); throw; }
            }
            catch { _parents.Dispose(); throw; }
        }

        internal string Text()
        {
            _file.Stream.Position = 0;
            using var reader = new StreamReader(_file.Stream, leaveOpen: true);
            return reader.ReadToEnd();
        }

        internal void Verify()
        {
            _parents.Verify();
            if (_file.VerifyMigrationFile(_path) != _identity || Hash(_file) != _hash) throw EvidenceChanged();
        }

        internal void Delete()
        {
            MoveCrashPointForTest?.Invoke("cleanup-evidence");
            Verify();
            _file.DeleteOnClose();
        }

        public void Dispose()
        {
            try { _file.Dispose(); }
            finally { _parents.Dispose(); }
        }
    }

    private static void VerifyMoveFiles(string root, IEnumerable<MoveFile> files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(root, file.Path);
            using var parents = new PilotDirectoryLease.MigrationChain(Path.GetDirectoryName(path)!);
            using var input = SyncLocalFileAuthority.OpenExistingReadGuard(path);
            input.VerifyMigrationFile(path);
            VerifyHash(input, file);
        }
    }

    private static void VerifyHash(SyncLocalFileAuthority input, MoveFile file)
    {
        if (Hash(input) != file.Sha256)
            throw new IOException(MoveText("data-move-manifest-differs", ("path", file.Path)));
    }

    private static void VerifyMoveDirectories(string root, MoveJournal journal)
    {
        foreach (var relative in journal.Directories)
        {
            using var chain = new PilotDirectoryLease.MigrationChain(Path.Combine(root, relative));
        }
    }

    private static MoveEvidence? EvidenceIfPresent(string path) => Path.Exists(path) ? new(path) : null;

    private static void DeleteEvidence(string path, MoveEvidence? evidence)
    {
        if (evidence is null)
        {
            if (Path.Exists(path)) throw EvidenceChanged();
            return;
        }
        evidence.Delete();
    }

    // The authority is re-verified after cleanup, and each journal evidence is deleted through the handle that verified it; the initial preimage is closed before the temporary journal is deleted, so a surviving publishing temporary can replay independently.
    private static bool RecoverMove(PilotStateAuthority authority)
    {
        authority.VerifyAuthority();
        var initialTemporary = !Path.Exists(MoveJournalPath);
        var journalPath = initialTemporary ? MoveJournalPath + ".tmp" : MoveJournalPath;
        if (!Path.Exists(journalPath)) return false;
        using var journalEvidence = new MoveEvidence(journalPath);
        var journal = JsonSerializer.Deserialize<MoveJournal>(journalEvidence.Text()) ?? throw EvidenceChanged();
        if (journal.Entries is null || journal.Directories is null || journal.Files is null ||
            journal.Files.Any(file => file is null || string.IsNullOrEmpty(file.Path) || string.IsNullOrEmpty(file.Sha256)) ||
            journal.Entries.Concat(journal.Directories).Any(string.IsNullOrEmpty)) throw EvidenceChanged();
        ValidateDirectory(journal.Source); ValidateDirectory(journal.Target); ValidateDirectory(journal.Stage);
        if (PilotPath.IsPathInside(journal.Source, journal.Target) || PilotPath.IsPathInside(journal.Target, journal.Source) ||
            string.Equals(journal.Source, journal.Target, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(journal.Stage), Path.GetDirectoryName(journal.Target), StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(journal.Stage), "^\\.syncshark-data-move-[0-9a-f]{32}$"))
            throw EvidenceChanged();
        foreach (var relative in journal.Entries.Concat(journal.Directories).Concat(journal.Files.Select(file => file.Path)))
            if (Path.IsPathRooted(relative) || relative.Split('\\', '/').Any(part => part is "" or "." or "..") || relative.Contains(':'))
                throw EvidenceChanged();
        foreach (var entry in journal.Entries)
            if (entry.Contains('\\') || entry.Contains('/') || entry == "MOVED-TO.txt" ||
                ((string.Equals(journal.Source, SyncSharkRuntimePaths.InstallationUserRoot, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(journal.Target, SyncSharkRuntimePaths.InstallationUserRoot, StringComparison.OrdinalIgnoreCase)) &&
                 entry is "product-root.txt" or "data-move.json" or "data-move.json.tmp" or "product-root.txt.move.tmp"))
                throw EvidenceChanged();
        var effective = ReadProductRoot();
        var committed = string.Equals(effective, journal.Target, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(authority.ProductRoot, committed ? journal.Target : journal.Source, StringComparison.OrdinalIgnoreCase) ||
            (!committed && !string.Equals(effective, journal.Source, StringComparison.OrdinalIgnoreCase)))
            throw EvidenceChanged();
        using var pointerTemp = EvidenceIfPresent(PointerPath + ".move.tmp");
        using var journalTemp = initialTemporary ? null : EvidenceIfPresent(MoveJournalPath + ".tmp");
        using var marker = EvidenceIfPresent(Path.Combine(journal.Source, "MOVED-TO.txt"));
        if (pointerTemp is not null && pointerTemp.Text() != journal.Target + Environment.NewLine) throw EvidenceChanged();
        if (marker is not null && marker.Text() != journal.Target) throw EvidenceChanged();
        if (journalTemp is not null)
        {
            var pending = JsonSerializer.Deserialize<MoveJournal>(journalTemp.Text()) ?? throw EvidenceChanged();
            if (JsonSerializer.Serialize(pending with { Publishing = journal.Publishing }) != JsonSerializer.Serialize(journal) ||
                !pending.Publishing || journal.Publishing) throw EvidenceChanged();
            journal = pending;
        }
        if (initialTemporary && !journal.Publishing && (committed || Directory.Exists(journal.Stage) ||
            pointerTemp is not null || marker is not null)) throw EvidenceChanged();
        if (committed)
        {
            if (!journal.Publishing) throw EvidenceChanged();
            VerifyMoveDirectories(journal.Target, journal);
            VerifyMoveFiles(journal.Target, journal.Files);
            var lockPath = Path.Combine(journal.Source, "state", "instance.lock");
            using (var exclusion = EvidenceIfPresent(lockPath))
            {
                RemoveManifestCopy(journal.Source, journal, verify: true, keepLock: true, authoritative: journal.Target);
                // The excluded old lock is deleted through the SAME retained handle, without a handoff gap.
                DeleteEvidence(lockPath, exclusion);
            }
            MoveCrashPointForTest?.Invoke("cleanup-lock");
            var state = Path.GetDirectoryName(lockPath)!;
            if (Directory.Exists(state) && !Directory.EnumerateFileSystemEntries(state).Any())
                DeleteMoveDirectory(state, Path.Combine(journal.Target, "state"));
        }
        else
        {
            if (journal.Publishing && Directory.Exists(journal.Target))
                RemoveManifestCopy(journal.Target, journal, verify: true, keepLock: false, authoritative: journal.Source);
            if (Directory.Exists(journal.Stage))
            {
                RemoveManifestCopy(journal.Stage, journal, verify: journal.Publishing, keepLock: false, authoritative: journal.Source);
                DeleteMoveDirectory(journal.Stage, journal.Source);
            }
            if (!journal.ExistingTarget && journal.Publishing && Directory.Exists(journal.Target))
                DeleteMoveDirectory(journal.Target, journal.Source);
        }
        authority.VerifyAuthority();
        if (!string.Equals(ReadProductRoot(), effective, StringComparison.OrdinalIgnoreCase)) throw EvidenceChanged();
        MoveCrashPointForTest?.Invoke("cleanup-before-evidence");
        journalEvidence.Verify();
        marker?.Verify();
        DeleteEvidence(Path.Combine(journal.Source, "MOVED-TO.txt"), marker);
        DeleteEvidence(PointerPath + ".move.tmp", pointerTemp);
        MoveCrashPointForTest?.Invoke("cleanup-journal");
        journalEvidence.Delete();
        // Close the initial preimage first: a surviving publishing temporary can replay independently.
        journalEvidence.Dispose();
        MoveCrashPointForTest?.Invoke("cleanup-initial-consumed");
        if (!initialTemporary) DeleteEvidence(MoveJournalPath + ".tmp", journalTemp);
        return true;
    }

    private static void DeleteMoveDirectory(string path, string authoritative)
    {
        using var target = new PilotDirectoryLease.MigrationChain(authoritative, denyWrite: true);
        using var parents = new PilotDirectoryLease.MigrationChain(Path.GetDirectoryName(path)!);
        using var directory = PilotDirectoryLease.OpenMigrationDirectory(path, delete: true);
        MoveCrashPointForTest?.Invoke("cleanup-directory");
        target.Verify(); parents.Verify(); directory.VerifyMigrationBinding();
        directory.DeleteOnClose();
    }

    // One manifest and journal drive both the preflight and the verified deletes; each delete reacquires the preserved and removed objects' guards and re-verifies both against that manifest immediately before deleting.
    private static void RemoveManifestCopy(string root, MoveJournal journal, bool verify, bool keepLock, string authoritative)
    {
        var files = journal.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var directories = journal.Directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        void Inspect(string path)
        {
            var relative = Path.GetRelativePath(root, path);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw EvidenceChanged();
            if (keepLock && relative.Equals(Path.Combine("state", "instance.lock"), StringComparison.OrdinalIgnoreCase)) return;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                using var directory = PilotDirectoryLease.OpenMigrationDirectory(path);
                if (!directories.Contains(relative)) throw EvidenceChanged();
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) Inspect(child);
            }
            else
            {
                if (!files.TryGetValue(relative, out var file)) throw EvidenceChanged();
                using var input = SyncLocalFileAuthority.OpenExistingReadGuard(path);
                input.VerifyMigrationFile(path);
                if (verify) VerifyHash(input, file);
            }
        }
        using (var rootChain = new PilotDirectoryLease.MigrationChain(root))
        {
            foreach (var entry in journal.Entries)
            {
                var path = Path.Combine(root, entry);
                if (Path.Exists(path)) Inspect(path);
            }
        }
        MoveCrashPointForTest?.Invoke("cleanup-preflight");
        foreach (var file in journal.Files)
        {
            var path = Path.Combine(root, file.Path);
            if (Path.Exists(path))
            {
                var preservedPath = Path.Combine(authoritative, file.Path);
                using var preservedParents = new PilotDirectoryLease.MigrationChain(Path.GetDirectoryName(preservedPath)!, denyWrite: true);
                using var preserved = SyncLocalFileAuthority.OpenExistingReadGuard(preservedPath);
                preserved.VerifyMigrationFile(preservedPath);
                VerifyHash(preserved, file);
                using var parents = new PilotDirectoryLease.MigrationChain(Path.GetDirectoryName(path)!);
                using var removed = SyncLocalFileAuthority.OpenExistingExclusive(path);
                removed.VerifyMigrationFile(path);
                if (verify) VerifyHash(removed, file);
                MoveCrashPointForTest?.Invoke("cleanup-before-file");
                preservedParents.Verify(); parents.Verify();
                preserved.VerifyMigrationFile(preservedPath); removed.VerifyMigrationFile(path);
                removed.DeleteOnClose();
            }
            MoveCrashPointForTest?.Invoke("cleanup-file");
        }
        foreach (var relative in journal.Directories.OrderByDescending(path => path.Length))
        {
            var path = Path.Combine(root, relative);
            if (Directory.Exists(path) && !(keepLock && relative.Equals("state", StringComparison.OrdinalIgnoreCase)))
                DeleteMoveDirectory(path, Path.Combine(authoritative, relative));
        }
    }
}
