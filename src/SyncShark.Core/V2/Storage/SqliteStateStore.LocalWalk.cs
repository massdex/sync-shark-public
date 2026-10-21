using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Unfinished observations never enter file_state, generation completion, or deletion coverage.
    internal string BeginLocalWalk(RootDef root, string options, NtfsUsnCheckpoint? current) => InStore(() =>
    {
        var binding = JsonSerializer.Serialize(root) + "\n" + options;
        using var tx = _connection.BeginTransaction();
        string? savedCursor = null;
        using (var command = StoreSchema.Command(_connection, tx,
            "SELECT cursor FROM local_walk WHERE root_id=$r AND binding=$b;"))
        {
            command.Parameters.AddWithValue("$r", root.RootId);
            command.Parameters.AddWithValue("$b", binding);
            savedCursor = command.ExecuteScalar() as string;
        }
        // An expired/unavailable journal cannot cover edits in already scanned directories.
        if (savedCursor is null || current is null || current.RequiresFullReconciliation(savedCursor))
        {
            savedCursor = current?.Cursor ?? string.Empty;
            StoreSchema.Execute(_connection, tx, "DELETE FROM local_walk_page WHERE root_id=$r;", ("$r", root.RootId));
            StoreSchema.Execute(_connection, tx,
                "INSERT INTO local_walk VALUES($r,$b,$c) ON CONFLICT(root_id) DO UPDATE SET binding=$b,cursor=$c;",
                ("$r", root.RootId), ("$b", binding), ("$c", savedCursor));
        }
        tx.Commit();
        return savedCursor;
    });

    internal bool HasLocalWalk(string root) => InStore(() =>
        (long)StoreSchema.Scalar(_connection, null,
            "SELECT EXISTS(SELECT 1 FROM local_walk WHERE root_id=$r);", ("$r", root))! != 0);

    internal string? LocalWalkPage(string root, int ordinal) => InStore(() =>
        StoreSchema.Scalar(_connection, null,
            "SELECT payload FROM local_walk_page WHERE root_id=$r AND ordinal=$n;",
            ("$r", root), ("$n", ordinal)) as string);

    // The count marker also detects a missing last page, not just holes in the middle.
    internal int LocalWalkPageCount(string root) => InStore(() =>
    {
        using var command = StoreSchema.Command(_connection, null,
            "SELECT COUNT(*), COALESCE(MAX(ordinal)+1,0), " +
            "(SELECT payload FROM local_walk_page WHERE root_id=$r AND ordinal=-1) " +
            "FROM local_walk_page WHERE root_id=$r AND ordinal>=0;");
        command.Parameters.AddWithValue("$r", root);
        using var rows = command.ExecuteReader();
        rows.Read();
        var count = rows.GetInt64(0);
        var end = rows.GetInt64(1);
        var expected = end;
        if (!rows.IsDBNull(2) && (!long.TryParse(rows.GetString(2), out expected) || expected < 0))
            throw new LocalWalkReplayInvalidException("Invalid local walk page count.");
        if (count != end || count != expected || count > int.MaxValue)
            throw new LocalWalkReplayInvalidException("Missing local walk page or invalid page count.");
        return (int)count;
    });

    internal void SaveLocalWalkPage(string root, int ordinal, string payload) =>
        SaveLocalWalkPages(root, [(ordinal, payload)]);

    internal void SaveLocalWalkPages(string root, IReadOnlyList<(int Ordinal, string Payload)> pages) => InStore(() =>
    {
        using var tx = _connection.BeginTransaction();
        foreach (var (ordinal, payload) in pages)
            StoreSchema.Execute(_connection, tx, "INSERT INTO local_walk_page VALUES($r,$n,$p);",
                ("$r", root), ("$n", ordinal), ("$p", payload));
        StoreSchema.Execute(_connection, tx,
            "INSERT INTO local_walk_page VALUES($r,-1,$p) ON CONFLICT(root_id,ordinal) DO UPDATE SET payload=$p;",
            ("$r", root), ("$p", (pages[^1].Ordinal + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        tx.Commit();
    });

    // Root-owned admission, independent of pair identity. Different roots never wait on each other.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _localWalkGates = new();
    internal SemaphoreSlim LocalWalkGate(string root) => _localWalkGates.GetOrAdd(root, _ => new(1, 1));

    internal void ForgetLocalWalk(string root) => InStore(() =>
    {
        using var tx = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, tx, "DELETE FROM local_walk_page WHERE root_id=$r;", ("$r", root));
        StoreSchema.Execute(_connection, tx, "DELETE FROM local_walk WHERE root_id=$r;", ("$r", root));
        tx.Commit();
    });
}
