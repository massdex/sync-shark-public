using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class PilotReleaseRecoveryRequiredException : IOException
{
    internal PilotReleaseRecoveryRequiredException(string transaction, string detail, Exception? inner = null)
        : base($"Sync Shark installation recovery is required: {detail} Resume the retained installer or rollback for {transaction} before opening the app. No normal state writes were admitted.", inner) { }
}

internal static class PilotReleaseRecovery
{
    internal static void RequireCurrentBuildReady(string productRoot)
        => RequireBuildReady(productRoot, BuildIdentity.InformationalVersion, BuildIdentity.SourceRevision);

    internal static void RequireBuildReady(string productRoot, string informationalVersion, string revision)
    {
        // Development fixtures have no installed release identity. Never select a
        // historical transaction by directory age or by a guessed latest version.
        if (revision == "UNBOUND") return;
        var identity = informationalVersion.Split('+', 2);
        if (revision.Length != 40 || !revision.All(Uri.IsHexDigit) || identity.Length != 2 ||
            !identity[1].Equals(revision, StringComparison.OrdinalIgnoreCase))
            throw new PilotReleaseRecoveryRequiredException(productRoot,
                "the current release identity cannot be bound before opening product state.");
        RequireReady(productRoot, identity[0], revision.ToLowerInvariant());
    }

    internal static void RequireReady(string productRoot, string version, string revision)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+$") ||
            revision.Length != 40 || !revision.All(Uri.IsHexDigit))
            throw new ArgumentException("An exact release version and revision are required.");
        var root = Path.GetFullPath(productRoot);
        var transaction = Path.Combine(root, "replacement-staging", "transactions", version + "-" + revision[..7]);
        var guards = new List<PilotDirectoryLease>();
        try
        {
            // Only this runtime's transaction is relevant. Superseded incomplete
            // journals remain evidence and must not disable a later complete release.
            foreach (var path in new[] { root, Path.Combine(root, "replacement-staging"),
                         Path.Combine(root, "replacement-staging", "transactions"), transaction })
            {
                try { _ = File.GetAttributes(path); }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
                guards.Add(PilotDirectoryLease.OpenProductDirectoryReadGuard(path));
            }
            foreach (var operation in new[] { "install", "rollback" })
            {
                var path = Path.Combine(transaction, operation + "-journal.json");
                if (Exists(path + ".new"))
                    throw new PilotReleaseRecoveryRequiredException(transaction, $"an interrupted {operation} journal write is retained.");
                using var stream = OpenJournal(path);
                if (stream is null) continue;
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 20 });
                var journal = document.RootElement;
                if (!journal.GetProperty("schema").TryGetInt32(out var schema) || schema != 1 ||
                    journal.GetProperty("operation").GetString() != operation ||
                    journal.GetProperty("version").GetString() != version ||
                    journal.GetProperty("commit").GetString() != revision ||
                    !Path.GetFullPath(journal.GetProperty("product_root").GetString()!).Equals(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The retained transaction identity is contradictory.");
                var status = journal.GetProperty("status").GetString();
                if (status == "active")
                    throw new PilotReleaseRecoveryRequiredException(transaction,
                        $"{operation} is unfinished at {journal.GetProperty("pending").GetString() ?? "a retained phase boundary"}.");
                // A completed rollback belongs to the removed successor. Its
                // versioned executable must not reopen the restored predecessor state.
                if (operation == "rollback")
                    throw new PilotReleaseRecoveryRequiredException(transaction,
                        "this runtime was rolled back; use the active predecessor instead.");
                var allowed = journal.GetProperty("allowed_steps").EnumerateArray().Select(step => step.GetString()).ToArray();
                var completed = journal.GetProperty("completed").EnumerateArray().Select(step => step.GetString()).ToArray();
                string[] required = ["stage-package", "publish-runtime", "backup-state", "create-next-link",
                    "retain-predecessor", "publish-successor", "register-explorer-package", "verify-complete"];
                if (status != "complete" || journal.GetProperty("pending").ValueKind != JsonValueKind.Null ||
                    !allowed.SequenceEqual(required, StringComparer.Ordinal) ||
                    !allowed.SequenceEqual(completed, StringComparer.Ordinal))
                    throw new InvalidDataException("The retained transaction is not a consistent complete boundary.");
            }
            foreach (var guard in guards) guard.VerifyBinding();
        }
        catch (PilotReleaseRecoveryRequiredException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
                                      KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw new PilotReleaseRecoveryRequiredException(transaction, "its retained journal or physical inputs could not be verified.", error); }
        finally { for (var index = guards.Count - 1; index >= 0; index--) guards[index].Dispose(); }
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static FileStream? OpenJournal(string path)
    {
        var handle = PilotDirectoryLease.CreateFile(path, 0x80000000, FileShare.Read, IntPtr.Zero,
            PilotDirectoryLease.OpenExisting, PilotDirectoryLease.FileFlagOpenReparsePoint | 0x00100000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is 2 or 3) return null;
            throw new IOException("The release journal could not be held unchanged.", new System.ComponentModel.Win32Exception(error));
        }
        try
        {
            if (!PilotDirectoryLease.GetFileInformationByHandle(handle, out var information) ||
                (information.FileAttributes & FileAttributes.Directory) != 0 || PilotPath.IsUnsafeAttributes(information.FileAttributes) ||
                information.NumberOfLinks != 1 || information.FileSizeHigh != 0 || information.FileSizeLow is 0 or > 65536 ||
                !PilotDirectoryLease.FinalPath(handle).Equals(path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The release journal is redirected, aliased, oversized or changed type.");
            return new FileStream(handle, FileAccess.Read, 4096, false);
        }
        catch { handle.Dispose(); throw; }
    }
}
