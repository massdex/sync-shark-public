using Massdex.SyncShark.Engine;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Delegates a store, counting scanned rows and offering narrow hooks to one scan-pass case.</summary>
internal sealed class HookStore(IStateStore inner) : ForwardingStateStore(inner)
{
    public Action? BeforeMarkRepairLogFailed { get; init; }
    public override void MarkRepairLogFailed(string pairId, string runId)
    {
        BeforeMarkRepairLogFailed?.Invoke();
        base.MarkRepairLogFailed(pairId, runId);
    }

    public Action? BeforeFinishRepair { get; init; }
    public Action? AfterFinishRepair { get; init; }
    public override bool FinishRepair(string pairId, RepairSummary summary, long expectedGeneration, Guid scanToken,
        LocalCatchUp local, DropboxCatchUp dropbox)
    {
        BeforeFinishRepair?.Invoke();
        var finished = base.FinishRepair(pairId, summary, expectedGeneration, scanToken, local, dropbox);
        AfterFinishRepair?.Invoke();
        return finished;
    }

    // The host item-retry checks: AutoSync reads live refusal once per opportunity before its dispatch, and asks
    // for folder-job progress once after each dispatch that returns; Monitor reads the retry rows on each pass.
    public Action? OnReadLiveRefusal { get; init; }
    public override LiveRefusal ReadLiveRefusal(string pairId)
    {
        OnReadLiveRefusal?.Invoke();
        return base.ReadLiveRefusal(pairId);
    }

    public Action? OnFolderJobProgress { get; init; }
    public override bool HasFolderJobProgress(string pairId, CancellationToken ct)
    {
        var pending = base.HasFolderJobProgress(pairId, ct);
        OnFolderJobProgress?.Invoke();
        return pending;
    }

    public Action? BeforeBeginSelection { get; init; }
    public override bool BeginSelection(WorkSelection selection)
    {
        BeforeBeginSelection?.Invoke();
        return base.BeginSelection(selection);
    }

    public Action? BeforeSelectWork { get; init; }
    public override WorkSelection? SelectWork(string pairId, long expectedGeneration, PathScope scope)
    {
        BeforeSelectWork?.Invoke();
        return base.SelectWork(pairId, expectedGeneration, scope);
    }

    // Unreviewed singular retry reads; in the A5 item-only fixture only a Monitor prerequisite wake makes one.
    private int _retryReads;
    public int RetryReads => Volatile.Read(ref _retryReads);
    public override FailureRetryOwner.State? ReadPrerequisiteRetry(string pairId, Issue? expected = null)
    {
        if (expected is null) Interlocked.Increment(ref _retryReads);
        return base.ReadPrerequisiteRetry(pairId, expected);
    }

    public Action? AfterTemporaryItem { get; init; }
    public override FailureRetryOwner.State? RecordTemporaryItem(string pairId, long expectedGeneration, long sourceEpoch,
        WorkSelection? selection, (Side Side, string Path, string? Id)? successor, FailureRetryOwner.State? expected,
        FailureRetryOwner.State? next)
    {
        var committed = base.RecordTemporaryItem(pairId, expectedGeneration, sourceEpoch, selection, successor,
            expected, next);
        AfterTemporaryItem?.Invoke();
        return committed;
    }

    public Action? AfterPrerequisiteRetries { get; init; }
    public override IEnumerable<(long PairRow, string PairId, long Generation, FailureRetryOwner.State State)>
        ReadPrerequisiteRetries(long? afterTicks, long? afterPairRow, int pageSize)
    {
        if (AfterPrerequisiteRetries is null) return base.ReadPrerequisiteRetries(afterTicks, afterPairRow, pageSize);
        var rows = base.ReadPrerequisiteRetries(afterTicks, afterPairRow, pageSize).ToArray();
        AfterPrerequisiteRetries();
        return rows;
    }

    public Action<string, long, DriveEvidence>? OnMergeDrive { get; init; }
    public override void MergeDriveEvidence(string pairId, long expectedGeneration, DriveEvidence acquired)
    {
        OnMergeDrive?.Invoke(pairId, expectedGeneration, acquired);
        base.MergeDriveEvidence(pairId, expectedGeneration, acquired);
    }

    public Func<Action<long>?, Action<long>?>? WrapRestored { get; init; }
    public override string? RestoreDeletedHistory(string pairId, Guid scanToken, Action<long>? restored = null) =>
        base.RestoreDeletedHistory(pairId, scanToken, WrapRestored is null ? restored : WrapRestored(restored));

    public bool DisposeInner { get; init; } = true;
    public override void Dispose() { if (DisposeInner) Inner.Dispose(); }

    public int Rows { get; private set; }
    public Action? OnScanRows { get; init; }
    public Func<Side, IScanSink, IScanSink>? WrapSink { get; init; }
    public Action<Side>? BeforeOpenScan { get; init; }
    public Action<PairConfig>? OnSavePair { get; init; }
    public Action<int>? OnRow { get; init; }
    public Func<string, StateChange, Task>? Record { get; init; }
    public int GateReads { get; private set; }
    public List<IReadOnlySet<long>?> HoldSnapshots { get; } = [];
    public List<IReadOnlyList<IssueKind>?> IssueFilters { get; } = [];
    public Dictionary<string, int> PendingReads { get; } = new(StringComparer.Ordinal);

    public override void SavePair(PairConfig pair)
    {
        OnSavePair?.Invoke(pair);
        base.SavePair(pair);
    }

    public override Task CommitRecordAsync(string pairId, StateChange change, Action<Issue>? committedIssue, Action<Issue, bool>? settledIssue = null) =>
        Record?.Invoke(pairId, change) ?? base.CommitRecordAsync(pairId, change, committedIssue, settledIssue);

    public Action<StateChange>? BeforeCommitAsync { get; init; }
    public override Task CommitAsync(string pairId, StateChange change, Action<Issue, bool>? settledIssue = null)
    {
        BeforeCommitAsync?.Invoke(change);
        return base.CommitAsync(pairId, change, settledIssue);
    }

    public Action<StateChange>? OnCommit { get; init; }
    public override void Commit(string pairId, StateChange change, Action<Issue>? committedIssue = null, Action<Issue, bool>? settledIssue = null)
    {
        OnCommit?.Invoke(change);
        base.Commit(pairId, change, committedIssue, settledIssue);
    }

    public override PendingRead Pending(string pairId)
    {
        PendingReads[pairId] = PendingReads.GetValueOrDefault(pairId) + 1;
        return base.Pending(pairId);
    }

    public override IReadOnlyList<Issue> Issues(string pairId, IReadOnlyList<IssueKind>? kinds = null)
    {
        if (kinds is { Count: 1 } && kinds[0] == IssueKind.DriveChange) GateReads++;
        else IssueFilters.Add(kinds);
        return base.Issues(pairId, kinds);
    }

    public override IssueKind? HeldKind(string pairId, Row row, IReadOnlySet<long>? exceptIssues)
    {
        HoldSnapshots.Add(exceptIssues);
        return base.HeldKind(pairId, row, exceptIssues);
    }

    public override bool HeldCovers(string pairId, string key, IReadOnlySet<long>? exceptIssues)
    {
        HoldSnapshots.Add(exceptIssues);
        return base.HeldCovers(pairId, key, exceptIssues);
    }

    public override bool HasOpenIssue(string pairId, IssueKind kind)
    {
        GateReads++;
        return base.HasOpenIssue(pairId, kind);
    }

    public override IReadOnlyDictionary<IssueKind, int> IssueCounts(string pairId, bool? ignored = null)
    {
        GateReads++;
        return base.IssueCounts(pairId, ignored);
    }

    public List<bool> DiscoveryStarts { get; } = [];
    public int ScanPasses { get; private set; }
    public override Guid BeginScan(string pairId, Guid scanId, long expectedGeneration, bool includeDeleted, string? scope = null)
    {
        DiscoveryStarts.Add(includeDeleted);
        return base.BeginScan(pairId, scanId, expectedGeneration, includeDeleted, scope);
    }

    public override IScanSink OpenScan(string pairId, Guid scanId, Side side, int batchRows)
    {
        BeforeOpenScan?.Invoke(side);
        var sink = base.OpenScan(pairId, scanId, side, batchRows);
        return WrapSink is { } wrap ? wrap(side, sink) : sink;
    }

    public override IEnumerable<Row> ScanRows(string pairId, Guid scanId, Func<Row, RowContext> context)
    {
        ScanPasses++;
        OnScanRows?.Invoke();
        foreach (var row in base.ScanRows(pairId, scanId, context))
        {
            Rows++;
            OnRow?.Invoke(Rows);
            yield return row;
        }
    }

    public int DiscoveryExistenceReads { get; private set; }
    public override bool HasDeletedNames(string pairId, Guid scanId)
    {
        DiscoveryExistenceReads++;
        return base.HasDeletedNames(pairId, scanId);
    }
}
