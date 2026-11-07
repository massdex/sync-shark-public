namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Everything one pair's Executor parts share (section 7). Events go to the run log. ReserveDownloadAsync
/// reserves local free space before a staged download; Time is the shared clock (production: TimeProvider.System).
/// ReadDeletionEvidenceAsync answers one exact target, its boolean saying the candidate is a folder. BeginDownloadAttempt
/// opens the attempt a download reports into; CompleteDownloadAttemptAsync claims its one completion and answers whether
/// only the ordinary transfer source retry may run, never a nested resend or unknown-write replay.
/// CaptureFolderCursorAsync and ReadFolderChangesAsync are a checked Dropbox folder removal's own post-window feed;
/// without them no Dropbox folder effect is sent.</summary>
public sealed record ExecutorContext(ISide Local, ISide Dropbox, ILocalRecovery Recovery, IStateStore Store,
    PairConfig Pair, PathScope Scope, EngineSettings Settings, ReplaceTurn Runtime, IAdmission Admission, bool Priority,
    Action<RunEvent> Events,
    Func<string, string, long, CancellationToken, ValueTask<IAsyncDisposable>> ReserveDownloadAsync, TimeProvider Time,
    Func<string, bool, CancellationToken, Task<DeletionEvidence>> ReadDeletionEvidenceAsync,
    Func<object, RequestAttempt> BeginDownloadAttempt,
    Func<RequestAttempt, CancellationToken, Task<bool>> CompleteDownloadAttemptAsync,
    (string AccountId, string RouteName, string SourceIdentity) UploadAdmissionIdentity,
    IItemAttempts ItemAttempts, object Opportunity, long ExpectedGeneration)
{
    /// <summary>The one Local-evidence cache for this context: every executor part reads the same source capture.</summary>
    internal LocalEvidence Evidence { get; } = new(Store, Pair, ExpectedGeneration);

    /// <summary>The owned listing, or Empty when this operation has no listing evidence.</summary>
    internal Guid ScanId { get; init; }

    internal string? BatchScope { get; init; }
    /// <summary>The batch side whose listing ended early; a new row missing there was not listed, not absent.</summary>
    internal Side? BatchUnlisted { get; init; }
    /// <summary>PairScan's joined listing was incomplete; no first-run folder may settle in this batch.</summary>
    internal bool BatchIncomplete { get; init; }
    internal bool BatchSettlesFirstRun { get; init; }

    public ISide For(Side side) => side == Side.Local ? Local : Dropbox;

    /// <summary>D33: one operation takes one permit before its first Dropbox observation; its inner steps take none.
    /// The permit uses this session's priority.</summary>
    internal Task<IAsyncDisposable> EnterOperationAsync(CancellationToken ct) => Admission.AdmitAsync(Priority, ct);

    public Func<CancellationToken, Task<string>>? CaptureFolderCursorAsync { get; init; }

    public Func<string, CancellationToken, IAsyncEnumerable<Listed>>? ReadFolderChangesAsync { get; init; }

    /// <summary>Claims a folder-job read's one completion; true means its selected wait has passed and the same
    /// observation may run once more. It never authorizes a mutation.</summary>
    public Func<RequestAttempt, CancellationToken, Task<bool>>? CompleteRecoveryAttemptAsync { get; init; }
}
