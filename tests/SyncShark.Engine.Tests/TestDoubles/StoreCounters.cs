using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Counts store calls, Record completions, reader connections and rows, and observes scan sinks and
/// folder-plan phases while delegating to the underlying store. Every other store command is forwarded unchanged
/// and counted once as a <see cref="Commands"/> call.</summary>
internal sealed class StoreCounters(IStateStore inner) : IStateStore
{
    private long _commands;
    private long _commits;
    private long _readConnections;
    private long _rowsRead;
    private long _recordCalls;
    private long _recordCommitted;
    private long _recordFailures;
    private long _committedGroups;
    private long _filteredIssues;
    private long _scanAccepted;
    private long _scanApplied;
    private long _scanCompleted;
    private long _scanAborted;
    private long _scanFailed;
    private long _planStatements;
    private int _planIncomplete;
    private readonly object _recordLock = new();
    private readonly List<Task> _records = [];
    private readonly ConcurrentDictionary<string, PhaseCounts> _planPhases = new();
    private readonly ConcurrentDictionary<string, long> _memberCalls = new();
    public IReadOnlyDictionary<string, long> MemberCalls => _memberCalls;

    public long RecordCommitCalls => Interlocked.Read(ref _recordCalls);
    public long RecordCommittedMembers => Interlocked.Read(ref _recordCommitted);
    public long RecordCommitFailures => Interlocked.Read(ref _recordFailures);

    /// <summary>Derived completed-task groups; notifications may split or coalesce transactions, not count them.</summary>
    public long ObservedCommittedGroups => Interlocked.Read(ref _committedGroups);
    public long FilteredIssuesCalls => Interlocked.Read(ref _filteredIssues);

    /// <summary>Successful Add calls before termination; the void sink cannot expose actual queue acceptance.</summary>
    public long ScanAccepted => Interlocked.Read(ref _scanAccepted);

    /// <summary>Accepted-call totals only for successfully completed, non-aborted sinks; abort work is unknown.</summary>
    public long ScanAppliedOnCompletion => Interlocked.Read(ref _scanApplied);
    public long ScanCompleted => Interlocked.Read(ref _scanCompleted);
    public long ScanAborted => Interlocked.Read(ref _scanAborted);
    public long ScanFailedCompletions => Interlocked.Read(ref _scanFailed);

    public IReadOnlyDictionary<string, PhaseCounts> PlanPhases => _planPhases;

    /// <summary>Derived successful data-statement recipe only; excludes setup, transactions and durable work.</summary>
    public long DerivedPlanDataStatements => Interlocked.Read(ref _planStatements);

    /// <summary>True if failures, partial cursors or unobservable probes prevent a complete statement total.</summary>
    public bool PlanStatementCountIncomplete => Volatile.Read(ref _planIncomplete) != 0;

    /// <summary>Every member called, one per top-level store command.</summary>
    public long Commands => Interlocked.Read(ref _commands);

    /// <summary>Every Commit, CommitAsync or CommitRecordAsync attempt, including calls taking no transaction.</summary>
    public long CommitCalls => Interlocked.Read(ref _commits);

    /// <summary>Every call that opens a separate read-only connection (rows and renames).</summary>
    public long ReadConnections => Interlocked.Read(ref _readConnections);

    /// <summary>Every row a <see cref="ScanRows"/> enumeration yielded.</summary>
    public long RowsRead => Interlocked.Read(ref _rowsRead);

    /// <summary>Clears every counter between drained runs; never call while a sink, plan or Record is active.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _commands, 0);
        Interlocked.Exchange(ref _commits, 0);
        Interlocked.Exchange(ref _readConnections, 0);
        Interlocked.Exchange(ref _rowsRead, 0);
        Interlocked.Exchange(ref _recordCalls, 0);
        Interlocked.Exchange(ref _recordCommitted, 0);
        Interlocked.Exchange(ref _recordFailures, 0);
        Interlocked.Exchange(ref _committedGroups, 0);
        Interlocked.Exchange(ref _filteredIssues, 0);
        Interlocked.Exchange(ref _scanAccepted, 0);
        Interlocked.Exchange(ref _scanApplied, 0);
        Interlocked.Exchange(ref _scanCompleted, 0);
        Interlocked.Exchange(ref _scanAborted, 0);
        Interlocked.Exchange(ref _scanFailed, 0);
        Interlocked.Exchange(ref _planStatements, 0);
        Volatile.Write(ref _planIncomplete, 0);
        _planPhases.Clear();
        _memberCalls.Clear();
    }

    public IReadOnlyList<PairConfig> Pairs()
    {
        Command();
        return inner.Pairs();
    }

    public PairConfig? Pair(string pairId)
    {
        Command();
        return inner.Pair(pairId);
    }

    public void SavePair(PairConfig pair)
    {
        Command();
        inner.SavePair(pair);
    }

    public void SavePairDecision(PairConfig expected, long expectedGeneration, PairConfig next,
        Issue? expectedIssue, bool resetHistory, string? reincludedKey)
    {
        Command();
        inner.SavePairDecision(expected, expectedGeneration, next, expectedIssue, resetHistory, reincludedKey);
    }

    public (PairConfig Pair, long Generation)? ReadPairExecution(string pairId)
    {
        Command();
        return inner.ReadPairExecution(pairId);
    }

    public ItemRetryCandidate? ReadItemRetryForSubject(string pairId, long expectedGeneration,
        string path, IssueSubject subject, bool registeredWithin = false)
    {
        Command();
        return inner.ReadItemRetryForSubject(pairId, expectedGeneration, path, subject, registeredWithin);
    }

    public ItemRetryCandidate? ReadReplacedItemRetry(string pairId, long expectedGeneration, string path, string listedId)
    {
        Command();
        return inner.ReadReplacedItemRetry(pairId, expectedGeneration, path, listedId);
    }

    public void RetireReplacedItemRetry(ItemRetryCandidate replaced, string path, string freshLocalId)
    {
        Command();
        inner.RetireReplacedItemRetry(replaced, path, freshLocalId);
    }

    public long ReadWorkWatermark()
    {
        Command();
        return inner.ReadWorkWatermark();
    }

    public void MergeDriveEvidence(string pairId, long expectedGeneration, DriveEvidence acquired)
    {
        Command();
        inner.MergeDriveEvidence(pairId, expectedGeneration, acquired);
    }

    public void ReconcileRootIdentity(string pairId, long expectedGeneration, RootBinding? expectedBinding,
        Issue? expectedIssue, StateChange issueOnlyChange)
    {
        Command();
        inner.ReconcileRootIdentity(pairId, expectedGeneration, expectedBinding, expectedIssue, issueOnlyChange);
    }

    public void RemovePair(string pairId)
    {
        Command();
        inner.RemovePair(pairId);
    }

    public bool FirstRepairDone(string pairId)
    {
        Command();
        return inner.FirstRepairDone(pairId);
    }

    public RepairSummary? LastRepair(string pairId)
    {
        Command();
        return inner.LastRepair(pairId);
    }

    public void MarkRepairLogFailed(string pairId, string runId)
    {
        Command();
        inner.MarkRepairLogFailed(pairId, runId);
    }

    public void FinishRepair(string pairId, RepairSummary summary)
    {
        Command();
        inner.FinishRepair(pairId, summary);
    }

    public bool FinishRepair(string pairId, RepairSummary summary, long expectedGeneration, Guid scanToken,
        LocalCatchUp local, DropboxCatchUp dropbox, out Exception? secondaryFailure)
    {
        Command();
        return inner.FinishRepair(pairId, summary, expectedGeneration, scanToken, local, dropbox, out secondaryFailure);
    }

    internal IStateStore FixtureInner => inner;
    public Task EndScanAsync(string pairId, Guid scanId, int pageSize) => inner.EndScanAsync(pairId, scanId, pageSize);
    public bool CloseScanIssue(string pairId, Guid scanId, Issue expected) => inner.CloseScanIssue(pairId, scanId, expected);
    public ScanRecord ReadScan(string pairId, Guid scanToken)
    {
        Command();
        return inner.ReadScan(pairId, scanToken);
    }

    public void ForgetAllSynced(string pairId)
    {
        Command();
        inner.ForgetAllSynced(pairId);
    }

    public int? LimiterLevel(string connectionId)
    {
        Command();
        return inner.LimiterLevel(connectionId);
    }

    public void SaveLimiterLevel(string connectionId, int level)
    {
        Command();
        inner.SaveLimiterLevel(connectionId, level);
    }

    public SyncedEntry? SyncedByKey(string pairId, Side side, string key)
    {
        Command();
        return inner.SyncedByKey(pairId, side, key);
    }

    public SyncedEntry? SyncedById(string pairId, Side side, string id)
    {
        Command();
        return inner.SyncedById(pairId, side, id);
    }

    public IReadOnlyList<SyncedEntry> SyncedBindingsById(string pairId, Side side, string id)
    {
        Command();
        return inner.SyncedBindingsById(pairId, side, id);
    }

    public WorkSelection RegisterDiscoveredRename(string pairId, long generation, Guid scanId, RenameCandidate candidate) =>
        inner.RegisterDiscoveredRename(pairId, generation, scanId, candidate);
    public bool CanUseListedEvidence(string pairId, Guid scanId, string localKey, string dropboxKey,
        bool includesDescendants, SyncedEntry? before = null)
    {
        Command();
        return inner.CanUseListedEvidence(pairId, scanId, localKey, dropboxKey, includesDescendants, before);
    }
    public RenameContinuation? ReadRenameContinuation(WorkSelection selection) { Command(); return inner.ReadRenameContinuation(selection); }
    public IReadOnlyList<(SyncedEntry Binding, bool LocalKeyMatched, bool DropboxKeyMatched)> ReconcileRenameContinuation(
        WorkSelection selection, SyncedEntry incoming)
    { Command(); return inner.ReconcileRenameContinuation(selection, incoming); }
    public void RegisterRenameContinuation(WorkSelection selection, SyncedEntry incoming, SyncedEntry destination, Issue? clash)
    { Command(); inner.RegisterRenameContinuation(selection, incoming, destination, clash); }
    public IReadOnlyList<(SyncedEntry Binding, bool LocalKeyMatched, bool DropboxKeyMatched)> RenameDestinationOwners(
        string pairId, SyncedEntry? before, SyncedEntry after)
    {
        Command();
        return inner.RenameDestinationOwners(pairId, before, after);
    }

    public void Commit(string pairId, StateChange change, Action<Issue>? committedIssue = null, Action<Issue, bool>? settledIssue = null) =>
        Commit(pairId, change, null, committedIssue, settledIssue);

    public void Commit(string pairId, StateChange change, WorkSelection? selection, Action<Issue>? committedIssue, Action<Issue, bool>? settledIssue = null)
    {
        Command();
        Interlocked.Increment(ref _commits);
        inner.Commit(pairId, change, selection, committedIssue, settledIssue);
    }

    public Task CommitAsync(string pairId, StateChange change, Action<Issue, bool>? settledIssue = null)
    {
        Command();
        Interlocked.Increment(ref _commits);
        return inner.CommitAsync(pairId, change, settledIssue);
    }

    public async Task CommitRecordAsync(string pairId, StateChange change, Action<Issue>? committedIssue, Action<Issue, bool>? settledIssue = null)
    {
        Command();
        Interlocked.Increment(ref _commits);
        Interlocked.Increment(ref _recordCalls);
        Task task;
        try { task = inner.CommitRecordAsync(pairId, change, committedIssue, settledIssue); }
        catch
        {
            Interlocked.Increment(ref _recordFailures);
            throw;
        }
        lock (_recordLock)
        {
            _records.Add(task);
            ObserveRecords();
        }
        try { await task.ConfigureAwait(false); }
        finally
        {
            lock (_recordLock) ObserveRecords();
        }
    }

    private void ObserveRecords()
    {
        var committed = 0;
        for (var index = _records.Count - 1; index >= 0; index--)
        {
            var task = _records[index];
            if (!task.IsCompleted) continue;
            if (task.IsCompletedSuccessfully) committed++;
            else Interlocked.Increment(ref _recordFailures);
            _records.RemoveAt(index);
        }
        if (committed == 0) return;
        Interlocked.Add(ref _recordCommitted, committed);
        Interlocked.Increment(ref _committedGroups);
    }

    public PendingRead Pending(string pairId)
    {
        Command();
        return inner.Pending(pairId);
    }

    public void ArmPending(string pairId, PendingEffect value)
    {
        Command();
        inner.ArmPending(pairId, value);
    }

    public void AdvancePending(string pairId, PendingEffect expected, PendingEffect next)
    {
        Command();
        inner.AdvancePending(pairId, expected, next);
    }

    public void SettlePending(string pairId, PendingEffect expected, StateChange change, Action<Issue>? committedIssue = null, Action<Issue, bool>? settledIssue = null)
    {
        Command();
        inner.SettlePending(pairId, expected, change, committedIssue, settledIssue);
    }

    public void SettlePending(string pairId, PendingEffect expected, StateChange change, WorkSelection? selection, Action<Issue>? committedIssue, Action<Issue, bool>? settledIssue = null)
    {
        Command();
        inner.SettlePending(pairId, expected, change, selection, committedIssue, settledIssue);
    }

    public void SettleLegacyPending(string pairId, string rawToken, StateChange issueOnlyChange)
    {
        Command();
        inner.SettleLegacyPending(pairId, rawToken, issueOnlyChange);
    }

    public JournalPosition? ReadJournal(string volumeKey)
    {
        Command();
        return inner.ReadJournal(volumeKey);
    }

    public long? AcquireJournalPage(string volumeKey, JournalPosition? expected, JournalPosition next,
        IReadOnlyList<(string PairId, long Generation, long Epoch, long Revision,
            bool LostInterval)> members,
        IEnumerable<(string PairId, ChangeNotice Notice)> notices)
    {
        Command();
        return inner.AcquireJournalPage(volumeKey, expected, next, members, notices);
    }

    public void RequireRepairAfterLoss(string pairId, long expectedGeneration)
    {
        Command();
        inner.RequireRepairAfterLoss(pairId, expectedGeneration);
    }

    public void RequireRepairForOwedHistory(string pairId, long expectedGeneration)
    {
        Command();
        inner.RequireRepairForOwedHistory(pairId, expectedGeneration);
    }

    public SourceObligation LostIntervalObligation(string pairId)
    {
        Command();
        return inner.LostIntervalObligation(pairId);
    }

    public (long Epoch, SourceProvenance? Provenance, string? Cursor, SourceObligation? Obligation, long Revision)?
        ReadSource(string pairId, Side side)
    {
        Command();
        return inner.ReadSource(pairId, side);
    }

    public bool Obligated(string pairId) => inner.Obligated(pairId);

    public LiveRefusal ReadLiveRefusal(string pairId)
    {
        Command();
        return inner.ReadLiveRefusal(pairId);
    }

    public long? ReadLocalGap(string pairId, long localEpoch)
    {
        Command();
        return inner.ReadLocalGap(pairId, localEpoch);
    }

    public long AcquireDropboxPage(string pairId, long expectedGeneration, long expectedEpoch,
        long expectedRevision, SourceProvenance provenance, string? expectedCursor,
        IEnumerable<ChangeNotice> notices, string cursor)
    {
        Command();
        return inner.AcquireDropboxPage(pairId, expectedGeneration, expectedEpoch, expectedRevision,
            provenance, expectedCursor, notices, cursor);
    }

    public void AdvanceSource(string pairId, Side side, long expectedGeneration,
        long expectedEpoch, long expectedRevision, SourceProvenance? expectedProvenance,
        string? expectedCursor, long nextEpoch, long nextRevision, SourceProvenance? provenance,
        string? cursor, SourceObligation? obligation)
    {
        Command();
        inner.AdvanceSource(pairId, side, expectedGeneration, expectedEpoch, expectedRevision,
            expectedProvenance, expectedCursor, nextEpoch, nextRevision, provenance, cursor, obligation);
    }

    public WorkSelection? SelectWork(string pairId, long expectedGeneration, PathScope scope)
    {
        Command();
        return inner.SelectWork(pairId, expectedGeneration, scope);
    }

    public (long Local, long Dropbox) CountWork(string pairId, long expectedGeneration, long through)
    {
        Command();
        return inner.CountWork(pairId, expectedGeneration, through);
    }

    public bool CompleteOutOfScope(WorkSelection selection, PathScope scope)
    {
        Command();
        return inner.CompleteOutOfScope(selection, scope);
    }

    public bool EndWorkPass(string pairId) => inner.EndWorkPass(pairId);

    public IEnumerable<PendingChange> ReadWork(WorkSelection selection)
    {
        Command();
        return inner.ReadWork(selection);
    }

    public SyncedEntry? SelectionBaseline(WorkSelection selection)
    {
        Command();
        return inner.SelectionBaseline(selection);
    }

    public IEnumerable<WorkSelection> PendingAncestors(string pairId, long expectedGeneration, Side side,
        IReadOnlyList<string> ancestorKeys, long? coveredThrough = null)
    {
        Command();
        return inner.PendingAncestors(pairId, expectedGeneration, side, ancestorKeys, coveredThrough);
    }

    public AncestorAnswer LaterAncestorNews(string pairId, long expectedGeneration, Side side,
        IReadOnlyList<string> ancestorKeys, long coveredThrough)
    {
        Command();
        return inner.LaterAncestorNews(pairId, expectedGeneration, side, ancestorKeys, coveredThrough);
    }

    public Func<Side, string, string?, bool> CreateSeparateWorkProbe(WorkSelection selection)
    {
        Command();
        var probe = inner.CreateSeparateWorkProbe(selection);
        return (side, key, identity) => { Command(); return probe(side, key, identity); };
    }

    public Func<SyncedEntry, long, bool> CreateLocalEchoProbe(WorkSelection selection)
    {
        Command();
        var probe = inner.CreateLocalEchoProbe(selection);
        return (saved, through) => { Command(); return probe(saved, through); };
    }

    public void ClaimSelectionPeer(WorkSelection selection, SelectionPeer peer) => inner.ClaimSelectionPeer(selection, peer);
    public void EndSelection(WorkSelection selection, WorkSelection? prerequisite = null) => inner.EndSelection(selection, prerequisite);
    public void RequireDiscovery(WorkSelection selection, long workId)
    {
        Command();
        inner.RequireDiscovery(selection, workId);
    }

    public bool WorkAttempted(WorkSelection selection) => inner.WorkAttempted(selection);

    public WorkSelection? BeginSelection(WorkSelection selection)
    {
        Command();
        return inner.BeginSelection(selection);
    }

    public bool CompleteWork(WorkSelection selection, SelectionPeer? peer)
    {
        Command();
        return inner.CompleteWork(selection, peer);
    }

    public bool AttachIssueWork(WorkSelection parent, Issue exactIssue,
        IEnumerable<(ChangeNotice Notice, bool IsFolder, SyncedEntry? Before)> unfinished)
    {
        Command();
        return inner.AttachIssueWork(parent, exactIssue, unfinished);
    }

    public (ClashRefusal State, Issue? Existing) ClashRefusalState(WorkSelection selection, string path,
        IssueSubject subject, (ChangeNotice Notice, bool IsFolder, SyncedEntry? Before) work)
    {
        Command();
        return inner.ClashRefusalState(selection, path, subject, work);
    }

    public (bool Dirty, JournalPosition? SourceBoundary, long EvidenceVersion) ReadLocalDirty(
        string pairId, long expectedGeneration, long localSourceEpoch, string nativeDomain,
        string? fullIdentity, string? currentPathKey, string? recordedPathKey)
    {
        Command();
        return inner.ReadLocalDirty(pairId, expectedGeneration, localSourceEpoch, nativeDomain,
            fullIdentity, currentPathKey, recordedPathKey);
    }

    public PendingRead ReadPendingForFolderRecovery(string pairId, FolderRemovalState operation)
    {
        Command();
        return inner.ReadPendingForFolderRecovery(pairId, operation);
    }

    public void ValidatePendingForFolderRecovery(string pairId, FolderRemovalState operation,
        PendingRead expectedPending)
    {
        Command();
        inner.ValidatePendingForFolderRecovery(pairId, operation, expectedPending);
    }

    public LiveCoverage CaptureLiveCoverage(WorkSelection selection, LocalCatchUp local, DropboxCatchUp dropbox)
    {
        Command();
        return inner.CaptureLiveCoverage(selection, local, dropbox);
    }

    public void ValidateLiveCoverage(WorkSelection selection, LiveCoverage coverage,
        IReadOnlyList<LiveEffectScope> footprint)
    {
        Command();
        inner.ValidateLiveCoverage(selection, coverage, footprint);
    }

    public void RefuseLaterEvidence(WorkSelection selection, IReadOnlyList<LiveEffectScope> footprint)
    {
        Command();
        inner.RefuseLaterEvidence(selection, footprint);
    }

    public Massdex.SyncShark.V2.FailureRetryOwner.State? ReadPrerequisiteRetry(string pairId, Issue? expected = null)
    {
        Command();
        return inner.ReadPrerequisiteRetry(pairId, expected);
    }

    public IEnumerable<(long PairRow, string PairId, long Generation,
        Massdex.SyncShark.V2.FailureRetryOwner.State State)> ReadPrerequisiteRetries(
        long? afterTicks, long? afterPairRow, int pageSize)
    {
        Command();
        return inner.ReadPrerequisiteRetries(afterTicks, afterPairRow, pageSize);
    }

    public void RecordPrerequisiteFailure(string pairId, long expectedGeneration,
        long localEpoch, long dropboxEpoch, long localRevision, long dropboxRevision,
        Massdex.SyncShark.V2.FailureRetryOwner.State? expected,
        Massdex.SyncShark.V2.FailureRetryOwner.State next, StateChange failureNotice)
    {
        Command();
        inner.RecordPrerequisiteFailure(pairId, expectedGeneration, localEpoch, dropboxEpoch,
            localRevision, dropboxRevision, expected, next, failureNotice);
    }

    public void CompleteRecomposition(string pairId, long expectedGeneration, long localEpoch, long dropboxEpoch,
        long localRevision, long dropboxRevision, Massdex.SyncShark.V2.FailureRetryOwner.State expected)
    {
        Command();
        inner.CompleteRecomposition(pairId, expectedGeneration, localEpoch, dropboxEpoch, localRevision, dropboxRevision, expected);
    }

    public Massdex.SyncShark.V2.FailureRetryOwner.State? ReadItemRetry(string pairId)
    {
        Command();
        return inner.ReadItemRetry(pairId);
    }

    public bool CompleteItemRetry(string pairId, long expectedGeneration,
        Massdex.SyncShark.V2.FailureRetryOwner.State admitted)
    {
        Command();
        return inner.CompleteItemRetry(pairId, expectedGeneration, admitted);
    }

    public Massdex.SyncShark.V2.FailureRetryOwner.State? RecordTemporaryItem(string pairId, long expectedGeneration,
        long sourceEpoch, WorkSelection? selection, (Side Side, string Path, string? Id)? successor,
        Massdex.SyncShark.V2.FailureRetryOwner.State? expected, Massdex.SyncShark.V2.FailureRetryOwner.State? next)
    {
        Command();
        return inner.RecordTemporaryItem(pairId, expectedGeneration, sourceEpoch, selection, successor, expected, next);
    }

    public Issue RecordPrerequisitePageWait(string pairId, long expectedGeneration, object operation,
        StateChange failureNotice)
    {
        Command();
        return inner.RecordPrerequisitePageWait(pairId, expectedGeneration, operation, failureNotice);
    }

    public void CompletePrerequisitePageWait(string pairId, long expectedGeneration, object operation,
        Issue expectedNotice)
    {
        Command();
        inner.CompletePrerequisitePageWait(pairId, expectedGeneration, operation, expectedNotice);
    }

    public ItemRetryCandidate RecordInUseFailure(string pairId, long expectedGeneration, long sourceEpoch,
        string path, IssueSubject subject, TextKey cause, WorkSelection? selection,
        ItemRetryDispatch dispatch, PendingEffect? expectedPending, ItemRetryToken? expectedEpisode,
        Guid registeredAttemptId, Action<Issue, bool>? settledIssue = null)
    {
        Command();
        return inner.RecordInUseFailure(pairId, expectedGeneration, sourceEpoch, path, subject, cause,
            selection, dispatch, expectedPending, expectedEpisode, registeredAttemptId, settledIssue);
    }

    public bool TryClaimItemRetry(ItemRetryCandidate expected, Guid registeredAttemptId)
    {
        Command();
        return inner.TryClaimItemRetry(expected, registeredAttemptId);
    }

    public void CompleteItemAttempt(ItemRetryCandidate expected, Guid registeredAttemptId, StateChange change,
        PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null, Action<Issue, bool>? settledIssue = null)
    {
        Command();
        inner.CompleteItemAttempt(expected, registeredAttemptId, change, completedPending, selection, committedIssue, settledIssue);
    }

    public IReadOnlyDictionary<long, ItemRetryCandidate?> ReadItemRetryState(string pairId,
        IReadOnlyList<long> issueIds, Issue? expected = null)
    {
        Command();
        return inner.ReadItemRetryState(pairId, issueIds, expected);
    }

    public FolderRemovalItem InsertFolderMember(string pairId, string operationId, long expectedGeneration,
        FolderRemovalItem.ProvenanceKind provenance, string path, Entry? observed, Entry? selected)
    {
        Command();
        return inner.InsertFolderMember(pairId, operationId, expectedGeneration, provenance, path, observed, selected);
    }

    public Action? BeforePlanDispose { get; set; }

    public string BeginFolderRemoval(string pairId, FolderPlan plan, int pageSize, CancellationToken ct, WorkSelection renameWork,
        Action? admit = null) => inner.BeginFolderRemoval(pairId, plan, pageSize, ct, renameWork, admit);
    public string? RenameRemoval(WorkSelection selection) => inner.RenameRemoval(selection);
    public long? RenameRemovalWork(string pairId, string operationId) => inner.RenameRemovalWork(pairId, operationId);
    public string BeginFolderRemoval(string pairId, FolderPlan plan, int pageSize, CancellationToken ct, Action? admit = null)
    {
        Command();
        return inner.BeginFolderRemoval(pairId, plan, pageSize, ct, admit);
    }

    public FolderRemovalState FolderRemoval(string pairId, string operationId)
    {
        Command();
        return inner.FolderRemoval(pairId, operationId);
    }

    public IEnumerable<FolderRemovalState> ActiveFolderRemovals(string pairId, CancellationToken ct)
    {
        Command();
        return inner.ActiveFolderRemovals(pairId, ct);
    }

    public IEnumerable<FolderRemovalEffect> ReadyFolderEffects(string pairId, string operationId,
        long afterOrdinal, int pageSize)
    {
        Command();
        return inner.ReadyFolderEffects(pairId, operationId, afterOrdinal, pageSize);
    }

    public IEnumerable<FolderRemovalItem> PendingFolderFollowUps(string pairId, string operationId,
        long afterOrdinal, int pageSize)
    {
        Command();
        return inner.PendingFolderFollowUps(pairId, operationId, afterOrdinal, pageSize);
    }

    public void CompleteFolderFollowUp(string pairId, string operationId, long ordinal, long expectedGeneration)
    {
        Command();
        inner.CompleteFolderFollowUp(pairId, operationId, ordinal, expectedGeneration);
    }

    public FolderRemovalItem? FolderRemovalItemByKey(string pairId, string operationId, string key)
    {
        Command();
        return inner.FolderRemovalItemByKey(pairId, operationId, key);
    }

    public IEnumerable<FolderRemovalItem> FolderRemovalItems(string pairId, string operationId,
        long afterOrdinal, int pageSize)
    {
        Command();
        return inner.FolderRemovalItems(pairId, operationId, afterOrdinal, pageSize);
    }

    public void AdvanceFolderRemoval(string pairId, string operationId, long expectedGeneration,
        FolderRemovalPhase expectedPhase, FolderRemovalState next)
    {
        Command();
        inner.AdvanceFolderRemoval(pairId, operationId, expectedGeneration, expectedPhase, next);
    }

    public void SettleFolderRemovalItem(string pairId, string operationId, long ordinal,
        long expectedGeneration, FolderRemovalItem next, StateChange verifiedChange)
    {
        Command();
        inner.SettleFolderRemovalItem(pairId, operationId, ordinal, expectedGeneration, next, verifiedChange);
    }

    public IReadOnlyList<bool> SettleFolderRemovalItems(string pairId, string operationId,
        IReadOnlyList<(long ExpectedGeneration, FolderRemovalItem Next, StateChange Change)> items)
    {
        Command();
        return inner.SettleFolderRemovalItems(pairId, operationId, items);
    }

    public void CompleteFolderRemoval(string pairId, string operationId, long expectedGeneration,
        StateChange verifiedRootChange, WorkSelection? selection)
    {
        Command();
        inner.CompleteFolderRemoval(pairId, operationId, expectedGeneration, verifiedRootChange, selection);
    }

    public void RetireFolderRemoval(string pairId, string operationId, long expectedGeneration,
        FolderRetirementDisposition disposition, StateChange issueOnlyAcknowledgement)
    {
        Command();
        inner.RetireFolderRemoval(pairId, operationId, expectedGeneration, disposition, issueOnlyAcknowledgement);
    }

    public string ArmFolderSubmission(string pairId, IReadOnlyList<FolderRemovalEffect> effects)
    {
        Command();
        return inner.ArmFolderSubmission(pairId, effects);
    }

    public FolderSubmission FolderSubmission(string submissionId)
    {
        Command();
        return inner.FolderSubmission(submissionId);
    }

    public IReadOnlyList<FolderRemovalEffect> FolderSubmissionEffects(string submissionId)
    {
        Command();
        return inner.FolderSubmissionEffects(submissionId);
    }

    public IEnumerable<FolderSubmission> PendingFolderSubmissions(string pairId, CancellationToken ct, bool progressOnly = false)
    {
        Command();
        return inner.PendingFolderSubmissions(pairId, ct, progressOnly);
    }

    public bool HasFolderJobProgress(string pairId, CancellationToken ct)
    {
        Command();
        return inner.HasFolderJobProgress(pairId, ct);
    }

    public void AdvanceFolderSubmission(FolderSubmission expected, FolderSubmission next,
        IReadOnlyList<FolderMemberResult> results)
    {
        Command();
        inner.AdvanceFolderSubmission(expected, next, results);
    }

    public void RecordFolderJobObservation(FolderSubmission expected,
        FolderJobResult? observation, TextKey? failureCause)
    {
        Command();
        inner.RecordFolderJobObservation(expected, observation, failureCause);
    }

    public bool HasInvalidFolderJob(string pairId, string path)
    {
        Command();
        return inner.HasInvalidFolderJob(pairId, path);
    }

    public IReadOnlyList<Issue> Issues(string pairId, IReadOnlyList<IssueKind>? kinds = null)
    {
        Command();
        if (kinds is not null) Interlocked.Increment(ref _filteredIssues);
        return inner.Issues(pairId, kinds);
    }

    public (IReadOnlyList<(string PairId, Issue Issue)> Page, int Total) OpenIssuePage(string? pairId,
        string? account, string? search, IReadOnlyList<IssueKind>? kinds, int limit, int offset, bool ignored = false)
    {
        Command();
        if (kinds is not null) Interlocked.Increment(ref _filteredIssues);
        return inner.OpenIssuePage(pairId, account, search, kinds, limit, offset, ignored);
    }

    public (IReadOnlyList<(string PairId, Issue Issue)> Rows, int Total) OpenIssueSnapshot(
        IReadOnlyList<IssueKind>? kinds, int pageSize)
    {
        Command();
        if (kinds is not null) Interlocked.Increment(ref _filteredIssues);
        return inner.OpenIssueSnapshot(kinds, pageSize);
    }

    public bool SetIssueIgnored(string pairId, Issue expected, bool ignored) => inner.SetIssueIgnored(pairId, expected, ignored);

    public IEnumerable<Issue> OpenIssuesUnder(string pairId, string scopeKey, IReadOnlyList<IssueKind>? kinds = null)
    {
        Command();
        if (kinds is not null) Interlocked.Increment(ref _filteredIssues);
        return inner.OpenIssuesUnder(pairId, scopeKey, kinds);
    }

    public Issue? IssueById(string pairId, long id)
    {
        Command();
        return inner.IssueById(pairId, id);
    }

    public IssueKind? HeldKind(string pairId, Row row, IReadOnlySet<long>? exceptIssues)
    {
        Command();
        return inner.HeldKind(pairId, row, exceptIssues);
    }

    public bool HeldCovers(string pairId, string key, IReadOnlySet<long>? exceptIssues)
    {
        Command();
        return inner.HeldCovers(pairId, key, exceptIssues);
    }

    public bool HeldByOtherIssue(string pairId, Row row, long selectedIssueId)
    {
        Command();
        return inner.HeldByOtherIssue(pairId, row, selectedIssueId);
    }

    public Issue? HoldingIssue(string pairId, Row row)
    {
        Command();
        return inner.HoldingIssue(pairId, row);
    }

    public IReadOnlyDictionary<string, IssueSubject> RetainedFirstRun(string pairId)
    {
        Command();
        return inner.RetainedFirstRun(pairId);
    }

    public long SyncedCount(string pairId)
    {
        Command();
        return inner.SyncedCount(pairId);
    }

    public bool HasOpenIssue(string pairId, IssueKind kind)
    {
        Command();
        return inner.HasOpenIssue(pairId, kind);
    }

    public IReadOnlyDictionary<IssueKind, int> IssueCounts(string pairId, bool? ignored = null)
    {
        Command();
        return inner.IssueCounts(pairId, ignored);
    }

    public void ReplaceSkipped(string pairId, Guid scanToken,
        IEnumerable<(string Path, TextKey Cause, IssueSubject Subject)> skipped)
    {
        Command();
        inner.ReplaceSkipped(pairId, scanToken, skipped);
    }

    public void ObserveSkipped(string pairId, long expectedGeneration, Side side, string path,
        (TextKey Cause, IssueSubject Subject)? currentSkip)
    {
        Command();
        inner.ObserveSkipped(pairId, expectedGeneration, side, path, currentSkip);
    }

    public bool RetryNoticeProtected(string pairId, long issueId)
    {
        Command();
        return inner.RetryNoticeProtected(pairId, issueId);
    }

    public void DismissIssue(string pairId, long id)
    {
        Command();
        inner.DismissIssue(pairId, id);
    }

    public void KeepFolderAlways(string pairId, long expectedGeneration, Issue expected,
        IReadOnlyList<Massdex.SyncShark.SyncIgnoreRule> keepRules)
    {
        Command();
        inner.KeepFolderAlways(pairId, expectedGeneration, expected, keepRules);
    }

    public Guid BeginScan(string pairId, Guid scanId, long expectedGeneration, bool includeDeleted, string? scope = null)
    {
        Command();
        return inner.BeginScan(pairId, scanId, expectedGeneration, includeDeleted, scope);
    }

    public bool HasDeletedNames(string pairId, Guid scanId)
    {
        Command();
        return inner.HasDeletedNames(pairId, scanId);
    }

    public bool DeletedNameExact(string pairId, Guid scanId, string key)
    {
        Command();
        return inner.DeletedNameExact(pairId, scanId, key);
    }

    public bool SavedDeletedNameExact(string pairId, string key)
    {
        Command();
        return inner.SavedDeletedNameExact(pairId, key);
    }

    public bool DeletedNameSpent(string pairId, string key)
    {
        Command();
        return inner.DeletedNameSpent(pairId, key);
    }

    public bool DeletedHistoryOwed(string pairId) => inner.DeletedHistoryOwed(pairId);
    public string? ContinueDeletedHistory(string pairId, Guid scanToken) => inner.ContinueDeletedHistory(pairId, scanToken);

    public string? RestoreDeletedHistory(string pairId, Guid scanToken, Action<long>? restored = null) => inner.RestoreDeletedHistory(pairId, scanToken, restored);
    public void ResetDeletedHistory(string pairId, Guid scanToken, bool discardNames) => inner.ResetDeletedHistory(pairId, scanToken, discardNames);
    public void SaveDeletedHistory(string pairId, Guid scanToken, string cursor) => inner.SaveDeletedHistory(pairId, scanToken, cursor);
    public IScanSink OpenScan(string pairId, Guid scanId, Side side, int batchRows)
    {
        Command();
        return new CountedScan(this, inner.OpenScan(pairId, scanId, side, batchRows));
    }

    public FolderPlan OpenFolderPlan(string pairId, Row folder, Side deletingFrom)
    {
        Command();
        return new CountedPlan(this, Step(nameof(OpenFolderPlan),
            () => inner.OpenFolderPlan(pairId, folder, deletingFrom)));
    }

    public bool UnreadableTouches(string pairId, string localKey, bool isFolder)
    {
        Command();
        return inner.UnreadableTouches(pairId, localKey, isFolder);
    }

    public IReadOnlyList<string> UnreadableUnder(string pairId, string localKey)
    {
        Command();
        return inner.UnreadableUnder(pairId, localKey);
    }

    public bool ReleaseUnreadable(string pairId, long expectedGeneration, string localKey, string acquiredId)
    {
        Command();
        return inner.ReleaseUnreadable(pairId, expectedGeneration, localKey, acquiredId);
    }

    public bool ForgetTouchesUnreadable(string pairId, IReadOnlyList<(Side Side, string Key)> roots, CancellationToken ct)
    {
        Command();
        return inner.ForgetTouchesUnreadable(pairId, roots, ct);
    }

    public IEnumerable<Row> ScanRows(string pairId, Guid scanId, Func<Row, RowContext> context)
    {
        Command();
        Interlocked.Increment(ref _readConnections);
        foreach (var row in inner.ScanRows(pairId, scanId, context))
        {
            Interlocked.Increment(ref _rowsRead);
            yield return row;
        }
    }

    public IReadOnlyList<RenameCandidate> ScanRenames(string pairId, Guid scanId)
    {
        Command();
        Interlocked.Increment(ref _readConnections);
        return inner.ScanRenames(pairId, scanId);
    }

    public Entry? ScanEntry(string pairId, Guid scanId, Side side, string key)
    {
        Command();
        return inner.ScanEntry(pairId, scanId, side, key);
    }

    public bool InScan(string pairId, Guid scanId, Side side, string key)
    {
        Command();
        return inner.InScan(pairId, scanId, side, key);
    }

    public void Dispose() => inner.Dispose();

    private void Command([CallerMemberName] string member = "")
    {
        Interlocked.Increment(ref _commands);
        _memberCalls.AddOrUpdate(member, 1, static (_, count) => count + 1);
    }

    private PhaseCounts Phase(string name) => _planPhases.GetOrAdd(name, static _ => new PhaseCounts());
    private void Incomplete() => Volatile.Write(ref _planIncomplete, 1);

    private T Step<T>(string name, Func<T> action, long statements = 0)
    {
        var phase = Phase(name);
        phase.Start();
        try
        {
            var result = action();
            phase.Succeed();
            Interlocked.Add(ref _planStatements, statements);
            return result;
        }
        catch
        {
            Incomplete();
            throw;
        }
    }

    private void Step(string name, Action action, long statements = 0) =>
        Step(name, () => { action(); return true; }, statements);

    /// <summary>Observed calls and yields; success requires normal return, including iterator disposal.</summary>
    public sealed class PhaseCounts
    {
        private long _attempts;
        private long _succeeded;
        private long _items;
        public long Attempts => Interlocked.Read(ref _attempts);
        public long Succeeded => Interlocked.Read(ref _succeeded);
        public long Items => Interlocked.Read(ref _items);
        internal void Start() => Interlocked.Increment(ref _attempts);
        internal void Succeed() => Interlocked.Increment(ref _succeeded);
        internal void Yield() => Interlocked.Increment(ref _items);
    }

    private sealed class CountedScan(StoreCounters owner, IScanSink sink) : IScanSink
    {
        private long _accepted;
        private int _ending;
        private int _aborted;
        private int _credited;
        private int _abortCounted;
        private int _failureCounted;

        public async Task AddAsync(Listed item, CancellationToken ct)
        {
            await sink.AddAsync(item, ct).ConfigureAwait(false);
            if (Volatile.Read(ref _ending) != 0) return;
            Interlocked.Increment(ref _accepted);
            Interlocked.Increment(ref owner._scanAccepted);
        }

        public async Task CompleteAsync()
        {
            Volatile.Write(ref _ending, 1);
            try
            {
                await sink.CompleteAsync().ConfigureAwait(false);
                if (Volatile.Read(ref _aborted) == 0 && Interlocked.Exchange(ref _credited, 1) == 0)
                {
                    Interlocked.Add(ref owner._scanApplied, Interlocked.Read(ref _accepted));
                    Interlocked.Increment(ref owner._scanCompleted);
                }
            }
            catch
            {
                if (Interlocked.Exchange(ref _failureCounted, 1) == 0)
                    Interlocked.Increment(ref owner._scanFailed);
                throw;
            }
        }

        public async Task AbortAsync()
        {
            Volatile.Write(ref _ending, 1);
            Volatile.Write(ref _aborted, 1);
            await sink.AbortAsync().ConfigureAwait(false);
            if (Interlocked.Exchange(ref _abortCounted, 1) == 0)
                Interlocked.Increment(ref owner._scanAborted);
        }
    }

    // The wrapper retains counters, never candidates, selected keys or a second plan.
    private sealed class CountedPlan(StoreCounters owner, FolderPlan plan) : FolderPlan
    {
        private bool _hasBranches;

        public Row Folder => plan.Folder;
        public Side DeletingFrom => plan.DeletingFrom;
        public bool DeleteWhole => plan.DeleteWhole;

        public async Task CaptureAsync(IAsyncEnumerable<Listed> source, CancellationToken ct)
        {
            var phase = owner.Phase(nameof(CaptureAsync));
            phase.Start();
            long events = 0;
            var tombstones = false;
            try
            {
                await plan.CaptureAsync(CountSource(ct), ct).ConfigureAwait(false);
                phase.Succeed();
                if (tombstones) owner.Incomplete();
                else Interlocked.Add(ref owner._planStatements, events);
            }
            catch
            {
                owner.Incomplete();
                throw;
            }

            async IAsyncEnumerable<Listed> CountSource([EnumeratorCancellation] CancellationToken token)
            {
                await foreach (var item in source.WithCancellation(token).ConfigureAwait(false))
                {
                    events++;
                    tombstones |= item.Entry is null;
                    phase.Yield();
                    yield return item;
                }
            }
        }

        public void PrepareRemoval(Row observed) => owner.Step(nameof(PrepareRemoval), () => plan.PrepareRemoval(observed));

        public IEnumerable<Entry> Entries(CancellationToken ct) =>
            Rows(nameof(Entries), () => plan.Entries(ct), terminalPages: 1);

        public IEnumerable<(string Key, string NativeKey, Entry? Entry, SyncedEntry? Synced)> Children(
            CancellationToken ct) => Rows(nameof(Children), () => plan.Children(ct), terminalPages: 2);

        public void MarkClassified(string key, string nativeKey, FolderChild child, bool followUp,
            CancellationToken ct) => owner.Step(nameof(MarkClassified),
                () => plan.MarkClassified(key, nativeKey, child, followUp, ct), statements: 1);

        public void PrepareBranches(CancellationToken ct)
        {
            owner.Step(nameof(PrepareBranches), () => plan.PrepareBranches(ct), statements: 1);
            _hasBranches = true;
        }

        public IEnumerable<(long Id, string Key, FolderChild Child)> RemovedChildren(CancellationToken ct) =>
            Rows(nameof(RemovedChildren), () => plan.RemovedChildren(ct), terminalPages: 1,
                depth: static item => PathNames.Depth(item.Key));

        public bool HasSurvivorAtOrBelow(string key, CancellationToken ct) =>
            owner.Step(nameof(HasSurvivorAtOrBelow), () => plan.HasSurvivorAtOrBelow(key, ct), statements: 1);

        public bool HasSelectedAncestor(string key, CancellationToken ct)
        {
            // The bool result hides ancestor-probe counts; CostProbe measures their SQL separately.
            owner.Incomplete();
            return owner.Step(nameof(HasSelectedAncestor), () => plan.HasSelectedAncestor(key, ct));
        }

        public void SelectBranch(long id, CancellationToken ct) =>
            owner.Step(nameof(SelectBranch), () => plan.SelectBranch(id, ct), statements: 1);

        public void Complete(bool deleteWhole, CancellationToken ct)
        {
            owner.Step(nameof(Complete), () => plan.Complete(deleteWhole, ct));
        }

        public bool ForgetTouchesUnreadable(CancellationToken ct) =>
            owner.Step(nameof(ForgetTouchesUnreadable), () => plan.ForgetTouchesUnreadable(ct));

        public IEnumerable<FrozenFolderEntry> FrozenEvidence(int pageSize, CancellationToken ct) =>
            Rows(nameof(FrozenEvidence), () => plan.FrozenEvidence(pageSize, ct), terminalPages: 1, pageSize: pageSize);

        public IEnumerable<FolderChild> Branches(CancellationToken ct) =>
            Rows(nameof(Branches), () => plan.Branches(ct), terminalPages: _hasBranches ? 1 : 0);

        public IEnumerable<FolderChild> Survivors(CancellationToken ct) =>
            Rows(nameof(Survivors), () => plan.Survivors(ct), terminalPages: _hasBranches ? 1 : 0);

        public void Dispose()
        {
            owner.BeforePlanDispose?.Invoke();
            owner.Step(nameof(Dispose), plan.Dispose);
        }

        private IEnumerable<T> Rows<T>(string name, Func<IEnumerable<T>> source, int terminalPages,
            Func<T, int>? depth = null, int pageSize = 1)
        {
            var phase = owner.Phase(name);
            phase.Start();
            long rows = 0;
            long depths = 0;
            int? previousDepth = null;
            var complete = false;
            try
            {
                foreach (var item in source())
                {
                    if (depth is not null)
                    {
                        var current = depth(item);
                        if (current != previousDepth) depths++;
                        previousDepth = current;
                    }
                    rows++;
                    phase.Yield();
                    yield return item;
                }
                complete = true;
                phase.Succeed();
                // RemovedChildren adds one next-depth and one terminal-row page per occupied depth.
                Interlocked.Add(ref owner._planStatements, (rows + pageSize - 1) / pageSize + terminalPages + 2 * depths);
            }
            finally
            {
                if (!complete) owner.Incomplete();
            }
        }
    }
}
