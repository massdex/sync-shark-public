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
                V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var root = LocalChecks.RootOf(directory, "local-review");
            var other = root with { RootId = "other" };
            var pair = new PairDef("removal-pair", "Pair A", side == Side.A ? root : other,
                side == Side.B ? root : other, [], [], true);
            store.SavePair(pair);
            var inventory = new LocalInventory(root, side, store, new HashSet<string>(), V2Settings.Default, TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage)
                { GapIo = new LocalGapIo(store, V2Settings.Default, new HashSet<string>(), () => false) };
            var rows = new List<ItemState>();
            await inventory.ReadSubtreeAsync("reviewed", page => { rows.AddRange(page); return Task.CompletedTask; }, default);
            var folder = rows.Single(item => item.Path == "reviewed");
            LocalChecks.Require(rows.Single(item => item.Kind == ItemKind.File).Hash ==
                LocalChecks.BlockHash(LocalChecks.Bytes("previously unindexed bytes")), "real local read proves never-indexed child through VERIFY");
            var action = new SyncAction("remove", 40, ActionKind.Delete, side, folder.Path,
                null, null, folder, false, null) { RemovalReviewChoice = true };
            var signature = await DeleteSourceGuard.CaptureReviewedRemovalAsync(action, pair, store, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage);
            LocalChecks.Require(signature is not null, "real local producer yields complete baseline");
            action = action with { FrozenRemovalSignature = signature };
            LocalChecks.Require(await DeleteSourceGuard.ReviewedRemovalUnchangedAsync(action, pair, store, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage),
                "unchanged real local tree passes on " + side);
            LocalChecks.Seed(directory, "reviewed/new.txt", "unreviewed");
            LocalChecks.Require(!await DeleteSourceGuard.ReviewedRemovalUnchangedAsync(action, pair, store, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage),
                "real local new child vetoes on " + side);
            var consentPair = pair with { IgnoreRules = ["FileExtension:tmp"] };
            store.SavePair(consentPair);
            var contentReads = 0;
            inventory.GapIo = new LocalGapIo(store, V2Settings.Default, new HashSet<string>(), () => false)
                { AfterContentReadForCheck = _ => contentReads++ };
            LocalChecks.Seed(directory, "consented/original.tmp", "data");
            var consentRows = new List<ItemState>();
            await inventory.ReadSubtreeAsync("consented", page => { consentRows.AddRange(page); return Task.CompletedTask; }, default);
            var ordinaryHashes = await inventory.HashAsync(["consented/original.tmp"], default);
            LocalChecks.Require(contentReads == 0 && ordinaryHashes.Count == 0 &&
                consentRows.Single(item => item.Kind == ItemKind.File).Hash is null,
                "without reviewed removal, ignored children receive no content work on " + side);
            var proposed = new SyncAction("consent", 23, ActionKind.Delete, side, "consented", null, null,
                consentRows.Single(item => item.Path == "consented"), true, null)
                { CarriedIgnoredBudget = new CarriedIgnoredContent(1, 0, 4), FrozenVictimSignature = FolderDelete.Signature([]) };
            var consent = await DeleteSourceGuard.RecordRemovalConsentAsync(proposed, consentPair, store, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage);
            LocalChecks.Require(contentReads > 0, "reviewed enclosing removal proves its carried ignored bytes on " + side);
            LocalChecks.Require(consent.RemovalReviewChoice && consent.FrozenRemovalSignature is not (null or "unproven") &&
                await DeleteSourceGuard.ReviewedRemovalUnchangedAsync(consent, consentPair, store, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage),
                "consent freezes complete carried observation together with authority on " + side);
            var reviewedReads = contentReads;
            LocalChecks.Require((await inventory.HashAsync(["consented/original.tmp"], default)).Count == 0 &&
                contentReads == reviewedReads, "reviewed proof scope cannot leak into ordinary content work on " + side);
            LocalChecks.Seed(directory, "sibling.tmp", "outside the reviewed folder");
            await inventory.ReadSubtreeAsync("consented", async _ =>
            {
                var beforeSibling = contentReads;
                var siblingRows = new List<ItemState>();
                await inventory.ReadSubtreeAsync("sibling.tmp", page =>
                { siblingRows.AddRange(page); return Task.CompletedTask; }, default);
                LocalChecks.Require(contentReads == beforeSibling && siblingRows.Single().Hash is null,
                    "an ignored sibling outside the active reviewed folder receives no content read on " + side);
            }, default, progress: null, reviewedRemoval: consentPair);
            var plannedConsent = await DeleteSourceGuard.RecordRemovalConsentAsync(
                proposed with { ActionId = "planned-consent", DescendantsPlanned = true, PreserveIgnoredChildren = false },
                consentPair, store, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage);
            LocalChecks.Require(DeleteSourceGuard.RequiresReviewedSubtreeProof(plannedConsent) &&
                plannedConsent.FrozenRemovalSignature is not (null or "unproven") &&
                await DeleteSourceGuard.ReviewedRemovalUnchangedAsync(plannedConsent, consentPair, store, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage),
                "separately planned folder freezes carried children on " + side);
            LocalChecks.Seed(directory, "consented/late.tmp", "later");
            LocalChecks.Require(!await DeleteSourceGuard.ReviewedRemovalUnchangedAsync(plannedConsent, consentPair, store, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage),
                "separately planned folder holds ignored child added after consent on " + side);
            LocalChecks.Require(!await DeleteSourceGuard.ReviewedRemovalUnchangedAsync(consent, consentPair, store, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage),
                "carried file added after consent holds whole removal on " + side);
            var incompleteConsent = await DeleteSourceGuard.RecordRemovalConsentAsync(proposed, consentPair, store, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage);
            LocalChecks.Require(incompleteConsent.FrozenRemovalSignature == "unproven",
                "undisclosed carried contents cannot acquire a consent signature on " + side);
            // Reviewed proof cannot turn a link or online-only child into deletion authority.
            LocalChecks.Seed(directory, "outside/target.txt", "never read through the link");
            Directory.CreateDirectory(Path.Combine(directory, "unsafe"));
            var junction = Path.Combine(directory, "unsafe", "link");
            var stubPath = Path.Combine(directory, "unsafe", "stub.txt");
            LocalChecks.Seed(directory, "unsafe/stub.txt", "offline bytes");
            LocalChecks.CreateJunction(junction, Path.Combine(directory, "outside"));
            var attributes = File.GetAttributes(stubPath);
            File.SetAttributes(stubPath, attributes | FileAttributes.Offline);
            try
            {
                var beforeUnsafe = contentReads;
                var unsafeRows = new List<ItemState>();
                await inventory.ReadSubtreeAsync("unsafe", page =>
                { unsafeRows.AddRange(page); return Task.CompletedTask; }, default, progress: null, reviewedRemoval: consentPair);
                LocalChecks.Require(contentReads == beforeUnsafe &&
                    unsafeRows.Any(item => item.Path == "unsafe/link" && (item.Flags & ItemFlags.Link) != 0) &&
                    unsafeRows.Any(item => item.Path == "unsafe/stub.txt" && (item.Flags & ItemFlags.Placeholder) != 0) &&
                    unsafeRows.All(item => item.Hash is null),
                    "reviewed link and stub children get no content reads or hashes on " + side);
                var unsafeAction = proposed with { Path = "unsafe", ExpectedTarget = unsafeRows.Single(item => item.Path == "unsafe"),
                    RemovalReviewChoice = true };
                LocalChecks.Require(await DeleteSourceGuard.CaptureReviewedRemovalAsync(unsafeAction, consentPair, store,
                    inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage) is null &&
                    !await DeleteSourceGuard.ReviewedRemovalUnchangedAsync(unsafeAction with { FrozenRemovalSignature = "unproven" },
                        consentPair, store, inventory, default, isStateDamage: StorageFailureClassifier.IsStateDamage) &&
                    contentReads == beforeUnsafe,
                    "link/stub removal remains unproven and refused without content reads on " + side);
            }
            finally
            {
                File.SetAttributes(stubPath, attributes);
                LocalChecks.RemoveJunction(junction);
            }
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
