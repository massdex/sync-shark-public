namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task DisplacedConflictOriginalAsync()
    {
        foreach (var mode in new[] { "restore", "changed", "foreign", "missing-proof", "cli" })
        {
            var root = LocalChecks.FreshRoot("displaced-conflict-" + mode);
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            var a = fx.A.Seed("Parent/capsules/work/item.json", "local version");
            var b = fx.B.Seed(a.Path, "cloud version");
            var copyA = fx.A.Seed("Parent/capsules/work/cloud-copy.json", "cloud version");
            var copyB = fx.B.Seed("Parent/capsules/work/local-copy.json", "local version");
            var basis = new Baseline(fx.Pair.PairId, "original-key", a, b, 1);
            var gen = store.BeginGeneration(fx.Pair.A.RootId, "fixture");
            void Adopt(Side side, ItemState left, ItemState right)
            {
                var key = PlanIds.Split(basis.ObjectKey, side);
                var action = new SyncAction(key, 16, ActionKind.Adopt, Side.B, right.Path, null, left, right, false, key);
                store.CommitResult(fx.Pair.PairId, gen, action, new(key, Outcome.Done, left, right, null, null));
            }
            Adopt(Side.A, a, copyB);
            Adopt(Side.B, copyA, b);
            await fx.A.DeleteAsync(new("external-remove", 6, ActionKind.Delete, Side.A, a.Path, null, null, a, false, null), default);
            store.AddNotices([new(fx.Pair.PairId, 27, NoticeKind.KeepBoth, a.Path, "both kept", "keep-both",
                ConflictVersions: new(mode == "missing-proof" ? a with { Hash = null } : a, b, basis, copyA.Path, copyB.Path))]);
            var notice = store.OpenNotices(fx.Pair.PairId).Single();
            if (mode == "changed") fx.A.Seed(copyA.Path, "later content");
            if (mode == "foreign") fx.A.Seed(a.Path, "foreign occupant");
            ActionResult? result = null;
            if (mode == "cli")
            {
                var runtime = new V2CliRuntime(null, store, fx.Run(), new EndpointRootProbe(fx.Provider), root);
                await SyncCliRegistry.DispatchAsync(runtime, SyncCliParser.Parse(["--cli", "conflicts", "resolve", "--pair", fx.Pair.PairId,
                    "--path", a.Path, "--choice", NoticeChoices.KeepB, "--key", notice.NoticeId!, "--generation", "0", "--yes"]), TextWriter.Null, default);
            }
            else result = await fx.Run().ResolveConflictNoticeAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.KeepB, null, default);
            if (mode is "restore" or "cli")
            {
                Require(result is null || result.Outcome == Outcome.Done, "displaced original choice completes: " + result?.Reason);
                Require((await MapAsync(fx.A)).Single().Value == b.Hash && (await MapAsync(fx.B)).Single().Value == b.Hash,
                    "canonical cloud content remains on both sides and both labelled copies leave the synced tree");
                Require(store.Recoveries(fx.Pair.PairId).Count == 2, "both removed copies have recovery provenance");
                var settled = store.Baselines(fx.Pair.PairId).Single();
                Require(settled.ObjectKey == PlanIds.Split(basis.ObjectKey, Side.B) && settled.A.Path == a.Path && settled.B.Path == b.Path,
                    "restore updates the winning correspondence and atomically retires the losing split");
                Require(store.OpenNotices(fx.Pair.PairId).Count == 0, "successful choice closes its notice");
            }
            else Require(result?.Outcome == Outcome.Skipped && fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 &&
                store.OpenNotices(fx.Pair.PairId).Count == 1, "changed, foreign or unproven occupants refuse with one notice and no effects");
        }
        Console.WriteLine("PASS DisplacedConflictOriginal");
    }
}
