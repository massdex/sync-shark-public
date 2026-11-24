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

    internal ScanScratch(string storePath, bool readOnly)
    {
        this.storePath = storePath;
        this.readOnly = readOnly;
        var fullPath = Path.GetFullPath(storePath);
        path = fullPath + ".scan";
        connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite }.ToString();
    }

    internal SqliteConnection OpenWriter()
    {
        var db = OpenConnection();
        try
        {
            using var version = StorageCommand.Create(db, null, "PRAGMA user_version");
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
        catch { db.Dispose(); throw; }
    }

    internal SqliteConnection OpenConnection()
    {
        var db = new SqliteConnection(connectionString);
        try
        {
            db.Open();
            db.CreateFunction("pathkey", (string path) => PathNames.Key(path), isDeterministic: true);
            return db;
        }
        catch { db.Dispose(); throw; }
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
            using var sql = StorageCommand.Create(writer, null, "PRAGMA scan_state.journal_mode=WAL;" + Schema.CreateScan);
            sql.ExecuteNonQuery();
            attached = true;
            return writer;
        }
        catch
        {
            using var detach = StorageCommand.Create(writer, null, "DETACH scan_state");
            detach.ExecuteNonQuery();
            throw;
        }
    }

    internal void Attach(SqliteConnection db)
    {
        using var attach = StorageCommand.Create(db, null, "ATTACH DATABASE $path AS scan_state", "$path", path);
        attach.ExecuteNonQuery();
    }

    // Readers may commit while visiting a row: never lend them the writer or hold its gate across yield.
    internal async IAsyncEnumerable<T> Stream<T>(Func<SqliteConnection,IAsyncEnumerable<T>> read, bool scanRows,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var db = OpenConnection();
        if (scanRows) Attach(db);
        await foreach (var row in read(db).WithCancellation(ct)) yield return row;
    }
}
