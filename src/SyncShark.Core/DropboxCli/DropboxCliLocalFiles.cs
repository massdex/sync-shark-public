using System.Text;

namespace Massdex.SyncShark;

/// <summary>All local file effects for the Dropbox CLI: safe input opens and atomic unpredictable output.</summary>
public static class DropboxCliLocalFiles
{
    /// <summary>Opens --in for reading: a regular, seekable file that is not a reparse point or placeholder.</summary>
    public static FileStream OpenInput(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.InFileMissing, ("path", path)));
        if (PilotPath.IsUnsafeAttributes(file.Attributes))
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.InFileUnsafe, ("path", path)));
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>
    /// Checks --out names a file (never an existing folder, even with --yes) inside an existing folder and,
    /// without --yes, that the destination will not be replaced.
    /// </summary>
    public static void PrepareOutput(string outPath, bool yes)
    {
        var folder = Path.GetDirectoryName(outPath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.OutFolderMissing,
                ("path", outPath)));
        if (Directory.Exists(outPath))
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.OutIsFolder,
                ("path", outPath)));
        if (File.Exists(outPath) && !yes)
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.OutExists, ("path", outPath)));
    }

    /// <summary>
    /// Creates a new, unpredictable temporary file beside --out. An existing destination is never opened or
    /// truncated; the completed bytes are moved over it only at <see cref="CommitOutput"/>.
    /// </summary>
    public static FileStream CreateTemporary(string outPath)
    {
        var folder = Path.GetDirectoryName(outPath) ?? throw new SyncCliUsageException(
            DropboxCliCatalog.Message(DropboxCliMessage.OutFolderMissing, ("path", outPath)));
        var temporary = Path.Combine(folder, Path.GetFileName(outPath) + "." +
            Guid.NewGuid().ToString("N") + ".partial");
        return new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>
    /// Moves the completed temporary onto the destination. --yes is the move's own overwrite flag, so without it
    /// the file system refuses a destination that appeared after the preflight (no check-then-move window); any
    /// failed move removes the temporary and reports a cleanup failure.
    /// </summary>
    public static void CommitOutput(string outPath, FileStream temporary, bool yes, DropboxCliWarnings? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(temporary);
        var path = temporary.Name;
        temporary.Dispose();
        try
        {
            File.Move(path, outPath, overwrite: yes);
        }
        catch (IOException) when (!yes && File.Exists(outPath))
        {
            TryDelete(path, warnings);
            throw new SyncCliUsageException(DropboxCliCatalog.Message(DropboxCliMessage.OutExists, ("path", outPath)));
        }
        catch
        {
            TryDelete(path, warnings);
            throw;
        }
    }

    /// <summary>Writes text atomically through an unpredictable temporary file beside the destination.</summary>
    public static async Task WriteAtomicAsync(string outPath, string text, bool yes, DropboxCliWarnings warnings,
        CancellationToken cancellationToken)
    {
        PrepareOutput(outPath, yes);
        FileStream? temporary = null;
        try
        {
            temporary = CreateTemporary(outPath);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            await temporary.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
        }
        catch
        {
            DiscardTemporary(temporary, warnings);
            throw;
        }
        CommitOutput(outPath, temporary!, yes, warnings);
    }

    /// <summary>Discards a temporary file without touching the destination; safe to call when none exists.</summary>
    public static void DiscardTemporary(FileStream? temporary, DropboxCliWarnings? warnings = null)
    {
        if (temporary is null) return;
        var path = temporary.Name;
        temporary.Dispose();
        TryDelete(path, warnings);
    }

    private static void TryDelete(string path, DropboxCliWarnings? warnings)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException error) { ReportCleanupFailure(path, error, warnings); }
        catch (UnauthorizedAccessException error) { ReportCleanupFailure(path, error, warnings); }
    }

    /// <summary>Reports a failed cleanup as a command warning, without replacing the original error.</summary>
    internal static void ReportCleanupFailure(string path, Exception error, DropboxCliWarnings? warnings) =>
        warnings?.Add(DropboxCliCatalog.Message(DropboxCliMessage.CleanupFailed,
            ("path", path), ("error", error.Message)));
}
