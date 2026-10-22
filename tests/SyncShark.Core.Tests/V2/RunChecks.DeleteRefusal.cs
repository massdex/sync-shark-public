namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static FreeSpaceAdmission DeleteRefusal(SyncAction action) => action.Kind == ActionKind.Delete
        ? new FreeSpaceAdmission(null, new FreeSpaceRefusal("Not enough free space; free space and retry.",
            FreeSpaceRefusal.InsufficientReasonKey, @"C:\", 2L << 20, 1L << 20, 512L << 20))
        : FreeSpaceAdmission.Granted;

    private static void OneRefusalNotice(SqliteStateStore store, Fixture fx, Notice notice)
    {
        var open = store.OpenNotices(fx.Pair.PairId);
        Require(open.Count == 1 && open[0].NoticeId == notice.NoticeId &&
            open[0].Detail.Contains("Not enough free space", StringComparison.Ordinal),
            "exactly the original owner notice remains open with the refusal cause");
        Require(store.UnresolvedIntents(fx.Pair.PairId).Count == 0,
            "a proven no-effect refusal leaves no uncertain intent to replay");
    }

    private static async Task CallerOwnedDeleteLowSpaceAsync()
    {
        // Keep A deletes both the losing version and its proven copy. Neither is settled by refusal.
        foreach (var rule in new[] { 10, 11 })
        {
            using var store = SqliteStateStore.Open(LocalChecks.FreshRoot("choice-delete-space-" + rule), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            var a = fx.A.Seed("a.txt", "left");
            var b = fx.B.Seed("b.txt", "right");
            var copyA = fx.A.Seed("b.txt", "right");
            var copyB = fx.B.Seed("a.txt", "left");
            var gen = store.BeginGeneration(fx.Pair.A.RootId, "test");
            foreach (var (key, left, right) in new[] { ("left-link", a, copyB), ("right-link", copyA, b) })
                store.CommitResult(fx.Pair.PairId, gen,
                    new SyncAction(key, 16, ActionKind.Adopt, Side.B, right.Path, null, left, right, false, key),
                    new(key, Outcome.Done, left, right, null, null));
            store.AddNotices([new(fx.Pair.PairId, rule, NoticeKind.KeepBoth, a.Path, PlanNotices.KeepBoth.Detail, PlanNotices.KeepBoth.Key,
                ConflictVersions: new(a, b, CopyOnA: copyA.Path, CopyOnB: copyB.Path))]);
            var notice = store.OpenNotices(fx.Pair.PairId).Single();
            fx.WriterA.FreeSpaceAdmissionFor = fx.WriterB.FreeSpaceAdmissionFor = DeleteRefusal;
            Require((await Choose(fx, notice, NoticeChoices.KeepA)).Outcome == Outcome.Refused,
                "choice delete refusal is not changed/kept");
            void Pending()
            {
                OneRefusalNotice(store, fx, notice);
                Require(store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice.ConflictOperation!.Completed.Count == 0 &&
                    fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 && store.Baselines(fx.Pair.PairId).Count == 2,
                    "refused choice keeps victims, existing baselines and incomplete steps unchanged");
            }
            Pending();
            store.MarkFirstSyncDone(fx.Pair.PairId);
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Pending();
            fx.WriterA.FreeSpaceAdmissionFor = fx.WriterB.FreeSpaceAdmissionFor = null;
            Require((await Choose(fx, notice, NoticeChoices.KeepA)).Outcome == Outcome.Done &&
                store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Closed && store.Recoveries(fx.Pair.PairId).Count == 2,
                "saved choice retries its refused deletes with preservation before closing");
        }
        {
            var (store, fx, notice) = await FolderReplaceAsync("delete-low-space", side => side.Seed("new/old.txt", "existing"));
            using var ownedStore = store;
            fx.WriterA.FreeSpaceAdmissionFor = DeleteRefusal;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Refused,
                "frozen child refusal is distinct from changed/kept");
            var removal = store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice.ConflictOperation!.Actions.Single();
            void Pending()
            {
                OneRefusalNotice(store, fx, notice);
                Require(store.CountChoiceVictims(removal.ActionId, ChoiceVictimState.Pending) == 1 &&
                    store.CountChoiceVictims(removal.ActionId, ChoiceVictimState.Kept) == 0 &&
                    store.CountChoiceVictims(removal.ActionId, ChoiceVictimState.Removed) == 0 &&
                    store.NoticeById(fx.Pair.PairId, notice.NoticeId!)!.Value.Notice.ConflictOperation!.Completed.Count == 0,
                    "refused frozen victim stays Pending, never Kept or Removed");
                Require(store.Baselines(fx.Pair.PairId).Count == 0 && fx.WriterA.Preserves == 0 && fx.WriterA.Deletes == 0,
                    "up-front refusal creates no baseline and performs no preservation/deletion");
            }
            Pending();
            // Upgrade a legacy frozen-child intent on retry without mutating the saved operation manifest.
            var victim = store.ChoiceVictims(removal.ActionId, ChoiceVictimState.Pending, 256, true).Single();
            store.RecordIntent(fx.Pair.PairId, new SyncAction(removal.ActionId + ":" + victim.Ord, removal.Rule,
                ActionKind.Delete, removal.Target, victim.Item.Path, null, null, victim.Item, true, null)
                { SuppressReconciledBaseline = true, FrozenChoiceId = removal.ActionId });
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Refused,
                "legacy frozen intent acquires caller ownership on retry");
            Pending();
            store.MarkFirstSyncDone(fx.Pair.PairId);
            await fx.Run().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Pending();
            Require(await HashAt(fx.A, "new/old.txt") == HashOf("existing"), "refused victim survives ordinary sync");
            fx.WriterA.FreeSpaceAdmissionFor = null;
            Require((await Choose(fx, notice, NoticeChoices.ReplaceExisting)).Outcome == Outcome.Done &&
                await HashAt(fx.A, "new/old.txt") is null && store.Recoveries(fx.Pair.PairId).Count == 1,
                "retry removes the pending victim only after preservation");
        }
    }

    private static async Task QuarantineDeleteLowSpaceAsync(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "delete-low-space"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var selected = fx.B.Seed("blocked.txt", "reviewed cloud bytes");
        fx.Provider.AddWriter(fx.Pair.A, new QuarantineWriter(fx.A, "write", 225));
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        var notice = store.OpenNotices("p1").Single(n => n.Quarantine is not null);
        fx.WriterB.FreeSpaceAdmissionFor = DeleteRefusal;
        var result = await run.ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.QuarantineDeleteDropbox, default);
        Require(result.Outcome == Outcome.Refused, "quarantine delete reports a no-effect refusal");
        void Pending()
        {
            OneRefusalNotice(store, fx, notice);
            Require(store.Live("rb", [selected.Path]).Single().Flags.HasFlag(ItemFlags.Quarantined) &&
                store.Baselines("p1").Count == 0 && fx.WriterB.Preserves == 0 && fx.WriterB.Deletes == 0,
                "refused quarantine victim stays held, without baseline or effect");
        }
        Pending();
        await run.IncrementalAsync("p1", new RecordingProgress(), default);
        Pending();
        Require((await fx.B.ObserveAsync(selected.Path, default))?.ObjectId == selected.ObjectId,
            "ordinary sync preserves the refused quarantine victim");
        fx.WriterB.FreeSpaceAdmissionFor = null;
        result = await run.ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.QuarantineDeleteDropbox, default);
        Require(result.Outcome == Outcome.Done && store.NoticeById("p1", notice.NoticeId!)!.Value.Closed &&
            await fx.B.ObserveAsync(selected.Path, default) is null && fx.WriterB.Preserves == 1,
            "explicit retry completes only after successful preservation and delete");
    }
}
