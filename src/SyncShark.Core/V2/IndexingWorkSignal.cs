using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.V2;

/// <summary>Per-store, in-memory work notification. Writers publish; readers never advance the version.</summary>
internal sealed class IndexingWorkSignal
{
    private static readonly ConditionalWeakTable<IStateStore, IndexingWorkSignal> Signals = new();
    private readonly object _gate = new();
    private long _version;
    private TaskCompletionSource _changed = NewCompletion();

    internal static IndexingWorkSignal For(IStateStore store) => Signals.GetValue(store, static _ => new());
    internal long Version { get { lock (_gate) return _version; } }

    internal Task WaitAsync(long afterVersion, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task pending;
        lock (_gate) pending = afterVersion == _version ? _changed.Task : Task.CompletedTask;
        // Cancellation belongs to this waiter, never to the shared completion source.
        return pending.WaitAsync(ct);
    }

    internal static void Publish(IStateStore store)
    {
        var signal = For(store);
        TaskCompletionSource previous;
        lock (signal._gate)
        {
            signal._version++;
            previous = signal._changed;
            signal._changed = NewCompletion();
        }
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
