namespace Massdex.SyncShark.V2;

internal static partial class RunCliChecks
{
    private static async Task RunCarriedIgnoredReviewChecksAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        {
            var fx = Build();
            var root = TempRoot();
            try
            {
                var endpoint = side == Side.A ? fx.A : fx.B;
                endpoint.Seed("folder", "", ItemKind.Folder);
                endpoint.Seed("folder/cache", "", ItemKind.Folder);
                endpoint.Seed("folder/cache/private.txt", "private");
                var runtime = Runtime(fx, root);
                await runtime.SaveIgnoresAsync(fx.Pair.PairId, "FolderName:cache", CancellationToken.None);
                var choices = side == Side.A
                    ? """{"oneSided":[{"prefix":"","side":"local","action":"remove"}]}"""
                    : """{"oneSided":[{"prefix":"","side":"Dropbox","action":"remove"}]}""";
                var prep = await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
                var disclosure = prep.CarriedIgnoredDeletes.Single(item => item.Path == "folder");
                Require(disclosure.Files == 1 && disclosure.Folders == 1 && disclosure.Bytes == 7,
                    "prepare discloses ignored subtree files, folders and bytes per parent delete");
                var saved = FirstSyncChoicesState.Load(root, fx.Pair.PairId)!;
                Require(saved.CarriedIgnoredDeletes.Single(item => item.Path == "folder") == disclosure,
                    "the reviewed carried-content budget survives save/load");
                var plan = await PreviewAsync(fx, choices);
                var bound = FirstSyncChoicesState.BindCarriedIgnoredBudgets(saved, plan);
                var folder = bound.Actions.Single(action => action.Kind == ActionKind.Delete && action.Path == "folder");
                Require(folder.CarriedIgnoredBudget is { Files: 1, Folders: 1, Bytes: 7 },
                    "apply binds the reviewed budget, not a fresh authorization");
                Require(CarriedIgnoredDeletes.WithinBudget(new(1, 1, 7), folder.CarriedIgnoredBudget) &&
                    CarriedIgnoredDeletes.WithinBudget(new(0, 0, 0), folder.CarriedIgnoredBudget) &&
                    !CarriedIgnoredDeletes.WithinBudget(new(2, 1, 7), folder.CarriedIgnoredBudget) &&
                    !CarriedIgnoredDeletes.WithinBudget(new(1, 2, 7), folder.CarriedIgnoredBudget) &&
                    !CarriedIgnoredDeletes.WithinBudget(new(1, 1, 8), folder.CarriedIgnoredBudget),
                    "equal or smaller aggregates pass; growth in any dimension does not");
                var snapshot = CarriedIgnoredDeletes.Snapshot(fx.Store, fx.Store.Pair(fx.Pair.PairId)!, folder);
                var notice = CarriedIgnoredDeletes.Notice(fx.Pair.PairId, folder, snapshot);
                Require(notice.Detail.Contains("folder/cache/private.txt", StringComparison.Ordinal) &&
                    notice.Detail.Contains("7 bytes", StringComparison.Ordinal) && PlanNotices.AwaitsAcknowledgement(notice),
                    "folder delete notices list carried paths and bytes, retained for acknowledgement");
                var legacy = FirstSyncChoicesState.BindCarriedIgnoredBudgets(saved with { CarriedIgnoredDeletes = [] }, plan);
                Require(legacy.Actions.Single(action => action.Path == "folder").CarriedIgnoredBudget is
                    { Files: 0, Folders: 0, Bytes: 0 }, "old prepares never authorize undisclosed ignored content");
                Require(!bound.Actions.Any(action => action.Path.Contains("cache", StringComparison.Ordinal)),
                    "disclosure does not manufacture standalone ignored actions");
                var added = endpoint.Seed("folder/cache/extra.txt", "new");
                var rootId = side == Side.A ? fx.Pair.A.RootId : fx.Pair.B.RootId;
                var generation = fx.Store.BeginGeneration(rootId, "delta");
                fx.Store.WriteDelta(new Delta(rootId, side, generation, "", "",
                    [new Change(ChangeKind.Upsert, added.ObjectId, added.Path, added, null)], false, []));
                foreach (var descendantsPlanned in new[] { false, true })
                {
                    var guardedPlan = bound with { Actions = bound.Actions.Select(action =>
                        action.ActionId == folder.ActionId ? action with { DescendantsPlanned = descendantsPlanned } : action).ToArray() };
                    var calls = fx.WriterA.Calls + fx.WriterB.Calls;
                    var results = new List<ActionResult>();
                    var executor = new Executor(fx.Store, Settings, ExecutorChecks.Admission());
                    await foreach (var result in executor.ApplyAsync(guardedPlan, fx.WriterA, fx.WriterB, CancellationToken.None))
                        results.Add(result);
                    Require(results.Any(result => result.ActionId == folder.ActionId && result.Outcome == Outcome.Skipped &&
                        result.Reason == EffectResolver.Superseded) && calls == fx.WriterA.Calls + fx.WriterB.Calls,
                        "execution skips the entire folder on ignored aggregate growth, including DescendantsPlanned");
                    Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(item => item.ReasonKey == "folder-delete-ignored-growth"),
                        "execution explains skipped ignored growth with a notice");
                }
            }
            finally { Cleanup(root); }
        }
        foreach (var test in new[]
        {
            (Path: "folder/desktop.ini", Size: 65536, Rule: "FileName:desktop.ini", Notice: false),
            (Path: "folder/desktop.ini", Size: 65537, Rule: "FileName:desktop.ini", Notice: true),
            (Path: "folder/file.tmp", Size: 1, Rule: "FileExtension:tmp", Notice: true),
            (Path: "folder/cache/desktop.ini", Size: 1, Rule: "FileName:desktop.ini;FolderName:cache", Notice: true),
        })
        {
            var fx = Build();
            fx.A.Seed("folder", "", ItemKind.Folder);
            fx.A.Seed(test.Path, new string('x', test.Size));
            fx.Store.SavePair(fx.Pair with { IgnoreRules = test.Rule.Split(';') });
            var plan = await PreviewAsync(fx,
                """{"oneSided":[{"prefix":"","side":"local","action":"remove"}]}""");
            var folder = plan.Actions.Single(action => action.Kind == ActionKind.Delete && action.Path == "folder");
            Require(folder.CarriedIgnoredBudget is { Files: 1 } budget && budget.Bytes == test.Size,
                "trivial carried files remain in prepare budgets");
            var executor = new Executor(fx.Store, Settings, ExecutorChecks.Admission());
            await foreach (var result in executor.ApplyAsync(plan, fx.WriterA, fx.WriterB, CancellationToken.None)) { }
            Require(fx.Store.OpenNotices(fx.Pair.PairId).Any(notice =>
                    notice.ReasonKey == "folder-delete-carried-ignored") == test.Notice,
                "only nontrivial carried content raises an acknowledgement notice: " + test.Rule);
        }
        Console.WriteLine("PASS Cli_CarriedIgnored_PrepareBudgetAndNotice");
    }
}
