using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Disposable scratch storage beside the store. A lease protects active stages across processes.</summary>
internal sealed class SnapshotTemporary : IDisposable
{
    private readonly string _path;
    private readonly FileStream _lease;
    internal SqliteConnection Connection { get; }

    internal SnapshotTemporary(SqliteConnection owner)
    {
        var folder = Folder(owner);
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "syncshark-snapshot-" + Guid.NewGuid().ToString("N") + ".db");
        _lease = new FileStream(_path + ".lease", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        try
        {
            Connection.Open();
            StoreSchema.Execute(Connection, null, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-32768; PRAGMA locking_mode=EXCLUSIVE;");
        }
        catch { Dispose(); throw; }
    }

    private static string Folder(SqliteConnection owner) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(owner.DataSource))!, "tmp");

    internal static void Sweep(SqliteConnection owner)
    {
        try
        {
            var folder = Folder(owner);
            if (!Directory.Exists(folder)) return;
            foreach (var leasePath in Directory.EnumerateFiles(folder, "syncshark-snapshot-*.db.lease"))
            {
                try
                {
                    // An active stage holds this handle exclusively, even between SQLite transactions.
                    bool removed;
                    using (var lease = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        removed = Cleanup(leasePath[..^6]);
                    if (removed) File.Delete(leasePath);
                }
                catch (IOException) { /* Active owner, or retry cleanup at the next open. */ }
                catch (UnauthorizedAccessException ex) { Warn(ex); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn(ex); }
    }

    private static bool Cleanup(string path)
    {
        try
        {
            foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" }) File.Delete(path + suffix);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn(ex); return false; }
    }

    private static void Warn(Exception ex) => Trace.TraceWarning("Snapshot scratch cleanup deferred until next store open: {0}", ex.Message);

    public void Dispose()
    {
        Connection.Dispose();
        var removed = Cleanup(_path);
        _lease.Dispose();
        if (removed)
        {
            try { File.Delete(_path + ".lease"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn(ex); }
        }
    }
}
