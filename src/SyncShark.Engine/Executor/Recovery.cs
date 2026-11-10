using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Owns pending settlement, recovery choices and discovery preparation behind the Syncer.</summary>
internal sealed class Recovery(ExecutorContext context, IRecoveryExecutionBoundary boundary, FolderDeleteRunner folders)
{
    private string PairId => context.Pair.PairId;

    internal Task ReconcileReceiptsAsync(CancellationToken ct, bool progressOnly = false,
        Func<string, CancellationToken, Task>? finish = null) =>
        folders.ReconcileFolderRemovalsAsync(ct, progressOnly, finish);

    internal async Task<OutstandingWork> SettleOutstandingAsync(CancellationToken ct)
    {
        context.Events(RunEvent.Stage(TextKey.StagePending));
        long settled = 0;
        var firstRun = !context.Store.FirstRepairDone(PairId);
        var recoveredPending = false;
        var pending = context.Store.Pending(PairId);
        if (pending is not NoPending)
        {
            var recovery = await ResolvePendingAsync(pending, RecoveryChoice.Check, ct);
            recoveredPending = recovery.Outcome is RemovalOutcome.Published or RemovalOutcome.Removed;
            settled++;
        }
        await ReconcileReceiptsAsync(ct);
        foreach (var operation in context.Store.ActiveFolderRemovals(PairId, ct))
        {
            context.Events(RunEvent.Stage(TextKey.StagePending, count: settled, inProgress: true,
                path: operation.RootEntry.Path));
            boundary.RequireCurrent();
            await folders.ResumeAsync(operation, null, ct);
            settled++;
            context.Events(RunEvent.Stage(TextKey.StagePending, count: settled, inProgress: true,
                path: operation.RootEntry.Path));
        }
        var needsDiscovery = firstRun || await NeedsDiscoveryAsync(ct);
        context.Events(RunEvent.Stage(TextKey.StagePending, ended: true, count: settled));
        return new OutstandingWork(firstRun, recoveredPending, needsDiscovery);
    }

    internal async Task<RemovalResult> RecoverAsync(RecoveryTarget target, RecoveryChoice choice, CancellationToken ct)
    {
        var pending = context.Store.Pending(PairId);
        if (pending is CurrentPending current && (current.Value.OperationId != target.OperationId ||
                current.Value.Generation != target.OperationGeneration) ||
            pending is LegacyPending legacy && (legacy.RawToken != target.OperationId || target.OperationGeneration != 0))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        if (pending is NoPending)
        {
            var operation = context.Store.FolderRemoval(PairId, target.OperationId);
            if (operation.Generation != target.OperationGeneration)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            if (choice != RecoveryChoice.Check)
                return await ResolveFolderChoiceAsync(operation, choice, target.Item, target.ItemGeneration, ct)
                    .ConfigureAwait(false);
            if (target.Item is not null) throw new ArgumentException(null, nameof(target.Item));
            boundary.RequireCurrent();
            await folders.ResumeAsync(operation, null, ct).ConfigureAwait(false);
            var result = context.Store.FolderRemoval(PairId, target.OperationId);
            return new RemovalResult(result.Phase switch
            {
                FolderRemovalPhase.Complete => RemovalOutcome.Removed,
                FolderRemovalPhase.Unknown => RemovalOutcome.Unknown,
                _ => RemovalOutcome.Retained
            }, target.OperationId, null, result.Cause);
        }
        if (target.Item is not null) throw new ArgumentException(null, nameof(target.Item));
        return await ResolvePendingAsync(pending, choice, ct).ConfigureAwait(false);
    }

    internal static IEnumerable<FolderRemovalItem> Members(IStateStore store, string pairId, string operationId, int pageSize)
    {
        var after = long.MinValue;
        while (true)
        {
            var page = store.FolderRemovalItems(pairId, operationId, after, pageSize).ToArray();
            if (page.Length == 0) yield break;
            foreach (var member in page) yield return member;
            after = page[^1].Ordinal;
        }
    }

    // One choice: both branches read and settle the same operation under one store boundary.
    private Task<RemovalResult> ResolveFolderChoiceAsync(FolderRemovalState operation,
        RecoveryChoice choice, long? ordinal, long? itemGeneration, CancellationToken ct)
    {
        var selected = ordinal is null ? null : ReadMember();
        return selected is null
            ? boundary.ExecuteAsync(operation, ExecuteAsync, ct)
            : boundary.ExecuteAsync(operation, selected, ExecuteAsync, ct);

        FolderRemovalItem ReadMember()
        {
            var item = context.Store.FolderRemovalItems(PairId, operation.OperationId,
                checked(ordinal!.Value - 1), StateSchema.OneRow).SingleOrDefault();
            if (item is null || item.Ordinal != ordinal || item.Generation != itemGeneration)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            return item;
        }

        async Task<RemovalResult> ExecuteAsync(IAdmittedExecution execution, CancellationToken token)
        {
            var store = context.Store;
            var pairId = PairId;
            if (ordinal is null)
            {
                if (choice != RecoveryChoice.AcceptCurrent) throw new ArgumentException(null, nameof(choice));
                var (disposition, canAcknowledge) = FolderRetirement.Evaluate(
                    Members(store, pairId, operation.OperationId, context.Settings.StoragePageSize));
                if (!canAcknowledge)
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
                store.RetireFolderRemoval(pairId, operation.OperationId, operation.Generation, disposition, StateChange.Empty);
                return new RemovalResult(RemovalOutcome.Retained, operation.OperationId, null, operation.Cause);
            }
            var item = ReadMember();
            if (!FolderRetirement.ItemChoices(item).Contains(choice))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            if (item.Stage is { } stage)
            {
                var armed = item.BeginTerminalDiscard() with { Generation = checked(item.Generation + 1) };
                store.SettleFolderRemovalItem(pairId, operation.OperationId, item.Ordinal, item.Generation, armed, StateChange.Empty);
                item = armed;
                await context.Recovery.DiscardRecoveryStageAsync(stage, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            else if (item.StageAllocation is { } allocation)
            {
                if (await context.Recovery.InspectRecoveryAllocationAsync(allocation, token).ConfigureAwait(false) !=
                    ILocalRecovery.AllocationOccupancy.Absent)
                    return new RemovalResult(RemovalOutcome.Unknown, operation.OperationId, null, item.Cause);
            }
            var settled = item.SettledRecovery() with { Generation = checked(item.Generation + 1) };
            store.SettleFolderRemovalItem(pairId, operation.OperationId, item.Ordinal, item.Generation, settled, StateChange.Empty);
            return new RemovalResult(RemovalOutcome.Retained, operation.OperationId, null, item.Cause);
        }
    }

    internal async Task<RemovalResult> ResolvePendingAsync(PendingRead pending, RecoveryChoice choice, CancellationToken ct)
    {
        if (pending is NoPending) return new RemovalResult(RemovalOutcome.NoEffect, null, null, null);
        await using var turn = await context.Runtime.EnterAsync(ct);
        var result = await context.Recovery.ResolvePendingAsync(pending, choice, ct);
        if (pending is LegacyPending legacy)
        {
            if (result is { Outcome: RemovalOutcome.NoEffect, Cause: null })
                context.Store.SettleLegacyPending(PairId, legacy.RawToken, StateChange.Empty);
            return result;
        }
        if (context.Store.Pending(PairId) is not CurrentPending current ||
            pending is not CurrentPending original || current.Value.OperationId != original.Value.OperationId)
            throw new ChangedUnderneathException(context.Pair.LocalRoot);
        var value = current.Value;
        var change = StateChange.Empty;
        if (result is { Outcome: RemovalOutcome.Published, Final: { } final } && value.Source is { } source)
            change = change with { Record = [SyncedEntries.From(final, source)],
                Close = [(final.Path, IssueClosure.AfterEffect), (source.Path, IssueClosure.AfterEffect)],
                FirstRunAcknowledged = [new IssueItem(Side.Local, value.Losing.Expected.Path, value.Losing.Expected.Id)] };
        else if (result.Outcome == RemovalOutcome.Removed)
            change = change with { ForgetSelf = [(Side.Local, PathNames.Key(value.Path))],
                FirstRunAcknowledged = [new IssueItem(Side.Local, value.Losing.Expected.Path, value.Losing.Expected.Id)] };
        else if (result is not { Outcome: RemovalOutcome.NoEffect, Cause: null } &&
            !(result.Outcome == RemovalOutcome.WrongRestored && value.Stage is null)) return result;
        context.Store.SettlePending(PairId, value, change);
        return result;
    }

    private async Task<bool> NeedsDiscoveryAsync(CancellationToken ct)
    {
        var subjects = new Dictionary<string, IssueSubject>(PathNames.Comparer);
        foreach (var issue in context.Store.Issues(PairId, [IssueKind.Failure]))
        {
            if (issue.Subject.FirstRunDeletion is not (IssueSubject.FirstRunDeletionKind.Item or
                IssueSubject.FirstRunDeletionKind.UnscannedSubtree)) continue;
            subjects.Clear();
            IssuePolicy.AddFirstRunSubject(subjects, issue);
            foreach (var item in issue.Subject.Items)
            {
                if (item.Side != Side.Local) continue;
                context.Events(RunEvent.Stage(TextKey.StagePending, inProgress: true, path: item.Path));
                var local = await context.Local.ReadOneAsync(item.Path, ct);
                if (local is null || context.Scope.Excludes(local.Path, null, local.IsFolder)) continue;
                var row = new Row(PathNames.Key(local.Path), local, null, null, [], null, null!);
                if (IssuePolicy.FirstRunKind(row, subjects, false) != IssueSubject.FirstRunDeletionKind.None)
                    return true;
            }
        }
        return false;
    }
}
