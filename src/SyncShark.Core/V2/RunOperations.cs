using System.Collections.Concurrent;

namespace Massdex.SyncShark.V2;

/// <summary>
/// One run's in-flight operation table and its single sweeper (RQ-090). Every potentially slow operation registers on
/// start and removes itself on finish; the sweeper warns once an operation passes its threshold and repeats every
/// <see cref="V2Settings.OperationWarningRepeat"/> until it finishes. The table is ambient to the run, so any call site
/// (the HTTP send point, the store, a gate) registers with one line and pays nothing when no run is active. Every
/// output is best-effort: a warning or an observation that throws is caught and counted, never escapes, and never
/// changes the run's behaviour.
/// </summary>
internal sealed class RunOperations : IDisposable
{
    // Beyond this many simultaneously tracked operations, further registrations are dropped rather than grown without
    // bound. Tracked with an Interlocked counter, never the dictionary's Count (which locks every bucket).
    private const int MaximumTracked = 100_000;

    private static readonly AsyncLocal<RunOperations?> Ambient = new();

    private readonly ConcurrentDictionary<long, Entry> _table = new();
    private readonly TimeProvider _time;
    private readonly V2Settings _settings;
    private readonly Action<string> _warn;
    private readonly TimeSpan _sweep;
    private readonly TimeSpan _repeat;
    private readonly ITimer? _timer;
    private readonly RunOperations? _previous;
    private long _next;
    private int _count;
    private int _sweeping;
    private DateTimeOffset _lastSlotSummary;
    private volatile bool _disposed;

    private RunOperations(TimeProvider time, V2Settings settings, Action<string> warn)
    {
        _time = time;
        _settings = settings;
        _warn = warn;
        _sweep = settings.OperationSweepInterval;
        _repeat = settings.OperationWarningRepeat;
        _previous = Ambient.Value;
        Ambient.Value = this;
        if (_sweep > TimeSpan.Zero && _repeat > TimeSpan.Zero)
            _timer = time.CreateTimer(_ => GuardedSweep(), null, _sweep, _sweep);
    }

    /// <summary>Starts tracking in the caller's execution context; disposing stops the sweeper and restores the enclosing run.</summary>
    internal static RunOperations Start(TimeProvider time, V2Settings settings, Action<string> warn) =>
        new(time, settings, warn);

    /// <summary>Whether a run is tracking operations in the current execution context.</summary>
    internal static bool Active => Ambient.Value is not null;
    internal static object? ContextIdentity => Ambient.Value;

    internal static void Failure(string detail) => Ambient.Value?.Warn(detail);

    /// <summary>
    /// Registers one operation until the returned token is disposed. A no-op that allocates nothing when no run is
    /// active, and cheap (one immutable entry, a struct token) otherwise, so it can sit on hot paths.
    /// </summary>
    internal static Token Begin(RunOperationKind kind, string label) =>
        Ambient.Value is { } run ? run.Register(kind, label) : default;

    /// <summary>Waits on a gate while the wait is tracked and named, so a stuck gate is named by the sweeper.</summary>
    internal static async Task WaitAsync(SemaphoreSlim semaphore, string label, CancellationToken ct = default,
        RunOperationKind kind = RunOperationKind.Gate)
    {
        using var token = Ambient.Value is { } run ? run.Register(kind, label) : default;
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
    }

    private Token Register(RunOperationKind kind, string label)
    {
        if (_disposed) return default;
        if (Interlocked.Increment(ref _count) > MaximumTracked)
        { Interlocked.Decrement(ref _count); return default; }
        var id = Interlocked.Increment(ref _next);
        var entry = new Entry(kind, label, _time.GetUtcNow());
        _table[id] = entry;
        return new Token(this, id);
    }

    private void Touch(long id)
    {
        while (_table.TryGetValue(id, out var entry))
            if (_table.TryUpdate(id, entry with { LastActivityUtc = _time.GetUtcNow(), LastWarnedUtcTicks = 0 }, entry)) return;
    }

    private void Release(long id)
    {
        if (!_table.TryRemove(id, out var entry)) return;
        Interlocked.Decrement(ref _count);
    }

    private void GuardedSweep()
    {
        try { Sweep(); }
        catch (Exception error) { Observe(error); }
    }

    private void Sweep()
    {
        if (_disposed || _table.IsEmpty) return;
        // One sweeper: a still-running sweep never overlaps the next tick.
        if (Interlocked.Exchange(ref _sweeping, 1) != 0) return;
        try
        {
            var now = _time.GetUtcNow();
            var nowTicks = now.UtcTicks;
            var slots = 0;
            Entry? oldestSlot = null;
            foreach (var pair in _table) // enumerate the dictionary directly, never its .Values snapshot
            {
                var entry = pair.Value;
                if (entry.Kind == RunOperationKind.TransferSlot)
                {
                    if (now - entry.StartedUtc >= _settings.SlowGateWaitThreshold)
                    { slots++; if (oldestSlot is null || entry.StartedUtc < oldestSlot.StartedUtc) oldestSlot = entry; }
                    continue;
                }
                var age = now - entry.LastActivityUtc;
                if (age < ThresholdOf(entry.Kind)) continue;
                var last = entry.LastWarnedUtcTicks;
                if (last != 0 && nowTicks - last < _repeat.Ticks) continue;
                if (!_table.TryUpdate(pair.Key, entry with { LastWarnedUtcTicks = nowTicks }, entry)) continue;
                Warn($"Still waiting: {PrefixOf(entry.Kind)} {entry.Label} {(long)age.TotalSeconds}s");
            }
            if (slots > 0 && now - _lastSlotSummary >= TimeSpan.FromSeconds(10))
            {
                _lastSlotSummary = now;
                Warn($"{slots} actions waiting for transfer slots; oldest: {oldestSlot!.Label} {(long)(now - oldestSlot.StartedUtc).TotalSeconds}s");
            }
        }
        finally { Volatile.Write(ref _sweeping, 0); }
    }

    private void Warn(string message)
    {
        try { _warn(message); }
        catch (Exception error) { Observe(error); }
    }

    private static void Observe(Exception error) => RunOutput.Observe(error);

    /// <summary>The count of in-flight operations by kind and the oldest one, for the heartbeat line.</summary>
    internal (IReadOnlyList<(RunOperationKind Kind, int Count)> Counts, Entry? Oldest) Summarize()
    {
        if (_table.IsEmpty) return (Array.Empty<(RunOperationKind, int)>(), null);
        var counts = new Dictionary<RunOperationKind, int>();
        Entry? oldest = null;
        foreach (var pair in _table)
        {
            var entry = pair.Value;
            counts[entry.Kind] = counts.GetValueOrDefault(entry.Kind) + 1;
            if (oldest is null || entry.StartedUtc < oldest.StartedUtc) oldest = entry;
        }
        return (counts.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)).ToArray(), oldest);
    }

    private TimeSpan ThresholdOf(RunOperationKind kind) => kind switch
    {
        RunOperationKind.BatchCommit => _settings.SlowBatchCommitThreshold,
        RunOperationKind.LocalIo => _settings.SlowLocalIoThreshold,
        RunOperationKind.LocalWrite => _settings.SlowLocalIoThreshold,
        RunOperationKind.Store => _settings.SlowStoreThreshold,
        RunOperationKind.Gate => _settings.SlowGateWaitThreshold,
        _ => _settings.SlowHttpThreshold,
    };

    private static string PrefixOf(RunOperationKind kind) => kind switch
    {
        RunOperationKind.Http => "Dropbox",
        RunOperationKind.BatchCommit => "Dropbox",
        RunOperationKind.LocalIo => "Local read",
        RunOperationKind.LocalWrite => "Local write",
        RunOperationKind.Store => "Store",
        RunOperationKind.Gate => "Gate",
        RunOperationKind.TransferSlot => "Transfer slots",
        RunOperationKind.TokenRefresh => "Token refresh",
        _ => "Operation",
    };

    /// <summary>The name a kind's heartbeat line uses.</summary>
    internal static string Name(RunOperationKind kind) => PrefixOf(kind);

    /// <summary>A bounded label (a path can be long), so one line stays readable.</summary>
    internal static string Short(string value) => value.Length <= 96 ? value : value[..96];

    public void Dispose()
    {
        _disposed = true;
        try { _timer?.Dispose(); }
        catch (Exception error) { Observe(error); }
        if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = _previous;
    }

    /// <summary>One tracked operation: identity plus the last time its warning was emitted. Never reused.</summary>
    internal sealed record Entry
    {
        internal readonly RunOperationKind Kind;
        internal readonly string Label;
        internal readonly DateTimeOffset StartedUtc;
        internal long LastWarnedUtcTicks { get; init; }
        internal DateTimeOffset LastActivityUtc { get; init; }

        internal Entry(RunOperationKind kind, string label, DateTimeOffset started)
        {
            Kind = kind;
            Label = label;
            StartedUtc = LastActivityUtc = started;
            LastWarnedUtcTicks = 0;
        }

    }

    /// <summary>The disposable registration handle: a value type, so registering allocates no separate token object.</summary>
    internal readonly struct Token : IDisposable
    {
        private readonly RunOperations? _owner;
        private readonly long _id;

        internal Token(RunOperations owner, long id)
        {
            _owner = owner;
            _id = id;
        }

        internal void Touch() => _owner?.Touch(_id);
        public void Dispose() => _owner?.Release(_id);
    }
}
