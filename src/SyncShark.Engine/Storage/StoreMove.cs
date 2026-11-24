using Microsoft.Data.Sqlite;
using static Massdex.SyncShark.Engine.Storage.StoreImage;

namespace Massdex.SyncShark.Engine.Storage;
/// <summary>Chooses the sole eligible release image and switches closed, verified stores without consuming the predecessor.</summary>
public sealed class StoreMove
{
    private const int PredecessorVersion = 16;
    private const string Backup = "state.v16.db";
    private static string StoreFile(string root) => Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.DatabaseFile);
    private static bool Occupied(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static int Version(string path)
    {
        using var db = Open(path, SqliteOpenMode.ReadOnly);
        var v = Convert.ToInt32(Scalar(db, "PRAGMA user_version"));
        if (v != PredecessorVersion && v != Schema.Version)
            throw new InvalidDataException(Text.Get(v < PredecessorVersion ? TextKey.StateSchemaTooOld : TextKey.StateSchemaTooNew, (TextArg.Path, path)));
        return v;
    }

    private static List<string> Required(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT json_extract(def_json,'$.ConnectionId') FROM pair";
        using var rd = cmd.ExecuteReader();
        var ids = new List<string>();
        while (rd.Read())
        {
            if (rd.IsDBNull(0) || rd.GetString(0).Length == 0)
                throw new InvalidDataException(Text.Get(TextKey.StateRootBindingInvalid));
            ids.Add(rd.GetString(0));
        }

        return ids;
    }

    private static string Stage(string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var path = target + "." + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N") + ".new";
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return path;
    }

    private static void Closed(string path)
    {
        if (Occupied(path + "-wal") || Occupied(path + "-shm") || Occupied(path + "-journal"))
            throw new IOException(Text.Get(TextKey.FailLocalInUse, (TextArg.Path, path)));
    }

    private static void RemoveStage(string path)
    {
        File.Delete(path);
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
        File.Delete(path + "-journal");
    }

    /// <summary>Publish the closed verified v17 stage, upgrading v16 in one transaction; Schema.Create only builds an empty schema.</summary>
    public Task MoveAsync(string predecessorRoot, string productRoot, Func<string, string, IReadOnlyList<string>, Action?> verifyConnections, CancellationToken ct) => MoveWithOutcomeAsync(predecessorRoot, productRoot, verifyConnections, ct);
    /// <summary>Refusals before publication throw; after publication a pointer-cleanup warning allows pairs to keep running.</summary>
    public Task<string?> MoveWithOutcomeAsync(string predecessorRoot, string productRoot, Func<string, string, IReadOnlyList<string>, Action?> verifyConnections, CancellationToken ct, Action<Func<string?>>? retryCleanup = null)
    {
        ct.ThrowIfCancellationRequested();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(productRoot));
        var target = StoreFile(root);
        var backup = Path.Combine(Path.GetDirectoryName(target)!, Backup);
        var pointer = Path.Combine(predecessorRoot, "product-root.txt");
        bool hasPointer = Occupied(pointer);
        var sourceRoot = PointerRoot(root, pointer, hasPointer);
        var location = LocateSource(root, sourceRoot, target, backup, pointer, hasPointer, verifyConnections);
        if (location == null) return Task.FromResult(CleanupPointer(pointer, sourceRoot, hasPointer, retryCleanup));
        var stage = Stage(target);
        Action? cleanup = null;
        bool publishedStage = false, replacing = false;
        try
        {
            PublishStage(location, root, stage, target, backup, verifyConnections, ct, ref cleanup, ref replacing);
            publishedStage = true;
        }
        catch
        {
            if (replacing && !Occupied(target) && Occupied(backup))
                File.Move(backup, target, false);
            cleanup?.Invoke();
            throw;
        }
        finally
        {
            if (!publishedStage && (!replacing || Occupied(target)))
                RemoveStage(stage);
        }

        return Task.FromResult(CleanupPointer(pointer, sourceRoot, hasPointer, retryCleanup));
    }

    private sealed record SourceLocation(string Root, string Source);

    private static string PointerRoot(string root, string pointer, bool hasPointer)
    {
        if (!hasPointer)
            return root;
        var lines = File.ReadAllLines(pointer);
        if (lines.Length != 1 || !Path.IsPathFullyQualified(lines[0]))
            throw new InvalidDataException(Text.Get(TextKey.StateRootBindingInvalid));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(lines[0]));
    }

    private static SourceLocation? LocateSource(string root, string sourceRoot, string target, string backup, string pointer, bool hasPointer, Func<string,string,IReadOnlyList<string>,Action?> verifyConnections)
    {
        bool external = !string.Equals(root, sourceRoot, StringComparison.OrdinalIgnoreCase);
        var externalPath = StoreFile(sourceRoot);
        var hasTarget = Occupied(target);
        var hasBackup = Occupied(backup);
        var version = hasTarget ? Version(target) : 0;
        if (version == Schema.Version)
        {
            var predecessor = external ? ExternalVersion(externalPath) : PredecessorVersion;
            if (predecessor == Schema.Version)
                throw new InvalidDataException(Text.Get(TextKey.RecoveryStageLocationBlocked, (TextArg.Place, externalPath)));
            VerifyPublished(target, hasPointer, predecessor == PredecessorVersion ? sourceRoot : null, root, verifyConnections);
            return null;
        }
        if (hasBackup && Version(backup) != PredecessorVersion || external && (!Occupied(externalPath) || Version(externalPath) != PredecessorVersion || hasBackup))
            throw new InvalidDataException(Text.Get(TextKey.RecoveryStageLocationBlocked, (TextArg.Place, externalPath)));
        if (hasTarget && (external || hasBackup))
            throw new InvalidDataException(Text.Get(TextKey.RecoveryStageLocationBlocked, (TextArg.Place, target)));
        if (!hasTarget && !hasBackup && !external)
        {
            if (hasPointer)
                throw new InvalidDataException(Text.Get(TextKey.RecoveryStageLocationBlocked, (TextArg.Place, pointer)));
            Closed(target);
            return null;
        }

        var source = external ? externalPath : hasBackup ? backup : target;
        if (!hasTarget)
            Closed(target);
        return new(sourceRoot, source);
    }

    /// <summary>A null source means the predecessor store is gone or unusable: no root is re-read; the fixed root's list published at the switch stands, as on a plain start.</summary>
    private static void VerifyPublished(string target, bool hasPointer, string? sourceRoot, string root, Func<string,string,IReadOnlyList<string>,Action?> verifyConnections)
    {
        using var published = Open(target, SqliteOpenMode.ReadOnly);
        ValidateShape(published);
        if (hasPointer)
        {
            Verify(published, Schema.Version);
            if (sourceRoot != null)
                verifyConnections(sourceRoot, root, Required(published));
        }
    }

    /// <summary>The pointer's store version, or 0 when missing or unreadable; beside a published store only a second current image is ambiguous.</summary>
    private static int ExternalVersion(string path)
    {
        try { return Occupied(path) ? Version(path) : 0; }
        catch (Exception e) when (e is SqliteException or InvalidDataException or IOException or UnauthorizedAccessException) { return 0; }
    }

    private static void PublishStage(SourceLocation location, string root, string stage, string target, string backup, Func<string,string,IReadOnlyList<string>,Action?> verifyConnections, CancellationToken ct, ref Action? cleanup, ref bool replacing)
    {
        List<string> ids;
        using (var original = Open(location.Source, SqliteOpenMode.ReadWrite))
        using (var next = Open(stage, SqliteOpenMode.ReadWrite))
        {
            Checkpoint(original);
            Verify(original, PredecessorVersion);
            ids = Required(original);
            ct.ThrowIfCancellationRequested();
            original.BackupDatabase(next);
            SchemaUpgrade.Upgrade(next, ct);
            Checkpoint(next);
            Checkpoint(original);
        }

        Closed(stage);
        Closed(location.Source);
        ct.ThrowIfCancellationRequested();
        cleanup = verifyConnections(location.Root, root, ids);
        ct.ThrowIfCancellationRequested();
        if (location.Source == target)
        {
            replacing = true;
            File.Replace(stage, target, backup);
        }
        else
        {
            Closed(target);
            File.Move(stage, target, false);
        }
    }

    private static string? RemovePointer(string pointer, bool hasPointer)
    {
        if (hasPointer)
            try { File.Delete(pointer); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
        return null;
    }

    /// <summary>One cleanup path for both verified exits; a failed delete hands back the retry so the running host can finish it without reopening the image.</summary>
    private static string? CleanupPointer(string pointer, string sourceRoot, bool hasPointer, Action<Func<string?>>? retryCleanup)
    {
        var warning = RemovePointer(pointer, hasPointer);
        if (warning != null)
            retryCleanup?.Invoke(() => RetryRemovePointer(pointer, sourceRoot));
        return warning;
    }

    /// <summary>Retry the failed delete only while the pointer still names the verified predecessor; a replaced or malformed pointer is left alone.</summary>
    private static string? RetryRemovePointer(string pointer, string sourceRoot)
    {
        if (!Occupied(pointer))
            return null;
        string current;
        try
        {
            var lines = File.ReadAllLines(pointer);
            if (lines.Length != 1 || !Path.IsPathFullyQualified(lines[0]))
                return Text.Get(TextKey.StateRootBindingInvalid);
            current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(lines[0]));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return Text.Get(TextKey.StateRootBindingInvalid); }
        _ = string.Equals(current, sourceRoot, StringComparison.OrdinalIgnoreCase);
        return RemovePointer(pointer, true);
    }

    /// <summary>Create a verified empty database for the first explicit pair add, without replacing an existing file.</summary>
    public Task CreateAsync(string productRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = StoreFile(productRoot);
        if (Occupied(target) || Occupied(Path.Combine(Path.GetDirectoryName(target)!, Backup)) || Occupied(Path.Combine(productRoot, "product-root.txt")))
            throw new IOException(Text.Get(TextKey.ClashNameTaken, (TextArg.Path, target)));
        Closed(target);
        var stage = Stage(target);
        try
        {
            using (var db = Open(stage, SqliteOpenMode.ReadWrite))
            {
                Execute(db, Schema.Pragmas);
                using (var tx = db.BeginTransaction())
                {
                    Execute(db, Schema.Create, tx);
                    Execute(db, $"PRAGMA user_version={Schema.Version}", tx);
                    Verify(db, Schema.Version, tx);
                    tx.Commit();
                }

                Checkpoint(db);
            }

            Closed(stage);
            ct.ThrowIfCancellationRequested();
            Closed(target);
            File.Move(stage, target, false);
        }
        finally
        {
            RemoveStage(stage);
        }

        return Task.CompletedTask;
    }
}
