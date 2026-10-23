namespace Massdex.SyncShark.V2;

internal static partial class LocalInventoryChecks
{
    private static async Task RemovalGuardBothSidesAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        {
            var directory = LocalChecks.FreshRoot("removal-proof-" + side);
            LocalChecks.Seed(directory, "reviewed/nested/child.txt", "previously unindexed bytes");
            using var store = SqliteStateStore.Open(Path.Combine(LocalChecks.FreshRoot("removal-db-" + side), "state"),
                V2Settings.Default, TimeProvider.System);
            var root = LocalChecks.RootOf(directory, "local-review");
            var other = root with { RootId = "other" };
            var pair = new PairDef("removal-pair", "Pair A", side == Side.A ? root : other,
                side == Side.B ? root : other, [], [], true);
            store.SavePair(pair);
            var inventory = new LocalInventory(root, side, store, new HashSet<string>(), V2Settings.Default, TimeProvider.System)
                { GapIo = new LocalGapIo(store, V2Settings.Default, new HashSet<string>(), () => false) };
            var rows = new List<ItemState>();
            await inventory.ReadSubtreeAsync("reviewed", page => { rows.AddRange(page); return Task.CompletedTask; }, default);
            var folder = rows.Single(item => item.Path == "reviewed");
            LocalChecks.Require(rows.Single(item => item.Kind == ItemKind.File).Hash ==
                LocalChecks.BlockHash(LocalChecks.Bytes("previously unindexed bytes")), "real local read proves never-indexed child through VERIFY");
            var action = new SyncAction("remove", 40, ActionKind.Delete, side, folder.Path,
                null, null, folder, false, null) { RemovalReviewChoice = true };
            var signature = await DeleteSourceGuard.CaptureReviewedRemovalAsync(action, pair, store, inventory, default);
            LocalChecks.Require(signature is not null, "real local producer yields complete baseline");
            action = action with { FrozenRemovalSignature = signature };
            LocalChecks.Require(await DeleteSourceGuard.ReviewedRemovalUnchangedAsync(action, pair, store, inventory, default),
                "unchanged real local tree passes on " + side);
            LocalChecks.Seed(directory, "reviewed/new.txt", "unreviewed");
            LocalChecks.Require(!await DeleteSourceGuard.ReviewedRemovalUnchangedAsync(action, pair, store, inventory, default),
                "real local new child vetoes on " + side);
            // Wide tree crosses page boundaries without opening content (empty directories).
            for (var i = 0; i < 513; i++) Directory.CreateDirectory(Path.Combine(directory, "wide", "d" + i));
            var count = 0;
            var maximum = 0;
            await inventory.ReadSubtreeAsync("wide", page =>
            { count += page.Count; maximum = Math.Max(maximum, page.Count); return Task.CompletedTask; }, default);
            LocalChecks.Require(count == 514 && maximum <= 256, "native local producer streams bounded pages on " + side);
        }
    }
}
