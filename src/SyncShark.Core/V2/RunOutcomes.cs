namespace Massdex.SyncShark.V2;

/// <summary>The outcome strings a <see cref="RunReport"/> can carry.</summary>
public static class RunOutcomes
{
    internal static int Pending(int failed, int uncertain, int held, int skipped) => failed + uncertain + held + skipped;
    /// <summary>Every planned effect finished with no failure, hold or uncertainty.</summary>
    public const string Complete = "complete";

    /// <summary>The attempt completed with retained work, even if no individual effect succeeded.</summary>
    public const string Partial = "partial";

    /// <summary>The attempt could not finish; individual effect failures alone do not imply this disposition.</summary>
    public const string Failed = "failed";

    /// <summary>The run was cancelled before it finished.</summary>
    public const string Stopped = "stopped";

    /// <summary>Explicit pair pause reached a durable safe point.</summary>
    public const string Paused = "paused";

    /// <summary>Pre-effect admission exceeded the requested plan bound; no new effects were started.</summary>
    public const string PlanBoundExceeded = "plan_bound_exceeded";

    /// <summary>Authoritative state is damaged; the report is in-memory and no store writes are permitted.</summary>
    public const string StateDamaged = "state_damaged";
}
