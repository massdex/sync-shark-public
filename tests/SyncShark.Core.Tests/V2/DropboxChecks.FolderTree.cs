namespace Massdex.SyncShark.V2;

/// <summary>
/// a large Dropbox folder Delete is one delete_batch entry, proven afterwards from the change feed;
/// anything added or changed after the scan is restored with a notice; a failed post-check stays Uncertain.
/// </summary>
internal static partial class DropboxChecks
{
    private static async Task FolderTreeChecksAsync()
    {
        await FolderTreeDispatchReappearedAsync(false);
        await FolderTreeDispatchReappearedAsync(true);
        await FolderTreeMissingProofAsync();
        await FolderTreeUnchangedAsync();
        await FolderTreeLargeGuardedAsync(false);
        await FolderTreeLargeGuardedAsync(true);
        await FolderTreeAddedAfterScanAsync();
        await FolderTreeEditedAfterScanAsync();
        await FolderTreeFeedFailureAsync();
        await FolderTreeBelowThresholdAsync();
        await FolderTreeRunsFirstAsync();
        await FolderTreePollAllowsUploadCommitAsync();
        await FolderTreeEditDuringJobNoticeAsync();
        await FolderTreeOccupiedRestoreAsync();
        await FolderTreeUnknownKindAsync();
        await FolderTreeRecoveryChecksAsync();
        await FolderTreeReviewFixChecksAsync();
    }

    private sealed record TreeRig(Harness H, InMemoryStateStore Store, Executor Executor, FakeEndpoint A,
        DropboxWriter B, SyncAction Delete, string Cursor) : IDisposable
    {
        public void Dispose() => H.Dispose();

        public Plan Plan(params SyncAction[] more) =>
            new("p1", 1, [Delete, .. more], Array.Empty<Notice>()) { ScanCursorB = Cursor };

        public async Task<List<ActionResult>> ApplyAsync(Plan plan)
        {
            var results = new List<ActionResult>();
            await foreach (var result in Executor.ApplyAsync(plan, A, B, CancellationToken.None)) results.Add(result);
            return results;
        }
    }

    // "big" holds one subfolder and `files` files; the store holds the scan, and the plan deletes "big" from Dropbox.
    private static async Task<TreeRig> TreeRigAsync(int files)
    {
        var h = new Harness();
        h.Wire.SeedFolder("big");
        h.Wire.SeedFolder("big/sub");
        for (var index = 0; index < files; index++)
            h.Wire.SeedFile((index % 2 == 0 ? "big/" : "big/sub/") + "f" + index + ".txt", "content " + index);
        var store = h.Known;
        store.SavePair(new PairDef("p1", "p1", new RootDef("ra", "Local", null, "C:\\a", "id:local:1"), Root(),
            Array.Empty<string>(), Array.Empty<string>(), true));
        var gen = store.BeginGeneration(RootId, "snapshot");
        var snapshot = await h.Inventory.FullAsync(gen, _ => true, Sink, CancellationToken.None);
        // The writer drains once more before deleting; its journal must start at that cursor,
        // not at the listing's former pre-walk cursor.
        h.Wire.FolderDeleteFeedCursor = snapshot.CursorAfter + "-end";
        store.WriteSnapshot(snapshot);
        store.CompleteGeneration(gen, snapshot.CursorAfter, true);
        var folder = snapshot.Items.Single(item => item.Path == "big");
        var delete = new SyncAction("del-big", 32, ActionKind.Delete, Side.B, "big", null, null, folder, false, null)
        {
            ChoiceRemove = true,
            FrozenVictimSignature = FolderDelete.Signature(FolderDelete.LiveDescendants(store, RootId, "big")),
        };
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"),
            V2Settings.Default with { DeleteBatchSize = 1, UploadBatchSize = 1, MkDirBatchSize = 1 });
        return new TreeRig(h, store, new Executor(store, V2Settings.Default, ExecutorChecks.Admission()),
            new FakeEndpoint(Side.A, "ra"), writer, delete, snapshot.CursorAfter);
    }

    private static async Task FolderTreeDispatchReappearedAsync(bool unknown)
    {
        using var rig = await TreeRigAsync(52);
        var source = new RecordingWriter(rig.A);
        rig.A.DropboxHashes = true;
        var folder = Path.Combine(_root, "folder-dispatch-" + Guid.NewGuid().ToString("N"));
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
        {
            store.SavePair(rig.Store.Pair("p1")!);
            var victims = FolderDelete.LiveDescendants(rig.Store, RootId, "big").Append(rig.Delete.ExpectedTarget!).ToArray();
            var gen = store.BeginGeneration(RootId, "snapshot");
            store.WriteSnapshot(new(RootId, Side.B, gen, rig.Cursor, victims, []));
            store.CompleteGeneration(gen, rig.Cursor, true);
            foreach (var victim in victims)
            {
                var bytes = victim.Kind == ItemKind.Folder ? "" : "content " + Path.GetFileNameWithoutExtension(victim.Path)[1..];
                var original = rig.A.Seed(victim.Path, bytes, victim.Kind);
                var adopt = new SyncAction("adopt-" + victim.ObjectId, 12, ActionKind.Adopt, Side.B,
                    victim.Path, null, original, victim, false, "baseline-" + victim.ObjectId);
                store.CommitResult("p1", gen, adopt, new(adopt.ActionId, Outcome.Done, original, victim, null, null));
                await rig.A.DeleteAsync(new("remove-" + original.ObjectId, 6, ActionKind.Delete, Side.A,
                    original.Path, null, null, original, false, null), default);
            }
            var recreated = false;
            rig.H.Wire.AfterContinueResponse = _ =>
            {
                if (recreated) return;
                recreated = true;
                if (unknown) source.AbsenceUnknown = true;
                else rig.A.Seed("big/f0.txt", "recreated during prepare");
            };
            var delete = rig.Delete with { Rule = 6, ChoiceRemove = false,
                ObjectKey = "baseline-" + rig.Delete.ExpectedTarget!.ObjectId };
            var results = new List<ActionResult>();
            var executor = new Executor(store, V2Settings.Default, ExecutorChecks.Admission());
            await foreach (var result in executor.ApplyAsync(rig.Plan() with { Actions = [delete] }, source, rig.B, default))
                results.Add(result);
            Require(recreated && rig.H.Wire.WholeFolderDeletes == 0,
                "prepare recheck prevents whole-folder submit after source recreation");
            Require(results.Any(result => result.DeleteSourceUnconfirmed) &&
                await rig.B.ObserveAsync("big/f0.txt", default) is not null &&
                (await rig.B.ObserveAsync("big/f2.txt", default) is null) == !unknown,
                "existing per-item fallback preserves recreated victim and deletes absent siblings");
        }
        using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        Require(reopened.SyncRetryAt("p1", "big/f0.txt") is not null,
            "prepare refusal retains per-item retry after cold reopen");
    }

    private static async Task FolderTreeLargeGuardedAsync(bool keepOneSource)
    {
        using var rig = await TreeRigAsync(140);
        var victims = FolderDelete.LiveDescendants(rig.Store, RootId, "big")
            .Append(rig.Delete.ExpectedTarget!).ToArray();
        foreach (var victim in victims)
        {
            var source = rig.A.Seed(victim.Path, "content", victim.Kind);
            var key = "baseline-" + victim.ObjectId;
            var adopt = new SyncAction("adopt-" + victim.ObjectId, 1, ActionKind.Adopt, Side.B,
                victim.Path, null, source, victim, false, key);
            rig.Store.CommitResult("p1", 1, adopt, new ActionResult(adopt.ActionId, Outcome.Done, source, victim, null, null));
            if (keepOneSource && victim.Path == "big/f0.txt") continue;
            await rig.A.DeleteAsync(new SyncAction("remove-" + source.ObjectId, 6, ActionKind.Delete, Side.A,
                source.Path, null, null, source, false, null), default);
        }
        var delete = rig.Delete with { Rule = 6, ChoiceRemove = false,
            ObjectKey = "baseline-" + rig.Delete.ExpectedTarget!.ObjectId };
        var results = await rig.ApplyAsync(rig.Plan() with { Actions = [delete] });
        if (!keepOneSource)
            Require(results.All(result => result.Outcome == Outcome.Done) && rig.H.Wire.WholeFolderDeletes == 1,
                "more than128 admitted actions retain whole-folder delete_batch after bounded source preflight");
        else
        {
            Require(rig.H.Wire.WholeFolderDeletes == 0 &&
                await rig.B.ObserveAsync("big/f0.txt", default) is not null &&
                await rig.B.ObserveAsync("big", default) is not null,
                "one present source excludes its child and every enclosing folder from destructive submission");
            Require(results.Any(result => result.Outcome == Outcome.Done) &&
                rig.H.Wire.Calls.Contains("/2/files/delete_batch"),
                "safe sibling files still use conditional delete_batch rather than blocking the whole tree");
        }
    }

    private static async Task FolderTreeMissingProofAsync()
    {
        using var rig = await TreeRigAsync(60);
        var tree = new FolderTree(rig.Delete.ExpectedTarget!, FolderDelete.LiveDescendants(rig.Store, RootId, "big"));
        var results = await rig.B.DeleteFolderTreesAsync(rig.Cursor, [tree], (_, _) => Task.CompletedTask, default);
        Require(results.All(result => result.Status == FolderTreeStatus.NotSubmitted) &&
            !rig.H.Wire.Calls.Contains("/2/files/delete_batch"),
            "whole-folder dispatch without callback is Unknown and sends no delete");
    }

    private static async Task FolderTreeUnchangedAsync()
    {
        using var rig = await TreeRigAsync(60);
        var results = await rig.ApplyAsync(rig.Plan());
        Require(results.Count == 62 && results.All(result => result.Outcome == Outcome.Done),
            "an unchanged large folder: every victim, its subfolder and the folder itself are Done");
        Require(rig.H.Wire.WholeFolderDeletes == 1 && rig.H.Wire.DeleteBatchSizes.SequenceEqual([1]) &&
            rig.H.Wire.LastDeleteBatchPaths.SequenceEqual([DropboxWire.RootId + "/big"]) &&
            rig.H.Wire.LastDeleteParentRev is null,
            "the folder is one id-rooted delete_batch entry without parent_rev");
        Require(results.Count(result => result.Recovery is not null) == 60,
            "each file keeps its Dropbox-history recovery reference, as on the per-item path");
        Require(!rig.H.Wire.Calls.Contains("/2/files/restore") && rig.Store.OpenNotices("p1").Count == 1,
            "nothing is restored; the group has its mandatory Dropbox-history notice");
        Require(rig.Store.UnresolvedIntents("p1").Count == 0, "every intent is resolved");
    }

    private static async Task FolderTreeAddedAfterScanAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.SeedFile("big/sub/late.txt", "added after the scan");
        var results = await rig.ApplyAsync(rig.Plan());
        Require(rig.H.Wire.Restores.SequenceEqual(["big/sub/late.txt"]) &&
            await rig.B.ObserveAsync("big/sub/late.txt", CancellationToken.None) is { Kind: ItemKind.File },
            "a file added under the folder after the scan is restored, with its parent folders");
        Require(results.Where(result => result.ActionId is "del-big" or "del-big:big/sub")
                .All(result => result.Outcome == Outcome.Skipped) &&
            results.Count(result => result.Outcome == Outcome.Done) == 60,
            "the folders the restore brought back are not Done; every planned file is");
        var notice = rig.Store.OpenNotices("p1").Single(notice => notice.ReasonKey == ExecutionReasons.FolderDeleteRestored.Key);
        Require(notice.ReasonKey == ExecutionReasons.FolderDeleteRestored.Key && notice.Path == "big" &&
            notice.Detail.Contains("big/sub/late.txt", StringComparison.Ordinal),
            "a stored notice names the restored path");
    }

    private static async Task FolderTreeEditedAfterScanAsync()
    {
        using var rig = await TreeRigAsync(60);
        var latest = rig.H.Wire.EditFile("big/f0.txt", "edited after the scan");
        // The feed from the scan cursor shows the edit before the delete; the delete's own entries follow it.
        rig.H.Wire.SeedChangePage(rig.Cursor, [rig.H.Wire.ChangeOf("big/f0.txt")], false, "after-edit");
        rig.H.Wire.FolderDeleteFeedCursor = "after-edit";
        var results = await rig.ApplyAsync(rig.Plan());
        Require(rig.H.Wire.Restores.SequenceEqual(["big/f0.txt"]) &&
            (await rig.B.ObserveAsync("big/f0.txt", CancellationToken.None))?.VersionId == latest,
            "a file edited after the scan is restored to its latest revision");
        Require(results.Single(result => result.ActionId == "del-big:big/f0.txt").Outcome == Outcome.Skipped &&
            results.Count(result => result.Outcome == Outcome.Done) == 60,
            "the edited victim is not Done; the other files and the subfolder are");
        Require(rig.Store.OpenNotices("p1").Single(notice => notice.ReasonKey == ExecutionReasons.FolderDeleteRestored.Key).Detail.Contains("big/f0.txt", StringComparison.Ordinal),
            "the notice names the edited file");
    }

    private static async Task FolderTreeFeedFailureAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.FailFeedAfterFolderDelete = true;
        var results = await rig.ApplyAsync(rig.Plan());
        Require(rig.H.Wire.WholeFolderDeletes == 1 && results.Count == 62 &&
            results.All(result => result.Outcome == Outcome.Uncertain &&
                result.Reason!.Contains("change feed after", StringComparison.Ordinal)),
            "a post-check feed failure leaves every victim Uncertain with its reason, never Failed");
        Require(rig.Store.UnresolvedIntents("p1").Count == 62 &&
            rig.H.Wire.Calls.Count(call => call == "/2/files/delete_batch") == 1,
            "the intents stay for reconcile and the delete is never replayed");
    }

    private static async Task FolderTreeBelowThresholdAsync()
    {
        using var rig = await TreeRigAsync(FolderDelete.WholeFolderMinimum - 2);
        var results = await rig.ApplyAsync(rig.Plan());
        Require(rig.H.Wire.WholeFolderDeletes == 0 &&
            rig.H.Wire.DeleteBatchSizes.Count == FolderDelete.WholeFolderMinimum - 2 &&
            rig.H.Wire.Calls.Contains("/2/files/delete_v2"),
            "a folder below the threshold keeps the per-item path: one conditional delete per file, then empty folders");
        Require(results.All(result => result.Outcome == Outcome.Done) && !rig.H.Wire.Calls.Contains("/2/files/restore"),
            "the per-item path completes unchanged");
    }

    private static async Task FolderTreeEditDuringJobNoticeAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.BeforeBuryFolder = () => rig.H.Wire.EditFile("big/f0.txt", "edit during delete");
        await rig.ApplyAsync(rig.Plan());
        Require(!rig.H.Wire.Calls.Contains("/2/files/list_revisions"),
            "accepted in-job edit window does not add one revision lookup per planned victim");
        Require(rig.Store.OpenNotices("p1").Any(notice => notice.Detail.Contains("history", StringComparison.OrdinalIgnoreCase)
                && notice.Detail.Contains("big", StringComparison.OrdinalIgnoreCase)),
            "the accepted edit window is never silent: durable notice names folder and Dropbox history");
    }

    private static async Task FolderTreeOccupiedRestoreAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.SeedFile("big/late.txt", "late");
        string? current = null;
        rig.H.Wire.AfterBuriedRevisionRead = () =>
        {
            rig.H.Wire.SeedFolder("big");
            current = rig.H.Wire.EditFile("big/late.txt", "new current content");
        };
        var results = await rig.ApplyAsync(rig.Plan());
        Require(!rig.H.Wire.Calls.Contains("/2/files/restore") &&
                (await rig.B.ObserveAsync("big/late.txt", CancellationToken.None))?.VersionId == current,
            "an occupied recovery destination is not overwritten, even by an overwrite-capable restore endpoint");
        Require(results.All(result => result.Outcome != Outcome.Uncertain) &&
            rig.Store.OpenNotices("p1").Any(notice => notice.Detail.Contains("path now in use", StringComparison.Ordinal)),
            "occupied path resolves with a recoverable-not-restored notice, not a permanent reconciliation stall");
    }

    private static async Task FolderTreeUnknownKindAsync()
    {
        using var rig = await TreeRigAsync(60);
        rig.H.Wire.BeforeBuryFolder = () => rig.H.Wire.SeedFolder("big/late-empty");
        var results = await rig.ApplyAsync(rig.Plan());
        Require(results.All(result => result.Outcome != Outcome.Uncertain) &&
            rig.Store.OpenNotices("p1").Any(notice => notice.Detail.Contains("type unknown", StringComparison.Ordinal)),
            "a new empty-folder tombstone is reported, never guessed to be a file or left unresolved forever");
        Require(await rig.B.ObserveAsync("big/late-empty", CancellationToken.None) is null,
            "an untyped tombstone never causes a fabricated directory");
    }

    private static async Task FolderTreeRunsFirstAsync()
    {
        using var rig = await TreeRigAsync(60);
        var source = rig.A.Seed("up.txt", "upload me", versionId: "v1");
        var upload = new SyncAction("put-up", 2, ActionKind.CopyNew, Side.B, "up.txt", null, source, null, false, null);
        await rig.ApplyAsync(rig.Plan(upload));
        var calls = rig.H.Wire.Calls;
        var firstUpload = calls.FindIndex(call => call.Contains("upload", StringComparison.Ordinal));
        Require(firstUpload > 0 && calls.IndexOf("/2/files/delete_batch") is var delete && delete >= 0 &&
            delete < firstUpload, "the whole-folder delete is sent before any upload");
    }
}
