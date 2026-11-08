using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Destination paths and parent folders (section 7.5).</summary>
public sealed class Parents(ExecutorContext context)
{
    private readonly ConcurrentDictionary<(Side Side, string Key), TaskCompletionSource> _creating = new();

    internal async Task JoinCreationAsync(Side side, string key, CancellationToken ct)
    {
        if (_creating.TryGetValue((side, key), out var creation)) await creation.Task.WaitAsync(ct);
    }

    /// <summary>The counterpart path: the saved path when a binding exists, else the destination through the
    /// nearest linked ancestor.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string Counterpart(Side to, string fromPath, SyncedEntry? saved) =>
        saved?.PathOn(to) ?? Destination(to, fromPath);

    /// <summary>Maps an item's destination through its nearest linked ancestor.</summary>
    public string Destination(Side to, string fromPath) =>
        PathNames.MappedDestination(to, fromPath, context,
            static (owner, side, key) => owner.Store.SyncedByKey(owner.Pair.PairId, side, key));

    /// <summary>Runs inside the action's hold and takes no keys; the target is read here because nothing observed it.</summary>
    public async Task EnsureAsync(Side target, string path, CancellationToken ct)
    {
        _ = path.Length; // a null path must fault with NullReferenceException, as the old LastIndexOf did
        var separator = PathNames.LastSeparator(path);
        if (separator < 0) return;

        await EnsureAsync(target, path, await context.For(target).ReadOneAsync(path, ct), ct);
    }

    // One ancestor creation: concurrent callers join the same creation through the shared _creating gate.
    /// <summary>Runs inside the action's hold when a read in that same hold already observed the target (BATCHING 4.6);
    /// null is an observed absence, never an expected-null for a conditional create.</summary>
    public async Task EnsureAsync(Side target, string path, Entry? observedTarget, CancellationToken ct)
    {
        _ = path.Length; // a null path must fault with NullReferenceException, as the old LastIndexOf did
        var separator = PathNames.LastSeparator(path);
        if (separator < 0) return;

        var targetSide = context.For(target);
        // An existing target already has its folders; only a create needs them prepared.
        if (observedTarget is not null) return;

        // A create needs its immediate parent first; farther ancestors are read only when it is missing.
        var parent = path[..separator];
        if (await targetSide.ReadOneAsync(parent, ct) is not null) return;

        var ancestors = new Stack<string>();
        var ancestor = parent;
        do
        {
            ancestors.Push(ancestor);
            ancestor = PathNames.Parent(ancestor);
        } while (ancestor.Length > 0 && await targetSide.ReadOneAsync(ancestor, ct) is null);

        var other = PathNames.Other(target);
        var otherSide = context.For(other);
        foreach (var missing in ancestors)
        {
            var key = (target, PathNames.KeyFor(target, missing));
            var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var current = _creating.GetOrAdd(key, mine);
                if (ReferenceEquals(current, mine)) break;
                await current.Task.WaitAsync(ct);
            }
            try
            {
                ItemSync.RequireLiveWrite(context, target == Side.Local);
                var created = await targetSide.EnsureFolderAsync(missing, ct);
                var saved = context.Store.SyncedByKey(context.Pair.PairId, target, key.Item2);
                var otherPath = Counterpart(other, missing, saved);
                var present = await otherSide.ReadOneAsync(otherPath, ct);
                if (present is null) continue;

                var record = SyncedEntries.From(target, created, present);
                var change = await RecordBindings.PrepareAsync(context,
                    StateChange.Empty with { Record = [record] }, ct);
                context.Store.Commit(context.Pair.PairId, change);
            }
            finally
            {
                _creating.TryRemove(key, out _);
                mine.SetResult();
            }
        }
    }

}
