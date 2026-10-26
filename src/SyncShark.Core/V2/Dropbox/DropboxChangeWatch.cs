using System.Net;
using System.Text.Json.Nodes;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Watches one Dropbox root by long-polling the cursor the store already holds; it never creates or advances a
/// cursor. A signalled cursor is not polled again until the store's cursor moves on. See continuous-sync.md section 1.
/// </summary>
internal sealed class DropboxChangeWatch : IChangeWatch
{
    // The watch owns its retry pacing (continuous-sync.md section 1), so the route caller retries nothing itself.
    private static readonly DropboxRetryLimits NoRouteRetry = new(0, 0, 0, TimeSpan.Zero);

    private readonly object _lock = new();
    private readonly DropboxChangeWatchSource _source;
    private readonly RootDef _root;
    private readonly IStateStore _store;
    private readonly DropboxPilotClient _client;
    private readonly DropboxRoute _route;
    private readonly V2Settings _settings;
    private readonly TimeProvider _clock;
    private bool _pending = true; // a new (or rebuilt) watch starts with one catch-up signal
    private bool _running;
    private bool _disposed;

    internal DropboxChangeWatch(DropboxChangeWatchSource source, RootDef root, IStateStore store,
        DropboxPilotClient client, DropboxRoute route, V2Settings settings, TimeProvider clock)
    {
        _source = source;
        _root = root;
        _store = store;
        _client = client;
        _route = route;
        _settings = settings;
        _clock = clock;
    }

    /// <inheritdoc/>
    public Task ChangedAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DropboxChangeWatch));
            if (_running) throw new InvalidOperationException("DropboxChangeWatch: a wait is already outstanding.");
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            if (_pending)
            {
                _pending = false;
                return Task.CompletedTask;
            }
            _running = true;
        }
        return PollAsync(ct);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock) _disposed = true;
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            await PollUntilSignalAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock) _running = false;
        }
    }

    // Polls the store's exact cursor, honouring the source's per-root backoff, until it sees a change or is cancelled.
    private async Task PollUntilSignalAsync(CancellationToken ct)
    {
        var retry = _settings.AutoRetryDelay;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var cursor = _store.Cursor(_root.RootId);
            // No stored cursor yet, or the cursor that already signalled: polling it would answer instantly forever.
            if (string.IsNullOrEmpty(cursor) ||
                string.Equals(cursor, _source.SignalledCursor(_root.RootId), StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _clock, ct).ConfigureAwait(false);
                return;
            }
            var wait = _source.NotBefore(_root.RootId) - _clock.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _clock, ct).ConfigureAwait(false);
                continue;
            }
            try
            {
                var (changes, backoff) = await LongpollAsync(cursor, ct).ConfigureAwait(false);
                // The backoff survives this wait and is honoured before the next poll, even after changes:true.
                _source.MarkBackoff(_root.RootId, backoff);
                retry = _settings.AutoRetryDelay;
                if (changes)
                {
                    _source.MarkSignalled(_root.RootId, cursor);
                    return;
                }
            }
            catch (DropboxRouteException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
            {
                // 409 (an expired cursor): signal; the next run's continue fails into a full scan (CursorInvalid).
                // The invalid cursor is recorded as signalled too, so it is never polled in a tight 409 loop.
                _source.MarkSignalled(_root.RootId, cursor);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (ProviderTransientFailure.IsTransient(exception, ct))
            {
                await BackoffAsync(exception, retry, ct).ConfigureAwait(false);
                retry = Ceiling(retry);
            }
            catch (Exception exception)
            {
                throw DropboxItems.MapAccessError(exception);
            }
        }
    }

    private async Task<(bool Changes, int? Backoff)> LongpollAsync(string cursor, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["cursor"] = cursor,
            ["timeout"] = (int)_settings.DropboxLongpollTimeout.TotalSeconds
        }.ToJsonString();
        using var result = await _client.CallRouteAsync(new DropboxRouteCall(_route, body), NoRouteRetry, ct)
            .ConfigureAwait(false);
        var root = result.Result.RootElement;
        return (DropboxCliOutput.Bool(root, "changes") ?? false, (int?)DropboxCliOutput.Long(root, "backoff"));
    }

    private async Task BackoffAsync(Exception error, TimeSpan retry, CancellationToken ct)
    {
        if (FailureRetryOwner.ThrottleCause(error) is { } throttle)
            _source.MarkBackoffUntil(_root.RootId, throttle.RetryAt > _clock.GetUtcNow()
                ? throttle.RetryAt : _clock.GetUtcNow() + retry);
        else await Task.Delay(retry, _clock, ct).ConfigureAwait(false);
    }

    private TimeSpan Ceiling(TimeSpan current)
    {
        var max = _source.RetryCeiling();
        var next = current + current;
        return next >= max ? max : next;
    }
}
