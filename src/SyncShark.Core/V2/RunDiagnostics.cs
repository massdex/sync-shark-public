namespace Massdex.SyncShark.V2;

/// <summary>A detached per-run measurement snapshot for progress and the final report; no file paths or credentials.</summary>
public sealed record RunDiagnostics(
    TimeSpan Elapsed,
    IReadOnlyDictionary<RunTimingStage, RunTiming> Timings,
    int AdmittedConcurrency,
    int PeakAdmittedConcurrency,
    long AdmissionCount,
    IReadOnlyDictionary<int, long> BatchHistogram,
    long ThrottleCount,
    long RetryCount,
    // Closed-session-start HTTP 500/502/503/504 responses, counted apart from 429 throttles so an overloaded
    // start endpoint is visible in the CLI JSON rather than folded into the shared throttle total.
    long ServerErrorCount,
    IReadOnlyDictionary<string, long> FailureReasons,
    // Skipped effects are unfinished but not failures; their reasons are kept apart from FailureReasons so a
    // deferred run is explained without an unmet precondition ever inflating the failure counts.
    IReadOnlyDictionary<string, long> SkipReasons);
