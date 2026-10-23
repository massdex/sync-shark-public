namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task ReviewedRemovalRootListingAsync()
    {
        // Synthetic HTTP shapes follow the existing production root-skip predicate documented
        // in dropbox-inventory.md, not a live account recording.
        foreach (var side in new[] { Side.A, Side.B })
        foreach (var copies in new[] { 0, 1, 3 })
        foreach (var different in new[] { false, true })
        {
            if (copies == 0 && different) continue;
            using var fixture = new Harness();
            fixture.Wire.SeedFolder("reviewed");
            fixture.Wire.SeedFile("reviewed/child.txt", "content");
            var root = Root();
            var other = root with { RootId = "other" };
            var pair = new PairDef("removal-pair", "Pair A", side == Side.A ? root : other,
                side == Side.B ? root : other, [], [], true);
            var inventory = new DropboxInventory(root, side, fixture.Client, fixture.Known, pair.PairId);
            var folder = (await fixture.Writer().ObserveAsync("reviewed", default))!;
            var action = new SyncAction("reviewed", 40, ActionKind.Delete, side, folder.Path,
                null, null, folder, false, null) { RemovalReviewChoice = true };
            var signature = await DeleteSourceGuard.CaptureReviewedRemovalAsync(action, pair, fixture.Known, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage);
            LocalChecks.Require(signature is not null, "real Dropbox adapter captures baseline proof");
            action = action with { FrozenRemovalSignature = signature };
            fixture.Wire.ListedRootCopies = copies;
            fixture.Wire.ListedRootDifferentIdentity = different;
            fixture.Wire.DeletedPageSize = 1;
            var rows = new List<ItemState>();
            await inventory.ReadSubtreeAsync(folder.Path, page => { rows.AddRange(page); return Task.CompletedTask; }, default);
            LocalChecks.Require(different || rows.Count(item => item.Path == folder.Path) == 1,
                "metadata root emitted once despite zero, one or repeated list roots");
            LocalChecks.Require(await DeleteSourceGuard.ReviewedRemovalUnchangedAsync(action, pair, fixture.Known, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage) == !different,
                "matching listed roots never count as children; different identity at root path vetoes");
        }
    }
}
