namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task ExistingKeepChoiceFollowupAsync()
    {
        foreach (var choice in new[] { NoticeChoices.KeepA, NoticeChoices.KeepB })
        foreach (var withoutHash in new[] { false, true })
        {
            using var store = SqliteStateStore.Open(FixtureFolder("keep-choice-followup"), Settings,
                TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            const string path = "Parent/item.json";
            fx.A.Seed(path, "original");
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            var a = fx.A.Seed(path, "local edit");
            var b = fx.B.Seed(path, "cloud edit");
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            var notice = store.OpenNotices(fx.Pair.PairId).Single(item => item.Rule == 27);
            fx.A.ObserveWithoutHashes = withoutHash;
            fx.A.HashedPaths.Clear();
            var selected = await run.ResolveConflictNoticeAsync(fx.Pair.PairId, notice.NoticeId!, choice, null, default);
            if (withoutHash)
            {
                Require(fx.A.HashedPaths.Contains(path) && fx.A.HashedPaths.Contains(notice.ConflictVersions!.CopyOnA!),
                    "present original and labelled local copy both use adapter hash proof");
                fx.A.ObserveWithoutHashes = false;
            }
            Require(selected.Outcome == Outcome.Done, "existing keep-version choice completes: " + selected.Reason);
            Require(store.Baselines(fx.Pair.PairId).Count == 1,
                "keep-version commits one correspondence before the next run");
            var expected = choice == NoticeChoices.KeepA ? a.Hash : b.Hash;
            var next = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(next.Deleted == 0 && (await fx.A.ObserveAsync(path, default))?.Hash == expected &&
                (await fx.B.ObserveAsync(path, default))?.Hash == expected,
                $"keep-version must not delete a kept canonical in a follow-up run: {next}");
            Require(store.Baselines(fx.Pair.PairId).Count == 1, "keep-version settles to one canonical correspondence");
        }
        Console.WriteLine("PASS ExistingKeepChoiceFollowup");
    }
}
