using System.Collections.Concurrent;

namespace Massdex.SyncShark;

/// <summary>Bounds outstanding conditional-delete jobs per namespace, separately from write request admission.</summary>
internal sealed class DropboxDeleteJobGate
{
    private static readonly ConcurrentDictionary<string, DropboxDeleteJobGate> Gates = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private TaskCompletionSource _changed = NewSignal();
    private int _active;
    internal const int MaximumConcurrency = 4;
    private int _limit = 2;
    private int _successes;

    internal static DropboxDeleteJobGate For(string account, string rootNamespace) =>
        Gates.GetOrAdd(account + "\n" + rootNamespace, _ => new());

    internal async Task EnterAsync()
    {
        using var wait = V2.RunOperations.Begin(V2.RunOperationKind.Gate, "delete-job admission");
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_active < _limit) { _active++; return; }
                changed = _changed.Task;
            }
            await changed.ConfigureAwait(false);
        }
    }

    internal void Throttled()
    {
        lock (_gate) { _limit = Math.Max(1, _limit / 2); _successes = 0; }
    }

    internal void Exit(bool successful)
    {
        lock (_gate)
        {
            _active--;
            // Recover cautiously; only a batch with no throttle signal increases admission.
            if (successful && ++_successes >= 4)
            { _limit = Math.Min(MaximumConcurrency, _limit + 1); _successes = 0; }
            var previous = _changed;
            _changed = NewSignal();
            previous.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
