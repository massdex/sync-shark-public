namespace Massdex.SyncShark.V2;

internal static partial class RunCliChecks
{
    // RQ-025/040, decision 54: a newly ignored child cannot be carried by its previously reviewed parent delete.
    private static async Task Cli_NewIgnoreProtectsReviewedAncestorAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        {
            var root = TempRoot();
            try
            {
                using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
                var fx = Build(store: store);
                var endpoint = side == Side.A ? fx.A : fx.B;
                endpoint.Seed("folder", "", ItemKind.Folder);
                var ignored = endpoint.Seed("folder/desktop.ini", "private");
                var ordinary = endpoint.Seed("folder/ordinary.txt", "also protected by skipped parent");
                var choices = side == Side.A
                    ? """{"oneSided":[{"prefix":"","side":"Local","action":"remove"}]}"""
                    : """{"oneSided":[{"prefix":"","side":"Dropbox","action":"remove"}]}""";
                var runtime = Runtime(fx, root);
                await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
                var before = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)!;
                Require(before.DeleteVictims.Count == 3 &&
                    FirstSyncChoicesState.ReviewedDeleteKeys(before).Contains(FolderDelete.VictimKey(side, "folder")),
                    "ancestor fixture reviews the parent and both children before the new ignore");
                await runtime.SaveIgnoresAsync(fx.Pair.PairId, "FileName:desktop.ini", CancellationToken.None);
                var narrowed = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)
                    ?? throw new InvalidOperationException("Adding ignore must retain the original review binding.");
                Require(narrowed.Digest != before.Digest &&
                    !FirstSyncChoicesState.ReviewedDeleteKeys(narrowed).Contains(FolderDelete.VictimKey(side, "folder")) &&
                    !FirstSyncChoicesState.ReviewedDeleteKeys(narrowed).Contains(FolderDelete.VictimKey(side, "folder/desktop.ini")) &&
                    fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
                    "new ignore removes child and ancestor delete authorization without file effects");
                var result = await Runtime(fx, root).ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null,
                    _ => { }, CancellationToken.None);
                Require(result.Failed == 0 && result.FirstSyncComplete && result.SkippedItems > 0 &&
                    fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 &&
                    await endpoint.ObserveAsync("folder", CancellationToken.None) is not null &&
                    (await endpoint.ObserveAsync("folder/desktop.ini", CancellationToken.None))?.Hash == ignored.Hash &&
                    (await endpoint.ObserveAsync("folder/ordinary.txt", CancellationToken.None))?.Hash == ordinary.Hash,
                    "newly protected child skips the whole ancestor, preserves all content and permits first-sync completion");
                Require(store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "folder" &&
                    notice.ReasonKey == PlanNotices.OutsidePairArea.Key &&
                    notice.Detail.Contains("skipped", StringComparison.OrdinalIgnoreCase) &&
                    notice.Detail.Contains("contents", StringComparison.OrdinalIgnoreCase)),
                    "skipping an ancestor is explicitly disclosed at the folder, not silently dropped");
            }
            finally { Cleanup(root); }
        }
        Console.WriteLine("PASS Cli_NewIgnoreProtectsReviewedAncestor");
    }

    // RQ-025: FolderName matches actual folders, never a same-named file's reviewed authorization.
    private static async Task Cli_NarrowingFolderIgnoreRetainsSameNamedFileAsync()
    {
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(store: store);
            fx.B.Seed("a/cache", "reviewed file");
            fx.B.Seed("b/cache", "", ItemKind.Folder);
            var protectedChild = fx.B.Seed("b/cache/child.txt", "protected child");
            const string choices = """{"oneSided":[{"prefix":"","side":"Dropbox","action":"remove"}]}""";
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            var before = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)!;
            var reviewedBefore = before.Database!.ReviewedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Require(reviewedBefore.SetEquals(["B:a/cache", "B:b/cache", "B:b/cache/child.txt"]),
                "typed narrowing fixture reviews the same-named file, real folder and child");
            await runtime.SaveIgnoresAsync(fx.Pair.PairId, "FolderName:cache", CancellationToken.None);
            var after = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)
                ?? throw new InvalidOperationException("Typed ignore addition must retain the review.");
            Require(after.Digest != before.Digest && after.DeleteVictims.SequenceEqual(["B:a/cache"]) &&
                after.DeleteVictimCount == 1 && after.Database!.ReviewedKeys.SetEquals(["B:a/cache"]) &&
                fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
                "FolderName narrowing retains the file's exact delete authorization and drops only the folder subtree");
            var result = await Runtime(fx, root).ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null,
                _ => { }, CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete &&
                await fx.B.ObserveAsync("a/cache", CancellationToken.None) is null &&
                await fx.B.ObserveAsync("b/cache", CancellationToken.None) is not null &&
                (await fx.B.ObserveAsync("b/cache/child.txt", CancellationToken.None))?.Hash == protectedChild.Hash &&
                fx.WriterA.Calls == 0,
                "original choices delete the actual file named cache while preserving the real cache folder without reprepare");
        }
        finally { Cleanup(root); }
        Console.WriteLine("PASS Cli_NarrowingFolderIgnoreRetainsSameNamedFile");
    }

    private static async Task RunIgnoreChecksAsync()
    {
        await Cli_NarrowingFolderIgnoreRetainsSameNamedFileAsync();
        await Cli_SqliteNarrowingPreservesReviewAsync(ignore: true);
        await Cli_SqliteWideningClearsReviewAsync(ignore: true);
        await Cli_NewIgnoreProtectsReviewedAncestorAsync();
        var root = TempRoot();
        try
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(store: store);
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None);
            var saved = (SyncCliIgnoreListData)await DispatchAsync(runtime,
                ["--cli", "ignores", "set", "--pair", fx.Pair.PairId, "--rules",
                    "FileName:DESKTOP.INI;FileExtension:TMP;FolderName:Cache", "--yes", "--json"]);
            var listed = (SyncCliIgnoreListData)await DispatchAsync(runtime,
                ["--cli", "ignores", "list", "--pair", fx.Pair.PairId, "--json"]);
            Require(saved.Count == 3 && saved.Rules.SequenceEqual(listed.Rules) &&
                listed.Rules.Any(rule => rule.Kind == "FileName" && rule.Value == "desktop.ini") &&
                listed.Rules.Any(rule => rule.Kind == "FileExtension" && rule.Value == ".tmp"),
                "ignores set/list round-trips normalized rules through the registry");
            var narrowed = FirstSyncChoicesState.Load(root, fx.Pair.PairId, store);
            Require(narrowed is not null,
                "strictly adding ignore rules retains the prepare without re-review");
            await runtime.SaveIgnoresAsync(fx.Pair.PairId,
                "FolderName:cache;FileName:desktop.ini;FileExtension:.tmp", CancellationToken.None);
            Require(FirstSyncChoicesState.Load(root, fx.Pair.PairId, store)?.Digest == narrowed!.Digest,
                "a normalized no-op keeps the saved prepare and its digest");
            foreach (var bad in new[] { "Unknown:x", "FileName:../x", "FileName:*" })
            {
                var refusal = await RefusalAsync(() => runtime.SaveIgnoresAsync(fx.Pair.PairId, bad, CancellationToken.None));
                Require(refusal is SyncCliUsageException, "invalid ignore rules are usage errors");
            }
            Require((await runtime.LoadIgnoresAsync(fx.Pair.PairId, CancellationToken.None)).Count == 3 &&
                FirstSyncChoicesState.Load(root, fx.Pair.PairId, store) is not null,
                "invalid rules change neither definition nor prepare");
            var oldError = Console.Error;
            using var warning = new StringWriter();
            try
            {
                Console.SetError(warning);
                await runtime.SaveIgnoresAsync(fx.Pair.PairId, "none", CancellationToken.None);
            }
            finally { Console.SetError(oldError); }
            Require(warning.ToString().Contains("removing FileName:desktop.ini", StringComparison.Ordinal) &&
                warning.ToString().Contains("eligible for sync", StringComparison.Ordinal) &&
                warning.ToString().Contains("Deletions made while ignored can now propagate", StringComparison.Ordinal),
                "dropping desktop.ini warns about changed sync behavior without refusing");
            Require((await runtime.LoadIgnoresAsync(fx.Pair.PairId, CancellationToken.None)).Count == 0 &&
                FirstSyncChoicesState.Load(root, fx.Pair.PairId, store) is null, "none clears rules and invalidates prepare");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "ignore edits have no endpoint effects");
        }
        finally { Cleanup(root); }
        Console.WriteLine("PASS Cli_Ignores_RoundTripValidationAndPrepareInvalidation");

        foreach (var side in new[] { Side.A, Side.B })
        foreach (var (spec, path) in new[] { ("FileName:desktop.ini", "DESKTOP.INI"),
            ("FileExtension:tmp", "nested/file.TMP"), ("FolderName:cache", "CaChe/file.txt") })
        {
            var item = Build();
            var itemRoot = TempRoot();
            try
            {
                (side == Side.A ? item.A : item.B).Seed(path, "untouched");
                var runtime = Runtime(item, itemRoot);
                await runtime.SaveIgnoresAsync(item.Pair.PairId, spec, CancellationToken.None);
                var preview = await runtime.PrepareFirstSyncAsync(item.Pair.PairId, _ => { }, CancellationToken.None);
                Require(preview.LocalOnlyItems == 0 && preview.DropboxOnlyItems == 0,
                    $"{spec} hides {side}-only {path} from the plan");
                var plan = await item.Run().PreviewFirstSyncAsync(item.Pair.PairId,
                    new RecordingProgress(), CancellationToken.None);
                Require(plan.Actions.Count == 0, "ignored-only inventories produce no planned actions");
                var scannedRoot = side == Side.A ? item.Pair.A.RootId : item.Pair.B.RootId;
                Require(item.Store.Live(scannedRoot).Any(row => row.Path.Equals(path, StringComparison.OrdinalIgnoreCase)),
                    "ignored items really are inventoried as metadata, not pruned before planning");
                Require(item.WriterA.Calls == 0 && item.WriterB.Calls == 0, "prepare has no endpoint effects");
            }
            finally { Cleanup(itemRoot); }
        }
        Console.WriteLine("PASS Cli_Ignores_LocalAndCloudOnlyPlanNoActions");
        foreach (var missing in new[] { Side.A, Side.B })
        {
            var item = Build();
            item.A.Seed("desktop.ini", "same");
            item.B.Seed("desktop.ini", "same");
            await FirstSyncAsync(item);
            var root2 = TempRoot();
            try
            {
                var runtime = Runtime(item, root2);
                await runtime.SaveIgnoresAsync(item.Pair.PairId, "FileName:desktop.ini", CancellationToken.None);
                var endpoint = missing == Side.A ? item.A : item.B;
                var before = await endpoint.ObserveAsync("desktop.ini", CancellationToken.None);
                await endpoint.DeleteAsync(new SyncAction("external-delete", 6, ActionKind.Delete, missing,
                    "desktop.ini", null, null, before, false, null), CancellationToken.None);
                var callsA = item.WriterA.Calls;
                var callsB = item.WriterB.Calls;
                await item.Run().IncrementalAsync(item.Pair.PairId, new RecordingProgress(), CancellationToken.None);
                Require(item.WriterA.Calls == callsA && item.WriterB.Calls == callsB,
                    "an ignored baselined disappearance never deletes or copies on either endpoint");
                Require(item.Store.Baselines(item.Pair.PairId).Count == 1,
                    "ignoring a baselined disappearance retains its baseline");
            }
            finally { Cleanup(root2); }
        }
        Console.WriteLine("PASS Cli_Ignores_BaselinedAbsenceIsNotDeletion");
        foreach (var side in new[] { Side.A, Side.B })
        {
            var item = Build();
            var endpoint = side == Side.A ? item.A : item.B;
            endpoint.Seed("folder", "", ItemKind.Folder);
            endpoint.Seed("folder/desktop.ini", "untouched");
            var root3 = TempRoot();
            try
            {
                await Runtime(item, root3).SaveIgnoresAsync(item.Pair.PairId,
                    "FileName:desktop.ini", CancellationToken.None);
                var choices = side == Side.A
                    ? """{"oneSided":[{"prefix":"","side":"local","action":"remove"}]}"""
                    : """{"oneSided":[{"prefix":"","side":"Dropbox","action":"remove"}]}""";
                var plan = await PreviewAsync(item, choices);
                Require(plan.Actions.Any(action => action.Kind == ActionKind.Delete && action.Path == "folder"),
                    "an ignored child does not block the enclosing folder delete");
                Require(!plan.Actions.Any(action => action.Path.EndsWith("desktop.ini", StringComparison.Ordinal)),
                    "the ignored child has no standalone delete");
            }
            finally { Cleanup(root3); }
        }
        Console.WriteLine("PASS Cli_Ignores_FolderDeleteCarriesIgnoredChildren");
        foreach (var (spec, path, kind) in new[] { ("FileName:desktop.ini", "desktop.ini", ItemKind.Folder),
            ("FileExtension:tmp", "report.tmp", ItemKind.Folder), ("FolderName:cache", "cache", ItemKind.File) })
        {
            var item = Build();
            item.A.Seed(path, "", kind);
            item.Store.SavePair(item.Pair with { IgnoreRules = [spec] });
            var plan = await item.Run().PreviewFirstSyncAsync(item.Pair.PairId,
                new RecordingProgress(), CancellationToken.None);
            Require(plan.Actions.Any(action => action.Path == path), "ignore matching uses the actual item kind");
        }
        var moved = Build();
        foreach (var endpoint in new[] { moved.A, moved.B })
        {
            endpoint.Seed("old", "", ItemKind.Folder);
            endpoint.Seed("old/f.txt", "synced");
            endpoint.Seed("old/desktop.ini", "private");
        }
        moved.Store.SavePair(moved.Pair with { IgnoreRules = ["FileName:desktop.ini"] });
        await FirstSyncAsync(moved);
        await moved.A.MoveAsync(new SyncAction("external-move", 25, ActionKind.Move, Side.A, "new", "old",
            null, null, false, null), CancellationToken.None);
        await moved.Run().IncrementalAsync(moved.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(await moved.B.ObserveAsync("old", CancellationToken.None) is null &&
            await moved.B.ObserveAsync("new/desktop.ini", CancellationToken.None) is not null,
            "a local folder rename carries its ignored child on the cloud side");
        Console.WriteLine("PASS Cli_Ignores_TypedMatchingAndFolderMove");
        var churn = Build();
        churn.B.Seed("folder", "", ItemKind.Folder);
        churn.B.Seed("folder/f.txt", "synced");
        churn.B.Seed("folder/desktop.ini", "ignored-before");
        churn.Store.SavePair(churn.Pair with { IgnoreRules = ["FileName:desktop.ini"] });
        var remove = await PreviewAsync(churn,
            """{"oneSided":[{"prefix":"","side":"Dropbox","action":"remove"}]}""");
        var folderDelete = remove.Actions.Single(action => action.Kind == ActionKind.Delete && action.Path == "folder");
        var changed = churn.B.Seed("folder/desktop.ini", "ignored-after");
        var gen = churn.Store.BeginGeneration(churn.Pair.B.RootId, "delta");
        churn.Store.WriteDelta(new Delta(churn.Pair.B.RootId, Side.B, gen, "", "",
            [new Change(ChangeKind.Upsert, changed.ObjectId, changed.Path, changed, null)], false, []));
        Require(folderDelete.FrozenVictimSignature == FolderDelete.Signature(FolderDelete.SyncedDescendants(
            churn.Store, churn.Store.Pair(churn.Pair.PairId)!, churn.Pair.B.RootId, "folder")),
            "ignored-child churn never supersedes the enclosing folder's frozen victims");
        await RunCarriedIgnoredReviewChecksAsync();
    }
}
