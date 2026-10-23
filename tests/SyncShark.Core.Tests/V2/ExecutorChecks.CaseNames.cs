namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task CaseOnlyFolder_AdoptsAndChildrenProceed()
    {
        foreach (var firstSync in new[] { false, true })
        foreach (var target in new[] { Side.A, Side.B })
        foreach (var exact in new[] { false, true })
        {
            var (store, a, b, executor) = Rig();
            var folderA = a.Seed("Child/Folders", "", ItemKind.Folder);
            var folderB = b.Seed(exact ? "Child/Folders" : "Child/folders", "", ItemKind.Folder);
            var oldA = target == Side.B ? folderA with { Path = "Child/FOLDERS" } : folderA;
            var oldB = target == Side.A ? folderB with { Path = "Child/FOLDERS" } : folderB;
            SeedBaseline(store, "folder", oldA, oldB);
            var source = target == Side.A ? b : a;
            var child = source.Seed((target == Side.A ? folderB.Path : folderA.Path) + "/child.txt", "child");
            var rows = new[]
            {
                new PlanRow(store.Baselines("p1").Single(), folderA, folderB, new Evidence(false, false, false, false)),
                // This fixture observes both endpoints completely: the child is genuinely absent
                // at the destination, rather than missing from an unobserved subtree (C19).
                new PlanRow(null, target == Side.B ? child : null, target == Side.A ? child : null,
                    new Evidence(false, false, true, true)),
            };
            var options = new PlanOptions(TimeSpan.FromSeconds(2), firstSync,
                new LabelRule("{name} [{side} version {time}]{ext}", "d MMM HH.mm", TimeSpan.Zero, "Local", "Dropbox"), 10);
            var plan = Planner.Decide("p1", 2, rows, options);
            Require(plan.Actions.Count(x => x.Kind == ActionKind.Move) == (exact ? 0 : 1),
                "case-equivalent folder renames once unless spelling already matches");
            var results = await Apply(executor, plan, a, b);
            Require(results.Count == 2 && results.All(x => x.Outcome == Outcome.Done),
                $"folder rename/adoption and child both complete (firstSync={firstSync}, target={target}, exact={exact}); " +
                "plan=" + string.Join("; ", plan.Actions.Select(x => $"{x.ActionId}:{x.Kind}:{x.FromPath}->{x.Path}")) +
                "; results=" + string.Join("; ", results.Select(x => $"{x.ActionId}:{x.Outcome}:{x.Reason}")));
            Require(await (target == Side.A ? a : b).ObserveAsync(child.Path, CancellationToken.None) is not null,
                "child is not blocked by parent folder move");
            var saved = store.Baselines("p1").Single(x => x.ObjectKey == "folder");
            var destination = target == Side.A ? folderB.Path : folderA.Path;
            Require(saved.A.Path == destination && saved.B.Path == destination, "sync point records propagated spelling on both sides");
            var next = Planner.Decide("p1", 3, new[] { new PlanRow(saved,
                await a.ObserveAsync(destination, CancellationToken.None), await b.ObserveAsync(destination, CancellationToken.None),
                new Evidence(false, false, false, false)) }, options);
            Require(next.Actions.Count == 0, "folder does not re-plan on next run");
        }
    }

    /// <summary>
    /// A folder case rename carries descendants, preserves independent leaf renames and concurrent edits,
    /// and leaves converged exact baseline spellings with no difference notice.
    /// </summary>
    private static async Task CaseOnlyFolder_BaselinedChildren_AdoptEditOneNotice()
    {
        var (store, a, b, executor) = Rig();
        const string old = "Child/folders";
        var ev = new Evidence(false, false, false, false);
        var options = new PlanOptions(TimeSpan.FromSeconds(2), false,
            new LabelRule("{name} [{side} version {time}]{ext}", "d MMM HH.mm", TimeSpan.Zero, "Local", "Dropbox"), 10);

        var folderA = a.Seed("Child/Folders", "", ItemKind.Folder);
        var folderB = b.Seed(old, "", ItemKind.Folder);
        SeedBaseline(store, "folder", folderA with { Path = old }, folderB);

        // An unchanged child: only its ancestor's spelling differs, so it adopts silently.
        var childA = a.Seed("Child/Folders/child.txt", "child");
        var childB = b.Seed(old + "/child.txt", "child");
        SeedBaseline(store, "child", childA with { Path = old + "/child.txt" }, childB);

        // Its own leaf also changed case under the differing folder: the topmost notice covers it.
        var nestedA = a.Seed("Child/Folders/Notes.txt", "notes");
        var nestedB = b.Seed(old + "/notes.txt", "notes");
        SeedBaseline(store, "nested", nestedA with { Path = old + "/notes.txt" }, nestedB);

        // Edited while the ancestor moved by case only: the content edit must still propagate.
        var editedA = a.Seed("Child/Folders/edited.txt", "new content");
        var editedB = b.Seed(old + "/edited.txt", "old");
        SeedBaseline(store, "edited",
            editedA with { Path = old + "/edited.txt", Hash = editedB.Hash, Size = editedB.Size }, editedB);

        var baselines = store.Baselines("p1").ToDictionary(x => x.ObjectKey, StringComparer.Ordinal);
        var plan = Planner.Decide("p1", 2, new[]
        {
            new PlanRow(baselines["folder"], folderA, folderB, ev),
            new PlanRow(baselines["child"], childA, childB, ev),
            new PlanRow(baselines["nested"], nestedA, nestedB, ev),
            new PlanRow(baselines["edited"], editedA, editedB, ev),
        }, options);

        Require(plan.Actions.Count(x => x.Kind == ActionKind.Move) == 2,
            "one folder rename carries children and one independent leaf rename remains");
        Require(plan.Actions.All(x => x.Kind is ActionKind.Move or ActionKind.Update), "case rename never deletes or creates");
        var update = plan.Actions.Single(x => x.Kind == ActionKind.Update);
        Require(update.Target == Side.B && update.Path == editedA.Path && update.Source == editedA,
            "the edited child updates after its parent rename");
        Require(plan.Notices.Count == 0, "successful propagation needs no case-difference notice");

        var results = await Apply(executor, plan, a, b);
        Require(results.Count == 3 && results.All(x => x.Outcome == Outcome.Done), "renames and the edit all complete");

        var observed = await b.ObserveAsync(editedA.Path, CancellationToken.None);
        Require(observed is not null && observed.Hash == editedA.Hash && observed.Path == editedA.Path,
            "the edited child's content reaches the renamed target");
        Require((await b.ObserveAsync(childA.Path, CancellationToken.None))?.Path == childA.Path &&
            (await b.ObserveAsync(nestedA.Path, CancellationToken.None))?.Path == nestedA.Path,
            "parent spelling and independent child spelling both propagate");

        var saved = store.Baselines("p1").ToDictionary(x => x.ObjectKey, StringComparer.Ordinal);
        Require(saved["folder"].A.Path == folderA.Path && saved["folder"].B.Path == folderA.Path &&
            saved["child"].A.Path == childA.Path && saved["child"].B.Path == childA.Path &&
            saved["nested"].A.Path == nestedA.Path && saved["nested"].B.Path == nestedA.Path,
            "the sync point records the propagated exact spelling throughout the subtree; actual=" +
            string.Join("; ", saved.OrderBy(x => x.Key).Select(x => $"{x.Key}:A={x.Value.A.Path},B={x.Value.B.Path}")));

        var next = Planner.Decide("p1", 3, new[]
        {
            new PlanRow(saved["folder"], folderA, await b.ObserveAsync(folderA.Path, CancellationToken.None), ev),
            new PlanRow(saved["child"], childA, await b.ObserveAsync(childA.Path, CancellationToken.None), ev),
            new PlanRow(saved["nested"], nestedA, await b.ObserveAsync(nestedA.Path, CancellationToken.None), ev),
        }, options);
        Require(next.Actions.Count == 0 && next.Notices.Count == 0,
            "next run is stable: no actions and no repeated notice");
    }
}
