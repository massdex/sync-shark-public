namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    public static async Task ConflictChoiceChecksAsync()
    {
        foreach (var rule in new[] { 10, 11, 27 })
        foreach (var choice in new[] { NoticeChoices.KeepA, NoticeChoices.KeepB, NoticeChoices.KeepBoth, "stale", "retry" })
        {
            var root = LocalChecks.FreshRoot($"conflict-choice-{rule}-{choice}");
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            var a = fx.A.Seed("a.txt", "left version");
            var b = fx.B.Seed(rule == 27 ? "a.txt" : "b.txt", "right version");
            var copyA = fx.A.Seed(rule == 27 ? "copy-b.txt" : "b.txt", "right version");
            var copyB = fx.B.Seed(rule == 27 ? "copy-a.txt" : "a.txt", "left version");
            var gen = store.BeginGeneration(fx.Pair.A.RootId, "test");
            void Adopt(string key, ItemState left, ItemState right)
            {
                var action = new SyncAction(key, 16, ActionKind.Adopt, Side.B, right.Path, null, left, right, false, key);
                store.CommitResult(fx.Pair.PairId, gen, action, new(key, Outcome.Done, left, right, null, null));
            }
            Adopt("left-link", a, copyB);
            Adopt("right-link", copyA, b);
            store.AddNotices([new(fx.Pair.PairId, rule, NoticeKind.KeepBoth, a.Path, "both kept", "keep-both",
                ConflictVersions: new(a, b, CopyOnA: copyA.Path, CopyOnB: copyB.Path))]);
            var notice = store.OpenNotices(fx.Pair.PairId).Single();
            if (choice == "stale") fx.A.Seed(copyA.Path, "later edit");
            if (choice == "retry") fx.B.FailNextWrites = 1;
            var result = await fx.Run().ResolveConflictNoticeAsync(fx.Pair.PairId, notice.NoticeId!,
                choice is "stale" or "retry" ? NoticeChoices.KeepA : choice, null, CancellationToken.None);
            if (choice == "retry")
            {
                Require(result.Outcome != Outcome.Done, "injected failure leaves choice unfinished");
                Require(store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice.ConflictOperation is not null,
                    "exact action manifest survives partial application");
                var switched = await fx.Run().ResolveConflictNoticeAsync(fx.Pair.PairId, notice.NoticeId!,
                    NoticeChoices.KeepB, null, CancellationToken.None);
                Require(switched.Outcome == Outcome.Skipped, "cannot switch a partly completed choice");
                result = await fx.Run().ResolveConflictNoticeAsync(fx.Pair.PairId, notice.NoticeId!,
                    NoticeChoices.KeepA, null, CancellationToken.None);
            }
            var mapA = await MapAsync(fx.A);
            var mapB = await MapAsync(fx.B);
            if (choice is "stale" or NoticeChoices.KeepBoth)
            {
                Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 && mapA.Count == 2 && mapB.Count == 2,
                    "keep-both/stale copy never changes a file");
                Require(result.Outcome == (choice == "stale" ? Outcome.Skipped : Outcome.Done), "stale remains pending; keep-both acknowledges");
                continue;
            }
            Require(result.Outcome == Outcome.Done, $"rule{rule} {choice} completed: {result.Reason}");
            Require(mapA.Count == 1 && mapB.Count == 1, "explicit keep-only removes only the two redundant copies");
            var hash = choice is NoticeChoices.KeepA or "retry" ? a.Hash : b.Hash;
            Require(mapA.Values.Single() == hash && mapB.Values.Single() == hash, "chosen content remains on both sides");
            Require(store.Recoveries(fx.Pair.PairId).Count == (rule == 27 ? 3 : 2), "every removed/replaced version has recovery provenance");
        }
        foreach (var choice in new[] { NoticeChoices.RenameIncoming, NoticeChoices.ReplaceExisting, NoticeChoices.Dismiss, "stale" })
        {
            var root = LocalChecks.FreshRoot("incoming-choice-" + choice);
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            var a = fx.A.Seed("new.txt", "incoming");
            var b = fx.B.Seed("new.txt", "existing");
            var incoming = new SyncAction("incoming", 14, ActionKind.CopyNew, Side.B, b.Path, null, a, null, false, null);
            store.AddNotices([new(fx.Pair.PairId, 14, NoticeKind.UserDecision, a.Path, "collision", "incoming-name-clash",
                ConflictVersions: new(a, b, Incoming: incoming))]);
            var notice = store.OpenNotices(fx.Pair.PairId).Single();
            if (choice == "stale") fx.B.Seed(b.Path, "changed destination");
            var result = await fx.Run().ResolveConflictNoticeAsync(fx.Pair.PairId, notice.NoticeId!,
                choice == "stale" ? NoticeChoices.ReplaceExisting : choice, "renamed.txt", CancellationToken.None);
            var mapA = await MapAsync(fx.A);
            var mapB = await MapAsync(fx.B);
            Require(result.Outcome == (choice == "stale" ? Outcome.Skipped : Outcome.Done), "incoming choice outcome");
            if (choice == NoticeChoices.RenameIncoming)
                Require(mapA.ContainsKey("renamed.txt") && !mapA.ContainsKey(a.Path) && mapB[b.Path] == b.Hash,
                    "rename moves incoming source, never the unrelated destination");
            if (choice == NoticeChoices.ReplaceExisting)
                Require(mapB[b.Path] == a.Hash && store.Recoveries(fx.Pair.PairId).Count == 1, "replace preserves exact displaced file");
            if (choice is NoticeChoices.Dismiss or "stale")
                Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "dismiss and stale occupant change nothing");
        }
        foreach (var mode in new[] { "clean", "overlap", "missing", "stale" })
        {
            var root = LocalChecks.FreshRoot("merge-base-" + mode);
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            var oldA = fx.A.Seed("story.txt", "alpha\nbeta\ngamma\n");
            var oldB = fx.B.Seed("story.txt", "alpha\nbeta\ngamma\n");
            var a = fx.A.Seed("story.txt", "ALPHA\nbeta\ngamma\n");
            var b = fx.B.Seed("story.txt", mode == "overlap" ? "other\nbeta\ngamma\n" : "alpha\nbeta\nGAMMA\n");
            var gen = store.BeginGeneration(fx.Pair.A.RootId, "test");
            var archive = Path.Combine(root, "baseline.recovery");
            await File.WriteAllTextAsync(archive, "alpha\nbeta\ngamma\n", new System.Text.UTF8Encoding(false));
            var prior = new SyncAction("before-conflict", 4, ActionKind.Update, Side.A, a.Path, null, a, oldA, true, "shared");
            if (mode != "missing") store.CommitResult(fx.Pair.PairId, gen, prior,
                new(prior.ActionId, Outcome.Done, a, oldB, new RecoveryRef(Side.A, archive, null, oldA.Hash!, oldA.Size), null));
            fx.Provider.AddWriter(fx.Pair.A, new NoticeArchiveWriter(fx.WriterA, root));
            store.AddNotices([new(fx.Pair.PairId, 27, NoticeKind.KeepBoth, a.Path, "merge", "keep-both",
                ConflictVersions: new(a, b, new(fx.Pair.PairId, "shared", oldA, oldB, gen)))]);
            var notice = store.OpenNotices(fx.Pair.PairId).Single();
            if (mode == "stale") fx.A.Seed(a.Path, "new edit");
            if (mode == "stale")
            {
                var refused = false;
                try { await fx.Run().CompareNoticeAsync(fx.Pair.PairId, notice.NoticeId!, CancellationToken.None); }
                catch (SyncRunRefusedException) { refused = true; }
                Require(refused && fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "stale comparison cannot read or replace the wrong version");
                continue;
            }
            var comparison = await fx.Run().CompareNoticeAsync(fx.Pair.PairId, notice.NoticeId!, CancellationToken.None);
            Require((comparison.BaseText is null) == (mode == "missing"), "shared baseline recovered independently of live baseline table");
            Require((comparison.MergedText is not null) == (mode == "clean"), "only nonoverlapping verified three-way edits produce clean merge");
            if (mode == "clean") fx.A.FailNextWrites = 1;
            var merged = await fx.Run().ResolveConflictNoticeAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.Merge,
                "merged.txt", CancellationToken.None);
            if (mode == "clean")
            {
                Require(merged.Outcome != Outcome.Done &&
                    store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice.ConflictOperation is { MergeBytes: null, MergeContentId: not null } &&
                    store.ConflictMergeBytes(notice.NoticeId!) is { Length: > 0 },
                    "interrupted merge retains exact durable output outside notice JSON before effects");
                merged = await fx.Run().ResolveConflictNoticeAsync(fx.Pair.PairId, notice.NoticeId!, NoticeChoices.Merge,
                    "merged.txt", CancellationToken.None);
                Require(store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Closed,
                    "resumed merge closes its notice only after verified output arrival");
                Require(store.ConflictMergeBytes(notice.NoticeId!) is null, "resolved merge reclaims its side-row payload");
            }
            Require(merged.Outcome == (mode == "clean" ? Outcome.Done : Outcome.Skipped), "merge requires verified baseline and clean result");
            Require((await MapAsync(fx.A))[a.Path] == a.Hash && (await MapAsync(fx.B))[b.Path] == b.Hash,
                "merge preserves both originals");
            if (mode == "clean") Require((await MapAsync(fx.A))["merged.txt"] ==
                DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("ALPHA\nbeta\nGAMMA\n")), "clean merge saved as independent file");
            else Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0, "missing baseline and conflicts perform no effects");
        }
        foreach (var choice in new[] { NoticeChoices.KeepA, NoticeChoices.ReplaceExisting })
        {
            var root = LocalChecks.FreshRoot("folder-choice-" + choice);
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            Notice notice;
            if (choice == NoticeChoices.KeepA)
            {
                var x = fx.A.Seed("x", "", ItemKind.Folder);
                fx.A.Seed("x/1.txt", "one");
                var y = fx.B.Seed("y", "", ItemKind.Folder);
                fx.B.Seed("y/2.txt", "two");
                var copyA = fx.A.Seed("y", "", ItemKind.Folder);
                fx.A.Seed("y/2.txt", "two");
                var copyB = fx.B.Seed("x", "", ItemKind.Folder);
                fx.B.Seed("x/1.txt", "one");
                var gen = store.BeginGeneration(fx.Pair.A.RootId, "test");
                foreach (var (key, left, right) in new[] { ("left-link", x, copyB), ("right-link", copyA, y) })
                    store.CommitResult(fx.Pair.PairId, gen, new SyncAction(key, 16, ActionKind.Adopt, Side.B, right.Path, null, left, right, false, key),
                        new(key, Outcome.Done, left, right, null, null));
                store.AddNotices([new(fx.Pair.PairId, 11, NoticeKind.KeepBoth, x.Path, "both kept", "keep-both",
                    ConflictVersions: new(x, y, CopyOnA: copyA.Path, CopyOnB: copyB.Path))]);
            }
            else
            {
                var incoming = fx.A.Seed("new", "", ItemKind.Folder);
                fx.A.Seed("new/in.txt", "incoming");
                var occupant = fx.B.Seed("new", "", ItemKind.Folder);
                fx.B.Seed("new/old.txt", "existing");
                store.AddNotices([new(fx.Pair.PairId, 14, NoticeKind.UserDecision, incoming.Path, "collision", "incoming-name-clash",
                    ConflictVersions: new(incoming, occupant, Incoming: new("", 14, ActionKind.MkDir, Side.B, occupant.Path, null, incoming, null, false, null)))]);
            }
            // Effect-free scan fills the live inventory the executor freezes and expands, as a real prior scan does.
            await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            notice = store.OpenNotices(fx.Pair.PairId).Single(item => item.ConflictVersions is not null);
            var callsBefore = fx.WriterA.Calls + fx.WriterB.Calls;
            var result = await fx.Run().ResolveConflictNoticeAsync(fx.Pair.PairId, notice.NoticeId!, choice, null, CancellationToken.None);
            Require(fx.WriterA.Calls + fx.WriterB.Calls > callsBefore, "folder choice performs real effects");
            var saved = store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value;
            Require(result.Outcome == Outcome.Done, $"folder {choice} completes: {result.Reason}");
            Require(choice == NoticeChoices.KeepA ? saved.Closed : !saved.Closed && NoticeChoices.Options(saved.Notice)
                    .Select(option => option.Choice).SequenceEqual([NoticeChoices.RestoreExisting, NoticeChoices.Dismiss]),
                "keep-only closes; replace stays open awaiting the incoming item and offers restore");
            async Task<bool> Has(FakeEndpoint side, string path) => await side.ObserveAsync(path, CancellationToken.None) is not null;
            if (choice == NoticeChoices.KeepA)
            {
                Require(await Has(fx.A, "x/1.txt") && await Has(fx.B, "x/1.txt") && !await Has(fx.A, "y") && !await Has(fx.B, "y"),
                    "keep only A's folder location removes B's location and its copy, keeping A's contents on both sides");
                Require(store.Recoveries(fx.Pair.PairId).Count == 2, "every removed descendant file is preserved before removal");
            }
            else
            {
                Require(await Has(fx.A, "new/in.txt") && !await Has(fx.B, "new/old.txt") && !await Has(fx.B, "new"),
                    "replace removes only the occupant folder; the incoming folder stays to arrive by sync");
                Require(store.Recoveries(fx.Pair.PairId).Count == 1, "the occupant's descendant file is recoverable");
            }
        }
        foreach (var large in new[] { false, true })
        {
            var root = LocalChecks.FreshRoot("comparison-export-" + large);
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            var a = fx.A.Seed("data.bin", large ? new string('x', 9 * 1024 * 1024) : "a\0b");
            var b = fx.B.Seed("data.bin", "c\0d");
            store.AddNotices([new(fx.Pair.PairId, 27, NoticeKind.KeepBoth, a.Path, "compare", "keep-both",
                ConflictVersions: new(a, b))]);
            var notice = store.OpenNotices(fx.Pair.PairId).Single();
            var comparison = await fx.Run().CompareNoticeAsync(fx.Pair.PairId, notice.NoticeId!, CancellationToken.None);
            Require(comparison.SnapshotA is not null && comparison.SnapshotB is not null, "binary/large comparison exports both sides");
            Require(new FileInfo(comparison.SnapshotA!).Length == a.Size && new FileInfo(comparison.SnapshotB!).Length == b.Size,
                "exported snapshot lengths match exact versions");
            await using var snapshot = File.OpenRead(comparison.SnapshotA!);
            Require(await DropboxContentHash.ComputeAsync(snapshot) == a.Hash, "exported bytes verify against notice hash");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 && comparison.MergedText is null,
                "binary export does not mutate originals or invent a merge");
            if (large) Require(comparison.A.Length == 0 && comparison.B.Length == 0, "large content stays on disk, not in preview arrays");
        }
        await ConflictFolderChecksAsync();
        Console.WriteLine("Conflict choices: 19 file/location/stale/retry, 4 merge-base, 2 folder and 2 streamed comparison cases passed.");
    }
}
