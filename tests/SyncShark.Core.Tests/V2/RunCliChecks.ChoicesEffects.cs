namespace Massdex.SyncShark.V2;

/// <summary>CLI first-sync choice effect checks: how each choice's remove/copy is counted (first-sync-choices.md).</summary>
internal static partial class RunCliChecks
{
    private static async Task RunFirstSyncChoiceEffectChecksAsync()
    {
        await Cli_Prepare_ReportsChoiceEffectsAsync();
        await Cli_Prepare_CountsCollapsedFolderOnceAsync();
        await Cli_Prepare_KeptFolderFollowsCopiedChildAsync();
        await Cli_Prepare_ChargesOnlyChoiceRemovesAsync();
        await Cli_Prepare_MixedFolderDeleteKeepsChoiceChildAsync(false);
        await Cli_Prepare_MixedFolderDeleteKeepsChoiceChildAsync(true);
        await Cli_Apply_MixedFolderReplacementLandsAsync();
    }

    // Applying the mixed case: the choice-removed child goes once, the emptied folder goes, the winner's file lands.
    private static async Task Cli_Apply_MixedFolderReplacementLandsAsync()
    {
        var fx = Build();
        fx.A.Seed("a", "", ItemKind.Folder);
        fx.A.Seed("a/x.txt", "hi");
        fx.B.Seed("a", "remote");
        var root = TempRoot();
        const string choices = """
            {"paths":{"a":"download"},"oneSided":[{"prefix":"a","side":"Local","action":"remove"}]}
            """;
        try
        {
            var plan = await PreviewAsync(fx, choices);
            Require(plan.Actions.Single(a => a is { Kind: ActionKind.Delete, Path: "a" }).DescendantsPlanned,
                "the uncollapsed mixed folder delete is marked as having planned descendants");
            var runtime = Runtime(fx, root);
            await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { },
                CancellationToken.None);
            Require(result.Failed == 0 && result.FirstSyncComplete, "the mixed apply completes without failures");
            Require((await fx.A.ObserveAsync("a", CancellationToken.None))?.Kind == ItemKind.File &&
                await fx.A.ObserveAsync("a/x.txt", CancellationToken.None) is null,
                "the child is removed, the folder is replaced by the winner's file");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Apply_MixedFolderReplacementLands");
    }

    private static async Task<Plan> PreviewAsync(Fixture fx, string choices)
    {
        var parsed = FirstSyncChoices.Parse(choices, fx.Pair);
        return await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, parsed.Winners, parsed.OneSided,
            new Progress<RunProgress>(), CancellationToken.None);
    }

    // A winner's folder replace (untagged) over a choice-removed child does not collapse, so the child stays charged;
    // post-preview children must survive without preventing independent reviewed work.
    private static async Task Cli_Prepare_MixedFolderDeleteKeepsChoiceChildAsync(bool childOnBothSides)
    {
        var fx = Build();
        var independent = fx.A.Seed("independent.txt", "safe");
        fx.A.Seed("a", "", ItemKind.Folder);
        fx.A.Seed("a/x.txt", "hi");
        fx.B.Seed("a", "remote");
        var root = TempRoot();
        const string choices = """
            {"paths":{"a":"download"},"oneSided":[{"prefix":"a","side":"Local","action":"remove"}]}
            """;
        try
        {
            var plan = await PreviewAsync(fx, choices);
            Require(plan.Actions.Any(a => a is { Kind: ActionKind.Delete, Rule: 32, Path: "a", ChoiceRemove: false }) &&
                plan.Actions.Any(a => a is { Kind: ActionKind.Delete, Rule: 32, Path: "a/x.txt", ChoiceRemove: true }) &&
                plan.Actions.All(a => a.Rule != 23),
                "a folder delete over victims of mixed provenance keeps its deletes separate");
            var runtime = Runtime(fx, root);
            var data = await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            Require(data.ChoiceEffects!.Single() is { Files: 1, Folders: 0, Bytes: 2 },
                "the choice-removed child under an untagged folder delete is charged");
            var newChild = fx.A.Seed("a/y.txt", "new");
            var remoteBefore = await fx.B.ObserveAsync("a", default);
            if (childOnBothSides)
            {
                fx.B.Seed("a", "", ItemKind.Folder);
                fx.B.Seed("a/y.txt", "new");
            }
            var result = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { }, default);
            Require(result.FirstSyncComplete && result.Failed == 0,
                "decision 85: post-preview child changes do not refuse the whole run");
            Require((await fx.A.ObserveAsync("a/y.txt", default))?.Hash == newChild.Hash &&
                (await fx.A.ObserveAsync("a", default))?.Kind == ItemKind.Folder,
                "new unreviewed child survives; reviewed folder replacement cannot remove it");
            if (childOnBothSides)
                Require((await fx.B.ObserveAsync("a/y.txt", default))?.Hash == newChild.Hash &&
                    (await fx.B.ObserveAsync("a", default))?.Kind == ItemKind.Folder,
                    "post-preview child and containing folder also survive on the remote side");
            else
                Require((await fx.B.ObserveAsync("a", default))?.Hash == remoteBefore!.Hash,
                    "unchanged remote winner survives the protected local folder replacement");
            Require((await fx.B.ObserveAsync("independent.txt", default))?.Hash == independent.Hash,
                "independent reviewed work proceeds while the changed folder is protected");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Prepare_MixedFolderDeleteKeepsChoiceChild");
    }

    // Mixed rules under one remove prefix: a winner's file replace (rule 32, not a remove choice) and a winner's
    // folder replace that collapses (rule 23 of a non-choice delete) are not charged; only the one-sided file is.
    private static async Task Cli_Prepare_ChargesOnlyChoiceRemovesAsync()
    {
        var fx = Build();
        fx.A.Seed("a", "", ItemKind.Folder);
        fx.A.Seed("a/x.txt", "hi");
        fx.A.Seed("a/k", "abc");
        fx.B.Seed("a", "", ItemKind.Folder);
        fx.B.Seed("a/k", "", ItemKind.Folder);
        fx.A.Seed("a/d", "", ItemKind.Folder);
        fx.B.Seed("a/d", "remote");
        var root = TempRoot();
        const string choices = """
            {"paths":{"a/k":"download","a/d":"download"},
             "oneSided":[{"prefix":"a","side":"Local","action":"remove"}]}
            """;
        try
        {
            var plan = await PreviewAsync(fx, choices);
            Require(plan.Actions.Any(a => a is { Kind: ActionKind.Delete, Rule: 32, Path: "a/k", ChoiceRemove: false }) &&
                plan.Actions.Any(a => a is { Kind: ActionKind.Delete, Rule: 23, Path: "a/d", ChoiceRemove: false }),
                "the plan holds a winner's rule-32 delete and a rule-23 collapse of it under the remove prefix");
            var data = await Runtime(fx, root).PrepareFirstSyncAsync(fx.Pair.PairId, _ => { },
                CancellationToken.None, choices);
            var remove = data.ChoiceEffects!.Single();
            Require(remove is { Files: 1, Folders: 0, Bytes: 2 },
                $"only the one-sided remove is charged, not winner replaces ({remove.Files}/{remove.Folders}/{remove.Bytes})");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Prepare_ChargesOnlyChoiceRemoves");
    }

    // Prepare previews the choices and reports one resolved effect per declared choice, with no writer call.
    private static async Task Cli_Prepare_ReportsChoiceEffectsAsync()
    {
        var fx = Build();
        fx.A.Seed("a/x.txt", "hello");
        fx.A.Seed("a/sub/deep.bin", "xy");
        fx.A.Seed("b/y.txt", "copy-me");
        var root = TempRoot();
        try
        {
            var data = await Runtime(fx, root).PrepareFirstSyncAsync(fx.Pair.PairId, _ => { },
                CancellationToken.None, """
                {"oneSided":[{"prefix":"a","side":"Local","action":"remove"},
                             {"prefix":"b/","side":"Local","action":"copy"}]}
                """);
            var remove = data.ChoiceEffects!.Single(effect => effect is { Action: "remove", Prefix: "a" });
            var copy = data.ChoiceEffects!.Single(effect => effect is { Action: "copy", Prefix: "b" });
            Require(remove is { Files: 2, Folders: 0, Bytes: 7 } && remove.Side == "Local",
                "the remove choice reports its two files and their bytes");
            Require(copy is { Files: 1, Bytes: 7 }, "the copy choice reports its one file and its bytes");
            Require(data.ChoicesDigest is { Length: 64 }, "prepare reports the digest of the normalized choices");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "a preparation preview makes no writer call");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Prepare_ReportsChoiceEffects");
    }

    // Two removes where the deeper one owns its own subtree: the collapsed folder delete is expanded, each
    // item is counted once under the choice that owns its path, and the folder itself is not counted twice.
    private static async Task Cli_Prepare_CountsCollapsedFolderOnceAsync()
    {
        var fx = Build();
        fx.A.Seed("a", "", ItemKind.Folder);
        fx.A.Seed("a/x.txt", "hi");
        fx.A.Seed("a/sub", "", ItemKind.Folder);
        fx.A.Seed("a/sub/deep.bin", "xyz");
        var root = TempRoot();
        const string choices = """
            {"oneSided":[{"prefix":"a","side":"Local","action":"remove"},
                         {"prefix":"a/sub","side":"Local","action":"remove"}]}
            """;
        try
        {
            var plan = await PreviewAsync(fx, choices);
            Require(plan.Actions.Count(a => a.Kind == ActionKind.Delete) == 1 &&
                plan.Actions.Single(a => a.Kind == ActionKind.Delete) is { Rule: 23, Path: "a", ChoiceRemove: true },
                "deletes that are all choice removes still collapse into one tagged folder delete");
            var runtime = Runtime(fx, root);
            var data = await runtime.PrepareFirstSyncAsync(fx.Pair.PairId, _ => { }, CancellationToken.None, choices);
            var parent =data.ChoiceEffects!.Single(effect => effect.Prefix == "a");
            var child = data.ChoiceEffects!.Single(effect => effect.Prefix == "a/sub");
            Require(parent is { Files: 1, Folders: 1, Bytes: 2 },
                "the collapsed parent charges its own folder and file once");
            Require(child is { Files: 1, Folders: 1, Bytes: 3 },
                "the longer choice owns its own subtree, counted once");
            var applied = await runtime.ApplyFirstSyncAsync(fx.Pair.PairId, choices, false, null, _ => { },
                CancellationToken.None);
            Require(applied.ChoiceEffects!.Single(effect => effect.Prefix == "a") is { Files: 1, Folders: 1, Bytes: 2 },
                "apply reports the same effects it resolved before executing");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Prepare_CountsCollapsedFolderOnce");
    }

    // A folder kept for a copied child (rule 24) is charged to the copy choice that kept it, not to the remove
    // choice whose delete it replaced.
    private static async Task Cli_Prepare_KeptFolderFollowsCopiedChildAsync()
    {
        var fx = Build();
        fx.A.Seed("a", "", ItemKind.Folder);
        fx.A.Seed("a/x.txt", "hi");
        fx.A.Seed("a/sub", "", ItemKind.Folder);
        fx.A.Seed("a/sub/deep.bin", "xyz");
        var root = TempRoot();
        try
        {
            var data = await Runtime(fx, root).PrepareFirstSyncAsync(fx.Pair.PairId, _ => { },
                CancellationToken.None, """
                {"oneSided":[{"prefix":"a","side":"Local","action":"remove"},
                             {"prefix":"a/sub","side":"Local","action":"copy"}]}
                """);
            var remove = data.ChoiceEffects!.Single(effect => effect.Action == "remove");
            var copy = data.ChoiceEffects!.Single(effect => effect.Action == "copy");
            Require(remove is { Files: 1, Folders: 0, Bytes: 2 },
                "the remove choice charges only the file it could still delete");
            Require(copy is { Files: 1, Folders: 2, Bytes: 3 },
                "the copy choice owns its subtree and the parent folder kept for it");
        }
        finally
        {
            Cleanup(root);
        }
        Console.WriteLine("PASS Cli_Prepare_KeptFolderFollowsCopiedChild");
    }
}
