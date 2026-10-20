namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task LocalPrepareCursorAsync(string folder)
    {
        string retained;
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
        {
            var fx = Build(suppliedStore: store);
            fx.A.Seed("matched.txt", "same");
            fx.B.Seed("matched.txt", "same");
            var initial = await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(initial.InventoryCursors.ContainsKey(fx.Pair.A.RootId),
                "first-sync prepare retains the local journal checkpoint, not only Dropbox's cursor");
            Require(store.Cursor(fx.Pair.A.RootId) is null, "prepare does not prematurely commit the successful local root cursor");
            // The pending checkpoint round-trips independently of the inventory/run instances.
            var cursors = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                System.Text.Json.JsonSerializer.Serialize(initial.InventoryCursors))!;
            var fullBefore = fx.A.FullCalls;
            var resumed = await fx.Run().PreviewFirstSyncApplyResolvedAsync(fx.Pair.PairId, null, null, [],
                null, new RecordingProgress(), CancellationToken.None, cursors);
            Require(fx.A.FullCalls == fullBefore && fx.A.DeltaCalls > 0,
                "first-sync resume reads a local delta instead of a full inventory");
            // The real run's invalid-cursor path must call FullAsync, not just set an advisory flag.
            cursors[fx.Pair.A.RootId] = "invalid-journal-gap";
            var recovered = await fx.Run().PreviewFirstSyncApplyResolvedAsync(fx.Pair.PairId, null, null, [],
                null, new RecordingProgress(), CancellationToken.None, cursors);
            Require(fx.A.FullCalls == fullBefore + 1 && recovered.Plan.InventoryCursors.ContainsKey(fx.Pair.A.RootId),
                "journal gap performs a full local scan and retains its replacement checkpoint");
            var applied = await fx.Run().ApplyAsync(fx.Pair.PairId, recovered.Plan, new RecordingProgress(), CancellationToken.None);
            retained = recovered.Plan.InventoryCursors[fx.Pair.A.RootId];
            Require(applied.Failed == 0 && store.Cursor(fx.Pair.A.RootId) == retained,
                "successful first sync promotes the local position to its root row");
        }
        using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        Require(reopened.Cursor("ra") == retained, "local root checkpoint survives SQLite reopen");
    }
}
