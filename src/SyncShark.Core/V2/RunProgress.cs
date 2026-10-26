namespace Massdex.SyncShark.V2;

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
