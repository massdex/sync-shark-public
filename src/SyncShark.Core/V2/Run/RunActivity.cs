using System.Text;
using System.Threading;

namespace Massdex.SyncShark.V2;

/// <summary>
/// One run's live-progress pump (RQ-090, RQ-130). It wraps the caller's <see cref="IProgress{RunProgress}"/> sink so
/// that (a) the shared byte-progress seam's process messages — provider retries, cooldowns, listing pages — reach the
/// same sink the run's own phases write to, and (b) a run that writes nothing for a while still says it is working,
/// naming the in-flight operations. It also owns the run's <see cref="RunOperations"/> table and sweeper.
/// <para>
/// Everything here is best-effort and cannot affect a run: every sink call, timer callback and disposal is guarded, so
/// an exception is observed and never escapes; a writing after disposal is a no-op. Process notices and heartbeats are
/// kept apart from the phase state, so a notice never resets the phase or its counts.
/// </para>
/// </summary>
internal sealed class RunActivity : IProgress<RunProgress>, IDisposable
{
    // The path the shared seam reports for the run itself when no narrower path is in scope.
    private const string RunPath = "<run>";
    private const int LastMessageLimit = 80;

    private readonly object _gate = new();
    private readonly IProgress<RunProgress>? _sink;
    private readonly TimeProvider _time;
    private readonly TimeSpan _messageInterval;
    private readonly TimeSpan _heartbeatInterval;
    private readonly DateTimeOffset _started;
    private readonly IDisposable _scope;
    private readonly RunOperations? _operations;
    private readonly ITimer? _timer;
    private DateTimeOffset _lastWrite;
    private DateTimeOffset _lastMessageAt;
    private DateTimeOffset _lastHeartbeat;
    private string? _phase;
    private string? _path;
    private string? _lastMessage;
    private string? _lastActivityText;
    private readonly Dictionary<string, (int Count, double Wait, DateTimeOffset Last)> _throttles = new();
    private readonly Dictionary<string, (int Count, DateTimeOffset Last)> _queued = new();
    private long _done;
    private long _total;
    private long _extra;
    private int _beating;
    private volatile bool _disposed;

    private RunActivity(IProgress<RunProgress>? sink, V2Settings settings, TimeProvider time)
    {
        _sink = sink;
        _time = time;
        _messageInterval = settings.ProgressInterval;
        _heartbeatInterval = settings.HeartbeatInterval;
        _started = _lastWrite = _lastMessageAt = _lastHeartbeat = time.GetUtcNow();
        // One scope for the whole run: every provider process message reaches this sink, where the v1 paths already
        // open one per effect.
        _scope = SyncByteProgressStream.Begin(RunPath, progress => { ProcessMessage(progress); return Task.CompletedTask; });
        if (sink is not null)
        {
            _operations = RunOperations.Start(time, settings, message => Announce(message, ProcessMessageKind.Notice));
            var check = settings.HeartbeatCheckInterval > TimeSpan.Zero
                ? settings.HeartbeatCheckInterval : TimeSpan.FromSeconds(5);
            if (_heartbeatInterval > TimeSpan.Zero)
                _timer = time.CreateTimer(_ => Heartbeat(), null, check, check);
        }
    }

    /// <summary>Starts one run's activity pump around <paramref name="sink"/>; a null sink disables all output.</summary>
    internal static RunActivity Begin(IProgress<RunProgress>? sink, V2Settings settings, TimeProvider time) =>
        new(sink, settings, time);

    /// <summary>The sink a run's phases must write through, so their own output counts as activity too.</summary>
    internal IProgress<RunProgress> Progress => this;

    void IProgress<RunProgress>.Report(RunProgress value) => Publish(value);

    // A provider process message becomes one line, throttled like other progress; a retry, cooldown or wait notice is
    // never dropped, so a run waiting on the provider is never silent.
    private void ProcessMessage(SyncByteProgress progress)
    {
        // Byte snapshots already have their own consumer; never enter the run-wide output lock.
        if (progress.Kind == ProcessMessageKind.Progress && progress.Unit is not null) return;
        if (progress.Kind == ProcessMessageKind.Throttle)
        {
            lock (_gate)
            {
                if (_disposed) return;
                var route = progress.Route ?? "provider";
                var now = _time.GetUtcNow();
                var pending = _throttles.GetValueOrDefault(route);
                pending = (pending.Count + 1, Math.Max(pending.Wait, progress.WaitSeconds), pending.Last);
                _throttles[route] = pending;
                if (now - pending.Last >= TimeSpan.FromSeconds(5)) FlushThrottle(route, pending, now);
            }
            return;
        }
        // Pacer queueing is not a throttle: say only that requests waited for the shared rate budget.
        if (progress.Kind == ProcessMessageKind.Pacer)
        {
            lock (_gate)
            {
                if (_disposed) return;
                var route = progress.Route ?? "provider";
                var now = _time.GetUtcNow();
                var pending = _queued.GetValueOrDefault(route);
                var next = (Count: pending.Count + 1, Last: pending.Last);
                if (now - next.Last >= TimeSpan.FromSeconds(30))
                {
                    Announce(QueuedLine(route, next.Count), ProcessMessageKind.Notice);
                    next = (0, now);
                }
                _queued[route] = next;
            }
            return;
        }
        Announce(string.IsNullOrEmpty(progress.Detail) ? progress.Phase : progress.Phase + ": " + progress.Detail, progress.Kind);
    }

    private void FlushThrottle(string route, (int Count, double Wait, DateTimeOffset Last) pending, DateTimeOffset now)
    {
        _throttles[route] = (0, 0, now);
        Announce(ThrottledLine(route, pending), ProcessMessageKind.Notice);
    }

    // Only real provider throttles (HTTP 429 or a 5xx with Retry-After) reach this line. The wait is shown only
    // when it is at least a whole second, rounded up; a sub-second wait is not worth claiming.
    private static string ThrottledLine(string route, (int Count, double Wait, DateTimeOffset Last) pending)
    {
        var wait = Math.Ceiling(pending.Wait);
        var suffix = pending.Wait >= 1 ? $"; waiting {wait:N0}s" : "";
        return $"{pending.Count} provider throttle {(pending.Count == 1 ? "response" : "responses")} on {route}{suffix}";
    }

    private static string QueuedLine(string route, int count) =>
        $"{count} requests queued for {route} (shared Dropbox rate budget)";

    // Announce a whole line (process message, sweeper warning, announced wait) without touching the phase state.
    private void Announce(string message, ProcessMessageKind kind)
    {
        try
        {
            if (_disposed || _sink is null) return;
            lock (_gate)
            {
                if (_disposed) return;
                if (kind == ProcessMessageKind.Progress && string.Equals(message, _lastMessage, StringComparison.Ordinal)) return;
                var now = _time.GetUtcNow();
                if (kind == ProcessMessageKind.Progress && _lastMessage is not null && now - _lastMessageAt < _messageInterval) return;
                _lastWrite = now;
                _lastMessageAt = now;
                _lastMessage = message;
                _lastActivityText = message;
                Forward(new RunProgress(_phase ?? "Working", _path, _done, _total)
                    { Extra = _extra, Message = message });
            }
        }
        catch (Exception error) { Observe(error); }
    }


    // A phase report: it updates the phase identity and its counts, then forwards (best-effort).
    private void Publish(RunProgress value)
    {
        try
        {
            if (_disposed) return;
            lock (_gate)
            {
                if (_disposed) return;
                _lastWrite = _time.GetUtcNow();
                if (!string.IsNullOrEmpty(value.Phase)) _phase = value.Phase;
                // A phase does not consume the process-message dedupe window.
                _lastActivityText = value.Message ?? Render(value);
                _path = value.Path;
                _done = value.Done;
                _total = value.Total;
                _extra = value.Extra;
                Forward(value);
            }
        }
        catch (Exception error) { Observe(error); }
    }

    private void Forward(RunProgress value)
    {
        try { if (!_disposed) _sink?.Report(value); }
        catch (Exception error) { Observe(error); }
    }

    // Fires every heartbeat check; it says "still working" only when the sink has been quiet at least the heartbeat
    // interval; its separate timestamp limits repeats without changing the last real activity.
    private void Heartbeat()
    {
        try
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _beating, 1) != 0) return; // never overlap two heartbeats
            try
            {
                DateTimeOffset now, lastWrite;
                string? phase, last;
                long done, total, extra;
                lock (_gate)
                {
                    if (_disposed) return;
                    now = _time.GetUtcNow();
                    foreach (var pair in _throttles.ToArray())
                    {
                        if (now - pair.Value.Last < TimeSpan.FromSeconds(5)) continue;
                        _throttles.Remove(pair.Key);
                        if (pair.Value.Count > 0) Announce(ThrottledLine(pair.Key, pair.Value), ProcessMessageKind.Notice);
                    }
                    // A pacer batch that never saw another request still reports on the same 30-second cadence.
                    foreach (var pair in _queued.ToArray())
                    {
                        if (now - pair.Value.Last < TimeSpan.FromSeconds(30) || pair.Value.Count == 0) continue;
                        _queued[pair.Key] = (0, now);
                        Announce(QueuedLine(pair.Key, pair.Value.Count), ProcessMessageKind.Notice);
                    }
                    lastWrite = _lastWrite;
                    phase = _phase;
                    last = _lastActivityText;
                    done = _done;
                    total = _total;
                    extra = _extra;
                    var quiet = now - lastWrite;
                    if (quiet < _heartbeatInterval || now - _lastHeartbeat < _heartbeatInterval) return;
                    _lastHeartbeat = now;
                    Forward(new RunProgress(phase ?? "Working", _path, done, total)
                    {
                        Extra = extra,
                        Message = HeartbeatLine(now, phase, last, done, total, extra, quiet)
                    });
                }
            }
            finally { Volatile.Write(ref _beating, 0); }
        }
        catch (Exception error) { Observe(error); }
    }

    private string HeartbeatLine(DateTimeOffset now, string? phase, string? last, long done, long total,
        long extra, TimeSpan quiet)
    {
        var builder = new StringBuilder("Still working: ").Append(phase ?? "starting");
        if (total > 0) builder.Append(' ').Append(done.ToString("N0")).Append('/').Append(total.ToString("N0"));
        else if (done > 0) builder.Append(' ').Append(done.ToString("N0")).Append(" so far");
        if (extra > 0) builder.Append(" (+").Append(extra.ToString("N0")).Append(" files inside deleted folders)");
        if (InFlight(out var inFlight)) builder.Append("; in flight: ").Append(inFlight);
        builder.Append("; last activity: ").Append(last is null ? "none yet" : Truncate(last, LastMessageLimit));
        builder.Append(' ').Append((long)quiet.TotalSeconds).Append("s ago; elapsed ").Append(Elapsed(now));
        return builder.ToString();
    }

    // The in-flight count by kind and the oldest operation with its age, or false when nothing is tracked.
    private bool InFlight(out string summary)
    {
        summary = string.Empty;
        if (_operations is not { } operations) return false;
        var (counts, oldest) = operations.Summarize();
        if (counts.Count == 0) return false;
        var builder = new StringBuilder();
        for (var index = 0; index < counts.Count; index++)
        {
            if (index > 0) builder.Append(", ");
            builder.Append(counts[index].Count).Append(' ').Append(RunOperations.Name(counts[index].Kind));
        }
        if (oldest is not null)
            builder.Append("; oldest: ").Append(RunOperations.Name(oldest.Kind)).Append(' ').Append(oldest.Label)
                .Append(' ').Append((long)(_time.GetUtcNow() - oldest.StartedUtc).TotalSeconds).Append('s');
        summary = builder.ToString();
        return true;
    }

    private string Elapsed(DateTimeOffset now)
    {
        var elapsed = now - _started;
        return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private static string Render(RunProgress value) =>
        string.IsNullOrEmpty(value.Path) ? value.Phase : value.Phase + ": " + value.Path;

    private static string Truncate(string value, int limit) => value.Length <= limit ? value : value[..limit];

    private static void Observe(Exception error) => RunOutput.Observe(error);

    public void Dispose()
    {
        if (_disposed) return;
        lock (_gate) _disposed = true;
        try { _timer?.Dispose(); }
        catch (Exception error) { Observe(error); }
        try { _operations?.Dispose(); }
        catch (Exception error) { Observe(error); }
        try { _scope.Dispose(); }
        catch (Exception error) { Observe(error); }
    }
}
