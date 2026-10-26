namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task DisplacedConflictOriginalAsync()
    {
        var refusalReasons = new Dictionary<string, string>
        {
            ["changed"] = "conflict-successor-changed",
            ["foreign"] = "conflict-successor-foreign",
            ["missing-proof"] = "conflict-successor-provenance",
            ["missing-baseline-hash"] = "conflict-successor-provenance",
            ["provenance-mismatch"] = "conflict-successor-provenance",
            ["unhashable"] = "conflict-successor-proof",
            ["missing"] = "conflict-successor-missing",
            ["protected"] = "conflict-successor-protected",
            ["wrong-choice"] = "conflict-successor-missing"
        };
        foreach (var winner in new[] { Side.A, Side.B })
        foreach (var mode in new[] { "restore", "changed", "foreign", "missing-proof", "cli", "null-hash",
            "unhashable", "missing", "protected", "wrong-choice", "restore-fails", "removal-fails", "drift", "missing-baseline-hash", "provenance-mismatch" })
        {
            var root = LocalChecks.FreshRoot("displaced-conflict-" + winner + "-" + mode);
            var store = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            try
            {
                var fx = Build(suppliedStore: store);
                fx.A.DropboxHashes = fx.B.DropboxHashes = true;
                var a = fx.A.Seed(mode == "drift" ? "Parent/Capsules/work/item.json" : "Parent/capsules/work/item.json", "local version");
                var b = fx.B.Seed("Parent/capsules/work/item.json", "cloud version");
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
                var savedA = mode switch
                {
                    "drift" => a with { Path = b.Path },
                    "missing-baseline-hash" => a with { Hash = null },
                    "provenance-mismatch" => a with { ObjectId = "different-saved-id" },
                    _ => a
                };
                Adopt(Side.A, savedA, mode == "missing-baseline-hash" ? copyB with { Hash = null } : copyB);
                Adopt(Side.B, copyA, mode == "drift" && winner == Side.A
                    ? b with { Path = a.Path } : b);
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
                var removalA = new RecordingWriter(fx.A) { FailDeleteAt = copyA.Path };
                var removalB = new RecordingWriter(fx.B);
                if (mode == "removal-fails")
                    fx.Provider.AddWriter(fx.Pair.A, removalA).AddWriter(fx.Pair.B, removalB);
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
                if (refusalReasons.TryGetValue(mode, out var reasonKey))
                {
                    var reason = MessageCatalog.Default.Message(reasonKey);
                    Require(result?.Reason == reason, $"{mode} reports {reasonKey}: {result?.Reason}");
                    Require(store.OpenNotices(fx.Pair.PairId).Single().Detail.Contains(reason),
                        "open notice retains the specific refusal reason: " + mode);
                }
                if (mode is "restore" or "cli" or "null-hash" or "drift")
                {
                    var expected = winner == Side.A ? a.Hash : b.Hash;
                    Require(result is null || result.Outcome == Outcome.Done, "displaced original choice completes: " + result?.Reason);
                    Require((await MapAsync(fx.A)).Single().Value == expected && (await MapAsync(fx.B)).Single().Value == expected,
                        "chosen canonical remains on both sides and both labelled copies leave the synced tree");
                    Require((await fx.A.ObserveAsync(a.Path, default))?.Path == a.Path &&
                        (await fx.B.ObserveAsync(b.Path, default))?.Path == b.Path,
                        "canonical restore uses frozen spelling despite ancestor-case drift in split records");
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
                else if (mode == "removal-fails")
                {
                    Require(result?.Outcome != Outcome.Done, "one copy removal fails after canonical restoration");
                    store.Dispose();
                    store = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
                    run = new SyncRun(store, NewExecutor(store), fx.Provider, Settings, TimeProvider.System);
                    var open = store.OpenNotices(fx.Pair.PairId).Single(item => item.NoticeId == noticeId);
                    var operation = open.ConflictOperation!;
                    Require(!operation.Finished && operation.Completed.Contains(operation.SettlementAction!.ActionId) &&
                        operation.Completed.Count == 2 && store.Baselines(fx.Pair.PairId).Count == 1,
                        "restore and exactly one removal committed before the cold reopen");
                    Require(open.Detail.Contains(result!.Reason!), "partial completion retains its specific failure reason");
                    var expected = winner == Side.A ? a.Hash : b.Hash;
                    var deletes = removalA.Deletes + removalB.Deletes;
                    var puts = removalA.Puts + removalB.Puts;
                    removalA.FailDeleteAt = null;
                    var next = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
                    Require(next.Deleted == 0 && removalA.Deletes + removalB.Deletes == deletes &&
                        removalA.Puts + removalB.Puts == puts &&
                        (await fx.A.ObserveAsync(a.Path, default))?.Hash == expected &&
                        (await fx.B.ObserveAsync(b.Path, default))?.Hash == expected &&
                        (await fx.A.ObserveAsync(copyA.Path, default))?.Hash == copyA.Hash &&
                        await fx.B.ObserveAsync(copyB.Path, default) is null &&
                        await fx.B.ObserveAsync(copyA.Path, default) is null,
                        "durable hold prevents canonical deletion, remaining-copy removal and orphan-copy propagation");
                    Require(store.OpenNotices(fx.Pair.PairId).Single(item => item.NoticeId == noticeId).Detail.Contains(result.Reason!),
                        "ongoing sync preserves the unfinished notice and reason");
                    var retried = await run.ResolveConflictNoticeAsync(fx.Pair.PairId, noticeId, choice, null, default);
                    Require(retried.Outcome == Outcome.Done && store.Baselines(fx.Pair.PairId).Count == 1 &&
                        store.OpenNotices(fx.Pair.PairId).Count == 0 &&
                        (await MapAsync(fx.A)).Single().Value == expected && (await MapAsync(fx.B)).Single().Value == expected,
                        "same choice resumes only unfinished removal and completes one canonical correspondence");
                    Require(removalA.Puts + removalB.Puts == puts &&
                        removalA.Deletes + removalB.Deletes == deletes + 1,
                        "retry neither repeats committed restore nor repeats completed copy deletion");
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
