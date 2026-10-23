using System.Reflection;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Adapter-level checks for the core v2 Recovery surface: the store-only default of <c>recovery list</c>, the
/// opt-in live classification, the queued GUI gate, and the restore notice choices. Isolated stores and fake
/// endpoints only; no provider, real user data or executable is touched.
/// </summary>
internal static class RecoveryAdapterChecks
{
    private static V2Settings Settings => V2Settings.Default with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };

    public static async Task RunAsync()
    {
        RecoveryRestoreChoiceRenderCheck();
        await RecoveryListStoreOnlyAsync().ConfigureAwait(false);
        await RecoveryHostQueuedGateAsync().ConfigureAwait(false);
    }

    private static void RecoveryRestoreChoiceRenderCheck()
    {
        var pending = new Notice("p1", 0, NoticeKind.Info, "file.txt", "existing destination left unchanged",
            SyncRun.RecoveryRestorePending,
            ConflictOperation: new ConflictNoticeOperation("recovery-1", "file.txt", [], []));
        Require(NoticeOptions.Options(pending).Select(option => option.Choice).SequenceEqual(
                new[] { NoticeChoices.RetryNow, NoticeChoices.RestoreAsCopy, NoticeChoices.Ignore, NoticeChoices.Dismiss }),
            "actionless restore offers Retry, Restore as a copy, Ignore and Dismiss with no durable hold");
        var unavailable = pending with { ConflictOperation = new ConflictNoticeOperation("recovery-1", null, [], []) };
        Require(NoticeOptions.Options(unavailable).Select(option => option.Choice).SequenceEqual(
                new[] { NoticeChoices.RetryNow, NoticeChoices.Ignore, NoticeChoices.Dismiss }),
            "an actionless failure with no destination does not offer Restore as a copy");
        var intent = new SyncAction(ShortId.Make("rrs", "p1", "recovery-1"), 0, ActionKind.CopyNew, Side.A,
            "file.txt", null, new ItemState("file.txt", ItemKind.File, "object", "rev", "hash", 1,
                DateTimeOffset.UnixEpoch, ItemFlags.None), null, false, null) { SuppressReconciledBaseline = true };
        var frozen = pending with { ConflictOperation = new ConflictNoticeOperation("recovery-1", "file.txt",
            [intent], []) };
        Require(NoticeOptions.Options(frozen).Select(option => option.Choice).SequenceEqual(
                new[] { NoticeChoices.RetryNow, NoticeChoices.Ignore }),
            "a frozen restore intent keeps only Retry and Ignore, so Dismiss never abandons written bytes");
        Require(NoticeChoices.IsRetryChoice(NoticeChoices.RestoreAsCopy),
            "Restore as a copy routes through the retry-notice choice path");
    }

    private static async Task RecoveryListStoreOnlyAsync()
    {
        var root = LocalChecks.FreshRoot("recovery-list-store-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, TimeProvider.System);
        var pair = Pair();
        store.SavePair(pair);
        var bytes = Encoding.UTF8.GetBytes("retained bytes");
        var source = new ItemState("file.txt", ItemKind.File, "archived-entity", "revision-1",
            DropboxContentHash.Compute(bytes), bytes.Length, DateTimeOffset.UtcNow.AddDays(-1), ItemFlags.None);
        var removed = new SyncAction("retained-action", 0, ActionKind.Delete, Side.A, source.Path, null, null,
            source, true, "old-key");
        var generation = store.BeginGeneration(pair.A.RootId, "fixture");
        store.CommitResult(pair.PairId, generation, removed, new ActionResult(removed.ActionId, Outcome.Done, null,
            null, new RecoveryRef(Side.A, "fixture-archive", source.VersionId, source.Hash!, source.Size), null));
        var recovery = store.RecoveryPage(pair.PairId, new RecoveryQuery()).Single();
        store.AddNotices([new Notice(pair.PairId, 0, NoticeKind.UserDecision, source.Path, "pending restore",
            SyncRun.RecoveryRestorePending,
            NoticeId: ShortId.Make("rrs", pair.PairId, recovery.RecoveryId),
            ConflictOperation: new ConflictNoticeOperation(recovery.RecoveryId, null, [], []))]);

        var endpointA = new FakeEndpoint(Side.A, pair.A.RootId);
        var writer = new ListRecoveryWriter(endpointA, source);
        var provider = new CountingProvider(new FakeEndpointProvider()
            .Add(pair.A, endpointA)
            .Add(pair.B, new FakeEndpoint(Side.B, pair.B.RootId))
            .AddWriter(pair.A, writer));
        var run = new SyncRun(store, new Executor(store, Settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage), provider, Settings,
            TimeProvider.System);
        var runtime = new V2CliRuntime(null, store, run, new EndpointRootProbe(provider), root);

        var storeOnly = (SyncCliRecoveryPageData)await runtime.LoadRecoveryCommandAsync(
            SyncCliRegistry.Parse(["--cli", "recovery", "list", "--pair", pair.PairId]),
            CancellationToken.None).ConfigureAwait(false);
        Require(storeOnly.Items.Count == 1 && storeOnly.Items[0].Classification == "NOTICE-OPEN" &&
                storeOnly.Items[0].Destination is null && provider.Prepares == 0 && provider.ReadPrepares == 0 &&
                writer.Observes == 0 && writer.Describes == 0,
            "the default recovery list is store-only: no provider request, no observe and no live describe");

        // A held engine gate proves the default takes no run gate: a bounded timeout makes a regression fail,
        // never hang the suite.
        var engineGate = (PairRunAdmission)typeof(SyncRun)
            .GetField("_admission", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(run)!;
        var admission = await engineGate.EnterAsync(null, CancellationToken.None).ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var whileBusy = (SyncCliRecoveryPageData)await runtime.LoadRecoveryCommandAsync(
                SyncCliRegistry.Parse(["--cli", "recovery", "list", "--pair", pair.PairId]),
                timeout.Token).ConfigureAwait(false);
            Require(whileBusy.Items.Count == 1,
                "the store-only recovery list never waits for the engine run gate");
        }
        finally
        {
            admission.Dispose();
        }

        var verified = (SyncCliRecoveryPageData)await runtime.LoadRecoveryCommandAsync(
            SyncCliRegistry.Parse(["--cli", "recovery", "list", "--pair", pair.PairId, "--verify"]),
            CancellationToken.None).ConfigureAwait(false);
        Require(verified.Items.Count == 1 && verified.Items[0].Classification == "RESTORE" &&
                provider.ReadPrepares == 1 && writer.Describes == 1 && writer.Observes >= 1,
            "--verify enables the live classification by observing the retained revision and its destination");
    }

    private static async Task RecoveryHostQueuedGateAsync()
    {
        var root = LocalChecks.FreshRoot("recovery-host-gate-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(Path.Combine(root, "state"), Settings, TimeProvider.System);
        var executor = new Executor(store, Settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage);
        var run = new SyncRun(store, executor, new FakeEndpointProvider(), Settings, TimeProvider.System);
        var host = new V2SyncHost(store, run, new ObservedExecutor(executor), null!, Settings, TimeProvider.System,
            SqliteStateStore.DatabasePath(Path.Combine(root, "state")));
        var gate = (PairRunAdmission)typeof(SyncRun)
            .GetField("_admission", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(run)!;
        var admission = await gate.EnterAsync("missing-pair", CancellationToken.None).ConfigureAwait(false);
        try
        {
            var item = new SyncRecoveryCatalogItem("missing-recovery", "missing-pair", "pair",
                SyncRecoverySource.LocalArchive, "file.txt", "Local", "C:\\x", 1, DateTimeOffset.UnixEpoch,
                "30 days", "retained", "hash", null, null, "{}", true, "file", false, SyncProviders.Local, "A");
            using var restoreTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var restore = host.RestoreRecoveryAsync(item, restoreAsCopy: false,
                cancellationToken: restoreTimeout.Token);
            // The method reaches the gate synchronously; the held gate can only leave it pending, never refused.
            await Task.Yield();
            Require(!restore.IsCompleted,
                "a GUI restore queues behind the active run instead of being refused");
            using var cancelled = new CancellationTokenSource();
            var withdrawn = host.RestoreRecoveryAsync(item, restoreAsCopy: false, cancellationToken: cancelled.Token);
            cancelled.Cancel();
            await Expect<OperationCanceledException>(() => withdrawn).ConfigureAwait(false);
            Require(!restore.IsCompleted, "cancelling a queued restore never releases its predecessor's pair lease");
            admission.Dispose();
            await Expect<InvalidDataException>(() => restore).ConfigureAwait(false);
        }
        finally
        {
            admission.Dispose();
        }
    }

    private static PairDef Pair() => new("p1", "p1",
        new RootDef("ra", SyncProviders.Local, null, "C:\\a", "id:ra"),
        new RootDef("rb", SyncProviders.Dropbox, "acc", "/b", "id:rb"), [], [], true);

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private static async Task Expect<TException>(Func<Task> operation) where TException : Exception
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (TException)
        {
            Console.WriteLine("PASS rejects via " + typeof(TException).Name);
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }

    private sealed class CountingProvider(IEndpointProvider inner) : IEndpointProvider
    {
        public int Prepares { get; private set; }
        public int ReadPrepares { get; private set; }
        public IInventorySource Inventory(RootDef root) => inner.Inventory(root);
        public IEndpointWriter Writer(RootDef root) => inner.Writer(root);
        public Task PrepareAsync(PairDef pair, CancellationToken ct) { Prepares++; return Task.CompletedTask; }
        public Task PrepareRecoveryReadAsync(PairDef pair, CancellationToken ct)
        {
            ReadPrepares++;
            return Task.CompletedTask;
        }
    }

    private sealed class ListRecoveryWriter(IEndpointWriter inner, ItemState source) : IEndpointWriter,
        IRecoveryRecordReader
    {
        public int Observes { get; private set; }
        public int Describes { get; private set; }
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct)
        {
            Observes++;
            return inner.ObserveAsync(path, ct);
        }
        public Task<Stream> OpenReadAsync(ItemState state, CancellationToken ct) => inner.OpenReadAsync(state, ct);
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct) =>
            inner.PutAsync(action, content, ct);
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => inner.MoveAsync(action, ct);
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => inner.DeleteAsync(action, ct);
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => inner.MkDirAsync(path, ct);
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) =>
            inner.PreserveAsync(victim, actionId, ct);
        public Task<ItemState> DescribeRecoveryAsync(RecoveryRecord recovery, CancellationToken ct)
        {
            Describes++;
            return Task.FromResult(source with { Path = recovery.Path });
        }
        public Task<Stream> OpenRecoveryRecordAsync(RecoveryRecord recovery, CancellationToken ct) =>
            throw new NotSupportedException("This check never opens live bytes.");
    }
}
