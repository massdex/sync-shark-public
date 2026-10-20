namespace Massdex.SyncShark.V2;

/// <summary>A test run that reports a scripted report and progress, driving the observed executor with a plan.</summary>
/// <remarks>
/// IncrementalAsync pushes the scripted plan through the shared <see cref="ObservedExecutor"/> (whose inner is the
/// <see cref="ScriptedExecutor"/>), exactly as the real run would, so the host's per-path mapping is exercised.
/// </remarks>
internal sealed class ScriptedRun : ISyncRun
{
    private readonly ObservedExecutor _observed;
    private readonly FakeEndpoint _a = new(Side.A, "r:scripted:a");
    private readonly FakeEndpoint _b = new(Side.B, "r:scripted:b");

    internal ScriptedRun(ObservedExecutor observed, Plan plan, RunReport report,
        IReadOnlyList<RunProgress>? progress = null)
    {
        _observed = observed;
        Plan = plan;
        Report = report;
        Progress = progress ?? [];
    }

    internal Plan Plan { get; }
    internal RunReport Report { get; set; }
    internal IReadOnlyList<RunProgress> Progress { get; }

    /// <summary>When this returns an exception for a pair id, IncrementalAsync refuses that pair with it.</summary>
    internal Func<string, Exception?>? RefuseFor { get; set; }

    /// <summary>Run at the end of one pair's IncrementalAsync (with that call's token), to simulate an event
    /// arriving between pairs.</summary>
    internal Func<string, CancellationToken, Task>? AfterPair { get; set; }

    /// <summary>Run on the run worker after each progress report (with its index), so a check can hold the next
    /// report at a barrier.</summary>
    internal Action<int>? AfterReport { get; set; }

    // Like SyncRun, StopAsync stops whichever run is in flight when it is called; that run then reports stopped.
    private CancellationTokenSource? _inFlight;

    public Task<Plan> PreviewFirstSyncAsync(string pairId, IProgress<RunProgress> p, CancellationToken ct) =>
        Task.FromResult(Plan);

    public Task<RunReport> ApplyAsync(string pairId, Plan approved, IProgress<RunProgress> p, CancellationToken ct) =>
        IncrementalAsync(pairId, p, ct);

    public async Task<RunReport> IncrementalAsync(string pairId, IProgress<RunProgress> p, CancellationToken ct)
    {
        if (RefuseFor?.Invoke(pairId) is { } refusal) throw refusal;
        var stop = new CancellationTokenSource();
        _inFlight = stop;
        for (var index = 0; index < Progress.Count; index++)
        {
            p.Report(Progress[index]);
            AfterReport?.Invoke(index);
        }
        await foreach (var _ in _observed.ApplyAsync(Plan, _a, _b, ct)) { }
        if (AfterPair is not null) await AfterPair(pairId, ct);
        _inFlight = null;
        return stop.IsCancellationRequested ? Report with { Outcome = RunOutcomes.Stopped } : Report;
    }

    public Task StopAsync()
    {
        _inFlight?.Cancel();
        return Task.CompletedTask;
    }
}
