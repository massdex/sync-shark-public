namespace Massdex.SyncShark.V2;

/// <summary>
/// Owner-requested core v2 scan change: the local disk walk and the remote (Dropbox) listing use different
/// resources, so both sides are read at once. These checks prove the two reads overlap, and that a side which fails
/// cancels the other and reports its own error rather than the cancellation it caused.
/// </summary>
internal static class ConcurrentScanChecks
{
    private static V2Settings Settings => V2Settings.Default with { ProgressInterval = TimeSpan.Zero, MaxWorkers = 1 };

    // A side that never starts (or is never cancelled) fails the check on this timeout instead of hanging it.
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    public static async Task RunAsync()
    {
        await BothSidesScanConcurrentlyAsync();
        await OneSideFailureCancelsTheOtherAsync();
        await TokenlessCancellationSurvivesAsync();
        Console.WriteLine("v2 concurrent scan checks passed.");
    }

    // Both inventories hand their side's read to a shared barrier, and each blocks the calling thread on it with no
    // await first — exactly like the real local walk (PilotLocalScanner commits only on a completed task). A scan
    // that read the sides one after the other would block here on the first side and never start the second, so the
    // barrier times out; both arriving proves each side runs on its own task and the two reads overlap.
    private static async Task BothSidesScanConcurrentlyAsync()
    {
        var fx = Build();
        fx.A.Seed("a.txt", "1");
        fx.B.Seed("c.txt", "3");
        var barrier = new ScanBarrier(2, GateTimeout);
        fx.Provider.AddInventory(fx.Pair.A, new BarrierInventory(fx.A, barrier))
            .AddInventory(fx.Pair.B, new BarrierInventory(fx.B, barrier));
        var plan = await fx.Run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(barrier.Arrived == 2, "both sides entered their inventory read before either finished");
        Require(plan.Actions.Any(x => x.Kind == ActionKind.CopyNew && x.Target == Side.B && x.Path == "a.txt") &&
            plan.Actions.Any(x => x.Kind == ActionKind.CopyNew && x.Target == Side.A && x.Path == "c.txt"),
            "the overlapped scan still plans both one-sided copies");
        Console.WriteLine("PASS scan reads both sides concurrently");
    }

    // One side fails while the other is still reading. The failure cancels the other side, and the error that started
    // it — not the cancellation — is what the run reports.
    private static async Task OneSideFailureCancelsTheOtherAsync()
    {
        var fx = Build();
        var blocked = new BlockingInventory(fx.A);
        var failing = new FailingInventory(fx.B, new InvalidOperationException("side B read failed"));
        fx.Provider.AddInventory(fx.Pair.A, blocked).AddInventory(fx.Pair.B, failing);
        var plan = await fx.Run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(plan.RunUnavailable == "side B read failed", "the real side error is reported cleanly, never the sibling cancellation");
        await blocked.Entered.WaitAsync(GateTimeout);
        await blocked.Cancelled.WaitAsync(GateTimeout);
        Console.WriteLine("PASS one failing side cancels the other and surfaces its own error");
    }

    // A side may throw a tokenless cancellation of its own (a provider timeout). With the caller passing None that
    // token is None too, so it must not be mistaken for the sibling cancellation the scan raised on the other side.
    private static async Task TokenlessCancellationSurvivesAsync()
    {
        var fx = Build();
        var blocked = new BlockingInventory(fx.A);
        var failing = new FailingInventory(fx.B, new TaskCanceledException("provider timeout"));
        fx.Provider.AddInventory(fx.Pair.A, blocked).AddInventory(fx.Pair.B, failing);
        var plan = await fx.Run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(plan.RunUnavailable == "provider timeout",
            "a tokenless cancellation a side raised survives, not mistaken for the sibling cancellation");
        await blocked.Cancelled.WaitAsync(GateTimeout);
        Console.WriteLine("PASS a tokenless side cancellation survives");
    }

    private static Fixture Build()
    {
        var store = new InMemoryStateStore();
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        var pair = new PairDef("p1", "p1",
            new RootDef("ra", SyncProviders.Local, null, "C:\\a", "id:ra"),
            new RootDef("rb", SyncProviders.Dropbox, "acc", "/b", "id:rb"),
            [], [], true);
        store.SavePair(pair);
        var provider = new FakeEndpointProvider().Add(pair.A, a).Add(pair.B, b);
        var run = new SyncRun(store, new Executor(store, Settings, ExecutorChecks.Admission()), provider, Settings,
            TimeProvider.System);
        return new Fixture(pair, store, a, b, provider, run);
    }

    private sealed record Fixture(PairDef Pair, InMemoryStateStore Store, FakeEndpoint A, FakeEndpoint B,
        FakeEndpointProvider Provider, SyncRun Run);

    private static async Task<TException> ExpectAsync<TException>(Func<Task> operation) where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException error)
        {
            return error;
        }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}

/// <summary>Lets N scans rendezvous: an arrival blocks until all have arrived, or the timeout fails the check.</summary>
internal sealed class ScanBarrier(int expected, TimeSpan timeout)
{
    private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public int Arrived => Volatile.Read(ref _arrived);

    /// <summary>
    /// Blocks the calling thread until every side has arrived, without yielding first. This mirrors the real local
    /// walk, which enumerates synchronously, so a scan that runs a side on the caller's thread (instead of its own
    /// task) holds the whole scan here and times out.
    /// </summary>
    public void ArriveBlocking(CancellationToken ct)
    {
        if (Interlocked.Increment(ref _arrived) >= expected) _all.TrySetResult();
        if (!_all.Task.Wait(timeout, ct)) throw new TimeoutException("The other scan side never started.");
    }
}

/// <summary>A side's inventory that meets the other side at a barrier before its read proceeds.</summary>
internal sealed class BarrierInventory(FakeEndpoint inner, ScanBarrier barrier) : IInventorySource
{
    private int _gated;

    // The first read waits at the barrier synchronously (Gate blocks), so this side only overlaps the other when the
    // scan runs it on its own task.
    public Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct)
    {
        Gate(ct);
        return inner.FullAsync(gen, include, p, ct);
    }

    public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct)
    {
        Gate(ct);
        return inner.ChangesSinceAsync(cursor, gen, include, ct);
    }

    public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        inner.HashAsync(paths, ct);

    public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
        CancellationToken ct) => inner.DeletionTimesAsync(paths, ct);

    // Only the side's first read waits at the barrier; a later read (there is none in the check) proceeds.
    private void Gate(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _gated, 1) == 0) barrier.ArriveBlocking(ct);
    }
}

/// <summary>A side whose inventory read holds until its scan is cancelled, recording the cancellation.</summary>
internal sealed class BlockingInventory(FakeEndpoint inner) : IInventorySource
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _block = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once this side's read has begun.</summary>
    public Task Entered => _entered.Task;

    /// <summary>Completes once this side's read is cancelled, so the check can prove the other side stopped it.</summary>
    public Task Cancelled => _cancelled.Task;

    public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct)
    {
        _entered.TrySetResult();
        try
        {
            await _block.Task.WaitAsync(ct);
        }
        finally
        {
            _cancelled.TrySetResult();
        }
        throw new InvalidOperationException("The block is only ever released by cancellation.");
    }

    public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) =>
        throw new InvalidOperationException("This check reads in full.");

    public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        inner.HashAsync(paths, ct);

    public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
        CancellationToken ct) => inner.DeletionTimesAsync(paths, ct);
}

/// <summary>A side whose inventory read fails at once, so the other side of the scan must be cancelled.</summary>
internal sealed class FailingInventory(FakeEndpoint inner, Exception failure) : IInventorySource
{
    public Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct) =>
        Task.FromException<Snapshot>(failure);

    public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) =>
        Task.FromException<Delta>(failure);

    public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        inner.HashAsync(paths, ct);

    public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
        CancellationToken ct) => inner.DeletionTimesAsync(paths, ct);
}
