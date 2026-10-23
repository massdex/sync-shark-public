namespace Massdex.SyncShark.V2;

/// <summary>The reasons the Executor's effect resolution raises: each stable key and its wording live here and nowhere else.
/// The detail is a format template; the resolver fills the failure (the exception type and message) or the action kind in.</summary>
public static class ExecutionReasons
{
    /// <summary>The endpoint could not be observed, so the effect stays unverified (executor.md section 6, A6).</summary>
    public static readonly NoticeReason Unverified = new("executor-unverified", "Could not verify: {0}");

    /// <summary>A frozen keep-both successor must not release its original conflict authority on failure.</summary>
    public const string ConflictCopyBlocked = "keep-both-copy-blocked";

    internal static string ConflictCopyNoticeId(string pairId, SyncAction action) =>
        ShortId.Make("n", pairId, ConflictCopyBlocked, action.ActionId);

    /// <summary>A recovery copy lookup failed while resolving the effect (executor.md section 6, A6).</summary>
    public static readonly NoticeReason RecoveryUnverified =
        new("executor-recovery-unverified", "recovery copy lookup failed: {0}");

    /// <summary>A persisted asynchronous provider job has not returned a terminal result.</summary>
    public static readonly NoticeReason ProviderPending =
        new("executor-provider-pending", "Provider batch job is still pending; it was not replayed.");

    /// <summary>An unknown acknowledgement cannot yet be resolved by observation and grace policy.</summary>
    public static readonly NoticeReason ProviderUnknown =
        new("executor-provider-unknown", "Provider batch acknowledgement is unknown; its intent was retained without replay.");

    /// <summary>
    /// A delete or folder-create job that has stayed unresolved well past its submission: the job may still be
    /// running on Dropbox, or the check itself may have failed, so Sync Shark cannot confirm the effect's outcome.
    /// Reconcile keeps the effect Uncertain and raises this notice (path and job id in the detail) so the user checks
    /// Dropbox; never replayed. The wording names no one operation, because both kinds raise it.
    /// </summary>
    public static readonly NoticeReason StuckProviderJob = new("provider-job-stuck",
        "The Dropbox job for '{0}' is unresolved: Sync Shark cannot confirm its outcome yet. Check Dropbox, then run " +
        "again to re-check; the effect stays unresolved and is never replayed (job {1}).");

    /// <summary>
    /// a whole-folder delete also removed items added or changed after the scan, and they were
    /// restored (a file to its latest revision, a folder recreated). Detail: folder, count, the paths.
    /// </summary>
    public static readonly NoticeReason FolderDeleteRestored = new("folder-delete-restored",
        "The folder delete '{0}' encountered {1} item(s) added or changed after the scan; they were restored or preserved: {2}");

    /// <summary>
    /// recovery failures or deliberately preserved occupied/unknown-kind paths. Only transient
    /// failures stay unresolved; permanent ambiguities resolve with this notice. Detail: folder, count, the paths.
    /// </summary>
    public static readonly NoticeReason FolderDeleteUnrestored = new("folder-delete-unrestored",
        "Folder '{0}': {1} item(s) recoverable, not restored (or requiring recovery inspection). " +
        "Review Dropbox deleted files and version history; any current content was preserved: {2}");

    /// <summary>No observation rule exists for the action kind (executor.md section 6).</summary>
    public static readonly NoticeReason NoObservationRule = new("executor-no-observation-rule", "no observation rule for {0}");
}
