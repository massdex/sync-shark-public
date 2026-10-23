namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static void QuarantineGrantPublicationRecoveryChecks(string root)
    {
        var state = Path.Combine(root, "grant-publication-recovery");
        var localRoot = Path.Combine(root, "grant-publication-local");
        Directory.CreateDirectory(localRoot);
        var item = new ItemState("held.txt", ItemKind.File, "held-entity", null, null, 3,
            DateTimeOffset.UtcNow, ItemFlags.None);
        var path = AttendedElevatedAction.ValidatePath(localRoot, item.Path);
        var grant = new DefenderGrant("publication-grant", "prior-owner", null, null, path,
            AttendedElevatedAction.DownloadStagePath(path));
        using (var store = SqliteStateStore.Open(state, Settings, TimeProvider.System))
        {
            var fx = Build(suppliedStore: store);
            store.SavePair(fx.Pair with { A = fx.Pair.A with { RootPath = localRoot } });
            store.StoreDefenderGrant(grant);
            store.DefenderCaptureForTest = _ => throw new IOException("publication-interruption");
            var interrupted = false;
            try { store.WriteSnapshot(new("ra", Side.A, store.BeginGeneration("ra", "seed"), "", [item], [])); }
            catch (IOException error) when (error.Message == "publication-interruption") { interrupted = true; }
            Require(interrupted && store.Live("ra").Single().ObjectId == item.ObjectId &&
                store.OpenNotices("p1").Count == 0 && store.DefenderGrantById(grant.GrantId) == grant &&
                store.DefenderGrantHoldPaths("ra").Contains(item.Path),
                "interruption after snapshot commit leaves durable ungranted path protection before notice publication");
        }
        using (var store = SqliteStateStore.Open(state, Settings, TimeProvider.System))
        {
            var gate = (FairStoreGate)typeof(SqliteStateStore).GetField("_gate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            var captures = 0;
            store.DefenderCaptureForTest = observed =>
            {
                Require(!gate.IsHeldByCurrentThread, "grant review hashes outside the store gate");
                captures++;
                return observed with { Hash = new string('a', 64) };
            };
            void Observe() => store.WriteDelta(new("ra", Side.A, store.BeginGeneration("ra", "resume"), "", "", [], false, []));
            Observe();
            var notice = store.OpenNotices("p1").Single();
            Require(notice.Quarantine!.ExpectedTarget!.Hash == new string('a', 64) && captures == 1 &&
                store.DefenderGrantById(grant.GrantId) == grant && store.Live("ra").Single().Hash is null,
                "reopen and empty delta re-derive hashed review without granting or hashing inventory");
            Observe();
            Require(store.OpenNotices("p1").Single().NoticeId == notice.NoticeId && captures == 1 &&
                store.DefenderGrantById(grant.GrantId) == grant,
                "grant publication replay is singular and preserves reviewed hash without rereading");
        }
    }

    private static void QuarantineReviewedBindingChecks(string root)
    {
        QuarantineGrantPublicationRecoveryChecks(root);
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
