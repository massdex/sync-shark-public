using System.ComponentModel;
using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks for DownloadSpace (`--engine-checks local`), written by DownloadSpace's writer. Every case
/// exercises the one accounting rule of this owner, so they stay in one file rather than split by job.</summary>
public static class DownloadSpaceChecks
{
    // Test barrier only: a held admission is always released by the same case.
    private static readonly TimeSpan Barrier = TimeSpan.FromSeconds(5);

    private const string Item = "Parent/Item.txt";

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory(nameof(DownloadSpaceChecks)).FullName;
        try
        {
            await CheckArithmeticAsync(root);
            await CheckVolumePoolsAsync(root);
            await CheckObservationsAsync(root);
            await CheckConcurrencyAsync(root);
            await CheckShrinkAndRecoveryAsync(root);
            await CheckCancellationAsync(root);
            await CheckRestartAsync(root);
            await CheckWorkCountsAsync(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckArithmeticAsync(string root)
    {
        var parent = ParentOf(root, Item);
        var volumes = NewVolumes(parent, 1, 100);
        var space = new DownloadSpace(volumes.Serial, volumes.Available);

        var whole = await space.ReserveAsync(root, Item, 100, CancellationToken.None);
        await whole.DisposeAsync();
        RequireFailure(await RefusalAsync(space, root, Item, 101), Item, TextKey.FailLocalNoSpace);

        // S = F - R admits, one byte over refuses, and a zero grant keeps the same lifecycle without a stage hook.
        var held = await space.ReserveAsync(root, Item, 60, CancellationToken.None);
        var equal = await space.ReserveAsync(root, Item, 40, CancellationToken.None);
        await equal.DisposeAsync();
        await equal.DisposeAsync();
        RequireFailure(await RefusalAsync(space, root, Item, 41), Item, TextKey.FailLocalNoSpace);
        var zero = await space.ReserveAsync(root, Item, 0, CancellationToken.None);
        await zero.DisposeAsync();
        await held.DisposeAsync();

        // A negative declaration is rejected before any native observation.
        var observed = volumes.Serials;
        RequireFailure(await RefusalAsync(space, root, Item, -1), Item, TextKey.FailDownloadSizeInvalid);
        Check.Require(volumes.Serials == observed, "A negative declaration reads no volume serial.");

        // long.MaxValue: equality, one byte over and an aggregate sum that would overflow if added naively.
        var maxParent = ParentOf(root, "Max/Item.txt");
        var maxVolumes = NewVolumes(maxParent, 2, long.MaxValue);
        var maxSpace = new DownloadSpace(maxVolumes.Serial, maxVolumes.Available);
        var max = await maxSpace.ReserveAsync(root, "Max/Item.txt", long.MaxValue, CancellationToken.None);
        RequireFailure(await RefusalAsync(maxSpace, root, "Max/Item.txt", 1), "Max/Item.txt",
            TextKey.FailLocalNoSpace);
        await max.DisposeAsync();

        var half = long.MaxValue / 2;
        var first = await maxSpace.ReserveAsync(root, "Max/Item.txt", half + 1, CancellationToken.None);
        RequireFailure(await RefusalAsync(maxSpace, root, "Max/Item.txt", half + 1), "Max/Item.txt",
            TextKey.FailLocalNoSpace);
        var second = await maxSpace.ReserveAsync(root, "Max/Item.txt", half, CancellationToken.None);
        RequireFailure(await RefusalAsync(maxSpace, root, "Max/Item.txt", 1), "Max/Item.txt",
            TextKey.FailLocalNoSpace);
        await second.DisposeAsync();
        await first.DisposeAsync();
        var again = await maxSpace.ReserveAsync(root, "Max/Item.txt", long.MaxValue, CancellationToken.None);
        await again.DisposeAsync();

        maxVolumes.Values[maxParent] = (2, long.MaxValue - 1);
        RequireFailure(await RefusalAsync(maxSpace, root, "Max/Item.txt", long.MaxValue), "Max/Item.txt",
            TextKey.FailLocalNoSpace);
    }

    private static async Task CheckVolumePoolsAsync(string root)
    {
        var zeroA = ParentOf(root, "ZeroA/Item.txt");
        var zeroB = ParentOf(root, "ZeroB/Item.txt");
        var other = ParentOf(root, "Other/Item.txt");
        var volumes = new Volumes();
        volumes.Values[zeroA] = (0, 100);
        volumes.Values[zeroB] = (0, 100);
        volumes.Values[other] = (17, 100);
        var space = new DownloadSpace(volumes.Serial, volumes.Available);

        // Serial zero is a valid grouping key, and distinct paths reporting it share one aggregate.
        var first = await space.ReserveAsync(root, "ZeroA/Item.txt", 60, CancellationToken.None);
        var rest = await space.ReserveAsync(root, "ZeroB/Item.txt", 40, CancellationToken.None);
        RequireFailure(await RefusalAsync(space, root, "ZeroB/Item.txt", 41), "ZeroB/Item.txt",
            TextKey.FailLocalNoSpace);

        // A different serial is a separate pool: it admits while the first pool holds a payload.
        var independent = await space.ReserveAsync(root, "Other/Item.txt", 100, CancellationToken.None);
        await independent.DisposeAsync();

        await rest.DisposeAsync();
        await first.DisposeAsync();
    }

    private static async Task CheckObservationsAsync(string root)
    {
        var parent = ParentOf(root, "Observe/Item.txt");
        var cases = new (bool AtSerial, Func<Exception> Error, TextKey Cause, bool KeepsInner)[]
        {
            (true, () => new Win32Exception(2), TextKey.ParentMissing, false),
            (true, () => new Win32Exception(3), TextKey.ParentMissing, false),
            (true, () => new FileNotFoundException(), TextKey.ParentMissing, false),
            (true, () => new DirectoryNotFoundException(), TextKey.ParentMissing, false),
            (true, () => new Win32Exception(5), TextKey.FailLocalSpaceUnknown, true),
            (true, () => new UnauthorizedAccessException(), TextKey.FailLocalSpaceUnknown, true),
            (true, () => new IOException(), TextKey.FailLocalSpaceUnknown, true),
            (true, () => new OverflowException(), TextKey.FailLocalSpaceUnknown, true),
            (false, () => new Win32Exception(2), TextKey.ParentMissing, false),
            (false, () => new Win32Exception(3), TextKey.ParentMissing, false),
            (false, () => new FileNotFoundException(), TextKey.ParentMissing, false),
            (false, () => new DirectoryNotFoundException(), TextKey.ParentMissing, false),
            (false, () => new Win32Exception(5), TextKey.FailLocalSpaceUnknown, true),
            (false, () => new UnauthorizedAccessException(), TextKey.FailLocalSpaceUnknown, true),
            (false, () => new IOException(), TextKey.FailLocalSpaceUnknown, true),
            (false, () => new OverflowException(), TextKey.FailLocalSpaceUnknown, true)
        };
        foreach (var item in cases)
        {
            var volumes = new Volumes();
            volumes.Values[parent] = (3, 100);
            var injected = item.Error();
            if (item.AtSerial) volumes.SerialErrors[parent] = injected;
            else volumes.AvailableErrors[parent] = injected;
            var space = new DownloadSpace(volumes.Serial, volumes.Available);

            var failure = await RefusalAsync(space, root, "Observe/Item.txt", 100);
            RequireFailure(failure, "Observe/Item.txt", item.Cause, item.KeepsInner ? injected : null);
            Check.Require(volumes.Serials == 1 && volumes.Availables == (item.AtSerial ? 0 : 1),
                item.AtSerial
                    ? "A serial failure observes no capacity sample."
                    : "An available-space failure still observed the serial once.");

            // No reservation is registered and the gate stays usable.
            volumes.SerialErrors.Remove(parent);
            volumes.AvailableErrors.Remove(parent);
            var usable = await space.ReserveAsync(root, "Observe/Item.txt", 100, CancellationToken.None);
            RequireFailure(await RefusalAsync(space, root, "Observe/Item.txt", 1), "Observe/Item.txt",
                TextKey.FailLocalNoSpace);
            await usable.DisposeAsync();
        }

        // An unreadable capacity (negative) is unknown space, not no space.
        var negative = new Volumes();
        negative.Values[parent] = (3, -1);
        var unknown = new DownloadSpace(negative.Serial, negative.Available);
        RequireFailure(await RefusalAsync(unknown, root, "Observe/Item.txt", 100), "Observe/Item.txt",
            TextKey.FailLocalSpaceUnknown);
    }

    private static async Task CheckConcurrencyAsync(string root)
    {
        var parent = ParentOf(root, "Hold/Item.txt");
        var independentParent = ParentOf(root, "Independent/Item.txt");
        var volumes = new Volumes();
        volumes.Values[parent] = (7, 100);
        volumes.Values[independentParent] = (8, 100);
        var space = new DownloadSpace(volumes.Serial, volumes.Available);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        volumes.Entered[parent] = entered;
        volumes.Held[parent] = release.Task;
        var first = Task.Run(() => space.ReserveAsync(root, "Hold/Item.txt", 60, CancellationToken.None).AsTask());
        Check.Require(entered.Task.Wait(Barrier), "The first admission reaches its capacity observation.");
        var second = Task.Run(() => space.ReserveAsync(root, "Hold/Item.txt", 60, CancellationToken.None).AsTask());
        Check.Require(!second.IsCompleted, "A second admission waits for the held accounting gate.");

        var independent = space.ReserveAsync(root, "Independent/Item.txt", 100, CancellationToken.None);
        Check.Require(independent.IsCompletedSuccessfully,
            "A different serial's pool admits independently of the held gate.");
        var independentLease = await independent;
        await independentLease.DisposeAsync();

        release.TrySetResult();
        volumes.Held.Remove(parent);
        volumes.Entered.Remove(parent);
        var granted = await first;
        ItemFailedException? refused = null;
        try { await second; }
        catch (ItemFailedException error) { refused = error; }
        RequireFailure(refused, "Hold/Item.txt", TextKey.FailLocalNoSpace);
        await granted.DisposeAsync();

        // Retirement waits for a held admission's accounting, and overlapping duplicate disposal subtracts once.
        var retireParent = ParentOf(root, "Retire/Item.txt");
        var retireVolumes = new Volumes();
        retireVolumes.Values[retireParent] = (9, 100);
        var retireSpace = new DownloadSpace(retireVolumes.Serial, retireVolumes.Available);
        var heldLease = await retireSpace.ReserveAsync(root, "Retire/Item.txt", 40, CancellationToken.None);
        var retireEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retireRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        retireVolumes.Entered[retireParent] = retireEntered;
        retireVolumes.Held[retireParent] = retireRelease.Task;
        var admission = Task.Run(() =>
            retireSpace.ReserveAsync(root, "Retire/Item.txt", 60, CancellationToken.None).AsTask());
        Check.Require(retireEntered.Task.Wait(Barrier), "The held admission reaches its capacity observation.");
        var retirement = heldLease.DisposeAsync();
        var duplicate = heldLease.DisposeAsync();
        Check.Require(!retirement.IsCompleted && !duplicate.IsCompleted,
            "Retirement waits for the held admission's accounting.");
        retireRelease.TrySetResult();
        retireVolumes.Held.Remove(retireParent);
        retireVolumes.Entered.Remove(retireParent);
        var admitted = await admission;
        await retirement;
        await duplicate;
        await admitted.DisposeAsync();

        // If the duplicate disposal subtracted, the aggregate would be negative and admit more than F.
        retireVolumes.Values[retireParent] = (9, 60);
        RequireFailure(await RefusalAsync(retireSpace, root, "Retire/Item.txt", 100), "Retire/Item.txt",
            TextKey.FailLocalNoSpace);
        var exact = await retireSpace.ReserveAsync(root, "Retire/Item.txt", 60, CancellationToken.None);
        await exact.DisposeAsync();
    }

    private static async Task CheckShrinkAndRecoveryAsync(string root)
    {
        var parent = ParentOf(root, "Shrink/Item.txt");
        var volumes = NewVolumes(parent, 11, 100);
        var space = new DownloadSpace(volumes.Serial, volumes.Available);

        // The open-stage checkpoint still contributes the declared bytes, not the prefix written so far.
        var declared = await space.ReserveAsync(root, "Shrink/Item.txt", 60, CancellationToken.None);
        volumes.Values[parent] = (11, 60);
        RequireFailure(await RefusalAsync(space, root, "Shrink/Item.txt", 40), "Shrink/Item.txt",
            TextKey.FailLocalNoSpace);
        volumes.Values[parent] = (11, 100);
        var fits = await space.ReserveAsync(root, "Shrink/Item.txt", 40, CancellationToken.None);
        RequireFailure(await RefusalAsync(space, root, "Shrink/Item.txt", 41), "Shrink/Item.txt",
            TextKey.FailLocalNoSpace);
        await fits.DisposeAsync();

        // Shrinking below the aggregate does not revoke admitted work, but new positive and zero candidates refuse.
        volumes.Values[parent] = (11, 40);
        RequireFailure(await RefusalAsync(space, root, "Shrink/Item.txt", 0), "Shrink/Item.txt",
            TextKey.FailLocalNoSpace);
        RequireFailure(await RefusalAsync(space, root, "Shrink/Item.txt", 1), "Shrink/Item.txt",
            TextKey.FailLocalNoSpace);

        // A raised fresh observation admits again in the same instance.
        volumes.Values[parent] = (11, 100);
        var raised = await space.ReserveAsync(root, "Shrink/Item.txt", 40, CancellationToken.None);
        await raised.DisposeAsync();
        await declared.DisposeAsync();

        // After the declared lease retires, the next admission uses the lower figure and never the written prefix.
        volumes.Values[parent] = (11, 40);
        var postStage = await space.ReserveAsync(root, "Shrink/Item.txt", 40, CancellationToken.None);
        RequireFailure(await RefusalAsync(space, root, "Shrink/Item.txt", 41), "Shrink/Item.txt",
            TextKey.FailLocalNoSpace);
        await postStage.DisposeAsync();

        // A failed cleanup retires only its own reservation; the fresh figure stays 60.
        volumes.Values[parent] = (11, 60);
        var cleanup = await space.ReserveAsync(root, "Shrink/Item.txt", 60, CancellationToken.None);
        await cleanup.DisposeAsync();
        var afterFailure = await space.ReserveAsync(root, "Shrink/Item.txt", 60, CancellationToken.None);
        RequireFailure(await RefusalAsync(space, root, "Shrink/Item.txt", 61), "Shrink/Item.txt",
            TextKey.FailLocalNoSpace);
        await afterFailure.DisposeAsync();

        // One retirement never frees another grant.
        volumes.Values[parent] = (11, 100);
        var first = await space.ReserveAsync(root, "Shrink/Item.txt", 60, CancellationToken.None);
        var second = await space.ReserveAsync(root, "Shrink/Item.txt", 40, CancellationToken.None);
        await first.DisposeAsync();
        var third = await space.ReserveAsync(root, "Shrink/Item.txt", 60, CancellationToken.None);
        RequireFailure(await RefusalAsync(space, root, "Shrink/Item.txt", 61), "Shrink/Item.txt",
            TextKey.FailLocalNoSpace);
        await second.DisposeAsync();
        await third.DisposeAsync();
        var full = await space.ReserveAsync(root, "Shrink/Item.txt", 100, CancellationToken.None);
        await full.DisposeAsync();

        // Repeated grant/refuse/zero/retirement cycles leave one reusable pool with a zero aggregate.
        for (var cycle = 0; cycle < 2; cycle++)
        {
            var lease = await space.ReserveAsync(root, "Shrink/Item.txt", 60, CancellationToken.None);
            RequireFailure(await RefusalAsync(space, root, "Shrink/Item.txt", 61), "Shrink/Item.txt",
                TextKey.FailLocalNoSpace);
            await lease.DisposeAsync();
            var zero = await space.ReserveAsync(root, "Shrink/Item.txt", 0, CancellationToken.None);
            await zero.DisposeAsync();
            await zero.DisposeAsync();
        }
        var reused = await space.ReserveAsync(root, "Shrink/Item.txt", 100, CancellationToken.None);
        await reused.DisposeAsync();
    }

    private static async Task CheckCancellationAsync(string root)
    {
        var parent = ParentOf(root, "Cancel/Item.txt");
        var volumes = NewVolumes(parent, 12, 100);
        var space = new DownloadSpace(volumes.Serial, volumes.Available);

        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            Check.Require(await CancelledAsync(() =>
                    space.ReserveAsync(root, "Cancel/Item.txt", 100, cts.Token)),
                "A pre-cancelled admission is cancelled at the accounting gate.");
        }
        Check.Require(volumes.Serials == 1 && volumes.Availables == 0,
            "A pre-cancelled admission makes no capacity observation.");
        var afterPreCancel = await space.ReserveAsync(root, "Cancel/Item.txt", 100, CancellationToken.None);
        await afterPreCancel.DisposeAsync();

        // A caller waiting at the accounting gate joins cancellation without registering a grant.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        volumes.Entered[parent] = entered;
        volumes.Held[parent] = release.Task;
        var held = Task.Run(() => space.ReserveAsync(root, "Cancel/Item.txt", 100, CancellationToken.None).AsTask());
        Check.Require(entered.Task.Wait(Barrier), "The held admission reaches its capacity observation.");
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            Check.Require(await CancelledAsync(() => space.ReserveAsync(root, "Cancel/Item.txt", 100, cts.Token)),
                "A caller waiting at the accounting gate joins cancellation.");
        }
        release.TrySetResult();
        volumes.Held.Remove(parent);
        volumes.Entered.Remove(parent);
        var granted = await held;
        await granted.DisposeAsync();

        // The second cancellation check runs after the capacity sample and before a grant is registered.
        var lateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        volumes.Entered[parent] = lateEntered;
        volumes.Held[parent] = lateRelease.Task;
        using (var cts = new CancellationTokenSource())
        {
            var late = Task.Run(() => space.ReserveAsync(root, "Cancel/Item.txt", 100, cts.Token).AsTask());
            Check.Require(lateEntered.Task.Wait(Barrier), "The late-cancelled admission reached its capacity sample.");
            cts.Cancel();
            lateRelease.TrySetResult();
            var cancelled = false;
            try { await late; }
            catch (OperationCanceledException) { cancelled = true; }
            Check.Require(cancelled, "Cancellation after the capacity sample registers no grant.");
        }
        volumes.Held.Remove(parent);
        volumes.Entered.Remove(parent);
        var usable = await space.ReserveAsync(root, "Cancel/Item.txt", 100, CancellationToken.None);
        await usable.DisposeAsync();
    }

    private static async Task CheckRestartAsync(string root)
    {
        var parent = ParentOf(root, "Restart/Item.txt");
        var volumes = NewVolumes(parent, 13, 100);
        var previous = new DownloadSpace(volumes.Serial, volumes.Available);
        var abandoned = await previous.ReserveAsync(root, "Restart/Item.txt", 60, CancellationToken.None);

        // CV-CRASH: terminating the test process for crash evidence needs its own authority; a fresh owner
        // carrying no reservation is what a restart must observe.
        var restarted = new DownloadSpace(volumes.Serial, volumes.Available);
        var fresh = await restarted.ReserveAsync(root, "Restart/Item.txt", 100, CancellationToken.None);
        await fresh.DisposeAsync();

        // Leftover stage usage is ordinary volume usage, so the fresh observation already excludes it.
        volumes.Values[parent] = (13, 60);
        var leftover = await restarted.ReserveAsync(root, "Restart/Item.txt", 60, CancellationToken.None);
        RequireFailure(await RefusalAsync(restarted, root, "Restart/Item.txt", 61), "Restart/Item.txt",
            TextKey.FailLocalNoSpace);

        // Repeated failure does not latch; a raised observation recovers in the same instance.
        volumes.Values[parent] = (13, 160);
        var recovered = await restarted.ReserveAsync(root, "Restart/Item.txt", 100, CancellationToken.None);
        await recovered.DisposeAsync();
        await leftover.DisposeAsync();
        await abandoned.DisposeAsync();
    }

    private static async Task CheckWorkCountsAsync(string root)
    {
        var parent = ParentOf(root, "Counts/Item.txt");
        var volumes = NewVolumes(parent, 14, 100);
        var space = new DownloadSpace(volumes.Serial, volumes.Available);

        var one = await space.ReserveAsync(root, "Counts/Item.txt", 100, CancellationToken.None);
        RequireFailure(await RefusalAsync(space, root, "Counts/Item.txt", 101), "Counts/Item.txt",
            TextKey.FailLocalNoSpace);
        var zero = await space.ReserveAsync(root, "Counts/Item.txt", 0, CancellationToken.None);
        Check.Require(volumes.Serials == 3 && volumes.Availables == 3,
            "Each admission observes its parent serial and fresh capacity once.");
        await one.DisposeAsync();
        Check.Require(volumes.Serials == 3 && volumes.Availables == 3,
            "Retirement makes no native observation.");

        RequireFailure(await RefusalAsync(space, root, "Counts/Item.txt", -1), "Counts/Item.txt",
            TextKey.FailDownloadSizeInvalid);
        Check.Require(volumes.Serials == 3 && volumes.Availables == 3,
            "A rejected declaration observes nothing.");

        var serialError = new Win32Exception(5);
        volumes.SerialErrors[parent] = serialError;
        RequireFailure(await RefusalAsync(space, root, "Counts/Item.txt", 1), "Counts/Item.txt",
            TextKey.FailLocalSpaceUnknown, serialError);
        Check.Require(volumes.Serials == 4 && volumes.Availables == 3,
            "A serial failure never reaches the capacity observation.");
        volumes.SerialErrors.Remove(parent);

        var availableError = new IOException();
        volumes.AvailableErrors[parent] = availableError;
        RequireFailure(await RefusalAsync(space, root, "Counts/Item.txt", 1), "Counts/Item.txt",
            TextKey.FailLocalSpaceUnknown, availableError);
        Check.Require(volumes.Serials == 5 && volumes.Availables == 4,
            "A capacity failure observed the parent serial exactly once.");
        volumes.AvailableErrors.Remove(parent);
        await zero.DisposeAsync();
    }

    private static string ParentOf(string root, string target) =>
        Path.GetDirectoryName(LocalPaths.Full(root, target))!;

    private static Volumes NewVolumes(string parent, uint serial, long available)
    {
        var volumes = new Volumes();
        volumes.Values[parent] = (serial, available);
        return volumes;
    }

    private static async Task<ItemFailedException?> RefusalAsync(DownloadSpace space, string root, string target,
        long bytes)
    {
        try { await (await space.ReserveAsync(root, target, bytes, CancellationToken.None)).DisposeAsync(); }
        catch (ItemFailedException error) { return error; }
        return null;
    }

    private static async Task<bool> CancelledAsync(Func<ValueTask<IAsyncDisposable>> reserve)
    {
        try { await (await reserve()).DisposeAsync(); }
        catch (OperationCanceledException) { return true; }
        return false;
    }

    private static void RequireFailure(ItemFailedException? failure, string target, TextKey cause,
        Exception? inner = null)
    {
        Check.Require(failure is not null, $"{cause} is reported for {target}.");
        var reported = failure!;
        Check.Require(reported.Path == target && reported.Cause == cause, $"{cause} is reported for {target}.");
        Check.Require(!reported.Message.Contains('{'), "A failure message never exposes a template placeholder.");
        if (inner is null) Check.Require(reported.InnerException is null, $"{cause} keeps no inner exception.");
        else Check.Require(ReferenceEquals(reported.InnerException, inner),
            $"{cause} keeps the original observation failure.");
    }

    // Synthetic parent observations: one entry per parent directory, an optional injected native failure and
    // an optional hold that keeps the admission lock while a case controls the overlap.
    private sealed class Volumes
    {
        private int _serials;
        private int _availables;

        public readonly Dictionary<string, (uint Serial, long Available)> Values = new();
        public readonly Dictionary<string, Exception> SerialErrors = new();
        public readonly Dictionary<string, Exception> AvailableErrors = new();
        public readonly Dictionary<string, TaskCompletionSource> Entered = new();
        public readonly Dictionary<string, Task> Held = new();

        public int Serials => Volatile.Read(ref _serials);
        public int Availables => Volatile.Read(ref _availables);

        public uint Serial(string parent)
        {
            Interlocked.Increment(ref _serials);
            if (SerialErrors.TryGetValue(parent, out var error)) throw error;
            return Values.TryGetValue(parent, out var value) ? value.Serial : throw new Win32Exception(2);
        }

        public long Available(string parent)
        {
            Interlocked.Increment(ref _availables);
            if (Entered.TryGetValue(parent, out var entered)) entered.TrySetResult();
            if (Held.TryGetValue(parent, out var held))
                Check.Require(held.Wait(Barrier), "A held capacity observation is released by its case.");
            if (AvailableErrors.TryGetValue(parent, out var error)) throw error;
            return Values.TryGetValue(parent, out var value) ? value.Available : throw new Win32Exception(2);
        }
    }
}
