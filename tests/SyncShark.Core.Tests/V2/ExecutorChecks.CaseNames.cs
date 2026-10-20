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
            var folderA = a.Seed("Child/Capsules", "", ItemKind.Folder);
            var folderB = b.Seed(exact ? "Child/Capsules" : "Child/capsules", "", ItemKind.Folder);
            var oldA = target == Side.B ? folderA with { Path = "Child/CAPSULES" } : folderA;
            var oldB = target == Side.A ? folderB with { Path = "Child/CAPSULES" } : folderB;
            SeedBaseline(store, "folder", oldA, oldB);
            var source = target == Side.A ? b : a;
            var child = source.Seed((target == Side.A ? folderB.Path : folderA.Path) + "/child.txt", "child");
            var rows = new[]
            {
                new PlanRow(store.Baselines("p1").Single(), folderA, folderB, new Evidence(false, false, false, false)),
                new PlanRow(null, target == Side.B ? child : null, target == Side.A ? child : null,
                    new Evidence(false, false, false, false)),
            };
            var options = new PlanOptions(TimeSpan.FromSeconds(2), firstSync,
                new LabelRule("{name} [{side} version {time}]{ext}", "d MMM HH.mm", TimeSpan.Zero, "Local", "Dropbox"), 10);
            var plan = Planner.Decide("p1", 2, rows, options);
            Require(!plan.Actions.Any(x => x.Kind == ActionKind.Move), "case-equivalent folder emits no filesystem move");
            var results = await Apply(executor, plan, a, b);
            Require(results.Count == 2 && results.All(x => x.Outcome == Outcome.Done), "folder adoption and child both complete");
            Require(await (target == Side.A ? a : b).ObserveAsync(child.Path, CancellationToken.None) is not null,
                "child is not blocked by parent folder move");
            var saved = store.Baselines("p1").Single(x => x.ObjectKey == "folder");
            Require(saved.A.Path == folderA.Path && saved.B.Path == folderB.Path, "sync point records both actual spellings");
            var next = Planner.Decide("p1", 3, new[] { new PlanRow(saved, folderA, folderB,
                new Evidence(false, false, false, false)) }, options);
            Require(next.Actions.Count == 0, "folder does not re-plan on next run");
        }
    }

    /// <summary>
    /// The real Child/Capsules shape: the local folder was renamed by case only and every child baseline still
    /// carries the old spelling. Adoption and edits proceed without any Move, and the whole subtree raises exactly
    /// one CaseOnlyNames Info: the topmost differing folder's.
    /// </summary>
    private static async Task CaseOnlyFolder_BaselinedChildren_AdoptEditOneNotice()
    {
        var (store, a, b, executor) = Rig();
        const string old = "Child/capsules";
        var ev = new Evidence(false, false, false, false);
        var options = new PlanOptions(TimeSpan.FromSeconds(2), false,
            new LabelRule("{name} [{side} version {time}]{ext}", "d MMM HH.mm", TimeSpan.Zero, "Local", "Dropbox"), 10);

        var folderA = a.Seed("Child/Capsules", "", ItemKind.Folder);
        var folderB = b.Seed(old, "", ItemKind.Folder);
        SeedBaseline(store, "folder", folderA with { Path = old }, folderB);

        // An unchanged child: only its ancestor's spelling differs, so it adopts silently.
        var childA = a.Seed("Child/Capsules/child.txt", "child");
        var childB = b.Seed(old + "/child.txt", "child");
        SeedBaseline(store, "child", childA with { Path = old + "/child.txt" }, childB);

        // Its own leaf also changed case under the differing folder: the topmost notice covers it.
        var nestedA = a.Seed("Child/Capsules/Notes.txt", "notes");
        var nestedB = b.Seed(old + "/notes.txt", "notes");
        SeedBaseline(store, "nested", nestedA with { Path = old + "/notes.txt" }, nestedB);

        // Edited while the ancestor moved by case only: the content edit must still propagate.
        var editedA = a.Seed("Child/Capsules/edited.txt", "new content");
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

        Require(!plan.Actions.Any(x => x.Kind == ActionKind.Move), "no filesystem move for the case-equivalent subtree");
        Require(plan.Actions.Count(x => x.Kind == ActionKind.Adopt) == 3, "the folder and both unchanged children adopt");
        var update = plan.Actions.Single(x => x.Kind == ActionKind.Update);
        Require(update.Target == Side.B && update.Path == old + "/edited.txt" && update.Source == editedA,
            "the edited child updates the target's actual spelling");
        Require(plan.Notices.Count == 1 && plan.Notices.Single().Path == folderB.Path &&
            plan.Notices.Single().ReasonKey == PlanNotices.CaseOnlyNames.Key,
            "exactly one case notice, on the topmost differing folder");

        var results = await Apply(executor, plan, a, b);
        Require(results.Count == 4 && results.All(x => x.Outcome == Outcome.Done), "adoptions and the edit all complete");

        var observed = await b.ObserveAsync(old + "/edited.txt", CancellationToken.None);
        Require(observed is not null && observed.Hash == editedA.Hash,
            "the edited child's content reaches the target at its own spelling");
        Require(await b.ObserveAsync(old + "/child.txt", CancellationToken.None) is not null &&
            await b.ObserveAsync(old + "/notes.txt", CancellationToken.None) is not null,
            "children stay where they are; nothing is moved or renamed");

        var saved = store.Baselines("p1").ToDictionary(x => x.ObjectKey, StringComparer.Ordinal);
        Require(saved["folder"].A.Path == folderA.Path && saved["folder"].B.Path == folderB.Path &&
            saved["child"].A.Path == childA.Path && saved["child"].B.Path == childB.Path,
            "the sync point records both actual spellings for the subtree");

        var next = Planner.Decide("p1", 3, new[]
        {
            new PlanRow(saved["folder"], folderA, folderB, ev),
            new PlanRow(saved["child"], childA, childB, ev),
            new PlanRow(saved["nested"], nestedA, nestedB, ev),
        }, options);
        Require(next.Actions.Count == 0 && next.Notices.Count == 0,
            "next run is stable: no actions and no repeated notice");
    }
}
