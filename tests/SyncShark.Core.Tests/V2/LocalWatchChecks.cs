namespace Massdex.SyncShark.V2;

/// <summary>The local change-watch checks (continuous-sync.md section 1); run with <c>--v2-localwatch-checks &lt;root&gt;</c>.</summary>
internal static class LocalWatchChecks
{
    // A FileSystemWatcher delivers on another thread, so a fire is awaited within a generous real-time bound.
    private static readonly TimeSpan Fires = TimeSpan.FromSeconds(5);

    // The window a filtered change must stay quiet for; short, but far longer than an event delivery.
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    private static string _base = string.Empty;

    public static async Task RunAsync(string root)
    {
        _base = Path.GetFullPath(root);
        Directory.CreateDirectory(_base);
        await PendingAtStartAsync().ConfigureAwait(false);
        await CreateFiresAsync().ConfigureAwait(false);
        await ExcludedWritesStayQuietAsync().ConfigureAwait(false);
        await RenamesAcrossBoundaryAsync().ConfigureAwait(false);
        await CancelledWaitLeavesSignalPendingAsync().ConfigureAwait(false);
        await SecondConcurrentWaitThrowsAsync().ConfigureAwait(false);
        await ForcedErrorFiresAsync().ConfigureAwait(false);
        await ReplacedRootFailsAsync().ConfigureAwait(false);
        Console.WriteLine("--v2-localwatch-checks passed.");
    }

    private static async Task PendingAtStartAsync()
    {
        var root = FreshRoot("pending-at-start");
        using var stop = new CancellationTokenSource();
        var watch = await OpenAsync(root, stop.Token).ConfigureAwait(false);
        Require(await CompletesAsync(watch.ChangedAsync(stop.Token)).ConfigureAwait(false),
            "a new watch starts with a signal pending");
        watch.Dispose();
    }

    private static async Task CreateFiresAsync()
    {
        var root = FreshRoot("create-fires");
        using var stop = new CancellationTokenSource();
        var watch = await ArmedAsync(root, stop.Token).ConfigureAwait(false);
        var wait = watch.ChangedAsync(stop.Token);
        Seed(root, "a.txt", "alpha");
        Require(await CompletesAsync(wait).ConfigureAwait(false), "creating a file fires the watch");
        watch.Dispose();
    }

    private static async Task ExcludedWritesStayQuietAsync()
    {
        var root = FreshRoot("excluded");
        using var stop = new CancellationTokenSource();
        var watch = await ArmedAsync(root, stop.Token).ConfigureAwait(false);
        var wait = watch.ChangedAsync(stop.Token);
        Seed(root, ".syncshark-recovery-pair00001/old.txt", "old");
        Seed(root, ".syncshark-transfer-abcdef.partial", "staging");
        Seed(root, "db/sync.db", "product state");
        Require(!await CompletesAsync(wait, Quiet).ConfigureAwait(false),
            "writes under a recovery folder, a transfer artifact or the product-owned DB stay quiet");
        Seed(root, "normal.txt", "content");
        Require(await CompletesAsync(wait).ConfigureAwait(false), "a later ordinary write still fires");
        watch.Dispose();
    }

    private static async Task RenamesAcrossBoundaryAsync()
    {
        var root = FreshRoot("renames");
        Seed(root, ".syncshark-transfer-abcd.partial", "staging");
        using var stop = new CancellationTokenSource();
        var watch = await ArmedAsync(root, stop.Token).ConfigureAwait(false);

        var outOfArtifact = watch.ChangedAsync(stop.Token);
        File.Move(Path.Combine(root, ".syncshark-transfer-abcd.partial"), Path.Combine(root, "was-artifact.txt"));
        Require(await CompletesAsync(outOfArtifact).ConfigureAwait(false), "a rename out of an artifact name fires");

        var intoArtifact = watch.ChangedAsync(stop.Token);
        File.Move(Path.Combine(root, "was-artifact.txt"), Path.Combine(root, ".syncshark-transfer-efgh.partial"));
        Require(await CompletesAsync(intoArtifact).ConfigureAwait(false), "a rename into an artifact name fires");

        var artifactToArtifact = watch.ChangedAsync(stop.Token);
        File.Move(Path.Combine(root, ".syncshark-transfer-efgh.partial"),
            Path.Combine(root, ".syncshark-transfer-ijkl.partial"));
        Require(!await CompletesAsync(artifactToArtifact, Quiet).ConfigureAwait(false),
            "a rename between two artifact names stays quiet");
        watch.Dispose();
    }

    private static async Task CancelledWaitLeavesSignalPendingAsync()
    {
        var root = FreshRoot("cancelled-wait");
        using var stop = new CancellationTokenSource();
        var watch = await ArmedAsync(root, stop.Token).ConfigureAwait(false);
        using (var cancel = new CancellationTokenSource())
        {
            var cancelled = watch.ChangedAsync(cancel.Token);
            cancel.Cancel();
            Require(await FaultAsync(cancelled).ConfigureAwait(false) is OperationCanceledException,
                "a cancelled wait ends as cancellation");
        }
        Seed(root, "late.txt", "content");
        Require(await CompletesAsync(watch.ChangedAsync(stop.Token)).ConfigureAwait(false),
            "a signal after a cancelled wait is not lost");
        watch.Dispose();
    }

    private static async Task SecondConcurrentWaitThrowsAsync()
    {
        var root = FreshRoot("concurrent-wait");
        using var stop = new CancellationTokenSource();
        var watch = await ArmedAsync(root, stop.Token).ConfigureAwait(false);
        var first = watch.ChangedAsync(stop.Token);
        var threw = false;
        try { _ = watch.ChangedAsync(stop.Token); }
        catch (InvalidOperationException) { threw = true; }
        Require(threw, "a second concurrent wait throws");
        stop.Cancel();
        await FaultAsync(first).ConfigureAwait(false);
        watch.Dispose();
    }

    private static async Task ForcedErrorFiresAsync()
    {
        var root = FreshRoot("forced-error");
        using var stop = new CancellationTokenSource();
        var watch = (LocalChangeWatch)await OpenAsync(root, stop.Token).ConfigureAwait(false);
        await watch.ChangedAsync(stop.Token).ConfigureAwait(false);
        var wait = watch.ChangedAsync(stop.Token);
        watch.RaiseErrorForCheck();
        Require(await CompletesAsync(wait).ConfigureAwait(false), "a watcher error fires the wait");
        watch.Dispose();
    }

    private static async Task ReplacedRootFailsAsync()
    {
        var root = FreshRoot("replaced-root");
        var source = new LocalChangeWatchSource(ProductOwned(root));
        var rootDef = RootOf(root);
        using var stop = new CancellationTokenSource();
        (await source.WatchAsync(rootDef, stop.Token).ConfigureAwait(false)).Dispose();
        Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        var error = await FaultAsync(source.WatchAsync(rootDef, stop.Token)).ConfigureAwait(false);
        Require(error is IOException, "a recreated root with another file id fails WatchAsync");
    }

    private static string FreshRoot(string name)
    {
        var directory = Path.Combine(_base, name);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IReadOnlySet<string> ProductOwned(string root) =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(root, "db") };

    private static async Task<LocalChangeWatch> OpenAsync(string root, CancellationToken ct)
    {
        var source = new LocalChangeWatchSource(ProductOwned(root));
        return (LocalChangeWatch)await source.WatchAsync(RootOf(root), ct).ConfigureAwait(false);
    }

    private static async Task<IChangeWatch> ArmedAsync(string root, CancellationToken ct)
    {
        var watch = await OpenAsync(root, ct).ConfigureAwait(false);
        await watch.ChangedAsync(ct).ConfigureAwait(false);
        return watch;
    }

    private static RootDef RootOf(string root)
    {
        using var lease = PilotDirectoryLease.Open(root);
        var objectId = SyncLocalEndpointStore.ObjectId(lease.Identity.VolumeSerialNumber, lease.Identity.FileId);
        return new RootDef("root-local", SyncProviders.Local, null, root, objectId);
    }

    // Awaiting the winner rethrows its fault, so a dead watch can never pass as "fired".
    private static async Task<bool> CompletesAsync(Task task, TimeSpan? bound = null)
    {
        var limit = bound ?? Fires;
        if (await Task.WhenAny(task, Task.Delay(limit)).ConfigureAwait(false) != task) return false;
        await task.ConfigureAwait(false);
        return true;
    }

    private static async Task<Exception> FaultAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return error;
        }
        throw new InvalidOperationException("local watch check failed: the wait was expected to fault.");
    }

    private static void Seed(string root, string relative, string text)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("local watch check failed: " + label);
        Console.WriteLine("PASS " + label);
    }
}
