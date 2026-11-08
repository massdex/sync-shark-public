namespace Massdex.SyncShark.V2;

/// <summary>'pairs create --exclude' checks (exclusions.md minimal slice): saved, shown, validated, and honoured by the first sync.</summary>
internal static partial class RunCliChecks
{
    private const string Kept = "keep-out";

    private static async Task RunCreateExcludeChecksAsync()
    {
        await Cli_CreatePair_ExcludeSavedAndShownAsync();
        await Cli_CreatePair_ExcludeRefusalsSaveNothingAsync();
        await FirstSync_RemoveWholeB_LeavesExcludedFolderAsync();
        await FirstSync_ExcludedOnlyOnA_NotUploadedAsync();
        await FirstSync_ExcludedOnBoth_UntouchedAsync();
    }

    private static V2CliRuntime CreateRuntime(Fixture fx)
    {
        var provider = new GatedObserveProvider();
        provider.Release();
        return new V2CliRuntime(null, fx.Store, fx.Run(), new EndpointRootProbe(provider), "C:\\root");
    }

    private static string[] CreateArgs(params string[] excludes)
    {
        var args = new List<string> { "--cli", "pairs", "create", "--account", "acc", "--path", "/fresh", "--local",
            "C:\\fresh" };
        foreach (var exclude in excludes) args.AddRange(["--exclude", exclude]);
        args.Add("--yes");
        return args.ToArray();
    }

    // Repeated --exclude values are saved trimmed and deduplicated ignoring case; pairs show lists them as excluded.
    private static async Task Cli_CreatePair_ExcludeSavedAndShownAsync()
    {
        var fx = Build();
        var runtime = CreateRuntime(fx);
        var created = await DispatchAsync(runtime, CreateArgs(Kept + "/", "KEEP-OUT", "docs/old"));
        var id = ((SyncCliPairData)created).Id;
        Require(fx.Store.Pair(id)?.Excluded.SequenceEqual([Kept, "docs/old"]) == true,
            "create saves each exclusion once, without a trailing '/'");
        var shown = (SyncCliPairData)await DispatchAsync(runtime, ["--cli", "pairs", "show", "--pair", id]);
        Require(shown.SelectionRuleCount == 2 && shown.SelectionRules!.Select(rule => rule.FolderPath)
                .SequenceEqual([Kept, "docs/old"]) && shown.SelectionRules!.All(rule => !rule.Included),
            "pairs show lists the exclusions as excluded selection rules");
        Console.WriteLine("PASS Cli_CreatePair_ExcludeSavedAndShown");
    }

    // A bad path, or --exclude with the --settings document, is a usage error and nothing is saved.
    private static async Task Cli_CreatePair_ExcludeRefusalsSaveNothingAsync()
    {
        var fx = Build();
        var runtime = CreateRuntime(fx);
        foreach (var bad in new[] { "/lead", "back\\slash", "a/../b", "a/./b", "a//b", "", "/" })
        {
            var refusal = await RefusalAsync(() => DispatchAsync(runtime, CreateArgs(Kept, bad)));
            Require(refusal is SyncCliUsageException && refusal.Message.Contains("--exclude", StringComparison.Ordinal),
                $"the exclusion '{bad}' is refused as a usage error");
        }
        var settings = await RefusalAsync(() => DispatchAsync(runtime, ["--cli", "pairs", "create", "--settings",
            "{\"left\":{},\"right\":{},\"direction\":\"TwoWay\"}", "--exclude", Kept, "--yes"]));
        Require(settings is SyncCliUsageException, "--exclude with the --settings document is refused");
        Require(fx.Store.Pairs().Count == 1, "a refused create saves nothing");
        Console.WriteLine("PASS Cli_CreatePair_ExcludeRefusalsSaveNothing");
    }

    // A whole-pair remove choice for side B never touches the excluded folder that only side B holds: no delete,
    // no rule-32 effect, not charged in the choice effects, and the pair raises its exclusion notice.
    private static async Task FirstSync_RemoveWholeB_LeavesExcludedFolderAsync()
    {
        var fx = Build();
        fx.Store.SavePair(fx.Pair with { Excluded = [Kept] });
        fx.B.Seed(Kept, "", ItemKind.Folder);
        fx.B.Seed(Kept + "/f.txt", "cloud-only");
        fx.B.Seed("other", "", ItemKind.Folder);
        fx.B.Seed("other/g.txt", "abc");
        var root = TempRoot();
        const string choices = """{"oneSided":[{"prefix":"","side":"Dropbox","action":"remove"}]}""";
        try
        {
            var plan = await PreviewAsync(fx, choices);
            Require(plan.Actions.Count > 0 && plan.Actions.All(action => !UnderKept(action.Path)),
                "the plan removes the other folder and plans nothing under the excluded folder");
            Require(plan.Actions.All(action => action.Path.Length > 0), "no action targets the pair root");
            var runtime = Runtime(fx, root);
            var data = await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            Require(data.ChoiceEffects!.Single() is { Files: 1, Folders: 1, Bytes: 3 },
                "the remove choice charges only the non-excluded folder and file");
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete, "the first sync completes");
            Require(await fx.B.ObserveAsync(Kept + "/f.txt", CancellationToken.None) is not null &&
                await fx.B.ObserveAsync("other/g.txt", CancellationToken.None) is null &&
                await fx.A.ObserveAsync(Kept + "/f.txt", CancellationToken.None) is null,
                "the excluded file stays on side B only; the other file is removed");
            var notice = fx.Store.OpenNotices(fx.Pair.PairId).Single(n => n.ReasonKey == "pair-excluded");
            Require(notice.Kind == NoticeKind.Info && notice.Detail.Contains(Kept, StringComparison.Ordinal),
                "the active exclusion is an open info notice that lists its path");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS FirstSync_RemoveWholeB_LeavesExcludedFolder");
    }

    // An excluded folder that only side A holds is never uploaded; the rest is.
    private static async Task FirstSync_ExcludedOnlyOnA_NotUploadedAsync()
    {
        var fx = Build();
        fx.Store.SavePair(fx.Pair with { Excluded = [Kept] });
        fx.A.Seed(Kept, "", ItemKind.Folder);
        fx.A.Seed(Kept + "/f.txt", "local-only");
        fx.A.Seed("up.txt", "up");
        var report = await FirstSyncAsync(fx);
        Require(report.Failed == 0 && fx.Store.FirstSyncDone(fx.Pair.PairId), "the first sync completes");
        Require(await fx.B.ObserveAsync("up.txt", CancellationToken.None) is not null &&
            await fx.B.ObserveAsync(Kept, CancellationToken.None) is null &&
            await fx.B.ObserveAsync(Kept + "/f.txt", CancellationToken.None) is null &&
            await fx.A.ObserveAsync(Kept + "/f.txt", CancellationToken.None) is not null,
            "the excluded folder is neither uploaded nor removed locally");
        Console.WriteLine("PASS FirstSync_ExcludedOnlyOnA_NotUploaded");
    }

    // Differing copies in an excluded folder on both sides are left as they are: no keep-both, no transfer.
    private static async Task FirstSync_ExcludedOnBoth_UntouchedAsync()
    {
        var fx = Build();
        fx.Store.SavePair(fx.Pair with { Excluded = [Kept] });
        fx.A.Seed(Kept, "", ItemKind.Folder);
        fx.A.Seed(Kept + "/f.txt", "local");
        fx.B.Seed(Kept, "", ItemKind.Folder);
        fx.B.Seed(Kept + "/f.txt", "remote version");
        var run = fx.Run();
        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(plan.Actions.All(action => !UnderKept(action.Path)), "nothing is planned under the excluded folder");
        await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
        Require((await fx.A.ObserveAsync(Kept + "/f.txt", CancellationToken.None))?.Size == 5 &&
            (await fx.B.ObserveAsync(Kept + "/f.txt", CancellationToken.None))?.Size == 14 &&
            fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
            "both copies stay as they were and no writer is called");
        Console.WriteLine("PASS FirstSync_ExcludedOnBoth_Untouched");
    }

    private static bool UnderKept(string path) =>
        string.Equals(path, Kept, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Kept + "/", StringComparison.OrdinalIgnoreCase);
}