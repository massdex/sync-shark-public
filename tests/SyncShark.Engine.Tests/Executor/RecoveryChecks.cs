using System.Reflection;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Exercises exact recovery selections through the Syncer entry, without provider effects.</summary>
public static class RecoveryChecks
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        await FolderDeleteRunnerChecks.SettledReadyNeverSendsAsync();
        await FolderDeleteRunnerChecks.SettledUnknownRejectsLateReceiptAsync();
        await FolderDeleteRunnerChecks.SettledStateOnlyNeverForgetsAsync();
        await FolderDeleteRunnerChecks.RealRecoveryAdmissionAsync();
        await CustodyAsync();
        await FolderDeleteRunnerChecks.ReplacementPlanFailureAsync();
        using (var fixture = new Fixture())
        {
            fixture.Store.DiscoverOperation = true;
            try { await fixture.Syncer.SettleOutstandingAsync(default); }
            catch (NotSupportedException) { }
            Check.Require(fixture.Store.SubmissionReads == 2,
                "Outstanding settlement performs receipt reconciliation before the separate resume join.");
        }
        foreach (var staleOperation in new[] { false, true })
        {
            using var fixture = new Fixture();
            var target = fixture.Target with
            {
                OperationGeneration = staleOperation ? fixture.Operation.Generation + 1 : fixture.Operation.Generation,
                ItemGeneration = staleOperation ? fixture.Item.Generation : fixture.Item.Generation + 1
            };
            await RefusesAsync(() => fixture.Syncer.RecoverAsync(target, RecoveryChoice.AcceptCurrent, default));
            Check.Require(fixture.Store.Writes == 0, "A stale operation or member writes nothing.");
        }
        using (var fixture = new Fixture())
        {
            await RefusesAsync(() => fixture.Syncer.RecoverAsync(fixture.Target with
                { OperationGeneration = fixture.Operation.Generation + 1, Item = null, ItemGeneration = null },
                RecoveryChoice.AcceptCurrent, default));
            Check.Require(fixture.Store.Writes == 0, "A stale whole-operation selection writes nothing.");
        }
        using (var fixture = new Fixture())
        {
            await RefusesAsync(() => fixture.Syncer.RecoverAsync(fixture.Target with { Item = 7 },
                RecoveryChoice.AcceptCurrent, default));
            Check.Require(fixture.Store.Writes == 0, "A missing ordinal never selects the following member.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Store.Items[1] = fixture.Item with { Phase = FolderRemovalItem.RecoveryPhase.Settled };
            await RefusesAsync(() => fixture.Syncer.RecoverAsync(fixture.Target, RecoveryChoice.AcceptCurrent, default));
            Check.Require(fixture.Store.Writes == 0, "A settled member refuses before a settlement write.");
        }
        foreach (var beyondOnePage in new[] { false, true })
        {
            using var fixture = new Fixture();
            // The larger operation exceeds one configured page; only the same selected member is involved.
            var count = beyondOnePage ? fixture.PageSize + fixture.Store.Items.Count : fixture.Store.Items.Count;
            for (var index = 2; index < count; index++)
                fixture.Store.Items.Add(fixture.Item with { Ordinal = index + 10 });
            Check.Require(fixture.PageSize > 0 && (!beyondOnePage || count > fixture.PageSize),
                "The growth fixture uses a positive page and a distinct larger operation.");
            var first = fixture.Store.Items[0];
            await fixture.Syncer.RecoverAsync(fixture.Target, RecoveryChoice.AcceptCurrent, default);
            Check.Require(fixture.Store.Items[0] == first && fixture.Store.Items[1].IsTerminal,
                "Two provenance members sharing a path settle only the selected ordinal.");
            Check.Require(fixture.Store.Writes == 1 && fixture.Store.Visited == 4,
                "Selected-member lookup and both admission validations visit exactly four rows regardless of operation size.");
        }
        using (var fixture = new Fixture())
        {
            fixture.Boundary.BeforeAdmission = () => fixture.Store.Operation = fixture.Operation with
                { Generation = fixture.Operation.Generation + 1 };
            await RefusesAsync(() => fixture.Syncer.RecoverAsync(fixture.Target, RecoveryChoice.AcceptCurrent, default));
            Check.Require(fixture.Store.Writes == 0, "An operation advancing before admission has no effect.");
        }
    }

    private static async Task CustodyAsync()
    {
        foreach (var cancelAfterDiscard in new[] { false, true })
        {
            using var fixture = new Fixture();
            using var cancel = new CancellationTokenSource();
            var stage = new StageProof("stage", "volume", "file", 1, "hash");
            fixture.Store.Items[1] = fixture.Item with { Stage = stage, Phase = FolderRemovalItem.RecoveryPhase.Staged };
            var other = fixture.Store.Items[0] = fixture.Store.Items[0] with
                { Stage = stage with { Path = "other-stage", FileId = "other-file" } };
            fixture.Recovery.Discard = proof =>
            {
                Check.Require(proof == stage && fixture.Item.IsTerminalDiscard && fixture.Store.Writes == 1,
                    "Discard sees only the selected stage after the terminal arm is committed.");
                fixture.Store.Order.Add("discard");
                if (cancelAfterDiscard) cancel.Cancel();
            };
            try { await fixture.Syncer.RecoverAsync(fixture.Target, RecoveryChoice.AbandonIncoming, cancel.Token); }
            catch (OperationCanceledException) when (cancelAfterDiscard) { }
            Check.Require(fixture.Store.Items[0] == other, "Another ordinal at the same path retains its stage.");
            Check.Require(fixture.Store.Order.SequenceEqual(cancelAfterDiscard ? new[] { "DiscardArmed", "discard" } :
                new[] { "DiscardArmed", "discard", "Settled" }), "Terminal custody preserves arm-discard-settle order.");
            Check.Require(cancelAfterDiscard ? fixture.Item.IsTerminalDiscard && fixture.Item.Stage == stage :
                fixture.Item.IsTerminal && fixture.Item.Stage is null, "Cancellation after disposal retains the durable arm.");
        }
        using (var fixture = new Fixture())
        {
            var staged = fixture.Store.Items[1] = fixture.Item with
                { Stage = new StageProof("stage", "volume", "file", 1, "hash"), Phase = FolderRemovalItem.RecoveryPhase.Staged };
            await RefusesAsync(() => fixture.Syncer.RecoverAsync(fixture.Target, RecoveryChoice.AcceptCurrent, default));
            Check.Require(fixture.Item == staged && fixture.Store.Writes == 0 && fixture.Recovery.Discards == 0,
                "AcceptCurrent cannot dispose a held stage.");
        }
        foreach (var occupancy in Enum.GetValues<ILocalRecovery.AllocationOccupancy>())
        {
            using var fixture = new Fixture();
            var allocated = fixture.Store.Items[1] = fixture.Item with
                { StageAllocation = new StageAllocation("operation", fixture.Item.Ordinal, "allocation", "stage"),
                    Phase = FolderRemovalItem.RecoveryPhase.AllocateArmed };
            fixture.Recovery.Occupancy = occupancy;
            var result = await fixture.Syncer.RecoverAsync(fixture.Target, RecoveryChoice.AbandonIncoming, default);
            Check.Require(occupancy == ILocalRecovery.AllocationOccupancy.Absent
                ? fixture.Item.IsTerminal && fixture.Item.StageAllocation is null && fixture.Store.Writes == 1
                : result.Outcome == RemovalOutcome.Unknown && fixture.Item == allocated && fixture.Store.Writes == 0,
                "Only a proved absent allocation settles; unknown occupants retain custody without writes.");
        }
    }

    private static async Task RefusesAsync(Func<Task> action)
    {
        try { await action(); }
        catch (LiveAdmissionStaleException error) when (error.Reason == LiveAdmissionStaleReason.SelectionChanged) { return; }
        throw new InvalidOperationException("Expected exact-selection refusal.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly InMemoryStateStore _inner = new();
        internal FolderRemovalState Operation => Store.Operation;
        internal FolderRemovalItem Item => Store.Items[1];
        internal RecoveryTarget Target => new(Operation.OperationId, Operation.Generation, Item.Ordinal, Item.Generation);
        internal RecoveryStore Store { get; }
        internal Boundary Boundary { get; }
        internal Syncer Syncer { get; }
        internal int PageSize { get; }
        internal ScriptedRecovery Recovery { get; } = new();

        internal Fixture()
        {
            var pair = new PairConfig("pair-a", "Pair A", "C:/PairA", "/PairA", "connection-a", [], [],
                FirstRunPolicy.NewestWins, true, null);
            _inner.SavePair(pair);
            var proxy = DispatchProxy.Create<IStateStore, RecoveryStore>();
            Store = (RecoveryStore)(object)proxy;
            Store.Inner = _inner;
            var root = new Entry("folder", true, 0, DateTimeOffset.UnixEpoch, "root", "", "", null);
            Store.Operation = new FolderRemovalState("operation", 0, FolderRemovalPhase.Recovering, Side.Dropbox,
                root, null, null, RowContext.Neutral, null, true, null, null);
            var item = new FolderRemovalItem("operation", 5, 0, FolderRemovalItem.ProvenanceKind.Planned,
                "folder/item", "folder/item", null, null, FolderVerdict.Survives, false,
                FolderRemovalItem.FollowUpStatus.None, false, FolderEffectKind.None,
                FolderRemovalItem.EffectStatus.None, 0, null, null, null, FolderRemovalItem.RecoveryPhase.Planned,
                null, null, null, null, null);
            Store.Items.Add(item);
            Store.Items.Add(item with { Ordinal = 9, Provenance = FolderRemovalItem.ProvenanceKind.Unexpected });
            Boundary = new Boundary(proxy, pair.PairId);
            var settings = new EngineSettings("", "", "", "", "", RecycleFallback.Fail, 1,
                FirstRunPolicy.NewestWins, 1, "", 256, ScanTestSettings.BatchRows) with { StoragePageSize = Store.Items.Count };
            PageSize = settings.StoragePageSize;
            var context = new ExecutorContext(new FakeSide(Side.Local), new FakeSide(Side.Dropbox),
                Recovery, proxy, pair, new PathScope(pair, [pair]), settings, new ReplaceTurn(),
                new FakeAdmission(), false, _ => { }, (_, _, _, _) => throw new NotSupportedException(),
                new FakeTimeProvider(), (_, _, _) => throw new NotSupportedException(),
                _ => throw new NotSupportedException(), (_, _) => throw new NotSupportedException(),
                ("account", "route", "source"), new RefusingItemAttempts(), new object(), 0);
            Syncer = new Syncer(context, new SyncPorts(Boundary, RefusingExecutionBoundary.Instance,
                RefusingExecutionBoundary.Instance, 0));
        }

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>A synthetic operation store that records rows returned and exact settlement writes.</summary>
    public class RecoveryStore : DispatchProxy
    {
        public IStateStore Inner { get; set; } = null!;
        public FolderRemovalState Operation { get; set; } = null!;
        public List<FolderRemovalItem> Items { get; } = [];
        public int Visited { get; private set; }
        public int Writes { get; private set; }
        public bool DiscoverOperation { get; set; }
        public int SubmissionReads { get; private set; }
        public List<string> Order { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod ?? throw new InvalidOperationException();
            args ??= [];
            switch (method.Name)
            {
                case nameof(IStateStore.ActiveFolderRemovals):
                    return DiscoverOperation ? new[] { Operation } : Array.Empty<FolderRemovalState>();
                case nameof(IStateStore.PendingFolderSubmissions):
                    SubmissionReads++;
                    return Array.Empty<FolderSubmission>();
                case nameof(IStateStore.FolderRemoval): return Operation;
                case nameof(IStateStore.FolderRemovalItems):
                    var rows = Items.Where(item => item.Ordinal > (long)args[2]!).OrderBy(item => item.Ordinal)
                        .Take((int)args[3]!).ToArray();
                    Visited += rows.Length;
                    return rows;
                case nameof(IStateStore.SettleFolderRemovalItem):
                    var ordinal = (long)args[2]!;
                    var index = Items.FindIndex(item => item.Ordinal == ordinal);
                    if (index < 0 || Items[index].Generation != (long)args[3]!)
                        throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
                    Items[index] = (FolderRemovalItem)args[4]!;
                    Order.Add(Items[index].Phase.ToString());
                    Writes++;
                    return null;
                default:
                    try { return method.Invoke(Inner, args); }
                    catch (TargetInvocationException error) when (error.InnerException is not null)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                        throw;
                    }
            }
        }
    }

    private sealed class ScriptedRecovery : ILocalRecovery
    {
        internal Action<StageProof>? Discard { get; set; }
        internal int Discards { get; private set; }
        internal ILocalRecovery.AllocationOccupancy Occupancy { get; set; }
        public Task DiscardRecoveryStageAsync(StageProof proof, CancellationToken ct)
        {
            Discards++;
            Discard?.Invoke(proof);
            return Task.CompletedTask;
        }
        public Task<ILocalRecovery.AllocationOccupancy> InspectRecoveryAllocationAsync(StageAllocation allocation,
            CancellationToken ct) => Task.FromResult(Occupancy);
        public Task<RemovalResult> ResolvePendingAsync(PendingRead pending, RecoveryChoice choice, CancellationToken ct) =>
            throw new NotSupportedException();
        public IReadOnlyList<(string Path, Exception Error)> RemoveStaleStages(IReadOnlySet<string> names) =>
            throw new NotSupportedException();
        public bool CanArmReplace() => throw new NotSupportedException();
        public Task<RemovalProof> ProveRemovalAsync(Entry expected, string? hash, CancellationToken ct) =>
            throw new NotSupportedException();
        public DriveEvidence Drive() => throw new NotSupportedException();
        public StageAllocation PlanRecoveryStage(string operationId, long ordinal) => throw new NotSupportedException();
        public Task<IRecoveryStage> StageRecoveryAsync(StageAllocation allocation, Entry selected, Stream content,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenRecoveryStageAsync(StageProof proof, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RefusingItemAttempts : IItemAttempts
    {
        public Task<T> ExecuteAsync<T>(object opportunity, ItemRetryCandidate? expected, IssueSubject subject,
            Func<Guid, CancellationToken, Task<T>> execute, CancellationToken ct) => throw new NotSupportedException();
        public ItemRetryCandidate ReportFailure(Guid attemptId, long sourceEpoch, string path, IssueSubject subject,
            TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending,
            ItemRetryToken? expectedEpisode) => throw new NotSupportedException();
        public void Complete(Guid attemptId, ItemRetryCandidate expected, StateChange change,
            PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) => throw new NotSupportedException();
    }

    private sealed class Boundary(IStateStore store, string pairId) : IRecoveryExecutionBoundary
    {
        internal Action? BeforeAdmission { get; set; }
        public void RequireCurrent() { }
        public Task<T> ExecuteAsync<T>(FolderRemovalState operation, FolderRemovalItem member,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            void Validate()
            {
                var current = store.FolderRemovalItems(pairId, operation.OperationId,
                    checked(member.Ordinal - 1), StateSchema.OneRow).SingleOrDefault();
                if (store.FolderRemoval(pairId, operation.OperationId).Generation != operation.Generation ||
                    current?.Ordinal != member.Ordinal || current.Generation != member.Generation)
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            }
            Validate();
            BeforeAdmission?.Invoke();
            Validate();
            return execute(new Lease(), ct);
        }
        public Task<T> ExecuteAsync<T>(IReadOnlyList<FolderRemovalState> operations,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) => throw new NotSupportedException();
        public Task<T> ExecuteAsync<T>(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct) => throw new NotSupportedException();
        public Task ExecuteAsync(FolderRemovalState operation,
            Func<IAdmittedExecution, CancellationToken, Task> execute, CancellationToken ct) => throw new NotSupportedException();
        private sealed class Lease : IAdmittedExecution
        {
            public void Require(string pairId, long generation, IReadOnlyList<string> requiredLockKeys) { }
            public void RequireScopes(string pairId, long generation, IReadOnlyList<PathLockScope> scopes) { }
            public ValueTask ReleaseAdmissionAsync() => default;
            public ValueTask DisposeAsync() => default;
        }
    }
}
