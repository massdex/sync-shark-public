namespace Massdex.SyncShark.V2;

internal static class InterruptedDeleteChecks
{
    internal static async Task RunAsync(string folder)
    {
        foreach (var target in new[] { Side.A, Side.B })
        {
            await RestoreAfterCrashAsync(Path.Combine(folder, target.ToString()), target);
            await ObservationCasesAsync(Path.Combine(folder, target + "-observations"), target);
        }
        Console.WriteLine("PASS interrupted-delete reconciliation: both directions, durable retry, tombstone and retained-match safety");
    }

    private static PairDef Pair() => new("pair", "Pair A", new("a", SyncProviders.Local, null, "/a", "a"),
        new("b", SyncProviders.Dropbox, null, "/b", "b"), [], [], true);

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
        var protectedRows = InterruptedDeleteRepair.Protect(rows, retries);
        var plan = Planner.Decide(pair.PairId, 3, protectedRows,
            new(TimeSpan.Zero, false, new("{name} [{side}]{ext}", "s", TimeSpan.Zero, "A", "B"), 10)
            { MatchedAtPrepare = new RetryMatches(reopened, pair.PairId) });
        Require(plan.Actions.All(action => action.Kind != ActionKind.Delete) &&
            plan.Actions.Any(action => action.Kind == ActionKind.CopyNew && action.Target == target),
            "attributed tombstone plans restore, never opposite delete");
        var repairPaths = InterruptedDeleteRepair.SourcePaths(scope, retries);
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
        var present = await EffectResolver.ResolveAsync(action, writer, source, default, historicalDeleteSource: historical);
        Require(present.Outcome == Outcome.Skipped && !present.ForgetUnsafeDeleteBaseline,
            "target still present keeps baseline intact");
        await writer.DeleteAsync(action, default);
        var noIdentity = await EffectResolver.ResolveAsync(action, writer, source, default);
        Require(noIdentity.Outcome == Outcome.Uncertain && !noIdentity.ForgetUnsafeDeleteBaseline,
            "absent target without trusted source identity never settles a deletion");
        var pending = action with { ProviderEffect = new("delete-job", "job", DateTimeOffset.UnixEpoch) };
        foreach (var kind in new[] { "delete-job", "delete-submit" })
        {
            var pendingResult = await EffectResolver.ResolveAsync(pending with
                { ProviderEffect = new(kind, "unknown", DateTimeOffset.UnixEpoch) }, writer, source, default,
                historicalDeleteSource: historical);
            Require(pendingResult.Outcome == Outcome.Uncertain && !pendingResult.ForgetUnsafeDeleteBaseline,
                "unknown provider job/submit never restores under a possibly running delete");
        }
        var terminal = await EffectResolver.ResolveAsync(pending, new TerminalWriter(writer), source, default,
            historicalDeleteSource: historical);
        Require(terminal.ForgetUnsafeDeleteBaseline && terminal.Outcome == Outcome.Skipped,
            "terminal provider success still rechecks source and queues restoration");
        var oldSource = await source.ObserveAsync("File", default);
        await source.DeleteAsync(action with { ExpectedTarget = oldSource, Target = target == Side.A ? Side.B : Side.A }, default);
        source.Seed("File", "replacement");
        store.RecordIntent(pair.PairId, action);
        var executor = new Executor(store, V2Settings.Default, ExecutorChecks.Admission());
        var changed = (await executor.ReconcileAsync([action], 2, a, b, default)).Single();
        Require(changed.Outcome == Outcome.Uncertain && store.Baselines(pair.PairId).Count == 1,
            "same-path different identity cannot invalidate history or authorize reverse deletion");
        await executor.ReconcileAsync([action], 2, a, b, default);
        Require(store.OpenNotices(pair.PairId).Count(notice => notice.ReasonKey == "interrupted-delete-unverified") == 1,
            "repeated unknown identity produces one notice, not whole-run failure");
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

    private sealed class TerminalWriter(IEndpointWriter inner) : IEndpointWriter, IProviderEffectReconciler
    {
        public Task<bool?> ResolveProviderEffectAsync(SyncAction action, ProviderJobCheckCache? jobChecks, CancellationToken ct) => Task.FromResult<bool?>(true);
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
