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

    // Review rc3-1 finding 2b: a Refused result inside the batched reconcile loop is retryable debt.
    // It unparks the path's retry to due and never settles, clears or forgets anything. The loop is
    // invoked directly because a full run's post-execution retry work settles a clean equal path.
    private static async Task ReconcileRefusedUnparksRetryAsync()
    {
        var folder = FixtureFolder("reconcile-refused-retry");
        try
        {
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.A.Seed("held.txt", "reviewed bytes");
            fx.B.Seed("held.txt", "reviewed bytes");
            // Reconciliation has no free-space admission of its own; the owner-notice replay is the only
            // producer of a Refused result, so the test supplies that exact outcome through the executor.
            var executor = new RefusedReconcileExecutor(fx.Executor, store, fx.Pair.PairId, "nch-reconcile-refused");
            var run = new SyncRun(store, executor, fx.Provider, Settings, TimeProvider.System);
            await FirstSyncAsync(run, fx);
            var victim = (await fx.B.ObserveAsync("held.txt", CancellationToken.None))!;
            var baselines = store.Baselines(fx.Pair.PairId).Count;
            // A caller-owned notice delete whose earlier attempt left a parked retry behind.
            store.RecordIntent(fx.Pair.PairId, new SyncAction("nch-reconcile-refused", 16, ActionKind.Delete,
                Side.B, victim.Path, null, null, victim, true, null)
                { SuppressReconciledBaseline = true, CallerOwnsNotice = true });
            store.SaveSyncRetry(fx.Pair.PairId, new(victim.Path, "sync", 1, "previous refusal", DateTimeOffset.MaxValue));
            const System.Reflection.BindingFlags hidden =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var reconcile = (Task)typeof(SyncRun).GetMethod("ReconcileAsync", hidden)!.Invoke(run,
                [fx.Pair, new ProgressThrottle(null, TimeProvider.System, TimeSpan.Zero), CancellationToken.None, null])!;
            await reconcile;
            var result = reconcile.GetType().GetProperty("Result")!.GetValue(reconcile)!;
            var touched = (IReadOnlyList<string>)result.GetType().GetProperty("Touched")!.GetValue(result)!;
            var retry = store.SyncRetryAt(fx.Pair.PairId, victim.Path);
            Require(retry is { Kind: "sync" } && retry.RetryAfter == DateTimeOffset.MinValue,
                "a Refused reconcile result keeps the retry and unparks it to due");
            Require(touched.Contains(victim.Path, StringComparer.OrdinalIgnoreCase),
                "a Refused reconcile result keeps its path in the touched set");
            Require(store.Baselines(fx.Pair.PairId).Count == baselines &&
                store.Recoveries(fx.Pair.PairId).Count == 0 &&
                fx.WriterB.Preserves == 0 && fx.WriterB.Deletes == 0 &&
                await fx.B.ObserveAsync(victim.Path, CancellationToken.None) is not null,
                "a refusal settles no baseline, forgets nothing and performs no effect");
        }
        finally { DeleteFixture(folder); }
    }

    // Commits the Refused result against the store exactly as the real executor does (a real observed
    // refusal is durable before the loop sees it). It also reproduces the parked-shape cleanup: a writer
    // re-parks the deadline while the outcome is observed, so CommitResults drops the row and only the
    // reconcile loop's own re-save can restore it as due debt.
    private sealed class RefusedReconcileExecutor : IExecutor
    {
        private readonly IExecutor _inner;
        private readonly SqliteStateStore _store;
        private readonly string _pairId;
        private readonly string _actionId;

        public RefusedReconcileExecutor(IExecutor inner, SqliteStateStore store, string pairId, string actionId)
        {
            _inner = inner;
            _store = store;
            _pairId = pairId;
            _actionId = actionId;
        }

        public IAsyncEnumerable<ActionResult> ApplyAsync(Plan p, IEndpointWriter a, IEndpointWriter b,
            CancellationToken ct) => _inner.ApplyAsync(p, a, b, ct);

        public async Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen,
            IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null)
        {
            var refused = intents.Where(intent => intent.ActionId == _actionId).ToArray();
            if (refused.Length == 0) return await _inner.ReconcileAsync(intents, gen, a, b, ct, progress);
            var results = new List<ActionResult>();
            foreach (var intent in refused)
            {
                if (_store.SyncRetryAt(_pairId, intent.Path) is { } current)
                    _store.SaveSyncRetry(_pairId, current with { RetryAfter = DateTimeOffset.MaxValue });
                var result = new ActionResult(intent.ActionId, Outcome.Refused, null, null, null,
                    "Not enough free space; free space and retry.");
                _store.CommitResult(_pairId, gen, intent, result);
                results.Add(result);
            }
            var others = intents.Where(intent => intent.ActionId != _actionId).ToArray();
            if (others.Length > 0) results.AddRange(await _inner.ReconcileAsync(others, gen, a, b, ct, progress));
            return results;
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
