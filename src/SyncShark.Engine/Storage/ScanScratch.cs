using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Opens connections to the store image and attaches its disposable scan image on demand.</summary>
internal sealed class ScanScratch
{
    private readonly string connectionString;
    private readonly string path;
    private readonly string storePath;
    private readonly bool readOnly;
    private bool attached;
    internal Action<SqliteConnection, bool>? Observe { get; init; }

    internal ScanScratch(string storePath, bool readOnly)
    {
        this.storePath = storePath;
        this.readOnly = readOnly;
        var fullPath = Path.GetFullPath(storePath);
        path = fullPath + ".scan";
        connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite }.ToString();
        if (!readOnly) _ = DeleteLeftover();
    }

    // No scan is active at open or after the last scan clears, so the image is disposable. A held file belongs to
    // a live scan: stop at the first failure, sidecars first, so the image is never kept without its write-ahead log.
    private bool DeleteLeftover()
    {
        try { foreach (var suffix in new[] { "-wal", "-shm", "" }) File.Delete(path + suffix); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    internal SqliteConnection OpenWriter()
    {
        var db = OpenConnection();
        try
        {
            using var version = StorageCommand.Create(db, null, Sql.Get(SqlKey.ScanScratchOpenWriter1));
            var actual = Convert.ToInt64(version.ExecuteScalar());
            if (actual != Schema.Version) throw new InvalidDataException(Text.Get(
                actual > Schema.Version ? TextKey.StateSchemaTooNew : TextKey.StateSchemaTooOld, (TextArg.Path,storePath)));
            if (!readOnly)
            {
                using var pragmas = StorageCommand.Create(db, null, Schema.Pragmas);
                pragmas.ExecuteNonQuery();
            }
            return db;
        }
        catch { Close(db); throw; }
    }

    internal SqliteConnection OpenConnection()
    {
        var db = new SqliteConnection(connectionString);
        try
        {
            db.Open();
            db.CreateFunction("pathkey", (string path) => PathNames.Key(path), isDeterministic: true);
            Observe?.Invoke(db, true);
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    internal void Close(SqliteConnection db)
    {
        try { Observe?.Invoke(db, false); }
        finally { db.Dispose(); }
    }

    // Called on the store writer under its gate; other connections attach the initialized image separately.
    internal SqliteConnection Ensure(SqliteConnection writer)
    {
        if (attached) return writer;
        // ATTACH inherits the open-existing flags of main. Create only the disposable scan image first.
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString())) db.Open();
        Attach(writer);
        try
        {
            using var sql = StorageCommand.Create(writer, null, Sql.Get(SqlKey.ScanScratchEnsure1) + Schema.CreateScan);
            sql.ExecuteNonQuery();
            attached = true;
            return writer;
        }
        catch
        {
            using var detach = StorageCommand.Create(writer, null, Sql.Get(SqlKey.ScanScratchEnsure2));
            detach.ExecuteNonQuery();
            throw;
        }
    }

    // Called on the store writer under its gate once no scan is active; the next Ensure recreates the image.
    // False when the image may still hold rows, so the caller purges them instead.
    internal bool Release(SqliteConnection writer)
    {
        if (attached)
        {
            using var detach = StorageCommand.Create(writer, null, Sql.Get(SqlKey.ScanScratchEnsure2));
            try { detach.ExecuteNonQuery(); } catch (SqliteException) { return false; }
            attached = false;
        }
        return DeleteLeftover();
    }

    internal void Attach(SqliteConnection db)
    {
        using var attach = StorageCommand.Create(db, null, Sql.Get(SqlKey.ScanScratchAttach1), "$path", path);
        attach.ExecuteNonQuery();
    }

    // Readers may commit while visiting a row: never lend them the writer or hold its gate across yield.
    internal async IAsyncEnumerable<T> Stream<T>(Func<SqliteConnection,IAsyncEnumerable<T>> read, bool scanRows,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var db = OpenConnection();
        try
        {
            if (scanRows) Attach(db);
            await foreach (var row in read(db).WithCancellation(ct)) yield return row;
        }
        finally { Close(db); }
    }
}
