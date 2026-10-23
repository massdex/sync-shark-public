using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void DeferredAliasContinuationChecks(string root)
    {
        var folder = Fresh(root, "deferred-alias-continuation");
        var pair = Pair("p1");
        var aliases = Enumerable.Range(0, 2100).Select(n => new ItemState($"Aliases/{n:D5}", ItemKind.File,
            "shared-identity", null, new string('a', 64), 1, T0, ItemFlags.None)).ToArray();
        using (var store = SqliteStateStore.Open(folder, Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(pair);
            var gen = store.BeginGeneration(pair.A.RootId, "fixture");
            store.WriteSnapshot(new(pair.A.RootId, Side.A, gen, "cursor", aliases, []));
            store.DeferPlanning(pair.PairId, [(pair.A.RootId, aliases[0])]);
        }
        using (var connection = new SqliteConnection($"Data Source={SqliteStateStore.DatabasePath(folder)};Pooling=False"))
        {
            connection.Open();
            foreach (var column in new[] { "alias_dir", "alias_key", "alias_name", "alias_unsettled" })
                StoreSchema.Execute(connection, null, $"ALTER TABLE planning_deferred DROP COLUMN {column};");
            StoreSchema.Execute(connection, null, "PRAGMA user_version=25;");
        }
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var run = 0; run < 4; run++)
        {
            using var store = SqliteStateStore.Open(folder, Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var preview = store.AdmitDeferredPlanning(pair.PairId, DateTime.UtcNow.Ticks, rotate: false);
            var repeat = store.AdmitDeferredPlanning(pair.PairId, DateTime.UtcNow.Ticks, rotate: false);
            Require(preview.SelectMany(x => x.Paths).SequenceEqual(repeat.SelectMany(x => x.Paths)),
                "v25 to v26 migration retains debt; repeated previews never advance its alias cursor");
            var page = store.AdmitDeferredPlanning(pair.PairId, DateTime.UtcNow.Ticks);
            Require(page.Sum(x => x.Paths.Count) <= 1024, "alias continuation stays within each run's path budget");
            observed.UnionWith(page.SelectMany(x => x.Paths));
            store.AcknowledgeDeferredPlanning(pair.PairId, page, _ => true);
        }
        using (var store = SqliteStateStore.Open(folder, Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            Require(observed.Count == 2100 && store.CountDeferredPlanning(pair.PairId) == 0,
                "cold-reopened bounded slices reach every alias and retire only the full settled cycle");
            store.DeferPlanning(pair.PairId, [(pair.A.RootId, aliases[0])]);
            var stale = store.AdmitDeferredPlanning(pair.PairId, DateTime.UtcNow.Ticks);
            store.DeferPlanning(pair.PairId, [(pair.A.RootId, aliases[0])]);
            store.AcknowledgeDeferredPlanning(pair.PairId, stale, _ => true);
            var fresh = store.AdmitDeferredPlanning(pair.PairId, DateTime.UtcNow.Ticks, rotate: false);
            Require(fresh.Single().AliasDir == -1, "new observation resets aliases and rejects stale acknowledgement");
            for (var run = 0; run < 3; run++)
            {
                var page = store.AdmitDeferredPlanning(pair.PairId, DateTime.UtcNow.Ticks);
                store.AcknowledgeDeferredPlanning(pair.PairId, page, p => p != aliases[0].Path);
            }
            Require(store.CountDeferredPlanning(pair.PairId) == 1,
                "a failed alias keeps entity debt across a full cycle while later aliases still progress");
        }
    }
}
