namespace Massdex.SyncShark.V2;

internal sealed record ActionKindProgress(ActionKind Kind, long Done, long Total);

/// <summary>
/// Forwards run progress on a phase change or once per interval, so a fast run stays quiet. Every phase begins with a
/// start line (its identity) and ends with a "done" line carrying its counts and elapsed time; a phase running longer
/// than ten seconds reports progress every ten seconds with a rate and an ETA (RQ-090). The phase identity stays
/// stable — only the display text changes — so a consumer keyed on the phase never sees it flicker. Concurrent sides
/// each take their own sibling.
/// </summary>
internal sealed class ProgressThrottle
{
    // A phase that has run this long reports rich progress (rate and ETA) instead of a bare phase line, this often.
    private static readonly TimeSpan LongPhase = TimeSpan.FromSeconds(10);

    private readonly IProgress<RunProgress>? _progress;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private bool _started;
    private bool _phaseOpen;
    private string? _phase;
    private string? _path;
    private long _done;
    private long _total;
    private long _extra;
    private long _phaseStartDone;
    private IReadOnlyList<ActionKindProgress>? _kinds;
    private DateTimeOffset _last;
    private DateTimeOffset _lastText;
    private DateTimeOffset _phaseStart;

    public ProgressThrottle(IProgress<RunProgress>? progress, TimeProvider time, TimeSpan interval)
    {
        _progress = progress;
        _time = time;
        _interval = interval;
    }

    /// <summary>Reports the latest progress; a new phase prints its start line, an ended one its "done" line.
    /// <paramref name="extra"/> counts finished results outside the phase's total, shown as a separate suffix.</summary>
    public void Report(string phase, string? path, long done, long total, long extra = 0,
        IReadOnlyList<ActionKindProgress>? kinds = null)
    {
        var now = _time.GetUtcNow();
        var changed = !_phaseOpen || !string.Equals(phase, _phase, StringComparison.Ordinal);
        if (changed && _phaseOpen) EndPhase(now);
        _phase = phase;
        _path = path;
        _done = done;
        _total = total;
        _extra = extra;
        _kinds = kinds;
        if (changed)
        {
            _phaseStart = now;
            _phaseStartDone = done;
            _phaseOpen = true;
            _last = _lastText = now;
            _started = true;
            Publish(Current());
            return;
        }
        var longPhase = now - _phaseStart >= LongPhase;
        if (now - _last < _interval) return;
        _last = now;
        Publish(!longPhase && _total == 0
            ? Current() with { Message = $"{_phase}: {_done:N0} so far{ExtraText()}" } : Current());
        if (longPhase && now - _lastText >= LongPhase)
        { _lastText = now; Publish(Rich(now)); }
    }

    /// <summary>
    /// A second throttle over the same sink and interval, with its own rate-limit state. Each side of a concurrent
    /// scan takes one so the two report streams never share this instance's mutable fields.
    /// </summary>
    public ProgressThrottle Sibling() => new(_progress, _time, _interval);

    /// <summary>Forces the final report and the open phase's end line, so the caller always sees where the run stopped.</summary>
    public void Flush(bool failed = false)
    {
        if (!_started) return;
        var now = _time.GetUtcNow();
        Publish(Current());
        EndPhase(now, failed);
    }

    // The end of a phase: what it did and how long it took. It never says "done: 0".
    private void EndPhase(DateTimeOffset now, bool failed = false)
    {
        if (!_phaseOpen || _phase is null) return;
        _phaseOpen = false;
        var counts = _total > 0 ? $": {_done:N0}/{_total:N0}" : _done > 0 ? $": {_done:N0}" : string.Empty;
        Publish(new RunProgress(_phase, null, _done, _total)
        {
            Extra = _extra,
            Message = failed ? _phase + " failed after " + Describe(now - _phaseStart)
                : _phase + " done" + counts + ExtraText() + " in " + Describe(now - _phaseStart)
        });
    }

    // A long phase's periodic progress line: done/total (or "N so far"), a rate since the phase began and an ETA.
    private RunProgress Rich(DateTimeOffset now)
    {
        var elapsed = now - _phaseStart;
        var advanced = Math.Max(0, _done - _phaseStartDone);
        var rate = advanced / Math.Max(0.001, elapsed.TotalSeconds);
        var volume = _total > 0 ? $"{_done:N0}/{_total:N0}" : $"{_done:N0} so far";
        var message = $"{_phase}: {volume}{ExtraText()}" + (rate > 0 ? $", {rate:N1}/s" : string.Empty) +
                      (rate > 0 && _total > _done ? $", ETA {Describe(TimeSpan.FromSeconds(Math.Clamp(((double)_total - _done) / rate, 0, TimeSpan.MaxValue.TotalSeconds / 2)))}"
                          : string.Empty);
        if (_kinds is { Count: > 0 })
        {
            // Do not extrapolate fast uploads over a slow delete tail. No completed sample of a kind
            // means its ETA is unknown, not zero. These are per-kind wall-clock rates, not additive ETAs.
            message = $"{_phase}: {volume}{ExtraText()}";
            foreach (var kind in _kinds)
            {
                var kindRate = kind.Done / Math.Max(0.001, elapsed.TotalSeconds);
                message += $"; {kind.Kind} {kind.Done:N0}/{kind.Total:N0}";
                if (kind.Total <= kind.Done) continue;
                message += kindRate > 0
                    ? $", {kindRate:N1}/s, ETA {Describe(TimeSpan.FromSeconds(Math.Clamp((kind.Total - kind.Done) / kindRate, 0, TimeSpan.MaxValue.TotalSeconds / 2)))}"
                    : ", ETA awaiting sample";
            }
        }
        return Current() with { Message = message };
    }

    private static string Describe(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" : $"{span.TotalSeconds:N1}s";

    // Finished results outside the phase's total (a folder delete's expanded child deletes), never added to it.
    private string ExtraText() => _extra > 0 ? $" (+{_extra:N0} files inside deleted folders)" : string.Empty;

    private void Publish(RunProgress value)
    {
        try { _progress?.Report(value); }
        catch (Exception) { /* Display failures cannot fail inventory, evidence, or effects. */ }
    }

    private RunProgress Current() => new(_phase!, _path, _done, _total)
    {
        Extra = _extra,
        Diagnostics = RunInstrumentation.Current?.Snapshot()
    };
}
