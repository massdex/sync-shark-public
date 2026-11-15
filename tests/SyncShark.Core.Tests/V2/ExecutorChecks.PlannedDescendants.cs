namespace Massdex.SyncShark.V2;

/// <summary>Executor checks for a folder Delete whose descendants keep their own Deletes (DescendantsPlanned).</summary>
internal static partial class ExecutorChecks
{
    private static async Task RunPlannedDescendantChecksAsync()
    {
        await PlannedDescendants_EachVictimOnce_ReplacementLands();
        await PlannedDescendants_FailedChild_FolderKept();
        await PlannedDescendants_IgnoredGrowthBlocksAllEffects();
    }

    // The child's own Delete runs once, the folder's Delete removes only the emptied folder, then the file lands.
    private static async Task PlannedDescendants_EachVictimOnce_ReplacementLands()
    {
        var (store, a, b, executor, folder, child) = PlannedDescendantsRig();
        var replacement = a.Seed("d", "file-content", versionId: "v1");
        var record = new RecordingWriter(b);
        var plan = Plan(
            Act(ActionKind.Delete, Side.B, "d/x", expected: child, rf: true, key: "kx", rule: 32),
            Act(ActionKind.Delete, Side.B, "d", expected: folder, key: "kd", rule: 32) with { DescendantsPlanned = true },
            Act(ActionKind.CopyNew, Side.B, "d", source: replacement, rule: 32));

        var results = await Apply(executor, plan, a, record);
        Require(results.Count == 3 && results.All(r => r.Outcome == Outcome.Done),
            "the planned child, the emptied folder and the replacement all complete");
        Require(record.Calls.Count(c => c == "Delete:d/x") == 1 && record.Preserves == 1,
            "the planned child is deleted (and preserved) exactly once");
        Require(Index(record.Calls, "Delete:d/x") < Index(record.Calls, "Delete:d") &&
            Index(record.Calls, "Delete:d") < Index(record.Calls, "Put:d"), "child, then folder, then the replacement");
        Require((await b.ObserveAsync("d", CancellationToken.None))!.Kind == ItemKind.File, "the replacement file landed");
    }

    // A failed planned child keeps the folder: its Delete is Skipped and the replacement cannot land over it.
    private static async Task PlannedDescendants_FailedChild_FolderKept()
    {
        var (store, a, b, executor, folder, child) = PlannedDescendantsRig();
        SeedBaseline(store, "kx", a.Seed("d/x", "child-content", versionId: "v1"), child);
        SeedBaseline(store, "kd", a.Seed("d", "", ItemKind.Folder, versionId: "v1"), folder);
        await RemoveSourceAsync(a, "d/x");
        await RemoveSourceAsync(a, "d");
        var record = new RecordingWriter(b) { FailDeleteAt = "d/x" };
        var childDelete = Act(ActionKind.Delete, Side.B, "d/x", expected: child, rf: true, key: "kx", rule: 6);
        var folderDelete = Act(ActionKind.Delete, Side.B, "d", expected: folder, key: "kd", rule: 6) with
        {
            DescendantsPlanned = true
        };

        var results = await Apply(executor, Plan(childDelete, folderDelete), a, record);
        Require(results.Count == 2, "no expanded descendant action is added");
        Require(results.Single(r => r.ActionId == childDelete.ActionId).Outcome == Outcome.Failed &&
            record.Calls.Count(c => c == "Delete:d/x") <= 1, "the failed child is attempted once");
        Require(results.Single(r => r.ActionId == folderDelete.ActionId) is
            { Outcome: Outcome.Skipped, Reason: "Folder not emptied: d/x" }, "the folder delete is Skipped");
        Require(!record.Calls.Contains("Delete:d") &&
            await b.ObserveAsync("d/x", CancellationToken.None) is not null, "the folder and its child remain");
    }

    private static async Task PlannedDescendants_IgnoredGrowthBlocksAllEffects()
    {
        foreach (var planned in new[] { false, true })
        {
            var (store, a, b, executor, folder, child) = PlannedDescendantsRig();
            var pair = store.Pairs().Single();
            store.SavePair(pair with { IgnoreRules = ["FileName:desktop.ini"] });
            var ignored = b.Seed("d/desktop.ini", "new ignored content", versionId: "v1");
            var gen = store.BeginGeneration("rb", "snapshot");
            store.WriteSnapshot(new Snapshot("rb", Side.B, gen, "2", [folder, child, ignored], []));
            store.CompleteGeneration(gen, "2", true);
            var record = new RecordingWriter(b);
            var folderDelete = Act(ActionKind.Delete, Side.B, "d", expected: folder, key: "kd", rule: 32) with
            {
                DescendantsPlanned = planned,
                CarriedIgnoredBudget = new CarriedIgnoredContent(0, 0, 0),
            };
            var childDelete = Act(ActionKind.Delete, Side.B, "d/x", expected: child, rf: true, key: "kx", rule: 32);
            var results = await Apply(executor, planned ? Plan(childDelete, folderDelete) : Plan(folderDelete), a, record);
            Require(results.Count == (planned ? 2 : 1) && results.All(result =>
                    result.Outcome == Outcome.Skipped && result.Reason == EffectResolver.Superseded),
                "ignored growth supersedes both expanded and already-planned folder subtrees");
            Require(record.Calls.Count == 0 && record.Preserves == 0,
                "ignored growth is checked before any child recovery or deletion");
            Require(store.OpenNotices(pair.PairId).Any(notice => notice.ReasonKey == "folder-delete-ignored-growth"),
                "ignored growth explains why the folder was skipped");
        }
    }

    private static (InMemoryStateStore, FakeEndpoint, FakeEndpoint, Executor, ItemState, ItemState) PlannedDescendantsRig()
    {
        var (store, a, b, executor) = Rig();
        var folder = b.Seed("d", "", ItemKind.Folder, versionId: "v1");
        var child = b.Seed("d/x", "child-content", versionId: "v1");
        var gen = store.BeginGeneration("rb", "snapshot");
        store.WriteSnapshot(new Snapshot("rb", Side.B, gen, "1", new[] { folder, child }, Array.Empty<string>()));
        store.CompleteGeneration(gen, "1", true);
        return (store, a, b, executor, folder, child);
    }
}
