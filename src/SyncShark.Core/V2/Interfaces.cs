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

    /// <summary>Whether this pair's first sync has been reviewed and applied.</summary>
    bool FirstSyncDone(string pairId);

    /// <summary>Marks the pair's first sync done; idempotent, throws for an unknown pair.</summary>
    void MarkFirstSyncDone(string pairId);

    IReadOnlyList<ItemState> Live(string rootId, IEnumerable<string>? paths = null);

    /// <summary>Live rows at a prefix or under it; the Executor expands a folder Delete with this.</summary>
    IReadOnlyList<ItemState> LiveUnder(string rootId, IEnumerable<string> pathPrefixes);

    /// <summary>Direct children only; production uses the directory index, never a subtree inventory.</summary>
    IReadOnlyList<ItemState> LiveChildren(string rootId, string directory);

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
    void SaveSyncRetry(string pairId, SyncRetry retry);
    void ClearSyncRetry(string pairId, string path);
    SyncRetry? SyncRetryAt(string pairId, string path);
    IReadOnlyList<SyncRetry> DueSyncRetries(string pairId, DateTimeOffset now, int limit);
    int CountSyncRetries(string pairId);
    bool RetryProtected(string pairId, string path, IReadOnlySet<string> active, bool descendants);

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

    /// <summary>Intents still in play: those whose last outcome was started, uncertain or failed.</summary>
    IReadOnlyList<SyncAction> UnresolvedIntents(string pairId);

    void AddNotices(IEnumerable<Notice> n);
    IReadOnlyList<Notice> OpenNotices(string? pairId);

    /// <summary>Recovery copies recorded for this pair; the Recovery owner restores or expires them.</summary>
    IReadOnlyList<RecoveryRecord> Recoveries(string pairId);

    // Production implementations use primary-key reads; defaults support small custom stores.
    RecoveryRecord? RecoveryForAction(string pairId, string actionId) =>
        Recoveries(pairId).FirstOrDefault(row => row.ActionId == actionId);
    SyncAction? IntentById(string pairId, string actionId) =>
        UnresolvedIntents(pairId).FirstOrDefault(row => row.ActionId == actionId);
    (Notice Notice, bool Closed)? NoticeById(string pairId, string noticeId) =>
        OpenNotices(pairId).FirstOrDefault(row => row.NoticeId == noticeId) is { } notice ? (notice, false) : null;

    /// <summary>Closes a notice with the user's choice recorded on it.</summary>
    void CloseNotice(string noticeId, string? choice);

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
    internal static Notice? NoticeFor(string pairId, SyncAction action, ActionResult result) =>
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
    /// <summary>Effects whose commit could not be confirmed, including those outside the failure sample.</summary>
    public int Uncertain { get; init; }

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
