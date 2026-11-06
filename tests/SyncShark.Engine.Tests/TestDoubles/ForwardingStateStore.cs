using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Passes every IStateStore member through to an inner store; checks add one hook by overriding.</summary>
public class ForwardingStateStore(IStateStore inner) : IStateStore
{
    internal IStateStore FixtureInner => Inner;
    protected IStateStore Inner { get; } = inner;

    public virtual IReadOnlyList<PairConfig> Pairs() => Inner.Pairs();
    public virtual PairConfig? Pair(string pairId) => Inner.Pair(pairId);
    public virtual void SavePair(PairConfig pair) => Inner.SavePair(pair);
    public virtual void SavePairDecision(PairConfig expected, long expectedGeneration, PairConfig next,
        Issue? expectedIssue, bool resetHistory, string? reincludedKey) =>
        Inner.SavePairDecision(expected, expectedGeneration, next, expectedIssue, resetHistory, reincludedKey);
    public virtual (PairConfig Pair, long Generation)? ReadPairExecution(string pairId) =>
        Inner.ReadPairExecution(pairId);
    public virtual ItemRetryCandidate? ReadItemRetryForSubject(string pairId, long expectedGeneration,
        string path, IssueSubject subject) =>
        Inner.ReadItemRetryForSubject(pairId, expectedGeneration, path, subject);
    public virtual long ReadWorkWatermark() => Inner.ReadWorkWatermark();
    public virtual void SaveImportedPair(PairConfig pair, bool legacyPaused, IEnumerable<SyncedEntry>? baselines = null) =>
        Inner.SaveImportedPair(pair, legacyPaused, baselines);
    public virtual void MergeDriveEvidence(string pairId, long expectedGeneration, DriveEvidence acquired) =>
        Inner.MergeDriveEvidence(pairId, expectedGeneration, acquired);
    public virtual void ReconcileRootIdentity(string pairId, long expectedGeneration, RootBinding? expectedBinding,
        Issue? expectedIssue, StateChange issueOnlyChange) =>
        Inner.ReconcileRootIdentity(pairId, expectedGeneration, expectedBinding, expectedIssue, issueOnlyChange);
    public virtual void RemovePair(string pairId) => Inner.RemovePair(pairId);
    public virtual bool FirstRepairDone(string pairId) => Inner.FirstRepairDone(pairId);
    public virtual RepairSummary? LastRepair(string pairId) => Inner.LastRepair(pairId);
    public virtual void MarkRepairLogFailed(string pairId, string runId) => Inner.MarkRepairLogFailed(pairId, runId);
    public virtual void FinishRepair(string pairId, RepairSummary summary) => Inner.FinishRepair(pairId, summary);
    public virtual void FinishRepair(string pairId, RepairSummary summary, long expectedGeneration, Guid scanToken,
        LocalCatchUp local, DropboxCatchUp dropbox) =>
        Inner.FinishRepair(pairId, summary, expectedGeneration, scanToken, local, dropbox);
    public virtual void ForgetAllSynced(string pairId) => Inner.ForgetAllSynced(pairId);
    public virtual int? LimiterLevel(string connectionId) => Inner.LimiterLevel(connectionId);
    public virtual void SaveLimiterLevel(string connectionId, int level) => Inner.SaveLimiterLevel(connectionId, level);
    public virtual SyncedEntry? SyncedByKey(string pairId, Side side, string key) => Inner.SyncedByKey(pairId, side, key);
    public virtual SyncedEntry? SyncedById(string pairId, Side side, string id) => Inner.SyncedById(pairId, side, id);
    public virtual IReadOnlyList<SyncedEntry> SyncedBindingsById(string pairId, Side side, string id) =>
        Inner.SyncedBindingsById(pairId, side, id);
    public virtual Entry? ScanEntry(string pairId, Guid scanId, Side side, string key) => Inner.ScanEntry(pairId, scanId, side, key);
    public virtual IReadOnlyList<Issue> Issues(string pairId, IReadOnlyList<IssueKind>? kinds = null) =>
        Inner.Issues(pairId, kinds);
    public virtual (IReadOnlyList<(string PairId, Issue Issue)> Page, int Total) OpenIssuePage(string? pairId,
        string? account, string? search, IReadOnlyList<IssueKind>? kinds, int limit, int offset, bool ignored = false) =>
        Inner.OpenIssuePage(pairId, account, search, kinds, limit, offset, ignored);
    public virtual (IReadOnlyList<(string PairId, Issue Issue)> Rows, int Total) OpenIssueSnapshot(
        IReadOnlyList<IssueKind>? kinds, int pageSize) => Inner.OpenIssueSnapshot(kinds, pageSize);
    public virtual IEnumerable<Issue> OpenIssuesUnder(string pairId, string scopeKey, IReadOnlyList<IssueKind>? kinds = null) =>
        Inner.OpenIssuesUnder(pairId, scopeKey, kinds);
    public virtual void RecordPrerequisiteFailure(string pairId, long expectedGeneration, long localEpoch,
        long dropboxEpoch, long localRevision, long dropboxRevision, FailureRetryOwner.State? expected,
        FailureRetryOwner.State next, StateChange failureNotice) =>
        Inner.RecordPrerequisiteFailure(pairId, expectedGeneration, localEpoch, dropboxEpoch, localRevision,
            dropboxRevision, expected, next, failureNotice);
    public virtual void CompleteRecomposition(string pairId, long expectedGeneration, long localEpoch, long dropboxEpoch,
        long localRevision, long dropboxRevision, FailureRetryOwner.State expected) =>
        Inner.CompleteRecomposition(pairId, expectedGeneration, localEpoch, dropboxEpoch, localRevision, dropboxRevision, expected);
    public virtual void Commit(string pairId, StateChange change, Action<Issue>? committedIssue = null) =>
        Inner.Commit(pairId, change, committedIssue);
    public virtual void Commit(string pairId, StateChange change, WorkSelection? selection, Action<Issue>? committedIssue) =>
        Inner.Commit(pairId, change, selection, committedIssue);
    public virtual Task CommitAsync(string pairId, StateChange change) => Inner.CommitAsync(pairId, change);
    public virtual Task CommitRecordAsync(string pairId, StateChange change, Action<Issue>? committedIssue) =>
        Inner.CommitRecordAsync(pairId, change, committedIssue);
    public virtual PendingRead Pending(string pairId) => Inner.Pending(pairId);
    public virtual void ArmPending(string pairId, PendingEffect value) => Inner.ArmPending(pairId, value);
    public virtual void AdvancePending(string pairId, PendingEffect expected, PendingEffect next) =>
        Inner.AdvancePending(pairId, expected, next);
    public virtual void SettlePending(string pairId, PendingEffect expected, StateChange change, Action<Issue>? committedIssue = null) =>
        Inner.SettlePending(pairId, expected, change, committedIssue);
    public virtual void SettlePending(string pairId, PendingEffect expected, StateChange change, WorkSelection? selection, Action<Issue>? committedIssue) =>
        Inner.SettlePending(pairId, expected, change, selection, committedIssue);
    public virtual void SettleLegacyPending(string pairId, string rawToken, StateChange issueOnlyChange) =>
        Inner.SettleLegacyPending(pairId, rawToken, issueOnlyChange);
    public virtual JournalPosition? ReadJournal(string volumeKey) => Inner.ReadJournal(volumeKey);
    public virtual long? AcquireJournalPage(string volumeKey, JournalPosition? expected, JournalPosition next,
        IReadOnlyList<(string PairId, long Generation, long Epoch, long Revision, bool LostInterval)> members,
        IEnumerable<(string PairId, ChangeNotice Notice)> notices) =>
        Inner.AcquireJournalPage(volumeKey, expected, next, members, notices);
    public virtual void RequireRepairAfterLoss(string pairId, long expectedGeneration) =>
        Inner.RequireRepairAfterLoss(pairId, expectedGeneration);
    public virtual SourceObligation LostIntervalObligation(string pairId) =>
        Inner.LostIntervalObligation(pairId);
    public virtual (long Epoch, SourceProvenance? Provenance, string? Cursor, SourceObligation? Obligation, long Revision)? ReadSource(string pairId, Side side) =>
        Inner.ReadSource(pairId, side);
    public virtual bool Obligated(string pairId) => Inner.Obligated(pairId);
    public virtual LiveRefusal ReadLiveRefusal(string pairId) => Inner.ReadLiveRefusal(pairId);
    public virtual long? ReadLocalGap(string pairId, long localEpoch) => Inner.ReadLocalGap(pairId, localEpoch);
    public virtual long AcquireDropboxPage(string pairId, long expectedGeneration, long expectedEpoch,
        long expectedRevision, SourceProvenance provenance, string? expectedCursor,
        IEnumerable<ChangeNotice> notices, string cursor) =>
        Inner.AcquireDropboxPage(pairId, expectedGeneration, expectedEpoch, expectedRevision, provenance,
            expectedCursor, notices, cursor);
    public virtual void AdvanceSource(string pairId, Side side, long expectedGeneration, long expectedEpoch,
        long expectedRevision, SourceProvenance? expectedProvenance, string? expectedCursor, long nextEpoch,
        long nextRevision, SourceProvenance? provenance, string? cursor, SourceObligation? obligation) =>
        Inner.AdvanceSource(pairId, side, expectedGeneration, expectedEpoch, expectedRevision, expectedProvenance,
            expectedCursor, nextEpoch, nextRevision, provenance, cursor, obligation);
    public virtual WorkSelection? SelectWork(string pairId, long expectedGeneration, PathScope scope) =>
        Inner.SelectWork(pairId, expectedGeneration, scope);
    public virtual bool EndWorkPass(string pairId) => Inner.EndWorkPass(pairId);
    public virtual IEnumerable<PendingChange> ReadWork(WorkSelection selection) => Inner.ReadWork(selection);
    public virtual IEnumerable<WorkSelection> PendingAncestors(string pairId, long expectedGeneration, Side side,
        IReadOnlyList<string> ancestorKeys, long? coveredThrough = null) =>
        Inner.PendingAncestors(pairId, expectedGeneration, side, ancestorKeys, coveredThrough);
    public virtual AncestorAnswer LaterAncestorNews(string pairId, long expectedGeneration, Side side,
        IReadOnlyList<string> ancestorKeys, long coveredThrough) =>
        Inner.LaterAncestorNews(pairId, expectedGeneration, side, ancestorKeys, coveredThrough);
    public virtual Func<Side, string, string?, bool> CreateSeparateWorkProbe(WorkSelection selection) => Inner.CreateSeparateWorkProbe(selection);
    public virtual Func<SyncedEntry, long, bool> CreateLocalEchoProbe(WorkSelection selection) =>
        Inner.CreateLocalEchoProbe(selection);
    public virtual void ClaimSelectionPeer(WorkSelection selection, SelectionPeer peer) => Inner.ClaimSelectionPeer(selection, peer);
    public virtual void EndSelection(WorkSelection selection) => Inner.EndSelection(selection);
    public virtual void BeginSelection(WorkSelection selection) => Inner.BeginSelection(selection);
    public virtual bool CompleteWork(WorkSelection selection, SelectionPeer? peer) => Inner.CompleteWork(selection, peer);
    public virtual bool AttachIssueWork(WorkSelection parent, Issue exactIssue,
        IEnumerable<(ChangeNotice Notice, bool IsFolder, SyncedEntry? Before)> unfinished) =>
        Inner.AttachIssueWork(parent, exactIssue, unfinished);
    public virtual (bool Dirty, JournalPosition? SourceBoundary, long EvidenceVersion) ReadLocalDirty(
        string pairId, long expectedGeneration, long localSourceEpoch, string nativeDomain, string? fullIdentity,
        string? currentPathKey, string? recordedPathKey) =>
        Inner.ReadLocalDirty(pairId, expectedGeneration, localSourceEpoch, nativeDomain, fullIdentity,
            currentPathKey, recordedPathKey);
    public virtual PendingRead ReadPendingForFolderRecovery(string pairId, FolderRemovalState operation) =>
        Inner.ReadPendingForFolderRecovery(pairId, operation);
    public virtual void ValidatePendingForFolderRecovery(string pairId, FolderRemovalState operation,
        PendingRead expectedPending) =>
        Inner.ValidatePendingForFolderRecovery(pairId, operation, expectedPending);
    public virtual LiveCoverage CaptureLiveCoverage(WorkSelection selection, LocalCatchUp local,
        DropboxCatchUp dropbox) => Inner.CaptureLiveCoverage(selection, local, dropbox);
    public virtual void ValidateLiveCoverage(WorkSelection selection, LiveCoverage coverage,
        IReadOnlyList<LiveEffectScope> footprint) => Inner.ValidateLiveCoverage(selection, coverage, footprint);
    public virtual FailureRetryOwner.State? ReadPrerequisiteRetry(string pairId) => Inner.ReadPrerequisiteRetry(pairId);
    public virtual IEnumerable<(long PairRow, string PairId, long Generation, FailureRetryOwner.State State)> ReadPrerequisiteRetries(
        long? afterTicks, long? afterPairRow, int pageSize) =>
        Inner.ReadPrerequisiteRetries(afterTicks, afterPairRow, pageSize);
    public virtual Issue RecordPrerequisitePageWait(string pairId, long expectedGeneration, object operation,
        StateChange failureNotice) =>
        Inner.RecordPrerequisitePageWait(pairId, expectedGeneration, operation, failureNotice);
    public virtual void CompletePrerequisitePageWait(string pairId, long expectedGeneration, object operation,
        Issue expectedNotice) =>
        Inner.CompletePrerequisitePageWait(pairId, expectedGeneration, operation, expectedNotice);
    public virtual ItemRetryCandidate RecordInUseFailure(string pairId, long expectedGeneration, long sourceEpoch,
        string path, IssueSubject subject, TextKey cause, WorkSelection? selection, ItemRetryDispatch dispatch,
        PendingEffect? expectedPending, ItemRetryToken? expectedEpisode, Guid registeredAttemptId) =>
        Inner.RecordInUseFailure(pairId, expectedGeneration, sourceEpoch, path, subject, cause, selection, dispatch,
            expectedPending, expectedEpisode, registeredAttemptId);
    public virtual bool TryClaimItemRetry(ItemRetryCandidate expected, Guid registeredAttemptId) =>
        Inner.TryClaimItemRetry(expected, registeredAttemptId);
    public virtual void CompleteItemAttempt(ItemRetryCandidate expected, Guid registeredAttemptId, StateChange change,
        PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null) =>
        Inner.CompleteItemAttempt(expected, registeredAttemptId, change, completedPending, selection, committedIssue);
    public virtual IReadOnlyDictionary<long, ItemRetryCandidate?> ReadItemRetryState(string pairId,
        IReadOnlyList<long> issueIds) => Inner.ReadItemRetryState(pairId, issueIds);
    public virtual FolderRemovalItem InsertFolderMember(string pairId, string operationId, long expectedGeneration,
        FolderRemovalItem.ProvenanceKind provenance, string path, Entry? observed, Entry? selected) =>
        Inner.InsertFolderMember(pairId, operationId, expectedGeneration, provenance, path, observed, selected);
    public virtual string BeginFolderRemoval(string pairId, FolderPlan plan, int pageSize, CancellationToken ct) =>
        Inner.BeginFolderRemoval(pairId, plan, pageSize, ct);
    public virtual FolderRemovalState FolderRemoval(string pairId, string operationId) =>
        Inner.FolderRemoval(pairId, operationId);
    public virtual IEnumerable<FolderRemovalState> ActiveFolderRemovals(string pairId, CancellationToken ct) =>
        Inner.ActiveFolderRemovals(pairId, ct);
    public virtual IEnumerable<FolderRemovalEffect> ReadyFolderEffects(string pairId, string operationId,
        long afterOrdinal, int pageSize) => Inner.ReadyFolderEffects(pairId, operationId, afterOrdinal, pageSize);
    public virtual IEnumerable<FolderRemovalItem> PendingFolderFollowUps(string pairId, string operationId,
        long afterOrdinal, int pageSize) => Inner.PendingFolderFollowUps(pairId, operationId, afterOrdinal, pageSize);
    public virtual void CompleteFolderFollowUp(string pairId, string operationId, long ordinal,
        long expectedGeneration) => Inner.CompleteFolderFollowUp(pairId, operationId, ordinal, expectedGeneration);
    public virtual IEnumerable<FolderRemovalItem> FolderRemovalItems(string pairId, string operationId,
        long afterOrdinal, int pageSize) => Inner.FolderRemovalItems(pairId, operationId, afterOrdinal, pageSize);
    public virtual FolderRemovalItem? FolderRemovalItemByKey(string pairId, string operationId, string key) =>
        Inner.FolderRemovalItemByKey(pairId, operationId, key);
    public virtual void AdvanceFolderRemoval(string pairId, string operationId, long expectedGeneration,
        FolderRemovalPhase expectedPhase, FolderRemovalState next) =>
        Inner.AdvanceFolderRemoval(pairId, operationId, expectedGeneration, expectedPhase, next);
    public virtual void SettleFolderRemovalItem(string pairId, string operationId, long ordinal,
        long expectedGeneration, FolderRemovalItem next, StateChange verifiedChange) =>
        Inner.SettleFolderRemovalItem(pairId, operationId, ordinal, expectedGeneration, next, verifiedChange);
    public virtual IReadOnlyList<bool> SettleFolderRemovalItems(string pairId, string operationId,
        IReadOnlyList<(long ExpectedGeneration, FolderRemovalItem Next, StateChange Change)> items) =>
        Inner.SettleFolderRemovalItems(pairId, operationId, items);
    public virtual void CompleteFolderRemoval(string pairId, string operationId, long expectedGeneration,
        StateChange verifiedRootChange, WorkSelection? selection) =>
        Inner.CompleteFolderRemoval(pairId, operationId, expectedGeneration, verifiedRootChange, selection);
    public virtual void RetireFolderRemoval(string pairId, string operationId, long expectedGeneration,
        FolderRetirementDisposition disposition, StateChange issueOnlyAcknowledgement) =>
        Inner.RetireFolderRemoval(pairId, operationId, expectedGeneration, disposition, issueOnlyAcknowledgement);
    public virtual string ArmFolderSubmission(string pairId, IReadOnlyList<FolderRemovalEffect> effects) =>
        Inner.ArmFolderSubmission(pairId, effects);
    public virtual FolderSubmission FolderSubmission(string submissionId) => Inner.FolderSubmission(submissionId);
    public virtual IReadOnlyList<FolderRemovalEffect> FolderSubmissionEffects(string submissionId) =>
        Inner.FolderSubmissionEffects(submissionId);
    public virtual IEnumerable<FolderSubmission> PendingFolderSubmissions(string pairId, CancellationToken ct, bool progressOnly = false) =>
        Inner.PendingFolderSubmissions(pairId, ct, progressOnly);
    public virtual bool HasFolderJobProgress(string pairId, CancellationToken ct) =>
        Inner.HasFolderJobProgress(pairId, ct);
    public virtual void AdvanceFolderSubmission(FolderSubmission expected, FolderSubmission next,
        IReadOnlyList<FolderMemberResult> results) => Inner.AdvanceFolderSubmission(expected, next, results);
    public virtual void RecordFolderJobObservation(FolderSubmission expected, FolderJobResult? observation,
        TextKey? failureCause) => Inner.RecordFolderJobObservation(expected, observation, failureCause);
    public virtual bool HasInvalidFolderJob(string pairId, string path) => Inner.HasInvalidFolderJob(pairId, path);
    public virtual void ObserveSkipped(string pairId, long expectedGeneration, Side side, string path,
        (TextKey Cause, IssueSubject Subject)? currentSkip) =>
        Inner.ObserveSkipped(pairId, expectedGeneration, side, path, currentSkip);
    public virtual bool RetryNoticeProtected(string pairId, long issueId) => Inner.RetryNoticeProtected(pairId, issueId);
    public virtual void KeepFolderAlways(string pairId, long expectedGeneration, Issue expected,
        IReadOnlyList<Massdex.SyncShark.SyncIgnoreRule> keepRules) =>
        Inner.KeepFolderAlways(pairId, expectedGeneration, expected, keepRules);
    public virtual bool HasDeletedNames(string pairId, Guid scanId) => Inner.HasDeletedNames(pairId, scanId);
    public virtual bool DeletedNameExact(string pairId, Guid scanId, string key) => Inner.DeletedNameExact(pairId, scanId, key);
    public virtual bool DeletedNameSpent(string pairId, string key) => Inner.DeletedNameSpent(pairId, key);
    public virtual FolderPlan OpenFolderPlan(string pairId, Row folder, Side deletingFrom) =>
        Inner.OpenFolderPlan(pairId, folder, deletingFrom);
    public virtual bool UnreadableTouches(string pairId, string localKey, bool isFolder) =>
        Inner.UnreadableTouches(pairId, localKey, isFolder);
    public virtual IReadOnlyList<string> UnreadableUnder(string pairId, string localKey) =>
        Inner.UnreadableUnder(pairId, localKey);
    public virtual bool ReleaseUnreadable(string pairId, long expectedGeneration, string localKey, string acquiredId) =>
        Inner.ReleaseUnreadable(pairId, expectedGeneration, localKey, acquiredId);
    public virtual bool ForgetTouchesUnreadable(string pairId, IReadOnlyList<(Side Side, string Key)> roots,
        CancellationToken ct) => Inner.ForgetTouchesUnreadable(pairId, roots, ct);
    public virtual bool SetIssueIgnored(string pairId, long id, bool ignored) => Inner.SetIssueIgnored(pairId, id, ignored);

    public virtual Issue? IssueById(string pairId, long id) => Inner.IssueById(pairId, id);
    public virtual IssueKind? HeldKind(string pairId, Row row, IReadOnlySet<long>? exceptIssues) =>
        Inner.HeldKind(pairId, row, exceptIssues);
    public virtual bool HeldCovers(string pairId, string key, IReadOnlySet<long>? exceptIssues) =>
        Inner.HeldCovers(pairId, key, exceptIssues);
    public virtual bool HeldByOtherIssue(string pairId, Row row, long selectedIssueId) =>
        Inner.HeldByOtherIssue(pairId, row, selectedIssueId);
    public virtual Issue? HoldingIssue(string pairId, Row row) => Inner.HoldingIssue(pairId, row);
    public virtual IReadOnlyDictionary<string, IssueSubject> RetainedFirstRun(string pairId) =>
        Inner.RetainedFirstRun(pairId);
    public virtual IReadOnlyDictionary<IssueKind, int> IssueCounts(string pairId, bool? ignored = null) => Inner.IssueCounts(pairId, ignored);
    public virtual long SyncedCount(string pairId) => Inner.SyncedCount(pairId);
    public virtual void ReplaceSkipped(string pairId, Guid scanToken,
        IEnumerable<(string Path, TextKey Cause, IssueSubject Subject)> skipped) =>
        Inner.ReplaceSkipped(pairId, scanToken, skipped);
    public virtual void DismissIssue(string pairId, long id) => Inner.DismissIssue(pairId, id);
    public virtual Guid BeginScan(string pairId, Guid scanId, long expectedGeneration, bool includeDeleted, string? scope = null) =>
        Inner.BeginScan(pairId, scanId, expectedGeneration, includeDeleted, scope);
    public virtual IScanSink OpenScan(string pairId, Guid scanId, Side side, int batchRows) =>
        Inner.OpenScan(pairId, scanId, side, batchRows);
    public virtual bool SavedDeletedNameExact(string pairId, string key) => Inner.SavedDeletedNameExact(pairId, key);
    public virtual bool DeletedHistoryOwed(string pairId) => Inner.DeletedHistoryOwed(pairId);
    public virtual string? ContinueDeletedHistory(string pairId, Guid scanToken) => Inner.ContinueDeletedHistory(pairId, scanToken);

    public virtual string? RestoreDeletedHistory(string pairId, Guid scanToken) => Inner.RestoreDeletedHistory(pairId, scanToken);
    public virtual void ResetDeletedHistory(string pairId, Guid scanToken, bool discardNames) => Inner.ResetDeletedHistory(pairId, scanToken, discardNames);
    public virtual void SaveDeletedHistory(string pairId, Guid scanToken, string cursor) => Inner.SaveDeletedHistory(pairId, scanToken, cursor);
    public virtual IEnumerable<Row> ScanRows(string pairId, Guid scanId, Func<Row, RowContext> context) =>
        Inner.ScanRows(pairId, scanId, context);
    public virtual IReadOnlyList<RenameCandidate> ScanRenames(string pairId, Guid scanId) => Inner.ScanRenames(pairId, scanId);
    public virtual bool InScan(string pairId, Guid scanId, Side side, string key) => Inner.InScan(pairId, scanId, side, key);
    public virtual ScanRecord ReadScan(string pairId, Guid scanToken) => Inner.ReadScan(pairId, scanToken);
    public virtual Task EndScanAsync(string pairId, Guid scanId, int pageSize) => Inner.EndScanAsync(pairId, scanId, pageSize);
    public virtual bool CloseScanIssue(string pairId, Guid scanId, Issue expected) => Inner.CloseScanIssue(pairId, scanId, expected);
    public virtual void Dispose() => Inner.Dispose();
}
