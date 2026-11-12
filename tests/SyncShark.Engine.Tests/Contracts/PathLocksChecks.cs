namespace Massdex.SyncShark.Engine.Tests.Contracts;

/// <summary>Checks for PathLocks (`--engine-checks locks`), written by PathLocks's writer.</summary>
public static class PathLocksChecks
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var pair = new PairConfig("pair", "pair",
            Path.Combine(Path.GetTempPath(), nameof(PathLocksChecks)),
            PathNames.PathSeparator + nameof(PathLocksChecks), nameof(PairConfig.ConnectionId),
            Array.Empty<string>(), Array.Empty<Exclusion>(), FirstRunPolicy.NewestWins, true, null);
        var other = pair with { PairId = "other", Name = "other" };
        var path = PathNames.Child(nameof(PairConfig), nameof(PathLocksChecks));
        var nested = other with
        {
            LocalRoot = Path.Combine(pair.LocalRoot, nameof(PairConfig)),
            DropboxRoot = PathNames.Child(pair.DropboxRoot, nameof(PairConfig))
        };
        var account = pair with { ConnectionId = "account" };
        var root = pair with { DropboxRoot = PathNames.PathSeparator };
        var emptyRoot = pair with { DropboxRoot = "" };

        Check.Require(ReferenceEquals(PathLocks.KeyComparer, StringComparer.Ordinal), nameof(PathLocks.KeyComparer));

        Check.Require(PathLocks.LocalKey(pair, path) == PathNames.Key(Path.GetFullPath(Path.Combine(pair.LocalRoot, path))),
            nameof(PathLocks.LocalKey));
        Check.Require(PathLocks.DropboxKey(pair, path) == pair.ConnectionId + PathLocks.KeySeparator +
            PathNames.DropboxKey(PathNames.Child(pair.DropboxRoot, path)), nameof(PathLocks.DropboxKey));
        Check.Require(PathLocks.DropboxKey(root, path) == root.ConnectionId + PathLocks.KeySeparator +
            PathNames.DropboxKey(PathNames.PathSeparator + path), nameof(root));
        Check.Require(PathLocks.DropboxKey(root, "") == root.ConnectionId + PathLocks.KeySeparator +
            PathNames.PathSeparator, nameof(root));
        Check.Require(PathLocks.DropboxKey(emptyRoot, "") == emptyRoot.ConnectionId + PathLocks.KeySeparator +
            PathNames.DropboxKey(PathNames.PathSeparator), "an empty provider root keeps the lock-root sentinel");
        Check.Require(PathLocks.DropboxKey(emptyRoot, path) == emptyRoot.ConnectionId + PathLocks.KeySeparator +
            PathNames.DropboxKey(PathNames.PathSeparator + path), nameof(emptyRoot));
        Check.Require(PathLocks.LocalKey(pair, "") == PathNames.Key(pair.LocalRoot), nameof(pair.LocalRoot));
        Check.Require(PathLocks.DropboxKey(pair, "") == pair.ConnectionId + PathLocks.KeySeparator +
            PathNames.DropboxKey(pair.DropboxRoot), nameof(pair.DropboxRoot));
        Check.Require(PathLocks.DropboxKey(pair, nameof(path) + PathLocks.KeySeparator) == pair.ConnectionId +
            PathLocks.KeySeparator + PathNames.DropboxKey(PathNames.Child(pair.DropboxRoot,
                nameof(path) + PathLocks.KeySeparator)), nameof(PathNames.DropboxKey));

        var sameLocations = new[]
        {
            (PathLocks.LocalKey(pair, path), PathLocks.LocalKey(pair, path)),
            (PathLocks.DropboxKey(pair, path), PathLocks.DropboxKey(pair, path)),
            (PathLocks.LocalKey(pair, path), PathLocks.LocalKey(other, path)),
            (PathLocks.DropboxKey(pair, path), PathLocks.DropboxKey(other, path)),
            (PathLocks.LocalKey(pair, path), PathLocks.LocalKey(nested, nameof(PathLocksChecks))),
            (PathLocks.DropboxKey(pair, path), PathLocks.DropboxKey(nested, nameof(PathLocksChecks))),
            (PathLocks.LocalKey(pair, path), PathLocks.LocalKey(other, path.ToUpperInvariant())),
            (PathLocks.DropboxKey(pair, path), PathLocks.DropboxKey(other, path.ToUpperInvariant())),
            (PathLocks.LocalKey(pair, path), PathLocks.LocalKey(
                other with { LocalRoot = other.LocalRoot + Path.DirectorySeparatorChar }, path)),
            (PathLocks.DropboxKey(pair, path), PathLocks.DropboxKey(
                other with { DropboxRoot = other.DropboxRoot + PathNames.PathSeparator }, path))
        };
        foreach (var (first, second) in sameLocations)
        {
            Check.Require(first == second, nameof(sameLocations));
            await SameKeyWaitsAsync(first, second);
        }

        var folder = new PathLockScope(PathLocks.LocalKey(pair, nameof(path)), true);
        var child = new PathLockScope(PathLocks.LocalKey(pair, PathNames.Child(nameof(path), nameof(pair))), false);
        Check.Require(PathLocks.Covers(folder, child), nameof(PathLocks.Covers));
        var subtree = await PathLocks.Shared.EnterAsync([folder], CancellationToken.None);
        var underneath = PathLocks.Shared.EnterAsync([child], CancellationToken.None);
        Check.Require(!underneath.IsCompleted, nameof(subtree));
        await subtree.DisposeAsync();
        await (await underneath).DisposeAsync();

        var differentLocations = new[]
        {
            (PathLocks.LocalKey(pair, path), PathLocks.LocalKey(pair, nameof(other))),
            (PathLocks.DropboxKey(pair, path), PathLocks.DropboxKey(pair, nameof(other))),
            (PathLocks.DropboxKey(pair, path), PathLocks.DropboxKey(account, path)),
            (PathLocks.LocalKey(pair, path), PathLocks.DropboxKey(pair, path))
        };
        foreach (var (first, second) in differentLocations)
        {
            var locks = new PathLocks();
            await using var hold = await locks.EnterAsync(new[] { first }, CancellationToken.None);
            var next = locks.EnterAsync(new[] { second }, CancellationToken.None);
            Check.Require(next.IsCompletedSuccessfully, nameof(differentLocations));
            await using var nextHold = await next;
        }

        await AllKeysTogetherAsync();
        await FolderFootprintAsync(pair);
        PairDefinitionKeys(pair);
        await ReleaseSignalsWaitersAsync();
        await CancellationAsync();
        await CancellationIndependenceAsync();
        await UnrelatedReleaseKeepsTargetWaitingAsync();
        await OwnershipAsync();
    }

    private static async Task SameKeyWaitsAsync(string first, string second)
    {
        var locks = PathLocks.Shared;
        await using var hold = await locks.EnterAsync(new[] { first }, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var next = locks.EnterAsync(new[] { second }, cancellation.Token);
        try
        {
            Check.Require(!next.IsCompleted, nameof(SameKeyWaitsAsync));
            await hold.DisposeAsync();
            await using var nextHold = await next;
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    private static async Task AllKeysTogetherAsync()
    {
        var locks = new PathLocks();
        var first = "first";
        var second = "second";
        await using var firstHold = await locks.EnterAsync(new[] { first }, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var both = locks.EnterAsync(new[] { second, first }, cancellation.Token);
        try
        {
            Check.Require(!both.IsCompleted, nameof(AllKeysTogetherAsync));
            var secondTask = locks.EnterAsync(new[] { second }, cancellation.Token);
            Check.Require(secondTask.IsCompletedSuccessfully, nameof(secondTask));
            await using var secondHold = await secondTask;
            await firstHold.DisposeAsync();
            Check.Require(!both.IsCompleted, nameof(both));
            await secondHold.DisposeAsync();
            await using var bothHold = await both;
            foreach (var key in new[] { first, second })
            {
                using var blockedCancellation = new CancellationTokenSource();
                var blocked = locks.EnterAsync(new[] { key }, blockedCancellation.Token);
                Check.Require(!blocked.IsCompleted, nameof(blocked));
                blockedCancellation.Cancel();
                await RequireCanceledAsync(blocked, blockedCancellation.Token);
            }
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    // A folder's own key never covers its subtree; live work passes every descendant key too (engine.md 2.3).
    private static async Task FolderFootprintAsync(PairConfig pair)
    {
        var locks = new PathLocks();
        var folder = PathNames.Child(nameof(FolderFootprintAsync), "Folder");
        var child = PathNames.Child(folder, "Child");
        await using (var own = await locks.EnterAsync(new[] { PathLocks.LocalKey(pair, folder) },
            CancellationToken.None))
        {
            var childOnly = locks.EnterAsync(new[] { PathLocks.LocalKey(pair, child) }, CancellationToken.None);
            Check.Require(childOnly.IsCompletedSuccessfully, nameof(FolderFootprintAsync));
            await using var childHold = await childOnly;
        }
        var footprint = new[]
        {
            PathLocks.LocalKey(pair, folder),
            PathLocks.LocalKey(pair, child),
            PathLocks.LocalKey(pair, PathNames.Child(child, "Grandchild"))
        };
        await using var held = await locks.EnterAsync(footprint, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var blocked = locks.EnterAsync(new[] { footprint[1] }, cancellation.Token);
        Check.Require(!blocked.IsCompleted, nameof(held));
        cancellation.Cancel();
        await RequireCanceledAsync(blocked, cancellation.Token);
    }

    // The optional saved root binding and ignored-folder keep rules are pair members, not lock identity.
    private static void PairDefinitionKeys(PairConfig pair)
    {
        var bound = pair with
        {
            Roots = new RootBinding(pair.LocalRoot, pair.DropboxRoot, pair.ConnectionId,
                new DirectoryIdentity(1UL, 2UL, null), new DropboxRootIdentity("account", "root", "home", "folder")),
            IgnoredFolderKeepRules = new[] { "kind:value" }
        };
        foreach (var path in new[] { nameof(PathLocksChecks), "", nameof(PairDefinitionKeys) })
        {
            Check.Require(PathLocks.LocalKey(bound, path) == PathLocks.LocalKey(pair, path),
                "The saved root binding does not change the local lock key.");
            Check.Require(PathLocks.DropboxKey(bound, path) == PathLocks.DropboxKey(pair, path),
                "The saved root binding does not change the Dropbox lock key.");
        }
    }

    private static async Task ReleaseSignalsWaitersAsync()
    {
        var locks = new PathLocks();
        var first = "first";
        var second = "second";
        await using var hold = await locks.EnterAsync(new[] { first, second }, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var firstWaiter = locks.EnterAsync(new[] { first }, cancellation.Token);
        var secondWaiter = locks.EnterAsync(new[] { second }, cancellation.Token);
        try
        {
            Check.Require(!firstWaiter.IsCompleted && !secondWaiter.IsCompleted, nameof(ReleaseSignalsWaitersAsync));
            await hold.DisposeAsync();
            await using var firstHold = await firstWaiter;
            await using var secondHold = await secondWaiter;
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    private static async Task CancellationAsync()
    {
        var locks = new PathLocks();
        var keys = new[] { nameof(CancellationAsync) };
        using var cancellation = new CancellationTokenSource();
        await using var hold = await locks.EnterAsync(keys, CancellationToken.None);
        var waiting = locks.EnterAsync(keys, cancellation.Token);
        Check.Require(!waiting.IsCompleted, nameof(waiting));
        cancellation.Cancel();
        await RequireCanceledAsync(waiting, cancellation.Token);
        await hold.DisposeAsync();
        await RequireCanceledAsync(locks.EnterAsync(keys, cancellation.Token), cancellation.Token);
        var next = locks.EnterAsync(keys, CancellationToken.None);
        Check.Require(next.IsCompletedSuccessfully, nameof(CancellationAsync));
        await using var nextHold = await next;
    }

    private static async Task CancellationIndependenceAsync()
    {
        var locks = new PathLocks();
        var key = nameof(CancellationIndependenceAsync);
        await using var hold = await locks.EnterAsync(new[] { key }, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var canceled = locks.EnterAsync(new[] { key }, cancellation.Token);
        var waiting = locks.EnterAsync(new[] { key }, CancellationToken.None);
        cancellation.Cancel();
        await RequireCanceledAsync(canceled, cancellation.Token);
        Check.Require(!waiting.IsCompleted, nameof(CancellationIndependenceAsync));
        await hold.DisposeAsync();
        await using var waitingHold = await waiting;
    }

    private static async Task UnrelatedReleaseKeepsTargetWaitingAsync()
    {
        var locks = new PathLocks();
        var target = nameof(UnrelatedReleaseKeepsTargetWaitingAsync);
        var other = "other";
        await using var targetHold = await locks.EnterAsync(new[] { target }, CancellationToken.None);
        await using var otherHold = await locks.EnterAsync(new[] { other }, CancellationToken.None);
        var waiter = locks.EnterAsync(new[] { target }, CancellationToken.None);
        Check.Require(!waiter.IsCompleted, nameof(UnrelatedReleaseKeepsTargetWaitingAsync));
        await otherHold.DisposeAsync();
        Check.Require(!waiter.IsCompleted, nameof(otherHold));
        await targetHold.DisposeAsync();
        await using var waiterHold = await waiter;
    }

    private static async Task RequireCanceledAsync(Task<IAsyncDisposable> task, CancellationToken ct)
    {
        try
        {
            await using var unexpected = await task;
            Check.Require(false, nameof(RequireCanceledAsync));
        }
        catch (OperationCanceledException exception)
        {
            Check.Require(exception.CancellationToken == ct, nameof(CancellationToken));
        }
    }

    private static async Task OwnershipAsync()
    {
        var locks = new PathLocks();
        var key = nameof(OwnershipAsync);
        var keys = new[] { key, key };
        await using var hold = await locks.EnterAsync(keys, CancellationToken.None);
        keys[0] = nameof(keys);
        await hold.DisposeAsync();
        var next = locks.EnterAsync(new[] { key }, CancellationToken.None);
        Check.Require(next.IsCompletedSuccessfully, nameof(OwnershipAsync));
        await using var nextHold = await next;
        await hold.DisposeAsync();
        using var cancellation = new CancellationTokenSource();
        var waiting = locks.EnterAsync(new[] { key }, cancellation.Token);
        Check.Require(!waiting.IsCompleted, nameof(IAsyncDisposable.DisposeAsync));
        cancellation.Cancel();
        await RequireCanceledAsync(waiting, cancellation.Token);
        await using var empty = await locks.EnterAsync(Array.Empty<string>(), CancellationToken.None);
    }
}
