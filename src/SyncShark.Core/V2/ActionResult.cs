namespace Massdex.SyncShark.V2;

/// <summary>The result of applying one action.</summary>
public sealed record ActionResult(string ActionId, Outcome Outcome, ItemState? NewA, ItemState? NewB,
    RecoveryRef? Recovery, string? Reason)
{
    /// <summary>Original failure code for run-level classification; no exception or provider payload retained.</summary>
    public int? FailureHResult { get; init; }
    /// <summary>A rejected duplicate is a separate admission failure, not the kept effect's result.</summary>
    public string? RejectedDuplicatePath { get; init; }
    public FailureRetryOwner.FailureKind? FailureKind { get; init; }
    public DateTimeOffset? RetryNotBefore { get; init; }

    /// <summary>Fresh reconciliation proved only a temporary wait; null requires ordinary review.</summary>
    public int? RetryAfterSeconds { get; init; }
    public bool UploadQuotaBlocked { get; init; }

    /// <summary>No delete attempted because its source evidence was unverified; retain baseline and queue replan.</summary>
    public bool DeleteSourceUnconfirmed { get; init; }
    public bool DeleteSourceIdentityMissing { get; init; }

    /// <summary>Handled by ignoring the refused name, not by completing an endpoint effect.</summary>
    public bool ProviderNameRefused { get; init; }

    public bool Quarantined { get; init; }

    /// <summary>Settled behind a durable exclusion grant, without changing native-quarantine flag128.</summary>
    public bool DefenderGrantHeld { get; init; }

    /// <summary>No effect attempted because an enclosing folder name was refused; no child flag is persisted.</summary>
    public bool ParentNameRefused { get; init; }
    /// <summary>Settled download has no target: atomically forget only its baseline, never propagate a deletion.</summary>
    public bool ForgetAbsentDownloadBaseline { get; init; }
    /// <summary>Interrupted delete ended with target absent but the original source alive: atomically
    /// retire only this baseline and queue restoration, never infer an opposite-direction user deletion.</summary>
    public bool ForgetUnsafeDeleteBaseline { get; init; }
}
