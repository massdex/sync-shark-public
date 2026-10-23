namespace Massdex.SyncShark.V2;

internal static class InterruptedDeleteChecks
{
    internal static async Task RunAsync(string folder)
    {
        foreach (var target in new[] { Side.A, Side.B })
        {
            await RestoreAfterCrashAsync(Path.Combine(folder, target.ToString()), target);
            await ObservationCasesAsync(Path.Combine(folder, target + "-observations"), target);
            await ExpiredIdentityReviewAsync(Path.Combine(folder, target + "-expired-review"), target);
            RepairPages(Path.Combine(folder, target + "-pages"), target);
        }
        ExistingSchemaIndex(Path.Combine(folder, "index-upgrade"));
        Console.WriteLine("PASS interrupted-delete reconciliation: both directions, durable retry, tombstone and retained-match safety");
    }

    private static PairDef Pair() => new("pair", "Pair A", new("a", SyncProviders.Local, null, "/a", "a"),
        new("b", SyncProviders.Dropbox, null, "/b", "b"), [], [], true);

    private static async Task ExpiredIdentityReviewAsync(string folder, Side target)
    {
        var pair = Pair();
        var a = new FakeEndpoint(Side.A, "a");
        var b = new FakeEndpoint(Side.B, "b");
        var sa = a.Seed("File", "original");
        var sb = b.Seed("File", "original");
        var writer = target == Side.A ? a : b;
        var source = target == Side.A ? b : a;
        var action = new SyncAction("expired-no-id", 6, ActionKind.Delete, target, "File", null,
            null, target == Side.A ? sa : sb, false, "linked")
            { ProviderEffect = new("delete-job", "expired", DateTimeOffset.UnixEpoch) };
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
        {
            store.SavePair(pair);
            Seed(store, pair, sa, sb);
            store.RecordIntent(pair.PairId, action);
            await writer.DeleteAsync(action, default);
            // Exercise a resolver caller without trusted historical identity while durable history exists:
            // result persistence must not infer permission to retire that history.
            var result = await EffectResolver.ResolveAsync(action,
                new TerminalWriter(writer, new DropboxEffectUncertainException("expired") { JobIdUnavailable = true }),
                source, default);
            Require(result is { Outcome: Outcome.Skipped, DeleteSourceIdentityMissing: true,
                DeleteSourceUnconfirmed: true, ForgetUnsafeDeleteBaseline: false },
                "expired no-identity reconciliation becomes review, not Done or endless Uncertain");
            store.CommitResult(pair.PairId, 2, action, result);
            Require(store.Baselines(pair.PairId).Count == 1 && store.UnresolvedIntents(pair.PairId).Count == 0,
                "review settlement keeps baseline and retires finished-unknown intent atomically");
        }
        using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        Require(reopened.Baselines(pair.PairId).Count == 1 &&
            reopened.SyncRetryAt(pair.PairId, "File") is { Kind: "decision", MissingDeleteIdentityReview: true,
                ReviewedActions: null, ProposedActions.Count: 1 },
            "baseline and unsigned per-item review survive database reopen");
        Require(reopened.OpenNotices(pair.PairId).Any(notice => notice.ReasonKey == "deletion-review" && notice.Path == "File"),
            "durable review notice remains actionable after reopen");
        Require(await source.ObserveAsync("File", default) is not null,
            "surviving endpoint is untouched by finished-unknown reconciliation");
    }

    private static async Task RestoreAfterCrashAsync(string folder, Side target)
    {
        var pair = Pair();
        var a = new FakeEndpoint(Side.A, "a");
        var b = new FakeEndpoint(Side.B, "b");
        var sa = a.Seed("File", "original", mtime: DateTimeOffset.UnixEpoch);
        var sb = b.Seed("File", "original", mtime: DateTimeOffset.UnixEpoch);
        var delete = new SyncAction("interrupted", 6, ActionKind.Delete, target, "File", null, null,
            target == Side.A ? sa : sb, false, "linked");
        var victim = target == Side.A ? a : b;
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
        {
            store.SavePair(pair);
            Seed(store, pair, sa, sb);
            // Existing first-sync handoff and deletion history are deliberately contradictory.
            store.SaveSyncRetry(pair.PairId, new("File", "sync", 1, "old", DateTimeOffset.MinValue,
                Match: new PrepareMatch(sa.ObjectId, sb.ObjectId)));
            store.StoreDeletionEvidence(pair.PairId, target == Side.A ? "a" : "b",
                [new("File", DateTimeOffset.UnixEpoch.AddDays(1), "cursor", sa.Hash)]);
            store.RecordIntent(pair.PairId, delete);
            await victim.DeleteAsync(delete, default); // effect landed, process lost its acknowledgement
            Snapshot(store, target == Side.A ? "a" : "b", target, []);
            var executor = new Executor(store, V2Settings.Default, ExecutorChecks.Admission());
            var result = (await executor.ReconcileAsync([delete], 2, a, b, default)).Single();
            Require(result.Outcome == Outcome.Skipped && result.ForgetUnsafeDeleteBaseline,
                "landed unsafe delete is not applied to sync history");
            Require(store.Baselines(pair.PairId).Count == 0 && store.UnresolvedIntents(pair.PairId).Count == 0,
                "intent retirement and baseline invalidation commit together");
            Require(store.SyncRetryAt(pair.PairId, "File") is { Match: null, Held: null },
                "stale prepared match and held evidence cannot authorize the inverse deletion");
            Require(store.DeletionEvidenceRows(pair.PairId, target == Side.A ? "a" : "b").Count == 0,
                "attributed stale deletion evidence is removed");
        }
        // Crash here: no changes to either endpoint or either persisted inventory cursor.
        using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        // Even a stale caller reattaching the old match must not turn repair debt into deletion authority.
        var retained = reopened.SyncRetryAt(pair.PairId, "File")!;
        reopened.SaveSyncRetry(pair.PairId, retained with { Match = new PrepareMatch(sa.ObjectId, sb.ObjectId) });
        var retries = reopened.DueSyncRetries(pair.PairId, DateTimeOffset.UtcNow, 256);
        Require(retries.Count == 1, "restoration survives reopen and unchanged cursors");
        var scope = new RunScope(reopened, _ => true, _ => true)
            .LoadTouched(pair, retries.Select(retry => retry.Path).ToArray(), [], []);
        var rows = Planner.Join(scope.Baselines, scope.A, scope.B, new Dictionary<string, DateTimeOffset?>(),
            new Dictionary<string, DateTimeOffset?>(), new(true, true, new HashSet<string>(), new HashSet<string>()), false);
        // Simulate a newly returned provider tombstone after reopening, including matching deleted bytes.
        rows = rows.Select(row => row with { Ev = row.Ev with
        {
            DeletedA = target == Side.A, DeletedB = target == Side.B,
            DeletedAtA = target == Side.A ? DateTimeOffset.UnixEpoch.AddDays(1) : null,
            DeletedAtB = target == Side.B ? DateTimeOffset.UnixEpoch.AddDays(1) : null,
            DeletedHashA = sa.Hash, DeletedHashB = sb.Hash
        } }).ToArray();
        var protectedRows = InterruptedDeleteRepair.Protect(rows, reopened, pair.PairId);
        var plan = Planner.Decide(pair.PairId, 3, protectedRows,
            new(TimeSpan.Zero, false, new("{name} [{side}]{ext}", "s", TimeSpan.Zero, "A", "B"), 10)
            { MatchedAtPrepare = new RetryMatches(reopened, pair.PairId) });
        Require(plan.Actions.All(action => action.Kind != ActionKind.Delete) &&
            plan.Actions.Any(action => action.Kind == ActionKind.CopyNew && action.Target == target),
            "attributed tombstone plans restore, never opposite delete");
        var repairPaths = InterruptedDeleteRepair.SourcePaths(scope, reopened, pair.PairId);
        Require(repairPaths.Contains("File"), "matching original source is excluded from first-sync deletion probes");
        var firstSyncPlan = Planner.Decide(pair.PairId, 3, protectedRows,
            new(TimeSpan.Zero, true, new("{name} [{side}]{ext}", "s", TimeSpan.Zero, "A", "B"), 10)
            { MatchedAtPrepare = InterruptedDeleteRepair.WithoutMatches(
                new Dictionary<string, PrepareMatch> { ["File"] = new(sa.ObjectId, sb.ObjectId) }, repairPaths) });
        Require(firstSyncPlan.Actions.All(action => action.Kind != ActionKind.Delete) &&
            firstSyncPlan.Actions.Any(action => action.Kind == ActionKind.CopyNew && action.Target == target),
            "first-sync tombstone and retained match also restore rather than delete");
        var executor2 = new Executor(reopened, V2Settings.Default, ExecutorChecks.Admission());
        await foreach (var result in executor2.ApplyAsync(plan, a, b, default))
            Require(result.Outcome == Outcome.Done, "restoration executes successfully: " + result.Reason);
        Require(await a.ObserveAsync("File", default) is not null && await b.ObserveAsync("File", default) is not null,
            "both copies exist after restoration");
        reopened.ClearSyncRetry(pair.PairId, "File");
        reopened.RefreshInterruptedDeleteNotice(pair.PairId);
        Require(!reopened.OpenNotices(pair.PairId).Any(notice => notice.ReasonKey == SqliteStateStore.InterruptedDeleteRestoreKind),
            "one repair summary closes when pending count reaches zero");
    }

    private static void ExistingSchemaIndex(string folder)
    {
        using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System)) store.SavePair(Pair());
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(folder, "v2", "state.db")};Pooling=False"))
        {
            connection.Open();
            // Simulate an already-current store created before the additive index was shipped.
            StoreSchema.Execute(connection, null, "DROP INDEX sync_retry_kind;");
        }
        using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        reopened.RefreshInterruptedDeleteNotice(Pair().PairId); // forced indexed COUNT requires the migration
        Require(reopened.InterruptedDeleteRepairsAt(Pair().PairId, ["File"]).Count == 0,
            "already-current schema gains repair index without resetting durable state");
    }

    private static void RepairPages(string folder, Side target)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        var pair = Pair();
        store.SavePair(pair);
        var endpoint = new FakeEndpoint(target == Side.A ? Side.B : Side.A, "source");
        var states = Enumerable.Range(0, 300).Select(i => endpoint.Seed("File-" + i.ToString("D3"), "original")).ToArray();
        foreach (var state in states)
        {
            var action = new SyncAction("repair-" + state.Path, 6, ActionKind.CopyNew, target,
                state.Path, null, state, null, false, null);
            store.SaveSyncRetry(pair.PairId, new(state.Path, SqliteStateStore.InterruptedDeleteRestoreKind, 1,
                "retry", state == states[^1] ? DateTimeOffset.MaxValue : DateTimeOffset.MinValue,
                ProposedActions: [action]));
        }
        // The last attributed item is backed off, and others lie beyond the due page's first 256.
        Require(store.DueSyncRetries(pair.PairId, DateTimeOffset.UtcNow, 256).Count == 256, "fixture exceeds due page");
        var rows = states.Select(state => new PlanRow(null, target == Side.B ? state : null,
            target == Side.A ? state : null, new Evidence(target == Side.A, target == Side.B, true, true))).ToArray();
        var protectedRows = InterruptedDeleteRepair.Protect(rows, store, pair.PairId);
        Require(protectedRows.All(row => !row.Ev.DeletedA && !row.Ev.DeletedB),
            "all repair rows masked across page boundaries and backoff");
        Snapshot(store, target == Side.A ? "b" : "a", target == Side.A ? Side.B : Side.A, states);
        Snapshot(store, target == Side.A ? "a" : "b", target, []);
        var scope = new RunScope(store, _ => true, _ => true).LoadFull(pair);
        Require(InterruptedDeleteRepair.SourcePaths(scope, store, pair.PairId).Count == 300,
            "first-sync protects every matching repair path, including backed-off rows");
        store.RefreshInterruptedDeleteNotice(pair.PairId);
        Require(store.OpenNotices(pair.PairId).Single(notice =>
            notice.ReasonKey == SqliteStateStore.InterruptedDeleteRestoreKind).Detail.StartsWith("300 ", StringComparison.Ordinal),
            "indexed pending count includes all repair rows");
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(folder, "v2", "state.db")};Pooling=False");
        connection.Open();
        var explain = StoreSchema.QueryPlan(connection, "repair-count",
            "SELECT COUNT(*) FROM sync_retry INDEXED BY sync_retry_kind WHERE pair_no=$p AND json_extract(json,'$.Kind')=$kind;",
            "$p", "$kind");
        Require(explain.Any(line => line.Contains("SEARCH", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("sync_retry_kind", StringComparison.Ordinal)), "count searches kind index, not retry JSON scan");
    }

    private static async Task ObservationCasesAsync(string folder, Side target)
    {
        using var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
        var pair = Pair();
        store.SavePair(pair);
        var a = new FakeEndpoint(Side.A, "a");
        var b = new FakeEndpoint(Side.B, "b");
        var sa = a.Seed("File", "original");
        var sb = b.Seed("File", "original");
        Seed(store, pair, sa, sb);
        var action = new SyncAction("uncertain", 6, ActionKind.Delete, target, "File", null, null,
            target == Side.A ? sa : sb, false, "linked");
        var writer = target == Side.A ? a : b;
        var source = target == Side.A ? b : a;
        var historical = target == Side.A ? sb : sa;
        var present = await EffectResolver.ResolveAsync(action, writer, source, default, deleteRestorePending: false, historicalDeleteSource: historical);
        Require(present.Outcome == Outcome.Skipped && !present.ForgetUnsafeDeleteBaseline,
            "target still present keeps baseline intact");
        var submitted = action with { ProviderEffect = new("delete-submit", "unknown", DateTimeOffset.UtcNow) };
        var withinGrace = await EffectResolver.ResolveAsync(submitted, writer, source, default, historicalDeleteSource: historical);
        Require(withinGrace.Outcome == Outcome.Uncertain, "E8 same target within submission grace remains protected and unknown");
        var elapsed = submitted with { ProviderEffect = new("delete-submit", "unknown", DateTimeOffset.UnixEpoch) };
        var notApplied = await EffectResolver.ResolveAsync(elapsed, writer, source, default, historicalDeleteSource: historical);
        Require(notApplied.Outcome == Outcome.Skipped && !notApplied.ForgetUnsafeDeleteBaseline,
            "E8 same identity after grace retires as not-applied without replaying deletion");
        var changedSubmission = elapsed with { ExpectedTarget = elapsed.ExpectedTarget! with { VersionId = "older-version" } };
        var superseded = await EffectResolver.ResolveAsync(changedSubmission, writer, source, default, historicalDeleteSource: historical);
        Require(superseded.Outcome == Outcome.Skipped && !superseded.ForgetUnsafeDeleteBaseline,
            "E8 changed target retires old submission authority without deleting current bytes");
        await writer.DeleteAsync(action, default);
        var approved = await EffectResolver.ResolveAsync(action with { RemovalReviewChoice = true }, writer, source, default);
        Require(approved.Outcome == Outcome.Done && !approved.ForgetUnsafeDeleteBaseline,
            "durable explicit victim approval is not reclassified as absence propagation");
        var noIdentity = await EffectResolver.ResolveAsync(action, writer, source, default);
        Require(noIdentity.Outcome == Outcome.Uncertain && !noIdentity.ForgetUnsafeDeleteBaseline,
            "absent target without trusted source identity never settles a deletion");
        var pending = action with { ProviderEffect = new("delete-job", "job", DateTimeOffset.UnixEpoch) };
        foreach (var kind in new[] { "delete-job" })
        {
            var pendingResult = await EffectResolver.ResolveAsync(pending with
                { ProviderEffect = new(kind, "unknown", DateTimeOffset.UnixEpoch) }, writer, source, default,
                deleteRestorePending: false, historicalDeleteSource: historical);
            Require(pendingResult.Outcome == Outcome.Uncertain && !pendingResult.ForgetUnsafeDeleteBaseline,
                "unknown provider job/submit never restores under a possibly running delete");
        }
        var absentSubmission = await EffectResolver.ResolveAsync(elapsed, writer, source, default, historicalDeleteSource: historical);
        Require(absentSubmission.Outcome == Outcome.Skipped && absentSubmission.ForgetUnsafeDeleteBaseline,
            "E8 absent submission uses source-aware absence resolution and protects surviving source content");
        var terminal = await EffectResolver.ResolveAsync(pending, new TerminalWriter(writer), source, default,
            deleteRestorePending: false, historicalDeleteSource: historical);
        Require(terminal.ForgetUnsafeDeleteBaseline && terminal.Outcome == Outcome.Skipped,
            "terminal provider success still rechecks source and queues restoration");
        var expiredWriter = new TerminalWriter(writer,
            new DropboxEffectUncertainException("expired job") { JobIdUnavailable = true });
        var expired = await EffectResolver.ResolveAsync(pending, expiredWriter, source, default,
            deleteRestorePending: false, historicalDeleteSource: historical);
        Require(expired.Outcome == Outcome.Skipped && expired.ForgetUnsafeDeleteBaseline,
            "expired job after grace rechecks same-ID survivor and queues repair");
        var recent = await EffectResolver.ResolveAsync(pending with
            { ProviderEffect = new("delete-job", "expired", DateTimeOffset.UtcNow) }, expiredWriter, source, default,
            deleteRestorePending: false, historicalDeleteSource: historical);
        Require(recent.Outcome == Outcome.Uncertain, "expired id within grace remains protected");
        foreach (var error in new Exception[] { new IOException("lookup failed"),
            new DropboxEffectUncertainException("internal error"), new HttpRequestException("offline") })
        {
            var unknown = await EffectResolver.ResolveAsync(pending, new TerminalWriter(writer, error), source,
                default, deleteRestorePending: false, historicalDeleteSource: historical);
            Require(unknown.Outcome == Outcome.Uncertain && !unknown.ForgetUnsafeDeleteBaseline,
                "generic lookup failures never impersonate expired job evidence");
        }
        var active = await EffectResolver.ResolveAsync(pending, new TerminalWriter(writer, applied: null), source,
            default, deleteRestorePending: false, historicalDeleteSource: historical);
        Require(active.Outcome == Outcome.Uncertain, "active jobs remain protected after grace with absent target");
        var oldSource = await source.ObserveAsync("File", default);
        await source.DeleteAsync(action with { ExpectedTarget = oldSource, Target = target == Side.A ? Side.B : Side.A }, default);
        var absent = await EffectResolver.ResolveAsync(pending, expiredWriter, source, default,
            deleteRestorePending: false, historicalDeleteSource: historical);
        Require(absent.Outcome == Outcome.Done && !absent.ForgetUnsafeDeleteBaseline,
            "expired job with trusted original source now absent settles normally");
        source.Seed("File", "replacement");
        store.RecordIntent(pair.PairId, action);
        var executor = new Executor(store, V2Settings.Default, ExecutorChecks.Admission());
        var changed = (await executor.ReconcileAsync([action], 2, a, b, default)).Single();
        Require(changed.Outcome == Outcome.Uncertain && store.Baselines(pair.PairId).Count == 1,
            "same-path different identity cannot invalidate history or authorize reverse deletion");
        await executor.ReconcileAsync([action], 2, a, b, default);
        Require(store.OpenNotices(pair.PairId).Count(notice => notice.ReasonKey == "interrupted-delete-unverified") == 1,
            "repeated unknown identity produces one notice, not whole-run failure");
        var uncertainNotice = store.OpenNotices(pair.PairId).Single(notice => notice.ReasonKey == "interrupted-delete-unverified");
        Require(NoticeChoices.Options(uncertainNotice).Any(option => option.Choice == NoticeChoices.RetryNow) &&
            NoticeChoices.Options(uncertainNotice).Any(option => option.Choice == NoticeChoices.Ignore),
            "uncertain deletion uses the shared Retry and Ignore choices");
        store.AddNotices([new(pair.PairId, 40, NoticeKind.Info, action.Path, "Generic retry", "sync-retry")]);
        Require(!store.OpenNotices(pair.PairId).Any(notice => notice.ReasonKey == "sync-retry" && notice.Path == action.Path),
            "uncertain deletion owns its explanation without a second generic retry notice");
    }

    private static void Seed(SqliteStateStore store, PairDef pair, ItemState a, ItemState b)
    {
        Snapshot(store, "a", Side.A, [a]);
        Snapshot(store, "b", Side.B, [b]);
        var adopt = new SyncAction("seed", 3, ActionKind.Adopt, Side.B, b.Path, null, a, b, false, "linked");
        store.CommitResult(pair.PairId, 1, adopt, new(adopt.ActionId, Outcome.Done, a, b, null, null));
    }

    private static void Snapshot(SqliteStateStore store, string root, Side side, IReadOnlyList<ItemState> items)
    {
        var gen = store.BeginGeneration(root, "fixture");
        store.WriteSnapshot(new(root, side, gen, "cursor", items, []));
        store.CompleteGeneration(gen, "cursor", true);
    }

    private sealed class TerminalWriter(IEndpointWriter inner, Exception? failure = null, bool? applied = true) : IEndpointWriter, IProviderEffectReconciler
    {
        public Task<bool?> ResolveProviderEffectAsync(SyncAction action, ProviderJobCheckCache? jobChecks, CancellationToken ct) =>
            failure is null ? Task.FromResult(applied) : Task.FromException<bool?>(failure);
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct) => inner.PutAsync(action, content, ct);
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => inner.MoveAsync(action, ct);
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => inner.DeleteAsync(action, ct);
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => inner.MkDirAsync(path, ct);
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) => inner.PreserveAsync(victim, actionId, ct);
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => inner.ObserveAsync(path, ct);
        public Task<Stream> OpenReadAsync(ItemState state, CancellationToken ct) => inner.OpenReadAsync(state, ct);
    }

    private static void Require(bool condition, string message) => LocalChecks.Require(condition, message);
}
