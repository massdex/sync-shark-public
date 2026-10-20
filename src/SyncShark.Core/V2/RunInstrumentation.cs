using System.Collections.ObjectModel;

namespace Massdex.SyncShark.V2;

/// <summary>Collects thread-safe, bounded per-run measurements without changing execution or provider policy.</summary>
public sealed class RunInstrumentation : IDisposable
{
    // The report keeps the most frequent reasons; the collector keeps a bounded set of distinct ones (a reason can
    // embed a path, so its vocabulary is not provably small).
    private const int MaximumReportedFailureReasons = 10;
    private const int MaximumDistinctFailureReasons = 64;
    // Skip reasons are bounded exactly like failure reasons, but counted in their own histogram.
    private const int MaximumReportedSkipReasons = 10;
    private const int MaximumDistinctSkipReasons = 64;
    private static readonly AsyncLocal<RunInstrumentation?> Ambient = new();
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly RunInstrumentation? _previous;
    private readonly Dictionary<RunTimingStage, RunTiming> _timings = [];
    private readonly Dictionary<int, long> _batches = [];
    private readonly Dictionary<string, long> _failureReasons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _skipReasons = new(StringComparer.Ordinal);
    private int _admitted;
    private int _peakAdmitted;
    private long _admissionCount;
    private long _throttles;
    private long _retries;
    private long _serverErrors;
    private bool _disposed;

    private RunInstrumentation(TimeProvider time)
    {
        _time = time;
        _started = time.GetTimestamp();
        _previous = Ambient.Value;
        Ambient.Value = this;
    }

    /// <summary>The current run's collector, propagated to asynchronous work; null when collection is disabled.</summary>
    public static RunInstrumentation? Current => Ambient.Value;

    /// <summary>Starts collection in the caller's execution context; disposing restores the enclosing run.</summary>
    public static RunInstrumentation? Begin(V2Settings settings, TimeProvider time) =>
        settings.EnableRunInstrumentation ? new RunInstrumentation(time) : null;

    /// <summary>Measures one operation until disposed, including its cancellation or failure path.</summary>
    public IDisposable Measure(RunTimingStage stage)
    {
        var started = _time.GetTimestamp();
        return new Measurement(() => RecordTiming(stage, _time.GetElapsedTime(started)));
    }

    /// <summary>Counts a successful admission until its actual lease release, not until the action finishes.</summary>
    public IDisposable Admitted()
    {
        lock (_gate)
        {
            _admissionCount++;
            _admitted++;
            _peakAdmitted = Math.Max(_peakAdmitted, _admitted);
        }
        return new Measurement(() => { lock (_gate) _admitted--; });
    }

    /// <summary>Records the actual number of entries submitted in one provider batch.</summary>
    public void RecordBatch(int size)
    {
        if (size <= 0) return;
        lock (_gate) _batches[size] = _batches.GetValueOrDefault(size) + 1;
    }

    /// <summary>Records a provider throttle response, independently of whether another attempt is made.</summary>
    public void RecordThrottle() { lock (_gate) _throttles++; }

    /// <summary>Counts one Failed or Uncertain outcome under its reason (the reason a result already carries).</summary>
    public void RecordFailure(string? reason)
    {
        var key = string.IsNullOrWhiteSpace(reason) ? "Unspecified failure" : reason;
        lock (_gate)
        {
            if (_failureReasons.TryGetValue(key, out var count)) _failureReasons[key] = count + 1;
            else if (_failureReasons.Count < MaximumDistinctFailureReasons) _failureReasons[key] = 1;
        }
    }

    /// <summary>Counts one Skipped outcome under its reason, kept apart from the failure histogram.</summary>
    public void RecordSkip(string? reason)
    {
        var key = string.IsNullOrWhiteSpace(reason) ? "Unspecified skip" : reason;
        lock (_gate)
        {
            if (_skipReasons.TryGetValue(key, out var count)) _skipReasons[key] = count + 1;
            else if (_skipReasons.Count < MaximumDistinctSkipReasons) _skipReasons[key] = 1;
        }
    }

    /// <summary>Records an actual retry attempt, excluding the initial attempt and admission polling.</summary>
    public void RecordRetry() { lock (_gate) _retries++; }

    /// <summary>Records one closed-session-start server error (HTTP 500/502/503/504), apart from a 429 throttle.</summary>
    public void RecordServerError() { lock (_gate) _serverErrors++; }

    /// <summary>Copies completed measurements and current concurrency without exposing mutable collector state.</summary>
    public RunDiagnostics Snapshot()
    {
        lock (_gate)
            return new RunDiagnostics(_time.GetElapsedTime(_started),
                new ReadOnlyDictionary<RunTimingStage, RunTiming>(new Dictionary<RunTimingStage, RunTiming>(_timings)),
                _admitted, _peakAdmitted, _admissionCount,
                new ReadOnlyDictionary<int, long>(new Dictionary<int, long>(_batches)), _throttles, _retries,
                _serverErrors,
                new ReadOnlyDictionary<string, long>(_failureReasons
                    .OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .Take(MaximumReportedFailureReasons)
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)),
                new ReadOnlyDictionary<string, long>(_skipReasons
                    .OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .Take(MaximumReportedSkipReasons)
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)));
    }

    /// <summary>Ends the ambient scope; in-flight operations retain their captured collector.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Ambient.Value = _previous;
    }

    private void RecordTiming(RunTimingStage stage, TimeSpan elapsed)
    {
        lock (_gate)
        {
            var previous = _timings.GetValueOrDefault(stage);
            _timings[stage] = new RunTiming((previous?.Count ?? 0) + 1,
                (previous?.Elapsed ?? TimeSpan.Zero) + elapsed,
                previous is null || elapsed > previous.Maximum ? elapsed : previous.Maximum);
        }
    }

    // Timings and admissions share an idempotent scope: provider early-release and finally may both dispose it.
    private sealed class Measurement(Action finish) : IDisposable
    {
        private Action? _finish = finish;
        public void Dispose() => Interlocked.Exchange(ref _finish, null)?.Invoke();
    }
}
