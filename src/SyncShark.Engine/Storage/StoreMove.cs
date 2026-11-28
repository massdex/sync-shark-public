using Microsoft.Data.Sqlite;
using static Massdex.SyncShark.Engine.Storage.StoreImage;

namespace Massdex.SyncShark.Engine.Storage;
/// <summary>Chooses the sole eligible release image and switches closed, verified stores without consuming the predecessor.</summary>
public sealed class StoreMove
{
    private const int PredecessorVersion = 16;
    private const string Backup = "state.v16.db";
    // A retired store reads Retired + its schema: every older binary refuses any value it does not know.
    private const int Retired = 1000;
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

    /// <summary>The raw user_version; a ReadWrite open lets SQLite roll back a hot journal before the read.</summary>
    private static int Version(string path, SqliteOpenMode mode)
    {
        try
        {
            using var db = Open(path, mode);
            return Convert.ToInt32(Scalar(db, Sql.Get(SqlKey.StoreMoveVersion1)));
        }
        catch (SqliteException e) when (mode == SqliteOpenMode.ReadWrite && InUse(e)) { throw InUse(path, e); }
    }

    private static bool InUse(SqliteException e) => e.SqliteErrorCode is 5 or 6 or 8 or 14;
    private static IOException InUse(string path, Exception e) => new(Text.Get(TextKey.StoreMoveInUse, (TextArg.Path, path)), e);
    private static int Logical(int v) => v >= Retired ? v - Retired : v;

    private static int Role(string path, int lv) => lv < PredecessorVersion || lv > Schema.Version - 1
        ? throw new InvalidDataException(Text.Get(lv < PredecessorVersion ? TextKey.StateSchemaTooOld : TextKey.StateSchemaTooNew, (TextArg.Path, path))) : lv;

    private static void Mark(string path, int lv)
    {
        try
        {
            // TRUNCATE: SQLite's Windows VFS does not directory-sync a DELETE-mode unlink. The closed re-read refuses a journal left hot.
            using (var db = Open(path, SqliteOpenMode.ReadWrite))
            {
                if (Scalar(db, Sql.Get(SqlKey.StoreMoveRetire2)) as string != "wal" && Scalar(db, Sql.Get(SqlKey.StoreMoveRetire3)) as string != "truncate")
                    throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, path)));
                Execute(db, Sql.Get(SqlKey.StoreMoveRetire1) + (Retired + lv));
            }
            if (Version(path, SqliteOpenMode.ReadOnly) != Retired + lv)
                throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, path)));
        }
        catch (SqliteException e) when (InUse(e)) { throw InUse(path, e); }
    }

    // Main file only: excludes every later SQLite writer while read-only readers and the WAL stay usable.
    private static FileStream Hold(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (IOException e) when ((e.HResult & 0xFFFF) is 32 or 33) { throw InUse(path, e); }
    }

    private static List<string> Required(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = Sql.Get(SqlKey.StoreMoveRequired1);
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

    /// <summary>Retire the predecessor in place and publish the closed verified upgraded stage at the new name; Schema.Create only builds an empty schema.</summary>
    public Task MoveAsync(string predecessorRoot, string productRoot, Func<string, string, IReadOnlyList<string>, Action?> verifyConnections, CancellationToken ct) => MoveWithOutcomeAsync(predecessorRoot, productRoot, verifyConnections, ct);
    /// <summary>Refusals before publication throw; after publication a pointer-cleanup warning allows pairs to keep running.</summary>
    public Task<string?> MoveWithOutcomeAsync(string predecessorRoot, string productRoot, Func<string, string, IReadOnlyList<string>, Action?> verifyConnections, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(productRoot));
        var target = StoreFile(root);
        var backup = Path.Combine(Path.GetDirectoryName(target)!, Backup);
        var pointer = Path.Combine(predecessorRoot, "product-root.txt");
        bool hasPointer = Occupied(pointer);
        var location = LocateSource(root, target, backup, pointer, hasPointer, verifyConnections);
        if (location == null) return Task.FromResult(RemovePointer(pointer, hasPointer));
        FileStream? hold = null;
        string? stage = null;
        Action? cleanup = null;
        bool published = false;
        try
        {
            // Rewritten on every run: a visible mark is no proof its commit was synced. Both are durable before the new store can exist.
            Mark(location.Source, location.Version);
            if (location.Other != null) Mark(location.Other, PredecessorVersion);
            hold = Hold(location.Source);
            stage = Stage(target);
            var ids = PublishStage(location, stage, ct);
            cleanup = verifyConnections(location.Root, root, ids);
            ct.ThrowIfCancellationRequested();
            Closed(target);
            Local.WriteThroughMove.Move(stage, target);
            published = true;
        }
        catch (Exception e) when (e is SqliteException { SqliteErrorCode: 13 } || e is IOException && (e.HResult & 0xFFFF) is 39 or 112)
        {
            cleanup?.Invoke();
            throw NoSpace(target, e);
        }
        catch
        {
            cleanup?.Invoke();
            throw;
        }
        finally
        {
            hold?.Dispose();
            if (!published && stage != null)
                try { RemoveStage(stage); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        return Task.FromResult(RemovePointer(pointer, hasPointer));
    }

    private sealed record SourceLocation(string Root, string Source, int Version, string? Other);

    private static string PointerRoot(string root, string pointer, bool hasPointer)
    {
        if (!hasPointer)
            return root;
        var lines = File.ReadAllLines(pointer);
        if (lines.Length != 1 || !Path.IsPathFullyQualified(lines[0]))
            throw new InvalidDataException(Text.Get(TextKey.StateRootBindingInvalid));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(lines[0]));
    }

    // Retain one job: one precedence matrix for published, external, local and fallback stores; sharing raw vs logical versions explicitly avoids two competing source selectors.
    private static SourceLocation? LocateSource(string root, string target, string backup, string pointer, bool hasPointer, Func<string,string,IReadOnlyList<string>,Action?> verifyConnections)
    {
        var sourceRoot = PointerRoot(root, pointer, hasPointer);
        bool external = !string.Equals(root, sourceRoot, StringComparison.OrdinalIgnoreCase);
        var local = Path.Combine(Path.GetDirectoryName(target)!, ResourceNames.PredecessorFile);
        var externalPath = Path.Combine(sourceRoot, ResourceNames.EngineFolder, ResourceNames.PredecessorFile);
        var hasBackup = Occupied(backup);
        if (Occupied(target))
        {
            var version = Version(target, SqliteOpenMode.ReadOnly);
            if (version != Schema.Version)
                throw new InvalidDataException(Text.Get(version < Schema.Version ? TextKey.StateSchemaTooOld : TextKey.StateSchemaTooNew, (TextArg.Path, target)));
            if (external && ExternalVersion(StoreFile(sourceRoot)) == Schema.Version)
                throw new InvalidDataException(Text.Get(TextKey.RecoveryStageLocationBlocked, (TextArg.Place, StoreFile(sourceRoot))));
            var predecessor = external ? ExternalVersion(externalPath) : PredecessorVersion;
            // A source of ours is always marked before the new store exists: an unmarked current image there is an old binary's.
            if (predecessor == Schema.Version - 1)
                throw new InvalidDataException(Text.Get(TextKey.RecoveryStageLocationBlocked, (TextArg.Place, externalPath)));
            VerifyPublished(target, hasPointer, Logical(predecessor) is >= PredecessorVersion and < Schema.Version ? sourceRoot : null, root, verifyConnections);
            return null;
        }
        Closed(target);
        if (external)
        {
            foreach (var place in new[] { local, backup, externalPath })
                if (Occupied(place) != (place == externalPath))
                    throw new InvalidDataException(Text.Get(TextKey.RecoveryStageLocationBlocked, (TextArg.Place, place)));
            return new(sourceRoot, externalPath, Role(externalPath, Logical(Version(externalPath, SqliteOpenMode.ReadWrite))), null);
        }
        if (Occupied(local))
        {
            var version = Logical(Version(local, SqliteOpenMode.ReadWrite));
            if (!hasBackup)
                return new(sourceRoot, local, Role(local, version), null);
            // Beside the put-aside v16 image only the v17 it became is a source; the v16 image is retired with it.
            if (version != Schema.Version - 1 || Logical(Version(backup, SqliteOpenMode.ReadWrite)) != PredecessorVersion)
                throw new InvalidDataException(Text.Get(TextKey.RecoveryStageLocationBlocked, (TextArg.Place, version != Schema.Version - 1 ? local : backup)));
            return new(sourceRoot, local, version, backup);
        }
        if (hasBackup)
            return Logical(Version(backup, SqliteOpenMode.ReadWrite)) == PredecessorVersion ? new(sourceRoot, backup, PredecessorVersion, null)
                : throw new InvalidDataException(Text.Get(TextKey.RecoveryStageLocationBlocked, (TextArg.Place, backup)));
        if (hasPointer)
            throw new InvalidDataException(Text.Get(TextKey.RecoveryStageLocationBlocked, (TextArg.Place, pointer)));
        return null;
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
        try { return Occupied(path) ? Version(path, SqliteOpenMode.ReadOnly) : 0; }
        catch (Exception e) when (e is SqliteException or InvalidDataException or IOException or UnauthorizedAccessException) { return 0; }
    }

    private static List<string> PublishStage(SourceLocation location, string stage, CancellationToken ct)
    {
        List<string> ids;
        using (var original = Open(location.Source, SqliteOpenMode.ReadOnly))
        using (var next = Open(stage, SqliteOpenMode.ReadWrite))
        {
            Verify(original, Retired + location.Version);
            ids = Required(original);
            ct.ThrowIfCancellationRequested();
            original.BackupDatabase(next);
            // The stage is private and discarded after any throw, so it needs no journal; sorts spill to disk, not memory.
            if (Scalar(next, Sql.Get(SqlKey.StoreMoveStage1)) as string != "off")
                throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, stage)));
            Execute(next, Sql.Get(SqlKey.StoreMoveStage2));
            SchemaUpgrade.Upgrade(next, location.Version, ct);
            if (Scalar(next, Sql.Get(SqlKey.StoreMoveStage3)) as string != "wal")
                throw new InvalidDataException(Text.Get(TextKey.FailStoreMutation, (TextArg.Path, stage)));
            Checkpoint(next);
        }

        Closed(stage);
        using (var file = new FileStream(stage, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            file.Flush(true);
        return ids;
    }

    private static IOException NoSpace(string target, Exception? e) => new(Text.Get(TextKey.StoreMoveNoSpace, (TextArg.Path, Path.GetDirectoryName(target)!)), e);

    private static string? RemovePointer(string pointer, bool hasPointer)
    {
        if (hasPointer)
            try { File.Delete(pointer); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
        return null;
    }

    /// <summary>Create a verified empty database for the first explicit pair add, without replacing an existing file.</summary>
    public Task CreateAsync(string productRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = StoreFile(productRoot);
        if (Occupied(target) || Occupied(Path.Combine(Path.GetDirectoryName(target)!, ResourceNames.PredecessorFile)) || Occupied(Path.Combine(Path.GetDirectoryName(target)!, Backup)) || Occupied(Path.Combine(productRoot, "product-root.txt")))
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
                    Execute(db, $"{Sql.Get(SqlKey.StoreMoveCreate1)}{Schema.Version}", tx);
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
