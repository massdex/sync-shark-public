using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

internal static class ProtectionCheckpointChecks
{
    internal static void Run(string folder)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = new PairDef("pair", "Pair A", new("left", SyncProviders.Local, null, "/left", "left"),
            new("right", SyncProviders.Local, null, "/right", "right"), [], [], true);
        store.SavePair(pair);
        var item = new ItemState("Parent/File", ItemKind.File, "entity", null, new string('a', 64), 1,
            DateTimeOffset.UnixEpoch, ItemFlags.NeedsRehash);
        var gen = store.BeginGeneration(pair.A.RootId, "fixture");
        store.WriteSnapshot(new(pair.A.RootId, Side.A, gen, "cursor", [item], []));
        store.SaveSyncRetry(pair.PairId, new("Retry/File", "sync", 1, "pending", DateTimeOffset.MaxValue));
        using var frozen = store.OpenPlanningProtection(pair);
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = SqliteStateStore.DatabasePath(folder), Pooling = false }.ToString());
        writer.Open();
        using var command = writer.CreateCommand();
        command.CommandText = "UPDATE file_state SET flags=0; DELETE FROM sync_retry;";
        command.ExecuteNonQuery();
        Checkpoint(command);
        LocalChecks.Require(frozen.Read([item.Path])[0].Covered &&
            frozen.RetryProtected(pair.PairId, "Retry/File", false, new HashSet<string>(), false),
            "released main snapshot retains exact held and retry keys after live release");
        using var stream = frozen.Stream();
        LocalChecks.Require(stream.Read([item.Path])[0].Covered, "stream reads the same materialized holds");
        // The fallback must also stay on the frozen TEMP inputs, not re-open live state.
        using var fallback = frozen.Stream();
        LocalChecks.Require(fallback.SourcePasses == 0 && fallback.Read([item.Path])[0].Covered, "failed stream preserves frozen exact fallback");
        Checkpoint(command);
        command.CommandText = "UPDATE file_state SET flags=256;";
        command.ExecuteNonQuery();
        gen = store.BeginGeneration(pair.B.RootId, "fixture");
        store.WriteSnapshot(new(pair.B.RootId, Side.B, gen, "cursor", [item with { ObjectId = "other", Flags = ItemFlags.None }], []));
        var scope = new RunScope(store, _ => true, _ => true, LinkExclusions.Of(store, pair, false));
        scope.AfterHideAForTest = () => { command.CommandText = "UPDATE file_state SET flags=0;";
            command.ExecuteNonQuery(); Checkpoint(command); };
        var result = scope.LoadFull(pair);
        LocalChecks.Require(result.A.Count == 0 && result.B.Count == 0,
            "RunScope retains both sides of the protected path after live release between sides");
    }

    internal static void Checkpoint(SqliteCommand command)
    {
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var result = command.ExecuteReader();
        LocalChecks.Require(result.Read() && result.GetInt64(0) == 0 && result.GetInt64(1) == 0 &&
            result.GetInt64(2) == 0, "main WAL checkpoints while protection scope remains alive");
    }
}
