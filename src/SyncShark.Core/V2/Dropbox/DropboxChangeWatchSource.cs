namespace Massdex.SyncShark.V2;

/// <summary>
/// Creates one <see cref="DropboxChangeWatch"/> per Dropbox root and remembers each root's backoff and last-signalled
/// cursor, so both survive a watch rebuild and a Kick (continuous-sync.md section 1).
/// </summary>
internal sealed class DropboxChangeWatchSource(
    IStateStore store, DropboxPilotClient client, V2Settings settings, Func<TimeSpan> safetyPoll, TimeProvider clock)
    : IChangeWatchSource
{
    private readonly object _lock = new();
    private readonly V2Settings _settings = ValidateSettings(settings);
    private readonly Dictionary<string, RootWatchState> _states = new(StringComparer.Ordinal);
    private readonly DropboxRoute _longpoll = DropboxRouteTable.Embedded.Find("files/list_folder/longpoll")
        ?? throw new InvalidOperationException("The Dropbox route table has no files/list_folder/longpoll route.");

    /// <inheritdoc/>
    public Task<IChangeWatch> WatchAsync(RootDef root, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(root);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IChangeWatch>(
            new DropboxChangeWatch(this, root, store, client, _longpoll, _settings, clock));
    }

    /// <summary>The retry ceiling for one watch: the safety-poll interval the composition passed in.</summary>
    internal TimeSpan RetryCeiling()
    {
        var ceiling = safetyPoll();
        if (ceiling <= TimeSpan.Zero)
            throw new InvalidOperationException(
                "The safety-poll interval must be a positive TimeSpan; it is the retry ceiling.");
        return ceiling;
    }

    /// <summary>The cursor that last signalled (a change or an invalid-cursor 409), or null.</summary>
    internal string? SignalledCursor(string rootId)
    {
        lock (_lock) return State(rootId).SignalledCursor;
    }

    /// <summary>Records the cursor that just signalled, so it is not polled again until the store moves on.</summary>
    internal void MarkSignalled(string rootId, string cursor)
    {
        lock (_lock) State(rootId).SignalledCursor = cursor;
    }

    /// <summary>Records the provider's requested backoff; a response without one never clears a pending not-before.</summary>
    internal void MarkBackoff(string rootId, int? seconds)
    {
        if (seconds is not { } value) return;
        lock (_lock) State(rootId).NotBefore = clock.GetUtcNow() + TimeSpan.FromSeconds(value);
    }

    /// <summary>The time before which this root must not be polled again.</summary>
    internal DateTimeOffset NotBefore(string rootId)
    {
        lock (_lock) return State(rootId).NotBefore;
    }

    // Every caller holds _lock, so the per-root dictionary is never touched unlocked.
    private RootWatchState State(string rootId)
    {
        if (!_states.TryGetValue(rootId, out var state)) _states[rootId] = state = new RootWatchState();
        return state;
    }

    // A zero or negative pacing setting would make the watch retry or long-poll without pause; refuse it here, where
    // the composition hands the settings in.
    private static V2Settings ValidateSettings(V2Settings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.AutoRetryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(value),
                "AutoRetryDelay must be a positive TimeSpan, or the watch would retry without pause.");
        if (value.DropboxLongpollTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(value),
                "DropboxLongpollTimeout must be a positive TimeSpan.");
        return value;
    }

    private sealed class RootWatchState
    {
        internal string? SignalledCursor;
        internal DateTimeOffset NotBefore = DateTimeOffset.MinValue;
    }
}
