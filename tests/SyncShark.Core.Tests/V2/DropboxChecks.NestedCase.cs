namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task NestedFolderCaseLocalAsync()
    {
        foreach (var parent in new[] { "Work", "work" })
        {
            var remoteParent = parent == "Work" ? "work" : "Work";
            var directory = LocalChecks.FreshRoot("nested-case-" + Guid.NewGuid().ToString("N"));
            var local = Path.Combine(directory, "local");
            Directory.CreateDirectory(Path.Combine(local, parent, "CAPSULES"));
            await File.WriteAllTextAsync(Path.Combine(local, parent, "CAPSULES", "child.txt"), "retained child");
            using var h = new Harness();
            using var store = SqliteStateStore.Open(Path.Combine(directory, "state"), StorageChecks.Fast,
                TimeProvider.System, LocalItems.CaptureOptionalHash);
            var root = LocalChecks.RootOf(local, "nested-local");
            var pair = new PairDef("nested-case", "Nested case", root, Root(), [], [], true);
            store.SavePair(pair);
            var writer = await LocalWriter.OpenAsync(root, Side.A, pair.PairId, LocalChecks.InstallationId,
                Path.Combine(directory, "recovery"), default, isStateDamage: StorageFailureClassifier.IsStateDamage, StorageChecks.Fast);
            var before = (await writer.ObserveAsync(parent + "/CAPSULES", default))!;
            var child = (await writer.ObserveAsync(parent + "/CAPSULES/child.txt", default))!;
            h.Wire.SeedFolder(remoteParent);
            h.Wire.SeedFolder(remoteParent + "/capsules");
            var remote = h.Writer();
            var source = (await remote.ObserveAsync(remoteParent + "/capsules", default))!;
            var basis = new Baseline(pair.PairId, "nested-folder", before with { Path = parent + "/Capsules" },
                source with { Path = remoteParent + "/Capsules" }, 1);
            var options = new PlanOptions(TimeSpan.FromSeconds(2), false,
                new LabelRule("{name} [{side}]{ext}", "s", TimeSpan.Zero, "A", "B"), 10,
                new Dictionary<string, Side?> { [before.Path] = Side.B });
            var plan = Planner.Decide(pair.PairId, 2, [new PlanRow(basis, before, source, new(false, false, true, true))], options);
            Require(plan.Actions.Single() is { Kind: ActionKind.Move } move && move.Path == parent + "/capsules",
                "nested folder choice preserves the local parent spelling and changes only the leaf");
            var executor = new Executor(store, StorageChecks.Fast, ExecutorChecks.Admission(),
                isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
            await foreach (var result in executor.ApplyAsync(plan, writer, remote, default))
                Require(result.Outcome == Outcome.Done, "native nested folder choice settles without retry: " + result.Reason);
            var after = (await writer.ObserveAsync(parent + "/capsules", default))!;
            var afterChild = (await writer.ObserveAsync(parent + "/capsules/child.txt", default))!;
            Require(after.ObjectId == before.ObjectId && after.Path == parent + "/capsules" &&
                afterChild.ObjectId == child.ObjectId && afterChild.Hash == child.Hash &&
                store.UnresolvedIntents(pair.PairId).Count == 0,
                "real local store confirms exact target spelling, unchanged folder and child identities, and no pending Move");
        }
    }
}
