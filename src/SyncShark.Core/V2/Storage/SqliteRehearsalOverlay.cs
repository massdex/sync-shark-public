using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Disk-backed changed metadata only. Each rehearsal session owns a fresh file inside its isolated data root.</summary>
internal sealed class SqliteRehearsalOverlay : RehearsalOverlay
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private long _version;
    internal Action? ReadPageForTest { get; set; }
    internal override long Version { get { lock (_gate) return _version; } }
    internal SqliteRehearsalOverlay(string path)
    {
        try
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            _connection.Open();
            var command = _connection.CreateCommand();
            Exception? cleanupPrimary0 = null;
            try
            {
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; CREATE TABLE overlay(root TEXT NOT NULL,path TEXT COLLATE NOCASE NOT NULL,state TEXT,seq INTEGER NOT NULL,display TEXT NOT NULL,objectid TEXT NOT NULL,PRIMARY KEY(root,path)) WITHOUT ROWID; CREATE INDEX overlay_seq ON overlay(root,seq);";
                command.ExecuteNonQuery();

            }
            catch (Exception cleanupError) { cleanupPrimary0 = cleanupError; throw; }
            finally { SqliteStateStore.DisposePreserving(cleanupPrimary0, command); }
        }
        catch (SqliteException error) when (StoreSchema.IsDamaged(error))
        {
            var failure = new StateDamagedException($"v2 state is damaged: {error.Message}", error);
            SqliteStateStore.DisposePreserving(failure, _connection);
            throw failure;
        }
        catch (Exception error)
        {
            SqliteStateStore.DisposePreserving(error, _connection);
            throw;
        }
    }
    internal override bool Try(string root, string path, out ItemState? state)
    {
        try
        {
            lock (_gate)
            {
                var command = _connection.CreateCommand();
                Exception? cleanupPrimary1 = null;
                try
                {
                    command.CommandText = "SELECT state FROM overlay WHERE root=$root AND path=$path";
                    command.Parameters.AddWithValue("$root", root);
                    command.Parameters.AddWithValue("$path", PathKey.Of(path));
                    var reader = command.ExecuteReader();
                    Exception? cleanupPrimary2 = null;
                    try
                    {
                        if (!reader.Read()) { state = null; return false; }
                        state = reader.IsDBNull(0) ? null : JsonSerializer.Deserialize<ItemState>(reader.GetString(0));
                        return true;

                    }
                    catch (Exception cleanupError) { cleanupPrimary2 = cleanupError; throw; }
                    finally { SqliteStateStore.DisposePreserving(cleanupPrimary2, reader); }

                }
                catch (Exception cleanupError) { cleanupPrimary1 = cleanupError; throw; }
                finally { SqliteStateStore.DisposePreserving(cleanupPrimary1, command); }
            }

        }
        catch (SqliteException error) when (StoreSchema.IsDamaged(error))
        { throw new StateDamagedException($"v2 state is damaged: {error.Message}", error); }
    }
    internal override void Save(string root, string path, ItemState? state, ItemState? victim = null)
    {
        try
        {
            lock (_gate)
            {
                var command = _connection.CreateCommand();
                Exception? cleanupPrimary3 = null;
                try
                {
                    command.CommandText = "INSERT INTO overlay(root,path,state,seq,display,objectid) VALUES($root,$path,$state,$seq,$display,$object) ON CONFLICT(root,path) DO UPDATE SET state=excluded.state,seq=excluded.seq,display=excluded.display,objectid=excluded.objectid";
                    command.Parameters.AddWithValue("$seq", ++_version);
                    command.Parameters.AddWithValue("$display", path);
                    command.Parameters.AddWithValue("$object", state?.ObjectId ?? victim?.ObjectId ?? "");
                    command.Parameters.AddWithValue("$root", root);
                    command.Parameters.AddWithValue("$path", PathKey.Of(path));
                    command.Parameters.AddWithValue("$state", state is null ? DBNull.Value : JsonSerializer.Serialize(state));
                    command.ExecuteNonQuery();

                }
                catch (Exception cleanupError) { cleanupPrimary3 = cleanupError; throw; }
                finally { SqliteStateStore.DisposePreserving(cleanupPrimary3, command); }
            }

        }
        catch (SqliteException error) when (StoreSchema.IsDamaged(error))
        { throw new StateDamagedException($"v2 state is damaged: {error.Message}", error); }
    }
    internal override IEnumerable<ItemState> Items(string root, string? subtree = null)
    {
        var prefix = subtree is null ? null : PathKey.Of(subtree);
        string? after = null;
        while (true)
        {
            var page = new List<(string Path, ItemState? State)>();
            SqliteStateStore.DamageBoundary(() =>
            {
                ReadPageForTest?.Invoke();
                lock (_gate)
                {
                    var command = _connection.CreateCommand();
                    Exception? cleanupPrimary4 = null;
                    try
                    {
                        command.CommandText = prefix is null or ""
                        ? "SELECT path,state FROM overlay WHERE root=$root AND ($after IS NULL OR path>$after COLLATE NOCASE) ORDER BY path COLLATE NOCASE LIMIT 256"
                        : "SELECT path,state FROM overlay WHERE root=$root AND path >= $prefix COLLATE NOCASE AND path < $upper COLLATE NOCASE AND (path=$prefix COLLATE NOCASE OR path >= $lower COLLATE NOCASE) AND ($after IS NULL OR path>$after COLLATE NOCASE) ORDER BY path COLLATE NOCASE LIMIT 256";
                        if (prefix is not null and not "")
                        {
                            command.Parameters.AddWithValue("$prefix", prefix);
                            command.Parameters.AddWithValue("$lower", prefix + "/");
                            command.Parameters.AddWithValue("$upper", prefix + "0");
                        }
                        command.Parameters.AddWithValue("$root", root);
                        command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
                        var reader = command.ExecuteReader();
                        Exception? cleanupPrimary5 = null;
                        try
                        {
                            while (reader.Read()) page.Add((reader.GetString(0), reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<ItemState>(reader.GetString(1))));

                        }
                        catch (Exception cleanupError) { cleanupPrimary5 = cleanupError; throw; }
                        finally { SqliteStateStore.DisposePreserving(cleanupPrimary5, reader); }

                    }
                    catch (Exception cleanupError) { cleanupPrimary4 = cleanupError; throw; }
                    finally { SqliteStateStore.DisposePreserving(cleanupPrimary4, command); }
                }
            });
            foreach (var item in page) if (item.State is not null) yield return item.State;
            if (page.Count < 256) yield break;
            after = page[^1].Path;
        }
    }
    internal override IEnumerable<Change> Since(string root, long after, long through)
    {
        while (true)
        {
            var page = new List<(long Sequence, Change Change)>();
            SqliteStateStore.DamageBoundary(() =>
            {
                ReadPageForTest?.Invoke();
                lock (_gate)
                {
                    var command = _connection.CreateCommand();
                    Exception? cleanupPrimary6 = null;
                    try
                    {
                        command.CommandText = "SELECT seq,display,objectid,state FROM overlay WHERE root=$root AND seq>$after AND seq<=$through ORDER BY seq LIMIT 256";
                        command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$after", after); command.Parameters.AddWithValue("$through", through);
                        var reader = command.ExecuteReader();
                        Exception? cleanupPrimary7 = null;
                        try
                        {
                            while (reader.Read())
                            {
                                var state = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<ItemState>(reader.GetString(3));
                                page.Add((reader.GetInt64(0), new Change(state is null ? ChangeKind.Delete : ChangeKind.Upsert,
                                reader.GetString(2), reader.GetString(1), state, null)));
                            }

                        }
                        catch (Exception cleanupError) { cleanupPrimary7 = cleanupError; throw; }
                        finally { SqliteStateStore.DisposePreserving(cleanupPrimary7, reader); }

                    }
                    catch (Exception cleanupError) { cleanupPrimary6 = cleanupError; throw; }
                    finally { SqliteStateStore.DisposePreserving(cleanupPrimary6, command); }
                }
            });
            foreach (var row in page) yield return row.Change;
            if (page.Count < 256) yield break;
            after = page[^1].Sequence;
        }
    }
    public override void Dispose() => SqliteStateStore.DamageBoundary(_connection.Dispose);
}
