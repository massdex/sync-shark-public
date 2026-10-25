using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static class JournalLookupChecks
{
    internal static async Task RunAsync(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var root = new RootDef("journal-root", SyncProviders.Local, null, folder, Id(1));
        var gen = store.BeginGeneration(root.RootId, "full");
        var items = new[] { Item("parent", 2, ItemKind.Folder), Item("parent/Old", 3), Item("alias-a", 4), Item("alias-b", 4) };
        store.WriteSnapshot(new Snapshot(root.RootId, Side.A, gen, "cursor", items, []));
        store.CompleteGeneration(gen, "cursor", true);
        var ids = new[] { Id(2), Id(3), Id(4), Id(99) };
        store.JournalElapsedForTest = () => TimeSpan.Zero;
        var batch = store.JournalObjects(root.RootId, ids, default);
        LocalChecks.Require(ids.All(id => batch[id].Item == store.ByObject(root.RootId, id) &&
            batch[id].MultipleNames == store.HasMultipleNames(root.RootId, id)), "journal page matches scalar lookup including misses and aliases");
        await ResolveAsync(store, root);
        store.JournalElapsedForTest = () => JournalReadBudget.StoreTime;
        LocalChecks.Require(store.JournalObjects(root.RootId, ids, default).Count == 1,
            "elapsed store slice yields after one identity, including missing identities");
        store.JournalElapsedForTest = null;
        Plans(folder);
    }

    private static async Task ResolveAsync(SqliteStateStore store, RootDef root)
    {
        var records = Enumerable.Range(0, JournalReadBudget.Records * 2 + 1)
            .Select(n => new NtfsUsnChange((ulong)(1000 + n), 999, n, 1, FileAttributes.Normal, "")).ToArray();
        // Put a case-only rename across a page boundary: finalization must remain whole-batch.
        records[JournalReadBudget.Records - 1] = new(3, 2, 1, 0x1000, FileAttributes.Normal, "Old");
        records[JournalReadBudget.Records] = new(3, 2, 2, 0x2000, FileAttributes.Normal, "old");
        records[^2] = new(77, 1, 3, 0x2000, FileAttributes.Directory, "incoming");
        records[^1] = new(4, 1, 4, 0x10000, FileAttributes.Normal, "alias-c");
        var counters = new LocalCatchUpCounters(store);
        var lookup = new LocalJournalLookup(store, root, counters);
        var scalarDeferred = new List<string?>();
        var batchedDeferred = new List<string?>();
        ResolveContext Context(bool batched) => new(1, 1,
            batched ? lookup.ById : id => store.ByObject(root.RootId, Id(id)), 100,
            (_, _, _) => null,
            (id, _) => batched ? lookup.MultipleNames(Id(id)) : store.HasMultipleNames(root.RootId, Id(id)),
            Defer: (_, _, scope, _) => (batched ? batchedDeferred : scalarDeferred).Add(scope),
            PreparePage: batched ? lookup.Prepare : null);
        var scalar = await LocalDeltaResolver.ResolveAsync(records, Context(false), default);
        var batched = await LocalDeltaResolver.ResolveAsync(records, Context(true), default);
        LocalChecks.Require(scalar is not null && batched is not null && scalar.SequenceEqual(batched) &&
            scalarDeferred.SequenceEqual(batchedDeferred), "paged resolution exactly matches scalar paths and hard-link holds");
        LocalChecks.Require(batched!.Contains("parent") && batched!.Contains("incoming") && batchedDeferred.Contains("alias-c"),
            "cross-page case-only rename, move into root and hard-link scope survive batching");
        LocalChecks.Require(counters.ObjectBatches == (records.Length + JournalReadBudget.Records - 1) / JournalReadBudget.Records,
            "one store admission per record page when the time cap is not reached");
        // Forced early yield must assemble the full page, not reinterpret its unread suffix as absent.
        store.JournalElapsedForTest = () => JournalReadBudget.StoreTime;
        lookup.Prepare([records[^2], records[^1]], default);
        LocalChecks.Require(lookup.ById(4) == store.ByObject(root.RootId, Id(4)) && lookup.MultipleNames(Id(4)),
            "early-yield continuation retains alias evidence");
    }

    private static void Plans(string folder)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "v2", "state.db") }.ToString());
        db.Open();
        foreach (var sql in new[] { FileStateReader.ByObjectSql, SqliteStateStore.JournalMultipleNamesSql,
            SqliteStateStore.AcknowledgeRehashIdentitySql })
        {
            using var cmd = StoreSchema.Command(db, null, "EXPLAIN QUERY PLAN " + sql);
            cmd.Parameters.AddWithValue("$r", 1); cmd.Parameters.AddWithValue("$o", Id(4));
            using var reader = cmd.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            LocalChecks.Require(plan.Any(line => line.Contains("fs_obj (root_no=? AND object_id=?)")) &&
                !plan.Any(line => line.Contains("SCAN ") || line.Contains("PRIMARY KEY (root_no=?)")),
                "journal metadata probes seek fs_obj rather than scan a root");
        }
    }

    private static string Id(ulong id) => SyncLocalEndpointStore.ObjectId(1, id);
    private static ItemState Item(string path, ulong id, ItemKind kind = ItemKind.File) =>
        new(path, kind, Id(id), null, null, 0, DateTimeOffset.UnixEpoch, ItemFlags.None);
}
