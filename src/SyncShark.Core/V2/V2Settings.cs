namespace Massdex.SyncShark.V2;

/// <summary>Every v2 tunable in one immutable record; the defaults live in <see cref="Default"/>.</summary>
public sealed record V2Settings(
    int StoreBatchRows,
    int StoreCacheMb,
    TimeSpan StoreBusyTimeout,
    bool StoreDurableCommits,
    TimeSpan HistoryRetention,
    TimeSpan RecoveryRetention,
    int LocalDeltaMaxPaths,
    TimeSpan LocalDiscoveryTimeout,
    int LocalHashWorkers,
    int LocalHashBufferBytes,
    TimeSpan TieWindow,
    string KeepBothLabelFormat,
    string KeepBothTimeFormat,
    int MaxLabelAttempts,
    int MaxWorkers,
    TimeSpan AdmissionRetryDelay,
    TimeSpan ProgressInterval,
    TimeSpan ProgressDrainTimeout,
    TimeSpan ChangeQuietWindow,
    TimeSpan ChangeMaxDelay,
    TimeSpan DropboxLongpollTimeout,
    TimeSpan AutoRetryDelay,
    TimeSpan MinSafetyPoll)
{
    // Bounded provider pacing and read recovery; fixed defaults shared by the existing owners.
    internal const int ProviderThrottleMaximumRetries = 32;
    internal static readonly TimeSpan ProviderThrottleMaxBackoff = TimeSpan.FromMinutes(5);
    internal const int ProviderRecoveryIncrement = 8;
    internal static readonly TimeSpan ProviderRecoveryInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ProviderReacquireDelay = TimeSpan.FromMilliseconds(25);
    internal static readonly TimeSpan ProviderPauseCheckInterval = TimeSpan.FromSeconds(1);

    // Journal metadata admission bounds; no file-content reads occur under these leases.
    internal const int JournalRecordsPerPage = 256;
    internal static readonly TimeSpan JournalStoreSlice = TimeSpan.FromMilliseconds(200);
    internal const int ScanRetryScopeLimit = 256;

    // Diagnostic write batches bound commit latency without buffering a root-sized set.
    internal const int DefaultProvenanceBatchRows = 512;

    internal const long DefaultUploadPayloadBudgetBytes = 256L * 1024 * 1024;

    /// <summary>Maximum identities examined per rehash gate lease, including ineligible identities.</summary>
    public int StoreRehashPageRows { get; init; } = 128;
    internal const int MinimumStoreRehashPageRows = 1;

    /// <summary>Checkpoint payload rows per synchronous invalidation transaction.</summary>
    public int StoreWalkCleanupRows { get; init; } = 32;
    /// <summary>Maximum checkpoint payload rows per background reclamation transaction.</summary>
    public int StoreWalkReclaimRows { get; init; } = 1024;
    /// <summary>Background deletion slice; checked between indivisible payload deletions, before commit.</summary>
    public TimeSpan StoreWalkReclaimSlice { get; init; } = TimeSpan.FromMilliseconds(100);
    internal const int MinimumStoreWalkRows = 1;

    /// <summary>Maximum notices maintained or published per store gate lease.</summary>
    public int StoreNoticePageRows { get; init; } = 128;
    internal const int MinimumStoreNoticePageRows = 1;

    /// <summary>Metadata entities per gap-promotion transaction; a memory/gate bound, not a run limit.
    /// Promotion drains the captured discovery horizon, releasing the store gate after every page.</summary>
    public int GapPromotionPageRows { get; init; } = 64;

    /// <summary>Shared background gap-verification byte budget; never used for foreground transfers.</summary>
    public long LocalGapBytesPerSecond { get; init; } = 30_000_000;
    /// <summary>User-input quiet time required for background gap IO; battery operation also pauses it.</summary>
    public TimeSpan LocalGapIdleTime { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The single place every v2 default is defined.</summary>
    public static V2Settings Default { get; } = new(
        StoreBatchRows: 20_000,
        StoreCacheMb: 64,
        StoreBusyTimeout: TimeSpan.FromSeconds(5),
        // synchronous=FULL: a committed intent survives power loss (an effect never runs without one on disk).
        // Only disposable test databases turn it off, to keep the storage suite fast.
        StoreDurableCommits: true,
        HistoryRetention: TimeSpan.FromDays(30),
        RecoveryRetention: TimeSpan.FromDays(30),
        LocalDeltaMaxPaths: 10_000,
        LocalDiscoveryTimeout: TimeSpan.FromSeconds(10),
        // Local hashing is bound by per-file open/close, not bytes: on the measured folder (SSD) 4-16 parallel
        // readers ran 4-9x one reader; 8 is the measured knee. The read chunk size made no measured difference.
        LocalHashWorkers: 8,
        LocalHashBufferBytes: 128 * 1024,
        TieWindow: TimeSpan.FromSeconds(2),
        KeepBothLabelFormat: "{name} [{side} version {time}]{ext}",
        KeepBothTimeFormat: "d MMM HH.mm",
        MaxLabelAttempts: 10_000,
        MaxWorkers: 64,
        AdmissionRetryDelay: TimeSpan.FromMilliseconds(20),
        ProgressInterval: TimeSpan.FromMilliseconds(250),
        // How long a finished UI batch waits for the window to take its last progress report.
        ProgressDrainTimeout: TimeSpan.FromSeconds(10),
        // Continuous sync (continuous-sync.md section 4): quiet window is v1's; long-poll allows 30-480 s.
        ChangeQuietWindow: TimeSpan.FromMilliseconds(750),
        ChangeMaxDelay: TimeSpan.FromSeconds(30),
        DropboxLongpollTimeout: TimeSpan.FromSeconds(120),
        AutoRetryDelay: TimeSpan.FromSeconds(10),
        // Floor for the edge's safety-poll interval (v1's minimum sync interval), so a bad value cannot spin.
        MinSafetyPoll: TimeSpan.FromMinutes(1));

    /// <summary>
    /// Maximum retained run summaries . Retention also expires summaries older than
    /// <see cref="HistoryRetention"/>, so history never grows past this cap.
    /// </summary>
    public int RunSummaryMaximumRows { get; init; } = 10_000;

    /// <summary>
    /// How long a run's progress sink may stay silent before the run reports a "Still working" heartbeat (RQ-090).
    /// Zero disables the heartbeat.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the heartbeat checks whether the sink has been quiet long enough; default five seconds.</summary>
    public TimeSpan HeartbeatCheckInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the run's sweeper checks the in-flight operations for one that is taking too long (RQ-090).</summary>
    public TimeSpan OperationSweepInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the sweeper repeats a "Still waiting" line while one operation stays in flight.</summary>
    public TimeSpan OperationWarningRepeat { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long one provider HTTP request may stay in flight before the sweeper names its route (RQ-090).</summary>
    public TimeSpan SlowHttpThreshold { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long one upload/delete batch commit may stay in flight before the sweeper names it.</summary>
    public TimeSpan SlowBatchCommitThreshold { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long one local file read or hash may take before the sweeper names its path.</summary>
    public TimeSpan SlowLocalIoThreshold { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long one store call (its lock wait included) may take before the sweeper names it.</summary>
    public TimeSpan SlowStoreThreshold { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long one semaphore or gate wait may take before the sweeper names it.</summary>
    public TimeSpan SlowGateWaitThreshold { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// (planner rule 35): replace an online-only cloud-files stub with the other side's file. OFF by
    /// default until the stub-replacement safety fixes land; while off, every stub is left alone like a link (decision
    /// 59): never read or replaced, nothing at its path touched on either side, listed in a standing notice.
    /// </summary>
    public bool ReplaceOnlineOnlyStubs { get; init; }

    /// <summary>Collect per-run throughput measurements for RunReport and RunProgress without changing execution.</summary>
    public bool EnableRunInstrumentation { get; init; } = true;

    /// <summary>
    /// The free space every local write or Recovery copy must leave on its target volume (512 MiB). On the volume
    /// holding Sync Shark's own data folder the larger of this floor and the state database plus WAL size is kept
    /// free instead, so a nearly full disk cannot make the store itself unable to commit.
    /// </summary>
    public long FreeSpaceFloorBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>
    /// How long the state database and WAL sizes are reused before they are read again (10 seconds), so admission
    /// does not pay two file-size reads per transferred item.
    /// </summary>
    public TimeSpan FreeSpaceSizeCache { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Maximum files hashed before committing reusable live-row hashes.</summary>
    public int LocalHashCheckpointFiles { get; init; } = 2000;

    /// <summary>Total transient recovery budget for each read-only Dropbox request.</summary>
    public TimeSpan DropboxReadTransientBudget { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Listing-page outage recovery window (RQ-032/090); item reads keep the shorter read budget.</summary>
    public TimeSpan DropboxListingTransientBudget { get; init; } = TimeSpan.FromMinutes(45);

    /// <summary>Maximum failed or uncertain result examples retained by one run; totals remain exact.</summary>
    public int FailureSampleLimit { get; init; } = 200;

    /// <summary>Two worker waves of results per queue, allowing 64 transfers without unbounded retention.</summary>
    public int ResultBufferCapacity => checked(Math.Max(1, MaxWorkers) * 2);

    private int? _pendingUploadCommitLimit;

    /// <summary>
    /// Maximum staged effects awaiting commit, separate from active staging workers. The default holds the current
    /// batch plus the ones queued behind it — <see cref="UploadBatchSize"/> × (<see cref="UploadCommitConcurrency"/>
    /// + 2), i.e. 3000 at the shipped 1000/1 — so a running serial commit never starves staging workers of buffer and
    /// idles them for the whole commit. This is the single home for the number, derived from the two bounds it must
    /// stay ahead of; callers may still set an explicit cap.
    /// </summary>
    public int PendingUploadCommitLimit
    {
        get => _pendingUploadCommitLimit ?? UploadBatchSize * (UploadCommitConcurrency + 2);
        init => _pendingUploadCommitLimit = value;
    }
    /// <summary>
    /// The cap on entries in one upload finish batch (1..1000, the provider's finish_batch_v2 maximum); commits flush at
    /// this size or their oldest entry's maximum age. The default is the whole provider maximum: Dropbox's commit lock
    /// is namespace-wide, so one maximal batch at a time (<see cref="UploadCommitConcurrency"/>) is its recommended
    /// contention-free pattern. A large finish_batch_v2 holds its response while Dropbox commits every entry, so the
    /// explicit <see cref="BatchOperationTimeout"/> carries a per-entry allowance (see <see cref="BatchTimeoutPerEntry"/>).
    /// </summary>
    public int UploadBatchSize { get; init; } = 1000;
    /// <summary>
    /// Maximum overlapping upload commits per account namespace (1..4), shared across bound clients. One commit at a
    /// time is the default: the namespace commit lock is account-wide, so overlapping commits only add contention.
    /// </summary>
    public int UploadCommitConcurrency { get; init; } = 1;
    /// <summary>
    /// Maximum simultaneous closed upload-session starts per account namespace (1..128), shared across bound
    /// clients. A large worker pool otherwise issues one start per file at once, and the upload_session/start
    /// endpoint returns HTTP 500 under that burst; the cap trades a little staging overlap for reliability.
    /// </summary>
    public int UploadSessionStartConcurrency { get; init; } = 32;
    public TimeSpan UploadBatchMaxAge { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>
    /// The cap on entries in one conditional file-delete batch (1..1000); folder removal stays on the empty-only path.
    /// Like uploads, the default is the whole provider maximum so the namespace commit lock is held for the fewest launches.
    /// </summary>
    public int DeleteBatchSize { get; init; } = 1000;
    public TimeSpan DeleteBatchMaxAge { get; init; } = TimeSpan.FromMilliseconds(200);
    /// <summary>
    /// The cap on entries in one folder-create batch (the Dropbox maximum is 1000). A first sync with tens of
    /// thousands of new folders otherwise pays one <c>create_folder_v2</c> plus an ancestor walk per folder.
    /// </summary>
    public int MkDirBatchSize { get; init; } = 1000;
    public TimeSpan MkDirBatchMaxAge { get; init; } = TimeSpan.FromMilliseconds(200);
    /// <summary>Retries of explicit, guaranteed-not-applied Dropbox write-operation refusals (zero or one).</summary>
    public int DropboxWriteOperationRetryCount { get; init; } = 1;
    /// <summary>Retries for staging a closed upload session before any finish request (0..5).</summary>
    public int DropboxSessionStartRetryCount { get; init; } = 2;
    /// <summary>Base delay for closed-session staging retries (0..30000 ms; zero permits deterministic fast tests).</summary>
    public int DropboxSessionStartRetryBaseDelayMs { get; init; } = 250;
    /// <summary>
    /// The explicit timeout for one batch operation — the finish_batch_v2 commit or the delete_batch launch — covering
    /// the request headers AND the whole response body. It is the base timeout for a batch and scales with the entry
    /// count (<see cref="BatchOperationTimeoutFor"/>): a full batch gets this base plus the per-entry allowance
    /// (<see cref="BatchTimeoutPerEntry"/>). The transport HttpClient timeout is set above the largest such bound
    /// (EngineComposition.DropboxHttpTimeoutFor), so the transport never pre-empts this bound.
    /// </summary>
    public TimeSpan BatchOperationTimeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>
    /// The per-entry allowance added to a batch operation's explicit timeout (1 s per 10 entries by default): a larger
    /// batch holds its response for as long as Dropbox takes to commit every entry, so a full 1000-entry batch gets the
    /// base five minutes plus this allowance rather than a shrinking per-entry share. Small batches are unaffected.
    /// </summary>
    public TimeSpan BatchTimeoutPerEntry { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>Read-only polling bounds for already-submitted provider jobs.</summary>
    public TimeSpan BatchPollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan BatchPollTimeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Observation grace before replanning a commit whose acknowledgement was lost.</summary>
    public TimeSpan UnknownCommitGracePeriod { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>
    /// How long a delete job Dropbox keeps reporting as still running may stay unresolved before reconcile raises a
    /// "provider-job-stuck" notice naming its path and job id. The effect is never replayed or released: the notice
    /// only tells the user to check Dropbox, so a job that can never be confirmed cannot leave the pair silently stuck.
    /// </summary>
    public TimeSpan StuckJobEscalation { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// The explicit operation timeout for a batch of <paramref name="entries"/> out of a maximum of
    /// <paramref name="maximumEntries"/>: the base <see cref="BatchOperationTimeout"/> scaled by the batch's share of
    /// the maximum, plus the per-entry allowance (<see cref="BatchTimeoutPerEntry"/>) for the entries actually sent,
    /// so a full batch gets the base plus the full allowance. It is never below the smaller of one minute and the base
    /// (so a single entry is not cut at a fraction of a second) and never above the full-batch budget (so the transport
    /// timeout, set above that budget, always stays above this bound).
    /// </summary>
    public TimeSpan BatchOperationTimeoutFor(int entries, int maximumEntries)
    {
        maximumEntries = Math.Max(1, maximumEntries);
        var counted = Math.Max(1, entries);
        var scaledBase = (long)((double)BatchOperationTimeout.Ticks * counted / maximumEntries);
        var allowance = BatchTimeoutPerEntry.Ticks * counted;
        var budget = BatchOperationTimeout.Ticks + BatchTimeoutPerEntry.Ticks * maximumEntries;
        var floor = Math.Min(TimeSpan.FromMinutes(1).Ticks, BatchOperationTimeout.Ticks);
        return TimeSpan.FromTicks(Math.Clamp(scaledBase + allowance, floor, budget));
    }
}
