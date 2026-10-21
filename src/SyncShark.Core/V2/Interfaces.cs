namespace Massdex.SyncShark.V2;

/// <summary>One stored deletion-evidence probe result: the server deletion time, or null for "no evidence".</summary>
public sealed record DeletionEvidenceRecord(string Path, DateTimeOffset? DeletedUtc, string? BasisCursor = null,
    string? ContentHash = null, DeletionEvidenceFailure? Failure = null)
{
    // Decision 78: legacy positive evidence without a revision hash needs a fresh history probe.
    // Negative evidence (never existed) requires no hash.
    public bool HasRequiredContentHash => DeletedUtc is null || ContentHash is not null;
}

/// <summary>Failed delta history reads are not negative evidence; retained independently of cache validation.</summary>
public sealed record DeletionEvidenceFailure(int Attempts, DateTimeOffset FirstUtc, long Generation, bool Released);

/// <summary>One bounded page of intents plus the exclusive keyset cursor for the next page.</summary>
public sealed record IntentPage(IReadOnlyList<SyncAction> Items, string? NextCursor);

/// <summary>One unresolved intent with the state it was streamed with, so a scan need not re-read it per row.</summary>
public sealed record IntentRow(SyncAction Action, string State);

/// <summary>One bounded page of unresolved intent rows in rowid order, plus the next exclusive cursor.</summary>
public sealed record IntentRowsPage(IReadOnlyList<IntentRow> Items, string? NextCursor);

/// <summary>The only place that reads and writes the sync database.</summary>
public interface IStateStore : IDisposable
{
    long BeginGeneration(string rootId, string kind);
    void WriteSnapshot(Snapshot s);
    void WriteDelta(Delta d);
    void CompleteGeneration(long gen, string cursor, bool complete);
    string? Cursor(string rootId);

    /// <summary>Clears the root's stored cursor so its next scan is a full scan; baselines and live rows stay.</summary>
    void ForgetCursor(string rootId);

    /// <summary>Clear an explicit hold only on its exact retained entity; scans never clear it.</summary>
    bool ClearQuarantine(string rootId, ItemState item) => false;
    void HoldQuarantine(string rootId, ItemState item) { }
    bool QuarantineRecoveryRetained(string pairId, string noticeId) => false;
    IEnumerable<ItemState> QuarantineBaselineItems(string rootId, IReadOnlyList<string>? coveredPaths = null) => [];
    IReadOnlySet<string> DeferDefenderHistory(IReadOnlyList<string> objects, string run, DateTimeOffset now) => new HashSet<string>();
    void ClearDefenderDeferrals(IReadOnlyList<string> objects) { }
    /// <summary>Pending rows only: evidence released them; the rows stay queued until the deletion settles.</summary>
    void ReleaseDefenderDeferrals(IReadOnlyList<string> objects) { }
    IReadOnlyList<string> DefenderDeferredPaths(string rootId, int limit) => [];
    IReadOnlyDictionary<string, DefenderDeferralState> DefenderDeferrals(IReadOnlyList<string> objects) =>
        new Dictionary<string, DefenderDeferralState>();
    IReadOnlyDictionary<string, DefenderConsent> DefenderDeferralConsent(IReadOnlyList<string> objects) =>
        DefenderDeferrals(objects).ToDictionary(row => row.Key, row => row.Value.Consent);
    void AssignDefenderBatch(IReadOnlyList<string> objects, string batch) { }
    long DefenderBatchCount(string batch) => 0;
    /// <summary>Proceed or Keep for exactly one reviewed batch; Keep moves its rows to the root's held batch.</summary>
    void DecideDefenderBatch(string batch, bool proceed, string rootId) { }
    /// <summary>Moves a closed review's still-pending rows into <paramref name="batch"/> (null: the next review).</summary>
    void ReopenDefenderBatch(string closedBatch, string? batch) { }
    IReadOnlyList<Notice> DefenderNotices(string pairId, string rootId) =>
        OpenNotices(pairId).Where(n => n.DefenderHistory?.RootId == rootId).ToArray();
    long DefenderHeldCount(string rootId, string? pairId = null) => 0;
    IReadOnlyList<string> DefenderHeldPaths(string rootId, int limit, string? pairId = null) => [];
    /// <summary>Releases one Keep-held entity of this root, or all of them when <paramref name="objectId"/> is null.</summary>
    int ReleaseDefenderHeld(string rootId, string? objectId) => 0;
    /// <summary>Forgets one Keep-held entity once the user excluded its path from sync.</summary>
    bool ForgetDefenderHeld(string rootId, string objectId) => false;
    bool AcceptDefenderGrant(DefenderGrant expected, ItemState local, ItemState? cloud) => false;
    void StoreDefenderGrant(DefenderGrant grant) { }
    DefenderGrant? DefenderGrantConflict(string pairId, SyncAction action) => null;
    DefenderGrant? DefenderGrantById(string id) => null;
    IReadOnlyList<string> DefenderGrantHoldPaths(string rootId) => [];
    bool RetireDefenderGrant(DefenderGrant expected) => false;

    /// <summary>Whether this pair's first sync has been reviewed and applied.</summary>
    bool FirstSyncDone(string pairId);

    /// <summary>Marks the pair's first sync done; idempotent, throws for an unknown pair.</summary>
    void MarkFirstSyncDone(string pairId);

    IReadOnlyList<ItemState> Live(string rootId, IEnumerable<string>? paths = null);

    /// <summary>Live rows at a prefix or under it; the Executor expands a folder Delete with this.</summary>
    IReadOnlyList<ItemState> LiveUnder(string rootId, IEnumerable<string> pathPrefixes);

    /// <summary>Direct children only; production uses the directory index, never a subtree inventory.</summary>
    IReadOnlyList<ItemState> LiveChildren(string rootId, string directory);

    /// <summary>
    /// Bounded folder paths under a prefix whose leaf name is one of <paramref name="names"/> (an empty list means
    /// any folder). This is a preview of candidates for a rebuildable rule; it never loads a subtree.
    /// </summary>
    IReadOnlyList<string> RebuildableFoldersUnder(string rootId, string prefix, IReadOnlyList<string> names,
        int offset, int limit)
    {
        var rows = prefix.Length == 0 ? Live(rootId) : LiveUnder(rootId, [prefix]);
        return rows.Where(item => item.Kind == ItemKind.Folder &&
                (names.Count == 0 || names.Contains(item.Path[(item.Path.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase)))
            .Select(item => item.Path).OrderBy(path => path, StringComparer.Ordinal)
            .Skip(offset).Take(limit).ToArray();
    }

    /// <summary>Bounded direct-child page. Persistent stores must limit rows before decoding.</summary>
    IReadOnlyList<ItemState> LiveChildrenPage(string rootId, string directory, int offset, int limit) =>
        LiveChildren(rootId, directory).OrderBy(item => item.Path, StringComparer.Ordinal).Skip(offset).Take(limit).ToArray();

    /// <summary>Bounded flagged-item page for catalog backfill.</summary>
    /// <param name="afterDirNo">The cursor item's stored folder number, so a vanished folder still seeks from its key.</param>
    IReadOnlyList<ItemState> LiveFlaggedPage(string rootId, ItemFlags flags, ItemState? after, int limit, long? afterDirNo = null)
    {
        var rows = LiveFlagged(rootId, flags);
        return (after is null ? rows : rows.SkipWhile(item => item.Path != after.Path).Skip(1)).Take(limit).ToArray();
    }

    /// <summary>The stored folder number for a path, when the store keeps one; null permits a path lookup fallback.</summary>
    long? DirectoryNumber(string rootId, string path) => null;

    /// <summary>Exact directory aliases through indexed keys, never ancestor children or subtrees.</summary>
    IReadOnlyList<string> DirectorySpellings(string rootId, string path);
    /// <summary>Whether a narrowed reviewed folder effect would carry newly protected content.</summary>
    bool AnyLiveIgnoredUnder(string rootId, string path, IReadOnlyList<SyncIgnoreRule> rules) =>
        rules.Count != 0 && LiveUnder(rootId, [path]).Any(item =>
            SyncIgnorePolicy.IsIgnored(item.Path, item.Kind == ItemKind.Folder, rules));


    /// <summary>
    /// Every live spelling of one path, matched ignoring case (more than one only for a case clash); the Dropbox
    /// inventory reads it to name a deleted path's other spellings without loading their subtrees.
    /// </summary>
    IReadOnlyList<ItemState> LiveSpellings(string rootId, string path) =>
        LiveUnder(rootId, [path]).Where(item => PairPath.Same(item.Path, path)).ToArray();

    /// <summary>Live rows of a root carrying any of the given flags (Run reads the links of decision 59 with this).</summary>
    IReadOnlyList<ItemState> LiveFlagged(string rootId, ItemFlags flags) =>
        Live(rootId).Where(item => (item.Flags & flags) != 0).ToArray();

    /// <summary>Bounded flag-only path snapshot; null requires bounded batch protection resolution.</summary>
    PathProtectionIndex? ProtectionIndex(string rootId, ItemFlags flags, int pathLimit = PathProtectionIndex.DefaultPathLimit,
        int characterLimit = PathProtectionIndex.DefaultCharacterLimit) =>
        PathProtectionIndex.TryCreate(LiveFlagged(rootId, flags).Select(item => item.Path), pathLimit, characterLimit);

    /// <summary>Resolve one bounded consumer batch; answers preserve input order.</summary>
    IReadOnlyList<ProtectionMatch> ProtectionBatch(string rootId, ItemFlags flags, IReadOnlyList<string> paths)
    {
        if (paths.Count > 1024) throw new ArgumentOutOfRangeException(nameof(paths));
        var held = LiveFlagged(rootId, flags).Select(item => item.Path).ToArray();
        return paths.Select(path => new ProtectionMatch(
            path.Length > 0 && held.Any(item => PairPath.IsSelfOrUnder(path, item)),
            held.Any(item => PairPath.IsSelfOrUnder(item, path)))).ToArray();
    }

    /// <summary>Only live folders carrying any requested flags; persistent stores filter before decoding rows.</summary>
    IReadOnlyList<ItemState> LiveFlaggedFolders(string rootId, ItemFlags flags) =>
        LiveFlagged(rootId, flags).Where(item => item.Kind == ItemKind.Folder).ToArray();

    /// <summary>
    /// Total live rows carrying any requested flags plus at most limit examples, in unspecified order.
    /// A nonpositive limit requests only the count. Persistent stores count and limit in the database.
    /// </summary>
    (long Count, IReadOnlyList<ItemState> Examples) LiveFlaggedSummary(string rootId, ItemFlags flags, int limit)
    {
        long count = 0;
        var examples = new List<ItemState>();
        foreach (var item in LiveFlagged(rootId, flags))
        {
            count++;
            if (examples.Count < limit) examples.Add(item);
        }
        return (count, examples);
    }

    ItemState? ByObject(string rootId, string objectId);

    /// <summary>Whether an object has multiple live names; persistent stores stop at the second indexed row.</summary>
    bool HasMultipleNames(string rootId, string objectId) =>
        Live(rootId).Where(item => item.ObjectId == objectId).Take(2).Count() > 1;

    /// <summary>Fair bounded retry page for local item-owned hash debt.</summary>
    IReadOnlyList<ItemState> RehashCandidates(string rootId, int limit) =>
        LiveFlagged(rootId, ItemFlags.NeedsRehash).Take(limit).ToArray();

    /// <summary>Captures the retry pass boundary; zero means this root has no pending pass.</summary>
    long RehashWatermark(string rootId);
    /// <summary>Acknowledge verified queue obligations only after all their current aliases were settled.</summary>
    void AcknowledgeRehash(string rootId, long through, Func<string, bool> settled, CancellationToken ct = default);
    IReadOnlyList<ItemState> RehashCandidates(string rootId, int limit, long through);
    IReadOnlyList<string> ObjectPaths(string rootId, string objectId, int offset, int limit) =>
        Live(rootId).Where(item => item.ObjectId == objectId).Select(item => item.Path).Skip(offset).Take(limit).ToArray();

    /// <summary>Persist a hash retry and its diagnostic before the journal cursor can advance.</summary>
    void RequireRehash(string rootId, ItemState item, string reason);

    /// <summary>Persist debt in bounded transactions before advancing a covered journal interval.</summary>
    void RequireRehashBatch(string rootId, IEnumerable<ItemState> items, string reason)
    {
        foreach (var item in items) RequireRehash(rootId, item, reason);
    }

    /// <summary>Indexed path protection without materializing the pending set.</summary>
    bool RehashTouches(string rootId, string path, bool descendants) => LiveFlagged(rootId, ItemFlags.NeedsRehash)
        .Any(item => descendants ? PairPath.IsSelfOrUnder(item.Path, path) : PairPath.IsSelfOrUnder(path, item.Path));

    /// <summary>Bounded diagnostic examples for the standing rehash notice.</summary>
    IReadOnlyList<string> RehashReasons(string rootId, int limit) =>
        LiveFlagged(rootId, ItemFlags.NeedsRehash).Take(limit).Select(item => item.Path + ": content verification pending").ToArray();

    /// <summary>Atomically publish validated content and retire only its exact entity's hash debt.</summary>
    bool CompleteRehash(string rootId, ItemState expected, string hash);

    /// <summary>All pairs, ordered by pair id.</summary>
    IReadOnlyList<PairDef> Pairs();

    /// <summary>The pair with this id, or null when unknown.</summary>
    PairDef? Pair(string pairId);

    /// <summary>Adds or replaces a pair, keeping its first-sync-done marker.</summary>
    void SavePair(PairDef p);

    /// <summary>Atomically widens selection and forgets only newly included scopes' baselines/deletion evidence.
    /// Keeps first-sync completion, other scopes and endpoint content; resets inventory coverage.</summary>
    int SavePairIncluded(PairDef previous, PairDef included) =>
        throw new NotSupportedException("This store cannot safely re-include folders. No selection was changed.");

    IReadOnlyList<Baseline> Baselines(string pairId, IEnumerable<string>? objectKeys = null);

    /// <summary>Baselines at exact side paths, via indexed point lookups; never includes descendants.</summary>
    IReadOnlyList<Baseline> BaselinesAt(string pairId, Side side, IEnumerable<string> paths);

    /// <summary>Baselines whose <paramref name="side"/> object id is listed or whose side path is on a prefix.</summary>
    IReadOnlyList<Baseline> BaselinesTouching(string pairId, Side side, IEnumerable<string> objectIds,
        IEnumerable<string> pathPrefixes);

    void CommitResult(string pairId, long gen, SyncAction a, ActionResult r);
    void RecordIntent(string pairId, SyncAction a);

    IEnumerable<SyncRetry> ReadSyncRetries(string pairId);
    IEnumerable<string> ReadSyncRetryPaths(string pairId) => ReadSyncRetries(pairId).Select(row => row.Path);
    bool SyncRetryExists(string pairId, string path) => SyncRetryAt(pairId, path) is not null;
    void SaveSyncRetry(string pairId, SyncRetry retry);
    void ClearSyncRetry(string pairId, string path);
    void RefreshInterruptedDeleteNotice(string pairId) { }
    void RefreshBlockedDeleteNotice(string pairId) { }
    BlockedDeletePage ReadBlockedDeletes(string pairId, string? after = null, int limit = 50) => new([], null);
    DeleteNoticePage ReadDeleteNoticeItems(string pairId, bool review, string? after = null, int limit = 50) => new([], null);
    IReadOnlyList<SyncRetry> InterruptedDeleteRepairsAt(string pairId, IReadOnlyCollection<string> paths) =>
        paths.Select(path => SyncRetryAt(pairId, path)).OfType<SyncRetry>()
            .Where(retry => retry.Kind == SqliteStateStore.InterruptedDeleteRestoreKind).ToArray();
    void RequestSyncRetryNow(string pairId, string path)
    {
        if (SyncRetryAt(pairId, path) is { } retry) SaveSyncRetry(pairId, retry with { RetryAfter = DateTimeOffset.MinValue });
    }
    void SuspendHeldRetry(string pairId, SyncRetry retry);
    SyncRetry? SyncRetryAt(string pairId, string path);
    IReadOnlyList<SyncRetry> DueSyncRetries(string pairId, DateTimeOffset now, int limit);
    int CountSyncRetries(string pairId);
    bool RetryProtected(string pairId, string path, IReadOnlySet<string> active, bool descendants, bool restoredOnly = false);

    /// <summary>Whether any restored evidence hold is still active (not dormant). One check per run, never per item.</summary>
    bool HasActiveRestoredRetries(string pairId) =>
        ReadSyncRetries(pairId).Any(retry => retry.ReprobeEvidence && retry.RetryAfter < DateTimeOffset.MaxValue);

    /// <summary>Several results in one transaction (the Executor's group commit, B-124); the default commits each.</summary>
    void CommitResults(string pairId, long gen, IReadOnlyList<(SyncAction Action, ActionResult Result)> results)
    {
        foreach (var (action, result) in results) CommitResult(pairId, gen, action, result);
    }

    /// <summary>Several intents in one transaction (the Executor's group commit, B-124); the default records each.</summary>
    void RecordIntents(string pairId, IReadOnlyList<SyncAction> actions)
    {
        foreach (var action in actions) RecordIntent(pairId, action);
    }

    /// <summary>Intents still in play: those whose last outcome was started, uncertain, failed or temporarily held.</summary>
    IReadOnlyList<SyncAction> UnresolvedIntents(string pairId);

    /// <summary>How many intents are unresolved (started, uncertain, failed or held); indexed and never materialized.</summary>
    int CountUnresolvedIntents(string pairId) => UnresolvedIntents(pairId).Count;

    /// <summary>
    /// Classifies one intent as temporarily held for verification, or returns a held one to uncertain. Holding never
    /// touches attempts or the update time, and an unknown/removed id is ignored rather than reinserted.
    /// </summary>
    void SetIntentHeld(string pairId, string actionId, bool held) { }

    /// <summary>Classifies several intents in one call; stores may reuse one transaction per bounded batch.</summary>
    void SetIntentsHeld(string pairId, IReadOnlyList<string> actionIds, bool held)
    {
        foreach (var actionId in actionIds) SetIntentHeld(pairId, actionId, held);
    }

    /// <summary>How many of the pair's intents are held for background verification; indexed, never materialized.</summary>
    int CountHeldIntents(string pairId) => 0;

    /// <summary>Whether this exact intent is currently held; a point lookup, false for an unknown or other pair's id.</summary>
    bool IsIntentHeld(string pairId, string actionId) => false;

    /// <summary>One bounded held-intent page, ordered by (updated, rowid); a null cursor starts at the first page.</summary>
    IntentPage HeldIntentsPage(string pairId, string? cursor, int limit) => new([], null);

    /// <summary>All unresolved intents, lazily paged in (updated, rowid) order so mutations cannot reorder the scan.</summary>
    IEnumerable<SyncAction> StreamUnresolvedIntents(string pairId) => UnresolvedIntents(pairId);

    /// <summary>
    /// All unresolved intents with their stored state, lazily paged so a run streams a million intents in bounded
    /// pages and does not point-query each row's state. A store without state pages reports the empty state.
    /// </summary>
    IEnumerable<IntentRow> StreamUnresolvedIntentRows(string pairId) =>
        StreamUnresolvedIntents(pairId).Select(action => new IntentRow(action, string.Empty));

    /// <summary>
    /// The pair that owns this exact intent, by an indexed primary-key lookup where the store can provide one.
    /// Reconciliation calls this per intent; the fallback scans pairs only for small custom stores.
    /// </summary>
    string? PairForIntent(string actionId) =>
        Pairs().FirstOrDefault(pair => UnresolvedIntents(pair.PairId)
            .Any(action => string.Equals(action.ActionId, actionId, StringComparison.Ordinal)))?.PairId;

    void AddNotices(IEnumerable<Notice> n);
    // Persistent stores override this to page reads and notice writes under one transaction.
    void RefreshProviderNameNotices(PairDef pair)
    {
        foreach (var notice in ProviderNameNotices.Read(pair,
            (root, after) => LiveFlaggedSummary(root.RootId, ItemFlags.ProviderNameRefused, 0).Count == 0
                ? Array.Empty<ItemState>() : LiveFlaggedPage(root.RootId, ItemFlags.ProviderNameRefused, after, 100)))
            AddNotices([notice]);
    }
    IReadOnlyList<Notice> OpenNotices(string? pairId);

    /// <summary>Open link-item notices only; persistent stores filter this in SQL so a page never loads every notice.</summary>
    IReadOnlyList<Notice> OpenLinkNotices(string pairId) =>
        OpenNotices(pairId).Where(notice => notice.ReasonKey == "link-item").ToArray();

    /// <summary>Recovery copies recorded for this pair; the Recovery owner restores or expires them.</summary>
    IReadOnlyList<RecoveryRecord> Recoveries(string pairId);

    // Production implementations use primary-key reads; defaults support small custom stores.
    RecoveryRecord? RecoveryForAction(string pairId, string actionId) =>
        Recoveries(pairId).FirstOrDefault(row => row.ActionId == actionId);
    /// <summary>Finds a retained baseline by its entity path and exact content, without loading Recovery history.</summary>
    RecoveryRecord? RecoveryForVersion(string pairId, Side side, ItemState version) => null;
    SyncAction? IntentById(string pairId, string actionId) =>
        UnresolvedIntents(pairId).FirstOrDefault(row => row.ActionId == actionId);
    (Notice Notice, bool Closed)? NoticeById(string pairId, string noticeId) =>
        OpenNotices(pairId).FirstOrDefault(row => row.NoticeId == noticeId) is { } notice ? (notice, false) : null;

    /// <summary>Closes a notice with the user's choice recorded on it.</summary>
    void CloseNotice(string noticeId, string? choice);

    /// <summary>Durably checkpoints an explicit conflict choice before any effect; implementations must not discard it.</summary>
    void SaveConflictOperation(string pairId, string noticeId, ConflictNoticeOperation operation) =>
        throw new NotSupportedException("This store cannot checkpoint conflict choices; no files were changed.");

    /// <summary>
    /// Freezes, once and inside SQLite, every live item strictly under a folder that an explicit choice removes. A
    /// second call for the same operation keeps the first snapshot. Returns the frozen item count.
    /// </summary>
    long FreezeChoiceVictims(string pairId, string operationId, string rootId, string folderPath) =>
        throw new NotSupportedException("This store cannot freeze folder contents; no files were changed.");

    /// <summary>One bounded page of an operation's frozen items in a state, deepest first or shallowest first.</summary>
    IReadOnlyList<(long Ord, ItemState Item)> ChoiceVictims(string operationId, ChoiceVictimState state, int limit,
        bool deepestFirst, long afterOrd = 0, bool ordinalOrder = false) => throw new NotSupportedException("This store has no frozen folder contents.");

    /// <summary>Records the outcome of frozen items in one transaction.</summary>
    void MarkChoiceVictims(string operationId, IReadOnlyList<(long Ord, ChoiceVictimState State)> marks) =>
        throw new NotSupportedException("This store has no frozen folder contents.");

    /// <summary>The count of an operation's frozen items in one state.</summary>
    long CountChoiceVictims(string operationId, ChoiceVictimState state) =>
        throw new NotSupportedException("This store has no frozen folder contents.");

    /// <summary>
    /// How many live items under the folder (up to 1000) are not in the operation's frozen snapshot: growth since
    /// the choice, found by one SQLite query without listing the subtree in memory.
    /// </summary>
    long CountChoiceGrowth(string operationId, string rootId, string folderPath) =>
        throw new NotSupportedException("This store has no frozen folder contents.");

    /// <summary>Indexed existence check for refused items or retained counterparts at, above, or below a removal.</summary>
    bool ChoiceRemovalProtected(PairDef pair, Side side, string path, bool folder) =>
        throw new NotSupportedException("This store cannot check folder choice protections.");

    bool HasSyncableChoiceGrowth(string operationId, string rootId, string folderPath, Func<ItemState, bool> syncable) =>
        CountChoiceGrowth(operationId, rootId, folderPath) != 0;
    bool VerifyCarriedChoice(string pairId, string operationId, ItemState current) => false;
    IReadOnlyList<bool> ChoiceRemovalProtectedBatch(PairDef pair, Side side, IReadOnlyList<ItemState> items) =>
        items.Select(item => ChoiceRemovalProtected(pair, side, item.Path, item.Kind == ItemKind.Folder)).ToArray();
    bool HasChoiceSnapshot(string operationId) => false;
    bool ChoiceCarriedChanged(string operationId, string rootId) => true;
    bool ConflictChoiceHasPending(string pairId, string operationId) => UnresolvedIntents(pairId)
        .Any(action => action.ActionId == operationId || action.ActionId.StartsWith(operationId + ":", StringComparison.Ordinal));
    IReadOnlyList<SyncAction> RestoreIntentsPage(string pairId, int limit) =>
        UnresolvedIntents(pairId).Where(action => action.ConflictRestore).Take(limit).ToArray();
    void ForgetRestoreReceipts(string pairId, IReadOnlyList<string> actionIds) { }
    byte[]? ConflictMergeBytes(string noticeId) => null;
    void ForgetConflictPayload(string noticeId) { }
    ItemState? ConflictRestoreReceipt(string pairId, string actionId) => null;

    /// <summary>Drops an ended operation's snapshot in bounded batches; Recovery records are untouched.</summary>
    void ForgetChoiceVictims(string operationId) { }
    /// <summary>Atomically reopens the accepted notice and appends a choice failure to its original detail.</summary>
    void RecordNoticeChoiceFailure(Notice original, string failure);

    /// <summary>Closes/resets one pair's notice family, including ignored occurrences after recovery.
    /// Production performs an indexed SQL update, not a load of the pair's item notices.</summary>
    void CloseNoticeFamily(string pairId, string reasonPrefix, string? exceptReason = null)
    {
        foreach (var notice in OpenNotices(pairId).Where(n => n.ReasonKey?.StartsWith(reasonPrefix, StringComparison.Ordinal) == true && n.ReasonKey != exceptReason))
            if (notice.NoticeId is { } id) CloseNotice(id, "resolved");
    }

    /// <summary>Retains one finished run's bounded summary; a repeated run id is idempotent.</summary>
    void RecordRunSummary(RunSummary summary) { }

    /// <summary>Retained run summaries, newest first; <paramref name="before"/> is an exclusive sequence cursor (0 = newest).</summary>
    IReadOnlyList<RunSummary> ReadRunHistory(string? pairId = null, int limit = 50, long before = 0) => [];

    /// <summary>Retained run summaries, oldest first, after a sequence cursor (0 = the oldest retained row).</summary>
    IReadOnlyList<RunSummary> ReadRunHistoryAfter(long after, int limit = 50) => [];

    /// <summary>A newest-first page positioned by ordinal offset, for the window's Load More paging.</summary>
    IReadOnlyList<RunSummary> ReadRunHistoryPage(string? pairId, int limit, int offset) => [];

    /// <summary>One retained summary by its run id, or null when it is unknown or has expired.</summary>
    RunSummary? RunSummaryById(string runId) => null;

    void Prune();

    /// <summary>Complete root/path tombstone classification; missing is not an empty classification.</summary>
    DeletionClassification? ReadDeletionClassification(RootDef root) => null;
    void ReplaceDeletionClassification(RootDef root, string cursor, IReadOnlySet<string> exact,
        IReadOnlySet<string> ancestors) { }
    void ReplayDeletionClassification(RootDef root, string cursor, IEnumerable<(string Path, bool Deleted)> changes) { }

    /// <summary>Cursor-bound probes, oldest checkpoint first; null deletion time means proven "no evidence".</summary>
    IReadOnlyList<DeletionEvidenceRecord> DeletionEvidenceRows(string pairId, string rootId) => [];
    void RemoveDeletionEvidence(string pairId, string rootId, IReadOnlyList<string> paths) { }

    /// <summary>Stores a durable checkpoint of completed probes; a later probe replaces an earlier row.</summary>
    void StoreDeletionEvidence(string pairId, string rootId, IReadOnlyList<DeletionEvidenceRecord> results) { }
}

public sealed record DeletionClassification(string Cursor, IReadOnlySet<string> Exact, IReadOnlySet<string> Ancestors);

/// <summary>Reads one root's complete or incremental inventory.</summary>
public interface IInventorySource
{
    /// <summary>Read only this folder entity and descendants, without advancing any root cursor.</summary>
    Task ReadSubtreeAsync(string path, Func<IReadOnlyList<ItemState>, Task> accept, CancellationToken ct,
        IProgress<string>? progress = null) =>
        throw new NotSupportedException("Subtree listing not implemented; a whole-root scan will not be used for removal review.");

    /// <summary>After reconciliation is stored, verify pending content in bounded pages and report affected paths.</summary>
    Task VerifyPendingAsync(Func<string, bool> include, Action<string> touched, CancellationToken ct) => Task.CompletedTask;

    Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct);
    Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct);

    /// <summary>
    /// Proven deletion times, from this side's own history, for paths it no longer holds; a path without proof is
    /// left out. A first-sync preview reads these on every preview, since it persists no earlier scan (decision 4).
    /// </summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths, CancellationToken ct);
}

/// <summary>Applies planned effects to one endpoint.</summary>
/// <remarks>
/// Writer contract (change 6): every effect is conditional on <see cref="SyncAction.ExpectedTarget"/> — PutAsync/MoveAsync
/// require the item at the action's path (Move: FromPath) to match its VersionId and Hash and the destination to be free,
/// else <see cref="ExpectedTargetMismatchException"/> (that type also means the source changed since planning, so nothing
/// was sent); DeleteAsync requires the same match. OpenReadAsync returns exactly the requested version or throws
/// <see cref="ExpectedTargetMismatchException"/> when the source changed since planning. PreserveAsync is idempotent per
/// action id: a second call returns the first <see cref="RecoveryRef"/> and never archives twice.
/// </remarks>
public interface IEndpointWriter
{
    Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct);
    Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct);
    Task DeleteAsync(SyncAction a, CancellationToken ct);
    Task<ItemState> MkDirAsync(string path, CancellationToken ct);
    Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct);
    Task<ItemState?> ObserveAsync(string path, CancellationToken ct);
    Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct);
}

/// <summary>
/// A source endpoint that can write one exact planned revision directly into a caller's stream instead of
/// materialising it in a product spool first. The bytes are verified against the planned revision, content
/// hash and size while they are written.
/// </summary>
public interface ISyncDirectDownloadSource
{
    Task<SyncDownloadResult> DownloadToAsync(ItemState s, Stream destination, CancellationToken ct);
}

/// <summary>One verified download stage in the destination directory, before it is published.</summary>
public sealed record SyncDownloadStage(string Path, string ObjectId, string RecoveryPath, bool Verified = true);

/// <summary>
/// A target endpoint that stages a remote revision in its own temporary file beside the destination, verifies
/// it, and publishes it with an atomic create-only rename. No second copy on the target volume is made.
/// </summary>
public interface ISyncDirectDownloadTarget
{
    /// <summary>
    /// Stages and verifies the download, then invokes <paramref name="preservedAfterVerify"/> (when supplied) before
    /// moving any original. The callback journals creation before bytes arrive and verification before any Recovery step.
    /// </summary>
    Task<ItemState> PutDownloadedAsync(SyncAction a, ISyncDirectDownloadSource source,
        Func<SyncDownloadStage, CancellationToken, Task>? preservedAfterVerify, CancellationToken ct);

    /// <summary>
    /// Finishes or rolls back one interrupted direct download whose durable checkpoint is still open. It never
    /// overwrites a present divergent target; anything unproven returns <see cref="Outcome.Uncertain"/>, so the run
    /// gate keeps the path out of planning.
    /// </summary>
    Task<ActionResult> ResumeDownloadAsync(SyncAction action, ProviderEffectCheckpoint checkpoint,
        CancellationToken ct);
}

/// <summary>
/// The narrow durable evidence one direct download writes after verification and before it archives the original:
/// the verified <see cref="SyncDownloadStage"/>, carrying the stage path, object identity and Recovery locator.
/// The action itself already binds the item path, source and target.
/// </summary>
public static class SyncDirectDownloadCheckpoint
{
    public const string Kind = "local-download";
    internal const string NoticePrefix = "Download recovery: ";
    internal const string DateNoticePrefix = "Download date: ";
    internal static Notice? NoticeFor(string pairId, SyncAction action, ActionResult result) =>
        result.Reason?.StartsWith(DateNoticePrefix, StringComparison.Ordinal) == true
            ? new Notice(pairId, action.Rule, NoticeKind.Info, action.Path, result.Reason, "local-download-date") :
        result.Reason?.StartsWith(NoticePrefix, StringComparison.Ordinal) == true
            ? new Notice(pairId, action.Rule, NoticeKind.Info, action.Path, result.Reason, "local-download-recovery")
            : null;
}

/// <summary>Applies and reconciles plans against both endpoints.</summary>
public interface IExecutor
{
    IAsyncEnumerable<ActionResult> ApplyAsync(Plan p, IEndpointWriter a, IEndpointWriter b, CancellationToken ct);

    /// <summary>
    /// Resolves recorded intents by observation, committing each outcome against <paramref name="gen"/>. Intents are
    /// independent, so one implementation may observe several at once; <paramref name="progress"/> reports the number
    /// whose observation and durable commit have completed (never a forecast).
    /// </summary>
    Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen, IEndpointWriter a,
        IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null);
}

/// <summary>Progress of a run as it moves through phases.</summary>
public sealed record RunProgress(string Phase, string? Path, long Done, long Total)
{
    /// <summary>Latest throughput measurements, or null when instrumentation is disabled.</summary>
    public RunDiagnostics? Diagnostics { get; init; }

    /// <summary>
    /// Finished results outside <see cref="Done"/>/<see cref="Total"/> — the Executor's own child deletes of an
    /// expanded folder delete — shown as a separate suffix so the planned done never overruns the total.
    /// </summary>
    public long Extra { get; init; }

    /// <summary>
    /// A whole line that is not a phase/bytes report — a provider process message (a retry, a cooldown, a listing
    /// page) or the run's heartbeat — which a sink prints verbatim instead of "phase: path (done/total)". Null on
    /// ordinary progress reports, so a sink that ignores it keeps its existing output.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>The outcome summary of one run, with per-direction action counts.</summary>
public sealed record RunReport(string PairId, string Outcome, int Done, int Failed, int Held, int Uploaded,
    int Downloaded, int Deleted, int Moved, int KeptBoth, IReadOnlyList<ActionResult> Failures)
{
    /// <summary>Inventory/planning reached the provider; individual effect failures have their own notices.</summary>
    public bool ProviderReached { get; init; }

    /// <summary>Effects whose commit could not be confirmed, including those outside the failure sample.</summary>
    public int Uncertain { get; init; }

    /// <summary>Pending intents protected by temporary verification/evidence holds, not failed effects.</summary>
    public int PendingVerification { get; init; }

    /// <summary>Total failed or unconfirmed effects, independent of the retained examples.</summary>
    public int FailureCount => Failed + Uncertain;

    /// <summary>The requested decision's failure explanation, even when it falls outside the example sample.</summary>
    public string? DecisionFailureReason { get; init; }

    /// <summary>First-sync apply: reviewed effects not completed (dropped, held, failed, skipped); 0 = complete.</summary>
    public int Deferred { get; init; }

    public IReadOnlyList<string> DeletionEvidencePending { get; init; } = [];
    public int ReviewedDeletionEvidencePendingCount { get; init; }
    public int UnreviewedPending { get; init; }

    /// <summary>Distinct paths awaiting content confirmation, including any also awaiting deletion evidence.</summary>
    public int ContentUnconfirmedCount { get; init; }

    /// <summary>At most fifty paths awaiting content confirmation.</summary>
    public IReadOnlyList<string> ContentUnconfirmedExamples { get; init; } = [];

    /// <summary>
    /// First-sync rebuildable paths whose cloud delete never reached the endpoint, so the first sync stays incomplete;
    /// each is left to ordinary planning and listed for the user.
    /// </summary>
    public IReadOnlyList<string> RebuildablesBlocked { get; init; } = [];

    /// <summary>Effects skipped because their reviewed preconditions no longer held.</summary>
    public int Skipped { get; init; }

    /// <summary>Display-only grouped pending space notices; durable retry state remains per path.</summary>
    public IReadOnlyList<FreeSpaceNoticeSummary> FreeSpacePending { get; init; } = [];

    /// <summary>Final throughput measurements, or null when instrumentation is disabled.</summary>
    public RunDiagnostics? Diagnostics { get; init; }

    /// <summary>The instant the run finished; the owner stamps it, so the edge can order two completions honestly.</summary>
    public DateTimeOffset CompletedUtc { get; init; }
}

/// <summary>Coordinates one pair's runs from inventory to commit.</summary>
public interface ISyncRun
{
    Task<Plan> PreviewFirstSyncAsync(string pairId, IProgress<RunProgress> p, CancellationToken ct);
    Task<RunReport> ApplyAsync(string pairId, Plan approved, IProgress<RunProgress> p, CancellationToken ct);
    Task<RunReport> IncrementalAsync(string pairId, IProgress<RunProgress> p, CancellationToken ct);
    Task StopAsync();
}

// Planner lives in V2/Planner/Planner.cs (one type per file).

/// <summary>Bounded fresh metadata observations for restore receipt verification.</summary>
public interface IEndpointMetadataPages
{
    Task<IReadOnlyDictionary<string, ItemState?>> ObservePageAsync(IReadOnlyList<string> paths, CancellationToken ct);
}
