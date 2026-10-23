namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static void QuarantineReviewedBindingChecks(string root)
    {
        foreach (var side in new[] { Side.A, Side.B })
        {
            using var store = SqliteStateStore.Open(Path.Combine(root, $"reviewed-binding-{side}"), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            var rootId = side == Side.A ? "ra" : "rb";
            var reviewed = new ItemState("reviewed.txt", ItemKind.File, "reviewed-entity", "reviewed-version",
                new string('a', 64), 1, DateTimeOffset.UtcNow, ItemFlags.None);
            var replacement = reviewed with { ObjectId = "replacement-entity", Hash = new string('b', 64) };
            var gen = store.BeginGeneration(rootId, "seed");
            store.WriteSnapshot(new(rootId, side, gen, "", [replacement], []));
            var notice = new Notice("p1", 30, NoticeKind.UserDecision, reviewed.Path, "reviewed hold", QuarantineFailure.NoticeKey,
                Quarantine: new(side, null, reviewed, true));
            store.AddNotices([notice]);
            Require(store.OpenNotices("p1").Single().Quarantine!.ExpectedTarget == reviewed,
                $"{side}: explicit review never adopts the replacement occupying its path");

            var changed = reviewed with { VersionId = "new-version", Hash = new string('c', 64), Size = 2 };
            store.WriteSnapshot(new(rootId, side, store.BeginGeneration(rootId, "changed"), "", [changed], []));
            store.AddNotices([notice]);
            Require(store.OpenNotices("p1").Single().Quarantine!.ExpectedTarget == reviewed,
                $"{side}: explicit review retains its own version and bytes despite a newer live row");

            store.WriteSnapshot(new(rootId, side, store.BeginGeneration(rootId, "replaced"), "", [replacement], []));
            var targetSide = side == Side.A ? Side.B : Side.A;
            var action = new SyncAction("binding-failure", 30, ActionKind.CopyNew, targetSide, reviewed.Path,
                null, reviewed, null, false, "reviewed-key");
            store.RecordIntents("p1", [action]);
            store.CommitResult("p1", gen, action,
                new ActionResult(action.ActionId, Outcome.Skipped, null, null, null, "test quarantine") { Quarantined = true });
            var committed = store.OpenNotices("p1").Single(n => n.Quarantine?.Source is not null).Quarantine!;
            Require(committed.Source == reviewed,
                $"{side}: quarantined result capture retains the failed entity rather than a same-path replacement");
        }
    }
}
