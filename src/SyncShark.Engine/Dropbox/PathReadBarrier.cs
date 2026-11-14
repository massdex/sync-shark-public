namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Joins concurrent point reads of one exact path. A caller shares only a read that started after it asked, so a
/// joined answer is never older than the caller's own read would be; a read already running when it asked only holds it
/// back. Only running reads are registered.</summary>
internal sealed class PathReadBarrier(Func<string, CancellationToken, Task<Entry?>> read)
{
    private readonly Dictionary<string, Read> _running = new(StringComparer.Ordinal);
    private long _started;

    // One registered read: its start order, its outcome, and whether it ended because its producer was stopped.
    internal sealed class Read(long started)
    {
        internal long Started { get; } = started;
        internal TaskCompletionSource<Entry?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ProducerCancelled { get; set; }
    }

    internal async Task<Entry?> ReadAsync(string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        long asked;
        lock (_running) asked = _started;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Read current;
            var mine = false;
            lock (_running)
            {
                if (!_running.TryGetValue(path, out current!))
                {
                    current = new Read(++_started);
                    _running[path] = current;
                    mine = true;
                }
            }
            if (mine) await ProduceAsync(path, current, ct).ConfigureAwait(false);
            // A read registered before this caller asked only holds it back; one registered after is its answer.
            var result = await AwaitReadAsync(current, current.Started > asked, ct).ConfigureAwait(false);
            if (!result.Again) return result.Value;
        }
    }

    internal static async ValueTask<(bool Again, Entry? Value)> AwaitReadAsync(Read current, bool answer, CancellationToken ct)
    {
        try
        {
            var value = await current.Done.Task.WaitAsync(ct).ConfigureAwait(false);
            return (!answer, value);
        }
        // Every answer outcome belongs to this caller, including terminal cancellation, unless its producer stopped.
        catch (Exception) when (!answer || !ct.IsCancellationRequested && current.ProducerCancelled)
        {
            return (true, null);
        }
    }

    private async Task ProduceAsync(string path, Read current, CancellationToken ct)
    {
        Entry? value = null;
        Exception? failure = null;
        try { value = await read(path, ct).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        // Only a cancellation carrying the producer's own token is the producer's stop; any other failure is the request
        // owner's outcome for the path, even when the producer's token has been cancelled since.
        current.ProducerCancelled = IsProducerCancellation(failure, ct);
        // Unregistered before completion: a caller released by this read never finds it still running.
        lock (_running) _running.Remove(path);
        if (failure is null) current.Done.SetResult(value);
        else current.Done.SetException(failure);
    }

    internal static bool IsProducerCancellation(Exception? failure, CancellationToken producer) =>
        failure is OperationCanceledException;
}
