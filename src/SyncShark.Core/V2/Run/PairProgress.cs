namespace Massdex.SyncShark.V2;

/// <summary>Projects one pair's run reports into the latest in-memory progress the status surface reads.</summary>
internal sealed class PairProgress(AutoSyncInputs inputs, string pairId, string kind, TimeProvider time)
    : IProgress<RunProgress>
{
    private readonly object _gate = new();
    private DateTimeOffset? _started;

    public void Report(RunProgress value)
    {
        try
        {
            var now = time.GetUtcNow();
            TimeSpan? eta;
            lock (_gate)
            {
                _started ??= now;
                eta = Estimate(_started.Value, now, value.Done, value.Total);
            }
            inputs.ReportProgress?.Invoke(pairId,
                new LiveProgress(kind, value.Phase, value.Done, value.Total, value.Path, eta, now));
        }
        catch (Exception error) { RunOutput.Observe(error); }
    }

    // Linear extrapolation over the reports already seen; an idle or unmeasured run has no honest estimate.
    private static TimeSpan? Estimate(DateTimeOffset started, DateTimeOffset now, long done, long total)
    {
        if (done <= 0 || total <= done) return null;
        var elapsed = now - started;
        if (elapsed <= TimeSpan.Zero) return null;
        var seconds = elapsed.TotalSeconds * (total - done) / done;
        return double.IsFinite(seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }
}
