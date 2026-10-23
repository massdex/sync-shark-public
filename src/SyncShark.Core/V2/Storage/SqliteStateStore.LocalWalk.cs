using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore : ILocalWalkStore
{
    string ILocalWalkStore.BeginLocalWalk(RootDef root, string options, NtfsUsnCheckpoint? current) => BeginLocalWalk(root, options, current);
    bool ILocalWalkStore.HasLocalWalk(string root) => HasLocalWalk(root);
    string? ILocalWalkStore.LocalWalkPage(string root, int ordinal) => LocalWalkPage(root, ordinal);
    int ILocalWalkStore.LocalWalkPageCount(string root) => LocalWalkPageCount(root);
    void ILocalWalkStore.SaveLocalWalkPages(string root, IReadOnlyList<(int Ordinal, string Payload)> pages) => SaveLocalWalkPages(root, pages);
    SemaphoreSlim ILocalWalkStore.LocalWalkGate(string root) => LocalWalkGate(root);
    void ILocalWalkStore.ForgetLocalWalk(string root) => ForgetLocalWalk(root);
    bool ILocalWalkStore.GapInventoryPending(string rootId) => GapInventoryPending(rootId);

    // Unfinished observations never enter file_state, generation completion, or deletion coverage.
    internal string BeginLocalWalk(RootDef root, string options, NtfsUsnCheckpoint? current)
    {
        var binding = JsonSerializer.Serialize(root) + "\n" + options;
        var savedCursor = InStore(() => StoreSchema.Scalar(_connection, null,
            "SELECT cursor FROM local_walk WHERE root_id=$r AND binding=$b;",
            ("$r", root.RootId), ("$b", binding)) as string);
        // The root walk gate serializes Begin/Save/Forget, not the store-wide gate.
        // An expired/unavailable journal or interrupted cleanup requires a fresh walk.
        if (savedCursor is not null && current is not null && !current.RequiresFullReconciliation(savedCursor))
            return savedCursor;
        ForgetLocalWalk(root.RootId);
        var cursor = current?.Cursor ?? string.Empty;
        InStore(() =>
        {
            using var tx = _connection.BeginTransaction();
            StoreSchema.Execute(_connection, tx,
                "INSERT INTO local_walk VALUES($r,$b,$c) ON CONFLICT(root_id) DO UPDATE SET binding=$b,cursor=$c;",
                ("$r", root.RootId), ("$b", binding), ("$c", cursor));
            tx.Commit();
        });
        return cursor;
    }

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

    // Keep the header AND count marker until every payload has been reclaimed. A crash
    // between pages leaves an incomplete checkpoint, never a truncated successful replay:
    // LocalWalkPageCount detects the missing prefix (including a missing final page).
    // The caller holds LocalWalkGate(root), as for Begin/Save, throughout this operation.
    internal const int LocalWalkCleanupPageRows = 32;
    internal const string LocalWalkCleanupPageSql =
        "DELETE FROM local_walk_page WHERE root_id=$r AND ordinal IN " +
        "(SELECT ordinal FROM local_walk_page WHERE root_id=$r AND ordinal>=0 ORDER BY ordinal LIMIT $n);";

    internal void ForgetLocalWalk(string root)
    {
        while (InStore(() =>
        {
            using var tx = _connection.BeginTransaction();
            // Also reject legacy checkpoints without a count marker on the next Begin.
            StoreSchema.Execute(_connection, tx, "UPDATE local_walk SET binding='' WHERE root_id=$r;", ("$r", root));
            StoreSchema.Execute(_connection, tx, LocalWalkCleanupPageSql,
                ("$r", root), ("$n", LocalWalkCleanupPageRows));
            var removed = StoreSchema.Changes(_connection, tx);
            if (removed < LocalWalkCleanupPageRows)
            {
                StoreSchema.Execute(_connection, tx, "DELETE FROM local_walk_page WHERE root_id=$r AND ordinal=-1;", ("$r", root));
                StoreSchema.Execute(_connection, tx, "DELETE FROM local_walk WHERE root_id=$r;", ("$r", root));
            }
            tx.Commit();
            return removed == LocalWalkCleanupPageRows;
        })) { }
    }
}
