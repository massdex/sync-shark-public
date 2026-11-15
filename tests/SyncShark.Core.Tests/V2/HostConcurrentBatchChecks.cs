using System.Reflection;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Checks the v2 host's concurrent-batch contract: two manual batches over unrelated pairs may overlap, a damaged
/// pair cancels its blocked siblings (a thrown <see cref="StateDamagedException"/> drains and rethrows, a typed
/// state-damaged report drains and maps without a store read), a stop cancels every overlapping batch, and the
/// active-run snapshot stays truthful while any batch remains.
/// </summary>
/// <remarks>
/// Every run is a <c>BlockedSyncRun</c> fake: each pair enters and then blocks until the check releases it
/// or its batch token is cancelled. No store content, endpoint, provider or file is touched.
/// </remarks>
internal static class HostConcurrentBatchChecks
{
    // Every wait is bounded so a missing cancellation can only fail the check, never hang the suite.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    // OUTCOME bdef7686 adds RunOutcomes.StateDamaged = "state_damaged". The literal keeps this fixture compiling
    // until that constant is integrated; replace it with RunOutcomes.StateDamaged once it lands.
    private const string TypedStateDamaged = "state_damaged";

    public static async Task RunAsync()
    {
        await UnrelatedManualBatchesOverlapAsync();
        await DamageCancelsBlockedSiblingAndThrowsAsync();
        await DamageCancelsBlockedSiblingAndThrowsAsync(freshDatabase: true);
        await TypedDamageCancelsSiblingAndMapsReportAsync();
        await StopCancelsEveryOverlappingBatchAsync();
        await ActiveRunStaysTruthfulWhileAnyBatchRemainsAsync();
        Console.WriteLine("v2 host concurrent batch checks passed.");
    }

    private static async Task UnrelatedManualBatchesOverlapAsync()
    {
        using var store = new InMemoryStateStore();
        var first = PairDefOf("p1", "Alpha");
        var second = PairDefOf("p2", "Beta", 2);
        store.SavePair(first);
        store.SavePair(second);
        using var run = new BlockedSyncRun();
        var host = Host(store, run);
        var batchA = host.RunPairsAsync([PairOf(first)], "manual");
        Task<SyncAuthorityBatchResult>? batchB = null;
        try
        {
            await EnteredAsync(run.Hold("p1").Entered.Task, "the first manual batch's pair");
            batchB = host.RunPairsAsync([PairOf(second)], "manual");
            await EnteredAsync(run.Hold("p2").Entered.Task, "the second manual batch's unrelated pair");

            Require(!batchA.IsCompleted && !batchB!.IsCompleted,
                "two manual batches of unrelated pairs run at the same time");

            run.Hold("p1").Release.TrySetResult();
            run.Hold("p2").Release.TrySetResult();
            var resultA = await CompletedAsync(batchA, "the first manual batch");
            var resultB = await CompletedAsync(batchB!, "the second manual batch");
            Require(resultA.Items.Single().Pair.Id == "p1" && !resultA.Stopped,
                "the first batch completes its own pair");
            Require(resultB.Items.Single().Pair.Id == "p2" && !resultB.Stopped,
                "the second batch completes its own pair");
            Require(!host.HasActiveRun && host.ActiveRun is null,
                "no active run remains once both overlapping batches complete");
        }
        finally
        {
            await SettleAsync(run, batchA, batchB);
        }
    }

    private static async Task DamageCancelsBlockedSiblingAndThrowsAsync(bool freshDatabase = false)
    {
        using var store = new InMemoryStateStore();
        var first = PairDefOf("p1", "Alpha");
        var second = PairDefOf("p2", "Beta", 2);
        store.SavePair(first);
        store.SavePair(second);
        using var run = new BlockedSyncRun(damageFor: "p1", damage: freshDatabase
            ? new StateNeedsFreshDatabaseException(1, 2) : new StateDamagedException("Injected state damage."));
        var guarded = PairReadGuardStore.For(store, () => run.Hold("p1").DamageSignal.Task.IsCompleted);
        var host = Host(guarded.Store, run);
        Task<SyncAuthorityBatchResult>? batch = null;
        try
        {
            batch = host.RunPairsAsync([PairOf(first), PairOf(second)], "manual");
            await EnteredAsync(run.Hold("p1").Entered.Task, "the pair that reports damage");
            await EnteredAsync(run.Hold("p2").Entered.Task, "the sibling pair blocked on the batch token");

            run.Hold("p1").DamageSignal.TrySetResult();
            if (freshDatabase)
                await Throws<StateNeedsFreshDatabaseException>(batch!, "a schema-incompatible manual sync-all batch");
            else
                await Throws<StateDamagedException>(batch!, "a damaged manual sync-all batch");
            Require(guarded.RefusedReads == 0, "thrown fatal persistence failures never re-read the damaged store");
            // The sibling is released only by the batch token, so this can only be complete if the damage
            // cancelled it and the batch drained before the exception reached the caller.
            Require(run.Hold("p2").Cancelled.Task.IsCompleted,
                "the damaged pair's exception cancels the sibling blocked on the batch token");
            Require(!host.HasActiveRun && host.ActiveRun is null,
                "the damaged batch drains before the caller sees the exception");
            Require(!await host.StopActiveRunAsync(),
                "nothing is left active after the damaged batch drained");
        }
        finally
        {
            await SettleAsync(run, batch);
        }
    }

    // Typed damage is reported in the counted RunReport, not thrown: the batch cancels its blocked sibling, maps
    // the report to the damaged pair's item from the definition captured before the run, and returns normally.
    private static async Task TypedDamageCancelsSiblingAndMapsReportAsync()
    {
        using var inner = new InMemoryStateStore();
        var first = PairDefOf("p1", "Alpha");
        var second = PairDefOf("p2", "Beta", 2);
        inner.SavePair(first);
        inner.SavePair(second);
        using var run = new BlockedSyncRun(typedDamageFor: "p1");
        // The guard refuses any pair read after typed damage, proving the mapping keeps the pre-run definition.
        var store = PairReadGuardStore.For(inner, () => run.Hold("p1").TypedDamageSignal.Task.IsCompleted);
        var host = Host(store.Store, run);
        Task<SyncAuthorityBatchResult>? batch = null;
        try
        {
            batch = host.RunPairsAsync([PairOf(first), PairOf(second)], "manual");
            await EnteredAsync(run.Hold("p1").Entered.Task, "the pair that reports typed damage");
            await EnteredAsync(run.Hold("p2").Entered.Task, "the sibling pair blocked on the batch token");

            run.Hold("p1").TypedDamageSignal.TrySetResult();
            await WaitedAsync(run.Hold("p2").Cancelled.Task,
                "the sibling blocked on the batch token was not cancelled after typed damage");

            var result = await CompletedAsync(batch!, "a typed-damage manual batch");
            Require(result.Stopped, "typed damage stops the batch instead of rethrowing");
            var item = result.Items.Single(entry => entry.Pair.Id == "p1");
            Require(item.FailureType == TypedStateDamaged,
                "typed damage is carried as the state_damaged failure type");
            Require(item.Result is { Outcome: TypedStateDamaged, Uploaded: 2, Downloaded: 1 },
                "the result retains the typed outcome and the counted report");
            Require(item.FailureMessage == SyncRunOutcomeProjection.FromResult(item.Result!).NotificationDetail,
                "the item carries the shared recovery message from the outcome projection");
            Require(result.Items.Count == 1, "the cancelled sibling contributes no item");
            Require(store.RefusedReads == 0,
                "mapping the typed-damage report reads no pair from the store after damage");
            Require(!host.HasActiveRun && host.ActiveRun is null,
                "the typed-damage batch drains and returns without rethrowing");
        }
        finally
        {
            await SettleAsync(run, batch);
        }
    }

    private static async Task StopCancelsEveryOverlappingBatchAsync()
    {
        using var store = new InMemoryStateStore();
        var first = PairDefOf("p1", "Alpha");
        var second = PairDefOf("p2", "Beta", 2);
        store.SavePair(first);
        store.SavePair(second);
        using var run = new BlockedSyncRun();
        var host = Host(store, run);
        var batchA = host.RunPairsAsync([PairOf(first)], "manual");
        Task<SyncAuthorityBatchResult>? batchB = null;
        try
        {
            await EnteredAsync(run.Hold("p1").Entered.Task, "the first overlapping batch's pair");
            batchB = host.RunPairsAsync([PairOf(second)], "manual");
            await EnteredAsync(run.Hold("p2").Entered.Task, "the second overlapping batch's pair");

            Require(await host.StopActiveRunAsync().WaitAsync(Bound),
                "stop reports that batches were active");
            var resultA = await CompletedAsync(batchA, "the first overlapping batch");
            var resultB = await CompletedAsync(batchB!, "the second overlapping batch");
            Require(resultA.Stopped && resultA.Items.Count == 0,
                "stop cancels the first overlapping batch");
            Require(resultB.Stopped && resultB.Items.Count == 0,
                "stop cancels every overlapping batch, not only the last one");
            Require(!host.HasActiveRun && host.ActiveRun is null,
                "stopping all overlapping batches clears the active run");
            Require(!await host.StopActiveRunAsync(),
                "stopping again reports nothing active");
        }
        finally
        {
            await SettleAsync(run, batchA, batchB);
        }
    }

    private static async Task ActiveRunStaysTruthfulWhileAnyBatchRemainsAsync()
    {
        using var store = new InMemoryStateStore();
        var first = PairDefOf("p1", "Alpha");
        var second = PairDefOf("p2", "Beta", 2);
        store.SavePair(first);
        store.SavePair(second);
        using var run = new BlockedSyncRun();
        var host = Host(store, run);
        var batchA = host.RunPairsAsync([PairOf(first)], "manual");
        Task<SyncAuthorityBatchResult>? batchB = null;
        try
        {
            await EnteredAsync(run.Hold("p1").Entered.Task, "the first batch's pair");
            batchB = host.RunPairsAsync([PairOf(second)], "manual");
            await EnteredAsync(run.Hold("p2").Entered.Task, "the second batch's pair");
            Require(host.HasActiveRun && host.ActiveRun?.PairId is "p1" or "p2",
                "both overlapping batches are reported active");

            run.Hold("p1").Release.TrySetResult();
            var resultA = await CompletedAsync(batchA, "the first batch");
            Require(resultA.Items.Single().Pair.Id == "p1" && !resultA.Stopped,
                "the first batch completes while the second is still blocked");
            Require(host.HasActiveRun,
                "HasActiveRun stays true while any overlapping batch remains");
            Require(host.ActiveRun is { PairId: "p2" },
                "ActiveRun still names the pair that is actually running");

            run.Hold("p2").Release.TrySetResult();
            var resultB = await CompletedAsync(batchB!, "the second batch");
            Require(resultB.Items.Single().Pair.Id == "p2" && !resultB.Stopped,
                "the remaining batch completes on its own");
            Require(!host.HasActiveRun && host.ActiveRun is null,
                "the active run clears only after the last batch ends");
        }
        finally
        {
            await SettleAsync(run, batchA, batchB);
        }
    }

    // ---- helpers ----
    private static V2SyncHost Host(IStateStore store, ISyncRun run) =>
        new(store, run, new ObservedExecutor(new ScriptedExecutor()), v1: null!, V2Settings.Default,
            TimeProvider.System, @"C:\fixture\syncshark.db");

    // Each pair gets its own local object id (8-digit volume, 16-digit file id): a shared identity would let a
    // root-identity assumption pass by accident when two pairs are in play.
    private static PairDef PairDefOf(string id, string name, int localId = 1) => new(
        id, name,
        new RootDef("r:local:" + id, SyncProviders.Local, null, @"C:\root\" + id,
            "id:local:0a1b2c3d:deadbeefcafeba" + localId.ToString("x2")),
        new RootDef("r:dropbox:" + id, SyncProviders.Dropbox, "connection-fixture-3", "/root/" + id,
            "id:dropbox:" + id),
        [], [], true);

    private static SyncPair PairOf(PairDef def) => PairMapping.ToSyncPair(def, false, V2Settings.Default);

    private static async Task EnteredAsync(Task entered, string label)
    {
        try
        {
            await entered.WaitAsync(Bound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                label + " did not enter a run within " + Bound.TotalSeconds + "s.");
        }
    }

    // Waits for a one-shot signal the production path must raise; a missing signal can only fail, never hang.
    private static async Task WaitedAsync(Task signal, string label)
    {
        try
        {
            await signal.WaitAsync(Bound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(label + " (waited " + Bound.TotalSeconds + "s).");
        }
    }

    private static async Task<SyncAuthorityBatchResult> CompletedAsync(
        Task<SyncAuthorityBatchResult> batch, string label)
    {
        try
        {
            return await batch.WaitAsync(Bound).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(label + " did not complete within " + Bound.TotalSeconds + "s.");
        }
    }

    private static async Task<TException> Throws<TException>(Task batch, string label) where TException : Exception
    {
        try
        {
            await batch.WaitAsync(Bound).ConfigureAwait(false);
        }
        catch (TException error)
        {
            return error;
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(label + " did not finish within " + Bound.TotalSeconds + "s.");
        }
        throw new InvalidOperationException(label + ": expected " + typeof(TException).Name + ".");
    }

    // Releases every pair and lets the batches finish, so a failed assertion cannot leave work parked.
    private static async Task SettleAsync(BlockedSyncRun run, params Task?[] batches)
    {
        run.ReleaseAll();
        foreach (var batch in batches)
        {
            if (batch is null) continue;
            try
            {
                await batch.WaitAsync(Bound).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The check's own assertions decide the outcome; this only prevents unobserved, parked work.
            }
        }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    /// <summary>
    /// A store that forwards everything but refuses any pair read once typed damage has been signalled, proving the
    /// host maps the counted report from the definition it captured before the run instead of reading it afterwards.
    /// </summary>
    public class PairReadGuardStore : DispatchProxy
    {
        private IStateStore _inner = null!;
        private Func<bool> _damaged = () => false;

        internal int RefusedReads;

        internal IStateStore Store => (IStateStore)(object)this;

        internal static PairReadGuardStore For(IStateStore inner, Func<bool> damaged)
        {
            var proxy = (PairReadGuardStore)(object)DispatchProxy.Create<IStateStore, PairReadGuardStore>();
            proxy._inner = inner;
            proxy._damaged = damaged;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(IStateStore.Pair) && _damaged())
            {
                RefusedReads++;
                throw new InvalidOperationException(
                    "The host read the folder pair from the store after typed damage.");
            }
            return method!.Invoke(_inner, args);
        }
    }

    /// <summary>
    /// A fake runner that enters one pair, blocks until the check releases it or its batch token is cancelled, and
    /// reports a complete run otherwise. One designated pair instead throws <see cref="StateDamagedException"/> when
    /// the check signals it, and another returns a typed state-damaged report. It holds no store content, endpoint
    /// or provider.
    /// </summary>
    private sealed class BlockedSyncRun : ISyncRun, IDisposable
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PairHold> _pairs =
            new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _stopped = new();
        private readonly string? _damageFor;
        private readonly string? _typedDamageFor;
        private readonly Exception _damage;

        internal BlockedSyncRun(string? damageFor = null, string? typedDamageFor = null, Exception? damage = null)
        {
            _damage = damage ?? new StateDamagedException("Injected state damage.");
            _damageFor = damageFor;
            _typedDamageFor = typedDamageFor;
        }

        internal PairHold Hold(string pairId) => _pairs.GetOrAdd(pairId, _ => new PairHold());

        internal void ReleaseAll()
        {
            foreach (var hold in _pairs.Values)
            {
                // The damaged pair waits on its damage signal, not its release gate; clean-up must free both so a
                // failed early assertion can never leave the damage pair parked until the settle timeout.
                hold.DamageSignal.TrySetResult();
                hold.TypedDamageSignal.TrySetResult();
                hold.Release.TrySetResult();
            }
        }

        public Task<Plan> PreviewFirstSyncAsync(string pairId, IProgress<RunProgress> p, CancellationToken ct) =>
            Task.FromResult(new Plan(pairId, 1, [], []));

        public Task<RunReport> ApplyAsync(string pairId, Plan approved, IProgress<RunProgress> p, CancellationToken ct) =>
            IncrementalAsync(pairId, p, ct);

        public async Task<RunReport> IncrementalAsync(string pairId, IProgress<RunProgress> p, CancellationToken ct)
        {
            var hold = Hold(pairId);
            hold.Entered.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopped.Token);
            try
            {
                if (string.Equals(pairId, _typedDamageFor, StringComparison.Ordinal))
                {
                    await hold.TypedDamageSignal.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                    return new RunReport(pairId, TypedStateDamaged, Done: 6, Failed: 0, Held: 0, Uploaded: 2,
                        Downloaded: 1, Deleted: 3, Moved: 0, KeptBoth: 0, Failures: []);
                }
                if (string.Equals(pairId, _damageFor, StringComparison.Ordinal))
                {
                    await hold.DamageSignal.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                    throw _damage;
                }
                await hold.Release.Task.WaitAsync(linked.Token).ConfigureAwait(false);
                return new RunReport(pairId, RunOutcomes.Complete, 0, 0, 0, 0, 0, 0, 0, 0, []);
            }
            catch (OperationCanceledException)
            {
                hold.Cancelled.TrySetResult();
                throw;
            }
        }

        public Task StopAsync()
        {
            _stopped.Cancel();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _stopped.Cancel();
            _stopped.Dispose();
        }

        internal sealed class PairHold
        {
            internal TaskCompletionSource Entered { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Release { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource DamageSignal { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource TypedDamageSignal { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Cancelled { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
