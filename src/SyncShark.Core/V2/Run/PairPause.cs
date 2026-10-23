namespace Massdex.SyncShark.V2;

/// <summary>A normal checkpoint-safe exit, not a provider failure or a retry request.</summary>
public sealed class PairPausedException(string pairId) : OperationCanceledException("Pair paused; resume to continue.")
{
    public string PairId { get; } = pairId;
    internal RunReport? Report { get; set; }
}

internal sealed class PairPause : IDisposable
{
    private static readonly AsyncLocal<PairPause?> Current = new();
    private readonly PairPause? _previous;
    private readonly SqliteStateStore? _store;
    private readonly string _pair;
    private readonly Func<bool>? _requested;
    private PairPause(IStateStore store, string pair, Func<bool>? requested)
    {
        _previous = Current.Value;
        _store = store as SqliteStateStore;
        _pair = pair;
        _requested = requested ?? _previous?._requested;
        Current.Value = this;
    }
    internal static PairPause Begin(IStateStore store, string pair, Func<bool>? requested = null) => new(store, pair, requested);
    // A durable or active-run pause requested in this scope. Work that must not count a paused run reads
    // this; work that must stop calls Check. Both observe the same two sources.
    internal static bool IsPaused =>
        Current.Value is { } pause &&
        (pause._requested?.Invoke() == true || pause._store?.CachedPairPaused(pause._pair) == true);
    internal static void Check()
    {
        if (Current.Value is { } pause && IsPaused) throw new PairPausedException(pause._pair);
    }
    public void Dispose() => Current.Value = _previous;
}
