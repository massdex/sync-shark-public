namespace Massdex.SyncShark.V2;

// Decisions 87/88 folder choices: frozen SQLite snapshot, bounded pages, no replay, pending arrival and restore.
internal static partial class RunChecks
{
    private static async Task<(SqliteStateStore Store, Fixture Fx, Notice Notice)> FolderReplaceAsync(string name,
        Action<FakeEndpoint> occupant, string folderPath = "new")
    {
        var root = LocalChecks.FreshRoot("folder-replace-" + name);
        var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        fx.A.DropboxHashes = fx.B.DropboxHashes = true;
        // The occupant is local (A), where Recovery is a readable archive; the incoming folder comes from Dropbox (B).
        fx.Provider.AddWriter(fx.Pair.A, new NoticeArchiveWriter(fx.WriterA, root));
        var slash = folderPath.LastIndexOf('/');
        if (slash >= 0)
        {
            fx.A.Seed(folderPath[..slash], "", ItemKind.Folder);
            fx.B.Seed(folderPath[..slash], "", ItemKind.Folder);
        }
        var incoming = fx.B.Seed(folderPath, "", ItemKind.Folder);
        fx.B.Seed(folderPath + "/in.txt", "incoming");
        var existing = fx.A.Seed(folderPath, "", ItemKind.Folder);
        occupant(fx.A);
        store.AddNotices([new(fx.Pair.PairId, 14, NoticeKind.UserDecision, incoming.Path, "collision", "incoming-name-clash",
            ConflictVersions: new(existing, incoming, Incoming: new("", 14, ActionKind.MkDir, Side.A, existing.Path, null, incoming, null, false, null)))]);
        // Effect-free scan fills the live inventory that the choice freezes.
        await fx.Run().PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        return (store, fx, store.OpenNotices(fx.Pair.PairId).Single(item => item.ConflictVersions is not null));
    }

    private static Task<ActionResult> Choose(Fixture fx, Notice notice, string choice) =>
        fx.Run().ResolveConflictNoticeAsync(fx.Pair.PairId, notice.NoticeId!, choice, null, CancellationToken.None);

    private static async Task<string?> HashAt(FakeEndpoint side, string path) =>
        (await side.ObserveAsync(path, CancellationToken.None))?.Hash;

    private static string HashOf(string text) => DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes(text));

    public static async Task ConflictFolderChecksAsync()
    {
        // (1) Replace, then the next sync delivers the exact incoming folder and closes the notice; the removed
        // occupant is not resurrected and its file stays in Recovery.
        {
            var (store, fx, notice) = await FolderReplaceAsync("arrival", side => side.Seed("new/old.txt", "existing"));
            using var _ = store;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done, "replace removes occupant");
            Require(!store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Closed, "notice waits for arrival");
            store.MarkFirstSyncDone(fx.Pair.PairId);
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(await HashAt(fx.A, "new/in.txt") == HashOf("incoming") && await HashAt(fx.A, "new/old.txt") is null,
                "the waiting notice no longer holds the path: the incoming folder arrives and the occupant is not resurrected");
            Require(store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Closed, "verified arrival closes the notice");
            Require(store.Recoveries(fx.Pair.PairId).Count == 1, "the replaced file stays recoverable after arrival");
        }
        // Excluded children are not an arrival obligation (owner default, RQ-060).
        {
            var (store, fx, notice) = await FolderReplaceAsync("partial-arrival", side => side.Seed("new/old.txt", "existing"));
            using var _ = store;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done, "replace prepares partial arrival");
            store.SavePair(fx.Pair with { Excluded = ["new/in.txt"] });
            store.MarkFirstSyncDone(fx.Pair.PairId);
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(await fx.A.ObserveAsync("new", CancellationToken.None) is not null && await HashAt(fx.A, "new/in.txt") is null,
                "only the incoming root arrived");
            Require(store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Closed,
                "excluded children do not hold an otherwise fully arrived folder notice");
        }
        // (1b) Owner default: source deletion settles arrival; the occupant stays recoverable.
        {
            var (store, fx, notice) = await FolderReplaceAsync("no-arrival", side => side.Seed("new/old.txt", "existing"));
            using var _ = store;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done, "replace removes occupant");
            foreach (var path in new[] { "new/in.txt", "new" })
                await fx.B.DeleteAsync(new SyncAction("user-" + path, 6, ActionKind.Delete, Side.B, path, null, null,
                    await fx.B.ObserveAsync(path, CancellationToken.None), false, null), CancellationToken.None);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            var saved = store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value;
            Require(saved.Closed, "source deletion settles the arrival obligation without a transfer");
            Require(store.Recoveries(fx.Pair.PairId).Count == 1, "the removed occupant remains recoverable");
        }
        // (1c) Settled + provider-refused: a known refused descendant is never deleted on its own, and every folder
        // above it is kept even though the settled folder delete skips the subtree-wide listing.
        {
            var (store, fx, notice) = await FolderReplaceAsync("refused", side =>
            {
                side.Seed("new/ok.txt", "ok");
                side.Seed("new/sub", "", ItemKind.Folder);
                side.Seed("new/sub/refused.txt", "refused name");
            });
            using var _ = store;
            var refusedItem = await fx.A.ObserveAsync("new/sub/refused.txt", CancellationToken.None);
            store.WriteDelta(new Delta(fx.Pair.A.RootId, Side.A, store.BeginGeneration(fx.Pair.A.RootId, "delta"), "x", "x",
                [new Change(ChangeKind.Upsert, refusedItem!.ObjectId, refusedItem.Path,
                    refusedItem with { Flags = ItemFlags.ProviderNameRefused }, null)], false, []));
            var result = await Choose(fx, notice, NoticeChoices.ReplaceExisting);
            Require(result.Outcome != Outcome.Done, "a folder holding a refused item is not reported replaced: " + result.Reason);
            Require(await HashAt(fx.A, "new/sub/refused.txt") == HashOf("refused name") &&
                await fx.A.ObserveAsync("new/sub", CancellationToken.None) is not null &&
                await fx.A.ObserveAsync("new", CancellationToken.None) is not null,
                "the refused item and every folder above it are kept");
            Require(await HashAt(fx.A, "new/ok.txt") is null && store.Recoveries(fx.Pair.PairId).All(record => record.Path == "new/ok.txt"),
                "ordinary frozen files are still removed after preservation; the refused item never reaches Recovery or deletion");
        }
        // N1: path_key is uppercase even when the removal folder's parent is mixed case.
        // No refused descendant can mask a failed self lookup through the subtree fallback here.
        {
            var (store, fx, notice) = await FolderReplaceAsync("nested-refused-root", side => { }, "Docs/new");
            using var _ = store;
            var item = (await fx.A.ObserveAsync("Docs/new", CancellationToken.None))!;
            store.WriteDelta(new Delta(fx.Pair.A.RootId, Side.A, store.BeginGeneration(fx.Pair.A.RootId, "delta"), "x", "x",
                [new Change(ChangeKind.Upsert, item.ObjectId, item.Path,
                    item with { Flags = ItemFlags.ProviderNameRefused }, null)], false, []));
            Require(store.ChoiceRemovalProtected(fx.Pair, Side.A, item.Path, true),
                "nested refused removal root is protected by its normalized parent key");
            Require(store.ChoiceRemovalProtected(fx.Pair, Side.A, item.Path + "/child.txt", false),
                "nested refused ancestor protects a child even without a subtree scan");
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome != Outcome.Done &&
                await fx.A.ObserveAsync(item.Path, CancellationToken.None) is not null,
                "the nested refused folder is not removed");
        }
        // Freeze first, then refuse a file: its frozen flags remain clear, so the live per-item
        // batch query must catch the new protection rather than relying on the snapshot flags.
        {
            var (store, fx, notice) = await FolderReplaceAsync("nested-refused-after-freeze",
                side => side.Seed("Docs/new/only.txt", "untouched"), "Docs/new");
            using var _ = store;
            var victim = notice.ConflictVersions!.A;
            var action = new SyncAction("nested-refused-freeze", 14, ActionKind.Delete, Side.A, victim.Path,
                null, null, victim, false, null) { SuppressReconciledBaseline = true };
            store.SaveConflictOperation(fx.Pair.PairId, notice.NoticeId!, new(NoticeChoices.ReplaceExisting, null, [action], []));
            var frozen = store.ChoiceVictims(action.ActionId, ChoiceVictimState.Pending, 256, true).Single().Item;
            Require((frozen.Flags & ItemFlags.ProviderNameRefused) == 0 &&
                !store.ChoiceRemovalProtectedBatch(fx.Pair, Side.A, [frozen]).Single(),
                "ordinary frozen child starts unprotected");
            store.WriteDelta(new Delta(fx.Pair.A.RootId, Side.A, store.BeginGeneration(fx.Pair.A.RootId, "delta"), "x", "x",
                [new Change(ChangeKind.Upsert, frozen.ObjectId, frozen.Path,
                    frozen with { Flags = ItemFlags.ProviderNameRefused }, null)], false, []));
            Require(store.ChoiceRemovalProtectedBatch(fx.Pair, Side.A, [frozen]).Single(),
                "live batch guard protects a child refused after freeze under a mixed-case parent");
            var writes = fx.A.WriteCalls;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome != Outcome.Done &&
                fx.A.WriteCalls == writes && await HashAt(fx.A, frozen.Path) == HashOf("untouched") &&
                await fx.A.ObserveAsync(victim.Path, CancellationToken.None) is not null &&
                store.Recoveries(fx.Pair.PairId).Count == 0,
                "newly refused frozen child and enclosing folder stay untouched, without a recovery/delete effect");
        }
        // A durable attempted-but-not-applied child must recheck current protections before any retry write.
        foreach (var protection in new[] { "refused", "excluded" })
        {
            var (store, fx, notice) = await FolderReplaceAsync("retry-" + protection,
                side => side.Seed("new/only.txt", "untouched"));
            using var _ = store;
            fx.A.FailNextWrites = 1;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome != Outcome.Done,
                "injected child failure leaves a retryable choice");
            Require(await HashAt(fx.A, "new/only.txt") == HashOf("untouched"), "failed attempt made no deletion");
            var writes = fx.A.WriteCalls;
            if (protection == "excluded") store.SavePair(fx.Pair with { Excluded = ["new/only.txt"] });
            else
            {
                var item = (await fx.A.ObserveAsync("new/only.txt", CancellationToken.None))!;
                store.WriteDelta(new Delta(fx.Pair.A.RootId, Side.A, store.BeginGeneration(fx.Pair.A.RootId, "delta"), "x", "x",
                    [new Change(ChangeKind.Upsert, item.ObjectId, item.Path,
                        item with { Flags = ItemFlags.ProviderNameRefused }, null)], false, []));
            }
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome != Outcome.Done,
                "changed protection prevents retry completion");
            Require(fx.A.WriteCalls == writes && await HashAt(fx.A, "new/only.txt") == HashOf("untouched"),
                "attempted intent authorizes verification, not a write through changed " + protection);
        }
        // Ignore-only protection belongs to the parent writer, rather than permanently keeping the parent.
        {
            var (store, fx, notice) = await FolderReplaceAsync("carried-ignore",
                side => side.Seed("new/desktop.ini", "ignored"));
            using var _ = store;
            store.SavePair(fx.Pair with { IgnoreRules = ["FileName:desktop.ini"] });
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done,
                "an ignored frozen child does not veto its enclosing explicit folder choice");
            Require(store.Recoveries(fx.Pair.PairId).All(record => record.Path != "new/desktop.ini"),
                "ignored file is not issued a standalone recovery/delete action");
        }
        {
            var (store, fx, notice) = await FolderReplaceAsync("carried-retry-budget", side => side.Seed("new/desktop.ini", "small"));
            using var _ = store;
            store.SavePair(fx.Pair with { IgnoreRules = ["FileName:desktop.ini"] });
            var victim = notice.ConflictVersions!.A;
            var action = new SyncAction("carried-retry", 14, ActionKind.Delete, Side.A, victim.Path,
                null, null, victim, false, null) { SuppressReconciledBaseline = true };
            store.SaveConflictOperation(fx.Pair.PairId, notice.NoticeId!, new(NoticeChoices.ReplaceExisting, null, [action], []));
            var page = store.ChoiceVictims(action.ActionId, ChoiceVictimState.Pending, 256, true);
            store.MarkChoiceVictims(action.ActionId, page.Select(row => (row.Ord, ChoiceVictimState.Carried)).ToArray());
            store.RecordIntent(fx.Pair.PairId, action with { DescendantsPlanned = true, ContentsSettled = true });
            var grown = fx.A.Seed("new/desktop.ini", new string('x', 70_000));
            store.WriteDelta(new Delta(fx.Pair.A.RootId, Side.A, store.BeginGeneration(fx.Pair.A.RootId, "delta"), "x", "x",
                [new Change(ChangeKind.Upsert, grown.ObjectId, grown.Path, grown, null)], false, []));
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome != Outcome.Done &&
                await HashAt(fx.A, grown.Path) == grown.Hash,
                "retry rechecks already-carried content against its original frozen budget");
        }
        // (2) Replace, then restore before arrival: the exact removed contents come back, the incoming is untouched.
        {
            var (store, fx, notice) = await FolderReplaceAsync("restore", b =>
            {
                b.Seed("new/old.txt", "existing");
                b.Seed("new/sub", "", ItemKind.Folder);
                b.Seed("new/sub/deep.txt", "deep");
            });
            using var _ = store;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done, "replace completes");
            Require(await HashAt(fx.A, "new/sub/deep.txt") is null, "occupant contents removed");
            var restored = await Choose(fx, store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice, NoticeChoices.RestoreExisting);
            Require(restored.Outcome == Outcome.Done, "restore completes: " + restored.Reason);
            Require(await HashAt(fx.A, "new/old.txt") == HashOf("existing") && await HashAt(fx.A, "new/sub/deep.txt") == HashOf("deep"),
                "restore returns every removed file with its exact content, nested folders first");
            Require(await HashAt(fx.B, "new/in.txt") == HashOf("incoming") && await HashAt(fx.A, "new/in.txt") is null,
                "restore never touches the incoming item");
            Require(store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Closed, "restore closes this notice");
        }
        // A caller-owned restore failure must not create a second sync-owned free-space notice. The shared
        // executor branch is keyed on CallerOwnsNotice, so cover both refusal points on this route too: the
        // writer's own byte admission after a metadata-only reservation (in-effect), and the up-front volume
        // admission before any intent is recorded.
        foreach (var upFront in new[] { false, true })
        {
            var (store, fx, notice) = await FolderReplaceAsync("restore-low-space-" + (upFront ? "up-front" : "in-effect"),
                side => side.Seed("new/old.txt", "existing"));
            using var _ = store;
            var refusal = new FreeSpaceRefusal("Not enough free space; free space and retry.",
                FreeSpaceRefusal.InsufficientReasonKey, @"C:\", 2L << 20, 1L << 20, 512L << 20);
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done, "replace before low-space restore");
            var putsBefore = fx.WriterA.Puts;
            if (upFront)
                fx.WriterA.FreeSpaceAdmissionFor = action => action.ConflictRestore
                    ? new FreeSpaceAdmission(null, refusal) : FreeSpaceAdmission.Granted;
            else
                fx.WriterA.PutFailure = action => action.ConflictRestore
                    ? new FreeSpaceRefusedException(refusal) : null;
            var restored = await Choose(fx, notice, NoticeChoices.RestoreExisting);
            Require(restored.Outcome == Outcome.Refused && restored.Reason!.Contains("Not enough free space", StringComparison.Ordinal),
                "conflict restore returns the refusal detail");
            if (upFront) Require(fx.WriterA.Puts == putsBefore, "up-front refusal prevents the restore write");
            void OneConflictNotice()
            {
                var open = store.OpenNotices(fx.Pair.PairId);
                Require(open.Count == 1 && open[0].NoticeId == notice.NoticeId &&
                    open[0].ReasonKey != FreeSpaceRefusal.InsufficientReasonKey &&
                    open[0].Detail.Contains("Not enough free space", StringComparison.Ordinal),
                    "exactly the original conflict notice remains open with the free-space cause");
            }
            OneConflictNotice();
            // Allow unrelated sync work normally. Excluding the folder would deliberately add a
            // separate pair-excluded summary and contaminate this exact-one-notice assertion.
            store.MarkFirstSyncDone(fx.Pair.PairId);
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            OneConflictNotice();
            Require(await HashAt(fx.A, "new/old.txt") is null, "ordinary sync did not silently complete the refused restore");
        }
        foreach (var obstruction in new[] { "excluded", "unrelated", "uncertain-root" })
        {
            var (store, fx, notice) = await FolderReplaceAsync("restore-" + obstruction, side => side.Seed("new/old.txt", "existing"));
            using var _ = store;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done, "replace before guarded restore");
            if (obstruction == "excluded") store.SavePair(fx.Pair with { Excluded = ["new/old.txt"] });
            if (obstruction == "unrelated") fx.A.Seed("new", "", ItemKind.Folder);
            if (obstruction == "uncertain-root")
            {
                var removal = store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice.ConflictOperation!.Actions.Single();
                store.RecordIntent(fx.Pair.PairId, new SyncAction(ShortId.Make("nrx", fx.Pair.PairId, removal.ActionId + ":root"),
                    87, ActionKind.MkDir, Side.A, removal.Path, null, removal.ExpectedTarget, null, false, null)
                    { SuppressReconciledBaseline = true, ConflictRestore = true });
                fx.A.Seed("new", "", ItemKind.Folder);
            }
            var restored = await Choose(fx, notice, NoticeChoices.RestoreExisting);
            Require(restored.Outcome != Outcome.Done && await HashAt(fx.A, "new/old.txt") is null,
                "restore never fills an excluded or unverified destination: " + obstruction);
            Require(!store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Closed, "unfinished restore remains available");
            if (obstruction == "excluded")
            {
                var ours = (await fx.A.ObserveAsync("new", CancellationToken.None))!;
                await fx.A.DeleteAsync(new SyncAction("replace-restored-root", 6, ActionKind.Delete, Side.A,
                    "new", null, null, ours, false, null), CancellationToken.None);
                var unrelated = fx.A.Seed("new", "", ItemKind.Folder);
                store.SavePair(fx.Pair);
                Require(unrelated.ObjectId != ours.ObjectId &&
                    (await Choose(fx, notice, NoticeChoices.RestoreExisting)).Outcome != Outcome.Done &&
                    await HashAt(fx.A, "new/old.txt") is null,
                    "a durable root marker never authorizes filling a replacement folder with a different identity");
            }
            if (obstruction == "uncertain-root")
            {
                // A normal sync must settle a receipt-less create instead of stalling the pair.
                // Keep the source present but exclude this subtree to prevent legitimate arrival.
                // Deleting the source would instead settle the notice (the no-arrival case above).
                store.SavePair(fx.Pair with { Excluded = ["new"] });
                fx.B.Seed("unrelated.txt", "progress");
                store.MarkFirstSyncDone(fx.Pair.PairId);
                await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
                Require(await HashAt(fx.A, "unrelated.txt") == HashOf("progress") &&
                    !store.UnresolvedIntents(fx.Pair.PairId).Any(action => action.ConflictRestore),
                    "interrupted restore does not block ordinary pair sync");
                var open = store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value;
                Require(!open.Closed && NoticeChoices.Options(open.Notice).Any(option => option.Choice == NoticeChoices.RestoreAsCopy) &&
                    store.Recoveries(fx.Pair.PairId).Count == 1, "Recovery and restore-as-copy options survive abandonment");
                var dismissed = await Choose(fx, notice, NoticeChoices.Dismiss);
                Require(dismissed.Outcome == Outcome.Done && store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Closed,
                    "Dismiss closes without blocking on a create-only restore intent");
            }
        }
        // (3) Partial failure, then a retry: the frozen snapshot is kept. An item added after the choice and an item
        // changed after it are kept (so the folder stays), and completed removals are never repeated.
        {
            var (store, fx, notice) = await FolderReplaceAsync("retry", b =>
            {
                b.Seed("new/a.txt", "a");
                b.Seed("new/b.txt", "b");
                b.Seed("new/c.txt", "c");
            });
            using var _ = store;
            fx.A.RefuseNonEmptyFolderDelete = true;
            fx.A.FailNextWrites = 1;
            var first = await Choose(fx, notice, NoticeChoices.ReplaceExisting);
            Require(first.Outcome != Outcome.Done, "an injected failure leaves the choice unfinished");
            var removedFirst = store.Recoveries(fx.Pair.PairId).Count;
            fx.A.Seed("new/late.txt", "added after the choice");
            if (await HashAt(fx.A, "new/b.txt") is not null) fx.A.Seed("new/b.txt", "changed after the choice");
            // Observe the live growth before retry, without changing the frozen choice snapshot.
            store.WriteSnapshot(await fx.A.FullAsync(store.BeginGeneration(fx.Pair.A.RootId, "refresh"),
                _ => true, null!, CancellationToken.None));
            var retry = await Choose(fx, notice, NoticeChoices.ReplaceExisting);
            Require(retry.Outcome != Outcome.Done, "a folder with kept items is not reported replaced: " + retry.Reason);
            Require(await HashAt(fx.A, "new/late.txt") == HashOf("added after the choice"),
                "an item added after the choice is never removed by a retry (no re-freeze)");
            Require(await HashAt(fx.A, "new/b.txt") is { } kept ? kept == HashOf("changed after the choice") : removedFirst >= 1,
                "an item changed after the choice is kept, never removed");
            Require(await fx.A.ObserveAsync("new", CancellationToken.None) is not null, "the folder is kept while it holds kept items");
            var recoveries = store.Recoveries(fx.Pair.PairId);
            var gone = 0;
            foreach (var name in new[] { "new/a.txt", "new/b.txt", "new/c.txt" })
                if (await HashAt(fx.A, name) is null) gone++;
            Require(recoveries.Count == gone && recoveries.All(record => record.Path is "new/a.txt" or "new/b.txt" or "new/c.txt"),
                "each frozen file is preserved once; nothing outside the snapshot is preserved or removed");
            var stopped = await Choose(fx, notice, NoticeChoices.Dismiss);
            Require(stopped.Outcome == Outcome.Done && stopped.Reason!.Contains("stay removed") &&
                stopped.Reason.Contains("nothing was put back") && stopped.Reason.Contains("Recovery"),
                "stopping a partial choice says completed removals stay removed and where to restore them");
        }
        // (4) Bounded memory: the freeze runs inside SQLite and pages are bounded; operations are exactly owned.
        {
            const int perFolder = 1000;
            var (store, fx, notice) = await FolderReplaceAsync("bounded", b =>
            {
                foreach (var folder in new[] { "new/x", "new/y", "new/z" })
                {
                    b.Seed(folder, "", ItemKind.Folder);
                    for (var index = 0; index < perFolder; index++) b.Seed($"{folder}/{index}.txt", folder + index);
                }
            });
            using var _ = store;
            var rootA = fx.Pair.A.RootId;
            var before = GC.GetTotalAllocatedBytes(true);
            var frozen = store.FreezeChoiceVictims(fx.Pair.PairId, "probe-1", rootA, "new");
            var freezeBytes = GC.GetTotalAllocatedBytes(true) - before;
            before = GC.GetTotalAllocatedBytes(true);
            var materialized = store.LiveUnder(rootA, ["new"]).Count;
            var materializeBytes = GC.GetTotalAllocatedBytes(true) - before;
            Require(frozen == 3 * perFolder + 3 && materialized == frozen + 1, $"the snapshot holds every descendant ({frozen})");
            Require(freezeBytes * 4 < materializeBytes,
                $"freezing allocates far less than materializing the subtree ({freezeBytes} vs {materializeBytes} bytes)");
            Require(store.ChoiceVictims("probe-1", ChoiceVictimState.Pending, 256, true).Count == 256 &&
                store.ChoiceVictims("probe-1", ChoiceVictimState.Pending, int.MaxValue, true).Count <= 4096,
                "pages are bounded");
            Require(store.FreezeChoiceVictims(fx.Pair.PairId, "probe-1", rootA, "new") == frozen, "a second freeze keeps the first snapshot");
            store.FreezeChoiceVictims(fx.Pair.PairId, "probe-2", rootA, "new");
            store.MarkChoiceVictims("probe-1", store.ChoiceVictims("probe-1", ChoiceVictimState.Pending, 256, true)
                .Select(victim => (victim.Ord, ChoiceVictimState.Removed)).ToArray());
            Require(store.CountChoiceVictims("probe-2", ChoiceVictimState.Pending) == frozen &&
                store.CountChoiceVictims("probe-1", ChoiceVictimState.Pending) == frozen - 256,
                "marks change only their exact owning operation");
            store.ForgetChoiceVictims("probe-1");
            store.ForgetChoiceVictims("probe-2");
            var result = await Choose(fx, notice, NoticeChoices.ReplaceExisting);
            Require(result.Outcome == Outcome.Done && await fx.A.ObserveAsync("new/y/999.txt", CancellationToken.None) is null,
                "a large occupant folder is removed page by page: " + result.Reason);
            Require(store.Recoveries(fx.Pair.PairId).Count == 3 * perFolder, "every file is preserved exactly once");
        }
        // Simulate a stop immediately after the manifest commit but before the first executor call.
        {
            var (store, fx, notice) = await FolderReplaceAsync("manifest-freeze", side => side.Seed("new/old.txt", "existing"));
            using var _ = store;
            var victim = notice.ConflictVersions!.A;
            var action = new SyncAction("freeze-before-effects", 14, ActionKind.Delete, Side.A, victim.Path,
                null, null, victim, false, null) { SuppressReconciledBaseline = true };
            store.SaveConflictOperation(fx.Pair.PairId, notice.NoticeId!,
                new ConflictNoticeOperation(NoticeChoices.ReplaceExisting, null, [action], []));
            Require(store.HasChoiceSnapshot(action.ActionId) && store.CountChoiceVictims(action.ActionId, ChoiceVictimState.Pending) == 1,
                "manifest commit already includes its frozen victim snapshot, before any effect");
            var late = fx.A.Seed("new/late.txt", "later creation");
            store.WriteDelta(new Delta(fx.Pair.A.RootId, Side.A, store.BeginGeneration(fx.Pair.A.RootId, "delta"), "x", "x",
                [new Change(ChangeKind.Upsert, late.ObjectId, late.Path, late, null)], false, []));
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome != Outcome.Done &&
                await HashAt(fx.A, late.Path) == late.Hash,
                "resume after manifest commit never re-freezes or removes a later creation");
        }
        await ConflictRestoreReviewChecksAsync();
        ChoiceSnapshotScaleCheck();
        ChoiceSchemaPresenceCheck();
        Console.WriteLine("PASS ConflictFolderChecks (arrival, missing arrival, restore, retry snapshot, bounded pages, 100k scale)");
    }

    private static void ChoiceSchemaPresenceCheck()
    {
        var root = LocalChecks.FreshRoot("choice-presence-upgrade");
        using (var store = SqliteStateStore.Open(root, Settings, TimeProvider.System)) { _ = Build(suppliedStore: store); }
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = SqliteStateStore.DatabasePath(root), Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE choice_victim; DROP TABLE choice_restore_receipt; DROP TABLE choice_merge;";
            command.ExecuteNonQuery();
        }
        for (var open = 0; open < 2; open++)
        {
            using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
            Require(!store.HasChoiceSnapshot("missing") && store.ConflictMergeBytes("missing") is null && store.Pairs().Count == 1,
                "presence upgrade repairs choice storage at the existing schema version and preserves pair data on repeated opens");
        }
    }

    // Owner low-memory rule at realistic scale: 100,000 descendants, one 60,000-file directory (high fanout) plus a
    // nested tree. Setup streams in bounded deltas. Freeze, growth and each page must allocate a small amount that
    // does not grow with the subtree; LiveUnder is measured only as the contrast the product path avoids.
    private static void ChoiceSnapshotScaleCheck()
    {
        var root = LocalChecks.FreshRoot("choice-snapshot-scale");
        using var store = SqliteStateStore.Open(root, Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var rootId = fx.Pair.A.RootId;
        var time = DateTimeOffset.UnixEpoch.AddYears(50);
        var hash = new string('a', 64);
        var gen = store.BeginGeneration(rootId, "snapshot");
        store.WriteSnapshot(new Snapshot(rootId, Side.A, gen, "c0", [new ItemState("big", ItemKind.Folder, "id:big", null, null, 0, time, ItemFlags.None)], []));
        store.CompleteGeneration(gen, "c0", true);
        IEnumerable<ItemState> Items()
        {
            yield return new ItemState("big/flat", ItemKind.Folder, "id:flat", null, null, 0, time, ItemFlags.None);
            for (var index = 0; index < 60_000; index++)
                yield return new ItemState($"big/flat/f{index:D6}.bin", ItemKind.File, $"id:f{index}", "v1", hash, 10, time, ItemFlags.None);
            for (var folder = 0; folder < 40; folder++)
            {
                yield return new ItemState($"big/n{folder:D2}", ItemKind.Folder, $"id:n{folder}", null, null, 0, time, ItemFlags.None);
                yield return new ItemState($"big/n{folder:D2}/deep", ItemKind.Folder, $"id:d{folder}", null, null, 0, time, ItemFlags.None);
                for (var index = 0; index < 1_000; index++)
                    yield return new ItemState($"big/n{folder:D2}/deep/g{index:D4}.bin", ItemKind.File, $"id:g{folder}-{index}", "v1", hash, 10, time, ItemFlags.None);
            }
        }
        var cursor = 0;
        foreach (var batch in Items().Chunk(5_000))
        {
            var delta = store.BeginGeneration(rootId, "delta");
            store.WriteDelta(new Delta(rootId, Side.A, delta, "c" + cursor, "c" + (cursor + 1),
                batch.Select(item => new Change(ChangeKind.Upsert, item.ObjectId, item.Path, item, null)).ToArray(), false, []));
            store.CompleteGeneration(delta, "c" + ++cursor, true);
        }
        const long expected = 1 + 60_000 + 40 * 1_002;
        // Check the production query, not a hand-copied equivalent. This phase used to
        // finish victim paging and then spend hours scanning the root for each flag rewrite.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = SqliteStateStore.DatabasePath(root), Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false }.ToString()))
        {
            connection.Open();
            void RequireObjectBound(string name, string sql, params (string Name, object Value)[] values)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "EXPLAIN QUERY PLAN " + sql;
                foreach (var (parameter, value) in values) command.Parameters.AddWithValue(parameter, value);
                var details = new List<string>();
                using (var reader = command.ExecuteReader())
                    while (reader.Read()) details.Add(reader.GetString(3));
                Require(details.Any(detail => detail.Contains("fs_obj", StringComparison.Ordinal) &&
                        detail.Contains("object_id=?", StringComparison.Ordinal)) &&
                        !details.Any(detail => detail.Contains("USING PRIMARY KEY (root_no=?)", StringComparison.Ordinal)),
                    $"100k-row {name} uses an object-bound index, never a root scan: " + string.Join("; ", details));
            }
            RequireObjectBound("debt reconciliation", FileStateTable.ReconcileProtectionSql, ("$r", 1), ("$o", "id:f45000"));
            RequireObjectBound("held-move probe", FileStateTable.HeldMoveProbeSql,
                ("$r", 1), ("$o", "id:f45000"), ("$d", 1), ("$n", "x.txt"));
            RequireObjectBound("rehash-debt reset", FileStateTable.ClearRehashDebtSql, ("$r", 1), ("$o", "id:f45000"));
            RequireObjectBound("rehash-debt probe", FileStateTable.RehashDebtProbeSql, ("$r", 1), ("$o", "id:f45000"));
            RequireObjectBound("by-object read", FileStateReader.ByObjectSql, ("$r", 1), ("$o", "id:f45000"));
        }
        static long Allocated(Action work)
        {
            var before = GC.GetTotalAllocatedBytes(true);
            work();
            return GC.GetTotalAllocatedBytes(true) - before;
        }
        long frozen = 0, growth = -1;
        var freezeBytes = Allocated(() => frozen = store.FreezeChoiceVictims(fx.Pair.PairId, "scale", rootId, "big"));
        var growthBytes = Allocated(() => growth = store.CountChoiceGrowth("scale", rootId, "big"));
        Require(frozen == expected && growth == 0, $"the snapshot freezes all {expected} descendants and sees no growth ({frozen}, {growth})");
        Require(freezeBytes < 1_000_000 && growthBytes < 1_000_000,
            $"freeze and growth checks stay in SQLite at 100k scale ({freezeBytes}, {growthBytes} bytes)");
        long worstPage = 0, seen = 0, pages = 0;
        while (true)
        {
            IReadOnlyList<(long Ord, ItemState Item)> page = [];
            worstPage = Math.Max(worstPage, Allocated(() => page = store.ChoiceVictims("scale", ChoiceVictimState.Pending, 256, true)));
            if (page.Count == 0) break;
            Require(page.Count <= 256, "a page never exceeds its bound");
            store.MarkChoiceVictims("scale", page.Select(victim => (victim.Ord, ChoiceVictimState.Removed)).ToArray());
            seen += page.Count;
            pages++;
        }
        Require(seen == expected && pages == (expected + 255) / 256, "every frozen item is visited exactly once, in bounded pages");
        Require(worstPage < 1_000_000, $"each page allocates a bounded amount, independent of the 60,000-file directory ({worstPage} bytes)");
        store.WriteDelta(new Delta(rootId, Side.A, store.BeginGeneration(rootId, "delta"), "c" + cursor, "c" + (cursor + 1),
            [new Change(ChangeKind.Upsert, "id:late", "big/flat/late.bin", new ItemState("big/flat/late.bin", ItemKind.File, "id:late", "v1", hash, 1, time, ItemFlags.None), null)],
            false, []));
        Require(store.CountChoiceGrowth("scale", rootId, "big") == 1, "an item added after the freeze is detected as growth");
        var materializeBytes = Allocated(() => _ = store.LiveUnder(rootId, ["big"]).Count);
        Require(materializeBytes > 50 * Math.Max(freezeBytes, 1), $"contrast: listing the subtree costs {materializeBytes} bytes");
        // 100,000+ refused rows must not become an application list while checking a single victim/ancestor.
        foreach (var batch in Items().Chunk(5_000))
            store.WriteDelta(new Delta(rootId, Side.A, store.BeginGeneration(rootId, "delta"), "r", "r",
                batch.Select(item => new Change(ChangeKind.Upsert, item.ObjectId, item.Path,
                    item with { Flags = ItemFlags.ProviderNameRefused }, null)).ToArray(), false, []));
        bool protectedFolder = false, protectedFile = false;
        var protectionBytes = Allocated(() =>
        {
            protectedFolder = store.ChoiceRemovalProtected(fx.Pair, Side.A, "big", true);
            protectedFile = store.ChoiceRemovalProtected(fx.Pair, Side.A, "big/flat/f000001.bin", false);
        });
        Require(protectedFolder && protectedFile && protectionBytes < 1_000_000,
            $"refused existence checks remain bounded with {expected} refused rows ({protectionBytes} bytes)");
        Require(!store.ChoiceRemovalProtected(fx.Pair, Side.A, "outside", true), "refusal protection does not leak outside its subtree");
        store.ForgetChoiceVictims("scale");
        Require(store.CountChoiceVictims("scale", ChoiceVictimState.Removed) == 0, "the ended snapshot is dropped in batches");
    }
}
