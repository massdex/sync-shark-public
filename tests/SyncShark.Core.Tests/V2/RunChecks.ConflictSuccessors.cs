namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task DisplacedConflictOriginalAsync()
    {
        foreach (var winner in new[] { Side.A, Side.B })
        foreach (var mode in new[] { "restore", "changed", "foreign", "missing-proof", "cli", "null-hash",
            "unhashable", "missing", "protected", "wrong-choice", "restore-fails" })
        {
            var root = LocalChecks.FreshRoot("displaced-conflict-" + winner + "-" + mode);
            var store = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            try
            {
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
                store.MarkFirstSyncDone(fx.Pair.PairId);
                var target = winner == Side.A ? fx.B : fx.A;
                var targetSide = winner == Side.A ? Side.B : Side.A;
                var absent = winner == Side.A ? b : a;
                await target.DeleteAsync(new("external-remove", 6, ActionKind.Delete, targetSide,
                    absent.Path, null, null, absent, false, null), default);
                store.AddNotices([new(fx.Pair.PairId, 27, NoticeKind.KeepBoth, a.Path, "both kept", "keep-both",
                    ConflictVersions: new(mode == "missing-proof" ? a with { Hash = null } : a, b, basis, copyA.Path, copyB.Path))]);
                var noticeId = store.OpenNotices(fx.Pair.PairId).Single().NoticeId!;
                // Cold-open every choice: provenance and checkpoints must come from durable state.
                store.Dispose();
                store = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
                var run = new SyncRun(store, NewExecutor(store), fx.Provider, Settings, TimeProvider.System);
                if (mode == "changed") fx.B.Seed(copyB.Path, "later content");
                if (mode is "foreign" or "missing")
                {
                    await fx.B.DeleteAsync(new("external-copy-remove", 6, ActionKind.Delete, Side.B,
                        copyB.Path, null, null, copyB, false, null), default);
                    if (mode == "foreign") fx.B.Seed(copyB.Path, "local version");
                }
                if (mode == "protected") store.SavePair(fx.Pair with { Excluded = [copyA.Path] });
                if (mode is "null-hash" or "unhashable") fx.A.ObserveWithoutHashes = true;
                if (mode == "unhashable") { fx.A.Unhashable.Add(copyA.Path); fx.A.Unhashable.Add(a.Path); }
                if (mode == "restore-fails") target.FailNextWrites = 1;
                var choice = winner == Side.A ? NoticeChoices.KeepA : NoticeChoices.KeepB;
                if (mode == "wrong-choice") choice = winner == Side.A ? NoticeChoices.KeepB : NoticeChoices.KeepA;
                ActionResult? result = null;
                if (mode == "cli")
                {
                    var runtime = new V2CliRuntime(null, store, run, new EndpointRootProbe(fx.Provider), root);
                    await SyncCliRegistry.DispatchAsync(runtime, SyncCliParser.Parse(["--cli", "conflicts", "resolve", "--pair", fx.Pair.PairId,
                        "--path", a.Path, "--choice", choice, "--key", noticeId, "--generation", "0", "--yes"]), TextWriter.Null, default);
                }
                else result = await run.ResolveConflictNoticeAsync(fx.Pair.PairId, noticeId, choice, null, default);
                if (mode is "null-hash" or "unhashable")
                    Require(fx.A.HashedPaths.Count > 0, "null local observation requests adapter content proof");
                if (mode == "unhashable")
                    Require(result?.Reason == MessageCatalog.Default.Message("conflict-successor-proof"),
                        "unavailable fresh proof has a specific visible reason");
                if (mode is "restore" or "cli" or "null-hash")
                {
                    var expected = winner == Side.A ? a.Hash : b.Hash;
                    Require(result is null || result.Outcome == Outcome.Done, "displaced original choice completes: " + result?.Reason);
                    Require((await MapAsync(fx.A)).Single().Value == expected && (await MapAsync(fx.B)).Single().Value == expected,
                        "chosen canonical remains on both sides and both labelled copies leave the synced tree");
                    Require(store.Recoveries(fx.Pair.PairId).Count == 2, "both removed copies have recovery provenance");
                    var settled = store.Baselines(fx.Pair.PairId).Single();
                    Require(settled.ObjectKey == PlanIds.Split(basis.ObjectKey, winner) && settled.A.Path == a.Path && settled.B.Path == b.Path,
                        "restore updates the winning correspondence and atomically retires the losing split");
                    Require(store.OpenNotices(fx.Pair.PairId).Count == 0, "successful choice closes its notice");
                    var next = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
                    Require(next.Deleted == 0 && next.KeptBoth == 0 && store.Baselines(fx.Pair.PairId).Count == 1 &&
                        (await MapAsync(fx.A)).Single().Value == expected && (await MapAsync(fx.B)).Single().Value == expected,
                        "follow-up preserves one canonical correspondence without recreating copies");
                }
                else if (mode == "restore-fails")
                {
                    Require(result?.Outcome != Outcome.Done && store.Recoveries(fx.Pair.PairId).Count == 0 &&
                        fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0,
                        "failed restore admits no redundant-copy removals");
                    var next = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
                    Require(next.Deleted == 0 && fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 &&
                        (await MapAsync(fx.A)).ContainsKey(copyA.Path) && (await MapAsync(fx.B)).ContainsKey(copyB.Path) &&
                        await (winner == Side.A ? fx.A : fx.B).ObserveAsync(a.Path, default) is not null,
                        "later sync cannot remove copies or the kept canonical after a failed restore");
                    Require(store.OpenNotices(fx.Pair.PairId).Any(n => n.NoticeId == noticeId && n.Detail.Contains(result!.Reason!)),
                        "unfinished restore retains a visible notice explaining the failure");
                }
                else Require(result?.Outcome == Outcome.Skipped && fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 &&
                    store.OpenNotices(fx.Pair.PairId).Count == 1,
                    "changed, foreign, missing, protected or unproven occupants refuse with one notice and no effects: " + mode);
            }
            finally { store.Dispose(); }
        }
        Console.WriteLine("PASS DisplacedConflictOriginal");
    }
}
