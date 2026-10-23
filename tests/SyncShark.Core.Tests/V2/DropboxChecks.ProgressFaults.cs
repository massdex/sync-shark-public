namespace Massdex.SyncShark.V2;

// Every progress/display callback is best-effort: a throwing sink must not fail a scan, an apply or a run.
internal static partial class DropboxChecks
{
    /// <summary>A sink that fails on every report, so the test proves the guard and the call happened.</summary>
    private sealed class ThrowingProgress<T> : IProgress<T>
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public void Report(T value)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("Injected display fault: progress sink failed.");
        }
    }

    private static async Task ProgressCallbackFaultsAsync()
    {
        using var h = new Harness();
        var product = Path.Combine(_root, "progress-fault");
        var localPath = Path.Combine(product, "local");
        Directory.CreateDirectory(localPath);
        var settings = StorageChecks.Fast with { MaxWorkers = 1, ProgressInterval = TimeSpan.Zero };
        using var store = SqliteStateStore.Open(Path.Combine(product, "state"), settings, TimeProvider.System);
        var pair = new PairDef("pf", "throwing progress", LocalChecks.RootOf(localPath, "local"), Root(), [], [], true);
        store.SavePair(pair);
        store.MarkFirstSyncDone(pair.PairId);
        var localWriter = await LocalWriter.OpenAsync(pair.A, Side.A, pair.PairId, LocalChecks.InstallationId,
            Path.Combine(product, "recovery"), CancellationToken.None, isStateDamage: StorageFailureClassifier.IsStateDamage, settings);
        var endpoints = new FakeEndpointProvider().AddWriter(pair.A, localWriter)
            .AddInventory(pair.A, new LocalInventory(pair.A, Side.A, store, new HashSet<string>(), settings, TimeProvider.System, isStateDamage: StorageFailureClassifier.IsStateDamage))
            .AddInventory(pair.B, new DropboxInventory(pair.B, Side.B, h.Client, store, pair.PairId))
            .AddWriter(pair.B, h.Writer(Path.Combine(product, "spool")));
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage), endpoints, settings,
            TimeProvider.System);
        h.Wire.LatestCursor = "pf-c0";
        h.Wire.SeedChangePage("pf-c0", [], false, "pf-c0");
        h.Wire.SeedFile("listed.txt", "listed");

        // A full listing (the server-side scan and the deletion-evidence reads) survives a throwing string sink.
        var listingSink = new ThrowingProgress<string>();
        var snapshot = await new DropboxInventory(pair.B, Side.B, h.Client, store, pair.PairId)
            .FullAsync(1, _ => true, listingSink, CancellationToken.None);
        Require(listingSink.Calls > 0 && snapshot.Items.Any(item => item.Path == "listed.txt"),
            "a throwing listing progress sink notifies, yet the full scan still completes");

        // A representative run (scan plus apply) survives a throwing run-progress sink.
        var runSink = new ThrowingProgress<RunProgress>();
        var first = await run.IncrementalAsync(pair.PairId, runSink, CancellationToken.None);
        var before = store.Cursor(RootId);
        Require(first.Failed == 0 && before is { Length: > 0 },
            "a throwing run-progress sink cannot fail the scan phase");
        h.Wire.SeedFile("download.txt", "downloaded despite the display fault");
        h.Wire.SeedChangePage(before!, [h.Wire.ChangeExistingFile("download.txt")], false, "pf-after");
        var applied = await run.IncrementalAsync(pair.PairId, runSink, CancellationToken.None);
        Require(applied.Failed == 0 && runSink.Calls > 0 &&
            await File.ReadAllTextAsync(Path.Combine(localPath, "download.txt")) == "downloaded despite the display fault" &&
            store.Cursor(RootId) == "pf-after",
            "a throwing run-progress sink cannot fail the scan-and-apply run or its committed cursor");
    }
}
