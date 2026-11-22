namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Acquire the whole physical footprint together, across all pairs on the host.</summary>
public sealed class ItemLocks
{
    private static readonly object Gate = new();
    private static readonly SortedSet<string> Held = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Trees = new(StringComparer.Ordinal);
    private static TaskCompletionSource changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static string Physical(PairConfig pair, Side side, string path) => side == Side.Local
        ? PathNames.Key(PathNames.NormalizeLocalPath(Path.Combine(pair.LocalRoot, path)).Replace('\','/').TrimEnd('/'))
        : pair.ConnectionId + ":" + PathNames.DropboxKey(PathNames.RootedDropboxPath(pair.DropboxRoot,path)).TrimEnd('/');

    /// <summary>The legacy shape requests exact endpoints, not their children.</summary>
    public ValueTask<IDisposable> AcquireAsync(IReadOnlyList<(Side Side,string Key)> footprint, CancellationToken ct) =>
        AcquireAsync(footprint.Select(x => (x.Side,x.Key,false)).ToArray(),ct);

    /// <summary>Keys are physical endpoint keys. Only a recursive effect locks descendants.</summary>
    public async ValueTask<IDisposable> AcquireAsync(IReadOnlyList<(Side Side,string Key,bool Subtree)> footprint, CancellationToken ct)
    {
        var keys = footprint.GroupBy(x => x.Side + ":" + PathNames.TreeKey(x.Key),StringComparer.Ordinal)
            .Select(g => (Key:g.Key,Tree:g.Any(x => x.Subtree))).ToArray();
        while (true)
        {
            Task signal;
            lock (Gate)
            {
                ct.ThrowIfCancellationRequested();
                if (!keys.Any(x => Held.Contains(x.Key) || PathNames.Ancestors(x.Key).Any(Trees.Contains) ||
                    x.Tree && PathNames.HasDescendantKey(Held,x.Key)))
                {
                    foreach (var x in keys) { Held.Add(x.Key); if (x.Tree) Trees.Add(x.Key); }
                    return new Lease(keys);
                }
                signal = changed.Task;
            }
            await signal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class Lease((string Key,bool Tree)[] keys) : IDisposable
    {
        private bool released;
        public void Dispose()
        {
            lock (Gate)
            {
                if (released) return;
                released = true;
                foreach (var x in keys) { Held.Remove(x.Key); if (x.Tree) Trees.Remove(x.Key); }
                var old = changed; changed = NewSignal(); old.SetResult();
            }
        }
    }
}
