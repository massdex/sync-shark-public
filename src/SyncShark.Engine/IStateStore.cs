namespace Massdex.SyncShark.Engine;

/// <summary>The engine's ordered persistence boundary for mappings, issues, source work and recovery custody.
/// Implementations own transactions and encoding; callers never infer settlement from an external effect alone.</summary>
public interface IStateStore : IDisposable
{
    IReadOnlyList<PairConfig> Pairs();
    PairConfig? Pair(string pairId);
    void SavePair(PairConfig pair);
    /// <summary>Commits the exact authorized pair decision, generation/source transition and optional history reset
    /// atomically, preserving current binding, protected recovery custody and unrelated issues.</summary>
    void SavePairDecision(PairConfig expected, long expectedGeneration, PairConfig next,
        Issue? expectedIssue, bool resetHistory, string? reincludedKey);
    (PairConfig Pair, long Generation)? ReadPairExecution(string pairId);
    ItemRetryCandidate? ReadItemRetryForSubject(string pairId, long expectedGeneration,
        string path, IssueSubject subject);
    long ReadWorkWatermark();
    void MergeDriveEvidence(string pairId, long expectedGeneration, DriveEvidence acquired);
    void ReconcileRootIdentity(string pairId, long expectedGeneration, RootBinding? expectedBinding,
        Issue? expectedIssue, StateChange issueOnlyChange);
    /// <summary>Removes the pair only when pending custody, active operations and submission guards permit it.
    /// Refusal rolls back the entire transaction, including member detachment; it is not successful removal.</summary>
    void RemovePair(string pairId);
    bool FirstRepairDone(string pairId);
    RepairSummary? LastRepair(string pairId);
    /// <summary>Marks only the matching run's logging metadata, preserving its authoritative outcome.</summary>
    void MarkRepairLogFailed(string pairId, string runId);
    /// <summary>Saves progress without clearing prerequisite debt or declaring authoritative completion.</summary>
    void FinishRepair(string pairId, RepairSummary summary);
    /// <summary>Completes only the captured scan and source responsibility after the exact durable checkpoint returns.
    /// Reads retry state inside the transaction; closes its notice only when that episode exists, and retains later work.
    /// BeginScan owns the pre-list fences and retirement watermark; catch-ups attest later checkpoints.
    /// The same transaction installs the owed-history obligation. True means committed: a later publication or
    /// maintenance failure is returned as `secondaryFailure`, never thrown.</summary>
    bool FinishRepair(string pairId, RepairSummary summary, long expectedGeneration, Guid scanToken,
        LocalCatchUp local, DropboxCatchUp dropbox, out Exception? secondaryFailure);
    /// <summary>One transaction: deletes the pair's saved records and clears first_repair_done.</summary>
    void ForgetAllSynced(string pairId);
    /// <summary>Null when never saved (Run then uses InitialLimiterLevel).</summary>
    int? LimiterLevel(string connectionId);
    void SaveLimiterLevel(string connectionId, int level);

    SyncedEntry? SyncedByKey(string pairId, Side side, string key);
    SyncedEntry? SyncedById(string pairId, Side side, string id);
    /// <summary>All saved bindings for one provider identity, including distinct hard-link paths.</summary>
    IReadOnlyList<SyncedEntry> SyncedBindingsById(string pairId, Side side, string id);
    /// <summary>Returns every other binding at either destination key in one snapshot, merging matches by binding key.</summary>
    IReadOnlyList<(SyncedEntry Binding, bool LocalKeyMatched, bool DropboxKeyMatched)> RenameDestinationOwners(
        string pairId, SyncedEntry? before, SyncedEntry after);
    bool CanUseListedEvidence(string pairId, Guid scanId, string localKey, string dropboxKey,
        bool includesDescendants, SyncedEntry? before = null);
    WorkSelection RegisterDiscoveredRename(string pairId, long generation, Guid scanId, RenameCandidate candidate);
    RenameContinuation? ReadRenameContinuation(WorkSelection selection);
    /// <summary>Reconciles no-issue rename responsibility with all current destination owners in one snapshot.</summary>
    IReadOnlyList<(SyncedEntry Binding, bool LocalKeyMatched, bool DropboxKeyMatched)> ReconcileRenameContinuation(
        WorkSelection selection, SyncedEntry incoming);
    void RegisterRenameContinuation(WorkSelection selection, SyncedEntry incoming, SyncedEntry destination, Issue? clash);

    /// <summary>Applies the change atomically and reports exact raised issues only after successful commit.
    /// settledIssue receives the Storage-owned announcement disposition after commit and lock release; committedIssue
    /// independently retains responsibility. Callback failure cannot undo state; residual attachment retains its parent on failure.</summary>
    void Commit(string pairId, StateChange change, Action<Issue>? committedIssue = null,
        Action<Issue, bool>? settledIssue = null);
    /// <summary>Commits a rename and advances only its selected preimage in the same transaction.</summary>
    void Commit(string pairId, StateChange change, WorkSelection? selection, Action<Issue>? committedIssue,
        Action<Issue, bool>? settledIssue = null);
    /// <summary>Commits file Records and store-only file adoptions through the Record queue, or incompatible changes immediately.
    /// Completion belongs to this submission's durable result, never merely to a later write barrier.</summary>
    Task CommitAsync(string pairId, StateChange change, Action<Issue, bool>? settledIssue = null);
    /// <summary>First-run Record entry: file Records join the Record queue; raising or folder Records commit now
    /// and report their committed issue exactly once. Other malformed changes are rejected.</summary>
    Task CommitRecordAsync(string pairId, StateChange change, Action<Issue>? committedIssue,
        Action<Issue, bool>? settledIssue = null);

    PendingRead Pending(string pairId);
    /// <summary>Refuses any occupied slot and verifies the selected path and identity's exact saved binding.</summary>
    void ArmPending(string pairId, PendingEffect value);
    /// <summary>Advances only the same operation's next generation with exact persisted phase/evidence equality.</summary>
    void AdvancePending(string pairId, PendingEffect expected, PendingEffect next);
    /// <summary>Checks the pending note and saved binding, applies its permitted change and clears custody atomically.</summary>
    void SettlePending(string pairId, PendingEffect expected, StateChange change, Action<Issue>? committedIssue = null,
        Action<Issue, bool>? settledIssue = null);
    void SettlePending(string pairId, PendingEffect expected, StateChange change, WorkSelection? selection, Action<Issue>? committedIssue,
        Action<Issue, bool>? settledIssue = null);
    /// <summary>Acknowledges only the exact raw legacy note with an issue-only change; it grants no file ownership.</summary>
    void SettleLegacyPending(string pairId, string rawToken, StateChange issueOnlyChange);

    /// <summary>The volume's committed journal position, or null before its first base.</summary>
    JournalPosition? ReadJournal(string volumeKey);
    /// <summary>Commits ordered journal notices, member obligations and the checkpoint in one transaction.
    /// The caller holds the authoritative volume membership gate through publication; Storage validates persistent fences.
    /// A lost-interval member installs Storage's Bootstrap-or-NormalRepair answer, never clearing debt; LocalSkippedCoverage takes precedence.
    /// Returns null only for definitive stale rollback with no page changes, otherwise the committed watermark, including zero.
    /// Invalid inputs and genuine failures throw; uncertain acknowledgment requires authoritative readback, not replay.</summary>
    long? AcquireJournalPage(string volumeKey, JournalPosition? expected, JournalPosition next,
        IReadOnlyList<(string PairId, long Generation, long Epoch, long Revision,
            bool LostInterval)> members,
        IEnumerable<(string PairId, ChangeNotice Notice)> notices);

    /// <summary>The obligation a lost source interval installs: Bootstrap before the pair's first completion,
    /// NormalRepair after it. First-run behavior itself comes from first_repair_done, never from this reason.</summary>
    SourceObligation LostIntervalObligation(string pairId);
    /// <summary>Invalidates Local Repair completion after source loss, preserving existing debt and acquired work.</summary>
    void RequireRepairAfterLoss(string pairId, long expectedGeneration);
    /// <summary>Retains automatic acquisition responsibility when a Repair leaves history owed without existing debt.</summary>
    void RequireRepairForOwedHistory(string pairId, long expectedGeneration);

    /// <summary>The authoritative source fence, or null before the source is installed; reread after a lost page reply.</summary>
    (long Epoch, SourceProvenance? Provenance, string? Cursor, SourceObligation? Obligation, long Revision)?
        ReadSource(string pairId, Side side);
    /// <summary>Whether either source owes a Repair: an outstanding obligation, or a source row not yet installed.</summary>
    bool Obligated(string pairId);
    /// <summary>What owed Repair refuses in live work; the one owner for AutoSync, selection and live admission.</summary>
    LiveRefusal ReadLiveRefusal(string pairId);
    /// <summary>The Local debt's revision when that debt is a skipped interval, else null, for the completion fence.
    /// It is source responsibility only, never evidence about content.</summary>
    long? ReadLocalGap(string pairId, long localEpoch);
    /// <summary>Replays ordered notices synchronously in the same-source cursor transaction; failure rolls back all work.
    /// The returned watermark belongs only to the successful commit, not to terminal coverage by itself.</summary>
    long AcquireDropboxPage(string pairId, long expectedGeneration, long expectedEpoch, long expectedRevision,
        SourceProvenance provenance, string? expectedCursor, IEnumerable<ChangeNotice> notices, string cursor);
    /// <summary>Installs or replaces a source under exact expected fences in one Store-owned transaction.
    /// A Local epoch advance preserves required skipped-coverage responsibility rather than hiding acquired evidence.</summary>
    void AdvanceSource(string pairId, Side side, long expectedGeneration,
        long expectedEpoch, long expectedRevision, SourceProvenance? expectedProvenance, string? expectedCursor,
        long nextEpoch, long nextRevision, SourceProvenance? provenance, string? cursor,
        SourceObligation? obligation);

    WorkSelection? SelectWork(string pairId, long expectedGeneration, PathScope scope);
    /// <summary>Ends the opportunity's finite selection pass without retiring unfinished work.</summary>
    bool EndWorkPass(string pairId);
    /// <summary>Streams immutable Acquired membership; issue-origin work is reached through its issue owner instead.</summary>
    IEnumerable<PendingChange> ReadWork(WorkSelection selection);
    /// <summary>The selection's current saved binding, by identity when known and otherwise by path.</summary>
    SyncedEntry? SelectionBaseline(WorkSelection selection);
    /// <summary>Unresolved selections at any owner-supplied ancestor key, including old paths and registered residual
    /// aliases, so they take precedence over dependent descendants across restart. Work at or below coveredThrough
    /// is omitted: only a Repair passes it, with the pre-list watermark its listing covers.</summary>
    IEnumerable<WorkSelection> PendingAncestors(string pairId, long expectedGeneration, Side side,
        IReadOnlyList<string> ancestorKeys, long? coveredThrough = null);
    /// <summary>Atomically classifies later ancestor notices against their saved folder bindings without retiring work.</summary>
    AncestorAnswer LaterAncestorNews(string pairId, long expectedGeneration, Side side,
        IReadOnlyList<string> ancestorKeys, long coveredThrough);
    /// <summary>Captures selection membership once for a walk; the returned probe checks separate acquired work.</summary>
    Func<Side, string, string?, bool> CreateSeparateWorkProbe(WorkSelection selection);
    /// <summary>Snapshots open responsibility once per finite work pass (or standalone selection), then checks current guards.
    /// Never grants peer retirement authority; a Dropbox row through the supplied watermark forbids settlement.</summary>
    Func<SyncedEntry, long, bool> CreateLocalEchoProbe(WorkSelection selection);
    /// <summary>True admits; false means already attempted this finite pass with no new claim; stale selections still throw.</summary>
    bool BeginSelection(WorkSelection selection);
    /// <summary>Reports whether this exact anchor was attempted in the matching-generation finite pass.</summary>
    bool WorkAttempted(WorkSelection selection);
    /// <summary>Durably retains a selected folder's discovery before its root can be recorded.</summary>
    void RequireDiscovery(WorkSelection selection, long workId);
    /// <summary>Claims the observed peer until this selection ends; no durable work is retired.</summary>
    void ClaimSelectionPeer(WorkSelection selection, SelectionPeer peer);
    /// <summary>Releases process-local claims; a prerequisite suspends this attempt until that work settles in this pass.</summary>
    void EndSelection(WorkSelection selection, WorkSelection? prerequisite = null);
    /// <summary>Retires completed membership and observed peers; returns false while the anchor remains owned by an issue or live parent.</summary>
    bool CompleteWork(WorkSelection selection, SelectionPeer? peer);
    /// <summary>Registers unfinished branches against the exact committed issue without retiring their parent.</summary>
    bool AttachIssueWork(WorkSelection parent, Issue exactIssue,
        IEnumerable<(ChangeNotice Notice, bool IsFolder, SyncedEntry? Before)> unfinished);

    /// <summary>Returns actual acquired dirty evidence and its immutable watermark, never gap-only content debt.
    /// A null journal boundary or unknown identity is unavailable evidence, not a clean-content answer.</summary>
    (bool Dirty, JournalPosition? SourceBoundary, long EvidenceVersion) ReadLocalDirty(
        string pairId, long expectedGeneration, long localSourceEpoch, string nativeDomain,
        string? fullIdentity, string? currentPathKey, string? recordedPathKey);
    /// <summary>Captures the exact pending descriptor while validating the durable folder operation.</summary>
    PendingRead ReadPendingForFolderRecovery(string pairId, FolderRemovalState operation);
    /// <summary>Rejects changed operation or pending descriptors instead of widening an already-held footprint.</summary>
    void ValidatePendingForFolderRecovery(string pairId, FolderRemovalState operation, PendingRead expectedPending);
    LiveCoverage CaptureLiveCoverage(WorkSelection selection, LocalCatchUp local, DropboxCatchUp dropbox);
    /// <summary>Validates both source fences, own-source watermark and relevant unselected evidence at admission.
    /// Root identities do not mint coverage, and stale evidence never means successful settlement.</summary>
    void ValidateLiveCoverage(WorkSelection selection, LiveCoverage coverage, IReadOnlyList<LiveEffectScope> footprint);

    /// <summary>An expected notice must still match under the same lock as the retry-state read; otherwise ResolveStale.</summary>
    Massdex.SyncShark.V2.FailureRetryOwner.State? ReadPrerequisiteRetry(string pairId, Issue? expected = null);
    IEnumerable<(long PairRow, string PairId, long Generation, Massdex.SyncShark.V2.FailureRetryOwner.State State)>
        ReadPrerequisiteRetries(long? afterTicks, long? afterPairRow, int pageSize);
    /// <summary>Records one unsuccessful prerequisite attempt and its notice under exact fences, without creating source debt.
    /// In-flight listing-page failures never advance this durable episode.</summary>
    void RecordPrerequisiteFailure(string pairId, long expectedGeneration,
        long localEpoch, long dropboxEpoch, long localRevision, long dropboxRevision,
        Massdex.SyncShark.V2.FailureRetryOwner.State? expected,
        Massdex.SyncShark.V2.FailureRetryOwner.State next, StateChange failureNotice);
    /// <summary>Clears exact recomposition retry state after healthy source checks, without recording a Repair.</summary>
    void CompleteRecomposition(string pairId, long expectedGeneration, long localEpoch, long dropboxEpoch,
        long localRevision, long dropboxRevision, Massdex.SyncShark.V2.FailureRetryOwner.State expected);
    /// <summary>The pair's retry state when it is item-only: a row exists, no source owes Repair and no root
    /// retry notice is open. Otherwise null. The one answer for owed versus item-only.</summary>
    Massdex.SyncShark.V2.FailureRetryOwner.State? ReadItemRetry(string pairId) => throw new NotSupportedException();
    /// <summary>Clears the exact admitted item-only retry state against fresh source fences; false when newer state
    /// must survive. Never reads or closes a notice.</summary>
    bool CompleteItemRetry(string pairId, long expectedGeneration, Massdex.SyncShark.V2.FailureRetryOwner.State admitted)
        => throw new NotSupportedException();
    /// <summary>Retains one temporary turn's work (an acquired selection, or a Repair successor registered as ordinary
    /// work) and, when next is non-null, applies it with an exact CAS against expected, without a notice. Returns the
    /// row's committed state after the transaction; a CAS clash returns the newer state.</summary>
    Massdex.SyncShark.V2.FailureRetryOwner.State? RecordTemporaryItem(string pairId, long expectedGeneration,
        long sourceEpoch, WorkSelection? selection, (Side Side, string Path, string? Id)? successor,
        Massdex.SyncShark.V2.FailureRetryOwner.State? expected, Massdex.SyncShark.V2.FailureRetryOwner.State? next)
        => throw new NotSupportedException();

    /// <summary>Raises or reuses the page-wait notice under the current operation without changing pair retry state.</summary>
    Issue RecordPrerequisitePageWait(string pairId, long expectedGeneration, object operation,
        StateChange failureNotice);
    /// <summary>Ends only this operation's exact page-wait contribution after page success, cancellation or disable.
    /// Independently protected failed-Repair debt and its notice survive; state failure is never healthy completion.</summary>
    void CompletePrerequisitePageWait(string pairId, long expectedGeneration, object operation, Issue expectedNotice);

    /// <summary>Atomically retains the exact in-use issue, work and lifecycle for one registered attempt.
    /// There is no timed item retry; post-commit callbacks are notification only.</summary>
    ItemRetryCandidate RecordInUseFailure(string pairId, long expectedGeneration, long sourceEpoch,
        string path, IssueSubject subject, TextKey cause, WorkSelection? selection,
        ItemRetryDispatch dispatch, PendingEffect? expectedPending, ItemRetryToken? expectedEpisode,
        Guid registeredAttemptId, Action<Issue, bool>? settledIssue = null);
    /// <summary>Requires the existing exclusive registry reservation for a real file, Repair or user Retry opportunity.</summary>
    bool TryClaimItemRetry(ItemRetryCandidate expected, Guid registeredAttemptId);
    /// <summary>Settles this exact claimed attempt and verified change atomically without clearing unrelated custody.</summary>
    void CompleteItemAttempt(ItemRetryCandidate expected, Guid registeredAttemptId, StateChange change,
        PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null,
        Action<Issue, bool>? settledIssue = null);
    /// <summary>One page-batched read of lifecycle/work facts; Run supplies transient presentation authority.
    /// An expected notice must match under the same lock before any retry identity is returned; otherwise ResolveStale.</summary>
    IReadOnlyDictionary<long, ItemRetryCandidate?> ReadItemRetryState(string pairId, IReadOnlyList<long> issueIds,
        Issue? expected = null);

    /// <summary>Streams a completed plan into durable operation evidence before any submission is permitted.</summary>
    string BeginFolderRemoval(string pairId, FolderPlan plan, int pageSize, CancellationToken ct);
    string BeginFolderRemoval(string pairId, FolderPlan plan, int pageSize, CancellationToken ct, WorkSelection renameWork);
    long? RenameRemovalWork(string pairId, string operationId);
    string? RenameRemoval(WorkSelection selection);
    /// <summary>Durably adds an observed recovery path without changing frozen effect membership or operation generation.</summary>
    FolderRemovalItem InsertFolderMember(string pairId, string operationId, long expectedGeneration,
        FolderRemovalItem.ProvenanceKind provenance, string path, Entry? observed, Entry? selected);
    FolderRemovalState FolderRemoval(string pairId, string operationId);
    IEnumerable<FolderRemovalState> ActiveFolderRemovals(string pairId, CancellationToken ct);
    IEnumerable<FolderRemovalEffect> ReadyFolderEffects(string pairId, string operationId,
        long afterOrdinal, int pageSize);
    IEnumerable<FolderRemovalItem> PendingFolderFollowUps(string pairId, string operationId,
        long afterOrdinal, int pageSize);
    void CompleteFolderFollowUp(string pairId, string operationId, long ordinal, long expectedGeneration);
    IEnumerable<FolderRemovalItem> FolderRemovalItems(string pairId, string operationId,
        long afterOrdinal, int pageSize);
    /// <summary>The operation's frozen member at one exact key on its deleting side, or null when none is known.</summary>
    FolderRemovalItem? FolderRemovalItemByKey(string pairId, string operationId, string key);
    void AdvanceFolderRemoval(string pairId, string operationId, long expectedGeneration,
        FolderRemovalPhase expectedPhase, FolderRemovalState next);
    /// <summary>Commits only the verified member change with its exact saved-binding and generation checks.</summary>
    void SettleFolderRemovalItem(string pairId, string operationId, long ordinal,
        long expectedGeneration, FolderRemovalItem next, StateChange verifiedChange);
    /// <summary>Atomically commits verified members, returning false for each stale or rebound member left pending.</summary>
    IReadOnlyList<bool> SettleFolderRemovalItems(string pairId, string operationId,
        IReadOnlyList<(long ExpectedGeneration, FolderRemovalItem Next, StateChange Change)> items);
    /// <summary>Settles the enclosing root last, after every effect, follow-up and custody obligation permits completion.</summary>
    void CompleteFolderRemoval(string pairId, string operationId, long expectedGeneration,
        StateChange verifiedRootChange, WorkSelection? selection);
    /// <summary>Records honest terminal disposition with issue-only acknowledgement, never fabricated removal success.</summary>
    void RetireFolderRemoval(string pairId, string operationId, long expectedGeneration,
        FolderRetirementDisposition disposition, StateChange issueOnlyAcknowledgement);
    /// <summary>Arms all exact ready effects and immutable wire-member links in one transaction or none.</summary>
    string ArmFolderSubmission(string pairId, IReadOnlyList<FolderRemovalEffect> effects);
    FolderSubmission FolderSubmission(string submissionId);
    IReadOnlyList<FolderRemovalEffect> FolderSubmissionEffects(string submissionId);
    /// <summary>Streams accepted receipts for one pair in submission-ID order, releasing the read gate and
    /// reader before each yield so no snapshot crosses consumer provider work. Progress wakes exclude failed observations.</summary>
    IEnumerable<FolderSubmission> PendingFolderSubmissions(string pairId, CancellationToken ct, bool progressOnly = false);
    /// <summary>Whether an accepted folder job still has a progress cadence to observe: no failure or invalid cause.
    /// Its one reader is AutoSync's own next-poll decision.</summary>
    bool HasFolderJobProgress(string pairId, CancellationToken ct);
    /// <summary>Persists attributed receipt certainty under CAS; missing or malformed members never shift attribution.</summary>
    void AdvanceFolderSubmission(FolderSubmission expected, FolderSubmission next,
        IReadOnlyList<FolderMemberResult> results);
    /// <summary>Atomically records a validated observation or failure under exact submission fences and reconciles
    /// invalid-job responsibility without clearing another submission's contribution or settling deletion.</summary>
    void RecordFolderJobObservation(FolderSubmission expected,
        FolderJobResult? observation, TextKey? failureCause);
    /// <summary>Whether any still-responsible submission linked to this root contributes an invalid-job blocker.</summary>
    bool HasInvalidFolderJob(string pairId, string path);

    IReadOnlyList<Issue> Issues(string pairId, IReadOnlyList<IssueKind>? kinds = null);
    /// <summary>One bounded page of open issues across the pair, account or all pairs, ordered by issue id, with the
    /// filtered total in the same read transaction. Dismissed rows never page; only the page is decoded, and only
    /// page rows cost retry projection.</summary>
    (IReadOnlyList<(string PairId, Issue Issue)> Page, int Total) OpenIssuePage(string? pairId, string? account,
        string? search, IReadOnlyList<IssueKind>? kinds, int limit, int offset, bool ignored = false);
    /// <summary>Every open issue of the requested kinds in issue-id order and the filtered total, read in one
    /// read-only snapshot: a concurrent commit can neither hide a notice behind a reused issue id nor make the
    /// count disagree with the rows.</summary>
    (IReadOnlyList<(string PairId, Issue Issue)> Rows, int Total) OpenIssueSnapshot(IReadOnlyList<IssueKind>? kinds,
        int pageSize);
    bool SetIssueIgnored(string pairId, Issue expected, bool ignored);
    /// <summary>Streams requested issue kinds under any subject's side-specific key, in distinct id order.
    /// Includes dismissed rows; empty scope equals Issues. Item-less operational notices use their issue path.</summary>
    IEnumerable<Issue> OpenIssuesUnder(string pairId, string scopeKey, IReadOnlyList<IssueKind>? kinds = null);
    Issue? IssueById(string pairId, long id);
    IssueKind? HeldKind(string pairId, Row row, IReadOnlySet<long>? exceptIssues);
    bool HeldCovers(string pairId, string key, IReadOnlySet<long>? exceptIssues);
    /// <summary>Whether another holding issue covers the row's policy keys or its Dropbox removal path.</summary>
    bool HeldByOtherIssue(string pairId, Row row, long selectedIssueId);
    Issue? HoldingIssue(string pairId, Row row);
    IReadOnlyDictionary<string, IssueSubject> RetainedFirstRun(string pairId);
    /// <summary>Open (not dismissed) issues per kind; kinds with none are absent. Subjects are not read.</summary>
    IReadOnlyDictionary<IssueKind, int> IssueCounts(string pairId, bool? ignored = null);
    /// <summary>Checks open stored-kind authority; ignored notices remain safety obligations.</summary>
    bool HasOpenIssue(string pairId, IssueKind kind);
    /// <summary>Saved matched items, files and folders; each row represents one item on each side.</summary>
    long SyncedCount(string pairId);
    /// <summary>Reconciles only untouched sides of the completed scan; stale or restarted scan tokens refuse.</summary>
    void ReplaceSkipped(string pairId, Guid scanToken,
        IEnumerable<(string Path, TextKey Cause, IssueSubject Subject)> skipped);
    /// <summary>Records a live side observation and scan touch atomically; null means a positively observed clear. A
    /// protecting Local member ignores both null and a nonprotecting skip: a named read cannot prove enumeration.</summary>
    void ObserveSkipped(string pairId, long expectedGeneration, Side side, string path,
        (TextKey Cause, IssueSubject Subject)? currentSkip);
    /// <summary>Returns the issue owner's durable-responsibility answer; mutation rechecks it inside its transaction.
    /// First-run debt is not part of it: the scan close acknowledges that debt itself.</summary>
    bool RetryNoticeProtected(string pairId, long issueId);
    void DismissIssue(string pairId, long id);
    /// <summary>Unions normalized keep rules and dismisses only the exact validated kept-folder issue atomically.
    /// The caller retains fresh locked coverage; stale subject or applicability refuses without changes.</summary>
    void KeepFolderAlways(string pairId, long expectedGeneration, Issue expected,
        IReadOnlyList<Massdex.SyncShark.SyncIgnoreRule> keepRules);

    /// <summary>Starts the disposable scan and its process-local skip overlay before either listing.
    /// Discovery is selected once by Repair; no independent plan scratch is removed.</summary>
    Guid BeginScan(string pairId, Guid scanId, long expectedGeneration, bool includeDeleted, string? scope = null);
    /// <summary>Returns the immutable pre-list responsibility of the exact current scan; stale tokens refuse.</summary>
    ScanRecord ReadScan(string pairId, Guid scanToken);
    /// <summary>Checks the completed discovery relation once per immutable scan, bypassing per-row work when empty.</summary>
    bool HasDeletedNames(string pairId, Guid scanId);
    /// <summary>Probes one path-owner-supplied exact key in discovery or retained history, masked by spent state; a match is not deletion proof.</summary>
    /// <exception cref="InvalidDataException">Saved history was corrupt and its acquisition authority was revoked.</exception>
    bool DeletedNameExact(string pairId, Guid scanId, string key);
    /// <summary>Whether this exact history key was consumed by committed Local removal or endpoint retirement; independent of scans.</summary>
    bool DeletedNameSpent(string pairId, string key);
    /// <summary>Opens a side writer whose queue and save transaction are bounded by <paramref name="batchRows"/>.</summary>
    IScanSink OpenScan(string pairId, Guid scanId, Side side, int batchRows);
    /// <summary>Whether a full deleted-name acquisition is still owed, independently of first-Repair completion.</summary>
    bool DeletedHistoryOwed(string pairId);
    /// <summary>Exact membership in completed durable history; a match selects evidence, never removal authority.</summary>
    /// <exception cref="InvalidDataException">Saved history was corrupt and its acquisition authority was revoked.</exception>
    bool SavedDeletedNameExact(string pairId, string key);
    /// <summary>Seeds deleted names from a completed acquisition, returning its own continuation cursor.
    /// <paramref name="restored"/> receives the running count after each block, outside the store lock.</summary>
    string? RestoreDeletedHistory(string pairId, Guid scanToken, Action<long>? restored = null);

    /// <summary>Continues the published history cursor without restoring blocks; new names stay in the durable delta relation.</summary>
    string? ContinueDeletedHistory(string pairId, Guid scanToken);
    /// <summary>Clears this scan's remote live rows; optionally discards invalid saved discovery after cursor reset.</summary>
    void ResetDeletedHistory(string pairId, Guid scanToken, bool discardNames);
    /// <summary>Atomically retains joined discovery names and their terminal cursor; never a deletion decision.</summary>
    void SaveDeletedHistory(string pairId, Guid scanToken, string cursor);
    /// <summary>Closes an operation's partition after its borrowers join; cleanup is not cancelled midway.</summary>
    Task EndScanAsync(string pairId, Guid scanId, int pageSize);
    /// <summary>Closes only an unchanged issue covered by this scan's pre-list evidence, including its acknowledgements.</summary>
    bool CloseScanIssue(string pairId, Guid scanId, Issue expected);
    FolderPlan OpenFolderPlan(string pairId, Row folder, Side deletingFrom);
    /// <summary>Whether a protecting Local member sits at the key, at an ancestor or, when isFolder, below it. isFolder
    /// describes the affected old footprint. A file action runs no range.</summary>
    bool UnreadableTouches(string pairId, string localKey, bool isFolder);
    /// <summary>The protecting Local members at or below the key, for one involved folder's plan or release.</summary>
    IReadOnlyList<string> UnreadableUnder(string pairId, string localKey);
    /// <summary>Removes the exact Local member, only when its own cause protects and its stored identity is the id read
    /// from the enumerated handle; false writes nothing and is not an error.</summary>
    bool ReleaseUnreadable(string pairId, long expectedGeneration, string localKey, string acquiredId);
    /// <summary>Whether either exact affected forget range touches a protecting Local member directly or through a saved
    /// binding; an empty footprint is false without a statement.</summary>
    bool ForgetTouchesUnreadable(string pairId, IReadOnlyList<(Side Side, string Key)> roots, CancellationToken ct);
    IEnumerable<Row> ScanRows(string pairId, Guid scanId, Func<Row, RowContext> context);
    IReadOnlyList<RenameCandidate> ScanRenames(string pairId, Guid scanId);
    /// <summary>This run's scan entry on that side whose own key is that key; null when absent (Repair: the local
    /// entry of an interrupted case rename, matched by id by the caller).</summary>
    Entry? ScanEntry(string pairId, Guid scanId, Side side, string key);
    /// <summary>Whether this run's listing of that side holds the join key (absent = arrived later).</summary>
    bool InScan(string pairId, Guid scanId, Side side, string key);

    /// <summary>Answers a repeated name-clash refusal for one member without writing: Covered and Attach carry the
    /// continuing open issue at the path; Raise carries none. Stale selections throw.</summary>
    (ClashRefusal State, Issue? Existing) ClashRefusalState(WorkSelection selection, string path,
        IssueSubject subject, (ChangeNotice Notice, bool IsFolder, SyncedEntry? Before) work);
    /// <summary>Completes the selection without observation when its anchor key and every member's paths, saved
    /// bindings and mapped counterparts lie under exclusions or nested roots and it owes nothing else (rename custody,
    /// residual, discovery, Local dirty evidence, a recorded protecting member at or under a Local member path, issue or
    /// parent ownership); returns whether it did. Ask it after pending ancestors are resolved. Stale selections throw.
    /// </summary>
    bool CompleteOutOfScope(WorkSelection selection, PathScope scope);
}
