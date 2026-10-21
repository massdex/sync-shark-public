namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ConflictRestoreReviewChecksAsync()
    {
        // A crash after a create but before its receipt must not poison ordinary reconciliation.
        foreach (var kind in new[] { ActionKind.MkDir, ActionKind.CopyNew })
        {
            var (store, fx, notice) = await FolderReplaceAsync("abandon-" + kind, side => side.Seed("new/old.txt", "existing"));
            using var _ = store;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done, "prepare interrupted restore");
            var saved = store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice;
            var removal = saved.ConflictOperation!.Actions.Single();
            var root = fx.A.Seed("new", "", ItemKind.Folder);
            var item = kind == ActionKind.MkDir ? root : fx.A.Seed("new/old.txt", "existing");
            var intent = new SyncAction(ShortId.Make("nrx", fx.Pair.PairId, removal.ActionId + (kind == ActionKind.MkDir ? ":root" : ":" + store.ChoiceVictims(removal.ActionId, ChoiceVictimState.Removed, 256, false).Single().Ord)), 87, kind, Side.A, item.Path, null, item, null, false, null)
                { ConflictRestore = true, SuppressReconciledBaseline = true };
            store.RecordIntent(fx.Pair.PairId, intent);
            store.SaveConflictOperation(fx.Pair.PairId, notice.NoticeId!, saved.ConflictOperation with { Restoring = true });
            fx.B.Seed("outside.txt", "still syncing");
            store.MarkFirstSyncDone(fx.Pair.PairId);
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(store.IntentById(fx.Pair.PairId, intent.ActionId) is null &&
                await HashAt(fx.A, "outside.txt") == HashOf("still syncing"), "normal sync abandons receipt-less " + kind + " without stalling");
            var open = store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value;
            Require(!open.Closed && NoticeChoices.Options(open.Notice).Any(option => option.Choice == NoticeChoices.RestoreAsCopy) &&
                store.Recoveries(fx.Pair.PairId).Count == 1, "abandon keeps Recovery and notice options");
            Require((await fx.A.ObserveAsync(item.Path, CancellationToken.None))?.ObjectId == item.ObjectId,
                "abandon never removes or adopts the unverified created item");
            // Dismiss also settles a pending create without requiring a preceding normal run.
            store.RecordIntent(fx.Pair.PairId, intent);
            var unrelated = intent with { ActionId = "another-notice-restore", Path = "elsewhere" };
            store.RecordIntent(fx.Pair.PairId, unrelated);
            Require((await Choose(fx, notice, NoticeChoices.Dismiss)).Outcome == Outcome.Done &&
                store.IntentById(fx.Pair.PairId, intent.ActionId) is null &&
                store.IntentById(fx.Pair.PairId, unrelated.ActionId) is not null, "Dismiss abandons only its own pending restore create");
        }
        // Both sides of the crash boundary: receipt-owned copies resume; unreceipted
        // root/child occupants force a fresh sibling, never adoption based on matching bytes.
        foreach (var committed in new[] { false, true })
        foreach (var childCrash in new[] { false, true })
        {
            var (store, fx, notice) = await FolderReplaceAsync($"copy-crash-{committed}-{childCrash}", side => side.Seed("new/old.txt", "existing"));
            using var _ = store;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done, "replace before copy interruption");
            var op = store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice.ConflictOperation!;
            var removal = op.Actions.Single();
            var row = store.ChoiceVictims(removal.ActionId, ChoiceVictimState.Removed, 256, false).Single();
            const string copy = "new (restored copy interrupted)";
            store.SaveConflictOperation(fx.Pair.PairId, notice.NoticeId!, op with { Restoring = true, RestorePath = copy });
            var generation = store.BeginGeneration(fx.Pair.A.RootId, "copy-interrupted");
            var root = fx.A.Seed(copy, "", ItemKind.Folder);
            var rootKey = ShortId.Make("nrx", fx.Pair.PairId, removal.ActionId + ":root:copy:" + copy);
            var rootAction = new SyncAction(rootKey, 87, ActionKind.MkDir, Side.A, copy, null, root, null, false, null)
                { ConflictRestore = true, SuppressReconciledBaseline = true };
            store.RecordIntent(fx.Pair.PairId, rootAction);
            if (committed || childCrash)
                store.CommitResult(fx.Pair.PairId, generation, rootAction, new(rootKey, Outcome.Done, root, null, null, null));
            if (childCrash)
            {
                var child = fx.A.Seed(copy + "/old.txt", "existing");
                var childKey = ShortId.Make("nrx", fx.Pair.PairId, removal.ActionId + ":" + row.Ord + ":copy:" + copy);
                var childAction = new SyncAction(childKey, 87, ActionKind.CopyNew, Side.A, child.Path, null, child, null, false, null)
                    { ConflictRestore = true, SuppressReconciledBaseline = true };
                store.RecordIntent(fx.Pair.PairId, childAction);
                if (committed) store.CommitResult(fx.Pair.PairId, generation, childAction, new(childKey, Outcome.Done, child, null, null, null));
            }
            Require((await Choose(fx, notice, NoticeChoices.RestoreAsCopy)).Outcome == Outcome.Done, "interrupted copy can finish");
            var destination = store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice.ConflictOperation!.RestorePath!;
            Require((destination == copy) == committed && await HashAt(fx.A, destination + "/old.txt") == HashOf("existing"),
                "receipt identity resumes same destination; otherwise use fresh sibling");
            Require((await fx.A.ObserveAsync(copy, default))?.ObjectId == root.ObjectId,
                "retry never overwrites or removes interrupted occupant");
        }
        // More than one bounded page and more than one depth must restore shallowest-first.
        {
            var (store, fx, notice) = await FolderReplaceAsync("copy-many-depths", side =>
            {
                side.Seed("new/sub", "", ItemKind.Folder);
                side.Seed("new/sub/deep", "", ItemKind.Folder);
                for (var i = 0; i < 260; i++) side.Seed($"new/sub/f{i:D3}.txt", "saved " + i);
                side.Seed("new/sub/deep/last.txt", "deep bytes");
            });
            using var _ = store;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done, "replace multipage folder");
            Require((await Choose(fx, notice, NoticeChoices.RestoreAsCopy)).Outcome == Outcome.Done, "restore multipage folder");
            var path = store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice.ConflictOperation!.RestorePath!;
            for (var i = 0; i < 260; i++)
                Require(await HashAt(fx.A, $"{path}/sub/f{i:D3}.txt") == HashOf("saved " + i), "every page restores exact bytes");
            Require(await HashAt(fx.A, path + "/sub/deep/last.txt") == HashOf("deep bytes"), "deep restore follows parents");
        }
        // An occupied destination is a no-op until the user explicitly chooses a separate copy.
        {
            var (store, fx, notice) = await FolderReplaceAsync("occupied-copy", side =>
            {
                side.Seed("new/sub", "", ItemKind.Folder);
                side.Seed("new/sub/old.txt", "old bytes");
            });
            using var _ = store;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done, "replace before occupied restore");
            var occupant = fx.A.Seed("new", "", ItemKind.Folder);
            fx.A.Seed("new/in.txt", "incoming");
            var before = store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice.ConflictOperation!;
            var blocked = await Choose(fx, notice, NoticeChoices.RestoreExisting);
            var after = store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice.ConflictOperation!;
            Require(blocked.Outcome == Outcome.Skipped && blocked.Reason!.Contains("Restore as a copy") &&
                !after.Restoring && after.RestorePath is null && before.Completed.SequenceEqual(after.Completed),
                "blocked Restore leaves the manifest and hold state unchanged and offers alternatives");
            var copied = await Choose(fx, notice, NoticeChoices.RestoreAsCopy);
            var copyPath = store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice.ConflictOperation!.RestorePath!;
            Require(copied.Outcome == Outcome.Done && copyPath.StartsWith("new (restored copy ", StringComparison.Ordinal) &&
                await HashAt(fx.A, copyPath + "/sub/old.txt") == HashOf("old bytes"), "copy restores nested bytes at a unique clear sibling name");
            Require((await fx.A.ObserveAsync("new", CancellationToken.None))?.ObjectId == occupant.ObjectId &&
                await HashAt(fx.A, "new/in.txt") == HashOf("incoming"), "copy never overwrites the arrived incoming item");
            Require(store.Recoveries(fx.Pair.PairId).Count == 1, "copy does not consume Recovery");
        }
        // An unfinished copy owns its sibling, never the original incoming path.
        {
            var (store, fx, notice) = await FolderReplaceAsync("copy-does-not-hold", side => side.Seed("new/old.txt", "old"));
            using var _ = store;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done, "replace before copy hold check");
            var operation = store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice.ConflictOperation!;
            store.SaveConflictOperation(fx.Pair.PairId, notice.NoticeId!, operation with { Restoring = true, RestorePath = "separate-copy" });
            store.MarkFirstSyncDone(fx.Pair.PairId);
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(await HashAt(fx.A, "new/in.txt") == HashOf("incoming") &&
                !store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Closed,
                "incoming content arrives while the copy remains resumable");
        }
        // A blocked click must not stop the incoming folder's remaining children from arriving.
        {
            var (store, fx, notice) = await FolderReplaceAsync("blocked-still-arrives", side => side.Seed("new/old.txt", "old"));
            using var _ = store;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done, "replace before blocked click");
            fx.A.Seed("new", "", ItemKind.Folder);
            Require((await Choose(fx, notice, NoticeChoices.RestoreExisting)).Outcome == Outcome.Skipped, "occupied root blocks restore");
            store.MarkFirstSyncDone(fx.Pair.PairId);
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(await HashAt(fx.A, "new/in.txt") == HashOf("incoming"), "blocked restore does not hold incoming content");
        }
        // Root and child receipts survive a stop after commit but before marking the frozen child Restored.
        {
            var (store, fx, notice) = await FolderReplaceAsync("receipt-resume", side => side.Seed("new/old.txt", "existing"));
            using var _ = store;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done, "replace before receipt resume");
            var saved = store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice;
            var removal = saved.ConflictOperation!.Actions.Single();
            var row = store.ChoiceVictims(removal.ActionId, ChoiceVictimState.Removed, 256, false).Single();
            var gen = store.BeginGeneration(fx.Pair.A.RootId, "restore-checkpoint");
            var rootId = ShortId.Make("nrx", fx.Pair.PairId, removal.ActionId + ":root");
            var childId = ShortId.Make("nrx", fx.Pair.PairId, removal.ActionId + ":" + row.Ord);
            var root = fx.A.Seed("new", "", ItemKind.Folder);
            var child = fx.A.Seed("new/old.txt", "existing");
            foreach (var (id, item, kind) in new[] { (rootId, root, ActionKind.MkDir), (childId, child, ActionKind.CopyNew) })
            {
                var action = new SyncAction(id, 87, kind, Side.A, item.Path, null, item, null, false, null)
                    { ConflictRestore = true, SuppressReconciledBaseline = true };
                store.RecordIntent(fx.Pair.PairId, action);
                store.CommitResult(fx.Pair.PairId, gen, action, new(id, Outcome.Done, item, item, null, null));
            }
            var writes = fx.A.WriteCalls;
            Require((await Choose(fx, notice, NoticeChoices.RestoreExisting)).Outcome == Outcome.Done && fx.A.WriteCalls == writes,
                "receipt-positive resume does not repeat committed root or file creates");
            Require(store.ConflictRestoreReceipt(fx.Pair.PairId, rootId) is null && store.ConflictRestoreReceipt(fx.Pair.PairId, childId) is null,
                "resolved restore reclaims its root and child receipts");
        }
        // Ignored source descendants do not require impossible sync links to close the notice.
        {
            var (store, fx, notice) = await FolderReplaceAsync("ignored-arrival", side => side.Seed("new/old.txt", "existing"));
            using var _ = store;
            fx.B.Seed("new/desktop.ini", "ignored");
            store.SavePair(fx.Pair with { IgnoreRules = ["FileName:desktop.ini"] });
            await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done, "replace before ignored arrival");
            store.MarkFirstSyncDone(fx.Pair.PairId);
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(await HashAt(fx.A, "new/in.txt") == HashOf("incoming") && await HashAt(fx.A, "new/desktop.ini") is null &&
                store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Closed, "syncable contents alone prove full arrival");
        }
        // Owner default: a frozen child deleted at source is settled, not an impossible arrival.
        {
            var (store, fx, notice) = await FolderReplaceAsync("missing-syncable-child", side => side.Seed("new/old.txt", "existing"));
            using var _ = store;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done, "replace before missing child");
            var child = await fx.B.ObserveAsync("new/in.txt", CancellationToken.None);
            await fx.B.DeleteAsync(new SyncAction("source-child-gone", 6, ActionKind.Delete, Side.B, child!.Path, null, null, child, false, null), CancellationToken.None);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Closed && await HashAt(fx.A, "new/in.txt") is null,
                "a source-deleted frozen child is settled without pretending its bytes arrived");
        }
        // A carried authorization must be revoked when today's exclusion protects that child.
        {
            var (store, fx, notice) = await FolderReplaceAsync("carried-now-excluded", side => side.Seed("new/desktop.ini", "keep"));
            using var _ = store;
            var victim = notice.ConflictVersions!.A;
            var action = new SyncAction("carried-excluded", 14, ActionKind.Delete, Side.A, victim.Path, null, null, victim, false, null)
                { SuppressReconciledBaseline = true };
            store.SavePair(fx.Pair with { IgnoreRules = ["FileName:desktop.ini"], Excluded = ["new/desktop.ini"] });
            store.SaveConflictOperation(fx.Pair.PairId, notice.NoticeId!, new(NoticeChoices.ReplaceExisting, null, [action], []));
            var rows = store.ChoiceVictims(action.ActionId, ChoiceVictimState.Pending, 256, false);
            store.MarkChoiceVictims(action.ActionId, rows.Select(row => (row.Ord, ChoiceVictimState.Carried)).ToArray());
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome != Outcome.Done &&
                store.CountChoiceVictims(action.ActionId, ChoiceVictimState.Kept) == 1 &&
                await HashAt(fx.A, "new/desktop.ini") == HashOf("keep"), "changed exclusion re-marks carried child Kept without deleting it");
        }
    }
}
