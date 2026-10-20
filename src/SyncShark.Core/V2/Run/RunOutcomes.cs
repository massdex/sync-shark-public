namespace Massdex.SyncShark.V2;

/// <summary>The outcome strings a <see cref="RunReport"/> can carry.</summary>
public static class RunOutcomes
{
    /// <summary>Every planned effect finished with no failure, hold or uncertainty.</summary>
    public const string Complete = "complete";

    /// <summary>Some work finished, and some is left (held, failed or not reached).</summary>
    public const string Partial = "partial";

    /// <summary>Nothing finished and at least one effect failed.</summary>
    public const string Failed = "failed";

    /// <summary>The run was cancelled before it finished.</summary>
    public const string Stopped = "stopped";
}
