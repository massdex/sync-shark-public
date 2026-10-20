namespace Massdex.SyncShark.V2;

/// <summary>Rebuildable-folder first-sync checks (docs/design/rebuildables.md): the prepare report section and frozen
/// set, the plan effect (local untouched, the cloud copy removed as one folder delete), drift between prepare and
/// apply, and the exclusions saved in the same step that marks the first sync done.</summary>
internal static partial class RunCliChecks
{
    private static async Task RunRebuildableChecksAsync()
    {
        await Cli_Prepare_ScopedRebuildablesAsync();
        await Cli_ScopedRebuildablesWarnOnNoMatchAsync();
        await Cli_Prepare_ReportsRebuildablesAsync();
        await Cli_Apply_RemovesCloudCopyKeepsLocalAndPersistsAsync();
        await Cli_Apply_NoCloudCopyChangesNothingLocalAsync();
        await Cli_Apply_NewLocalFolderIsNotAddedAsync();
        await Cli_Apply_LocalFolderGoneDropsThePathAsync();
        await Cli_Apply_MarkerGoneDropsThePathAsync();
        await Cli_Apply_ExcludedDeleteSkipsAndCompletesAsync();
        await Cli_Prepare_OneDetectionFeedsReportPlanAndRecordAsync();
        await Cli_Digest_CoversTheFrozenSetAsync();
        await Cli_Completion_SavesExclusionsWithFirstSyncDoneAsync();
        await Cli_Completion_NoExclusionsWhenRemovalFailsAsync();
    }

    // A marker removed between prepare and apply drops the path at the apply's single revalidation, before planning:
    // the executed plan holds no rebuildable removal, nothing is persisted, and the path syncs normally.
    private static async Task Cli_Apply_MarkerGoneDropsThePathAsync()
    {
        var fx = Build();
        fx.A.Seed("a/package.json", "{}");
        fx.A.Seed("a/node_modules", "", ItemKind.Folder);
        fx.A.Seed("a/node_modules/x.js", "local");
        fx.B.Seed("a/node_modules", "", ItemKind.Folder);
        fx.B.Seed("a/node_modules/x.js", "cloud");
        var root = TempRoot();
        const string choices = """{"rebuildables":["js-packages"]}""";
        try
        {
            var runtime = Runtime(fx, root);
            var prepared = await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            Require(prepared.Rebuildables!.Single(row => row.Id == "js-packages") is { Folders: 1, Chosen: true },
                "the prepare detects the folder while its marker is still there");
            // The marker disappears after the prepare, so the frozen path no longer qualifies at apply.
            var marker = await fx.A.ObserveAsync("a/package.json", CancellationToken.None);
            await fx.A.DeleteAsync(new SyncAction("seed", 6, ActionKind.Delete, Side.A, "a/package.json", null, null,
                marker, false, null), CancellationToken.None);
            // The apply hand the CLI executes is exactly the plan this preview returns, so this asserts on the
            // executed plan without a second revalidation able to disagree with it.
            var frozen = FirstSyncChoicesState.Load(root, fx.Pair.PairId)!.Rebuildables
                .Select(entry => new RebuildableFolder(entry.Category, entry.Path, 0, 0)).ToArray();
            var (plan, resolution) = await fx.Run().PreviewFirstSyncApplyResolvedAsync(fx.Pair.PairId, null, null,
                frozen, null, new Progress<RunProgress>(), CancellationToken.None);
            Require(plan.Actions.All(action => !action.RebuildableRemove) && resolution.Selected.Count == 0,
                "a frozen path whose marker is gone is dropped before planning, so the executed plan holds no " +
                "rebuildable removal: " + string.Join(", ", plan.Actions.Select(action => action.Kind + " " + action.Path)));
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete,
                "the dropped path neither blocks nor defers the first sync");
            Require(await fx.B.ObserveAsync("a/node_modules", CancellationToken.None) is not null,
                "the dropped path's cloud copy is left to ordinary planning, with no refusal");
            Require(fx.Store.Pair(fx.Pair.PairId)?.Excluded.Count == 0,
                "a dropped path is never persisted as an exclusion");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Apply_MarkerGoneDropsThePath");
    }

    // An exclusion inside a reviewed rebuildable makes its ancestor delete an intentional terminal skip.
    // Completion is allowed, but the entire folder stays untouched and no rebuildable exclusion is saved.
    private static async Task Cli_Apply_ExcludedDeleteSkipsAndCompletesAsync()
    {
        var fx = Build();
        fx.A.Seed("package.json", "{}");
        fx.A.Seed("node_modules", "", ItemKind.Folder);
        var localFile = fx.A.Seed("node_modules/x.js", "one");
        var localKeep = fx.A.Seed("node_modules/keep", "", ItemKind.Folder);
        var cloudFolder = fx.B.Seed("node_modules", "", ItemKind.Folder);
        var cloudFile = fx.B.Seed("node_modules/x.js", "cloud");
        var root = TempRoot();
        const string choices = """{"rebuildables":["js-packages"]}""";
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            // An exclusion inside the tree appears after prepare: skip the whole ancestor operation,
            // disclose it, and finish this first sync without persisting a rebuildable exclusion.
            fx.Store.SavePair(fx.Pair with { Excluded = ["node_modules/keep"] });
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { },
                CancellationToken.None);
            Require(result.BlockedRebuildables.Count == 0 && result.SkippedItems > 0 && result.Failed == 0,
                "an intentional exclusion skip is reported as skipped, not a blocked or failed removal");
            Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && result.FirstSyncComplete && result.Deferred == 0,
                "an intentional excluded ancestor skip permits first-sync completion");
            Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "node_modules" &&
                notice.ReasonKey == PlanNotices.OutsidePairArea.Key &&
                notice.Detail.Contains("Entire planned effect", StringComparison.Ordinal) &&
                notice.Detail.Contains("folder contents", StringComparison.Ordinal) &&
                notice.Detail.Contains("skipped; no changes applied", StringComparison.Ordinal)),
                "the exact ancestor has an explicit notice that its entire effect and contents were skipped");
            Require(fx.Store.Pair(fx.Pair.PairId)!.Excluded.Contains("node_modules/keep") &&
                fx.Store.Pair(fx.Pair.PairId)!.Excluded.Contains("node_modules"),
                "the reviewed rebuildable exclusion persists while the protected child and cloud tree stay untouched");
            Require(await fx.B.ObserveAsync("node_modules", CancellationToken.None) == cloudFolder &&
                await fx.B.ObserveAsync("node_modules/x.js", CancellationToken.None) == cloudFile &&
                await fx.A.ObserveAsync("node_modules/x.js", CancellationToken.None) == localFile &&
                await fx.A.ObserveAsync("node_modules/keep", CancellationToken.None) == localKeep &&
                fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
                "the skipped cloud folder and both sides' contents retain their exact stamps with no deletes");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Apply_ExcludedDeleteSkipsAndCompletes");
    }

    // The report, the frozen record and the plan all come from the single detection the preview returns: a nested
    // selection is reported per category but frozen and planned as its outermost root.
    private static async Task Cli_Prepare_OneDetectionFeedsReportPlanAndRecordAsync()
    {
        var fx = Build();
        fx.A.Seed("a/package.json", "{}");
        fx.A.Seed("a/node_modules", "", ItemKind.Folder);
        fx.A.Seed("a/node_modules/.venv", "", ItemKind.Folder);
        fx.A.Seed("a/node_modules/.venv/pyvenv.cfg", "x");
        fx.B.Seed("a/node_modules", "", ItemKind.Folder);
        var root = TempRoot();
        const string choices = """{"rebuildables":["js-packages","python-venv"]}""";
        try
        {
            var data = await Runtime(fx, root).PrepareFirstSyncAsync(fx.Pair.PairId, _ => { },
                CancellationToken.None, choices);
            var prepared = FirstSyncChoicesState.Load(root, fx.Pair.PairId)!;
            Require(prepared.Rebuildables.Select(entry => entry.Path).SequenceEqual(["a/node_modules"]),
                "the frozen record holds the collapsed outermost root: " +
                string.Join(", ", prepared.Rebuildables.Select(entry => entry.Category + " " + entry.Path)));
            var frozen = prepared.Rebuildables
                .Select(entry => new RebuildableFolder(entry.Category, entry.Path, 0, 0)).ToArray();
            var (plan, resolution) = await fx.Run().PreviewFirstSyncApplyResolvedAsync(fx.Pair.PairId, null, null, frozen,
                null, new Progress<RunProgress>(), CancellationToken.None);
            Require(resolution.Selected.Select(folder => folder.Path).SequenceEqual(["a/node_modules"]) &&
                plan.Actions.Where(action => action.RebuildableRemove).Select(action => action.Path)
                    .SequenceEqual(["a/node_modules"]),
                "the plan's single rebuildable delete is at the same path the record froze");
            Require(data.Rebuildables!.Single(row => row.Id == "js-packages") is { Folders: 1, Chosen: true } &&
                data.Rebuildables!.Single(row => row.Id == "python-venv") is { Folders: 1, Chosen: true },
                "the report describes that same one detection, one folder per chosen category");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Prepare_OneDetectionFeedsReportPlanAndRecord");
    }

    // The digest covers the frozen set, and every frozen category must still be one of the chosen categories.
    private static async Task Cli_Digest_CoversTheFrozenSetAsync()
    {
        var fx = Build();
        fx.A.Seed("package.json", "{}");
        fx.A.Seed("node_modules", "", ItemKind.Folder);
        var root = TempRoot();
        const string choices = """{"rebuildables":["js-packages"]}""";
        try
        {
            var runtime = Runtime(fx, root);
            // A record whose digest covers a different frozen set than the one it stores cannot be applied: the
            // digest is the only thing that binds the frozen tuples to the reviewed choices.
            var tampered = new
            {
                digest = FirstSyncChoices.Digest(FirstSyncChoices.Parse(choices, fx.Pair),
                    [new RebuildableFolder("js-packages", "other/node_modules", 0, 0)]),
                paths = new Dictionary<string, string>(),
                oneSided = Array.Empty<object>(),
                deleteVictims = Array.Empty<string>(),
                deleteVictimCount = 0,
                heldDeletions = Array.Empty<HeldDeletion>(),
                rebuildables = new[] { new { category = "js-packages", path = "node_modules" } }
            };
            Directory.CreateDirectory(Path.Combine(root, "v2"));
            await File.WriteAllTextAsync(Path.Combine(root, "v2", "first-sync-" + fx.Pair.PairId + ".choices.json"),
                System.Text.Json.JsonSerializer.Serialize(tampered));
            Require(await ApplyRefusedReviewAsync(runtime, fx, choices),
                "a frozen set the digest did not cover cannot be applied");
            // A record whose digest matches but whose frozen category is not one of the chosen categories is refused.
            FirstSyncChoicesState.Save(root, fx.Pair.PairId, FirstSyncChoices.Parse("""{"rebuildables":[]}""", fx.Pair),
                [], [], [new RebuildableFolder("js-packages", "node_modules", 0, 0)]);
            Require(await ApplyRefusedReviewAsync(runtime, fx, """{"rebuildables":[]}"""),
                "a frozen category that is not one of the chosen categories is refused");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "a refused apply makes no writer call");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Digest_CoversTheFrozenSet");
    }

    private static async Task<bool> ApplyRefusedReviewAsync(V2CliRuntime runtime, Fixture fx, string choices)
    {
        try
        {
            await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { }, CancellationToken.None);
        }
        catch (SyncCliReviewException error)
        {
            return error.Message.Contains("differ from the last prepare", StringComparison.Ordinal);
        }
        return false;
    }

    private static async Task Cli_ScopedRebuildablesWarnOnNoMatchAsync()
    {
        var fx = Build();
        fx.A.Seed("a/package.json", "{}");
        fx.A.Seed("a/node_modules", "", ItemKind.Folder);
        fx.A.Seed("a/node_modules/x.js", "local");
        var root = TempRoot();
        const string choices = """{"rebuildables":[{"id":"js-packages","prefix":"typo/"}]}""";
        try
        {
            var runtime = Runtime(fx, root);
            var prepared = await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            Require(prepared.Warnings.SequenceEqual([
                "rebuildables 'js-packages' under 'typo/' matched no folders: check the prefix"]),
                "prepare warns about an unmatched scoped rebuildable without refusing");
            var applied = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { }, CancellationToken.None);
            Require(applied.FirstSyncComplete && applied.Failed == 0 && applied.Warnings.SequenceEqual(prepared.Warnings),
                "apply repeats the unmatched-prefix warning and completes normally");
            Require(await fx.B.ObserveAsync("a/node_modules/x.js", CancellationToken.None) is not null,
                "unmatched scoped choice does not stop other rebuildable folders syncing");
        }
        finally { Cleanup(root); }
    }

    private static async Task Cli_Prepare_ScopedRebuildablesAsync()
    {
        var fx = Build();
        foreach (var prefix in new[] { "a", "ab" })
        {
            fx.A.Seed(prefix + "/package.json", "{}");
            fx.A.Seed(prefix + "/node_modules", "", ItemKind.Folder);
            fx.A.Seed(prefix + "/node_modules/x.js", "local");
            fx.B.Seed(prefix + "/node_modules", "", ItemKind.Folder);
        }
        var root = TempRoot();
        const string choices = """{"rebuildables":[{"id":"js-packages","prefix":"a/"}]}""";
        try
        {
            var runtime = Runtime(fx, root);
            var data = await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            Require(data.Warnings.Count == 0, "matching scoped rebuildable has no prefix warning");
            var candidates = data.Rebuildables!.Single(row => row.Id == "js-packages").Candidates!;
            Require(candidates.Count == 2 && candidates.Single(row => row.Path == "a/node_modules").Chosen &&
                !candidates.Single(row => row.Path == "ab/node_modules").Chosen,
                "prepare lists both candidates and marks only the scoped folder chosen");
            var stored = FirstSyncChoicesState.Load(root, fx.Pair.PairId)!;
            Require(stored.RebuildableChoices!.Single() == new RebuildableChoice("js-packages", "a") &&
                stored.Rebuildables.Single().Path == "a/node_modules", "state retains scope and only freezes in-scope folders");
            Require(await ApplyRefusedReviewAsync(runtime, fx, """{"rebuildables":["js-packages"]}"""),
                "changing a scoped prepare to pair-wide is refused");
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { }, CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete, "scoped rebuildable apply completes");
            Require(await fx.B.ObserveAsync("a/node_modules", CancellationToken.None) is null &&
                await fx.B.ObserveAsync("ab/node_modules/x.js", CancellationToken.None) is not null,
                "only scoped cloud folder is removed while its sibling continues syncing");
            Require(fx.Store.Pair(fx.Pair.PairId)!.Excluded.SequenceEqual(["a/node_modules"]),
                "only the scoped folder becomes excluded");
        }
        finally { Cleanup(root); }
    }

    // Every category appears in the prepare report, chosen or not, with its explanation, folder count, files and bytes.
    private static async Task Cli_Prepare_ReportsRebuildablesAsync()
    {
        var fx = Build();
        fx.A.Seed("package.json", "{}");
        fx.A.Seed("node_modules", "", ItemKind.Folder);
        fx.A.Seed("node_modules/p/x.js", "abc");
        fx.A.Seed("other.txt", "x");
        var root = TempRoot();
        try
        {
            var data = await Runtime(fx, root).PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None,
                """{"rebuildables":["js-packages"]}""");
            var rows = data.Rebuildables!;
            Require(rows.Count == RebuildableDetector.Categories.Count, "one report row per category, chosen or not");
            var packages = rows.Single(row => row.Id == "js-packages");
            Require(packages is { Folders: 1, Files: 1, Bytes: 3, Chosen: true } &&
                packages.Examples.SequenceEqual(["node_modules"]),
                "the chosen category reports its folder, file count, bytes and example path");
            Require(rows.Single(row => row.Id == "python-cache") is { Folders: 0, Files: 0, Chosen: false },
                "a category with nothing found is still reported, unchosen");
            Require(rows.All(row => row.Explanation.Length > 0), "every row carries its plain-English explanation");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "the prepare makes no writer call");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Prepare_ReportsRebuildables");
    }

    // The plan removes the cloud folder once and never touches the local side; applying it removes the cloud copy,
    // leaves the local copy as it was and persists the path as a pair exclusion.
    private static async Task Cli_Apply_RemovesCloudCopyKeepsLocalAndPersistsAsync()
    {
        var fx = Build();
        fx.A.Seed("package.json", "{}");
        fx.A.Seed("node_modules", "", ItemKind.Folder);
        fx.A.Seed("node_modules/p/x.js", "local");
        fx.A.Seed("keep.txt", "keep");
        fx.B.Seed("node_modules", "", ItemKind.Folder);
        fx.B.Seed("node_modules/p/x.js", "cloud version!");
        var root = TempRoot();
        const string choices = """{"rebuildables":["js-packages"]}""";
        try
        {
            var frozen = new[] { new RebuildableFolder("js-packages", "node_modules", 0, 0) };
            var (plan, _) = await fx.Run().PreviewFirstSyncApplyResolvedAsync(fx.Pair.PairId, null, null, frozen,
                null, new Progress<RunProgress>(), CancellationToken.None);
            Require(plan.Actions.Single(action => action.Kind == ActionKind.Delete) is
                { Target: Side.B, Path: "node_modules", Rule: 32, RecoveryFirst: false, RebuildableRemove: true } &&
                plan.Actions.Count(action => PairPath.IsSelfOrUnder(action.Path, "node_modules")) == 1 &&
                plan.Actions.All(action => action.Target != Side.A),
                "the plan is one cloud folder delete; nothing else under the folder and nothing on the local side");
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete, "the first sync completes");
            Require(await fx.B.ObserveAsync("node_modules/p/x.js", CancellationToken.None) is null &&
                await fx.B.ObserveAsync("node_modules", CancellationToken.None) is null,
                "the cloud copy is removed");
            Require(await fx.A.ObserveAsync("node_modules/p/x.js", CancellationToken.None) is { Size: 5 },
                "the local copy is left exactly as it was");
            Require(fx.Store.Pair(fx.Pair.PairId)?.Excluded.Contains("node_modules") == true,
                "the path is persisted as a pair rule so ongoing sync keeps ignoring it");
            Require(await fx.B.ObserveAsync("keep.txt", CancellationToken.None) is not null,
                "the rest of the pair still syncs");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Apply_RemovesCloudCopyKeepsLocalAndPersists");
    }

    // With no cloud copy at all, nothing under the folder is uploaded: the local side is left alone, no delete is
    // planned, and the path is still persisted so ongoing sync keeps ignoring it.
    private static async Task Cli_Apply_NoCloudCopyChangesNothingLocalAsync()
    {
        var fx = Build();
        fx.A.Seed("package.json", "{}");
        fx.A.Seed("node_modules", "", ItemKind.Folder);
        fx.A.Seed("node_modules/p/x.js", "local");
        var root = TempRoot();
        const string choices = """{"rebuildables":["js-packages"]}""";
        try
        {
            var plan = await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, null, null, ["js-packages"],
                new Progress<RunProgress>(), CancellationToken.None);
            Require(plan.Actions.All(action => !PairPath.IsSelfOrUnder(action.Path, "node_modules")) &&
                plan.Actions.Any(action => action.Path == "package.json"),
                "nothing under the folder is planned and no delete is raised; the rest still syncs: " +
                string.Join(", ", plan.Actions.Select(action => action.Kind + " " + action.Path)));
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { }, CancellationToken.None);
            Require(await fx.A.ObserveAsync("node_modules/p/x.js", CancellationToken.None) is not null &&
                await fx.B.ObserveAsync("node_modules/p/x.js", CancellationToken.None) is null,
                "the local copy stays and is never uploaded");
            Require(fx.Store.Pair(fx.Pair.PairId)?.Excluded.Contains("node_modules") == true,
                "the rule is still persisted");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Apply_NoCloudCopyChangesNothingLocal");
    }

    // A rebuildable folder that appears only after the prepare is planned normally at apply: the frozen set is never
    // extended, and only the prepared path is persisted.
    private static async Task Cli_Apply_NewLocalFolderIsNotAddedAsync()
    {
        var fx = Build();
        fx.A.Seed("a/package.json", "{}");
        fx.A.Seed("a/node_modules", "", ItemKind.Folder);
        fx.A.Seed("a/node_modules/x.js", "one");
        var root = TempRoot();
        const string choices = """{"rebuildables":["js-packages"]}""";
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            fx.A.Seed("b/package.json", "{}");
            fx.A.Seed("b/node_modules", "", ItemKind.Folder);
            fx.A.Seed("b/node_modules/y.js", "two");
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed == 0, "the apply completes");
            Require(await fx.B.ObserveAsync("b/node_modules/y.js", CancellationToken.None) is not null,
                "a folder first seen after the prepare is planned normally, not added to the frozen set");
            Require(fx.Store.Pair(fx.Pair.PairId)?.Excluded.SequenceEqual(["a/node_modules"]) == true,
                "only the prepared path is persisted");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Apply_NewLocalFolderIsNotAdded");
    }

    // A prepared path whose local folder is gone at apply is dropped: the cloud copy falls to ordinary planning and
    // the path is never persisted.
    private static async Task Cli_Apply_LocalFolderGoneDropsThePathAsync()
    {
        var fx = Build();
        fx.A.Seed("a/package.json", "{}");
        fx.A.Seed("a/node_modules", "", ItemKind.Folder);
        fx.A.Seed("a/node_modules/x.js", "one");
        fx.B.Seed("a/node_modules", "", ItemKind.Folder);
        fx.B.Seed("a/node_modules/x.js", "cloud");
        var root = TempRoot();
        const string choices = """{"rebuildables":["js-packages"]}""";
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            var file = await fx.A.ObserveAsync("a/node_modules/x.js", CancellationToken.None);
            var folder = await fx.A.ObserveAsync("a/node_modules", CancellationToken.None);
            await fx.A.DeleteAsync(new SyncAction("seed1", 6, ActionKind.Delete, Side.A, "a/node_modules/x.js",
                null, null, file, false, null), CancellationToken.None);
            await fx.A.DeleteAsync(new SyncAction("seed2", 6, ActionKind.Delete, Side.A, "a/node_modules",
                null, null, folder, false, null), CancellationToken.None);
            var frozen = new[] { new RebuildableFolder("js-packages", "a/node_modules", 0, 0) };
            var (plan, _) = await fx.Run().PreviewFirstSyncApplyResolvedAsync(fx.Pair.PairId, null, null, frozen,
                null, new Progress<RunProgress>(), CancellationToken.None);
            Require(plan.Actions.Any(action => action.Path == "a/node_modules") &&
                plan.Actions.All(action => !action.RebuildableRemove),
                "the dropped path is planned by the ordinary rules, never as a rebuildable removal");
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed == 0, "the apply completes");
            Require(fx.Store.Pair(fx.Pair.PairId)?.Excluded.Count == 0,
                "a dropped path is never persisted as an exclusion");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Apply_LocalFolderGoneDropsThePath");
    }

    // Completion and the exclusions land together: when the first sync finishes, the record is already saved (so the
    // prepare record can then be cleared), and there is no window with the first sync done but no exclusions.
    private static async Task Cli_Completion_SavesExclusionsWithFirstSyncDoneAsync()
    {
        var fx = Build();
        fx.A.Seed("package.json", "{}");
        fx.A.Seed("node_modules", "", ItemKind.Folder);
        fx.A.Seed("node_modules/x.js", "one");
        fx.B.Seed("package.json", "{}");
        fx.B.Seed("node_modules", "", ItemKind.Folder);
        fx.B.Seed("node_modules/x.js", "one");
        var root = TempRoot();
        const string choices = """{"rebuildables":["js-packages"]}""";
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete, "the first sync completes");
            Require(fx.Store.FirstSyncDone(fx.Pair.PairId) &&
                fx.Store.Pair(fx.Pair.PairId)?.Excluded.Contains("node_modules") == true,
                "the exclusions are durable with the completion, never after it");
            Require(FirstSyncChoicesState.Load(root, fx.Pair.PairId) is null,
                "the prepare record is cleared only once the exclusions are saved");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Completion_SavesExclusionsWithFirstSyncDone");
    }

    // An unfinished removal keeps the pair in first-sync mode and saves no exclusions, so the removal is retried.
    internal static async Task Cli_Completion_NoExclusionsWhenRemovalFailsAsync()
    {
        foreach (var grows in new[] { false, true })
        {
        var fx = Build();
        fx.A.Seed("package.json", "{}");
        fx.A.Seed("node_modules", "", ItemKind.Folder);
        fx.A.Seed("node_modules/x.js", "one");
        fx.B.Seed("package.json", "{}");
        fx.B.Seed("node_modules", "", ItemKind.Folder);
        fx.B.Seed("node_modules/x.js", "one");
        var root = TempRoot();
        const string choices = """{"rebuildables":["js-packages"]}""";
        try
        {
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            fx.B.FailNextWrites = 1;
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed > 0, "the cloud removal failed");
            Require(fx.Store.FirstSyncDone(fx.Pair.PairId) && fx.Store.Pair(fx.Pair.PairId)!.Excluded.Contains("node_modules"),
                "completion persists the reviewed exclusion even when removal is unfinished");
            var puts = fx.WriterB.Puts;
            var deletes = fx.WriterB.Deletes;
            if (grows) fx.B.Seed("node_modules/new.txt", "unreviewed cloud child");
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new Progress<RunProgress>(), default);
            Require(fx.WriterB.Puts == puts && await fx.A.ObserveAsync("node_modules/x.js", default) is not null,
                "later retries never re-upload the excluded rebuildable or remove its local content");
            if (grows)
                Require(await fx.B.ObserveAsync("node_modules/new.txt", default) is not null &&
                    await fx.B.ObserveAsync("node_modules/x.js", default) is not null && fx.WriterB.Deletes == deletes &&
                    fx.Store.OpenNotices(fx.Pair.PairId).Any(notice => notice.Path == "node_modules" && notice.ReasonKey == "reviewed-folder-grew"),
                    "new cloud child changes the frozen signature: whole reviewed removal stays held, with no deletes");
            else
                Require(await fx.B.ObserveAsync("node_modules", default) is null,
                    "the unchanged reviewed cloud removal finishes on a later retry");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Completion_NoExclusionsWhenRemovalFails grows=" + grows);
        }
    }
}
