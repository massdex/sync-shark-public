namespace Massdex.SyncShark.Engine;

/// <summary>Process-wide locks keyed on real locations, so pairs sharing a folder wait for each other (2.3).
/// Keys come from the PathNames facade, so this class adds no mapping or validation of its own.
/// Each outer operation enters once with all its keys; inner steps never enter.</summary>
public sealed class PathLocks
{
    /// <summary>Separates the connection id from the Dropbox key in a lock key.</summary>
    public const string KeySeparator = ":";

    /// <summary>The one comparer for lock keys; they are the frozen PathNames keys, so ordinal is their identity.</summary>
    public static StringComparer KeyComparer { get; } = StringComparer.Ordinal;

    private readonly object gate = new();
    private readonly SortedSet<string> held = new(KeyComparer);
    private readonly HashSet<string> subtrees = new(KeyComparer);
    private readonly HashSet<TaskCompletionSource> waiters = new();

    /// <summary>The process-wide instance all pairs use.</summary>
    public static PathLocks Shared { get; } = new();

    /// <summary>The real local location of one pair-relative path, the one projection callers share.</summary>
    public static string LocalNativePath(PairConfig pair, string path) => Path.Combine(pair.LocalRoot, path);

    /// <summary>The real Dropbox location of one pair-relative path, the one projection callers share.</summary>
    public static string DropboxNativePath(PairConfig pair, string path) => PathNames.RootedDropboxPath(pair.DropboxRoot, path);

    /// <summary>Lock key for a local path: its real full path as a frozen path key.</summary>
    public static string LocalKey(PairConfig pair, string path) => LocalNativeKey(LocalNativePath(pair, path));

    public static string LocalNativeKey(string fullPath) => PathNames.Key(PathNames.NormalizeLocalPath(fullPath));

    /// <summary>Lock key for a Dropbox path: connection id plus the frozen key of the Dropbox path.</summary>
    public static string DropboxKey(PairConfig pair, string path) =>
        DropboxNativeKey(pair.ConnectionId, DropboxNativePath(pair, path));

    public static string DropboxNativeKey(string connectionId, string fullPath) => DropboxKey(connectionId, fullPath);

    private static string DropboxKey(string connectionId, string fullPath)
    {
        // The provider account root is "", but its lock identity stays the root sentinel.
        if (fullPath.Length == 0) fullPath = PathNames.PathSeparator;
        return connectionId + KeySeparator + PathNames.DropboxKey(fullPath);
    }

    /// <summary>Takes all keys together or waits; disposing releases them. Exact keys only: a caller that
    /// needs a whole ancestor/descendant footprint passes that complete key set (engine.md 2.3).</summary>
    public Task<IAsyncDisposable> EnterAsync(IReadOnlyCollection<string> keys, CancellationToken ct) =>
        EnterAsync(keys.Select(key => new PathLockScope(key, false)).ToArray(), ct);

    public static bool Covers(PathLockScope heldScope, PathLockScope required) =>
        (heldScope.Key == required.Key && (!required.IncludesDescendants || heldScope.IncludesDescendants)) ||
        heldScope.IncludesDescendants && PathNames.IsUnderKey(PathNames.TreeKey(required.Key), PathNames.TreeKey(heldScope.Key));

    public async Task<IAsyncDisposable> EnterAsync(IReadOnlyList<PathLockScope> scopes, CancellationToken ct)
    {
        var ownedKeys = scopes.GroupBy(scope => PathNames.TreeKey(scope.Key), KeyComparer)
            .Select(group => new PathLockScope(group.Key, group.Any(scope => scope.IncludesDescendants))).ToArray();
        while (true)
        {
            TaskCompletionSource waiter;
            lock (gate)
            {
                ct.ThrowIfCancellationRequested();
                if (!ownedKeys.Any(Conflicts))
                {
                    foreach (var scope in ownedKeys)
                    {
                        held.Add(scope.Key);
                        if (scope.IncludesDescendants) subtrees.Add(scope.Key);
                    }
                    return new Lease(this, ownedKeys);
                }
                waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add(waiter);
            }
            try
            {
                await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                lock (gate) waiters.Remove(waiter);
                throw;
            }
        }
    }

    private bool Conflicts(PathLockScope scope)
    {
        if (held.Contains(scope.Key) || PathNames.Ancestors(scope.Key).Any(subtrees.Contains)) return true;
        if (!scope.IncludesDescendants) return false;
        var bounds = PathNames.DescendantBounds(scope.Key);
        return held.GetViewBetween(bounds.Low, bounds.High).Any(key => PathNames.IsUnderKey(key, scope.Key));
    }

    private void Release(PathLockScope[] keys)
    {
        lock (gate)
        {
            foreach (var scope in keys)
            {
                held.Remove(scope.Key);
                subtrees.Remove(scope.Key);
            }
            foreach (var waiter in waiters) waiter.TrySetResult();
            waiters.Clear();
        }
    }

    private sealed class Lease(PathLocks owner, PathLockScope[] keys) : IAsyncDisposable
    {
        private PathLocks? owner = owner;

        /// <summary>Releases the keys once, allowing waiters to proceed.</summary>
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref owner, null)?.Release(keys);
            return ValueTask.CompletedTask;
        }
    }
}
