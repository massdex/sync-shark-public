using System.Diagnostics;

namespace Massdex.SyncShark.V2;

/// <summary>
/// Offline checks for the free-space admission guard: the 512 MiB floor, the state database and WAL raising it on
/// the data volume, unknown readings leaving items pending, shared reservations, their release, the one-copy
/// download demand on the target volume and the bounded wait for other writers' reservations. Free space and
/// database sizes come from fakes, so no disk space is consumed.
/// </summary>
internal static class FreeSpaceAdmissionChecks
{
    private const long MiB = 1L << 20;
    private const long GiB = 1L << 30;
    private const string RootPath = "C:\\work\\root";
    private const string RecoveryPath = "D:\\work\\recovery";
    private const string ConcurrentRoot = "C:\\work\\concurrent";
    private const string ReleaseRoot = "C:\\work\\release";
    private const string PeakRoot = "C:\\work\\peak";
    private const string PeakRecovery = "C:\\work\\peak-recovery";
    private const string OtherRoot = "D:\\work\\other";
    private const string WaitRoot = "C:\\work\\wait";
    private const string CancelRoot = "C:\\work\\cancel";
    private const string TimeoutRoot = "C:\\work\\timeout";

    public static async Task RunAsync()
    {
        FloorIsFiveHundredTwelveMiB();
        await ShortageIsRefusedWithTheStoredTextAsync().ConfigureAwait(false);
        await ConcurrentWritesSeeEachOthersReservationsAsync().ConfigureAwait(false);
        await ReservationIsReleasedWhenDisposedAsync().ConfigureAwait(false);
        await SameVolumeDownloadAndRecoverySumAsync().ConfigureAwait(false);
        await DifferentVolumesAreCheckedSeparatelyAsync().ConfigureAwait(false);
        await ZeroByteCopyStillMustLeaveTheReserveAsync().ConfigureAwait(false);
        await RecoveryCopyIsCheckedOnTheRecoveryVolumeAsync().ConfigureAwait(false);
        await UnknownFreeSpaceLeavesTheItemPendingAsync().ConfigureAwait(false);
        await DataVolumeSizeRaisesTheReserveAboveTheFloorAsync().ConfigureAwait(false);
        await UnknownDatabaseSizeLeavesTheItemPendingAsync().ConfigureAwait(false);
        await UnavailableDataPathLeavesTheItemPendingAsync().ConfigureAwait(false);
        await OneDownloadNeedsOnlyTheTargetCopyAsync().ConfigureAwait(false);
        await AnotherVolumeIsNotDemandedForADownloadAsync().ConfigureAwait(false);
        await ADemandBlockedOnlyByReservationsWaitsForReleaseAsync().ConfigureAwait(false);
        await TheBoundedWaitIsCancellableAsync().ConfigureAwait(false);
        await TheBoundedWaitEndsInARefusalAsync().ConfigureAwait(false);
        await LocalWriterAdmitsThroughItsProbeAsync().ConfigureAwait(false);
        await LocalWriterAdmitsADownloadAgainstTheTargetVolumeOnlyAsync().ConfigureAwait(false);
        Console.WriteLine("v2 free-space admission checks passed.");
    }

    private static void FloorIsFiveHundredTwelveMiB()
    {
        LocalChecks.Require(V2Settings.Default.FreeSpaceFloorBytes == 512 * MiB,
            "the general free-space floor is 512 MiB");
    }

    private static async Task ShortageIsRefusedWithTheStoredTextAsync()
    {
        var admission = await Guard(600 * MiB).AdmitAsync(Copy(100 * MiB), RootPath, RecoveryPath,
            CancellationToken.None).ConfigureAwait(false);
        LocalChecks.Require(admission.Refusal is not null && admission.Reservation is null,
            "a write that would leave the volume below its reserve is refused");
        var stored = admission.Refusal!;
        var text = stored.Detail;
        LocalChecks.Require(text ==
            "Not enough free space on C:: needs 100.0 MiB, free 600.0 MiB, reserve 512.0 MiB. " +
            "Options: free space and retry; exclude this folder",
            "the refusal carries the stored notice text and both options: " + text);
        LocalChecks.Require(stored.VolumeRoot == "C:\\" && stored.RequiredBytes == 100 * MiB &&
            stored.FreeBytes == 600 * MiB && stored.ReserveBytes == 512 * MiB,
            "the refusal carries the volume root, required bytes, free and reserve for notice aggregation");
        var second = await Guard(600 * MiB).AdmitAsync(Copy(100 * MiB), RootPath, RecoveryPath,
            CancellationToken.None).ConfigureAwait(false);
        LocalChecks.Require(second.Reservation is null, "a refused write reserves nothing");
    }

    private static async Task ConcurrentWritesSeeEachOthersReservationsAsync()
    {
        var probe = new FakeProbe();
        probe.Free[ConcurrentRoot] = 2 * GiB;
        var guard = new LocalFreeSpaceGuard(probe, V2Settings.Default);
        var first = await guard.AdmitAsync(Copy(1 * GiB), ConcurrentRoot, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(first.Reservation is not null, "the first write fits the observed free space");
        // The demand fits the observed free space alone but not while the first write's bytes are reserved, so a
        // bounded wait expires into a refusal here; the release/wake path has its own check below.
        var otherWriter = new LocalFreeSpaceGuard(probe, V2Settings.Default,
            admissionWait: TimeSpan.FromMilliseconds(100));
        var blocked = await otherWriter.AdmitAsync(Copy(1 * GiB), ConcurrentRoot, RecoveryPath,
            CancellationToken.None).ConfigureAwait(false);
        LocalChecks.Require(blocked.Refusal is not null,
            "another writer on the same volume sees the first writer's reservation");
        first.Reservation!.Dispose();
        var third = await guard.AdmitAsync(Copy(1 * GiB), ConcurrentRoot, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(third.Reservation is not null,
            "after the first reservation is released the next write fits again");
        third.Reservation!.Dispose();
    }

    private static async Task ReservationIsReleasedWhenDisposedAsync()
    {
        var guard = Guard(2 * GiB, ReleaseRoot);
        var first = await guard.AdmitAsync(Copy(1 * GiB), ReleaseRoot, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(first.Reservation is not null, "a write fitting the reserve is admitted");
        first.Reservation!.Dispose();
        first.Reservation!.Dispose();
        var second = await guard.AdmitAsync(Copy(1536 * MiB), ReleaseRoot, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(second.Reservation is not null,
            "a double dispose releases the reservation once and the full capacity is available again");
        second.Reservation!.Dispose();
    }

    private static async Task SameVolumeDownloadAndRecoverySumAsync()
    {
        var probe = new FakeProbe();
        probe.Free[PeakRoot] = 4 * GiB + 600 * MiB;   // 3 GiB partial + 1 GiB archive + reserve.
        probe.Free[PeakRecovery] = 4 * GiB + 600 * MiB;
        var admission = await new LocalFreeSpaceGuard(probe, V2Settings.Default)
            .AdmitAsync(Update(3 * GiB, 1 * GiB), PeakRoot, PeakRecovery, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(admission.Reservation is not null,
            "the verified partial and the Recovery archive are summed on one shared volume: " +
            admission.Refusal?.Detail);
        admission.Reservation!.Dispose();

        probe.Free[PeakRoot] = 4 * GiB;                 // Sum plus the reserve no longer fits.
        probe.Free[PeakRecovery] = 4 * GiB;
        var refused = await new LocalFreeSpaceGuard(probe, V2Settings.Default)
            .AdmitAsync(Update(3 * GiB, 1 * GiB), PeakRoot, PeakRecovery, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(refused.Refusal is { } shortage &&
            shortage.Detail.Contains("needs 4.0 GiB", StringComparison.Ordinal),
            "a shared volume that can hold the peak but not the sum is refused: " + refused.Refusal?.Detail);
    }

    private static async Task DifferentVolumesAreCheckedSeparatelyAsync()
    {
        var probe = new FakeProbe();
        probe.Free[RootPath] = 1 * GiB;
        probe.Free[RecoveryPath] = 50 * GiB;
        var refused = await new LocalFreeSpaceGuard(probe, V2Settings.Default)
            .AdmitAsync(Update(1 * GiB, 4 * GiB), RootPath, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(refused.Refusal is { } shortage &&
            shortage.Detail.Contains("on C:", StringComparison.Ordinal),
            "the write's own volume refuses first and the notice names it");

        var reversedProbe = new FakeProbe();
        reversedProbe.Free[RootPath] = 50 * GiB;
        reversedProbe.Free[RecoveryPath] = 1 * GiB;
        var reversed = await new LocalFreeSpaceGuard(reversedProbe, V2Settings.Default)
            .AdmitAsync(Update(1 * GiB, 4 * GiB), RootPath, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(reversed.Refusal is { } recoveryShortage &&
            recoveryShortage.Detail.Contains("on D:", StringComparison.Ordinal),
            "the recovery volume is checked on its own, and a shortage names that volume");
    }

    private static async Task ZeroByteCopyStillMustLeaveTheReserveAsync()
    {
        var admission = await Guard(256 * MiB).AdmitAsync(Copy(0), RootPath, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(admission.Refusal is not null,
            "an empty copy is still refused when the volume has less than the reserve");
    }

    private static async Task RecoveryCopyIsCheckedOnTheRecoveryVolumeAsync()
    {
        var probe = new FakeProbe();
        probe.Free[RootPath] = 50 * GiB;
        probe.Free[RecoveryPath] = 1 * GiB;
        var admission = await new LocalFreeSpaceGuard(probe, V2Settings.Default)
            .AdmitAsync(DeleteOf(1 * GiB), RootPath, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(admission.Refusal is { } shortage &&
            shortage.Detail.Contains("on D:", StringComparison.Ordinal),
            "a Recovery-first delete is admitted against the recovery volume's bytes");
    }

    private static async Task UnknownFreeSpaceLeavesTheItemPendingAsync()
    {
        var admission = await new LocalFreeSpaceGuard(new FakeProbe(), V2Settings.Default)
            .AdmitAsync(Copy(10 * MiB), RootPath, RecoveryPath, CancellationToken.None).ConfigureAwait(false);
        LocalChecks.Require(admission.Refusal is { ReasonKey: FreeSpaceRefusal.UnknownSpaceReasonKey } refusal &&
            refusal.Detail.Contains("Could not read the target volume's free space", StringComparison.Ordinal) &&
            refusal.RequiredBytes == 10 * MiB,
            "unreadable target free space leaves the item pending instead of granting it");
    }

    // The database and WAL sizes raise the reserve only on their own volume, and are cached while fresh. Tiny real
    // files and a byte-scaled floor prove the arithmetic without allocating meaningful disk space.
    private static async Task DataVolumeSizeRaisesTheReserveAboveTheFloorAsync()
    {
        var data = LocalChecks.FreshRoot("free-space-data");
        var database = Path.Combine(data, "v2", "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using (var stream = File.Create(database)) stream.SetLength(100);
        using (var stream = File.Create(database + "-wal")) stream.SetLength(100);
        var settings = V2Settings.Default with { FreeSpaceFloorBytes = 512 };
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var probe = new FakeProbe();
        probe.Free[data] = 700;
        probe.Free[OtherRoot] = 700;
        var guard = new LocalFreeSpaceGuard(probe, settings, database, clock);

        var first = await guard.AdmitAsync(Copy(100), data, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(first.Reservation is not null,
            "below the floor the 512-byte reserve admits the write: " + first.Refusal?.Detail);
        first.Reservation!.Dispose();

        // The database grows past the floor; the cached 200-byte total is reused until the window closes.
        using (var stream = new FileStream(database, FileMode.Open, FileAccess.Write)) stream.SetLength(600);
        using (var stream = new FileStream(database + "-wal", FileMode.Open, FileAccess.Write))
            stream.SetLength(200);
        var cached = await guard.AdmitAsync(Copy(100), data, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(cached.Reservation is not null,
            "the database and WAL sizes are reused inside the cache window");
        cached.Reservation!.Dispose();

        var other = await guard.AdmitAsync(Copy(100), OtherRoot, "D:\\work\\other-recovery",
            CancellationToken.None).ConfigureAwait(false);
        LocalChecks.Require(other.Reservation is not null, "another volume keeps only the 512-byte floor");
        other.Reservation!.Dispose();

        clock.Now = clock.Now.Add(settings.FreeSpaceSizeCache + TimeSpan.FromSeconds(1));
        var refused = await guard.AdmitAsync(Copy(100), data, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(refused.Refusal is { } refusal &&
            refusal.Detail.Contains("reserve 800 B", StringComparison.Ordinal),
            "after the cache window the database and WAL become the reserve: " + refused.Refusal?.Detail);
    }

    // An unknown database size is never treated as zero: the item stays pending until the size can be read.
    private static async Task UnknownDatabaseSizeLeavesTheItemPendingAsync()
    {
        var data = LocalChecks.FreshRoot("free-space-missing-db");
        var probe = new FakeProbe();
        probe.Free[data] = 10 * GiB;
        var guard = new LocalFreeSpaceGuard(probe, V2Settings.Default with { FreeSpaceFloorBytes = 512 },
            Path.Combine(data, "v2", "state.db"), new MutableClock(DateTimeOffset.UnixEpoch));
        var admission = await guard.AdmitAsync(Copy(1 * MiB), data, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(admission.Refusal is { ReasonKey: FreeSpaceRefusal.UnknownReserveReasonKey } refusal &&
            refusal.Detail.Contains("Could not read the Sync Shark state database size on " +
                Path.GetPathRoot(data)!.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal) && refusal.RequiredBytes == 1 * MiB,
            "an unreadable database size leaves the item pending: " + admission.Refusal?.Detail);
    }

    // A configured data path whose volume cannot be determined leaves local writes pending (fail safe).
    private static async Task UnavailableDataPathLeavesTheItemPendingAsync()
    {
        var probe = new FakeProbe();
        probe.Free[RootPath] = 10 * GiB;
        var guard = new LocalFreeSpaceGuard(probe, V2Settings.Default, "bad\0path",
            new MutableClock(DateTimeOffset.UnixEpoch));
        var admission = await guard.AdmitAsync(Copy(1 * MiB), RootPath, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(admission.Refusal is { ReasonKey: FreeSpaceRefusal.UnknownReserveReasonKey },
            "an unusable configured data path leaves local writes pending");
    }

    // A download writes its one copy straight into the target directory. It must be admitted when the target
    // volume can hold that copy plus the reserve; before this change the spool copy and the staged target copy
    // were summed, so the same 1 GiB write was refused.
    private static async Task OneDownloadNeedsOnlyTheTargetCopyAsync()
    {
        var probe = new FakeProbe();
        probe.Free[RootPath] = 1_600 * MiB;   // 1 GiB + 512 MiB reserve fits; 2 GiB + reserve does not.
        var admission = await new LocalFreeSpaceGuard(probe, V2Settings.Default)
            .AdmitAsync(Copy(1 * GiB), RootPath, RecoveryPath, CancellationToken.None).ConfigureAwait(false);
        LocalChecks.Require(admission.Reservation is not null,
            "a download is admitted when the target volume holds one copy plus the reserve: " +
            admission.Refusal?.Detail);
        admission.Reservation!.Dispose();

        var tooTight = await new LocalFreeSpaceGuard(probe, V2Settings.Default)
            .AdmitAsync(Copy(1_200 * MiB), RootPath, RecoveryPath, CancellationToken.None).ConfigureAwait(false);
        LocalChecks.Require(tooTight.Refusal is { } shortage &&
            shortage.Detail.Contains("needs 1.2 GiB", StringComparison.Ordinal),
            "the one target copy is still refused when it does not fit beside the reserve: " +
            tooTight.Refusal?.Detail);
    }

    // A download no longer has a spool volume demand: only the target volume is observed and reserved.
    private static async Task AnotherVolumeIsNotDemandedForADownloadAsync()
    {
        var probe = new FakeProbe();
        probe.Free[RootPath] = 1_600 * MiB;
        var admission = await new LocalFreeSpaceGuard(probe, V2Settings.Default)
            .AdmitAsync(Copy(1 * GiB), RootPath, RecoveryPath, CancellationToken.None).ConfigureAwait(false);
        LocalChecks.Require(admission.Reservation is not null,
            "a download demands only the target volume, never a separate spool volume");
        admission.Reservation!.Dispose();
    }

    // N3: a demand that fits observed free alone but not while another writer's reservation is held waits for the
    // release instead of refusing.
    private static async Task ADemandBlockedOnlyByReservationsWaitsForReleaseAsync()
    {
        var probe = new FakeProbe();
        probe.Free[WaitRoot] = 2 * GiB;
        var guard = new LocalFreeSpaceGuard(probe, V2Settings.Default, admissionWait: TimeSpan.FromSeconds(10));
        var first = await guard.AdmitAsync(Copy(1 * GiB), WaitRoot, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        LocalChecks.Require(first.Reservation is not null, "the blocking reservation is admitted");

        var waiting = guard.AdmitAsync(Copy(1 * GiB), WaitRoot, RecoveryPath, CancellationToken.None);
        await Task.Delay(100).ConfigureAwait(false);
        LocalChecks.Require(!waiting.IsCompleted,
            "a demand blocked only by another reservation waits instead of refusing");
        first.Reservation!.Dispose();
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        LocalChecks.Require(second.Reservation is not null,
            "a release wakes the waiting demand and it is admitted");
        second.Reservation!.Dispose();
    }

    private static async Task TheBoundedWaitIsCancellableAsync()
    {
        var probe = new FakeProbe();
        probe.Free[CancelRoot] = 2 * GiB;
        var guard = new LocalFreeSpaceGuard(probe, V2Settings.Default, admissionWait: TimeSpan.FromSeconds(30));
        var first = await guard.AdmitAsync(Copy(1 * GiB), CancelRoot, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        using var cancel = new CancellationTokenSource();
        var waiting = guard.AdmitAsync(Copy(1 * GiB), CancelRoot, RecoveryPath, cancel.Token);
        await Task.Delay(100).ConfigureAwait(false);
        LocalChecks.Require(!waiting.IsCompleted, "the demand is waiting");
        cancel.Cancel();
        var cancelled = false;
        try
        {
            await waiting.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        first.Reservation!.Dispose();
        LocalChecks.Require(cancelled, "cancellation ends the wait with OperationCanceledException");
    }

    private static async Task TheBoundedWaitEndsInARefusalAsync()
    {
        var probe = new FakeProbe();
        probe.Free[TimeoutRoot] = 2 * GiB;
        var guard = new LocalFreeSpaceGuard(probe, V2Settings.Default,
            admissionWait: TimeSpan.FromMilliseconds(150));
        var first = await guard.AdmitAsync(Copy(1 * GiB), TimeoutRoot, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        var elapsed = Stopwatch.StartNew();
        var admission = await guard.AdmitAsync(Copy(1 * GiB), TimeoutRoot, RecoveryPath, CancellationToken.None)
            .ConfigureAwait(false);
        elapsed.Stop();
        first.Reservation!.Dispose();
        LocalChecks.Require(admission.Refusal is { ReasonKey: FreeSpaceRefusal.InsufficientReasonKey },
            "after the bounded wait the still-blocked demand is refused");
        LocalChecks.Require(elapsed.Elapsed < TimeSpan.FromSeconds(5),
            "the wait respects its bound: " + elapsed.Elapsed);
    }

    private static async Task LocalWriterAdmitsThroughItsProbeAsync()
    {
        var root = LocalChecks.FreshRoot("free-space-writer");
        var recovery = Path.Combine(Path.GetDirectoryName(root)!, "recovery");
        var probe = new FakeProbe();
        probe.Free[root] = 256 * MiB;
        probe.Free[recovery] = 256 * MiB;
        var writer = await LocalWriter.OpenAsync(LocalChecks.RootOf(root, "root-free"), Side.A, LocalChecks.PairId,
                LocalChecks.InstallationId, recovery, CancellationToken.None, V2Settings.Default, probe)
            .ConfigureAwait(false);

        var copy = await writer.AdmitFreeSpaceAsync(Copy(100 * MiB), CancellationToken.None).ConfigureAwait(false);
        LocalChecks.Require(copy.Refusal is { } copyRefusal &&
            copyRefusal.Detail.Contains("needs 100.0 MiB", StringComparison.Ordinal),
            "a local copy is admitted against the root volume with the configured reserve");
        var delete = await writer.AdmitFreeSpaceAsync(DeleteOf(1 * GiB), CancellationToken.None).ConfigureAwait(false);
        LocalChecks.Require(delete.Refusal is { } deleteRefusal &&
            deleteRefusal.Detail.Contains("needs 1.0 GiB", StringComparison.Ordinal),
            "a Recovery-first delete is admitted against the recovery volume");
    }

    // A writer whose pair source is Dropbox admits a download against the target volume only, needing one copy
    // plus the reserve instead of a spool copy plus a staging copy.
    private static async Task LocalWriterAdmitsADownloadAgainstTheTargetVolumeOnlyAsync()
    {
        var root = LocalChecks.FreshRoot("free-space-writer-download");
        var recovery = Path.Combine(Path.GetDirectoryName(root)!, "recovery-download");
        var probe = new FakeProbe();
        probe.Free[root] = 1_600 * MiB;
        probe.Free[recovery] = 10 * GiB;
        var writer = await LocalWriter.OpenAsync(LocalChecks.RootOf(root, "root-free-download"), Side.A,
                LocalChecks.PairId, LocalChecks.InstallationId, recovery, CancellationToken.None, V2Settings.Default,
                probe).ConfigureAwait(false);

        var copy = await writer.AdmitFreeSpaceAsync(Copy(1 * GiB), CancellationToken.None).ConfigureAwait(false);
        LocalChecks.Require(copy.Reservation is not null,
            "a Dropbox-sourced local copy is admitted when the target volume holds one copy plus the reserve: " +
            copy.Refusal?.Detail);
        copy.Reservation!.Dispose();
    }

    private static LocalFreeSpaceGuard Guard(long freeBytes, string root = RootPath)
    {
        var probe = new FakeProbe();
        probe.Free[root] = freeBytes;
        return new LocalFreeSpaceGuard(probe, V2Settings.Default);
    }

    private static SyncAction Copy(long size) =>
        new("a-copy", 2, ActionKind.CopyNew, Side.A, "x", null, Source(size), null, false, null);

    private static SyncAction Update(long write, long victim) =>
        new("a-update", 2, ActionKind.Update, Side.A, "x", null, Source(write), Victim(victim), true, null);

    private static SyncAction DeleteOf(long victim) =>
        new("a-delete", 6, ActionKind.Delete, Side.A, "x", null, null, Victim(victim), true, null);

    private static ItemState Source(long size) =>
        new("source", ItemKind.File, "id:local:00000001:0000000000000001", null, "hash", size,
            DateTimeOffset.UnixEpoch, ItemFlags.None);

    private static ItemState Victim(long size) =>
        new("victim", ItemKind.File, "id:local:00000001:0000000000000002", null, "hash", size,
            DateTimeOffset.UnixEpoch, ItemFlags.None);

    internal sealed class FakeProbe : IVolumeSpaceProbe
    {
        internal Dictionary<string, long> Free { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long? FreeBytes(string path) => Free.TryGetValue(path, out var bytes) ? bytes : null;
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
